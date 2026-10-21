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
