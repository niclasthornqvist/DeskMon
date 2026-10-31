using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// The widget's tooltips as small cards, in SessionTip's style: a caption
/// saying where the row sits, the row's name in bold, what it means, the
/// levels as label / value lines in their own colours, and what a click does
/// at the foot. They used to be two or three plain sentences in one grey,
/// where the levels and the click hint had to be read out of the text.
/// </summary>
internal static class InfoTip
{
    /// <summary>One label / value line, e.g. "Amber at" / "80 °C or above" in the amber.</summary>
    public sealed record Fact(string Label, string Value, Brush? Brush = null);

    /// <param name="caption">Small capitals over the title - the section, "TEMPERATURE"; null for none.</param>
    /// <param name="title">The name in bold; null for a card that is only a sentence - see FromText.</param>
    /// <param name="body">The explanation: its first line plainly, the lines after it dimmer, as detail.</param>
    /// <param name="hints">What clicking or right-clicking does, under a rule at the foot.</param>
    public static ToolTip Make(UiTheme theme, string? caption, string? title, string? body,
                               IEnumerable<Fact>? facts = null, IEnumerable<string>? hints = null)
    {
        // The shared tooltip style's width and left alignment apply to this
        // one as to any (see PanelStyles.xaml), so a ToolTip around the card
        // is all it takes.
        return new ToolTip { Content = Card(theme, caption, title, body, facts, hints) };
    }

    /// <summary>A card still to be made - see <see cref="Later"/>.</summary>
    public sealed record LaterCard(Func<object> Make);

    /// <summary>
    /// <see cref="Make"/>, with the card made only when the tooltip is drawn -
    /// see TooltipCards. For what a binding reads often and is rarely seen:
    /// the widget's rows read theirs on every settings keystroke and every new
    /// peak, and each read built a whole card nobody was looking at.
    /// </summary>
    public static LaterCard Later(UiTheme theme, string? caption, string? title, string? body,
                                  IEnumerable<Fact>? facts = null, IEnumerable<string>? hints = null)
        => new(() => Card(theme, caption, title, body, facts, hints));

    /// <summary>The card itself, for a ToolTip (Make) or for a plain text tooltip's content (Cards).</summary>
    private static StackPanel Card(UiTheme theme, string? caption, string? title, string? body,
                                   IEnumerable<Fact>? facts, IEnumerable<string>? hints)
    {
        var card = new StackPanel();

        if (caption is not null)
            card.Children.Add(new TextBlock
            {
                Text = caption,
                Foreground = theme.Section,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 1),
            });

        if (title is not null)
            card.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = theme.NotesText,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

