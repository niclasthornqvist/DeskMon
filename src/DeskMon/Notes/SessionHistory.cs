using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DeskMon.Notes;

/// <summary>
/// The record of what has been played: one line per finished session, appended
/// to a per-game log in app\sessions.
///
/// These lines used to go into the game's notes. That put the program's own
/// bookkeeping in the middle of what you had written there yourself, and grew
/// without end - the notes are a document you read and edit, so anything
/// appended to them forever is in the way. Here the history is a file of its
/// own: still plain text you can open, search or paste into a spreadsheet
/// months later, and nothing you have to scroll past to reach your puzzle
/// solutions.
///
/// Appending is a real file append, unlike the notes, where adding a line meant
/// loading the RTF, rebuilding a FlowDocument and writing the whole thing back.
/// So this needs no UI thread, cannot be undone by the notepad's next autosave,
/// and cannot damage the lines already on disk.
/// </summary>
public sealed class SessionHistory
{
    /// <summary>
    /// With a BOM: the lines carry "·", "↑" and "°", and Notepad still guesses
    /// the system code page for a file that has no BOM - which would show the
    /// whole history as mojibake.
    /// </summary>
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly string _folder;

    public SessionHistory(string? folderOverride)
    {
        _folder = string.IsNullOrWhiteSpace(folderOverride)
            ? Path.Combine(AppContext.BaseDirectory, "sessions")
            : folderOverride!;
    }

    /// <summary>
    /// Where this game's history is written. The same name the notes use, so
    /// "The Witcher 3.rtf" and "The Witcher 3.log" are recognisably a pair.
    /// </summary>
    public string PathFor(string game)
        => Path.Combine(_folder, NotesStore.SafeFileName(game) + ".log");

    /// <summary>
    /// This game's newest session, or null when it has none or the file can't
    /// be read. Strictly this game's own log: a session is only ever shown
    /// under the game it was played in.
    /// </summary>
    public SessionRecord? LastSession(string game)
        => Sessions(game).LastOrDefault();

    /// <summary>
    /// Every session in this game's log, oldest first - the order they were
    /// written in. Lines that don't read as a session are skipped; an empty
    /// list when there is no log or it can't be read.
    /// </summary>
    public IReadOnlyList<SessionRecord> Sessions(string game)
    {
        try
        {
            var sessions = Lines(PathFor(game))
                .Select(SessionRecord.Parse)
                .OfType<SessionRecord>()
                .ToList();

            for (int i = 0; i < sessions.Count; i++)
                sessions[i].Id = i + 1;

            return sessions;
        }
        catch
        {
            // Only ever a footnote or a history view - never worth an error.
            return Array.Empty<SessionRecord>();
        }
    }

    /// <summary>
    /// Every game that has a history, by the name in its log's heading, in
    /// name order.
    /// </summary>
    public IReadOnlyList<string> Games()
    {
        try
        {
            if (!Directory.Exists(_folder))
                return Array.Empty<string>();

            return Directory.EnumerateFiles(_folder, "*.log")
                .Select(GameNameOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The lines that are sessions rather than the heading or a blank.
    ///
    /// Opened with FileShare.ReadWrite: a game quitting appends to this file,
    /// and a plain read that happened to coincide would throw rather than
    /// simply see the file a moment earlier.
    /// </summary>
    private static List<string> Lines(string path)
    {
        var lines = new List<string>();
        if (!File.Exists(path))
            return lines;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8WithBom, detectEncodingFromByteOrderMarks: true);

        for (string? line; (line = reader.ReadLine()) is not null;)
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                lines.Add(line.Trim());

        return lines;
    }

    /// <summary>
    /// The game a log belongs to, from its heading. The file name is only a
    /// fallback: it has had the characters Windows forbids taken out, so it
    /// is not always the game's real name - and the real name is what the
    /// log has to be found by again.
    /// </summary>
    private static string GameNameOf(string path)
    {
        const string Heading = "# DeskMon session history - ";

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8WithBom, detectEncodingFromByteOrderMarks: true);

            string? first = reader.ReadLine();
            if (first is not null && first.StartsWith(Heading, StringComparison.Ordinal))
                return first[Heading.Length..].Trim();
        }
        catch
        {
            // Fall back to the name below.
        }

        return Path.GetFileNameWithoutExtension(path);
    }


