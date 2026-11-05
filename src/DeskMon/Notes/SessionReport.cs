using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Media;

namespace DeskMon.Notes;

/// <summary>
/// The sessions as a report to read or print - the HTML and PDF exports.
///
/// The CSV is for a spreadsheet and has every figure the log keeps, some
/// fifty columns; no page holds that, and no one reads it as a table. So
/// these two have a title, the totals, and the columns worth reading: when,
/// how long, how smooth, how hot, and the note. Both are written from the
/// same rows, so the two files say the same.
/// </summary>
internal sealed class SessionReport
{
    /// <param name="Number">Right-aligned, as figures are, so their digits line up.</param>
    public sealed record Column(string Header, bool Number);

    public string Title { get; }
    public string Summary { get; }
    public IReadOnlyList<Column> Columns { get; }
    public IReadOnlyList<string[]> Rows { get; }

    /// <param name="withGame">A Game column first - All games, where every game's sessions share one report.</param>
    /// <param name="energyPrice">Above 0 adds a Cost column, as in the table; the log keeps energy, not what it cost.</param>
    public SessionReport(string title, IReadOnlyList<SessionRecord> sessions, bool withGame,
                         double energyPrice, string energyCurrency)
    {
        var inv = CultureInfo.InvariantCulture;
        Title = title;

        // Oldest first, as the log and the CSV have them, so a printout reads
        // down through the weeks.
        var ordered = sessions.OrderBy(s => s.Start).ToList();

        var columns = new List<Column>();
        if (withGame)
            columns.Add(new("Game", false));
        columns.AddRange(new Column[]
        {
            new("Date", false), new("Time", false), new("Length", true),
            new("Avg FPS", true), new("1% low", true), new("0.1% low", true), new("Stutters", true),
            new("Score", true), new("Ping", true), new("CPU °C", true), new("GPU °C", true),
            new("Energy", true),
        });
        bool costed = energyPrice > 0;
        if (costed)
            columns.Add(new(energyCurrency.Length > 0 ? $"Cost ({energyCurrency})" : "Cost", true));
        columns.Add(new("Note", false));
        Columns = columns;

        static string Fixed(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

        var rows = new List<string[]>();
        foreach (var s in ordered)
        {
            var row = new List<string>();
            if (withGame)
                row.Add(s.Game ?? string.Empty);
            row.Add(s.Start.ToString("ddd d MMM yyyy", inv));
            row.Add($"{s.Start.ToString("HH:mm", inv)}–{s.End.ToString("HH:mm", inv)}");
            row.Add(SessionRecord.Describe(s.Length));
            row.Add(Fixed(s.AverageFps, "0"));
            row.Add(Fixed(s.OnePercentLow, "0"));
            row.Add(Fixed(s.PointOnePercentLow, "0"));
            row.Add(s.Stutters?.ToString(inv) ?? string.Empty);
            row.Add(PerformanceScore.For(s)?.Score.ToString(inv) ?? string.Empty);
            row.Add(s.PingMs is double ping ? $"{ping.ToString("0", inv)} ms" : string.Empty);
            row.Add(s.Find("CPU", "°C")?.Value ?? string.Empty);
            row.Add(s.Find("GPU", "°C")?.Value ?? string.Empty);
            row.Add(s.EnergyWh is double wh ? $"{wh.ToString("0", inv)} Wh" : string.Empty);
            if (costed)
                row.Add(s.EnergyWh is double cost ? (cost / 1000 * energyPrice).ToString("0.00", inv) : string.Empty);
            row.Add(s.Note ?? string.Empty);
            rows.Add(row.ToArray());
        }
        Rows = rows;

        // The totals the sessions window shows over its table, in one line:
        // how many, how long altogether, over which dates. The average frame
        // rate weighted by length, so a 5-minute test doesn't count as much
        // as an evening.
        var played = TimeSpan.FromTicks(ordered.Sum(s => s.Length.Ticks));
        var parts = new List<string> { ordered.Count == 1 ? "1 session" : $"{ordered.Count} sessions", SessionRecord.Describe(played) };
        if (ordered.Count > 0)
            parts.Add(ordered[0].Start.Date == ordered[^1].Start.Date
                ? ordered[0].Start.ToString("d MMM yyyy", inv)
                : $"{ordered[0].Start.ToString("d MMM yyyy", inv)} – {ordered[^1].Start.ToString("d MMM yyyy", inv)}");
        var timed = ordered.Where(s => s.AverageFps is not null && s.Length > TimeSpan.Zero).ToList();
        double weight = timed.Sum(s => s.Length.TotalMinutes);
        if (weight > 0)
            parts.Add($"{(timed.Sum(s => s.AverageFps!.Value * s.Length.TotalMinutes) / weight).ToString("0", inv)} fps average");
        parts.Add($"exported {DateTime.Now.ToString("yyyy-MM-dd HH:mm", inv)}");
        Summary = string.Join("  ·  ", parts);
    }

    // --- HTML ----------------------------------------------------------------

    /// <summary>
    /// One file with its style inside, so it opens the same anywhere and can
    /// be mailed on its own. Light or dark by the reader's own setting; the
    /// table head stays in view while scrolling a long history.
    /// </summary>
    public string ToHtml()
    {
        static string E(string text) => WebUtility.HtmlEncode(text);

        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(E(Title)).Append("</title>\n<style>\n")
            .Append(":root { --bg: #ffffff; --text: #1b1f27; --dim: #667085; --line: #e4e7ec; --head: #f2f4f7; --zebra: #fafbfc; --accent: #3b82f6; }\n")
            .Append("@media (prefers-color-scheme: dark) { :root { --bg: #121216; --text: #e6eaf0; --dim: #8b95a5; --line: #262a33; --head: #1b1e25; --zebra: #16181e; --accent: #60a5fa; } }\n")
            .Append("body { margin: 0; padding: 28px 16px 40px; background: var(--bg); color: var(--text); font: 14px/1.45 \"Segoe UI\", system-ui, sans-serif; }\n")
            .Append("main { max-width: 1200px; margin: 0 auto; }\n")
            .Append("h1 { margin: 0 0 4px; font-size: 22px; font-weight: 600; }\n")
            .Append(".summary { margin: 0 0 20px; color: var(--dim); }\n")
            .Append(".wrap { overflow-x: auto; border: 1px solid var(--line); border-radius: 8px; }\n")
            .Append("table { border-collapse: collapse; width: 100%; font-size: 13px; }\n")
            .Append("th, td { padding: 6px 10px; text-align: left; white-space: nowrap; border-bottom: 1px solid var(--line); }\n")
            .Append("th { position: sticky; top: 0; background: var(--head); font-weight: 600; color: var(--dim); }\n")
            .Append("tbody tr:nth-child(even) { background: var(--zebra); }\n")
            .Append("tbody tr:last-child td { border-bottom: none; }\n")
            .Append(".n { text-align: right; font-variant-numeric: tabular-nums; }\n")
            .Append("td.note { white-space: normal; min-width: 180px; }\n")
            .Append("footer { margin-top: 14px; color: var(--dim); font-size: 12px; }\n")
            .Append("</style>\n</head>\n<body>\n<main>\n")
            .Append("<h1>").Append(E(Title)).Append("</h1>\n")
            .Append("<p class=\"summary\">").Append(E(Summary)).Append("</p>\n")
            .Append("<div class=\"wrap\">\n<table>\n<thead><tr>");

        foreach (var column in Columns)
            html.Append(column.Number ? "<th class=\"n\">" : "<th>").Append(E(column.Header)).Append("</th>");
        html.Append("</tr></thead>\n<tbody>\n");

        foreach (var row in Rows)
        {
            html.Append("<tr>");
            for (int i = 0; i < row.Length; i++)
            {
                string css = Columns[i].Number ? " class=\"n\"" : Columns[i].Header == "Note" ? " class=\"note\"" : string.Empty;
                html.Append("<td").Append(css).Append('>').Append(E(row[i])).Append("</td>");
            }
            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n</div>\n")
            .Append("<footer>Exported from DeskMon. Temperatures are each session's highest, the lows its worst.</footer>\n")
            .Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    // --- PDF -----------------------------------------------------------------
    //
    // Written by hand rather than with a library: a table of text needs only
    // the PDF basics - pages, two of the fonts every PDF reader has built in
    // (Helvetica and its bold), text and grey boxes. Helvetica's letters are
    // measured with Arial's, which was made to the same widths, so the
    // columns are sized to what is really in them.

    private const double PageWidth = 842, PageHeight = 595;   // A4 landscape, in points
    private const double Margin = 36;
    private const double RowHeight = 14;
    private const double CellPad = 4;

    /// <summary>The Game column's widest, in points - "Baldur's Gate 3" and the like fit; longer names end in "…".</summary>
    private const double WidestGame = 130;

    /// <summary>
    /// The report as a PDF: landscape A4, the title and totals on the first
    /// page, the table across as many pages as it takes with its head on each,
    /// and the page number at the foot.
    /// </summary>
    public byte[] ToPdf()
    {
        var regular = new Metrics("Arial", bold: false);
        var bold = new Metrics("Arial", bold: true);

        // The size that fits every column but the note in the page's width,
        // down to 6.5 pt; the note gets the rest and is cut short past it.
        double usable = PageWidth - 2 * Margin;
        int note = Columns.Count - 1;
        int game = Columns.Count > 0 && Columns[0].Header == "Game" ? 0 : -1;   // All games only
        double size = 8;
        double[] widths;
        while (true)
        {
            widths = Columns.Select((column, i) => Math.Max(
                bold.Width(column.Header, size),
                Rows.Count == 0 ? 0 : Rows.Max(row => regular.Width(row[i], size))) + 2 * CellPad).ToArray();
            // The Game column held to a width, its names cut short past it:
            // one long name widened it until the table ran off the page.
            if (game >= 0)
                widths[game] = Math.Min(widths[game], WidestGame);
            double others = widths.Take(note).Sum();
            if (others + 60 <= usable || size <= 6.5)
            {
                widths[note] = Math.Max(60, usable - others);
                break;
            }
            size -= 0.5;
        }

        var pages = new List<string>();
        var page = new StringBuilder();
        double y = PageHeight - Margin;

        void Text(double x, double baseline, string text, Metrics font, double points, double grey)
            => page.Append(Fmt($"{grey} g BT /{font.Name} {points} Tf {x} {baseline} Td ")).Append(PdfString(text)).Append(" Tj ET\n");

        void Box(double x, double top, double width, double height, double grey)
            => page.Append(Fmt($"{grey} g {x} {top - height} {width} {height} re f\n"));

        void Head()
        {
            Box(Margin, y, usable, RowHeight, 0.92);
            double x = Margin;
            for (int i = 0; i < Columns.Count; i++)
            {
                string header = Columns[i].Header;
                double left = Columns[i].Number ? x + widths[i] - CellPad - bold.Width(header, size) : x + CellPad;
                Text(left, y - RowHeight + 4, header, bold, size, 0.25);
                x += widths[i];
            }
            y -= RowHeight;
        }

        void NewPage()
        {
            pages.Add(page.ToString());
            page.Clear();
            y = PageHeight - Margin;
            Head();
        }

        // The first page opens with the title and the totals.
        Text(Margin, y - 16, Title, bold, 16, 0.1);
        Text(Margin, y - 32, Summary, regular, 9, 0.4);
        y -= 46;
        Head();

        for (int r = 0; r < Rows.Count; r++)
        {
            if (y - RowHeight < Margin + 16)
                NewPage();

            if (r % 2 == 1)
                Box(Margin, y, usable, RowHeight, 0.97);

            double x = Margin;
            for (int i = 0; i < Columns.Count; i++)
            {
                string cell = Rows[r][i];
                if (i == note || i == game)
                    cell = regular.Fit(cell, size, widths[i] - 2 * CellPad);
                double left = Columns[i].Number ? x + widths[i] - CellPad - regular.Width(cell, size) : x + CellPad;
                Text(left, y - RowHeight + 4, cell, regular, size, 0.1);
                x += widths[i];
            }
            y -= RowHeight;
        }

        if (Rows.Count == 0)
            Text(Margin + CellPad, y - RowHeight + 4, "No sessions.", regular, size, 0.4);
        pages.Add(page.ToString());

        // The foot of every page, once their number is known.
        for (int p = 0; p < pages.Count; p++)
        {
            page.Clear().Append(pages[p]);
            Text(Margin, Margin - 18, "DeskMon", regular, 8, 0.55);
            string number = $"Page {p + 1} of {pages.Count}";
            Text(PageWidth - Margin - regular.Width(number, 8), Margin - 18, number, regular, 8, 0.55);
            pages[p] = page.ToString();
        }

        return Assemble(pages, regular, bold);
    }

    /// <summary>
    /// The file around the pages: catalogue, page list, the two fonts, each
    /// page and its drawing, and the table of where each of those starts,
    /// which a reader needs to open it. All in ASCII - text outside it is
    /// written as octal escapes - so the byte offsets are the string's.
    /// </summary>
    private byte[] Assemble(List<string> pages, Metrics regular, Metrics bold)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty,   // the page list, filled in below once the page numbers are known
            regular.FontObject("Helvetica"),
            bold.FontObject("Helvetica-Bold"),
            $"<< /Title {PdfString(Title)} /Producer (DeskMon) >>",
        };

        var kids = new List<string>();
        foreach (string drawing in pages)
        {
            int pageObject = objects.Count + 1;
            kids.Add($"{pageObject} 0 R");
            objects.Add(Fmt($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] ") +
                        $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObject + 1} 0 R >>");
            objects.Add($"<< /Length {drawing.Length} >>\nstream\n{drawing}endstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pages.Count} >>";

        var file = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Length);
            file.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = file.Length;
        file.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            file.Append($"{offset:D10} 00000 n \n");
        file.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(file.ToString());
    }

    /// <summary>Numbers with a full stop, two decimals at most - the PDF's own form, whatever the PC's region.</summary>
    private static string Fmt(FormattableString text)
        => string.Format(CultureInfo.InvariantCulture, text.Format,
                         text.GetArguments().Select(a => a is double d ? (object)Math.Round(d, 2) : a).ToArray());

    /// <summary>
    /// A PDF text string in the fonts' Windows-1252 encoding: brackets and
    /// backslashes escaped, anything past ASCII as an octal byte. A character
    /// that encoding lacks - a game name in Japanese, say - comes out as "?":
    /// the built-in fonts have no letters for it.
    /// </summary>
    private static string PdfString(string text)
    {
        var s = new StringBuilder("(");
        foreach (char c in text)
        {
            byte b = Metrics.ToWinAnsi(c);
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
                s.Append('\\').Append((char)b);
            else if (b is < 32 or > 126)
                s.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else
                s.Append((char)b);
        }
        return s.Append(')').ToString();
    }

    /// <summary>A built-in PDF font's widths, read from the matching Windows font.</summary>
    private sealed class Metrics
    {
        public string Name { get; }
        private readonly GlyphTypeface? _glyphs;

        public Metrics(string family, bool bold)
        {
            Name = bold ? "F2" : "F1";
            var face = new Typeface(new FontFamily(family), System.Windows.FontStyles.Normal,
                                    bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                                    System.Windows.FontStretches.Normal);
            face.TryGetGlyphTypeface(out _glyphs);
        }

        /// <summary>
        /// Points wide at this size. A character the font lacks counts as half
        /// an em - and without Arial at all, every one does, which only makes
        /// the columns roomier than they need be.
        /// </summary>
        public double Width(string text, double size)
        {
            double width = 0;
            foreach (char c in text)
            {
                char drawn = FromWinAnsi(ToWinAnsi(c));
                width += _glyphs is not null && _glyphs.CharacterToGlyphMap.TryGetValue(drawn, out ushort glyph)
                    ? _glyphs.AdvanceWidths[glyph]
                    : 0.5;
            }
            return width * size;
        }

        /// <summary>
        /// The font as the PDF names it, with the width of every letter it
        /// can draw. Without them a reader is free to put in a font of its
        /// own, and one did: wider than Arial, so right-aligned figures ran
        /// past their column's edge. With them, every reader spaces the text
        /// as it was measured here.
        /// </summary>
        public string FontObject(string baseFont)
        {
            var widths = Enumerable.Range(32, 224).Select(code =>
            {
                char c = FromWinAnsi((byte)code);
                double em = _glyphs is not null && _glyphs.CharacterToGlyphMap.TryGetValue(c, out ushort glyph)
                    ? _glyphs.AdvanceWidths[glyph]
                    : 0.5;
                return ((int)Math.Round(em * 1000)).ToString(CultureInfo.InvariantCulture);
            });
            return $"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} /Encoding /WinAnsiEncoding " +
                   $"/FirstChar 32 /LastChar 255 /Widths [{string.Join(" ", widths)}] >>";
        }

        /// <summary>Cut to the width with "…" at the end - a long note on one line of the table.</summary>
        public string Fit(string text, double size, double room)
        {
            if (Width(text, size) <= room)
                return text;
            int keep = text.Length;
            while (keep > 0 && Width(text[..keep] + "…", size) > room)
                keep--;
            return keep == 0 ? string.Empty : text[..keep].TrimEnd() + "…";
        }

        // Windows-1252's own letters where it differs from Latin-1 - the
        // dashes, quotes and ellipsis these reports actually use.
        private static readonly Dictionary<char, byte> Special = new()
        {
            ['€'] = 0x80, ['…'] = 0x85, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94,
            ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['™'] = 0x99,
        };

        public static byte ToWinAnsi(char c)
        {
            if (c < 0x80 || (c >= 0xA0 && c <= 0xFF))
                return (byte)c;
            return Special.TryGetValue(c, out byte b) ? b : (byte)'?';
        }

        public static char FromWinAnsi(byte b)
        {
            foreach (var (c, code) in Special)
                if (code == b)
                    return c;
            return (char)b;
        }
    }
}