        // Each line of the explanation a block of its own, with a little room
        // between, rather than one paragraph the eye has to split up itself.
        // No gap over the first when there is no title above it.
        var lines = body?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        for (int i = 0; i < lines.Length; i++)
            card.Children.Add(new TextBlock
            {
                Text = lines[i],
                Foreground = i == 0 ? theme.NotesText : theme.Label,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, i > 0 ? 3 : title is null && caption is null ? 0 : 5, 0, 0),
            });

        // The levels as a two-column grid, so "Amber at" and "Red at" line up
        // and the figures stand out in the colour the row turns at them.
        var factList = facts?.ToList() ?? new List<Fact>();
        if (factList.Count > 0)
        {
            var grid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach (var fact in factList)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int row = grid.RowDefinitions.Count - 1;
                var name = new TextBlock { Text = fact.Label, Foreground = theme.Label, Margin = new Thickness(0, 1, 14, 1) };
                var value = new TextBlock
                {
                    Text = fact.Value,
                    Foreground = fact.Brush ?? theme.NotesText,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                Grid.SetRow(name, row);
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);
                grid.Children.Add(name);
                grid.Children.Add(value);
            }
            card.Children.Add(grid);
        }

        // Under a faint rule, so "what it is" and "what to do with it" read
        // as two things; dimmer, as the part already known after a first look.
        var hintList = hints?.Where(h => h.Length > 0).ToList() ?? new List<string>();
        if (hintList.Count > 0)
        {
            card.Children.Add(new Border
            {
                Height = 1,
                Background = theme.Separator,
                Margin = new Thickness(0, 8, 0, 5),
            });
            foreach (string hint in hintList)
                card.Children.Add(new TextBlock
                {
                    Text = hint,
                    Foreground = theme.Header,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 1),
                });
        }

        return card;
    }

    /// <summary>
    /// Every plain-text tooltip in the program shown as a card - see Cards,
    /// which the shared tooltip style runs each one's text through as it is
    /// drawn. Call once, with the program's one theme.
    /// </summary>
    /// <remarks>
    /// Not done as the tooltip opens, as it first was: some tooltips are given
    /// their text by their own ToolTipOpening handler (the notes' screenshot
    /// button names the hotkey of the moment), which ran after the one that
    /// made the card and put the plain text back. Drawn by the style, every
    /// text is caught however and whenever it was set.
    /// </remarks>
    public static void UseCardsEverywhere(UiTheme theme) => _theme ??= theme;

    private static UiTheme? _theme;

    /// <summary>A tooltip's text as a card in the program's theme, or null before there is one - see TooltipCards.</summary>
    internal static object? CardFor(string text)
        => _theme is { } theme ? CardFromText(theme, text) : null;

    /// <summary>
    /// A card from a tooltip's plain text, by the way the window's tooltips
    /// are written: the first line says what the thing is, and becomes the
    /// title; "Caption: value" on a line of its own gives a caption over the
    /// value; lines that say what a click does go to the foot; lines of a
    /// name and a figure two spaces apart - a day's games and their time -
    /// become aligned rows; the rest is the explanation.
    /// </summary>
    private static StackPanel CardFromText(UiTheme theme, string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? title = lines.Length > 0 ? lines[0] : text;
        string? caption = null;
        var facts = new List<Fact>();

        // "Avg FPS: 128" - short, so a label and its figure, not a sentence.
        int colon = title.IndexOf(": ", StringComparison.Ordinal);
        if (lines.Length == 1 && colon > 0 && colon <= 24)
        {
            caption = title[..colon].ToUpperInvariant();
            title = title[(colon + 2)..];
        }

        // "Search all notes (Ctrl+F)": the key on a row of its own, so the
        // title reads as words and the key is easy to pick out.
        var key = System.Text.RegularExpressions.Regex.Match(title,
            @"\s*\(((?:Ctrl|Shift|Alt|Win|Enter|Esc|Delete|F\d{1,2})[^()]*)\)$");
        if (key.Success)
        {
            title = title[..key.Index];
            facts.Add(new Fact("Shortcut", key.Groups[1].Value));
        }

        // A single long sentence - "DeskMon was closed during the game, so…" -
        // reads as text, not as a heading: in bold at title size it was a wall.
        string? lead = null;
        if (lines.Length == 1 && caption is null && title.Length > 60)
        {
            lead = title;
            title = null;
        }

        static bool IsHint(string line)
            => line.StartsWith("Click", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("Ctrl+click", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("Right-click", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("Double-click", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("Delete ", StringComparison.Ordinal)
               || line.StartsWith("← ", StringComparison.Ordinal);

        var body = new List<string>();
        if (lead is not null)
            body.Add(lead);
        var hints = new List<string>();
        foreach (string line in lines.Skip(1))
        {
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            if (IsHint(line))
                hints.Add(line);
            else if (gap > 0)
                facts.Add(new Fact(line[..gap], line[gap..].Trim()));
            else
                body.Add(line);
        }

        return Card(theme, caption, title, body.Count > 0 ? string.Join("\n", body) : null, facts, hints);
    }
}

/// <summary>
/// The shared tooltip style's content converter (PanelStyles.xaml): a
/// tooltip's text into a card - see InfoTip - made fresh each time it is drawn,
/// so it is in the theme of the moment; anything else, a card already or
/// SessionTip's, as it is. Public and top-level so the style can name it.
/// </summary>
public sealed class TooltipCards : System.Windows.Data.IValueConverter
{
    public static readonly TooltipCards Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is InfoTip.LaterCard later ? later.Make()
         : value is string text && !string.IsNullOrWhiteSpace(text) ? InfoTip.CardFor(text) ?? value
         : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
