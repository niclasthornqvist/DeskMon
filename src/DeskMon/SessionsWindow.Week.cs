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
// game, and a calendar of three months - slid back as far as a year - shaded by time played. The
// totals above answer "how much, ever"; this answers "how much lately", which
// they can't. From DeskMon's own sessions only - Steam's and GOG's playtime
// is a total with no days in it.
public partial class SessionsWindow
{
    /// <summary>One session's span, for the report - read with All games' totals, see ShowSelection.</summary>
    private sealed record PlaySpan(string Game, DateTime Start, DateTime End);

    private IReadOnlyList<PlaySpan> _played = Array.Empty<PlaySpan>();

    /// <summary>How many calendar months the calendar shows at a time, the last one included.</summary>
    private const int CalendarMonths = 3;

    /// <summary>How far back the calendar's slider reaches, in months, this one included: a year.</summary>
    private const int CalendarReachMonths = 12;

    /// <summary>
    /// How many months back the calendar is slid - 0 for the months up to
    /// now. Kept for the window's life, so a refresh doesn't throw you back
    /// to now; not saved, since opening the window is a fresh look.
    /// </summary>
    private int _calendarMonthsBack;

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

        // The whole year the slider can reach, counted once here, so sliding
        // only redraws the squares rather than adding the sessions up again.
        var reachStart = new DateTime(today.Year, today.Month, 1).AddMonths(-(CalendarReachMonths - 1));
        var calendarStart = reachStart.AddDays(-(((int)reachStart.DayOfWeek + 6) % 7));

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

        var calendar = Calendar(today, byDay);
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
                Margin = new Thickness(0, 2, 10, 2), ToolTip = $"{game}\nThe bright bar is this week's play, the faint one last week's, on one scale.", VerticalAlignment = VerticalAlignment.Center,
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

    // Squares of 18 px: at 11 the grid was a small patch in a wide window,
    // too fine to make out one day from the next or aim the mouse at, and the
    // day of the month has to fit inside. The weekday row and month column
    // (Top, Left) sized to the labels.
    private const double CalendarCell = 18, CalendarGap = 3, CalendarTop = 16, CalendarLeft = 32;

    /// <summary>
    /// Three months laid out like a wall calendar, with a slider under it that
    /// goes back a month at a time, as far as a year. Three months rather
    /// than the twelve weeks it was: whole months read as "August", where
    /// twelve weeks started and ended mid-month; and the slider reaches what
    /// twelve weeks never showed - "was I playing this much in spring?".
    /// </summary>
    private StackPanel Calendar(DateTime today, Dictionary<DateTime, Dictionary<string, TimeSpan>> byDay)
    {
        var panel = new StackPanel();
        var heading = Heading(string.Empty);
        panel.Children.Add(heading);

        // One scale over the whole reachable year, so a day's shade means the
        // same however far back the calendar is slid.
        double longest = byDay.Count > 0 ? byDay.Values.Max(d => d.Values.Sum(t => t.TotalMinutes)) : 0;

        var host = new Border();
        panel.Children.Add(host);

        // Far right is now, far left a year back - time running left to right
        // as on the chart. Whole months only, and snapped, so it lands on one.
        int furthest = CalendarReachMonths - CalendarMonths;
        var slider = new Slider
        {
            Style = (Style)FindResource("DarkSlider"),
            Width = CalendarLeft + 7 * (CalendarCell + CalendarGap) - CalendarGap,
            Minimum = 0,
            Maximum = furthest,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 3,
            Value = furthest - Math.Clamp(_calendarMonthsBack, 0, furthest),
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Months shown\nDrag left to go back, a month at a time, as far as a year.",
        };
        panel.Children.Add(slider);

        void Draw()
        {
            _calendarMonthsBack = furthest - (int)Math.Round(slider.Value);
            var last = new DateTime(today.Year, today.Month, 1).AddMonths(-_calendarMonthsBack);
            var first = last.AddMonths(-(CalendarMonths - 1));

            heading.Text = _calendarMonthsBack == 0
                ? $"LAST {CalendarMonths} MONTHS"
                : first.Year == last.Year
                // Invariant, like every other date in this window: as plain
                // interpolation these came out in the PC's own language.
                ? string.Create(CultureInfo.InvariantCulture, $"{first:MMM} – {last:MMM yyyy}").ToUpperInvariant()
                : string.Create(CultureInfo.InvariantCulture, $"{first:MMM yyyy} – {last:MMM yyyy}").ToUpperInvariant();
            host.Child = CalendarGrid(first, last.AddMonths(1), today, byDay, longest);
        }

        slider.ValueChanged += (_, _) => Draw();
        Draw();
        return panel;
    }

