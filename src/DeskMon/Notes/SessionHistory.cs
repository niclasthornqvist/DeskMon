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
            ? Path.Combine(DataFolder.Root, "sessions")   // the user's own - see DataFolder
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
    /// <remarks>
    /// Only the last line is parsed. This runs on the UI thread on every
    /// switch of the notes and as a game quits, and parsing the whole log
    /// with all its regexes to keep one line grew with every session played.
    /// The others are only counted, for the session's ID, with the same test
    /// Parse starts with.
    /// </remarks>
    public SessionRecord? LastSession(string game)
    {
        try
        {
            int count = 0;
            string? last = null;
            foreach (string line in Lines(PathFor(game)))
            {
                if (!SessionRecord.StartsLikeSession(line))
                    continue;
                count++;
                last = line;
            }

            if (last is null || SessionRecord.Parse(last) is not { } session)
                return null;

            session.Id = count;
            session.Game = game;
            return session;
        }
        catch
        {
            return null;   // a footnote, as in Sessions
        }
    }

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
            {
                sessions[i].Id = i + 1;
                sessions[i].Game = game;
            }

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
    /// Takes sessions out of the game's log. Returns null on success, or
    /// why it couldn't - shown to the user, who is waiting on this one.
    ///
    /// Matched by its line, which holds the session to the minute along with
    /// every reading, so it can't take out a different session by mistake. One
    /// match per session: two identical lines are two sessions, and picking
    /// one deletes one.
    ///
    /// All or nothing, in one write: if any is gone from the log - changed in
    /// Notepad - none is deleted, rather than some of a picked set vanishing.
    ///
    /// The log is deleted when nothing but its heading is left, or the game
    /// would stay in the picker with nothing under it.
    /// </summary>
    public string? Delete(string game, SessionRecord session) => Delete(game, new[] { session });

    /// <inheritdoc cref="Delete(string, SessionRecord)"/>
    public string? Delete(string game, IReadOnlyCollection<SessionRecord> sessions)
    {
        try
        {
            string path = PathFor(game);
            var lines = AllLines(path);

            foreach (var session in sessions)
            {
                int at = lines.FindIndex(l => l.Trim() == session.Raw);
                if (at < 0)
                    return sessions.Count == 1
                        ? "That session is no longer in the log - it may have been changed in Notepad. Press Refresh."
                        : $"Session ID {session.Id} is no longer in the log - it may have been changed in Notepad. Nothing was deleted; press Refresh.";
                lines.RemoveAt(at);
            }

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

    /// <summary>
    /// Every session of the game: its log goes, as it would once its last
    /// session was deleted, so the game leaves the picker. Null on success,
    /// or why it couldn't.
    /// </summary>
    public string? DeleteAll(string game)
    {
        try
        {
            // File.Delete is quiet about a file that is already gone, which
            // is the outcome asked for anyway.
            File.Delete(PathFor(game));
            // The timelines go with it: kept, they would turn up again under
            // whatever session next starts in the same minute - never, but
            // a folder of orphans is clutter either way.
            SessionTimeline.DeleteAll(PathFor(game));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The timelines of sessions deleted from the history window. Not part of
    /// Delete itself: that also takes back the line written when a game quit
    /// and then came back (DeleteLater), and that session carries on - its
    /// timeline with it.
    /// </summary>
    public void DeleteTimelines(string game, IEnumerable<SessionRecord> sessions)
        => SessionTimeline.Delete(PathFor(game), sessions.Select(s => s.Start).ToList());

    /// <summary>The timeline of the session that started at <paramref name="start"/>, or null - see SessionTimeline.Load.</summary>
    public SessionTimeline? Timeline(string game, DateTime start) => SessionTimeline.Load(PathFor(game), start);

    /// <summary>
    /// Writes the open session's timeline on AtomicFile's writer, as the
    /// session lines are: the text is taken here, on the tick, and the disk is
    /// left to the writer, so a busy disk can't freeze the windows. Written
    /// whole each time - at the checkpoint and at the end - so a crash keeps
    /// all but the last minute, as with the line.
    /// </summary>
    public void SaveTimelineLater(string game, SessionTimeline timeline)
    {
        string path = SessionTimeline.PathFor(PathFor(game), timeline.Start);
        string text = timeline.ToText();
        AtomicFile.Later(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                App.Log("writing a session timeline", ex);
            }
        });
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
    /// <see cref="Append"/> on AtomicFile's writer thread, behind any checkpoint
    /// still queued - for the polling tick, where waiting on a busy disk froze
    /// every DeskMon window the moment a game quit. A failure is logged, as
    /// nobody is waiting on it. <paramref name="done"/> runs on the UI thread
    /// once the line is on disk, so the windows that show the log read it then.
    /// </summary>
    public void AppendLater(string game, string line, Action? done = null)
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        AtomicFile.Later(() =>
        {
            if (Append(game, line) is { } error)
                App.Log("writing a session", error);
            if (done is not null)
                ui?.BeginInvoke(done);
        });
    }

    /// <summary>
    /// <see cref="Delete(string, SessionRecord)"/> on the writer thread, after
    /// any append still queued - so a line taken back is never looked for
    /// before it has been written. Logged on failure, like AppendLater.
    /// </summary>
    public void DeleteLater(string game, SessionRecord session, Action? done = null)
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        AtomicFile.Later(() =>
        {
            if (Delete(game, session) is { } error)
                App.Log("taking back a session line", error);
            if (done is not null)
                ui?.BeginInvoke(done);
        });
    }

    /// <summary>
    /// The history window's DELETE: the sessions, and once their lines are
    /// gone their timelines, on the writer thread behind every line still
    /// queued. Done on the UI thread after a Flush that could time out, a
    /// busy disk let the rewrite race an append still being written - and the
    /// line just appended was dropped. <paramref name="done"/> gets the error,
    /// or null, on the UI thread.
    /// </summary>
    public void DeleteForUser(string game, IReadOnlyCollection<SessionRecord> sessions, Action<string?> done)
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        AtomicFile.Later(() =>
        {
            string? error = Delete(game, sessions);
            // Not before: a delete that failed would leave sessions without their curves.
            if (error is null)
                DeleteTimelines(game, sessions);
            ui?.BeginInvoke(() => done(error));
        });
    }

    /// <summary>DELETE ALL on the writer thread, for <see cref="DeleteForUser"/>'s reason.</summary>
    public void DeleteAllForUser(string game, Action<string?> done)
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        AtomicFile.Later(() =>
        {
            string? error = DeleteAll(game);
            ui?.BeginInvoke(() => done(error));
        });
    }

    /// <summary>
    /// Adds one line for the session that just ended. Returns null on success,
    /// or the reason it failed. Synchronous: at startup (RecoverOpenSession)
    /// and on the writer thread through <see cref="AppendLater"/>.
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

    /// <summary>
    /// Replaces the saved open session. Atomic, so a power cut mid-write leaves
    /// the previous one. On AtomicFile's writer: this runs once a minute inside
    /// the polling tick, mid-game, and waiting there for the disk froze the
    /// widget whenever the game was loading something.
    /// </summary>
    public void SaveOpenSession(string game, string line)
    {
        string path = OpenSessionPath;
        string json = JsonSerializer.Serialize(new OpenSession(game, line));

        AtomicFile.Later(() =>
        {
            try
            {
                AtomicFile.WriteAllText(path, json);
            }
            catch
            {
                // Insurance only: the session is still written normally when it ends.
            }
        });
    }

    /// <summary>
    /// The session ended properly and has its line; nothing to recover. On the
    /// same writer as the saves, behind any still queued: deleted at once, a
    /// checkpoint waiting in the queue would write the file back afterwards,
    /// and the next start would log the session a second time.
    /// </summary>
    public void ClearOpenSession()
    {
        string path = OpenSessionPath;
        AtomicFile.Later(() =>
        {
            try
            {
                File.Delete(path);   // no error when there is none
            }
            catch { }
        });
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
