using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DeskMon.Notes;

/// <summary>
/// Tickable to-do items in the notes: a paragraph that starts with a box,
/// ☐ open or ☑ done. Clicking the box ticks it and strikes the text through.
///
/// Characters and formatting rather than real check box controls, because
/// the notes are RTF and a control does not survive being saved - a box is
/// just a letter, and strikethrough is ordinary formatting. So the list looks
/// the same in WordPad, Word or anything else that opens the file, and
/// nothing about it needs special handling when it is loaded back.
/// </summary>
internal static class Checklist
{
    public const string Open = "☐";
    public const string Done = "☑";

    /// <summary>What a new item starts as: the box and the space after it.</summary>
    public const string NewItem = Open + " ";

    /// <summary>
    /// The boxes' own font. Left to the text's, Windows fell back to a
    /// different font for each: ☐ came from a symbol font, small and thin,
    /// and ☑ from the colour emoji font, big and filled in - an open item and
    /// a done one looked like two different kinds of box. Segoe UI Symbol has
    /// both, drawn alike, and is on every Windows.
    /// </summary>
    private static readonly FontFamily BoxFont = new("Segoe UI Symbol");

    /// <summary>A little air between items, so a list of one-liners doesn't read as one paragraph.</summary>
    private const double ItemSpacing = 3;

    /// <summary>
    /// Puts an item's layout right, or takes it off a paragraph that has
    /// stopped being one - its box deleted. Run on the line being edited as you
    /// type, and on every line when notes are loaded; it changes nothing that
    /// is already right, so running it again costs nothing and records no edit.
    ///
    /// An item gets its box in <see cref="BoxFont"/>, a little space after it,
    /// and a hanging indent exactly as wide as the box and its space: a long
    /// item that wrapped used to start its second line under the box, where it
    /// read as a line of its own. All ordinary paragraph formatting, so it is
    /// saved in the RTF and looks the same in WordPad.
    /// </summary>
    public static void Tidy(Paragraph paragraph)
    {
        if (!IsItem(paragraph, out _, out var boxStart, out var boxEnd))
        {
            // Only the exact layout an item gets is taken off - a hanging
            // indent set some other way, by pasting from Word say, is left be.
            if (paragraph.TextIndent < 0 && Math.Abs(paragraph.Margin.Left + paragraph.TextIndent) < 0.5
                && Math.Abs(paragraph.Margin.Bottom - ItemSpacing) < 0.5)
            {
                paragraph.ClearValue(Paragraph.TextIndentProperty);
                paragraph.ClearValue(Block.MarginProperty);
            }
            return;
        }

        var box = new TextRange(boxStart, boxEnd);
        if (box.GetPropertyValue(TextElement.FontFamilyProperty) is not FontFamily font
            || !font.Source.Equals(BoxFont.Source, StringComparison.OrdinalIgnoreCase))
            box.ApplyPropertyValue(TextElement.FontFamilyProperty, BoxFont);

        double indent = HangingIndent(paragraph, boxStart);
        if (Math.Abs(paragraph.TextIndent + indent) > 0.5 || Math.Abs(paragraph.Margin.Left - indent) > 0.5
            || Math.Abs(paragraph.Margin.Bottom - ItemSpacing) > 0.5)
        {
            paragraph.TextIndent = -indent;
            paragraph.Margin = new Thickness(indent, 0, 0, ItemSpacing);
        }
    }

    /// <summary>Every item in the document, for notes just loaded - written before this layout existed, or in another program.</summary>
    public static void TidyAll(FlowDocument document)
    {
        foreach (var paragraph in document.Blocks.OfType<Paragraph>().ToList())
            Tidy(paragraph);
    }

    /// <summary>
    /// The width of the box and the space after it, measured in the fonts and
    /// size they are drawn in - so the wrapped lines line up with the first
    /// letter of the item whatever size the notes are written in.
    /// </summary>
    private static double HangingIndent(Paragraph paragraph, TextPointer boxStart)
    {
        double size = boxStart.Parent is TextElement element ? element.FontSize : paragraph.FontSize;

        double Width(string text, FontFamily family) => new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

        return Math.Ceiling(Width(Open, BoxFont) + Width(" ", paragraph.FontFamily));
    }

    /// <summary>
    /// Whether <paramref name="paragraph"/> is an item, and where its box is.
    /// The box has to be the paragraph's very first character - a ☐ written in
    /// the middle of a sentence is just a character.
    /// </summary>
    public static bool IsItem(Paragraph paragraph, out bool done, out TextPointer boxStart, out TextPointer boxEnd)
    {
        done = false;
        boxStart = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        boxEnd = boxStart;

        var next = boxStart.GetNextInsertionPosition(LogicalDirection.Forward);

        // An empty paragraph's next position is in the paragraph after it.
        if (next is null || next.CompareTo(paragraph.ContentEnd) > 0)
            return false;

        string first = new TextRange(boxStart, next).Text;
        if (first != Open && first != Done)
            return false;

        done = first == Done;
        boxEnd = next;
        return true;
    }