    /// <summary>
    /// The days from <paramref name="from"/> up to <paramref name="until"/>
    /// laid out like a wall calendar: a row a week, the oldest at the top,
    /// Monday to Sunday from left to right, so the dates read 1, 2, 3 along a
    /// row - a column per week had them running down and across, which took
    /// counting to follow. Each day shaded by how long was played, with the
    /// games in its tooltip; today outlined, so "where am I" needs no counting.
    /// </summary>
    private Canvas CalendarGrid(DateTime from, DateTime until, DateTime today,
                                Dictionary<DateTime, Dictionary<string, TimeSpan>> byDay, double longest)
    {
        const double Cell = CalendarCell, Gap = CalendarGap, Top = CalendarTop, Left = CalendarLeft;
        double labelSize = SmallFont - 1;

        var start = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        int weeks = (int)Math.Ceiling((until - start).TotalDays / 7);

        // Always as tall as three months can need, so sliding to a month that
        // takes a row less doesn't make everything under it jump.
        const int MostWeeks = 14;
        var grid = new Canvas
        {
            Width = Left + 7 * (Cell + Gap),
            Height = Top + MostWeeks * (Cell + Gap),
            Margin = new Thickness(0, 2, 0, 0),
        };

        // The weekdays across the top, each centred over its column - all
        // seven, in two letters, which fit now that a column is a whole square.
        string[] weekdays = { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
        for (int d = 0; d < 7; d++)
        {
            var label = new TextBlock
            {
                Text = weekdays[d], Foreground = _theme.Header, FontSize = labelSize,
                Width = Cell, TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(label, Left + d * (Cell + Gap));
            Canvas.SetTop(label, -2);
            grid.Children.Add(label);
        }

        for (int week = 0; week < weeks; week++)
        {
            var monday = start.AddDays(7 * week);
            double y = Top + week * (Cell + Gap);

            // The month's name beside the week its 1st falls in, centred on
            // the row. The first row always holds the first month's 1st.
            var first = Enumerable.Range(0, 7).Select(d => monday.AddDays(d))
                                  .FirstOrDefault(d => d.Day == 1 && d >= from && d < until);
            if (first != default)
            {
                var month = new TextBlock
                {
                    Text = first.ToString("MMM", CultureInfo.InvariantCulture),
                    Foreground = _theme.Header, FontSize = labelSize,
                };
                Canvas.SetLeft(month, 0);
                Canvas.SetTop(month, y + (Cell - labelSize * 1.3) / 2);
                grid.Children.Add(month);
            }

            for (int d = 0; d < 7; d++)
            {
                var day = monday.AddDays(d);
                if (day > today || day >= until)
                    break;

                // The days of the month before, in the first row, left blank:
                // they belong to the next slide back, and drawn here they
                // made "1" look like the middle of a week of play.
                if (day < from)
                    continue;

                var games = byDay.GetValueOrDefault(day);
                double minutes = games?.Values.Sum(t => t.TotalMinutes) ?? 0;

                // At least a third of the accent for any play at all, so a
                // short session isn't lost beside a long weekend.
                Brush fill = minutes > 0 && longest > 0
                    ? _theme.AccentAt((byte)Math.Round(0x55 + (0xFF - 0x55) * Math.Min(1, minutes / longest)))
                    : _theme.Separator;

                var square = new Border
                {
                    Width = Cell, Height = Cell, CornerRadius = new CornerRadius(3.5), Background = fill,
                    BorderBrush = day == today ? _theme.NotesText : null,
                    BorderThickness = new Thickness(day == today ? 1 : 0),
                    ToolTip = DayTip(day, games),

                    // The day of the month inside it, so a day can be found
                    // by its date rather than by counting from the month's
                    // name. Bright on a played day, where the fill is the
                    // accent; dim on an empty one, so the played days still
                    // stand out first. Not hit-tested, so the tooltip comes
                    // from the square under it.
                    Child = new TextBlock
                    {
                        Text = day.Day.ToString(CultureInfo.InvariantCulture),
                        FontSize = 9,
                        Foreground = minutes > 0 ? _theme.NotesText : _theme.Header,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false,
                    },
                };
                Canvas.SetLeft(square, Left + d * (Cell + Gap));
                Canvas.SetTop(square, y);
                grid.Children.Add(square);
            }
        }
        return grid;
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
