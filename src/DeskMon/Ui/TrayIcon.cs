using System;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskMon.Ui;

/// <summary>
/// The single notification-area icon that represents the whole program, the way
/// Steam or Discord do it. The widget and the extra windows stay out of the
/// taskbar entirely, so there is one place to find DeskMon rather than one
/// button per open window.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _showWidget;
    private readonly Forms.ToolStripMenuItem _alwaysOnTop;
    private readonly Forms.ToolStripMenuItem _locked;
    private Drawing.Icon? _generatedIcon;

    public TrayIcon(string tooltip)
    {
        var menu = new Forms.ContextMenuStrip
        {
            // Windows renders tool strips light by default, which would be the
            // one bright thing among these windows.
            BackColor = Drawing.Color.FromArgb(0x1B, 0x1E, 0x24),
            ForeColor = Drawing.Color.FromArgb(0xE6, 0xEA, 0xF0),
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            // For the two tickable items. On every row, so all the text
            // starts on one line - the same as the widget's own menu.
            ShowCheckMargin = true,
        };

        // The same items in the same order as the widget's right-click menu
        // (MainWindow.xaml), so it doesn't matter which one you open. Change
        // one, change the other.

        // Named for what it will do - "Hide widget" while it is up, "Show
        // widget" while it is put away - rather than a tick beside a fixed
        // "Show widget", which left you working out whether ticked meant
        // "is shown" or "click to show".
        _showWidget = new Forms.ToolStripMenuItem();
        _showWidget.Click += (_, _) => FlipWidget();
        menu.Items.Add(_showWidget);
        ShowWidgetState();

        _alwaysOnTop = AddTick(menu, "Always on top", on => SetAlwaysOnTop?.Invoke(on));
        _locked = AddTick(menu, "Lock position", on => SetLocked?.Invoke(on));
        Add(menu, "Reset peaks", () => ResetPeaks?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Read as the menu opens rather than pushed in whenever they change:
        // the widget's menu and the settings window change them too, and this
        // way there is no third place that has to remember to tell the tray.
        menu.Opening += (_, _) =>
        {
            _alwaysOnTop.Checked = IsAlwaysOnTop?.Invoke() ?? false;
            _locked.Checked = IsLocked?.Invoke() ?? false;
        };

        Add(menu, "Hardware info", () => OpenHardware?.Invoke());
        Add(menu, "Versions", () => OpenVersions?.Invoke());
        Add(menu, "Session history", () => OpenSessions?.Invoke());
        Add(menu, "Notes", () => OpenNotes?.Invoke());
        Add(menu, "Open notes folder", () => OpenNotesFolder?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        // The window first, the file second: the window is what you want nine
        // times out of ten, and it is the same settings either way.
        Add(menu, "Settings", () => OpenSettingsWindow?.Invoke());
        Add(menu, "Open settings.json", () => OpenSettings?.Invoke());
        Add(menu, "Reload settings", () => ReloadSettings?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add(menu, "Exit", () => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = BuildIcon(),
            Text = Truncate(tooltip),
            Visible = true,
            ContextMenuStrip = menu,
        };

        // A left click shows or hides the widget, the same as the menu item -
        // the one thing done from the tray often enough to deserve a single
        // click. Right click still opens the menu.
        //
        // MouseClick rather than Click, which fires for the right button too
        // and would flip the widget every time the menu was opened. A
        // double-click raises one MouseClick, not two, so it can't flip the
        // widget and straight back - which is also why the old double-click
        // handler is gone: it would now be a second, competing toggle.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                FlipWidget();
        };

        _icon.BalloonTipClicked += (_, _) =>
        {
            _balloonUp = false;
            _notificationClicked?.Invoke();
        };

        // A balloon replaced by a newer one closes after the newer one is
        // shown, and its Closed used to clear the newer one's click action -
        // so "DeskMon x.y is out" did nothing when clicked. Those closes are
        // counted when the newer one is shown, and passed over here.
        _icon.BalloonTipClosed += (_, _) =>
        {
            if (_staleCloses > 0)
            {
                _staleCloses--;
                return;
            }

            _balloonUp = false;
            _notificationClicked = null;
        };
    }

    private Action? _notificationClicked;

    /// <summary>A balloon is on screen, so the next Notify replaces it - see the BalloonTipClosed handler.</summary>
    private bool _balloonUp;

    /// <summary>Closed events still to come from balloons a newer one replaced.</summary>
    private int _staleCloses;

    /// <summary>
    /// A Windows notification from the tray icon - an alert, or a new version.
    /// <paramref name="clicked"/> runs when it is clicked, e.g. opening the
    /// release page. Windows holds notifications back during a fullscreen game
    /// (Focus assist) and shows them afterwards; MainWindow puts alerts under
    /// the FPS overlay too, for that.
    /// </summary>
    public void Notify(string title, string text, bool warning, Action? clicked = null)
    {
        if (_balloonUp)
            _staleCloses++;   // the one up now closes after this is shown
        _balloonUp = true;
        _notificationClicked = clicked;
        _icon.ShowBalloonTip(10_000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// What you asked for, not what is on screen - see the note below on why
    /// a game hiding the widget doesn't count.
    /// </summary>
    private bool _widgetShown = true;

    private void FlipWidget()
    {
        _widgetShown = !_widgetShown;
        ShowWidgetState();
        ToggleWidget?.Invoke(_widgetShown);
    }

    private void ShowWidgetState()
        => _showWidget.Text = _widgetShown ? "Hide widget" : "Show widget";

    /// <summary>
    /// "Hide widget" from the widget's own menu. Routed through here so this
    /// menu's item flips to "Show widget" - hiding the window directly would
    /// leave the tray offering to hide a widget that is already gone.
    /// </summary>
    public void HideWidget()
    {
        if (_widgetShown)
            FlipWidget();
    }

    /// <summary>True to show the widget, false to hide it.</summary>
    public event Action<bool>? ToggleWidget;

    /// <summary>The new state, after the click.</summary>
    public event Action<bool>? SetAlwaysOnTop;
    public event Action<bool>? SetLocked;
    public event Action? ResetPeaks;

    /// <summary>Asked each time the menu opens, for the ticks.</summary>
    public Func<bool>? IsAlwaysOnTop { get; set; }
    public Func<bool>? IsLocked { get; set; }
    public event Action? OpenHardware;
    public event Action? OpenVersions;
    public event Action? OpenSessions;
    public event Action? OpenNotes;
    public event Action? OpenNotesFolder;
    /// <summary>The settings window.</summary>
    public event Action? OpenSettingsWindow;

    /// <summary>settings.json in whatever opens .json files.</summary>
    public event Action? OpenSettings;
    public event Action? ReloadSettings;
    public event Action? ExitRequested;

    // There is deliberately no way to set the widget's state from outside -
    // HideWidget above is a click, not a state - and no live tooltip.
    //
    // "Show/Hide widget" tracks what you asked for, not what is currently on
    // screen: PauseWhenFullscreen hides the panel for the duration of a game,
    // and offering "Show widget" then would say the widget had been turned off
    // when it is simply waiting for the game to end. Only your own clicks -
    // the menu item, a left click on the icon, and Hide widget in the
    // widget's own menu - change that intent.
    //
    // A tooltip showing live readings was tried and removed: while the widget is
    // hidden nothing is read at all, which is the whole point of hiding it, so
    // the hover text would have been quietly out of date exactly when it was
    // the only thing left to look at.

    private static void Add(Forms.ContextMenuStrip menu, string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    /// <summary>An item with a tick, which flips itself and reports the new state.</summary>
    private static Forms.ToolStripMenuItem AddTick(Forms.ContextMenuStrip menu, string text, Action<bool> changed)
    {
        var item = new Forms.ToolStripMenuItem(text) { CheckOnClick = true };
        item.Click += (_, _) => changed(item.Checked);
        menu.Items.Add(item);
        return item;
    }

    /// <summary>NotifyIcon.Text throws above 63 characters.</summary>
    private static string Truncate(string text)
        => text.Length <= 62 ? text : text[..62];

    /// <summary>
    /// Draws the icon rather than shipping an .ico: a dark rounded tile with
    /// three bars, echoing the widget's meters so it is recognisable at 16px.
    /// </summary>
    private Drawing.Icon BuildIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var canvas = Drawing.Graphics.FromImage(bitmap))
        {
            canvas.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            canvas.Clear(Drawing.Color.Transparent);

            using var panel = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xF2, 0x15, 0x17, 0x1C));
            using var edge = new Drawing.Pen(Drawing.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            using var path = RoundedRectangle(1, 1, 29, 29, 7);
            canvas.FillPath(panel, path);
            canvas.DrawPath(edge, path);

            // Short, medium, tall - reads as a meter even when tiny.
            using var bar = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
            canvas.FillRectangle(bar, 7, 19, 4, 6);
            canvas.FillRectangle(bar, 14, 13, 4, 12);
            canvas.FillRectangle(bar, 21, 8, 4, 17);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Clone, because the icon must outlive the handle we destroy below.
            _generatedIcon = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
            return _generatedIcon;
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRectangle(int x, int y, int width, int height, int radius)
    {
        var path = new Drawing.Drawing2D.GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The menu in the theme's popup colours - the ones the widget's own
    /// right-click menu takes from UiTheme, so the two still match.
    /// </summary>
    public void ApplyTheme()
    {
        if (System.Windows.Application.Current is not { } app || _icon.ContextMenuStrip is not { } menu)
            return;

        static Drawing.Color From(object? resource, Drawing.Color fallback)
            => resource is System.Windows.Media.SolidColorBrush { Color: var c }
                ? Drawing.Color.FromArgb(c.A, c.R, c.G, c.B)
                : fallback;

        DarkColours.Panel = From(app.Resources["Theme.Popup"], DarkColours.Panel);
        DarkColours.Hover = From(app.Resources["Theme.MenuHot"], DarkColours.Hover);
        DarkColours.Edge = From(app.Resources["Theme.PopupEdge"], DarkColours.Edge);
        menu.BackColor = DarkColours.Panel;
        menu.ForeColor = From(app.Resources["Theme.Text"], menu.ForeColor);
        foreach (Forms.ToolStripItem item in menu.Items)
            item.ForeColor = menu.ForeColor;
        menu.Invalidate();
    }

    public void Dispose()
    {
        _icon.Visible = false;   // or the icon lingers in the tray until hovered
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _generatedIcon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// The professional renderer in dark colours, with one part redrawn: the
    /// tick. The stock one is a dark glyph on a light-blue square, which on
    /// this background is a bright blob with the tick lost in it. Drawn here
    /// as the same stroke the widget's menu uses (DarkMenuItem in PanelStyles.xaml).
    /// </summary>
    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColours())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var box = e.ImageRectangle;
            float x = box.Left + (box.Width - 7) / 2f;
            float y = box.Top + (box.Height - 6) / 2f;

            var smoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Drawing.Pen(e.Item.ForeColor, 1.4f);
            e.Graphics.DrawLines(pen, new[]
            {
                new Drawing.PointF(x, y + 3),
                new Drawing.PointF(x + 2.5f, y + 6),
                new Drawing.PointF(x + 7, y + 0.5f),
            });
            e.Graphics.SmoothingMode = smoothing;
        }
    }

    /// <summary>
    /// Dark colours for the tool strip renderer. The widget's right-click menu
    /// copies these (DarkContextMenu in PanelStyles.xaml), so change both together.
    /// </summary>
    private sealed class DarkColours : Forms.ProfessionalColorTable
    {
        // The defaults until ApplyTheme gives the theme's; static, as there is one menu.
        internal static Drawing.Color Panel = Drawing.Color.FromArgb(0x1B, 0x1E, 0x24);
        internal static Drawing.Color Hover = Drawing.Color.FromArgb(0x33, 0x38, 0x42);
        internal static Drawing.Color Edge = Drawing.Color.FromArgb(0x3A, 0x40, 0x4A);

        public override Drawing.Color ToolStripDropDownBackground => Panel;
        public override Drawing.Color MenuBorder => Edge;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Panel;
        public override Drawing.Color ImageMarginGradientMiddle => Panel;
        public override Drawing.Color ImageMarginGradientEnd => Panel;
        public override Drawing.Color SeparatorDark => Edge;
        public override Drawing.Color SeparatorLight => Edge;
    }
}
