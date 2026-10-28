using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DeskMon.Notes;

/// <summary>
/// Takes the window's own text colour back out of the RTF that WPF generates.
///
/// WPF's RTF writer records the colour each piece of text is *rendered* in,
/// whether that colour was applied to the text or merely inherited from the
/// editor around it. DeskMon renders notes in a near-white suited to a dark
/// panel, so every note was being stored as near-white text - which opens in
/// WordPad or Word as near-white on white, i.e. invisible. These notes are
/// meant to outlive the program, and that quietly broke the promise.
///
/// Writing black instead would only move the problem: the next reader of that
/// file is DeskMon itself, where black on a dark panel is just as unreadable.
/// What the file needs is *no* colour for ordinary text, so that every reader
/// applies its own default - black on white in WordPad, near-white here - and
/// only the colours you chose deliberately are recorded.
///
/// Removing a reference also repairs old notes: text loaded from a file that
/// already had the colour baked in carries it explicitly, and gets stripped on
/// the next save like anything else.
/// </summary>
public static class NotesRtf
{
    /// <summary>
    /// Strips every reference to <paramref name="normal"/> from the document's
    /// colour table. Returns the bytes unchanged if that colour isn't used, or
    /// if this doesn't look like RTF.
    /// </summary>
    public static byte[] WithoutColour(byte[] rtf, Color normal)
    {
        try
        {
            // RTF is 7-bit ASCII with escapes, which is why this can be treated
            // as text at all; anything outside ASCII is already \'xx encoded.
            string text = Encoding.ASCII.GetString(rtf);

            int index = IndexInColourTable(text, normal);
            if (index < 0)
                return rtf;

            // The space after a control word is the delimiter and is swallowed
            // by the reader, so it has to go with the word - leave it behind and
            // it becomes a literal space in the middle of the notes. The
            // lookahead keeps \cf1 from matching inside \cf12.
            string stripped = Regex.Replace(text, $@"\\cf{index}(?![0-9]) ?", string.Empty);

            // The now-unused entry is left in the colour table: harmless, and
            // renumbering the table would mean rewriting every other reference.
            return Encoding.ASCII.GetBytes(stripped);
        }
        catch
        {
            // Never risk the notes over a cosmetic fix.
            return rtf;
        }
    }

    /// <summary>
    /// A blank line, a heading in <paramref name="headingColour"/> and bold,
    /// then <paramref name="lines"/>, added at the end of a notes file's RTF -
    /// for a session note going into notes that aren't on screen.
    ///
    /// Done on the bytes rather than by loading the notes into a document:
    /// that builds every screenshot in them on the UI thread, twice with the
    /// switch back, and froze the widget as the note was saved. Each paragraph
    /// is a group of its own with \plain, so it takes nothing from the text
    /// before it. Null when the bytes don't look like RTF, so nothing is
    /// written over a file this can't read.
    /// </summary>
    /// <param name="rtf">The file as it is, or null for notes that have none yet.</param>
    /// <param name="sizeDips">The notes' text size in WPF units, so the entry matches the rest.</param>
    public static byte[]? WithParagraphsAdded(byte[]? rtf, string heading, Color headingColour,
                                              System.Collections.Generic.IEnumerable<string> lines, double sizeDips)
    {
        try
        {
            string text = rtf is { Length: > 0 }
                ? Encoding.ASCII.GetString(rtf)
                : @"{\rtf1\ansi\ansicpg1252\uc1\deff0{\fonttbl{\f0 Segoe UI;}}{\colortbl;}}";
            if (!text.StartsWith(@"{\rtf", StringComparison.Ordinal))
                return null;

            // The heading's colour as one more entry in the colour table; none
            // when there is no table, rather than guessing where to put one.
            int colour = -1;
            var table = Regex.Match(text, @"\\colortbl(?<body>[^}]*)\}");
            if (table.Success)
            {
                string body = table.Groups["body"].Value;
                if (!body.EndsWith(';'))
                    body += ";";
                colour = body.Split(';').Length - 1;   // slots are counted from nought, the empty "auto" one too
                string entry = $@"\red{headingColour.R}\green{headingColour.G}\blue{headingColour.B};";
                text = text[..table.Groups["body"].Index] + body + entry + text[(table.Groups["body"].Index + table.Groups["body"].Length)..];
            }

            // The document's own closing brace: everything goes in before it.
            int end = text.LastIndexOf('}');
            if (end < 0)
                return null;

            // Half-points, as RTF counts sizes; WPF's units are 96ths of an inch.
            int size = (int)Math.Round(sizeDips * 72 / 96 * 2);
            var added = new StringBuilder();
            added.Append($@"{{\pard\plain\fs{size}\par}}");
            added.Append($@"{{\pard\plain\fs{size}\b{(colour >= 0 ? $@"\cf{colour}" : string.Empty)} ").Append(Escape(heading)).Append(@"\par}");
            foreach (string line in lines)
                added.Append($@"{{\pard\plain\fs{size} ").Append(Escape(line)).Append(@"\par}");

            return Encoding.ASCII.GetBytes(text.Insert(end, added.ToString()));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Text as RTF: its three special characters escaped, anything past ASCII
    /// as \u with "?" for a reader that can't show it - RTF itself is 7-bit.
    /// </summary>
    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\\' or '{' or '}')
                escaped.Append('\\').Append(c);
            else if (c is '\r' or '\n')
                continue;
            else if (c < 0x80)
                escaped.Append(c);
            else
                escaped.Append(@"\u").Append((short)c).Append('?');   // signed, as RTF reads it
        }
        return escaped.ToString();
    }

    /// <summary>
    /// Which slot of the <c>\colortbl</c> holds <paramref name="wanted"/>, or
    /// -1. Entries are separated by semicolons and numbered from zero, and an
    /// empty first entry - RTF's "automatic" colour - counts as a slot.
    /// </summary>
    private static int IndexInColourTable(string rtf, Color wanted)
    {
        var table = Regex.Match(rtf, @"\\colortbl(?<body>[^}]*)\}");
        if (!table.Success)
            return -1;

        string target = $@"\red{wanted.R}\green{wanted.G}\blue{wanted.B}";
        string[] entries = table.Groups["body"].Value.Split(';');

        for (int i = 0; i < entries.Length; i++)
            if (entries[i].Trim().Equals(target, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }
}
