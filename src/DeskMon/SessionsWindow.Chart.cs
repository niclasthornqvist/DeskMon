using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Metrics;
using DeskMon.Notes;
using DeskMon.Ui;

namespace DeskMon;

// The session history's chart, in a file of its own: SessionsWindow.xaml.cs
// had grown past 3,000 lines, and the chart - drawing, its chips and empty
// state - is the largest part that stands on its own. Moved as it was.
public partial class SessionsWindow
{
    // --- Chart -----------------------------------------------------------------

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private ChartMeasure SelectedMeasure
        => ChartMeasures[Math.Clamp(ChartPicker.SelectedIndex, 0, ChartMeasures.Length - 1)];

    private void ChartPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.SessionsChart = SelectedMeasure.Key;
        _settings.Save();
        DrawChart();
    }

    /// <summary>Whether the last draw showed the empty chart's picture - see ChartEmptyState.</summary>
    private bool _chartEmptyShown;

    /// <summary>
    /// What the chart shows when no session recorded the picked reading: a
    /// small drawn chart with empty, dashed bars, a title and what to do
    /// about it - the same kind of picture the pictures window shows with no
    /// pictures, where one dim line of small text was easy to take for a
    /// broken chart. Drawn, not an image file, so it is sharp and in the
    /// theme's colours (by reference, so it follows a theme change).
    /// It never takes a click, so the chart's own tooltips stay reachable.
    /// </summary>
    private FrameworkElement ChartEmptyState(string reading, double width, double height, bool arriving)
    {
        var art = new Canvas { Width = 100, Height = 66, HorizontalAlignment = HorizontalAlignment.Center };

        System.Windows.Shapes.Rectangle Box(double left, double top, double w, double h, double radius,
                                            string? fill, string? stroke, double thickness = 1.2, bool dashed = false)
        {
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = h, RadiusX = radius, RadiusY = radius, StrokeThickness = thickness,
            };
            if (fill is not null) box.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fill);
            if (stroke is not null) box.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
            if (dashed) box.StrokeDashArray = new DoubleCollection { 2, 2 };
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
            art.Children.Add(box);
            return box;
        }

        // The card, its baseline, and three bars of different heights drawn
        // as dashed outlines: a chart whose bars are missing.
        Box(0, 0, 88, 58, 7, "Theme.Ov.0A", "Theme.Ov.2B");
        Box(12, 47, 64, 1.2, 0, "Theme.Ov.33", null, 0);
        Box(18, 31, 12, 15, 2, null, "Theme.Ov.4D", dashed: true);
        Box(36, 17, 12, 29, 2, null, "Theme.Ov.4D", dashed: true);
        Box(54, 24, 12, 22, 2, null, "Theme.Ov.4D", dashed: true);

        // A badge on the corner with a dash in it: "nothing here".
        Box(72, 40, 26, 26, 13, "Theme.Popup", "Theme.Accent.99", 1.5);
        var dash = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M80.5,53 H89.5"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Theme.Accent");
        art.Children.Add(dash);

        var stack = new StackPanel { Width = width, IsHitTestVisible = false };
        stack.Children.Add(art);
        stack.Children.Add(new TextBlock
        {
            Text = "Not recorded in these sessions",
            Foreground = _theme.Section,
            FontSize = UiTheme.PanelFontSize + 2,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 3),
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{reading} wasn't logged for any of them - pick another reading above.",
            Foreground = _theme.Label,
            FontSize = SmallFont,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 380,
        });

        // Centred in the chart by its own measured height, which depends on
        // the font and whether the line wraps.
        stack.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetTop(stack, Math.Max(0, (height - stack.DesiredSize.Height) / 2));

        if (arriving)
        {
            // The pictures window's fade and rise, shorter and with a shorter
            // wait: the chart is part of a window already on screen. Once,
            // and still after that, since this window can be open over a game.
            var shift = new TranslateTransform(0, 12);
            stack.RenderTransform = shift;
            stack.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(120);
            var duration = new Duration(TimeSpan.FromMilliseconds(400));
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            stack.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(12, 0, duration) { BeginTime = delay, EasingFunction = ease });
        }

        return stack;
    }

    /// <summary>
    /// One drawn bar, kept so a pick can restyle it without drawing the chart
    /// again - see DrawChartPicks. Its hover reads Normal and Rest from here,
    /// so a pick made since it was drawn is what the mouse leaving goes back to.
    /// </summary>
    private sealed class ChartBar
    {
        public required Border Bar, Halo, Hover;
        public required Brush Plain, Lit;
        public required double Value;
        public required bool IsHighest, IsLowest;
        public Brush Normal = Brushes.Transparent;
        public double Rest = 1;
    }

    /// <summary>
    /// Where the last draw put things, for <see cref="DrawChartPicks"/> to lay
    /// the picked sessions' marks over - null with no bars drawn.
    /// </summary>
    private sealed record ChartLayout(List<SessionRecord> Shown, ChartBar?[] Bars, double Width,
                                      double PlotLeft, double PlotTop, double PlotHeight, double Baseline,
                                      double Slot, double Top, double LabelSize, bool LabelAll,
                                      double IdSize, int IdEvery, double ZeroRoom,
                                      Canvas Spots, Canvas Labels, Canvas Ids);

    private ChartLayout? _chartLayout;

    // The summary moved here from above _chartEmptyShown, which it had been
    // left over when that field was added, describing a bool as the chart.
    /// <summary>
    /// The picked reading per session, as bars, oldest on the left - for "did
    /// that patch or driver change anything?". Bars rather than a line because
    /// sessions are separate evenings, not a continuous measurement, and a
    /// line would draw a trend through the days in between.
    ///
    /// One value axis starting at zero, so bar heights compare honestly, with
    /// faint gridlines at round numbers. Every bar has a tooltip, and its
    /// hover area is the full height of the chart rather than the bar alone,
    /// so a short bar is as easy to point at as a tall one.
    /// </summary>
    private void DrawChart()
    {
        Chart.Children.Clear();
        _chartLayout = null;

        // The picked session's timeline goes with every redraw of the chart -
        // a pick, a resize, a refresh - see SessionsWindow.Timeline.cs.
        ShowTimeline();

        // Shown whenever there are sessions, even if none recorded the picked
        // reading - hiding the picker along with the bars would leave no way
        // to pick something else.
        bool any = !_allGames && _sessions.Count > 0;
        ChartTitle.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Chart.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        // Its line goes with it: under All games, with no chart, it would
        // separate the cards from the table for no reason.
        ChartRule.Visibility = Chart.Visibility;

        double width = Chart.ActualWidth;
        double height = Chart.ActualHeight;
        if (!any || width <= 0 || height <= 0)
        {
            _chartEmptyShown = false;
            return;
        }

        var measure = SelectedMeasure;
        var shown = _sessions.Skip(Math.Max(0, _sessions.Count - ChartSessions)).ToList();

        // The bars that will be drawn: a session that didn't record the
        // reading, or recorded nothing, has none, so it can't be the lowest.
        var values = shown.Select(s => measure.Value(s)).OfType<double>().Where(v => v > 0).ToList();
        bool anyBar = values.Count > 0;

        // The key only for what there is a bar to explain. A lowest only when
        // it differs from the highest - with one bar, or every bar level, the
        // same bar would be both.
        double lowest = anyBar ? values.Min() : 0;
        bool markLowest = anyBar && lowest < values.Max();

        HighestChip.Visibility = anyBar ? Visibility.Visible : Visibility.Collapsed;
        LowestChip.Visibility = markLowest ? Visibility.Visible : Visibility.Collapsed;
        AverageChip.Visibility = HighestChip.Visibility;
        // The heading only over figures: with no bars the chart says why itself.
        ChartTitleText.Visibility = HighestChip.Visibility;

        // The figures beside their keys: the colour alone only said which bar
        // was highest, and finding its value meant hovering it or reading it
        // off the axis. Taken from the bars drawn, so they match what is seen.
        // The average outside the block: the chart draws its line too.
        double average = 0;
        if (anyBar)
        {
            // Average FPS weighted by session length, as the AVG FPS card is:
            // a plain mean let short menu sessions at 300+ fps pull it far
            // above the card's figure. The other readings are a plain mean -
            // weighting minutes played by minutes played would favour long ones.
            average = measure.Key == "avg" && WeightedFps(shown) is double weighted
                ? weighted
                : values.Average();
            SetChip(AverageText, "Average", BarValue(average));
            SetChip(HighestText, "Highest", BarValue(values.Max()));
            SetChip(LowestText, "Lowest", BarValue(lowest));
        }

        // Fades in only when it first appears: the chart is redrawn on every
        // resize and refresh, and arriving again each time would flicker.
        bool arriving = !anyBar && !_chartEmptyShown;
        _chartEmptyShown = !anyBar;

        if (!anyBar)
        {
            Chart.Children.Add(ChartEmptyState(measure.Name, width, height, arriving));
            return;
        }

        const double AxisWidth = 30;   // room for the value labels
        const double LabelRoom = 16;   // a bar's value label above the tallest bar
        const double Gap = 2;          // surface gap between bars

        double highest = shown.Max(s => measure.Value(s) ?? 0);
        double plotLeft = AxisWidth;
        double plotWidth = width - AxisWidth;
        double plotTop = LabelRoom;
        // Room under the baseline for the lower half of the "0" label, which is
        // centred on its gridline like the others. With only 1 px there, the
        // chart's clipping cut the 0 off at the bottom.
        const double ZeroRoom = 8;
        // Under that, each bar's session ID - the number in the table's ID
        // column - so a bar can be found in the table without hovering it.
        double idSize = Math.Max(8, SmallFont - 2);
        // Room for the gap a picked session's ID pill keeps under its
        // spotlight's edge too - see IdPillGap - or the canvas would clip it.
        double idRoom = idSize + 4 + IdPillGap;
        double plotHeight = height - LabelRoom - ZeroRoom - idRoom;
        double baseline = plotTop + plotHeight;

        // The top of the scale high enough that the tallest bar's value label
        // fits under the top gridline, not just the bar: rounding 57 up to 60
        // put the bar right under the line and its "57" across and above it.
        // The bar may reach at most the plot's height less one label's room.
        double fits = plotHeight > LabelRoom ? highest * plotHeight / (plotHeight - LabelRoom) : highest;
        double top = NiceCeiling(fits);

        // Gridlines at a quarter steps of a round top value; labels on the left
        // in the muted text colour, so the axis stays out of the bars' way.
        for (int step = 0; step <= 4; step++)
        {
            double value = top * step / 4;
            double y = Math.Round(baseline - plotHeight * step / 4) + 0.5;

            // Fully qualified: Shapes also has a Path, which would collide with System.IO's.
            Chart.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = plotLeft, X2 = width, Y1 = y, Y2 = y,
                Stroke = step == 0 ? _theme.Border : _theme.Separator,
                StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = value.ToString("0", CultureInfo.InvariantCulture),
                Foreground = _theme.Header,
                FontSize = SmallFont - 1,
                Width = AxisWidth - 6,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            Chart.Children.Add(label);
        }

        // Slots spread over the whole width, but never wider than an eighth of
        // it: a game with three sessions would otherwise draw three slabs.
        // Neon bars: thin, at most 11 px, so the glow around them is what fills
        // the slot - wide bars with a glow merged into one bright block.
        double slot = plotWidth / Math.Max(shown.Count, 8);
        double barWidth = Math.Min(NeonBarWidth, Math.Max(2, Math.Min(slot * 0.6, slot - Gap)));

        // The picked sessions' spotlights, behind the glow and bars - filled
        // by DrawChartPicks, so a pick redraws only them.
        var spots = new Canvas { IsHitTestVisible = false };
        Chart.Children.Add(spots);

        // The glow: a copy of every bar in one layer behind them, blurred as a
        // whole. One effect for the chart instead of one per bar - each WPF
        // effect renders its element off-screen, and forty of them is real GPU
        // work in a window that can sit over a game. A copy rather than a
        // DropShadowEffect so each glow keeps its own bar's colour.
        var glow = new Canvas
        {
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 10,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            },
        };
        Chart.Children.Add(glow);

        // Every bar's value above it when the slots are wide enough for the
        // widest of them - a few sessions, or a wide window. Squeezed, only the
        // highest and lowest keep theirs: forty numbers run together read as
        // none, and the rest are a hover away in the tooltip.
        double labelSize = Math.Max(8, SmallFont - 1);
        bool labelAll = values.Max(v => ValueWidth(BarValue(v), labelSize)) <= slot - 2;

        // Every ID when they fit side by side, else every second, fifth or
        // tenth - counted from the newest, so the latest session always has
        // its number. Run together they would read as one long number.
        // Measured with its "ID " - see below - so the spacing allows for it.
        double idWidth = ValueWidth("ID " + shown[^1].Id.ToString(CultureInfo.InvariantCulture), idSize) + 4;
        int idEvery = new[] { 1, 2, 5, 10 }.FirstOrDefault(n => n * slot >= idWidth, 10);

        var bars = new ChartBar?[shown.Count];
        var hovers = new List<Border>();
        for (int i = 0; i < shown.Count; i++)
        {
            var session = shown[i];
            double x = plotLeft + i * slot + (slot - barWidth) / 2;

            var hover = new Border
            {
                Width = slot,
                Height = plotHeight,
                Background = Brushes.Transparent,
            };
            string? headline = null;
            Brush? headlineBrush = null;
            LazyTip(hover, () => SessionTip(session, headline, headlineBrush));
            // A click on the bar picks its session as a click on its row does -
            // Ctrl for several - and scrolls the table to that row: finding the
            // bar first and then hunting for its ID in the table was the long way.
            // On the whole slot, so a session with no bar for this reading can
            // be picked too.
            if (!_allGames)
            {
                hover.Cursor = Cursors.Hand;
                hover.MouseLeftButtonUp += (_, _) =>
                {
                    Select(session);
                    if (_selected.Contains(session) && _sessionRows.TryGetValue(session, out var row))
                        row.BringIntoView();
                };
            }
            Canvas.SetLeft(hover, plotLeft + i * slot);
            Canvas.SetTop(hover, plotTop);

            if (measure.Value(session) is double value && value > 0)
            {
                double barHeight = Math.Max(2, plotHeight * Math.Min(1, value / top));

                // Every bar that ties for highest or lowest, not just the
                // first: picking one of two equal sessions would claim a
                // difference there isn't.
                bool isHighest = value >= highest;
                bool isLowest = markLowest && value <= lowest;
                var (plain, lit) = isHighest ? MarkedBar(HighestFoot, HighestTip, HighestBrush, HighestHover, barHeight, plotHeight)
                                 : isLowest ? MarkedBar(LowestFoot, LowestTip, LowestBrush, LowestHover, barHeight, plotHeight)
                                 : PlainBar(barHeight, plotHeight);

                // Rounded at both ends, a lit tube rather than a block standing
                // on the baseline - the neon look.
                var bar = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = plain,
                    CornerRadius = new CornerRadius(barWidth / 2),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, baseline - barHeight);
                Chart.Children.Add(bar);

                // Its glow: 2 px wider so the blur has more colour to spread
                // and still shows beside a 2 px bar on a squeezed chart.
                var halo = new Border
                {
                    Width = barWidth + 2,
                    Height = barHeight,
                    Background = plain,
                    CornerRadius = new CornerRadius(barWidth / 2 + 1),
                };
                Canvas.SetLeft(halo, x - 1);
                Canvas.SetTop(halo, baseline - barHeight);
                glow.Children.Add(halo);

                var drawn = new ChartBar
                {
                    Bar = bar, Halo = halo, Hover = hover, Plain = plain, Lit = lit,
                    Value = value, IsHighest = isHighest, IsLowest = isLowest, Normal = plain,
                };
                bars[i] = drawn;

                // Not "Highest {name}": the peak measures are already named
                // "Highest CPU °C", and it would say it twice. In the bar's
                // own colour, so the tooltip says which mark it is.
                if (isHighest)
                    (headline, headlineBrush) = ($"Highest in the chart: {value:0}", HighestBrush);
                else if (isLowest)
                    (headline, headlineBrush) = ($"Lowest in the chart: {value:0}", LowestBrush);

                // The glow lights up with its bar, or the hovered bar would
                // sit brighter than its own halo. A stepped-back bar comes up
                // to full under the mouse, or hovering one beside the pick
                // would barely change it. What it goes back to is the bar's
                // own, kept current by DrawChartPicks.
                hover.MouseEnter += (_, _) => { bar.Background = halo.Background = drawn.Lit; bar.Opacity = halo.Opacity = 1; };
                hover.MouseLeave += (_, _) => { bar.Background = halo.Background = drawn.Normal; bar.Opacity = halo.Opacity = drawn.Rest; };
            }

            // A new driver or Windows build: a small accent triangle under the
            // bar, in the room the 0 label leaves below the baseline - clear of
            // the value labels above. Whether or not the session has a bar, so
            // the change still shows on a reading it didn't record.
            if (_changes.ContainsKey(session))
            {
                double middle = plotLeft + i * slot + slot / 2;
                Chart.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new(middle - 3.5, baseline + 7), new(middle + 3.5, baseline + 7), new(middle, baseline + 2) },
                    Fill = _theme.Accent,
                    IsHitTestVisible = false,
                });
            }

            hovers.Add(hover);
        }

        // After the bars, so the line stays visible where it crosses them.
        // Dashed, so it reads as a level rather than as one more gridline.
        // Not hit-testable: the bars' tooltips under it must still work.
        double averageY = Math.Round(baseline - plotHeight * Math.Min(1, average / top)) + 0.5;
        Chart.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = plotLeft, X2 = width, Y1 = averageY, Y2 = averageY,
            Stroke = AverageBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            // Toned down: at full strength the bright green drew the eye
            // away from the bars, which are what the chart is about.
            Opacity = AverageOpacity,
        });

        // The bars' value labels in a layer of their own, added last so they
        // sit over the average line, with the window-coloured halo round them:
        // where the green dashes ran through a number it was hard to read.
        // One effect on the layer, not one per label, for the glow's reason.
        // Filled by DrawChartPicks, since which bars have a label depends on
        // the pick; the average's own label goes in here now and stays.
        var labels = new Canvas { IsHitTestVisible = false, Effect = _theme.TextHalo };

        // The line named at its right end, "avg 57", in its own green: a
        // dashed line with nothing on it was taken for a gridline or a target,
        // and its chip above the chart was easy not to connect with it. In the
        // halo layer, like the bars' values, so it reads where a bar is under
        // it; under the line when the line runs too close to the top for it.
        string averageText = "avg " + BarValue(average);
        double averageWidth = ValueWidth(averageText, labelSize);
        var averageLabel = new TextBlock
        {
            Text = averageText,
            FontSize = labelSize,
            Foreground = AverageBrush,
            IsHitTestVisible = false,
        };
        double above = averageY - labelSize - 5;
        Canvas.SetLeft(averageLabel, width - averageWidth - 2);
        Canvas.SetTop(averageLabel, above >= plotTop ? above : averageY + 2);
        var averageLayer = new Canvas { IsHitTestVisible = false, Effect = _theme.TextHalo };
        averageLayer.Children.Add(averageLabel);

        // The IDs under the bars, in the room below the baseline - also the
        // pick's, so filled by DrawChartPicks.
        var ids = new Canvas { IsHitTestVisible = false };
        Chart.Children.Add(ids);

        // The hover areas over everything drawn, so every slot takes the mouse.
        foreach (var hover in hovers)
            Chart.Children.Add(hover);

        Chart.Children.Add(averageLayer);
        Chart.Children.Add(labels);

        _chartLayout = new ChartLayout(shown, bars, width, plotLeft, plotTop, plotHeight, baseline,
                                       slot, top, labelSize, labelAll, idSize, idEvery, ZeroRoom,
                                       spots, labels, ids);
        DrawChartPicks();
    }

    /// <summary>
    /// What in the chart depends on which sessions are picked: their
    /// spotlights, the other bars stepped back, the value labels and the IDs
    /// under the bars. Apart from DrawChart so a pick redraws only this -
    /// building all forty bars, their blurred glow and the hover areas again on
    /// every click grew slow, in a window that can sit over a game.
    /// </summary>
    private void DrawChartPicks()
    {
        if (_chartLayout is not { } layout)
            return;

        var shown = layout.Shown;
        double slot = layout.Slot, plotLeft = layout.PlotLeft, baseline = layout.Baseline;
        layout.Spots.Children.Clear();
        layout.Labels.Children.Clear();
        layout.Ids.Children.Clear();

        // The sessions picked in the table: a faint accent column behind each
        // one's slot, so they can be found in the chart without counting IDs.
        // Drawn even without a bar, so a session that didn't record this
        // reading still shows where it would be.
        var picked = new HashSet<int>(_selected.Select(p => shown.FindIndex(s => ReferenceEquals(s, p))).Where(i => i >= 0));

        // A spotlight rather than a flat tint: clear at the top, strongest at
        // the baseline where the bar stands, with a thin accent edge on each
        // side. The flat 15% tint was lost behind the bars' own glow.
        if (picked.Count > 0)
        {
            var spot = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            spot.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, _theme.AccentColor.R, _theme.AccentColor.G, _theme.AccentColor.B), 0));
            spot.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, _theme.AccentColor.R, _theme.AccentColor.G, _theme.AccentColor.B), 1));
            spot.Freeze();
            var spotEdge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            spotEdge.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, _theme.AccentColor.R, _theme.AccentColor.G, _theme.AccentColor.B), 0));
            spotEdge.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, _theme.AccentColor.R, _theme.AccentColor.G, _theme.AccentColor.B), 1));
            spotEdge.Freeze();
            foreach (int i in picked)
            {
                var column = new Border
                {
                    Width = slot,
                    Height = layout.PlotHeight + layout.ZeroRoom,
                    Background = spot,
                    BorderBrush = spotEdge,
                    // Side edges only on a slot wide enough to show the tint between
                    // them. The foot always, as thin as the sides: the spotlight
                    // fades to its strongest there but had no edge, and on a
                    // squeezed chart its tint was hard to tell from the next
                    // slot's. The column ends where the ID's pill begins, so the
                    // line closes the spotlight right on top of it.
                    BorderThickness = new Thickness(slot >= 8 ? 1 : 0, 0, slot >= 8 ? 1 : 0, 1),
                    CornerRadius = new CornerRadius(Math.Min(4, slot / 2), Math.Min(4, slot / 2), 0, 0),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(column, plotLeft + i * slot);
                Canvas.SetTop(column, layout.PlotTop);
                layout.Spots.Children.Add(column);
            }
        }

        // The other bars stepped back while something is picked, so the picked
        // one is the brightest thing in the chart, not one lit bar of forty.
        const double UnpickedOpacity = 0.45;

        // With one picked its value and ID are always shown; with several,
        // forced labels on neighbouring bars would run into each other.
        int onlyPicked = picked.Count == 1 ? picked.First() : -1;

        for (int i = 0; i < shown.Count; i++)
        {
            if (layout.Bars[i] is { } drawn)
            {
                // The picked session's bar stays in its hover colour, so it
                // stands out of the column as well as being inside it.
                bool isPicked = picked.Contains(i);
                drawn.Normal = isPicked ? drawn.Lit : drawn.Plain;
                drawn.Rest = picked.Count > 0 && !isPicked ? UnpickedOpacity : 1;
                bool hovered = drawn.Hover.IsMouseOver;
                drawn.Bar.Background = drawn.Halo.Background = hovered ? drawn.Lit : drawn.Normal;
                drawn.Bar.Opacity = drawn.Halo.Opacity = hovered ? 1 : drawn.Rest;

                // The picked one always has its number, even squeezed: having
                // picked it, its value is what you came to the chart for.
                if (layout.LabelAll || drawn.IsHighest || drawn.IsLowest || i == onlyPicked)
                {
                    // In the bar's own colour when it is a marked one, so the
                    // number says which mark it is; dim otherwise.
                    string text = BarValue(drawn.Value);
                    double textWidth = ValueWidth(text, layout.LabelSize);
                    var label = new TextBlock
                    {
                        Text = text,
                        FontSize = layout.LabelSize,
                        Foreground = drawn.IsHighest ? HighestBrush : drawn.IsLowest ? LowestBrush : _theme.Label,
                        IsHitTestVisible = false,
                    };
                    // Centred over the bar, but kept inside the plot at the ends.
                    double barLeft = Canvas.GetLeft(drawn.Bar);
                    double left = Math.Clamp(barLeft + drawn.Bar.Width / 2 - textWidth / 2, plotLeft, layout.Width - textWidth);
                    Canvas.SetLeft(label, left);
                    Canvas.SetTop(label, baseline - drawn.Bar.Height - layout.LabelSize - 4);
                    layout.Labels.Children.Add(label);
                }
            }

            // Below the change triangle's row, whether or not there is a bar.
            // A picked session's ID in the accent. A lone pick's always, and a
            // neighbour's that would run into it gives way, or the two read as
            // one number.
            bool idPicked = picked.Contains(i);
            // The pill's own width: "ID " before the number makes it wider than
            // a plain ID, and a neighbour's number must keep clear of all of it.
            bool crowdsPicked = onlyPicked >= 0 && i != onlyPicked
                                && Math.Abs(i - onlyPicked) * slot < ValueWidth("ID " + shown[onlyPicked].Id, layout.IdSize) + IdPillPad * 2 + 4;
            if (i == onlyPicked || ((shown.Count - 1 - i) % layout.IdEvery == 0 && !crowdsPicked))
            {
                // "ID 12", as the table's column and DELETE name it, on every
                // bar: the number alone could be read as the bar's value. The
                // spacing above thins the row out to make room for the word.
                string id = "ID " + shown[i].Id.ToString(CultureInfo.InvariantCulture);
                double idTextWidth = ValueWidth(id, layout.IdSize);
                var idLabel = new TextBlock
                {
                    Text = id,
                    FontSize = layout.IdSize,
                    Foreground = _theme.Header,
                    IsHitTestVisible = false,
                };
                FrameworkElement idMark = idLabel;
                double markWidth = idTextWidth;

                // A picked session's ID on a filled accent pill, joined to the
                // bottom of its spotlight: accent text alone was a few pixels of
                // colour in a row of grey numbers. The window's colour on it, so
                // it reads on any accent.
                if (idPicked)
                {
                    idLabel.Foreground = new SolidColorBrush(_theme.WindowColor);
                    idLabel.FontWeight = FontWeights.SemiBold;
                    markWidth = idTextWidth + IdPillPad * 2;
                    idMark = new Border
                    {
                        Background = _theme.Accent,
                        CornerRadius = new CornerRadius(layout.IdSize),
                        Padding = new Thickness(IdPillPad, 0, IdPillPad, 0),
                        Child = idLabel,
                        IsHitTestVisible = false,
                    };
                }

                Canvas.SetLeft(idMark, Math.Clamp(plotLeft + i * slot + slot / 2 - markWidth / 2, plotLeft, layout.Width - markWidth));
                // The pill a little below the spotlight's bottom edge rather than
                // against it, where the line and the pill ran together.
                Canvas.SetTop(idMark, baseline + layout.ZeroRoom + (idPicked ? IdPillGap : 0));
                layout.Ids.Children.Add(idMark);
            }
        }
    }

    /// <summary>
    /// One of the average / highest / lowest chips over the chart: the
    /// window's own colour with a little of the chip's mixed in, mostly
    /// opaque so it reads over the game's art as well as over the panel, and
    /// an outline in the chip's colour. The figure in the theme's text colour
    /// rather than the chip's, which would be faint on a light theme.
    /// </summary>
    private void StyleChip(Border chip, TextBlock text, Brush colour)
    {
        // The accent brush may not be a plain colour in every theme; the
        // accent's own colour stands in for it then.
        Color c = colour is SolidColorBrush solid ? solid.Color : _theme.AccentColor, w = _theme.WindowColor;
        byte Mix(byte window, byte tint) => (byte)Math.Round(window * 0.82 + tint * 0.18);

        var fill = new SolidColorBrush(Color.FromArgb(0xD9, Mix(w.R, c.R), Mix(w.G, c.G), Mix(w.B, c.B)));
        fill.Freeze();
        var edge = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B));
        edge.Freeze();

        chip.Background = fill;
        chip.BorderBrush = edge;
        text.Foreground = _theme.NotesText;
        text.FontSize = UiTheme.PanelFontSize;
    }

    /// <summary>
    /// A chip's words: the caption small and dim, the figure larger and
    /// bold, so the number is what the eye lands on.
    /// </summary>
    private void SetChip(TextBlock text, string caption, string value)
    {
        text.Inlines.Clear();
        // No caption run at all for an icon-only chip - see ShowLastSession -
        // or its two spaces would pad the figure on the left.
        if (caption.Length > 0)
            text.Inlines.Add(new System.Windows.Documents.Run(caption + "  ")
        {
            Foreground = _theme.Label,
            FontSize = SmallFont,
        });
        text.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = FontWeights.SemiBold });
    }

    /// <summary>The average line's opacity, which its key in the legend shares so the two still match.</summary>
    private const double AverageOpacity = 0.55;

    /// <summary>Room each side of a picked session's ID inside its pill - see DrawChart.</summary>
    private const double IdPillPad = 4;

    /// <summary>Space between a picked session's spotlight edge and its ID pill under it - see DrawChart.</summary>
    private const double IdPillGap = 3;

    /// <summary>The chart's neon bars at their widest - see DrawChart.</summary>
    private const double NeonBarWidth = 11;

    /// <summary>
    /// An unmarked bar's colour and its hover: the blue, or with BarGradient on
    /// the settings' gradient rising from the baseline. The gradient spans the
    /// whole plot height rather than the bar, so like the widget's bars only a
    /// tall one reaches the end colour. The highest and lowest keep their
    /// orange and red, which is what makes them findable.
    /// </summary>
    private (Brush Normal, Brush Lit) PlainBar(double barHeight, double plotHeight)
    {
        if (!_settings.BarGradient)
            return (BarBrush, BarHover);

        return GradientBar(_theme.BarGradientStart, _theme.BarGradientEnd, barHeight, plotHeight);
    }

    /// <summary>
    /// The highest or lowest bar: its own gradient when the plain bars have
    /// one, laid out the same way so all bars agree, else its solid colour.
    /// </summary>
    private (Brush Normal, Brush Lit) MarkedBar(Color foot, Color tip, Brush solid, Brush solidLit,
                                                double barHeight, double plotHeight)
        => _settings.BarGradient ? GradientBar(foot, tip, barHeight, plotHeight) : (solid, solidLit);

    /// <summary>A bar's gradient from <paramref name="start"/> at its foot, and the lighter one for its hover.</summary>
    private (Brush Normal, Brush Lit) GradientBar(Color start, Color end, double barHeight, double plotHeight)
    {
        // In the bar's own pixels: its foot is at barHeight, the plot's top
        // plotHeight above that - or, with BarGradientSpan "bar", the bar's
        // own top, so every bar shows the whole gradient as the widget's do.
        var foot = new Point(0, barHeight);
        var top = new Point(0, _theme.GradientAcrossBar ? 0 : barHeight - plotHeight);
        return (UiTheme.MakeGradient(start, end, foot, top),
                UiTheme.MakeGradient(UiTheme.Lighten(start, 0.35), UiTheme.Lighten(end, 0.35), foot, top));
    }

    /// <summary>The target the scores on screen were worked out for, so a new one re-scores them.</summary>
    private double _scoredFor = PerformanceScore.Target;

    /// <summary>
    /// For a settings change: the chart reads its colours from the theme only
    /// while drawing. A new target frame rate changes every score - in the
    /// cards, the table and a chart of scores - so it shows the lot again;
    /// only then, since this runs on every keystroke in the settings window.
    /// </summary>
    public void RedrawChart()
    {
        if (!IsLoaded)
            return;

        if (_scoredFor != PerformanceScore.Target)
        {
            _scoredFor = PerformanceScore.Target;
            ShowSelection(GamePicker.SelectedItem, null);
        }
        else
            DrawChart();

        // A new energy price or currency: the Cost column comes, goes or
        // changes - only then, for the same every-keystroke reason.
        if (_energyPrice != _settings.EnergyPrice || _energyCurrency != (_settings.EnergyCurrency?.Trim() ?? string.Empty))
        {
            FitColumns();
            ShowRows();
        }
    }

    /// <summary>A bar's value as its label says it: whole, with a decimal only under 10 where it matters.</summary>
    private static string BarValue(double value)
        => value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);

    /// <summary>How wide <paramref name="text"/> is drawn in the window's font, for whether labels fit.</summary>
    private double ValueWidth(string text, double size)
        => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                             new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                             size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// A round number at or above <paramref name="value"/> that divides into
    /// four tidy gridlines: 60, 120, 240 rather than 217.
    /// </summary>
    private static double NiceCeiling(double value)
    {
        foreach (double nice in new double[] { 20, 40, 60, 80, 120, 160, 200, 240, 300, 400, 600, 800, 1000 })
            if (value <= nice)
                return nice;

        return Math.Ceiling(value / 200) * 200;
    }
}
