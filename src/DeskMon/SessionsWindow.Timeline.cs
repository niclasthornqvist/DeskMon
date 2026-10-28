using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskMon.Notes;

namespace DeskMon;

// The picked session over time - see SessionTimeline. The chart above shows
// every session as one bar; this shows one session minute by minute, so a
// low average can be traced to when it happened: the loading screen, one bad
// fight, or the CPU heating up late in the evening.
public partial class SessionsWindow
{
    /// <summary>The session whose timeline is loaded, and what was read for it - null when it has none.</summary>
    private SessionRecord? _timelineFor;
    private SessionTimeline? _timeline;
    private int _timelineTicket;

    /// <summary>Which lines are on besides the frame rate. Kept while the window is open, not saved.</summary>
    private bool _showCpuTemp = true, _showGpuTemp, _showPing;

    // Their own colours, apart from the chart's highest / lowest / average,
    // which mean something else there.
    private static readonly Brush CpuTempBrush = Frozen(Color.FromRgb(0xF4, 0x72, 0xB6));
    private static readonly Brush GpuTempBrush = Frozen(Color.FromRgb(0xA7, 0x8B, 0xFA));
    private static readonly Brush PingBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));

    /// <summary>What the hover line and its dot are drawn with, kept to move them without a redraw.</summary>
    private System.Windows.Shapes.Line? _timelineCursor;
    private System.Windows.Shapes.Ellipse? _timelineDot;

    /// <summary>The plot's place in the canvas and its scale, for the hover - set by DrawTimeline.</summary>
    private double _tlLeft, _tlWidth, _tlTop, _tlHeight, _tlFpsTop, _tlSeconds;

    private const double TimelineAxis = 30;     // as the chart's, so the two plots line up

    /// <summary>
    /// Shows the timeline of the one picked session, read on a worker the
    /// first time - a three-hour one is 1,100 lines - and kept while it stays
    /// picked. Called with every chart redraw, which a pick, a resize and a
    /// refresh all cause.
    /// </summary>
    private async void ShowTimeline()
    {
        // Shown for every game with sessions, picked or not: hidden until a
        // pick, nothing said it existed, and the table jumped down by its
        // height the moment one was made. Without one it says how to get one.
        if (_allGames || _sessions.Count == 0 || GamePicker.SelectedItem is not string game)
        {
            TimelinePanel.Visibility = Visibility.Collapsed;
            return;
        }

        TimelinePanel.Visibility = Visibility.Visible;
        if (_selected.Count != 1)
        {
            DrawTimeline();
            return;
        }
        var session = _selected[0];

        if (!ReferenceEquals(session, _timelineFor))
        {
            // What is drawn stays until the new one is read - a few ms - and is
            // then replaced in one go. Cleared here, every switch of session
            // showed an empty panel for a frame and then the picture again,
            // which flickered.
            int ticket = ++_timelineTicket;

            SessionTimeline? read;
            try
            {
                read = await System.Threading.Tasks.Task.Run(() => _history.Timeline(game, session.Start));
            }
            catch (Exception ex)
            {
                App.Log("reading a session timeline", ex);
                read = null;
            }

            // Another session picked meanwhile.
            if (ticket != _timelineTicket)
                return;

            _timelineFor = session;
            _timeline = read;
        }

        DrawTimeline();
    }

    private void TimelineChart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TimelinePanel.Visibility == Visibility.Visible)
            DrawTimeline();
    }

    private void DrawTimeline()
    {
        TimelineChart.Children.Clear();
        _timelineCursor = null;
        _timelineDot = null;
        BuildTimelineToggles();

        double width = TimelineChart.ActualWidth, height = TimelineChart.ActualHeight;

        // Nothing picked, or several: what the panel is for, in its place.
        if (_selected.Count != 1)
        {
            TimelineCaption.Text = string.Empty;
            DrawTimelinePlaceholder(
                _selected.Count == 0 ? "Select a session to see it minute by minute"
                                     : "Select just one session to see its timeline",
                "Click its bar in the chart or its row in the table - frame rate, temperatures and ping over the session",
                width, height, TimelineBadge.Pick);
            return;
        }

        var session = _timelineFor;
        var timeline = _timeline;

        // The picked session's own timeline still being read: a resize or a
        // chip meanwhile drew the one before, under that session's caption.
        if (!ReferenceEquals(session, _selected[0]))
        {
            TimelineCaption.Text = string.Empty;
            DrawTimelinePlaceholder("Loading the timeline…", string.Empty, width, height, TimelineBadge.Clock);
            return;
        }

        // Said rather than left blank: an empty panel would look broken.
        if (session is null || timeline is null || timeline.HighestFps is not double highest)
        {
            TimelineCaption.Text = string.Empty;
            DrawTimelinePlaceholder(
                session is { LoadingLeftOut: null } ? "No timeline: logged before DeskMon 1.73" : "No timeline for this session",
                "Sessions played from 1.73 on are recorded minute by minute",
                width, height, TimelineBadge.None);
            return;
        }
        TimelineCaption.Text = Summary(session);
        _timelinePlaceholder = null;   // a placeholder after this one arrives afresh
        TimelineToggles.Visibility = Visibility.Visible;
        if (width <= TimelineAxis + 20 || height <= 0)
            return;

        var points = timeline.Points;
        const double TimeRoom = 16;     // the clock times under the plot
        _tlLeft = TimelineAxis;
        _tlWidth = width - TimelineAxis;
        _tlTop = 6;
        _tlHeight = height - _tlTop - TimeRoom;
        _tlSeconds = Math.Max(SessionTimeline.Every.TotalSeconds, points[^1].At.TotalSeconds + SessionTimeline.Every.TotalSeconds);
        _tlFpsTop = NiceCeiling(highest * 1.08);
        double baseline = _tlTop + _tlHeight;

        double X(TimeSpan at) => _tlLeft + _tlWidth * at.TotalSeconds / _tlSeconds;
        double FpsY(double fps) => baseline - _tlHeight * Math.Min(1, fps / _tlFpsTop);
        // Temperatures on a scale of their own, 30 to 100 °C, labelled on the
        // right: on the frame rate's scale a 70 °C line meant nothing.
        double TempY(double c) => baseline - _tlHeight * Math.Clamp((c - 30) / 70, 0, 1);

        // The loading screen the figures leave out, shaded - see FrameStats.
        // Each run of loading points one block, labelled once.
        bool labelled = false;
        for (int i = 0; i < points.Count; i++)
        {
            if (!points[i].Loading)
                continue;
            int end = i;
            while (end + 1 < points.Count && points[end + 1].Loading)
                end++;

            double from = X(points[i].At), to = X(points[end].At + SessionTimeline.Every);
            var shade = new Border { Width = Math.Max(1, to - from), Height = _tlHeight, Background = _theme.Separator, Opacity = 0.6, IsHitTestVisible = false };
            Canvas.SetLeft(shade, from);
            Canvas.SetTop(shade, _tlTop);
            TimelineChart.Children.Add(shade);

            if (!labelled && to - from > 40)
            {
                var label = new TextBlock { Text = "loading", Foreground = _theme.Header, FontSize = SmallFont - 1, IsHitTestVisible = false };
                Canvas.SetLeft(label, from + 4);
                Canvas.SetTop(label, _tlTop + 2);
                TimelineChart.Children.Add(label);
                labelled = true;
            }
            i = end;
        }

        // Gridlines at nought, half and the top, as the chart has quarters.
        for (int step = 0; step <= 2; step++)
        {
            double value = _tlFpsTop * step / 2;
            double y = Math.Round(baseline - _tlHeight * step / 2) + 0.5;
            TimelineChart.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = _tlLeft, X2 = width, Y1 = y, Y2 = y,
                Stroke = step == 0 ? _theme.Border : _theme.Separator,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
            var label = new TextBlock
            {
                Text = value.ToString("0", CultureInfo.InvariantCulture),
                Foreground = _theme.Header, FontSize = SmallFont - 1,
                Width = TimelineAxis - 6, TextAlignment = TextAlignment.Right, IsHitTestVisible = false,
            };
            Canvas.SetTop(label, y - 7);
            TimelineChart.Children.Add(label);
        }

        // The target the score is worked against, dashed like the chart's
        // average - only when it falls inside the scale.
        double target = PerformanceScore.TargetFor(game: GamePicker.SelectedItem as string);
        if (target <= _tlFpsTop)
        {
            double y = Math.Round(FpsY(target)) + 0.5;
            TimelineChart.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = _tlLeft, X2 = width, Y1 = y, Y2 = y,
                Stroke = AverageBrush, StrokeThickness = 1, Opacity = AverageOpacity,
                StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
            });

            // Named at its right end, "target 60", for the chart's reason: a
            // bare dashed line reads as a gridline. Left of the °C labels the
            // temperature lines put in that corner. Above the line, or under
            // it when it runs along the top.
            var label = new TextBlock
            {
                Text = $"target {target:0}", Foreground = AverageBrush, FontSize = SmallFont - 1,
                Effect = _theme.TextHalo, IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double right = _showCpuTemp || _showGpuTemp ? 26 : 2;
            double above = y - label.DesiredSize.Height - 1;
            Canvas.SetLeft(label, width - right - label.DesiredSize.Width);
            Canvas.SetTop(label, above >= _tlTop ? above : y + 1);
            TimelineChart.Children.Add(label);
        }

        // The lines, broken where a point has no reading - tabbed out of the
        // game, or a sensor that didn't answer - rather than drawn across it.
        void Line(Func<SessionTimeline.Point, double?> value, Func<double, double> y, Brush brush, double thickness, bool fill)
        {
            var run = new PointCollection();
            void Flush()
            {
                if (run.Count > 1)
                {
                    if (fill)
                    {
                        // A soft fill under the frame rate, so the line reads as
                        // an amount and not just a trace.
                        var area = new PointCollection(run) { new Point(run[^1].X, baseline), new Point(run[0].X, baseline) };
                        var glow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, _theme.AccentColor.R, _theme.AccentColor.G, _theme.AccentColor.B), 0));
                        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, _theme.AccentColor.R, _theme.AccentColor.G, _theme.AccentColor.B), 1));
                        glow.Freeze();
                        TimelineChart.Children.Add(new System.Windows.Shapes.Polygon { Points = area, Fill = glow, IsHitTestVisible = false });
                    }
                    TimelineChart.Children.Add(new System.Windows.Shapes.Polyline
                    {
                        Points = run, Stroke = brush, StrokeThickness = thickness,
                        StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
                    });
                }
                run = new PointCollection();
            }

            foreach (var p in points)
            {
                if (value(p) is double v)
                    run.Add(new Point(X(p.At + SessionTimeline.Every / 2), y(v)));
                else
                    Flush();
            }
            Flush();
        }

        if (_showCpuTemp)
            Line(p => p.CpuTemp, TempY, CpuTempBrush, 1.2, fill: false);
        if (_showGpuTemp)
            Line(p => p.GpuTemp, TempY, GpuTempBrush, 1.2, fill: false);

        // Ping on a scale of its own from nought to its highest, so a laggy
        // stretch stands out whatever the usual ping is. No labels: its
        // figures are in the hover, and a third axis would crowd the plot.
        if (_showPing && points.Select(p => p.Ping).OfType<double>().DefaultIfEmpty().Max() is double worstPing && worstPing > 0)
        {
            double pingTop = NiceCeiling(worstPing * 1.1);
            Line(p => p.Ping, ms => baseline - _tlHeight * Math.Min(1, ms / pingTop), PingBrush, 1.1, fill: false);
        }
        Line(p => p.Fps, FpsY, _theme.Accent, 1.8, fill: true);

        // The °C scale's ends on the right, only with a temperature showing.
        if (_showCpuTemp || _showGpuTemp)
            foreach (var (c, y) in new[] { (100.0, _tlTop), (30.0, baseline) })
            {
                var label = new TextBlock { Text = $"{c:0}°", Foreground = _theme.Header, FontSize = SmallFont - 2, IsHitTestVisible = false };
                Canvas.SetLeft(label, width - 22);
                Canvas.SetTop(label, y - (c == 100 ? 0 : 12));
                TimelineChart.Children.Add(label);
            }

        // Clock times under the plot at a round step, so "when" reads as the
        // evening's own times: every 10, 15, 30 or 60 minutes, or every two hours.
        double minutes = _tlSeconds / 60;
        int every = new[] { 5, 10, 15, 30, 60, 120 }.FirstOrDefault(m => minutes / m <= Math.Max(2, _tlWidth / 70), 120);
        // From the timeline's own start, to the second - the log's is only to
        // the minute, and the points count from the real one (see Load).
        var start = timeline.Start;
        var first = start.AddMinutes(every - (start.Minute % every)).AddSeconds(-start.Second).AddMilliseconds(-start.Millisecond);
        for (var at = first; (at - start).TotalSeconds < _tlSeconds; at = at.AddMinutes(every))
        {
            double x = X(at - start);
            var tick = new TextBlock { Text = at.ToString("HH:mm", CultureInfo.InvariantCulture), Foreground = _theme.Header, FontSize = SmallFont - 1, IsHitTestVisible = false };
            Canvas.SetLeft(tick, Math.Clamp(x - 14, _tlLeft, width - 30));
            Canvas.SetTop(tick, baseline + 2);
            TimelineChart.Children.Add(tick);
        }

        // The hover line and dot, hidden until the mouse is over the plot.
        _timelineCursor = new System.Windows.Shapes.Line
        {
            Y1 = _tlTop, Y2 = baseline, Stroke = _theme.Label, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 }, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        _timelineDot = new System.Windows.Shapes.Ellipse
        {
            Width = 7, Height = 7, Fill = _theme.Accent, Stroke = new SolidColorBrush(_theme.WindowColor), StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        TimelineChart.Children.Add(_timelineCursor);
        TimelineChart.Children.Add(_timelineDot);
    }

    /// <summary>The small mark in the corner of the empty timeline's picture - see DrawTimelinePlaceholder.</summary>
    private enum TimelineBadge { Pick, Clock, None }

    /// <summary>
    /// The placeholder last drawn, so a redraw of the same one - a resize, a
    /// chart refresh - doesn't fade it in again. Null once a real timeline is up.
    /// </summary>
    private string? _timelinePlaceholder;

    /// <summary>
    /// The panel with no timeline to draw: a faint dashed outline where the
    /// plot goes, with a small drawn timeline beside two lines of text - what
    /// to do, then what it shows. The same kind of picture the pictures window
    /// shows with no pictures and the chart with no bars, where text alone
    /// read as a panel that had failed to draw. Side by side rather than
    /// stacked: the plot is only 120 high. At the plot's full height, so the
    /// table under it stays where it is when a session is picked.
    /// </summary>
    /// <param name="badge">What the picture's corner says: pick one, wait, or there is none.</param>
    private void DrawTimelinePlaceholder(string title, string detail, double width, double height, TimelineBadge badge)
    {
        // The line switches mean nothing with no lines to switch.
        TimelineToggles.Visibility = Visibility.Collapsed;
        if (width <= 0 || height <= 0)
            return;

        var outline = new System.Windows.Shapes.Rectangle
        {
            Width = width - 1, Height = height - 1, RadiusX = 6, RadiusY = 6,
            Stroke = _theme.Separator, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, 0.5);
        Canvas.SetTop(outline, 0.5);
        TimelineChart.Children.Add(outline);

        // The picture only where it leaves the text room to be read: in a
        // narrow window it would squeeze the words into a column.
        bool withArt = width >= 300;
        var row = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        if (withArt)
            row.Children.Add(TimelineArt(badge));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(120, width - (withArt ? 150 : 24)) };
        text.Children.Add(new TextBlock
        {
            Text = title, Foreground = _theme.Section, FontWeight = FontWeights.SemiBold,
            FontSize = UiTheme.PanelFontSize + 1, TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = withArt ? TextAlignment.Left : TextAlignment.Center,
        });
        if (detail.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = detail, Foreground = _theme.Label, FontSize = SmallFont, Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
                TextAlignment = withArt ? TextAlignment.Left : TextAlignment.Center,
            });
        row.Children.Add(text);

        // Centred in the plot by its own measured size, which depends on the
        // font and on whether the detail wraps.
        row.Measure(new Size(width, height));
        Canvas.SetLeft(row, Math.Max(0, (width - row.DesiredSize.Width) / 2));
        Canvas.SetTop(row, Math.Max(0, (height - row.DesiredSize.Height) / 2));
        TimelineChart.Children.Add(row);

        // Fades and rises in when it first appears, as the chart's empty state
        // does - not on every resize or refresh drawing the same one again,
        // which would flicker, and not for "Loading…", which is gone in a moment.
        // "Loading…" doesn't count as a new placeholder either: in between two
        // sessions without a timeline it would make the second "No timeline"
        // look new, and fade in from nothing on every switch.
        if (badge == TimelineBadge.Clock)
            return;
        bool arriving = title != _timelinePlaceholder;
        _timelinePlaceholder = title;
        if (!arriving)
            return;

        var shift = new TranslateTransform(0, 10);
        row.RenderTransform = shift;
        row.Opacity = 0;
        var delay = TimeSpan.FromMilliseconds(120);
        var duration = new Duration(TimeSpan.FromMilliseconds(400));
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        row.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(10, 0, duration) { BeginTime = delay, EasingFunction = ease });
    }

    /// <summary>
    /// A small timeline with nothing recorded on it: a card, its baseline, a
    /// shaded loading stretch, a dashed target line and a dashed frame-rate
    /// curve - the line that would be there - with a badge on the corner.
    /// Drawn rather than an image file, so it is sharp and in the theme's
    /// colours, by reference so it follows a theme change.
    /// </summary>
    private FrameworkElement TimelineArt(TimelineBadge badge)
    {
        var art = new Canvas { Width = 100, Height = 70, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };

        T Add<T>(T shape, double left, double top, string? fill, string? stroke) where T : System.Windows.Shapes.Shape
        {
            if (fill is not null) shape.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fill);
            if (stroke is not null) shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            art.Children.Add(shape);
            return shape;
        }

        System.Windows.Shapes.Path Line(string data, string stroke, double thickness, bool dashed = false)
        {
            var path = Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data), StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            }, 0, 0, null, stroke);
            if (dashed)
                path.StrokeDashArray = new DoubleCollection { 2, 2 };
            return path;
        }

        // The card, the loading stretch shaded at its start, and the baseline.
        Add(new System.Windows.Shapes.Rectangle { Width = 88, Height = 60, RadiusX = 7, RadiusY = 7, StrokeThickness = 1.2 }, 0, 0, "Theme.Ov.0A", "Theme.Ov.2B");
        Add(new System.Windows.Shapes.Rectangle { Width = 14, Height = 36, RadiusX = 2, RadiusY = 2 }, 10, 12, "Theme.Ov.0F", null);
        Add(new System.Windows.Shapes.Rectangle { Width = 68, Height = 1.2 }, 10, 48, "Theme.Ov.33", null);

        // The target, dashed and faint, and the frame rate that isn't there.
        Line("M10,18 H78", "Theme.Ov.2B", 1, dashed: true);
        Line("M10,40 L18,34 L26,37 L35,24 L44,29 L53,19 L62,25 L70,21 L78,23", "Theme.Accent.99", 1.6, dashed: true);

        // The badge on the corner, as the pictures window and the chart have.
        Add(new System.Windows.Shapes.Ellipse { Width = 28, Height = 28, StrokeThickness = 1.5 }, 66, 40, "Theme.Popup", "Theme.Accent.99");
        switch (badge)
        {
            case TimelineBadge.Pick:
                // A pointer: pick a session.
                Line("M76.5,46.5 V60 L79.8,56.8 L82,61.5 L84.3,60.5 L82.2,55.9 H86.8 Z", "Theme.Accent", 1.4);
                break;
            case TimelineBadge.Clock:
                // A clock: being read.
                Add(new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, StrokeThickness = 1.4 }, 73.5, 47.5, null, "Theme.Accent");
                Line("M80,50.5 V54 L82.5,55.5", "Theme.Accent", 1.4);
                break;
            default:
                // A dash: nothing recorded.
                Line("M75.5,54 H84.5", "Theme.Accent", 1.8);
                break;
        }

        return art;
    }
    /// <summary>What the panel says while the mouse isn't on it: length, and how much loading was left out.</summary>
    private static string Summary(SessionRecord session)
    {
        string text = $"{session.Start:HH:mm}–{session.End:HH:mm}  ·  {SessionRecord.Describe(session.Length)}";
        if (session.LoadingLeftOut is TimeSpan loading && loading > TimeSpan.Zero)
            text += $"  ·  {Ui.SessionTip.Every(loading, zero: true)} loading left out";
        return text + "  ·  point at the line for a moment's figures";
    }

    /// <summary>The chips that switch the temperature lines - a dot in the line's colour, then its name.</summary>
    private void BuildTimelineToggles()
    {
        TimelineToggles.Children.Clear();

        void Toggle(string name, Brush brush, bool on, Action flip)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = on ? brush : Brushes.Transparent, BorderBrush = brush, BorderThickness = new Thickness(1.2),
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock { Text = name, Foreground = on ? _theme.NotesText : _theme.Label, FontSize = SmallFont });

            var chip = new Border
            {
                Child = row, Padding = new Thickness(8, 2, 9, 2), Margin = new Thickness(6, 0, 0, 0),
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                BorderBrush = on ? brush : _theme.Separator, Background = Brushes.Transparent,
                Cursor = Cursors.Hand, ToolTip = on ? $"Hide the {name} line" : $"Show the {name} line",
            };
            chip.MouseLeftButtonUp += (_, _) => { flip(); DrawTimeline(); };
            TimelineToggles.Children.Add(chip);
        }

        Toggle("CPU °C", CpuTempBrush, _showCpuTemp, () => _showCpuTemp = !_showCpuTemp);
        Toggle("GPU °C", GpuTempBrush, _showGpuTemp, () => _showGpuTemp = !_showGpuTemp);
        // Only with a ping to draw - no host set, or a timeline from before.
        if (_timeline?.Points.Any(p => p.Ping is not null) == true)
            Toggle("Ping", PingBrush, _showPing, () => _showPing = !_showPing);
    }

    /// <summary>
    /// The figures at the moment under the mouse, in the caption, with a line
    /// and a dot on the frame rate there - read off without hovering a tooltip
    /// that would hide the line it describes.
    /// </summary>
    private void TimelineChart_MouseMove(object sender, MouseEventArgs e)
    {
        if (_timeline is not { } timeline || _timelineFor is not { } session || _timelineCursor is null || _timelineDot is null
            || timeline.Points.Count == 0)
            return;

        double x = e.GetPosition(TimelineChart).X;
        if (x < _tlLeft || _tlWidth <= 0)
        {
            TimelineChart_MouseLeave(sender, e);
            return;
        }

        double seconds = (x - _tlLeft) / _tlWidth * _tlSeconds;
        var point = NearestPoint(timeline.Points, seconds);
        double px = _tlLeft + _tlWidth * (point.At.TotalSeconds + SessionTimeline.Every.TotalSeconds / 2) / _tlSeconds;

        _timelineCursor.X1 = _timelineCursor.X2 = Math.Round(px) + 0.5;
        _timelineCursor.Visibility = Visibility.Visible;
        if (point.Fps is double fps)
        {
            Canvas.SetLeft(_timelineDot, px - 3.5);
            Canvas.SetTop(_timelineDot, _tlTop + _tlHeight - _tlHeight * Math.Min(1, fps / _tlFpsTop) - 3.5);
            _timelineDot.Visibility = Visibility.Visible;
        }
        else
            _timelineDot.Visibility = Visibility.Collapsed;

        var parts = new List<string> { (timeline.Start + point.At).ToString("HH:mm", CultureInfo.InvariantCulture) };
        parts.Add(point.Fps is double f ? $"{f:0} fps" : "no frame rate");
        if (point.Low is double low)
            parts.Add($"1% low {low:0}");
        if (point.CpuTemp is double cpu)
            parts.Add($"CPU {cpu:0} °C");
        if (point.GpuTemp is double gpu)
            parts.Add($"GPU {gpu:0} °C");
        if (point.Ping is double ms)
            parts.Add($"ping {ms:0} ms");
        if (point.Limit is Bottleneck.Kind limit)
            parts.Add(limit switch
            {
                Bottleneck.Kind.Target => "at target",
                Bottleneck.Kind.Other => "other limit",
                _ => $"{Bottleneck.Name(limit)}-limited",
            });
        if (point.Loading)
            parts.Add("loading, not counted");
        TimelineCaption.Text = string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The point whose middle is closest to <paramref name="seconds"/>. Points
    /// are a fixed step apart, so its index is worked out and only it and its
    /// neighbours compared - rather than all 1,100 of a three-hour session on
    /// every mouse move. Neighbours too, as a gap (DeskMon paused) can shift them.
    /// </summary>
    private static SessionTimeline.Point NearestPoint(IReadOnlyList<SessionTimeline.Point> points, double seconds)
    {
        double half = SessionTimeline.Every.TotalSeconds / 2;
        double Distance(SessionTimeline.Point p) => Math.Abs(p.At.TotalSeconds + half - seconds);

        // Ordered by time, so a binary search finds the first point after the mouse.
        int low = 0, high = points.Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (points[middle].At.TotalSeconds + half < seconds)
                low = middle + 1;
            else
                high = middle;
        }

        var best = points[low];
        if (low > 0 && Distance(points[low - 1]) < Distance(best))
            best = points[low - 1];
        return best;
    }

    private void TimelineChart_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_timelineCursor is not null)
            _timelineCursor.Visibility = Visibility.Collapsed;
        if (_timelineDot is not null)
            _timelineDot.Visibility = Visibility.Collapsed;
        if (_timelineFor is { } session && _timeline is not null)
            TimelineCaption.Text = Summary(session);
    }
}
