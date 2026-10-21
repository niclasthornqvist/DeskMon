using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Metrics;

namespace DeskMon.Ui;

/// <summary>
/// The frame rate in large figures, in the top-right corner of the monitor the
/// game is on - for when the widget's FPS row is too small to read at a glance
/// in the middle of a fight.
///
/// It is the opposite of the other windows in every way that matters over a
/// game: it can never be clicked, never take focus and never appear in Alt+Tab.
/// Clicks pass straight through it to the game underneath, so it can sit over
/// the game's own HUD without getting in the way of it.
///
/// No panel behind the figures, just the figures - so the game shows through
/// everywhere but the digits. A thin dark outline keeps them readable over a
/// bright sky as well as a dark corridor.
///
/// Placed in device pixels against the monitor rather than in WPF units against
/// the work area: the corner wanted is the game's, and a fullscreen game covers
/// the taskbar, so the work area would leave a gap the size of it.
/// </summary>
public sealed class FpsOverlayWindow : Window
{
    /// <summary>
    /// Gap between the digits and the screen's top and right edges, in WPF
    /// units, so it is scaled like everything else. Measured from the digits
    /// themselves, not from a text box around them - see <see cref="Figure"/>.
    /// </summary>
    private const double CornerGap = 16;

    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly TopmostGuard _topmost;

    /// <summary>
    /// The FPS row's own thresholds, so the figure turns amber and red at the
    /// same rates as the row on the widget. Null if the row was taken out of
    /// MetricRegistry - the figure then just stays green.
    /// </summary>
    private readonly Metric? _fpsMetric = FindFpsMetric();

    private readonly Figure _figure = new();

    private IntPtr _handle;

    /// <summary>
    /// The part of the screen the game is drawn on, in device pixels - what
    /// the overlay sits in the top-right corner of. Empty until it is placed.
    /// </summary>
    private RECT _target;

    private double? _fps;

    /// <summary>What was last drawn, so an unchanged reading redraws nothing.</summary>
    private string? _drawnText;
    private Brush? _drawnBrush;

    private bool _allowClose;

