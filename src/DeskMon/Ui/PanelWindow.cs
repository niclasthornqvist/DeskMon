using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeskMon.Ui;

/// <summary>
/// Base class for the secondary windows (notes, hardware info) so they share
/// the widget's look instead of each reimplementing it: no OS chrome, rounded
/// translucent panel, drag by the header, resize by the edges.
///
/// There is no XAML for this class on purpose - a WPF window can only have one
/// XAML file, and the derived windows need theirs. This holds the behaviour;
/// Ui/PanelStyles.xaml holds the shared control styles.
/// </summary>
public abstract class PanelWindow : Window
{
    protected PanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        _topmost = new TopmostGuard(this);
    }

    /// <summary>
    /// Call from the header's MouseLeftButtonDown. Drag moves the window, and
    /// double-click maximises it - what the missing title bar would have done.
    /// </summary>
    protected void HandleHeaderMouseDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Button was already up; nothing moved.
        }
    }

    // --- Dimming during a game -------------------------------------------

    private AppSettings? _settings;
    private Func<double>? _ownOpacity;
    private bool _gameRunning;

    /// <summary>
    /// Makes this window fade back while a game is running, like the widget,
    /// and come back to full opacity whenever it is the window being used.
    ///
    /// The two halves answer different questions. Over a game these windows are
    /// something you glance at, so they should sit back out of the way - but the
    /// moment you click into the notes you are reading and typing in them, and
    /// a faded notepad is simply harder to write in. So the dimming lifts on
    /// focus and returns when focus goes back to the game.
    ///
    /// The settings are passed in rather than taken by the constructor because
    /// a WPF window needs a parameterless one for InitializeComponent, and the
    /// derived windows each already hold them.
    /// </summary>
    /// <param name="ownOpacity">
    /// This window's own opacity setting, read through a lambda rather than
    /// passed by value so that changing it in the settings window takes effect
    /// without the window being rebuilt.
    /// </param>
    protected void DimDuringGames(AppSettings settings, Func<double> ownOpacity)
    {
        _settings = settings;
        _ownOpacity = ownOpacity;

        Activated += (_, _) => ApplyWindowOpacity();
        Deactivated += (_, _) => ApplyWindowOpacity();

        ApplyWindowOpacity();
    }

    /// <summary>
    /// Told by the widget when a game starts or ends, and again when the
    /// opacity itself is changed in the settings window - so it re-applies
    /// every time rather than only on a change.
    /// </summary>
    public void SetGameRunning(bool running)
    {
        _gameRunning = running;
        ApplyWindowOpacity();
    }

    /// <summary>
    /// This window's own opacity, unless a game is running and it is not the
    /// one in front - then the lower of its own and GameOpacity.
    ///
    /// The lower of the two, not GameOpacity outright: a window already set to
    /// 0.5 would otherwise be made *brighter* by a game starting, which is the
    /// opposite of what dimming is for.
    ///
    /// Both are clamped like the widget's, because a hand-edited 0 would leave
    /// a window invisible but still sitting there to be clicked.
    /// </summary>
    private void ApplyWindowOpacity()
    {
        if (_settings is null || _ownOpacity is null)
            return;   // this window didn't ask to be dimmed

        double own = Math.Clamp(_ownOpacity(), 0.1, 1.0);

        Opacity = _gameRunning && !IsActive
            ? Math.Min(own, Math.Clamp(_settings.GameOpacity, 0.1, 1.0))
            : own;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // A transparent, chrome-less WPF window has no resize border of its
        // own, so the frame answers hit-tests for the edges itself. Windows
        // then does the rest - cursors, the drag, snapping.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnHitTest);

            // "Windows on top" kept on top - see TopmostGuard for how it was lost.
            _topmost.Attach(source);
        }
    }

    private readonly TopmostGuard _topmost;

    /// <summary>
    /// Back above another program's always-on-top window that has covered
    /// this one, when "Windows on top" is on. Asked by the widget on its tick -
    /// see <see cref="TopmostGuard.KeepAbove"/>.
    /// </summary>
    public void KeepAbove() => _topmost.KeepAbove();

    // --- Hiding ---------------------------------------------------------------

    /// <summary>
    /// Raised just before the window hides, while it is still on screen - for
    /// the banner to put itself out. See <see cref="HideSmoothly"/>.
    /// </summary>
    public event Action? Hiding;

    private bool _hidePending;

    /// <summary>
    /// On screen and staying there - not in the moment between being closed
    /// and actually hiding. What "is it open?" should ask before showing it.
    /// </summary>
    public bool IsOpen => IsVisible && !_hidePending;

    /// <summary>
    /// Long enough for a frame or two to reach the screen - the one with the
    /// banner put out - and too short to notice as a delay in closing.
    /// </summary>
    private static readonly TimeSpan HideAfter = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Hides the window - what its ✕ and the widget's toggles end in - with
    /// its banner put out first, on screen.
    ///
    /// Windows keeps the last picture it was given of a window, and shows it
    /// again the instant the window reappears, before WPF draws anything new.
    /// Hidden straight away, that last picture had the banner at full
    /// strength: reopening flashed it up for a moment, then dropped it to
    /// nothing and faded it in. Put out first and drawn so, the window is
    /// remembered without it, and reopening is one clean fade from nothing.
    /// </summary>
    protected async void HideSmoothly()
    {
        if (_hidePending || !IsVisible)
            return;

        _hidePending = true;
        Hiding?.Invoke();

        await System.Threading.Tasks.Task.Delay(HideAfter);

        // Shown again in the meantime - see Show.
        if (!_hidePending)
            return;

        _hidePending = false;
        Hide();
    }

    /// <summary>
    /// Shows the window. Asked while a <see cref="HideSmoothly"/> is still
    /// waiting, it hides first and shows again, so the window comes back
    /// through the same opening - banner fading in - rather than staying up
    /// with the banner put out.
    /// </summary>
    public new void Show()
    {
        if (_hidePending)
        {
            _hidePending = false;
            Hide();
        }

        base.Show();
    }

    private const int WM_NCHITTEST = 0x0084;
    protected const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>
    /// Lets a window take an edge away, or turn a corner into a plain edge -
    /// the pictures window, docked to the notes, has no use for the edge it
    /// shares with them. Gets which edge the mouse is on (the HT values above)
    /// and returns the one to use, or 0 for none.
    /// </summary>
    protected virtual int AdjustResizeEdge(int edge) => edge;

    /// <summary>Width of the grab band along each edge, in device pixels.</summary>
    private const int ResizeBorder = 6;

    private IntPtr OnHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST || WindowState != WindowState.Normal || ResizeMode == ResizeMode.NoResize)
            return IntPtr.Zero;

        if (!GetWindowRect(hwnd, out var bounds))
            return IntPtr.Zero;

        // lParam carries screen coordinates, and they are signed: a window on a
        // monitor left of the primary has negative x.
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

        bool left = x < bounds.Left + ResizeBorder;
        bool right = x >= bounds.Right - ResizeBorder;
        bool top = y < bounds.Top + ResizeBorder;
        bool bottom = y >= bounds.Bottom - ResizeBorder;

        int hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (_, true, true, _) => HTTOPRIGHT,
            (true, _, _, true) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTLEFT,
            (_, true, _, _) => HTRIGHT,
            (_, _, true, _) => HTTOP,
            (_, _, _, true) => HTBOTTOM,
            _ => 0,
        };

        if (hit != 0)
            hit = AdjustResizeEdge(hit);

        if (hit == 0)
            return IntPtr.Zero;   // not an edge; let WPF route it normally

        handled = true;
        return new IntPtr(hit);
    }

    /// <summary>
    /// Puts the window back where it was, clamped onto a monitor that still
    /// exists. Returns false when there was nothing saved to restore.
    /// </summary>
    protected bool RestorePlacement(double? left, double? top, double? width, double? height)
    {
        if (width is > 200) Width = width.Value;
        if (height is > 150) Height = height.Value;

        if (left is null || top is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return false;
        }

        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxLeft = minLeft + SystemParameters.VirtualScreenWidth - 100;
        double maxTop = minTop + SystemParameters.VirtualScreenHeight - 100;

        Left = Math.Clamp(left.Value, minLeft, Math.Max(minLeft, maxLeft));
        Top = Math.Clamp(top.Value, minTop, Math.Max(minTop, maxTop));
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
