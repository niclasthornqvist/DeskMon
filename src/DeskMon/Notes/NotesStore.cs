using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskMon.Notes;

/// <summary>
/// Per-game notes on disk: one file per game, in app\notes.
///
/// Notes are stored as RTF so bold, italics, alignment and numbered lists
/// survive. RTF is still a text format that WordPad, Word and LibreOffice all
/// open, so the notes outlive this program - which was the point of plain text
/// before formatting was needed.
///
/// Plain .txt files from before the change are still read. Nothing rewrites or
/// deletes them: the first save of a converted note writes the .rtf alongside,
/// and the .txt stays put as a backup.
/// </summary>
public sealed class NotesStore
{
    /// <summary>Used whenever no game is running.</summary>
    public const string GeneralNotes = "General";

    private readonly string _folder;

    public NotesStore(string? folderOverride)
    {
        _folder = string.IsNullOrWhiteSpace(folderOverride)
            ? Path.Combine(AppContext.BaseDirectory, "notes")
            : folderOverride!;

        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch
        {
            // Read-only location; load/save degrade to doing nothing and the
            // window shows the error in its status line.
        }
    }

    public string FolderPath => _folder;

    /// <summary>Where this game's notes are written.</summary>
    public string PathFor(string gameName) => Path.Combine(_folder, SafeFileName(gameName) + ".rtf");

    /// <summary>The pre-formatting plain text file, read only as a fallback.</summary>
    public string LegacyPathFor(string gameName) => Path.Combine(_folder, SafeFileName(gameName) + ".txt");