    public FpsOverlayWindow(AppSettings settings, UiTheme theme)
    {
        _settings = settings;
        _theme = theme;

        Title = "DeskMon FPS";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;   // always: it is no use behind the game
        UseLayoutRounding = true;

        // Off screen until it is anchored, so the first show doesn't flash it
        // up wherever Windows would have put a new window.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        Content = _figure;

        _topmost = new TopmostGuard(this);

        // Kept to the corner as it grows and shrinks: SizeToContent keeps the
        // left edge where it was, so 99 turning into 100 would otherwise push
        // the figure off the right of the screen.
        //
        // Twice: now, by the new layout size, and again once the window has
        // really been resized. SizeChanged arrives before Windows has resized
        // the window, so the window's own rectangle still has the old width
        // at that point - anchoring by it alone put the right edge of a wider
        // figure, the "FPS", off the screen.
        SizeChanged += (_, _) =>
        {
            Anchor();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Anchor));
        };

        // The outlines are built for one DPI; another monitor needs them again.
        DpiChanged += (_, _) => Redraw();

        ApplyStyle();
    }

    /// <summary>
    /// Hidden rather than closed, like the other windows: a closed window can
    /// never be shown again, and the next refresh would throw. A WM_CLOSE can
    /// reach it from outside - Build.ps1 asks the first visible DeskMon window
    /// it finds to close, and during a game this one is on top.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;

        // Click-through, never activated, kept out of Alt+Tab. These are also
        // the styles ForegroundState reads as "an overlay, not a game", so the
        // overlay can never be mistaken for one.
        int style = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE,
                      style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            _topmost.Attach(source);   // see TopmostGuard for how it gets lost
    }

    /// <summary>
    /// Shows it in the top-right corner of the game - see <see cref="TargetFor"/>
    /// - or of the primary monitor if there is no window to go by. Cheap
    /// enough to call on every refresh: it only moves when the game did.
    /// </summary>
    public void ShowOn(IntPtr gameWindow)
    {
        var target = TargetFor(gameWindow);

        bool moved = !Same(target, _target);
        _target = target;

        // The monitor first, so that the size settling during Show() already
        // anchors it to the right corner.
        if (!IsVisible)
            Show();

        if (moved)
            Anchor();
        else
            _topmost.Check();

        // A borderless game is often topmost itself, and brought forward after
        // the overlay it covers it - the figure is no use under the game.
        _topmost.KeepAbove();
    }

    /// <summary>
    /// The figure, coloured like the FPS row: green, amber below its Warn, red
    /// below its Crit. A dash while there is no reading yet.
    /// </summary>
    public void SetFps(double? fps)
    {
        if (fps is double value && double.IsNaN(value))
            fps = null;

        _fps = fps;

        string text = fps is double shown ? shown.ToString("0", CultureInfo.InvariantCulture) : "–";
        var brush = BrushFor(fps);

        if (text == _drawnText && ReferenceEquals(brush, _drawnBrush))
            return;   // twice a second, and most of the time nothing changed

        _drawnText = text;
        _drawnBrush = brush;
        Redraw();
    }

    /// <summary>
    /// An alert under the figure - "CPU temperature 92 °C" - in the red of a
    /// critical reading, for <see cref="AlertShownFor"/>. Over a fullscreen
    /// game this is the one place a warning can be seen: Windows holds
    /// notifications back until the game is left.
    /// </summary>
    public void ShowAlert(string text)
    {
        _alert = text;
        _alertTimer ??= new DispatcherTimer(AlertShownFor, DispatcherPriority.Normal, (_, _) =>
        {
            _alertTimer!.Stop();
            _alert = null;
            Redraw();
        }, Dispatcher);
        _alertTimer.Stop();
        _alertTimer.Start();
        Redraw();
    }

    private static readonly TimeSpan AlertShownFor = TimeSpan.FromSeconds(12);
    private string? _alert;
    private DispatcherTimer? _alertTimer;

    /// <summary>
    /// Re-reads the font, size, opacity and colours. The theme's brushes are
    /// replaced rather than edited when a colour changes, so they have to be
    /// fetched again - see UiTheme.Apply.
    /// </summary>
    public void ApplyStyle()
    {
        Opacity = Math.Clamp(_settings.FpsOverlayOpacity, 0.1, 1.0);

        _drawnText = null;   // force the redraw: the font or size may be new
        SetFps(_fps);
    }

    private void Redraw()
    {
        double size = Math.Clamp(_settings.FpsOverlaySize, 12, 160);

        _figure.Draw(_drawnText ?? "–", "FPS", _alert, SafeFont(_settings.FontFamily), size,
                     _drawnBrush ?? _theme.Unavailable, _theme.Label, _theme.Crit,
                     VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>
    /// Where the game is drawn: its window's inside - the client area, without
    /// title bar or borders - cut to the monitor it is on.
    ///
    /// For a fullscreen game that is the whole monitor, so the overlay is in
    /// the screen's corner as before. For a game played in a window, one on
    /// the "always a game" list, it is the window's own corner, and it follows
    /// the window when it is moved. The cut keeps it on screen for a window
    /// pushed partly off the edge.
    /// </summary>
    private static RECT TargetFor(IntPtr gameWindow)
    {
        IntPtr monitor = gameWindow != IntPtr.Zero
            ? MonitorFromWindow(gameWindow, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return default;

        var screen = info.rcMonitor;

        if (gameWindow == IntPtr.Zero || IsIconic(gameWindow) || !GetClientRect(gameWindow, out var client))
            return screen;

        var corner = new POINT { X = client.Left, Y = client.Top };
        if (!ClientToScreen(gameWindow, ref corner))
            return screen;

        var drawn = new RECT
        {
            Left = Math.Max(corner.X, screen.Left),
            Top = Math.Max(corner.Y, screen.Top),
            Right = Math.Min(corner.X + client.Right - client.Left, screen.Right),
            Bottom = Math.Min(corner.Y + client.Bottom - client.Top, screen.Bottom),
        };

        // A window entirely off this monitor, or one too small to matter.
        return drawn.Right - drawn.Left > 0 && drawn.Bottom - drawn.Top > 0 ? drawn : screen;
    }

    private static bool Same(RECT a, RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Moves it to the top-right corner of <see cref="_target"/>.</summary>
    private void Anchor()
    {
        if (_handle == IntPtr.Zero || Same(_target, default) || !GetWindowRect(_handle, out var bounds))
            return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int gap = (int)Math.Round(CornerGap * scale);

        // The wider of what the window is and what the layout says it is about
        // to be - see the SizeChanged handler for why they can differ.
        int width = Math.Max(bounds.Right - bounds.Left, (int)Math.Ceiling(ActualWidth * scale));

        SetWindowPos(_handle, HWND_TOPMOST,
                     _target.Right - width - gap, _target.Top + gap,
                     0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private Brush BrushFor(double? fps)
    {
        if (fps is not double value)
            return _theme.Unavailable;

        if (_fpsMetric is not { } metric)
            return _theme.Normal;

        return metric.Exceeds(value, metric.Crit) ? _theme.Crit
             : metric.Exceeds(value, metric.Warn) ? _theme.Warn
             : _theme.Normal;
    }

    /// <summary>The FPS row of the game section, by its label.</summary>
    private static Metric? FindFpsMetric()
    {
        foreach (var section in MetricRegistry.Sections)
        {
            if (!section.OnlyWhileGaming)
                continue;

            foreach (var metric in section.Metrics)
                if (string.Equals(metric.Label, "FPS", StringComparison.OrdinalIgnoreCase))
                    return metric;
        }

        return null;
    }

    private static FontFamily SafeFont(string name)
    {
        try { return new FontFamily(name); }
        catch { return new FontFamily("Consolas"); }
    }

    /// <summary>
    /// The figure and the "FPS" after it, drawn as outlined shapes and cropped
    /// to the ink.
    ///
    /// A TextBlock can't do either. It has no outline, and its box is a whole
    /// line high - room above the digits for accents and below them for
    /// descenders - so a TextBlock pushed into the corner still leaves a third
    /// of the font size of empty space above the numbers, and the gap to the
    /// top edge would be visibly bigger than the gap to the right one. Cropped
    /// to the shapes, both gaps are exactly <see cref="CornerGap"/>.
    /// </summary>
    private sealed class Figure : FrameworkElement
    {
        /// <summary>Dark enough to lift the digits off a bright scene, light enough not to read as a box.</summary>
        private static readonly Brush OutlineBrush = Frozen(Color.FromArgb(0xB0, 0, 0, 0));

        private Geometry? _value;
        private Geometry? _unit;
        private Geometry? _alert;
        private Brush _valueBrush = Brushes.Transparent;
        private Brush _unitBrush = Brushes.Transparent;
        private Brush _alertBrush = Brushes.Transparent;
        private Pen? _outline;
        private Rect _bounds = Rect.Empty;

        /// <param name="alert">A line under the figure, right-aligned with it; null for none.</param>
        public void Draw(string value, string unit, string? alert, FontFamily family, double size,
                         Brush valueBrush, Brush unitBrush, Brush alertBrush, double pixelsPerDip)
        {
            var bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var normal = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            var valueText = Text(value, bold, size, pixelsPerDip);
            var unitText = Text(unit, normal, Math.Max(9, size * 0.38), pixelsPerDip);

            // The unit on the figure's baseline, a little way after it.
            _value = valueText.BuildGeometry(new Point(0, 0));
            _unit = unitText.BuildGeometry(new Point(
                valueText.WidthIncludingTrailingWhitespace + size * 0.12,
                valueText.Baseline - unitText.Baseline));

            _valueBrush = valueBrush;
            _unitBrush = unitBrush;

            // Outside the shapes only - see OnRender - so half of it shows.
            double thickness = Math.Max(2, size / 12);
            _outline = new Pen(OutlineBrush, thickness) { LineJoin = PenLineJoin.Round };
            _outline.Freeze();

            var bounds = _value.Bounds;
            bounds.Union(_unit.Bounds);

            // The height of a digit rather than of whatever is shown, so the
            // dash before the first reading sits where the numbers will -
            // cropped to itself, it would be pulled up to the top edge.
            var digit = Text("0", bold, size, pixelsPerDip).BuildGeometry(new Point(0, 0)).Bounds;
            if (!bounds.IsEmpty && !digit.IsEmpty)
                bounds = new Rect(bounds.X, digit.Top, bounds.Width, digit.Height);

            // The alert under it all, its right edge on the figure's, so the
            // window - anchored by its right edge - grows to the left and down.
            _alert = null;
            _alertBrush = alertBrush;
            if (alert is not null && !bounds.IsEmpty)
            {
                var alertText = Text(alert, bold, Math.Max(11, size * 0.36), pixelsPerDip);
                var line = alertText.BuildGeometry(new Point(0, 0));
                _alert = alertText.BuildGeometry(new Point(
                    bounds.Right - line.Bounds.Right,
                    bounds.Bottom + size * 0.18 - line.Bounds.Top));
                bounds.Union(_alert.Bounds);
            }

            // Half the outline sticks out past the shapes, and a pixel more
            // for the edge smoothing, which would otherwise be clipped.
            if (!bounds.IsEmpty)
                bounds.Inflate(thickness / 2 + 1, thickness / 2 + 1);

            _bounds = bounds;

            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size available)
            => _bounds.IsEmpty ? new Size(0, 0) : _bounds.Size;

        protected override void OnRender(DrawingContext context)
        {
            if (_value is null || _unit is null || _outline is null || _bounds.IsEmpty)
                return;

            context.PushTransform(new TranslateTransform(-_bounds.X, -_bounds.Y));

            // Outline first and the fill over it, so the fill covers the inner
            // half of the stroke and the digits keep their full weight.
            context.DrawGeometry(null, _outline, _value);
            context.DrawGeometry(null, _outline, _unit);
            context.DrawGeometry(_valueBrush, null, _value);
            context.DrawGeometry(_unitBrush, null, _unit);

            if (_alert is not null)
            {
                context.DrawGeometry(null, _outline, _alert);
                context.DrawGeometry(_alertBrush, null, _alert);
            }

            context.Pop();
        }

        private static FormattedText Text(string text, Typeface typeface, double size, double pixelsPerDip)
            => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                   typeface, size, Brushes.White, pixelsPerDip);

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