    /// <summary>
    /// The item whose box is under <paramref name="point"/>, or null. Only the
    /// box itself counts, with a couple of pixels' slack: a click on the text
    /// has to place the caret as usual, or an item could never be edited.
    /// </summary>
    public static Paragraph? BoxAt(RichTextBox editor, Point point)
    {
        // Snapped to the nearest text, not "exactly here or nothing": the box
        // hangs out into the paragraph's left margin (see Tidy), and a point
        // in the margin is outside the paragraph as far as the exact lookup
        // goes - so every click on a box found nothing and none could be
        // ticked. Snapping finds the line; the box's own outline, checked
        // below, still decides whether the click was on it.
        var position = editor.GetPositionFromPoint(point, snapToText: true);
        if (position?.Paragraph is not { } paragraph)
            return null;

        if (!IsItem(paragraph, out _, out var boxStart, out var boxEnd))
            return null;

        Rect left = boxStart.GetCharacterRect(LogicalDirection.Forward);
        Rect right = boxEnd.GetCharacterRect(LogicalDirection.Backward);

        bool over = point.X >= left.Left - 2 && point.X <= right.Right + 2
                 && point.Y >= left.Top && point.Y <= left.Bottom;

        return over ? paragraph : null;
    }

    /// <summary>
    /// Ticks an open item or reopens a done one. The text after the box is
    /// struck through or cleared; the box itself is never struck, so it stays
    /// easy to read which state it is in.
    /// </summary>
    public static void Toggle(RichTextBox editor, Paragraph paragraph)
    {
        if (!IsItem(paragraph, out bool done, out var boxStart, out var boxEnd))
            return;

        // One undo step for the box and the strikethrough together.
        editor.BeginChange();
        try
        {
            new TextRange(boxStart, boxEnd).Text = done ? Open : Done;

            // Found again: replacing the box moved the positions on.
            if (!IsItem(paragraph, out _, out _, out boxEnd))
                return;

            // Cleared from the box on (older notes struck the space too), but
            // struck only from the text on: a line through the space right
            // after the box ran into the box and read as crossing it out.
            if (done)
                RemoveStrikethrough(boxEnd, paragraph.ContentEnd);
            else
                new TextRange(AfterBox(paragraph) ?? boxEnd, paragraph.ContentEnd).ApplyPropertyValue(
                    Inline.TextDecorationsProperty, TextDecorations.Strikethrough);

            // The new box takes the formatting of the text it replaced, which
            // may not be the box font.
            Tidy(paragraph);
        }
        finally
        {
            editor.EndChange();
        }
    }

    /// <summary>
    /// Takes strikethrough off the text between two positions, wherever it is
    /// set - on the text itself, or on an element around it.
    ///
    /// Clearing the range alone is not enough, and that was a real bug: when
    /// the notes are loaded back from RTF, WPF puts the formatting on a span
    /// wrapping the text rather than on the text, and TextDecorations is not
    /// inherited - so the range reported no strikethrough, clearing it changed
    /// nothing, and an item unticked after a restart stayed struck through.
    /// Other decorations, an underline say, are left as they were.
    /// </summary>
    private static void RemoveStrikethrough(TextPointer from, TextPointer to)
    {
        for (var position = from;
             position is not null && position.CompareTo(to) < 0;
             position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                continue;

            for (DependencyObject? node = position.Parent; node is Inline inline; node = inline.Parent)
            {
                if (inline.TextDecorations is not { Count: > 0 } decorations)
                    continue;

                var kept = new TextDecorationCollection();
                foreach (var decoration in decorations)
                    if (decoration.Location != TextDecorationLocation.Strikethrough)
                        kept.Add(decoration);

                if (kept.Count != decorations.Count)
                    inline.TextDecorations = kept;
            }
        }
    }

    /// <summary>
    /// Enter inside an item: a new open item below, which is how every list
    /// in a word processor behaves. Enter on an item with nothing after its
    /// box removes the box instead, ending the list - again as a word
    /// processor does with an empty bullet.
    ///
    /// Returns false to let Enter do its ordinary job: outside an item, or
    /// with the caret before the box, where a plain new line above is what
    /// you would expect.
    /// </summary>
    public static bool HandleEnter(RichTextBox editor)
    {
        if (editor.CaretPosition.Paragraph is not { } paragraph
            || !IsItem(paragraph, out _, out var boxStart, out _)
            || editor.Selection.Start.CompareTo(boxStart) <= 0)
            return false;

        editor.BeginChange();
        try
        {
            if (!editor.Selection.IsEmpty)
                editor.Selection.Text = string.Empty;

            paragraph = editor.CaretPosition.Paragraph ?? paragraph;
            string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;

            if (text.Trim().Length <= Open.Length)
            {
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text = string.Empty;
                Tidy(paragraph);   // an ordinary line again: no hanging indent
                return true;
            }

            var next = editor.CaretPosition.InsertParagraphBreak();
            if (next.Paragraph is not { } added)
                return true;

            next.InsertTextInRun(NewItem);

            // A new item is open, even when the one above was done - and
            // whatever text the break carried down with it is open too.
            new TextRange(added.ContentStart, added.ContentEnd).ApplyPropertyValue(
                Inline.TextDecorationsProperty, new TextDecorationCollection());
            Tidy(added);

            editor.CaretPosition = AfterBox(added) ?? added.ContentEnd;
            return true;
        }
        finally
        {
            editor.EndChange();
        }
    }

    /// <summary>Where typing goes in an item: after the box and its space.</summary>
    public static TextPointer? AfterBox(Paragraph paragraph)
    {
        if (!IsItem(paragraph, out _, out _, out var boxEnd))
            return null;

        // Past the space only if there is one: "☐buy milk" typed without it
        // would otherwise put the caret after the b.
        var afterSpace = boxEnd.GetNextInsertionPosition(LogicalDirection.Forward);
        return afterSpace is not null
               && afterSpace.CompareTo(paragraph.ContentEnd) <= 0
               && new TextRange(boxEnd, afterSpace).Text == " "
            ? afterSpace
            : boxEnd;
    }
}
