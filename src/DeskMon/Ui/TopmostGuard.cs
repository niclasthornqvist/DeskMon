using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskMon.Ui;

/// <summary>
/// Puts "always on top" back when Windows takes it away behind the window's
/// back.
///
/// WPF sets a window topmost once - when <see cref="Window.Topmost"/> changes -
/// and never looks again. But the flag lives on the window handle, and Windows
/// can clear it from outside: making a window non-topmost makes its owner
/// non-topmost too, and every one of these windows owns a "Default IME"
/// window that the text-input system shuffles about as focus moves between
/// programs. The widget then dropped behind whatever was clicked - VS Code,
/// say - with Always on top still ticked, and ticking it again did nothing,
/// because to WPF it had never changed.
///
/// So the handle is watched: whenever its position or style changes, and the
/// window is meant to be on top but no longer is, it is put back. The intent
/// is read from <see cref="Window.Topmost"/> itself, so turning the setting
/// off is respected - only a flag lost without being asked is restored.
/// </summary>
internal sealed class TopmostGuard
{
    private readonly Window _window;
    private IntPtr _handle;
    private bool _queued;
    private DateTime _lastRestore = DateTime.MinValue;
    private DateTime _lastLogged = DateTime.MinValue;
    private DispatcherTimer? _retry;

    private static readonly TimeSpan RestoreGap = TimeSpan.FromSeconds(1);

    public TopmostGuard(Window window) => _window = window;

    private void RetryTick(object? sender, EventArgs e)
    {
        _retry?.Stop();
        Check();
    }

    /// <summary>Call from OnSourceInitialized, the first moment there is a handle to watch.</summary>
    public void Attach(HwndSource source)
    {
        _handle = source.Handle;
        source.AddHook(OnMessage);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Every z-order change arrives as WINDOWPOSCHANGED, a flag cleared
        // through SetWindowLong as STYLECHANGED.
        if (msg is WM_WINDOWPOSCHANGED or WM_STYLECHANGED)
            CheckSoon();

        return IntPtr.Zero;
    }

    /// <summary>
    /// After the message in hand is finished, not inside it: moving the window
    /// in the middle of being told it moved would be asked for again before
    /// the first move had settled. Once, however many messages arrive.
    /// </summary>
    public void CheckSoon()
    {
        if (_queued)
            return;

        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false;
            Check();
        }));
    }

    /// <summary>Puts the flag back if the window wants it and has lost it. Cheap: one style read when all is well.</summary>
    public void Check()
    {
        if (_handle == IntPtr.Zero || !_window.Topmost || !_window.IsVisible)
            return;

        if ((GetWindowLong(_handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
            return;

        // Not more than once a second: if something were to take it away
        // again every time it is put back, the two would otherwise trade
        // places as fast as messages can go. Held back, it is tried again
        // when the second is up - the other windows have no tick of their own
        // to catch it, and nothing else might come along to ask.
        var now = DateTime.UtcNow;
        var wait = _lastRestore + RestoreGap - now;
        if (wait > TimeSpan.Zero)
        {
            _retry ??= new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher);
            _retry.Interval = wait;
            _retry.Tick -= RetryTick;
            _retry.Tick += RetryTick;
            _retry.Start();
            return;
        }
        _lastRestore = now;

        SetWindowPos(_handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Noted in error.log, at most hourly, so it can be told whether this
        // is what was happening - and how often.
        if (now - _lastLogged > TimeSpan.FromHours(1))
        {
            _lastLogged = now;
            App.Log("always on top", $"Windows took \"always on top\" off the {_window.Title} window; put it back.");
        }
    }

    /// <summary>
    /// Puts the window back above another program's always-on-top window that
    /// has come over it.
    ///
    /// The flag being kept is not enough on its own. Windows keeps every
    /// topmost window in one band, ordered by which was brought forward last -
    /// so a borderless game that is topmost itself, or a tool window like Task
    /// Manager, covers this one the moment it is clicked, flag or no flag.
    /// That read as "always on top" not working.
    ///
    /// Only for windows that actually overlap this one and can be seen: an
    /// invisible click-through overlay - NVIDIA's lies across the whole screen
    /// - covers nothing, and raising past it every tick would be churn for no
    /// purpose. DeskMon's own windows are left out on purpose, so clicking the
    /// notepad still brings it in front of the widget. Cheap: the windows
    /// above a topmost one are only the few other topmost ones.
    /// </summary>
    public void KeepAbove()
    {
        if (_handle == IntPtr.Zero || !_window.Topmost || !_window.IsVisible
            || !GetWindowRect(_handle, out var mine))
            return;

        int ownPid = Environment.ProcessId;

        for (IntPtr above = GetWindow(_handle, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
        {
            if (!IsWindowVisible(above) || IsCloaked(above))
                continue;

            if ((GetWindowLong(above, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
                continue;   // click-through: an overlay drawn over everything, not a window in the way

            GetWindowThreadProcessId(above, out uint pid);
            if (pid == ownPid)
                continue;

            if (!GetWindowRect(above, out var theirs)
                || theirs.Right <= mine.Left || theirs.Left >= mine.Right
                || theirs.Bottom <= mine.Top || theirs.Top >= mine.Bottom)
                continue;   // somewhere else on the screen

            SetWindowPos(_handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return;
        }
    }

    /// <summary>Hidden by the shell - a suspended Store app, a window on another virtual desktop.</summary>
    private static bool IsCloaked(IntPtr window)
        => DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint GW_HWNDPREV = 3;
    private const int DWMWA_CLOAKED = 14;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_STYLECHANGED = 0x007D;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
