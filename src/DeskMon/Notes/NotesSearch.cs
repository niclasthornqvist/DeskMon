using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;

namespace DeskMon.Notes;

/// <summary>
/// Search across every game's notes, for "which game was that puzzle in?"
/// months later.
///
/// The notes are RTF, so searching the files as they are would match
/// formatting codes and miss any word with an accent in it (RTF escapes
/// those). Each file is turned back into plain text the reliable way - by
/// loading it the way the notepad does - and that text is kept, keyed by the
/// file's write time, so only notes that changed since the last search are
/// read again. Searching itself is then an in-memory scan, quick enough to
/// run on every keystroke.
/// </summary>
public sealed class NotesSearch
{
    private readonly NotesStore _store;

    /// <summary>Plain text per game, with the write time it was read at. UI thread only.</summary>
    private readonly Dictionary<string, (DateTime Stamp, string Text)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most matches shown per game, so one note full of a word can't bury the rest.</summary>
    private const int PerGame = 5;

    public NotesSearch(NotesStore store) => _store = store;

    /// <param name="Occurrence">Which match in that game's notes, counting from 0.</param>
    public sealed record Hit(string Game, string Before, string Match, string After, int Occurrence);

    /// <summary>
    /// Re-reads the notes that changed since last time. Call from the UI
    /// thread; the reading happens on a thread of its own.
    /// </summary>
    public async Task RefreshAsync()
    {
        var games = _store.ListGames();

        foreach (string gone in _cache.Keys.Where(k => !games.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _cache.Remove(gone);

        var stale = new List<(string Game, DateTime Stamp)>();
        foreach (string game in games)
        {
            var stamp = _store.LastSaved(game) ?? DateTime.MinValue;
            if (!_cache.TryGetValue(game, out var cached) || cached.Stamp != stamp)
                stale.Add((game, stamp));
        }

        if (stale.Count == 0)
            return;

        var read = await RunOnSta(() => stale.Select(s => (s.Game, s.Stamp, Text: PlainText(s.Game))).ToList());

        foreach (var (game, stamp, text) in read)
            _cache[game] = (stamp, text);
    }

    /// <summary>
    /// Matches for <paramref name="query"/>, game by game in name order. Each
    /// carries the line around it, cut short, so the list shows what was found
    /// rather than only where.
    /// </summary>
    public IReadOnlyList<Hit> Find(string query, int max = 60)
    {
        var hits = new List<Hit>();
        if (string.IsNullOrWhiteSpace(query))
            return hits;

        foreach (var (game, entry) in _cache.OrderBy(e => e.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            string text = entry.Text;
            int at = 0;

            for (int occurrence = 0; occurrence < PerGame; occurrence++)
            {
                at = IndexOf(text, query, at, out int length);
                if (at < 0)
                    break;

                hits.Add(Snippet(game, text, at, length, occurrence));
                if (hits.Count >= max)
                    return hits;

                at += length;
            }
        }

        return hits;
    }

    /// <summary>
    /// The next match at or after <paramref name="from"/>, or -1, with how much
    /// of the text it covers. Not always the query's length: this compares as
    /// the language does, so "ﬁ" matches "fi" and a zero-width character counts
    /// for nothing. Going by the query's length ran past the end of the text
    /// and threw, or cut the wrong slice out of it.
    /// A match of nothing - a query of only such characters - is no match:
    /// stepping on by nothing, it would be found in the same place for ever.
    /// </summary>
    private static int IndexOf(string text, string query, int from, out int length)
    {
        int found = CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            text.AsSpan(from), query, CompareOptions.IgnoreCase, out length);
        return found < 0 || length == 0 ? -1 : from + found;
    }

    /// <summary>
    /// The match with up to 40 characters either side, kept to its own line -
    /// the line it is on is what tells you whether it is the one you meant.
    /// </summary>
    private static Hit Snippet(string game, string text, int at, int length, int occurrence)
    {
        const int Context = 40;

        int lineStart = text.LastIndexOfAny(new[] { '\r', '\n' }, Math.Max(0, at - 1)) + 1;
        int lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, at + length);
        if (lineEnd < 0) lineEnd = text.Length;

        int from = Math.Max(lineStart, at - Context);
        int to = Math.Min(lineEnd, at + length + Context);

        string before = (from > lineStart ? "…" : string.Empty) + text[from..at].TrimStart();
        string after = text[(at + length)..to].TrimEnd() + (to < lineEnd ? "…" : string.Empty);

        return new Hit(game, before, text.Substring(at, length), after, occurrence);
    }

    /// <summary>
    /// The notes as plain text, loaded the way the notepad loads them. On an
    /// STA thread of its own: a FlowDocument needs one, and a large note full
    /// of screenshots takes long enough to load that the UI thread should not
    /// be the one waiting.
    /// </summary>
    private string PlainText(string game)
    {
        // Held by another program: nothing to search this time, as with a
        // note that can't be parsed below. Unlike saving, no harm can come of it.
        byte[]? rtf = null;
        try { rtf = _store.LoadRtf(game); }
        catch { }

        if (rtf is { Length: > 0 })
        {
            try
            {
                var document = new FlowDocument();
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var stream = new MemoryStream(rtf);
                range.Load(stream, DataFormats.Rtf);
                return range.Text;
            }
            catch
            {
                // A file the notepad couldn't read either; nothing to search.
            }
        }

        return _store.LoadLegacyText(game) ?? string.Empty;
    }

    private static Task<T> RunOnSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                // The documents gave this thread a Dispatcher, which stays
                // registered and holds on to what it knows until shut down.
                System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "DeskMon notes search",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// The <paramref name="occurrence"/>th match in a loaded document, as a
    /// range to select - so a result can take you to the exact place.
    ///
    /// Paragraph by paragraph, joining the paragraph's runs first: a word
    /// with one letter in bold is split across runs, and searching run by run
    /// would miss it and number every later match wrongly.
    /// </summary>
    public static TextRange? Locate(FlowDocument document, string query, int occurrence)
    {
        int seen = 0;

        foreach (var paragraph in Paragraphs(document.Blocks))
        {
            var pieces = new List<(TextPointer Start, int Offset, int Length)>();
            var text = new StringBuilder();

            for (var position = paragraph.ContentStart;
                 position is not null && position.CompareTo(paragraph.ContentEnd) < 0;
                 position = position.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (position.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                    continue;

                string run = position.GetTextInRun(LogicalDirection.Forward);
                pieces.Add((position, text.Length, run.Length));
                text.Append(run);
            }

            string joined = text.ToString();
            int at = 0;

            while ((at = IndexOf(joined, query, at, out int length)) >= 0)
            {
                if (seen++ == occurrence)
                {
                    var start = PointerAt(pieces, at, preferEnd: false);
                    var end = PointerAt(pieces, at + length, preferEnd: true);
                    return start is not null && end is not null ? new TextRange(start, end) : null;
                }

                at += length;
            }
        }

        return null;
    }

    /// <summary>
    /// The position <paramref name="offset"/> characters into the joined
    /// paragraph text. At a boundary between two runs, the end of a match
    /// belongs to the run it finished in, the start to the run it begins in.
    /// </summary>
    private static TextPointer? PointerAt(List<(TextPointer Start, int Offset, int Length)> pieces, int offset, bool preferEnd)
    {
        foreach (var (start, from, length) in pieces)
        {
            bool inside = preferEnd
                ? offset > from && offset <= from + length
                : offset >= from && offset < from + length;

            if (inside)
                return start.GetPositionAtOffset(offset - from);
        }

        return null;
    }

    /// <summary>Every paragraph, including those inside lists.</summary>
    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;

                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var inner in Paragraphs(item.Blocks))
                            yield return inner;
                    break;

                case Section section:
                    foreach (var inner in Paragraphs(section.Blocks))
                        yield return inner;
                    break;
            }
        }
    }
}
