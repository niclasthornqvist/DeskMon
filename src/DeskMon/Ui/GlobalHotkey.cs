using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeskMon.Ui;

/// <summary>
/// A keyboard shortcut that works while another program - a game - has the
/// focus. Ordinary WPF shortcuts only reach the window that has it, which
/// during a game is never one of DeskMon's.
///
/// Windows hands a registered hotkey to one program only, and to nobody else:
/// while DeskMon holds it, that key combination stops reaching the game. That
/// is why the default is an unusual combination, and why it can be changed or
/// switched off in the settings.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    /// <summary>Held down, a key would otherwise fire over and over.</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _window;
    private readonly int _id;
    private readonly HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    /// <param name="window">Must already have its handle - call from OnSourceInitialized or later.</param>
    /// <param name="id">Unique per hotkey within this window.</param>
    public GlobalHotkey(Window window, int id)
    {
        _window = new WindowInteropHelper(window).Handle;
        _id = id;
        _source = HwndSource.FromHwnd(_window);
        _source?.AddHook(OnMessage);
    }

    /// <summary>
    /// Takes the hotkey written as in the settings - "Ctrl+Shift+F12" - and
    /// drops whatever was registered before. Empty switches it off. Returns
    /// null on success, or why it could not be done.
    /// </summary>
    public string? Register(string? gesture)
    {
        Unregister();

        if (string.IsNullOrWhiteSpace(gesture))
            return null;

        KeyGesture parsed;
        try
        {
            parsed = (KeyGesture)new KeyGestureConverter().ConvertFromInvariantString(gesture.Trim())!;
        }
        catch
        {
            return $"\"{gesture}\" is not a key combination DeskMon understands";
        }

        uint modifiers = MOD_NOREPEAT;
        if (parsed.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (parsed.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (parsed.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (parsed.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;

        int key = KeyInterop.VirtualKeyFromKey(parsed.Key);

        if (!RegisterHotKey(_window, _id, modifiers, (uint)key))
            return $"{gesture} is already taken by another program";

        _registered = true;
        return null;
    }

    private void Unregister()
    {
        if (!_registered)
            return;

        UnregisterHotKey(_window, _id);
        _registered = false;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(OnMessage);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
