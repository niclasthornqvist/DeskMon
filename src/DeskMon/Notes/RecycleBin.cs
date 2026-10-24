using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DeskMon.Notes;

/// <summary>
/// Deletes a file into the Recycle Bin rather than for good - for the pictures
/// window, where one click too many shouldn't cost a screenshot for ever.
///
/// Through the shell's own file operation, which is what Explorer's Delete
/// does; .NET's File.Delete has no way to the Recycle Bin. Quiet: no
/// confirmation or progress dialog, since the window asks first itself.
/// </summary>
internal static class RecycleBin
{
    /// <summary>Null when the file is in the Recycle Bin, else why not.</summary>
    public static string? Send(string path)
    {
        if (!File.Exists(path))
            return null;   // already gone - which is what was asked for

        var operation = new FileOperation
        {
            Function = Delete,
            // A list ending in two NULs; one comes from the marshaller.
            From = Path.GetFullPath(path) + "\0",
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi,
        };

        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
            return $"Windows refused (code {result})";

        return File.Exists(path) ? "the file is still there" : null;
    }

    /// <summary>
    /// Sends several files in one shell call, on a thread of its own, and
    /// returns those still there afterwards (as full paths) - an empty list
    /// when every one went.
    ///
    /// The pictures window's DELETE UNUSED used to call <see cref="Send"/>
    /// once per file on the UI thread. Recycling is slow, tens of milliseconds
    /// a file or more, so a hundred unused pictures froze every DeskMon window
    /// for seconds. One call takes the whole list, and the thread is STA
    /// because the shell's file operations expect one.
    /// </summary>
    public static Task<List<string>> SendAllAsync(IReadOnlyList<string> paths)
    {
        var done = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(SendAll(paths)); }
            catch (Exception ex) { done.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "DeskMon recycle bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<string> SendAll(IReadOnlyList<string> paths)
    {
        var present = paths.Where(File.Exists).Select(Path.GetFullPath).ToList();
        if (present.Count == 0)
            return present;

        var operation = new FileOperation
        {
            Function = Delete,
            // NUL between the paths and after the last; the marshaller adds the final NUL.
            From = string.Join("\0", present) + "\0",
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi,
        };
        SHFileOperation(ref operation);

        // Asked of the files rather than of the result code: one refused
        // file fails the call, while the rest may well have gone.
        return present.Where(File.Exists).ToList();
    }

    /// <summary>
    /// Puts files deleted out of <paramref name="folder"/> back from the
    /// Recycle Bin, by name, and returns the names that came back. For notes
    /// restored from HISTORY: they can show a picture whose file was deleted
    /// since, and the notes only hold its small preview - so the pictures
    /// window, which lists files, didn't show it at all.
    ///
    /// Read from the bin's own files rather than through the shell, whose
    /// Restore verb is named in the language Windows is in. Each deleted file
    /// is a $R file holding it and a $I file saying where it came from; a
    /// restore is moving the $R back and dropping the $I, which is all Explorer
    /// does too. Only the user's own bin, on the folder's drive. Never throws:
    /// a picture not brought back is one the window simply doesn't list.
    /// </summary>
    public static List<string> Restore(string folder, IEnumerable<string> names)
    {
        var restored = new List<string>();
        try
        {
            string full = Path.GetFullPath(folder);
            var wanted = names.Where(n => !File.Exists(Path.Combine(full, n)))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
                return restored;

            string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            string? root = Path.GetPathRoot(full);
            if (sid is null || string.IsNullOrEmpty(root))
                return restored;

            string bin = Path.Combine(root, "$Recycle.Bin", sid);
            if (!Directory.Exists(bin))
                return restored;

            // The same name deleted more than once: the newest is the one the
            // notes were last showing.
            var found = new Dictionary<string, (string Info, DateTime Deleted)>(StringComparer.OrdinalIgnoreCase);
            foreach (string info in Directory.EnumerateFiles(bin, "$I*"))
            {
                if (ReadInfo(info) is not { } entry
                    || !string.Equals(Path.GetDirectoryName(entry.From), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileName(entry.From);
                if (wanted.Contains(name) && (!found.TryGetValue(name, out var seen) || entry.Deleted > seen.Deleted))
                    found[name] = (info, entry.Deleted);
            }

            foreach (var (name, (info, _)) in found)
            {
                // $IABC123.png holds the details, $RABC123.png the file itself.
                string data = Path.Combine(bin, "$R" + Path.GetFileName(info).Substring(2));
                string target = Path.Combine(full, name);
                try
                {
                    if (!File.Exists(data) || File.Exists(target))
                        continue;
                    File.Move(data, target);
                    File.Delete(info);   // else the bin would list an entry with nothing behind it
                    restored.Add(name);
                }
                catch (Exception ex)
                {
                    App.Log("recycle bin", $"Couldn't bring {name} back from the Recycle Bin: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("recycle bin", $"Couldn't read the Recycle Bin: {ex.Message}");
        }
        return restored;
    }

    /// <summary>
    /// Where a $I file says its file was deleted from, and when. Version 2
    /// (Windows 10 on) gives the path's length then the path; version 1
    /// (Vista to 8) a fixed 260-character field. Null for anything else.
    /// </summary>
    private static (string From, DateTime Deleted)? ReadInfo(string infoPath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(infoPath);
            if (bytes.Length < 28)
                return null;

            long version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));

            string from;
            if (version == 2)
            {
                int chars = BitConverter.ToInt32(bytes, 24);
                if (chars <= 0 || 28 + chars * 2 > bytes.Length)
                    return null;
                from = System.Text.Encoding.Unicode.GetString(bytes, 28, chars * 2);
            }
            else if (version == 1 && bytes.Length >= 24 + 520)
                from = System.Text.Encoding.Unicode.GetString(bytes, 24, 520);
            else
                return null;

            return (from.TrimEnd('\0'), deleted);
        }
        catch (Exception)
        {
            return null;   // unreadable or odd: not one of ours to restore
        }
    }

    private const uint Delete = 0x0003;
    private const ushort Silent = 0x0004, NoConfirmation = 0x0010, AllowUndo = 0x0040, NoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref FileOperation operation);
}
