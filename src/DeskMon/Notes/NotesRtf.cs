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
