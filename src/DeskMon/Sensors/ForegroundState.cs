using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskMon.Sensors;

/// <summary>
/// Answers "is the user in a game / fullscreen app right now?".
///
/// This is the whole point of the widget being cheap: while a fullscreen app is
/// in front, the widget is invisible anyway, so polling sensors would be pure
/// waste. The check itself is two or three Win32 calls - cheaper than a single
/// sensor read - so it is safe to run on a slow heartbeat while hidden.
/// </summary>
internal static class ForegroundState
{
    /// <summary>
    /// What is in front right now. <paramref name="Pid"/> is 0 when the
    /// foreground window isn't a measurable app (the shell, or this widget).
    /// </summary>
    public readonly record struct Foreground(bool IsFullscreen, int Pid);

    /// <summary>
    /// Looks at the foreground window. <paramref name="ownWindow"/> is this
    /// widget's handle so it never mistakes itself for a fullscreen app.
    /// </summary>
    public static Foreground Inspect(IntPtr ownWindow)
    {
        // Exclusive fullscreen is unambiguous, so it is trusted on its own.
        //
        // The vaguer states are not. QUNS_BUSY in particular means only "a
        // full-screen window is in front", and Windows counts a maximized
        // window that happens to fill its monitor - which is any maximized
        // window on a monitor without a taskbar. Trusting it was enough to make
        // a maximized Explorer window look like a game. Those states now have
        // to agree with what the window itself looks like, below.
        bool fullscreen = SHQueryUserNotificationState(out var state) == 0
                          && state == UserNotificationState.RunningD3dFullScreen;

        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == ownWindow || IsShellWindow(foreground))
            return new Foreground(fullscreen, 0);

        // A program that stops answering for a few seconds - a game loading -
        // has its window swapped for a "(Not responding)" ghost, a copy owned
        // by dwm.exe. A fullscreen game's ghost is fullscreen too, so it was
        // taken for a new game named Desktop Window Manager. The window it
        // stands in for is the one that counts, so the game stays the game.
        if (IsGhostWindow(foreground) && HungWindowFromGhostWindow(foreground) is var hung && hung != IntPtr.Zero)
            foreground = hung;

        // Borderless fullscreen: the window must look the part - no title bar,
        // and covering its whole monitor.
        if (!fullscreen)
            fullscreen = IsFullscreenLike(foreground);

        GetWindowThreadProcessId(foreground, out uint pid);
        return new Foreground(fullscreen, (int)pid);
    }

    /// <summary>True when a fullscreen app owns the screen, ignoring which one.</summary>
    public static bool IsFullscreenAppActive(IntPtr ownWindow) => Inspect(ownWindow).IsFullscreen;

    /// <summary>
    /// Whether a window looks like a game presenting borderless-fullscreen.
    ///
    /// Covering the monitor is not enough on its own. A maximized window on a
    /// monitor that has no taskbar fills the entire monitor rectangle, so
    /// Explorer or an editor maximized over there looks exactly like a
    /// fullscreen game by geometry. What separates them is the title bar: a
    /// game's fullscreen window has no caption, an ordinary maximized window
    /// does. Exclusive-fullscreen games don't rely on this path at all - the
    /// notification state identifies those outright.
    ///
    /// This errs toward missing an unusual game rather than mistaking a normal
    /// window for one, because a false positive runs PresentMon, slows the
    /// refresh and pops the notes window open for no reason.
    /// </summary>
    public static bool IsFullscreenLike(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return false;

        // The shell's own windows are the worst offenders. "Progman" is the
        // desktop: it has no title bar and spans every monitor, so it passes
        // both tests below and looks like a permanent fullscreen game. It also
        // belongs to explorer.exe, whose MainWindowHandle can resolve to it -
        // which is how a stray detection could hold on forever.
        if (IsShellWindow(window))
            return false;

        // Overlays are the other half of that problem, and naming them one by
        // one cannot keep up: NVIDIA's GeForce overlay sits on the desktop
        // permanently as a caption-less window covering the primary monitor,
        // which passes both tests here, and its process never exits - so a
        // single stray detection would hold game mode until DeskMon restarted.
        //
        // These three extended styles tell them apart by what they are rather
        // than by what they are called. A game's presentation window has to be
        // activatable and has to appear in Alt+Tab, so it carries none of
        // them, while overlays, HUDs and click-through layers carry at least
        // one. This also catches most of the shell windows listed above on its
        // own; they stay named there because that list documents why.
        int exStyle = GetWindowLong(window, GWL_EXSTYLE);
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT)) != 0)
            return false;

        int style = GetWindowLong(window, GWL_STYLE);
        if ((style & WS_CAPTION) == WS_CAPTION)
            return false;

        return CoversItsMonitor(window);
    }

    /// <summary>
    /// The desktop, the taskbars, and the shell's overlay windows. The answer
    /// for the last window asked about is kept: this runs every tick, mostly
    /// for the same game window, and each look-up built a StringBuilder and a
    /// string for a class name that doesn't change. Only touched from the UI
    /// thread, like the rest of this class's callers.
    /// </summary>
    private static bool IsShellWindow(IntPtr window)
    {
        Classify(window);
        return _wasShell;
    }

    /// <summary>A hung window's "(Not responding)" stand-in - see Inspect. Shares the class-name cache.</summary>
    private static bool IsGhostWindow(IntPtr window)
    {
        Classify(window);
        return _wasGhost;
    }

    private static void Classify(IntPtr window)
    {
        if (window == _classOf && window != IntPtr.Zero)
            return;

        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        string name = className.ToString();

        _classOf = window;
        _wasShell = IsShellClass(name);
        _wasGhost = name == "Ghost";
    }

    private static IntPtr _classOf;
    private static bool _wasShell;
    private static bool _wasGhost;

    private static bool IsShellClass(string className)
    {
        return className is "Progman"
                                    or "WorkerW"
                                    or "Shell_TrayWnd"
                                    or "Shell_SecondaryTrayWnd"
                                    or "Windows.UI.Core.CoreWindow"
                                    or "ApplicationFrameWindow"
                                    or "ThumbnailDeviceHelperWnd"
                                    or "EdgeUiInputTopWndClass"
                                    or "DummyDWMListenerWindow"
                                    or "ForegroundStaging";
    }

    private static bool CoversItsMonitor(IntPtr window)
    {
        if (!GetWindowRect(window, out var bounds))
            return false;

        IntPtr monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var screen = info.rcMonitor;
        return bounds.Left <= screen.Left && bounds.Top <= screen.Top
            && bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom;
    }

    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,                   // a fullscreen window is foreground
        RunningD3dFullScreen = 3,   // exclusive-fullscreen game
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;

    // Kept out of Alt+Tab, cannot be activated by a click, and invisible to
    // hit-testing respectively - none of which a game's own window can afford.
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    // Exported by user32 since Vista, though not in the SDK headers. Zero for
    // anything that isn't a ghost, which Inspect then leaves as it was.
    [DllImport("user32.dll")]
    private static extern IntPtr HungWindowFromGhostWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
