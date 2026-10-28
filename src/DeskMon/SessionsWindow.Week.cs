using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMon.Notes;

namespace DeskMon;

// All games' weekly report: this week's play against last week's, game by
// game, and a calendar of the last twelve weeks shaded by time played. The
// totals above answer "how much, ever"; this answers "how much lately", which
// they can't. From DeskMon's own sessions only - Steam's and GOG's playtime
// is a total with no days in it.
public partial class SessionsWindow
{
    /// <summary>One session's span, for the report - read with All games' totals, see ShowSelection.</summary>
    private sealed record PlaySpan(string Game, DateTime Start, DateTime End);

    private IReadOnlyList<PlaySpan> _played = Array.Empty<PlaySpan>();

    /// <summary>How many weeks the calendar shows, this one included.</summary>
    private const int CalendarWeeks = 12;

    /// <summary>
    /// Fills the report under All games' cards, or hides it for one game.
    /// Monday to Sunday, the ISO weeks the toolbar clock numbers.
    /// </summary>
    private void ShowWeek()
    {
        WeekPanel.Children.Clear();
        WeekPanel.ColumnDefinitions.Clear();
        if (!_allGames || _games.Count == 0)
        {
            WeekPanel.Visibility = Visibility.Collapsed;
            return;
        }
        WeekPanel.Visibility = Visibility.Visible;

        var today = DateTime.Today;
        var thisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var lastWeek = thisWeek.AddDays(-7);
        var calendarStart = thisWeek.AddDays(-7 * (CalendarWeeks - 1));

        // Time per day and game, a session past midnight split between the
        // two days - an evening from 22:00 to 01:00 is two hours on one day
        // and one on the next, not three on the first.
        var byDay = new Dictionary<DateTime, Dictionary<string, TimeSpan>>();
        foreach (var span in _played.Where(s => s.End > calendarStart && s.End > s.Start))
        {
            for (var day = span.Start.Date; day < span.End; day = day.AddDays(1))
            {
                var from = span.Start > day ? span.Start : day;
                var to = span.End < day.AddDays(1) ? span.End : day.AddDays(1);
                if (to <= from || day < calendarStart)
                    continue;
                if (!byDay.TryGetValue(day, out var games))
                    byDay[day] = games = new Dictionary<string, TimeSpan>();
                games[span.Game] = games.GetValueOrDefault(span.Game) + (to - from);
            }
        }

        TimeSpan Played(DateTime from, string? game = null)
            => byDay.Where(d => d.Key >= from && d.Key < from.AddDays(7))
                    .Aggregate(TimeSpan.Zero, (sum, d) => sum + (game is null
                        ? d.Value.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b)
                        : d.Value.GetValueOrDefault(game)));

        WeekPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        WeekPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var week = WeekSummary(thisWeek, lastWeek, Played);
        Grid.SetColumn(week, 0);
        WeekPanel.Children.Add(week);