    /// <summary>
    /// Takes one session out of the game's log. Returns null on success, or
    /// why it couldn't - shown to the user, who is waiting on this one.
    ///
    /// Matched by its line, which holds the session to the minute along with
    /// every reading, so it can't take out a different session by mistake. Only
    /// the first match goes: two identical lines are two sessions, and one
    /// click deletes one.
    ///
    /// The log is deleted when nothing but its heading is left, or the game
    /// would stay in the picker with nothing under it.
    /// </summary>
    public string? Delete(string game, SessionRecord session)
    {
        try
        {
            string path = PathFor(game);
            var lines = AllLines(path);

            int at = lines.FindIndex(l => l.Trim() == session.Raw);
            if (at < 0)
                return "That session is no longer in the log - it may have been changed in Notepad. Press Refresh.";

            lines.RemoveAt(at);

            if (lines.All(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith('#')))
            {
                File.Delete(path);
                return null;
            }

            // Atomic, like the notes: a crash halfway through rewriting this
            // must not lose the sessions that were meant to stay. The BOM goes
            // in by hand - AtomicFile writes without one, and Notepad needs it.
            string text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            AtomicFile.WriteAllBytes(path, [.. Utf8WithBom.GetPreamble(), .. Utf8WithBom.GetBytes(text)]);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Every line as it is, heading and blanks too - for rewriting the file without losing any.</summary>
    private static List<string> AllLines(string path)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8WithBom, detectEncodingFromByteOrderMarks: true);

        for (string? line; (line = reader.ReadLine()) is not null;)
            lines.Add(line);

        return lines;
    }

    /// <summary>
    /// Adds one line for the session that just ended. Returns null on success,
    /// or the reason it failed - nothing is waiting on the answer, since by the
    /// time this runs the game is gone and there is nobody to tell.
    /// </summary>
    public string? Append(string game, string line)
    {
        try
        {
            // Created on the first session rather than at startup, so a machine
            // that never plays anything never gets the folder.
            Directory.CreateDirectory(_folder);

            string path = PathFor(game);

            // The heading names the game, because a single .log copied out of
            // the folder would otherwise be a list of dates with no subject.
            if (!File.Exists(path))
                File.WriteAllText(path, $"# DeskMon session history - {game}{Environment.NewLine}", Utf8WithBom);

            // A crash or power cut mid-append can leave the last line without
            // its break, and this one would then be written onto the end of
            // it - two sessions in one line that reads as neither.
            string start = EndsWithLineBreak(path) ? string.Empty : Environment.NewLine;
            File.AppendAllText(path, start + line + Environment.NewLine, Utf8WithBom);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Whether the file's last byte is a line break - or the file is empty, which needs none.</summary>
    private static bool EndsWithLineBreak(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
            return true;

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    // --- The session still being played -----------------------------------
    //
    // A session's line is only written when it ends - the game quitting, or
    // DeskMon closing - so anything that stops DeskMon without letting it
    // close lost the whole session: a crash, a power cut, the process being
    // killed. The line as it would read if the session ended now is kept on
    // disk instead, rewritten every minute, and turned into a real entry the
    // next time DeskMon starts if it is still there. A session that ends
    // properly takes it away.

    /// <summary>
    /// Beside the logs, but not a .log, so it is never listed as a game.
    /// </summary>
    private string OpenSessionPath => Path.Combine(_folder, "open-session.json");

    private sealed record OpenSession(string Game, string Line);

    /// <summary>Replaces the saved open session. Atomic, so a power cut mid-write leaves the previous one.</summary>
    public void SaveOpenSession(string game, string line)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            AtomicFile.WriteAllText(OpenSessionPath, JsonSerializer.Serialize(new OpenSession(game, line)));
        }
        catch
        {
            // Insurance only: the session is still written normally when it ends.
        }
    }

    /// <summary>The session ended properly and has its line; nothing to recover.</summary>
    public void ClearOpenSession()
    {
        try
        {
            File.Delete(OpenSessionPath);   // no error when there is none
        }
        catch { }
    }

    /// <summary>
    /// Writes the session a previous run left open, if there is one, into its
    /// game's log. Returns the game's name, or null when there was nothing to
    /// recover. Left in place if the log can't be written, to try again next
    /// start rather than lose it.
    /// </summary>
    public string? RecoverOpenSession()
    {
        try
        {
            if (!File.Exists(OpenSessionPath))
                return null;

            string saved = File.ReadAllText(OpenSessionPath);
            var open = JsonSerializer.Deserialize<OpenSession>(saved);

            // Taken away before the line is written, not after: deleting it
            // afterwards could fail with the line already in the log, and the
            // next start would write the same session a second time. Should
            // this delete fail, nothing is written, and the next start tries
            // again from the top.
            try
            {
                File.Delete(OpenSessionPath);
            }
            catch (Exception ex)
            {
                App.Log("recovering a session", ex);
                return null;
            }

            if (open is { Game.Length: > 0, Line.Length: > 0 } && Append(open.Game, open.Line) is { } error)
            {
                // Put back, so it is tried again next start rather than lost.
                App.Log("recovering a session", error);
                try { AtomicFile.WriteAllText(OpenSessionPath, saved); } catch { }
                return null;
            }

            return open?.Game;
        }
        catch (Exception ex)
        {
            // Unreadable - nothing in it can be saved, so it must not block
            // every later start from trying.
            App.Log("recovering a session", ex);
            ClearOpenSession();
            return null;
        }
    }
}
