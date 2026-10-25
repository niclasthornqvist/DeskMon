using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DeskMon;

/// <summary>
/// Where DeskMon keeps what is yours: settings, notes, sessions and the log in
/// %APPDATA%\DeskMon, and the art cache - which can always be fetched again -
/// in %LOCALAPPDATA%\DeskMon, out of the roaming profile.
///
/// It all used to live in app\ beside DeskMon.exe. That made everyone who used
/// the PC share one set of notes and play history, and it kept app\ writable
/// by every user - which, with DeskMon started elevated from there at logon,
/// let any program swap the exe for its own (S1 in the security review). With
/// the data out, app\ holds only the exe and Build.ps1 can lock it.
/// </summary>
internal static class DataFolder
{
    /// <summary>
    /// %APPDATA%\DeskMon - settings, notes, sessions, error.log. Back to app\
    /// for the run if moving the old data there failed: starting on an empty
    /// folder would show no notes, and the settings it then wrote would mark
    /// the move as done - leaving the real data behind for good.
    /// </summary>
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskMon");

    /// <summary>%LOCALAPPDATA%\DeskMon\cache - game art, safe to delete. Back to app\cache with Root.</summary>
    public static string Cache { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMon", "cache");

    /// <summary>Why the old data couldn't be moved this run, for MainWindow to say; null when all is well.</summary>
    public static string? MoveProblem { get; private set; }

    /// <summary>The move failed and this run uses app\ as before - as opposed to data left for its owner.</summary>
    public static bool UsingOldFolder => Root == OldRoot;

    /// <summary>
    /// Moves the old data if it is still in app\ - see MoveOldData - and, if
    /// that fails, keeps using app\ for this run. Call before anything reads
    /// a setting or a note.
    /// </summary>
    public static void Prepare()
    {
        (MoveProblem, bool keepOld) = MoveOldData();
        if (MoveProblem is null || !keepOld)
            return;   // moved, nothing to move - or someone else's, so this user starts fresh in their own folder

        Root = OldRoot;
        Cache = Path.Combine(OldRoot, "cache");
        // After the switch, so it lands in the error.log with the rest of the data.
        App.Log("moving the data", MoveProblem + " - using the old folder in app\\ for now; the next start tries again.");
    }

    public static string Notes => Path.Combine(Root, "notes");
    public static string Sessions => Path.Combine(Root, "sessions");

    /// <summary>Where it all was before 1.65: beside the exe.</summary>
    private static string OldRoot => AppContext.BaseDirectory;

    /// <summary>
    /// Left in app\ when the old data couldn't all be deleted after the move,
    /// so the leftovers aren't taken for data to move by another Windows user
    /// on this PC - they would get your notes as theirs.
    /// </summary>
    private static string MovedMarker => Path.Combine(OldRoot, "moved-to-appdata.txt");

    /// <summary>
    /// Moves the data from app\ to here, once - the first start of this
    /// version, for the first Windows user to run it. Before anything reads a
    /// setting, under a lock of its own so two copies starting together can't
    /// both try.
    ///
    /// Copied, checked file by file, the notes' picture paths rewritten, and
    /// only then the old copy deleted. Anything going wrong stops it before
    /// the delete and returns why: the data in app\ is then still whole, still
    /// in use, and the next start tries again. A NotesFolder or SessionFolder
    /// set in the settings was never in app\, and stays where it is.
    /// </summary>
    /// <returns>
    /// Problem null when there was nothing to move or it moved; otherwise
    /// why not. KeepOld says whether to go on using app\ meanwhile - true for
    /// a move that failed, false for data that isn't this user's to take.
    /// </returns>
    private static (string? Problem, bool KeepOld) MoveOldData()
    {
        using var gate = new Mutex(false, @"Local\DeskMon.MoveData");
        try { gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { /* the other copy died mid-way; carry on */ }

        try
        {
            string oldSettings = Path.Combine(OldRoot, "settings.json");
            string newSettings = Path.Combine(Root, "settings.json");

            // Already moved, or nothing to move - a fresh install, a second user.
            if (File.Exists(newSettings) || File.Exists(MovedMarker) || !File.Exists(oldSettings))
                return (null, false);

            // Whoever starts 1.65 first would otherwise take it all: another
            // account on the PC getting your notes as theirs, and them deleted
            // from app\ - yours to find gone. Left for its owner instead.
            if (OwnerIfNotMe(oldSettings) is { } owner)
            {
                // An empty settings.json of this user's own, so it is said
                // once rather than at every start until something is saved.
                try { AtomicFile.WriteAllText(newSettings, "{}"); } catch { }
                return ($"The data in app\\ belongs to {owner}, so it is left for them - you start with your own.", false);
            }

            // The overrides are read from the old file itself: a notes folder
            // set elsewhere was never in app\ and isn't touched.
            var old = ReadOverrides(oldSettings);

            // What goes where. Folders only when they are app\'s own.
            // settings.json is not in the list: it is copied last, on its own -
            // its arrival is what says the move is done.
            var moves = new List<(string From, string To)>();
            foreach (string file in new[] { "error.log", "error.log.old", "sensors.txt" })
                moves.Add((Path.Combine(OldRoot, file), Path.Combine(Root, file)));
            foreach (string copy in SafeFiles(OldRoot, "settings.unreadable*.json"))
                moves.Add((copy, Path.Combine(Root, Path.GetFileName(copy))));

            var folders = new List<(string From, string To)>();
            if (string.IsNullOrWhiteSpace(old.Notes))
                folders.Add((Path.Combine(OldRoot, "notes"), Notes));
            if (string.IsNullOrWhiteSpace(old.Sessions))
                folders.Add((Path.Combine(OldRoot, "sessions"), Sessions));
            folders.Add((Path.Combine(OldRoot, "cache"), Cache));

            foreach (var (from, to) in folders.Where(f => Directory.Exists(f.From)))
                foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    moves.Add((file, Path.Combine(to, Path.GetRelativePath(from, file))));

            moves.RemoveAll(m => !File.Exists(m.From));

            // A try that failed part-way left its copies here, and anything
            // deleted from app\ since would come back with them. Emptied first:
            // with no settings.json yet, nothing but such a try has written here.
            foreach (var (_, to) in folders)
                if (Directory.Exists(to))
                    Directory.Delete(to, recursive: true);

            // 1. Copy.
            foreach (var (from, to) in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
            }

            // 2. Check every file arrived whole, before anything is let go of.
            foreach (var (from, to) in moves)
                if (!File.Exists(to) || new FileInfo(to).Length != new FileInfo(from).Length)
                    return ($"{Path.GetFileName(from)} didn't copy whole to {Path.GetDirectoryName(to)}.", true);

            // 3. The notes link each screenshot by its full path, which still
            // says app\notes; rewritten, or every picture would point at a
            // folder about to be deleted. Then checked the way the notepad
            // will read them, because the rewrite only knows one spelling of
            // a path and RTF has several for anything past plain ASCII.
            if (string.IsNullOrWhiteSpace(old.Notes) && Directory.Exists(Notes))
            {
                string oldNotes = Path.Combine(OldRoot, "notes");
                RewritePicturePaths(oldNotes, Notes);
                if (CheckPictureLinks(oldNotes, Notes) is { } broken)
                    return (broken, true);
            }

            // 4. settings.json last of all. Copied first, a failure part-way
            // would have left it there, the move looking done, and the notes
            // never moved; copied last, a failure anywhere above leaves no new
            // settings.json and the next start simply tries again.
            File.Copy(oldSettings, newSettings, overwrite: true);
            if (new FileInfo(newSettings).Length != new FileInfo(oldSettings).Length)
            {
                File.Delete(newSettings);
                return ("settings.json didn't copy whole.", true);
            }
            moves.Add((oldSettings, newSettings));

            // 5. Only now the old copy. A failure here loses nothing - the new
            // copy is complete - so it is logged, and the marker stops anyone
            // else taking the leftovers for data.
            var leftovers = new List<string>();
            foreach (var (from, _) in moves)
            {
                try { File.Delete(from); }
                catch { leftovers.Add(from); }
            }
            foreach (var (from, _) in folders)
            {
                try { if (Directory.Exists(from)) Directory.Delete(from, recursive: true); }
                catch { leftovers.Add(from); }
            }

            if (leftovers.Count > 0)
            {
                App.Log("moving the data", "Moved, but couldn't delete from app\\: " + string.Join(", ", leftovers));
                try
                {
                    File.WriteAllText(MovedMarker,
                        $"DeskMon's settings, notes and sessions moved to {Root} on {DateTime.Now:yyyy-MM-dd HH:mm}.{Environment.NewLine}" +
                        $"Anything left beside this file is an old copy and can be deleted.{Environment.NewLine}");
                }
                catch { /* the new copy works regardless */ }
            }

            App.Log("moving the data", $"Moved {moves.Count} files from {OldRoot} to {Root}.");
            return (null, false);
        }
        catch (Exception ex)
        {
            return (ex.Message, true);
        }
        finally
        {
            try { gate.ReleaseMutex(); } catch { }
        }
    }

    /// <summary>
    /// Every .rtf in the new notes folder - the notes and their HISTORY
    /// copies - with the old folder's path swapped for the new one.
    ///
    /// The path appears in the RTF as a HYPERLINK field, its backslashes
    /// doubled: "E:\\Nicke\\Program\\app\\notes\\screenshots\\…". Read and
    /// written as Latin-1, byte for byte, since RTF is 8-bit text and its own
    /// escapes carry anything wider. A new path with characters past ASCII (a
    /// user name with å, say) is written in RTF's \u escapes for that reason.
    /// </summary>
    private static void RewritePicturePaths(string oldNotes, string newNotes)
    {
        string from = RtfPath(oldNotes.TrimEnd('\\') + "\\");
        string to = RtfPath(newNotes.TrimEnd('\\') + "\\");
        var latin1 = Encoding.Latin1;

        foreach (string file in Directory.EnumerateFiles(newNotes, "*.rtf", SearchOption.AllDirectories))
        {
            string text = latin1.GetString(File.ReadAllBytes(file));
            if (text.IndexOf(from, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            AtomicFile.WriteAllBytes(file, latin1.GetBytes(text.Replace(from, to, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>
    /// Opens every moved note as the notepad will - through WPF's own RTF
    /// reader - and looks at each picture link. Broken: one still into the
    /// old folder, or one whose file was there at the old place but isn't at
    /// the new. A link to a picture that was already gone before the move is
    /// left alone; the MISSING card covers those. Null when all are good.
    /// On the UI thread, which WPF's documents need: this runs once.
    /// </summary>
    private static string? CheckPictureLinks(string oldNotes, string newNotes)
    {
        string oldPrefix = oldNotes.TrimEnd('\\') + "\\", newPrefix = newNotes.TrimEnd('\\') + "\\";

        foreach (string file in Directory.EnumerateFiles(newNotes, "*.rtf", SearchOption.AllDirectories))
        {
            var document = new System.Windows.Documents.FlowDocument();
            var range = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd);
            try
            {
                using var stream = File.OpenRead(file);
                range.Load(stream, System.Windows.DataFormats.Rtf);
            }
            catch
            {
                continue;   // a note WPF can't read now couldn't be read before either - not the move's doing
            }

            foreach (var link in Links(document))
            {
                if (link.NavigateUri is not { IsAbsoluteUri: true, IsFile: true } uri)
                    continue;

                string path = uri.LocalPath;
                if (path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    return $"{Path.GetFileName(file)} still links pictures in app\\notes.";

                if (path.StartsWith(newPrefix, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(path) && File.Exists(oldPrefix + path[newPrefix.Length..]))
                    return $"{Path.GetFileName(file)} links {Path.GetFileName(path)} at a place it didn't arrive.";
            }
        }

        return null;
    }

    /// <summary>Every hyperlink in a document, however deep in lists, tables or sections.</summary>
    private static IEnumerable<System.Windows.Documents.Hyperlink> Links(System.Windows.DependencyObject parent)
    {
        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not System.Windows.DependencyObject node)
                continue;
            if (node is System.Windows.Documents.Hyperlink link)
                yield return link;
            foreach (var inner in Links(node))
                yield return inner;
        }
    }

    /// <summary>
    /// Who app\settings.json belongs to, if it can be told and it isn't the
    /// user running this. The file's owner when that is a person's account;
    /// files made by an elevated program can be owned by the Administrators
    /// group instead, which says nothing, and then the logon task's user
    /// decides. Null when it is this user's - or when neither can tell, as on
    /// a PC with one account and no logon task, which is the usual case.
    /// </summary>
    private static string? OwnerIfNotMe(string settingsPath)
    {
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (me is null)
            return null;

        try
        {
            var owner = new FileInfo(settingsPath).GetAccessControl()
                .GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
            if (owner is not null && owner.IsAccountSid())
                return owner == me ? null : NameOf(owner);
        }
        catch
        {
            // Unreadable owner: try the task below.
        }

        try
        {
            if (Autostart.TaskUser() is { } account
                && new System.Security.Principal.NTAccount(account).Translate(typeof(System.Security.Principal.SecurityIdentifier))
                   is System.Security.Principal.SecurityIdentifier taskUser)
                return taskUser == me ? null : account;
        }
        catch
        {
            // A task naming an account that no longer exists: it can't own anything.
        }

        return null;
    }

    private static string NameOf(System.Security.Principal.SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(System.Security.Principal.NTAccount)).Value; }
        catch { return sid.Value; }
    }

    /// <summary>A path as an RTF field holds it: backslashes doubled, anything past ASCII as \uN?.</summary>
    private static string RtfPath(string path)
    {
        var rtf = new StringBuilder();
        foreach (char c in path)
        {
            if (c == '\\')
                rtf.Append(@"\\");
            else if (c > 127)
                rtf.Append(@"\u").Append((short)c).Append('?');
            else
                rtf.Append(c);
        }
        return rtf.ToString();
    }

    private static (string? Notes, string? Sessions) ReadOverrides(string settingsPath)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            string? Read(string key) => json.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
            return (Read("NotesFolder"), Read("SessionFolder"));
        }
        catch
        {
            // Unreadable: no overrides can be told, so app\'s own folders are moved -
            // settings.json itself still goes along and is dealt with as now.
            return (null, null);
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try { return Directory.GetFiles(folder, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
