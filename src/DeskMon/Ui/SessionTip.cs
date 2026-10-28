using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DeskMon.Notes;

namespace DeskMon.Ui;

/// <summary>
/// A session in full, as a tooltip - the session history's table and chart,
/// and the notes window's last-session card. One description here, as
/// StatCard is for the cards, so the two windows can't drift apart: the
/// notes window had its own copy of the layout, and a fix to one never
/// reached the other.
///
/// Laid out like the windows themselves: the ID on an accent pill with the
/// day beside it, the key figures as big numbers with a caption over each,
/// then labelled blocks for the rest. It used to be "·"-joined lines in two
/// greys, where every figure had to be read out of a sentence.
/// </summary>
internal static class SessionTip
{
    /// <param name="settings">For the energy price; null leaves the cost out.</param>
    /// <param name="changes">A new driver or Windows build since the session before, as "driver 576.80 → 581.57"; null for none.</param>
    /// <param name="headline">A line above it all, in <paramref name="headlineBrush"/>: the chart's "Highest in the chart: 142".</param>
    public static ToolTip Make(UiTheme theme, AppSettings? settings, SessionRecord session,
                               IReadOnlyList<string>? changes = null, string? headline = null, Brush? headlineBrush = null)
    {
        var card = new StackPanel();
        double small = Math.Max(9, UiTheme.PanelFontSize - 2.5);
        double captionSize = Math.Max(8, small - 1);

        // A small-caps heading over a block, in the cards' caption style.
        void Heading(string text, double top = 9)
            => card.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = theme.Section,
                FontSize = captionSize,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, top, 0, 3),
            });

        if (headline is not null)
            card.Children.Add(new TextBlock
            {
                Text = headline,
                Foreground = headlineBrush ?? theme.NotesText,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5),
            });

        // Header: the ID on an accent pill, as the chart marks a picked one,
        // so it reads as "which session" at once; the day and hours beside it.
        var header = new DockPanel();
        header.Children.Add(new Border
        {
            Background = theme.Accent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"#{session.Id}",
                Foreground = new SolidColorBrush(theme.WindowColor),
                FontWeight = FontWeights.SemiBold,
            },
        });
        var day = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        day.Inlines.Add(new Run(session.Start.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture))
        {
            Foreground = theme.NotesText,
            FontWeight = FontWeights.SemiBold,
        });
        day.Inlines.Add(new Run($"   {session.Start:HH:mm}–{session.End:HH:mm}") { Foreground = theme.Label });
        header.Children.Add(day);
        card.Children.Add(header);

        // The key figures, big, each with what it is above it.
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        void Tile(string caption, string value, Brush? brush = null)
        {
            var tile = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            tile.Children.Add(new TextBlock { Text = caption, Foreground = theme.Section, FontSize = captionSize, FontWeight = FontWeights.SemiBold });
            tile.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = brush ?? theme.NotesText,
                FontSize = UiTheme.PanelFontSize + 3,
                FontWeight = FontWeights.SemiBold,
            });
            tiles.Children.Add(tile);
        }
        Tile("LENGTH", SessionRecord.Describe(session.Length));
        if (session.AverageFps is double fps)
            Tile("AVG FPS", $"{fps:0}");
        if (session.OnePercentLow is double low)
            Tile("1% LOW", $"{low:0}");
        var result = PerformanceScore.For(session);
        if (result is not null)
            Tile("SCORE", $"{result.Score}  {PerformanceScore.Grade(result.Score)}", ScoreBrush(theme, result.Score));
        card.Children.Add(tiles);

        // Stutters, energy and the loading left out as label / value rows
        // instead of a sentence. The stutters as a rate too - "37" alone says
        // little without the length beside it - and the energy with what it covers.
        var facts = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Fact(string label, string value, string? note = null)
        {
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = facts.RowDefinitions.Count - 1;
            var name = new TextBlock { Text = label, Foreground = theme.Label, Margin = new Thickness(0, 1, 14, 1) };
            var text = new TextBlock { Margin = new Thickness(0, 1, 0, 1) };
            text.Inlines.Add(new Run(value) { Foreground = theme.NotesText, FontWeight = FontWeights.SemiBold });
            if (note is not null)
                text.Inlines.Add(new Run("   " + note) { Foreground = theme.Header });
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            facts.Children.Add(name);
            facts.Children.Add(text);
        }
        if (session.Stutters is long stutters)
            Fact("Stutters", stutters.ToString(CultureInfo.InvariantCulture),
                 stutters > 0 && session.Length.TotalMinutes > 0 ? $"about 1 every {Every(session.Length / stutters)}" : null);
        if (session.EnergyWh is double wh)
        {
            string cost = settings is { EnergyPrice: > 0 } ? $"  ({Cost(wh, settings)})" : string.Empty;
            Fact("Energy", $"{Kwh(wh)} kWh{cost}", "CPU + GPU - the whole PC draws more");
        }
        // What held the frame rate back, with what that means for what to
        // change - the verdict a low score leaves open. The split on its own
        // row under it, dim, for whoever wants the detail.
        if (session.Limits is { Count: > 0 } limits)
        {
            Fact("Limited by", Bottleneck.Verdict(limits), Bottleneck.Advice(limits));
            Fact(string.Empty, Bottleneck.Split(limits));
        }
        // The connection as a whole, not the one spike the PING peak keeps.
        if (session.PingMs is double ping)
            Fact("Network", $"ping {ping:0} ms ±{session.PingJitterMs ?? 0:0}",
                 $"{(session.PingLossPercent ?? 0).ToString("0.#", CultureInfo.InvariantCulture)}% of pings lost");

        // How much loading screen the figures leave out, so a wrong guess by
        // FrameStats' rule is there to see - 0 s is worth showing too.
        if (session.LoadingLeftOut is TimeSpan loading)
            Fact("Loading left out", Every(loading, zero: true), "not counted in the figures above");
        if (facts.RowDefinitions.Count > 0)
            card.Children.Add(facts);

        // Before 1.73 the loading screen counted, which pulled the lows and
        // the score down - Factorio's mod loading took a steady 60 to "Poor".
        // Said here, so an old low score isn't read as the game running badly.
        if (session.CountedLoading)
            card.Children.Add(new TextBlock
            {
                Text = "Logged before loading screens were left out, so its lows and score may read lower than the game ran.",
                Foreground = theme.Header,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

        // In the accent, as in the table: the update to line an FPS change up with.
        if (changes is { Count: > 0 })
        {
            Heading("CHANGED BEFORE THIS SESSION");
            foreach (string change in changes)
                card.Children.Add(new TextBlock { Text = change, Foreground = theme.Accent, Margin = new Thickness(0, 1, 0, 1) });
        }

        var sections = session.PeaksBySection().ToList();
        if (sections.Count > 0)
        {
            // What the arrows mean in the heading itself, not a footnote under
            // the table that was read last, if at all.
            Heading("PEAKS  ·  WORST EACH REACHED  (↑ HIGHEST  ↓ LOWEST)");

            var table = new Grid();
            int readings = sections.Max(s => s.Peaks.Count);
            for (int i = 0; i < 1 + readings * 2; i++)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            for (int row = 0; row < sections.Count; row++)
            {
                var (section, peaks) = sections[row];
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // The section in its own colour so each row starts with a
                // clear name; the label dim, the figure bright with its arrow
                // and unit dimmed, so the number is what stands out.
                var name = new TextBlock { Text = section, Foreground = theme.Header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 14, 1) };
                Grid.SetRow(name, row);
                table.Children.Add(name);

                for (int i = 0; i < peaks.Count; i++)
                {
                    var peak = peaks[i];
                    var label = new TextBlock { Text = peak.Label, Foreground = theme.Label, Margin = new Thickness(0, 1, 5, 1) };
                    var value = new TextBlock { Margin = new Thickness(0, 1, 16, 1) };
                    value.Inlines.Add(new Run(peak.LowerIsWorse ? "↓" : "↑") { Foreground = theme.Header });
                    value.Inlines.Add(new Run(peak.Value) { Foreground = theme.NotesText, FontWeight = FontWeights.SemiBold });
                    if (peak.Unit.Length > 0)
                        value.Inlines.Add(new Run(" " + peak.Unit) { Foreground = theme.Label });
                    Grid.SetRow(label, row);
                    Grid.SetColumn(label, 1 + i * 2);
                    Grid.SetRow(value, row);
                    Grid.SetColumn(value, 2 + i * 2);
                    table.Children.Add(label);
                    table.Children.Add(value);
                }
            }

            card.Children.Add(table);
        }

        // What the score was made of: which part cost the points is what says
        // where to look - the frame rate, the dips, or the heat. A small bar
        // per part shows that at a glance; the figures stay for the detail.
        if (result is not null)
        {
            Heading("HOW THE SCORE WAS REACHED");

            var parts = new Grid();
            for (int i = 0; i < 4; i++)
                parts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            for (int row = 0; row < result.Parts.Count; row++)
            {
                var part = result.Parts[row];
                parts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                const double BarWidth = 56;
                double share = part.Of > 0 ? Math.Clamp(part.Points / part.Of, 0, 1) : 0;
                var track = new Border
                {
                    Width = BarWidth, Height = 5, CornerRadius = new CornerRadius(2.5),
                    Background = theme.Separator,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                    Child = new Border
                    {
                        Width = BarWidth * share, CornerRadius = new CornerRadius(2.5),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = ScoreBrush(theme, (int)Math.Round(share * 100)),
                    },
                };

                // Points as earned of what the part is worth, right-aligned so they line up.
                var cells = new FrameworkElement[]
                {
                    new TextBlock { Text = part.Name, Foreground = theme.Label, Margin = new Thickness(0, 1, 12, 1) },
                    track,
                    new TextBlock { Text = $"{part.Points:0} / {part.Of:0}", Foreground = theme.NotesText, FontWeight = FontWeights.SemiBold,
                                    TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 1, 12, 1) },
                    new TextBlock { Text = part.Detail, Foreground = theme.Header, Margin = new Thickness(0, 1, 0, 1) },
                };
                for (int column = 0; column < cells.Length; column++)
                {
                    Grid.SetRow(cells[column], row);
                    Grid.SetColumn(cells[column], column);
                    parts.Children.Add(cells[column]);
                }
            }
            card.Children.Add(parts);

            if (result.Partial)
                card.Children.Add(new TextBlock
                {
                    Text = "Frame rate and thermals only: logged before DeskMon measured a session's lows and frame times.",
                    Foreground = theme.Header,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
        }

        // "cut short, DeskMon closed" in the warning colour, since it changes
        // what every number above means; anything else on the line plainly.
        if (session.Note is { } note)
            card.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = session.CutShort ? theme.Warn : theme.Label,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 9, 0, 0),
            });

        // Wider than the tooltip style's 340, which suits a sentence: a table
        // doesn't wrap, and a long Game line would be cut off at the edge.
        return new ToolTip { Content = card, MaxWidth = 640 };
    }

    /// <summary>A score in the widget's own green, amber or red - good, fair, needs work.</summary>
    public static Brush ScoreBrush(UiTheme theme, int score) => PerformanceScore.Band(score) switch
    {
        0 => theme.Normal,
        1 => theme.Warn,
        _ => theme.Crit,
    };

    /// <summary>"3 min", "40 s" - how often a stutter came, or how long the loading was.</summary>
    /// <param name="zero">Write "0 s" for nothing, rather than the floor of 1 s a gap between stutters gets.</param>
    public static string Every(TimeSpan gap, bool zero = false)
        => gap.TotalMinutes >= 1 ? $"{gap.TotalMinutes:0} min"
         : $"{(zero ? Math.Max(0, gap.TotalSeconds) : Math.Max(1, gap.TotalSeconds)):0} s";

    /// <summary>"0.31" - watt-hours as kWh, to two places, since a session is rarely a whole one.</summary>
    public static string Kwh(double? wh)
        => wh is double v ? (v / 1000).ToString("0.00", CultureInfo.InvariantCulture) : "–";

    /// <summary>"1.25 kr" - what <paramref name="wh"/> cost at the price in Settings.</summary>
    private static string Cost(double wh, AppSettings settings)
    {
        string cost = (wh / 1000 * settings.EnergyPrice).ToString("0.00", CultureInfo.InvariantCulture);
        string currency = settings.EnergyCurrency?.Trim() ?? string.Empty;
        return currency.Length > 0 ? $"{cost} {currency}" : cost;
    }
}