        var calendar = Calendar(calendarStart, today, byDay);
        Grid.SetColumn(calendar, 1);
        WeekPanel.Children.Add(calendar);
    }

    /// <summary>THIS WEEK: the total and how it compares, then a row per game played in either week.</summary>
    private StackPanel WeekSummary(DateTime thisWeek, DateTime lastWeek, Func<DateTime, string?, TimeSpan> played)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        int number = ISOWeek.GetWeekOfYear(thisWeek);
        panel.Children.Add(Heading($"THIS WEEK  ·  W{number}"));

        var now = played(thisWeek, null);
        var before = played(lastWeek, null);
        var total = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
        total.Inlines.Add(new System.Windows.Documents.Run(SessionRecord.Describe(now))
        {
            Foreground = _theme.NotesText, FontWeight = FontWeights.SemiBold, FontSize = UiTheme.PanelFontSize + 3,
        });
        // Up or down in the accent and the dim text: more play isn't good or
        // bad, so not the green and red the scores use.
        var difference = now - before;
        string change = difference == TimeSpan.Zero ? "the same as last week"
                      : $"{(difference > TimeSpan.Zero ? "▲" : "▼")} {SessionRecord.Describe(difference.Duration())} against last week's {SessionRecord.Describe(before)}";
        total.Inlines.Add(new System.Windows.Documents.Run("   " + change)
        {
            Foreground = difference > TimeSpan.Zero ? _theme.Accent : _theme.Label, FontSize = SmallFont,
        });
        panel.Children.Add(total);

        // The games of the two weeks, most played this week first; six at
        // most, so the report stays a glance and the table below is for the rest.
        var games = _played.Select(s => s.Game).Distinct()
            .Select(g => (Game: g, Now: played(thisWeek, g), Before: played(lastWeek, g)))
            .Where(g => g.Now > TimeSpan.Zero || g.Before > TimeSpan.Zero)
            .OrderByDescending(g => g.Now).ThenByDescending(g => g.Before)
            .Take(6).ToList();

        if (games.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Nothing played in the last two weeks.", Foreground = _theme.Label, Margin = new Thickness(0, 6, 0, 0) });
            return panel;
        }

        var rows = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 170 });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Both weeks on one scale, so a short bar beside a long last week reads as less.
        double most = games.Max(g => Math.Max(g.Now.TotalMinutes, g.Before.TotalMinutes));
        for (int i = 0; i < games.Count; i++)
        {
            var (game, nowTime, beforeTime) = games[i];
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock
            {
                Text = game, Foreground = _theme.NotesText, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 10, 2), ToolTip = game, VerticalAlignment = VerticalAlignment.Center,
            };

            // This week's bar over a faint one for last week, in the same track.
            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            track.Children.Add(new Border { Background = _theme.Separator, CornerRadius = new CornerRadius(3) });
            track.Children.Add(new Border
            {
                Width = 90 * beforeTime.TotalMinutes / most, HorizontalAlignment = HorizontalAlignment.Left,
                Background = _theme.AccentAt(0x50), CornerRadius = new CornerRadius(3),
            });
            track.Children.Add(new Border
            {
                Width = 90 * nowTime.TotalMinutes / most, HorizontalAlignment = HorizontalAlignment.Left,
                Background = _theme.Accent, CornerRadius = new CornerRadius(3),
            });

            var figures = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            figures.Inlines.Add(new System.Windows.Documents.Run(nowTime > TimeSpan.Zero ? SessionRecord.Describe(nowTime) : "–")
            {
                Foreground = _theme.NotesText, FontWeight = FontWeights.SemiBold,
            });
            figures.Inlines.Add(new System.Windows.Documents.Run($"   last week {(beforeTime > TimeSpan.Zero ? SessionRecord.Describe(beforeTime) : "–")}")
            {
                Foreground = _theme.Header, FontSize = SmallFont,
            });

            Grid.SetColumn(track, 1);
            Grid.SetColumn(figures, 2);
            foreach (var cell in new UIElement[] { name, track, figures })
            {
                Grid.SetRow(cell, i);
                rows.Children.Add(cell);
            }
        }
        panel.Children.Add(rows);

        panel.Children.Add(new TextBlock
        {
            Text = "From DeskMon's own sessions - Steam's and GOG's playtime has no days to show.",
            Foreground = _theme.Header, FontSize = SmallFont - 1, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return panel;
    }

    /// <summary>
    /// The last twelve weeks, a column each, Monday at the top: each day
    /// shaded by how long was played, with the games in its tooltip. Today
    /// outlined, so "where am I" needs no counting.
    /// </summary>
    private StackPanel Calendar(DateTime start, DateTime today, Dictionary<DateTime, Dictionary<string, TimeSpan>> byDay)
    {
        const double Cell = 11, Gap = 2;
        var panel = new StackPanel();
        panel.Children.Add(Heading($"LAST {CalendarWeeks} WEEKS"));

        double longest = byDay.Count > 0 ? byDay.Values.Max(d => d.Values.Sum(t => t.TotalMinutes)) : 0;
        var grid = new Canvas
        {
            Width = 26 + CalendarWeeks * (Cell + Gap),
            Height = 12 + 7 * (Cell + Gap),
            Margin = new Thickness(0, 2, 0, 0),
        };

        // Day names on the left - every other one, as a calendar does when
        // the rows are this small - and a month's name over its first week.
        foreach (var (name, row) in new[] { ("Mon", 0), ("Wed", 2), ("Fri", 4), ("Sun", 6) })
        {
            var label = new TextBlock { Text = name, Foreground = _theme.Header, FontSize = SmallFont - 2 };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, 12 + row * (Cell + Gap) - 2);
            grid.Children.Add(label);
        }

        for (int week = 0; week < CalendarWeeks; week++)
        {
            var monday = start.AddDays(7 * week);
            double x = 26 + week * (Cell + Gap);
            if (week == 0 || monday.Month != monday.AddDays(-7).Month)
            {
                var month = new TextBlock { Text = monday.ToString("MMM", CultureInfo.InvariantCulture), Foreground = _theme.Header, FontSize = SmallFont - 2 };
                Canvas.SetLeft(month, x);
                Canvas.SetTop(month, -2);
                grid.Children.Add(month);
            }

            for (int d = 0; d < 7; d++)
            {
                var day = monday.AddDays(d);
                if (day > today)
                    break;

                var games = byDay.GetValueOrDefault(day);
                double minutes = games?.Values.Sum(t => t.TotalMinutes) ?? 0;

                // At least a third of the accent for any play at all, so a
                // short session isn't lost beside a long weekend.
                Brush fill = minutes > 0 && longest > 0
                    ? _theme.AccentAt((byte)Math.Round(0x55 + (0xFF - 0x55) * Math.Min(1, minutes / longest)))
                    : _theme.Separator;

                var square = new Border
                {
                    Width = Cell, Height = Cell, CornerRadius = new CornerRadius(2.5), Background = fill,
                    BorderBrush = day == today ? _theme.NotesText : null,
                    BorderThickness = new Thickness(day == today ? 1 : 0),
                    ToolTip = DayTip(day, games),
                };
                Canvas.SetLeft(square, x);
                Canvas.SetTop(square, 12 + d * (Cell + Gap));
                grid.Children.Add(square);
            }
        }
        panel.Children.Add(grid);
        return panel;
    }

    /// <summary>"Sat 25 Oct · 1 h 40 min" and each game's share, most first.</summary>
    private static string DayTip(DateTime day, Dictionary<string, TimeSpan>? games)
    {
        string date = day.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        if (games is null || games.Count == 0)
            return $"{date} · nothing played";

        var total = games.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        return $"{date} · {SessionRecord.Describe(total)}\n" +
               string.Join("\n", games.OrderByDescending(g => g.Value).Select(g => $"{g.Key}  {SessionRecord.Describe(g.Value)}"));
    }

    /// <summary>A block's heading, as PER SESSION's and LAST SESSION's are.</summary>
    private TextBlock Heading(string text) => new()
    {
        Text = text,
        Foreground = _theme.Section,
        FontSize = Math.Max(8, SmallFont - 1),
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 0, 0, 3),
    };
}