    /// <summary>
    /// When this game's notes were last written, or null if it has none. Used
    /// so that opening notes says how old they are rather than just "Loaded" -
    /// the point of keeping them is coming back months later.
    /// </summary>
    public DateTime? LastSaved(string gameName)
    {
        try
        {
            var rtf = new FileInfo(PathFor(gameName));
            if (rtf.Exists)
                return rtf.LastWriteTime;

            var legacy = new FileInfo(LegacyPathFor(gameName));
            return legacy.Exists ? legacy.LastWriteTime : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The stored RTF, or null if this game has none yet.</summary>
    public byte[]? LoadRtf(string gameName)
    {
        try
        {
            string path = PathFor(gameName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Plain text from an older .txt file, or null if there isn't one.</summary>
    public string? LoadLegacyText(string gameName)
    {
        try
        {
            string path = LegacyPathFor(gameName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the notes. Returns null on success, or the reason it failed.
    /// <paramref name="hasContent"/> is false for an empty document, which is
    /// not written unless a file already exists - so merely launching a game
    /// never litters the folder with empty notes.
    /// </summary>
    public string? SaveRtf(string gameName, byte[] data, bool hasContent)
    {
        try
        {
            string path = PathFor(gameName);
            if (!hasContent && !File.Exists(path))
                return null;

            Directory.CreateDirectory(_folder);

            // Atomically, so an autosave interrupted by a crash or a power cut
            // cannot leave the notes truncated - they are meant to outlive this
            // program, and the save runs every 1.5 s while you type.
            AtomicFile.WriteAllBytes(path, data);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Where a full-size screenshot for this game's notes goes:
    /// notes\screenshots\Game\2026-10-17_210412.png. A subfolder per game, so
    /// they can be browsed game by game, and out of the notes folder's own
    /// files, which is what the picker lists. The extension follows the
    /// quality setting - see Screenshot.ExtensionFor.
    /// </summary>
    public string ScreenshotPathFor(string gameName, DateTime takenAt, string extension)
        => Path.Combine(ScreenshotFolderFor(gameName), $"{takenAt:yyyy-MM-dd_HHmmss}{extension}");

    /// <summary>
    /// The one game's screenshots folder - what the pictures window shows, so
    /// one game's pictures are never listed under another's.
    /// </summary>
    public string ScreenshotFolderFor(string gameName)
        => Path.Combine(_folder, "screenshots", SafeFileName(gameName));

    /// <summary>
    /// The screenshot file names every game's saved notes refer to - for the
    /// pictures window, so a picture that was copied into another game's notes
    /// is never taken for unused and deleted from under them. Names, not
    /// paths: they are unique (the time to the second), and a notes folder
    /// that has been moved since would leave every stored path stale.
    /// </summary>
    public HashSet<string> ScreenshotNamesInNotes(string? exceptGame)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? skip = exceptGame is null ? null : PathFor(exceptGame);

            foreach (string rtf in Directory.EnumerateFiles(_folder, "*.rtf"))
            {
                if (skip is not null && string.Equals(rtf, skip, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    names.UnionWith(NamesIn(rtf));
                }
                catch
                {
                    // Unreadable notes: fewer names known, so fewer pictures
                    // are offered as unused - the safe way to be wrong.
                }
            }
        }
        catch
        {
            // The folder itself couldn't be listed; the same safe way to be wrong.
        }

        return names;
    }

    /// <summary>
    /// The screenshot names in one notes file, read again only when the file
    /// has changed since. The pictures window asks every time it is clicked
    /// into, and notes holding screenshots run to megabytes each: reading
    /// every game's in full each time stalled the window on the way in.
    /// </summary>
    private string[] NamesIn(string rtf)
    {
        DateTime stamp = File.GetLastWriteTimeUtc(rtf);
        lock (_namesCache)
        {
            if (_namesCache.TryGetValue(rtf, out var cached) && cached.Stamp == stamp)
                return cached.Names;
        }

        // Latin-1, a byte per character: RTF is 7-bit text, and the names are
        // plain digits whatever the rest of the file holds.
        string text = File.ReadAllText(rtf, System.Text.Encoding.Latin1);
        string[] names = ScreenshotName.Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        lock (_namesCache)
            _namesCache[rtf] = (stamp, names);
        return names;
    }

    private readonly Dictionary<string, (DateTime Stamp, string[] Names)> _namesCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex ScreenshotName =
        new(@"\d{4}-\d{2}-\d{2}_\d{6}\.(?:png|jpg)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Backups kept per game; the oldest go first.</summary>
    private const int BackupsKept = 20;

    /// <summary>
    /// Where the copies made by <see cref="Backup"/> go: notes\backups. A
    /// subfolder, so the picker - which lists only this folder's own files -
    /// never offers a backup as though it were a game.
    /// </summary>
    public string BackupFolder => Path.Combine(_folder, "backups");

    /// <summary>
    /// Copies a game's notes file as it is now into <see cref="BackupFolder"/>,
    /// before the window first writes over it.
    ///
    /// This exists because these notes once disappeared: a bug wrote a blank
    /// document over General.rtf every time DeskMon started, each save looked
    /// like any other, and there was nothing to go back to. A copy taken before
    /// the first overwrite after each load would have held the text.
    ///
    /// Named "Game~2026-10-16_090241.rtf". The tilde is there because it is
    /// rare in game names, so the pruning below - which goes by prefix - can't
    /// mistake "Last Epoch 2" for a backup of "Last Epoch". The newest
    /// <see cref="BackupsKept"/> per game are kept.
    ///
    /// Never throws, and never stops a save: a backup that fails is a lost
    /// safety net, but a save that fails is lost notes.
    /// </summary>
    public void Backup(string gameName)
    {
        try
        {
            string source = PathFor(gameName);
            if (!File.Exists(source))
                return;

            Directory.CreateDirectory(BackupFolder);

            string prefix = SafeFileName(gameName) + "~";
            string target = Path.Combine(BackupFolder,
                $"{prefix}{DateTime.Now:yyyy-MM-dd_HHmmss}.rtf");

            File.Copy(source, target, overwrite: true);

            // The timestamp sorts the way it reads, so name order is age order.
            var stale = Directory.EnumerateFiles(BackupFolder, prefix + "*.rtf")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(BackupsKept);

            foreach (string old in stale)
                File.Delete(old);
        }
        catch
        {
            // See above: the save goes ahead regardless.
        }
    }

    /// <summary>One copy made by <see cref="Backup"/>: where it is, when it was taken, how big.</summary>
    public sealed record BackupCopy(string Path, DateTime Taken, long Bytes);

    /// <summary>
    /// A game's copies in <see cref="BackupFolder"/>, newest first - for the
    /// notes window's history list.
    ///
    /// Matched by the exact prefix up to the tilde, as the pruning is, so
    /// "Last Epoch 2" never lists among "Last Epoch"'s. The time comes from the
    /// name rather than the file: File.Copy carries the original's write time
    /// over, which is when the notes were saved, not when the copy was taken.
    /// </summary>
    public IReadOnlyList<BackupCopy> BackupsOf(string gameName)
    {
        try
        {
            if (!Directory.Exists(BackupFolder))
                return Array.Empty<BackupCopy>();

            string prefix = SafeFileName(gameName) + "~";
            var copies = new List<BackupCopy>();

            foreach (string path in Directory.EnumerateFiles(BackupFolder, prefix + "*.rtf"))
            {
                string stamp = Path.GetFileNameWithoutExtension(path)[prefix.Length..];
                var info = new FileInfo(path);
                DateTime taken = DateTime.TryParseExact(stamp, "yyyy-MM-dd_HHmmss",
                                                        System.Globalization.CultureInfo.InvariantCulture,
                                                        System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed
                    : info.LastWriteTime;
                copies.Add(new BackupCopy(path, taken, info.Length));
            }

            return copies.OrderByDescending(c => c.Taken).ToList();
        }
        catch
        {
            return Array.Empty<BackupCopy>();
        }
    }

    /// <summary>
    /// Puts a backup back as the game's notes. Null when done, else why not.
    ///
    /// The notes it replaces are backed up first, so a restore is itself
    /// undone the same way - picking the newer copy from the list. The chosen
    /// copy is read before that backup is made: making it prunes the oldest,
    /// and the oldest may be the very copy being restored.
    /// </summary>
    public string? Restore(string gameName, string backupPath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(backupPath);
            Backup(gameName);

            Directory.CreateDirectory(_folder);
            AtomicFile.WriteAllBytes(PathFor(gameName), data);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Sends a note's files to the Recycle Bin - for the notes window's delete
    /// button. Null when they are gone, else why not.
    ///
    /// The Recycle Bin rather than for good, like the pictures: notes are the
    /// one thing here that can't be measured again, and one click too many
    /// shouldn't cost them. The .txt from before formatting goes too, even
    /// though nothing else ever touches it - left behind, it would put the note
    /// straight back in the picker and be imported again on the next open.
    /// Backups in <see cref="BackupFolder"/> are left alone: they are the
    /// safety net for exactly this.
    /// </summary>
    public string? Delete(string gameName)
    {
        try
        {
            foreach (string path in new[] { PathFor(gameName), LegacyPathFor(gameName) })
                if (RecycleBin.Send(path) is { } error)
                    return error;

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Every game that has notes, for the picker. Counts both formats, so a
    /// game that has only ever had a .txt still shows up.
    /// </summary>
    public IReadOnlyList<string> ListGames()
    {
        try
        {
            return Directory.EnumerateFiles(_folder, "*.rtf")
                .Concat(Directory.EnumerateFiles(_folder, "*.txt"))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Game names become file names, and games are full of characters Windows
    /// won't take - "The Witcher 3: Wild Hunt" has a colon. Keep it readable
    /// rather than encoded, since the point is that you can find the file.
    /// </summary>
    public static string SafeFileName(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return GeneralNotes;

        var cleaned = new string(gameName
            .Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c)
            .ToArray());

        // Collapse the runs a replacement can leave behind, e.g. "Game - - X".
        while (cleaned.Contains("--"))
            cleaned = cleaned.Replace("--", "-");

        cleaned = cleaned.Trim(' ', '.', '-');
        return cleaned.Length == 0 ? GeneralNotes : cleaned;
    }
}
