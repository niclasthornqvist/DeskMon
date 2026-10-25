using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Metrics;
using DeskMon.Notes;
using DeskMon.Sensors;
using DeskMon.Ui;
using Microsoft.Win32;

namespace DeskMon;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly List<MetricRow> _rows = new();          // just the measured rows, for refreshing

    /// <summary>
    /// Everything that could be drawn, with what its visibility hangs on: a
    /// game-only row goes with the game, a section's rows fold with their
    /// heading, and a detail row shows only while its metric is expanded.
    /// </summary>
    private sealed record PanelItem(
        object Item,
        bool GameOnly,
        SectionRow? Section,
        MetricRow? DetailOf = null);

    private readonly List<PanelItem> _allItems = new();
    private readonly ObservableCollection<object> _visibleItems = new();     // what is drawn right now
    private readonly FpsMonitor _fps = new();

    /// <summary>Round trips to PingHost, only while a game runs - see PingMonitor.</summary>
    private readonly PingMonitor _ping = new();

    /// <summary>Readings that stay in the red, said once - see AlertWatcher.</summary>
    private AlertWatcher? _alerts;

    /// <summary>Looks for a newer release once a day - see CheckForUpdatesAsync.</summary>
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(24) };

    /// <summary>A check soon after the release page is changed in the settings, once typing stops.</summary>
    private readonly DispatcherTimer _updateSoon = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>The release page the checks were last set up for, so a settings change can tell it moved.</summary>
    private string? _updateCheckApplied;

    /// <summary>OnlineGameArt as the art lookups were made with - see ApplySettings.</summary>
    private bool? _onlineArtApplied;
    private readonly List<SectionRow> _gameSections = new();   // titled after the running game
    private readonly NotesStore _notesStore;
    private readonly SessionHistory _sessions;
    private NotesWindow? _notes;
    private PicturesWindow? _pictures;
    private HardwareWindow? _hardware;
    private VersionsWindow? _versions;
    private SessionsWindow? _sessionsWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _tray;

    /// <summary>DeskMon's mark behind the readings; see WidgetBackdrop.</summary>
    private readonly Banner _backdrop;
    private HeaderRow? _header;

    /// <summary>The widget was hidden from the tray menu, not by a game.</summary>
    private bool _userHidden;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);

    /// <summary>Delays the width re-measure until a burst of settings edits is over.</summary>
    private readonly DispatcherTimer _relayout = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly int _ownPid = Environment.ProcessId;

    // The tracked game, if any.
    private Process? _gameProcess;
    private string? _gameName;
    private int _gamePid;
    private bool _inGame;

    /// <summary>
    /// What the drawn list was last built for - HideFpsWithoutGame and both
    /// layouts' hidden sections - so ApplySettings can tell whether it needs
    /// redoing. See <see cref="VisibilitySettings"/>.
    /// </summary>
    private string? _visibilityApplied;

    private string VisibilitySettings()
        => $"{_settings.HideFpsWithoutGame}|{string.Join(",", _settings.HiddenSections)}|{string.Join(",", _settings.HiddenSectionsInGame)}";

    /// <summary>
    /// The sections the layout in use leaves out entirely, heading and all -
    /// the game's list while one runs, the desktop's otherwise. Set with the
    /// tick boxes under "Sections shown" in the settings window.
    /// </summary>
    private bool IsHidden(SectionRow section)
        => (_inGame ? _settings.HiddenSectionsInGame : _settings.HiddenSections)
            .Contains(section.Key, StringComparer.OrdinalIgnoreCase);

    // --- The session being recorded ---------------------------------------
    //
    // Deliberately apart from the tracking above, because the two have
    // different lifetimes. Tracking follows what is in front of you: an
    // exclusive-fullscreen game minimises itself when you Alt+Tab, so tracking
    // ends there and starts again when you come back. A session is one sitting
    // with one game, from launch to quit, however often you tab away in
    // between - and that is the only thing worth a line in the history.
    private Process? _sessionProcess;
    private string? _sessionGame;
    private int _sessionPid;
    private DateTime _sessionStartedAt;

    /// <summary>
    /// What the CPU and GPU have drawn this session, in watt-hours, added up
    /// from their power readings each tick - see AddSessionEnergy. Null until
    /// either has reported a figure, so a PC without power sensors logs no
    /// "energy 0 Wh" that would read as a measurement.
    /// </summary>
    private double? _sessionEnergyWh;
    private DateTime _lastEnergyAt;

    /// <summary>The GPU driver and Windows build the session is played on - see SystemInfo.QuickVersions.</summary>
    private string? _sessionDriver, _sessionBuild;

    /// <summary>
    /// The session's game's time from its history, read once when the session
    /// opens. The heading adds the running session to it every tick, which
    /// would otherwise mean reading the whole log every tick.
    /// </summary>
    private TimeSpan _sessionPlayedBefore;

    /// <summary>After the GAME heading while nothing is being played, so the heading never ends in mid-air.</summary>
    private const string NoGameTitle = "N/A";

    /// <summary>
    /// When the session's game process exited, while the session waits to see
    /// whether the game comes back. Null while it is running.
    /// </summary>
    private DateTime? _sessionExitedAt;

    /// <summary>
    /// How long an exited game has to come back under the same name and carry
    /// on its session. Some games close and start themselves again - after a
    /// graphics setting, or a launcher handing over - and each restart used to
    /// split one evening into two sessions a minute apart.
    /// </summary>
    private static readonly TimeSpan RelaunchGrace = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The session's frames so far, kept as sums and a histogram rather than as
    /// a rate: a session broken by Alt+Tab is measured in several PresentMon
    /// runs and only sums carry across them. See <see cref="HarvestFrames"/>.
    /// </summary>
    private FrameStats _sessionStats = new();

    /// <summary>
    /// Sessions shorter than this are not written to the history. A game that
    /// was launched and quit, or a window briefly mistaken for one, should not
    /// leave a line behind.
    /// </summary>
    private static readonly TimeSpan ShortestLoggedSession = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often the open session is saved to disk, in case DeskMon is stopped
    /// without the chance to write it - see <see cref="CheckpointSession"/>.
    /// Also the most a crash can cost: the minute since the last save.
    /// </summary>
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(1);

    private DateTime _lastCheckpoint;

    // --- What counts as a game, by name ---------------------------------

    /// <summary>The tracked game is on AlwaysGames, so it counts while windowed.</summary>
    private bool _gameListed;

    /// <summary>
    /// The last process whose name was looked up, and the name. Both name
    /// lists are asked about whatever is in front, on every tick, and the
    /// lookup enumerates every process on the machine - so it is done once
    /// per process that comes to the front, not once per tick.
    /// </summary>
    private int _namedPid;
    private string? _namedProcess;

    /// <summary>
    /// A fullscreen app already ruled out by <see cref="IsNotAGame"/>, so the
    /// lookup behind that decision runs once rather than once per tick.
    /// </summary>
    private int _rejectedPid;

    private string _baseTooltip = "Drag to move\nRight-click for options";

    /// <summary>Which optional programs are missing, as tooltip lines; null when none are.</summary>
    private string? _missingNote;

    private GlobalHotkey? _screenshotHotkey;

    /// <summary>The hotkey text last registered, so unchanged settings don't re-register it.</summary>
    private string? _registeredHotkey;

    /// <summary>Why the hotkey couldn't be registered, for the tooltip; null when it was.</summary>
    private string? _hotkeyNote;

    /// <summary>A capture is under way; a second press waits for it rather than overlapping.</summary>
    private bool _capturing;

    // --- The FPS overlay ----------------------------------------------------

    /// <summary>The big figure in the game's corner. Made the first time a game is measured.</summary>
    private FpsOverlayWindow? _overlay;

    private GlobalHotkey? _overlayHotkey;
    private string? _registeredOverlayHotkey;
    private string? _overlayHotkeyNote;

    /// <summary>
    /// The overlay's own refresh, apart from the tick. The tick runs every
    /// GameIntervalMs during a game - two seconds by default - which is fine
    /// for temperatures but sluggish for a number that is there to be watched.
    /// Reading the frame rate is only a look at what PresentMon has already
    /// counted, so twice a second costs nothing the game would notice.
    /// </summary>
    private readonly DispatcherTimer _overlayTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>The game window the overlay goes by, kept so it isn't looked up twice a second.</summary>
    private IntPtr _overlayAnchor;

    private SensorHub? _hub;
    private IntPtr _handle;
    private bool _reading;    // a background read is still in flight
    private bool _hidden;     // parked behind a fullscreen app
    private bool _paused;     // machine locked or asleep
    private bool _trimmed;    // startup memory has been handed back

    /// <summary>The young-object GC in OnTick: when it last ran, and how often.</summary>
    private DateTime _lastYoungCollect = DateTime.UtcNow;
    private static readonly TimeSpan YoungCollectEvery = TimeSpan.FromSeconds(30);
    private bool _detailsBuilt; // expandable rows have had their lists filled in
    private bool _widthLocked;  // panel width pinned, so only height follows content
    private bool _restarting; // restarting to reload settings.json
    private bool _needsPrime; // CPU-time baseline went stale while paused

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _theme = new UiTheme(_settings);
        GameIcons.Use(_settings);
        PerformanceScore.Use(_settings);
        _notesStore = new NotesStore(_settings.NotesFolder);
        _sessions = new SessionHistory(_settings.SessionFolder);

        // Before anything can open a session of its own: the last run may have
        // stopped without closing - a crash, a power cut - with one still open.
        _sessions.RecoverOpenSession();

        InitializeComponent();

        // DeskMon's own mark behind the readings, over the whole widget. How
        // strongly is a setting (WidgetBackdrop): the numbers are what the
        // widget is for, and it sits over games. Fades in each time it shows.
        // Drifting at a fixed size, so it doesn't rescale with the widget.
        _backdrop = new Banner(Panel, height: null, strength: _settings.WidgetBackdrop, drift: true);
        _backdrop.Show(GameArt.Motif.DeskMon);

        Panel.Background = _theme.Background;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        ApplyOpacity();
        Topmost = _settings.AlwaysOnTop;
        FontSize = SafeFontSize();
        FontFamily = SafeFont(_settings.FontFamily);

        // The menu's ticks are set as it opens - see WidgetMenu_Opened.
        Panel.ToolTip = _baseTooltip;

        // Says so when DeveloperRebuild is on, so a widget with the BUILD
        // button - which runs the source tree as administrator - is never
        // mistaken for a normal install.
        VersionText.Text = "v" + Changelog.Current + (_settings.DeveloperRebuild ? " (Developer mode)" : "");
        VersionText.ToolTip = $"DeskMon {Changelog.Current}\nClick to see what changed.";
        ApplyVersionStyle();

        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        _clockTimer.Start();

        // Only for working on DeskMon itself - see AppSettings.DeveloperRebuild.
        if (BuildScript() is { } script)
        {
            ToolRebuild.Visibility = Visibility.Visible;
            ToolRebuild.ToolTip = $"Rebuild DeskMon from the source and restart it\n{script}";
        }

        BuildRows();
        RowsHost.ItemsSource = _visibleItems;

        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, _settings.IntervalMs));
        _timer.Tick += OnTick;
        _relayout.Tick += (_, _) => OnSettingsSettled();
        _overlayTimer.Tick += (_, _) => UpdateOverlay();

        SetUpTray();

        Loaded += OnLoaded;
        Closed += OnClosed;

        // Stop polling while nobody can see the screen.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// The one notification-area icon that stands for the whole program. The
    /// widget and the other windows are kept out of the taskbar, so this is the
    /// only place DeskMon appears - the way Steam and Discord do it.
    /// </summary>
    private void SetUpTray()
    {
        _tray = new TrayIcon("DeskMon");
        _tray.ApplyTheme();
        _theme.Changed += _tray.ApplyTheme;

        // Said, so a widget that vanished for a moment mid-game isn't a mystery
        // - see App.RestartAfterCrash.
        if (App.StartedAfterCrash)
            _tray.Notify("DeskMon", "DeskMon restarted after a crash - the details are in error.log.", warning: true);

        // The data stayed in app\ this run - see DataFolder.Prepare. Said, so
        // it isn't a silent mystery why it still lives there.
        if (DataFolder.MoveProblem is { } problem)
            _tray.Notify("DeskMon", DataFolder.UsingOldFolder
                ? $"Couldn't move your data to %APPDATA%\\DeskMon: {problem} It stays in app\\ for now; the next start tries again."
                : problem, warning: true);

        _tray.ToggleWidget += show =>
        {
            _userHidden = !show;
            if (show) Show(); else Hide();
            UpdateDrift();
        };
        _tray.SetAlwaysOnTop += SetAlwaysOnTop;
        _tray.SetLocked += SetLocked;
        _tray.ResetPeaks += ResetPeaks;
        _tray.IsAlwaysOnTop = () => _settings.AlwaysOnTop;
        _tray.IsLocked = () => _settings.Locked;
        _tray.OpenHardware += () => _ = ShowHardwareAsync();
        _tray.OpenVersions += ShowVersions;
        _tray.OpenSessions += () => ShowSessions(null);
        _tray.OpenNotes += () => Notes().ShowAndFocus(_gameName);
        _tray.OpenNotesFolder += OpenNotesFolder;
        _tray.OpenSettingsWindow += () => SettingsPanel().ShowAndFocus();
        _tray.OpenSettings += OpenSettingsFile;
        _tray.ReloadSettings += RestartForSettings;
        _tray.ExitRequested += () =>
        {
            PrepareForShutdown();
            Application.Current.Shutdown();
        };
    }

    /// <summary>
    /// Flattens the registry into the list the panel draws: the hardware header,
    /// then each section's title followed by its rows. A separator goes above
    /// every section except the first.
    /// </summary>
    private void BuildRows()
    {
        if (_settings.ShowHardwareNames)
        {
            _header = new HeaderRow(_theme, _settings);
            _allItems.Add(new PanelItem(_header, false, null));
        }

        foreach (var section in OrderedSections())
        {
            if (section.Metrics.Length == 0)
                continue;   // an all-commented-out section shouldn't leave a stray title

            var sectionRow = new SectionRow(section.Title, _theme, _settings, section.Icon, section.Tooltip);
            if (section.OnlyWhileGaming)
            {
                _gameSections.Add(sectionRow);
                sectionRow.SetSuffix(NoGameTitle);   // until a game starts
            }
            _allItems.Add(new PanelItem(sectionRow, section.OnlyWhileGaming, null));

            // By label, so a later row can find the one it folds under.
            var byLabel = new Dictionary<string, MetricRow>(StringComparer.OrdinalIgnoreCase);

            foreach (var metric in section.Metrics)
            {
                var row = new MetricRow(metric, section.Title, _theme, _settings);
                _rows.Add(row);

                // A row folding under another is drawn like a detail row is -
                // only while that one is unfolded - so the hiding is the same
                // DetailOf check. A FoldsUnder naming no earlier row in the
                // section is ignored and the row just shows, rather than going
                // missing for a typo in the registry.
                MetricRow? parent = null;
                if (metric.FoldsUnder is { } under && byLabel.TryGetValue(under, out var found))
                {
                    parent = found;
                    parent.AddFolded(row);
                }

                _allItems.Add(new PanelItem(row, section.OnlyWhileGaming, sectionRow, parent));
                byLabel.TryAdd(metric.Label, row);
            }
        }

        RefreshVisibleItems();
    }

    /// <summary>
    /// MetricRegistry's sections in the order they were last dragged into:
    /// those named in SectionOrder first, in that order, then any others in
    /// the registry's own order - a section added to the registry since still
    /// shows, at the bottom.
    /// </summary>
    private IEnumerable<MetricSection> OrderedSections()
    {
        var order = _settings.SectionOrder;

        return MetricRegistry.Sections
            .Select((section, index) => (section, index))
            .OrderBy(entry =>
            {
                int saved = order.FindIndex(key => string.Equals(key, entry.section.Title, StringComparison.OrdinalIgnoreCase));
                return saved >= 0 ? saved : order.Count + entry.index;
            })
            .Select(entry => entry.section);
    }

    /// <summary>
    /// Rebuilds the drawn list for the current game state, dropping game-only
    /// sections when no game is running and fixing up which section shows a
    /// separator, since that depends on what ends up above it.
    /// </summary>
    private void RefreshVisibleItems()
    {
        _visibleItems.Clear();
        _spareItems.Clear();

        // Remembered so ApplySettings can tell whether the list needs redoing.
        _visibilityApplied = VisibilitySettings();

        bool isFirstSection = true;
        foreach (var entry in _allItems)
        {
            if (entry.GameOnly && !_inGame && _settings.HideFpsWithoutGame)
                continue;

            // Left out of this layout altogether: the heading by itself, the
            // rows by the section they are in.
            if ((entry.Item as SectionRow ?? entry.Section) is { } owner && IsHidden(owner))
                continue;

            if (entry.Item is SectionRow section)
            {
                // Side by side, the panel draws an upright line between the
                // columns instead; a heading's own line would sit across the
                // top of its column, under nothing.
                section.SetSeparator(!isFirstSection && !Horizontal);
                isFirstSection = false;
                _visibleItems.Add(section);   // headings always stay, so a folded
                continue;                     // section can be unfolded again
            }

            if (entry.Section is { IsCollapsed: true })
                continue;

            // A detail row hides with its metric's arrow as well as with the
            // section, so folding a section also hides anything unfolded in it.
            if (entry.DetailOf is { IsExpanded: false })
            {
                // Side by side, kept as a spare instead: its column keeps the
                // room for it, so unfolding the list doesn't widen the strip
                // and push every column after it along. Stacked, the width is
                // pinned to the unfolded layout already - see LockPanelWidth.
                if (Horizontal)
                {
                    _spareItems.Add(entry.Item);
                    _visibleItems.Add(entry.Item);
                }
                continue;
            }

            _visibleItems.Add(entry.Item);
        }

        ResizeToContent();
    }

    /// <summary>
    /// Folded rows in the list only to hold their column's width - see
    /// <see cref="SectionsPanel.IsSpare"/>. Compared by reference: two rows
    /// can be equal in every value they show.
    /// </summary>
    private readonly HashSet<object> _spareItems = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Makes the window re-fit its content. SizeToContent doesn't always
    /// revisit the size when items are added or removed after the window is up,
    /// which is exactly what happens when a game starts.
    ///
    /// Once the width has been pinned this only revisits the height, so folding
    /// a section moves the bottom edge and nothing else.
    /// </summary>
    private void ResizeToContent()
    {
        if (!IsLoaded)
            return;

        SizeToContent = SizeToContent.Manual;
        UpdateLayout();
        SizeToContent = _widthLocked ? SizeToContent.Height : SizeToContent.WidthAndHeight;
        UpdateLayout();
        RestorePosition();   // a taller panel must not end up off the screen edge
    }

    /// <summary>
    /// Pins the panel's width to the widest layout it could ever need, measured
    /// with every section and every sensor list unfolded.
    ///
    /// Folding used to re-measure both dimensions, so clicking an arrow moved
    /// the panel's left and right edges as well as its bottom - the whole thing
    /// twitched on every click. Only the height follows the content now.
    ///
    /// It has to be the unfolded layout that is measured, not the current one:
    /// the width must already be enough for rows that are not on screen yet, or
    /// unfolding one would clip it. Anything wider still than that - a long
    /// game name in a heading - trims with an ellipsis instead of stretching
    /// the panel, which is what the MaxWidth in the templates is there for.
    /// </summary>
    private void LockPanelWidth()
    {
        if (!IsLoaded || _widthLocked)
            return;

        // Side by side, folding a section is how the strip is made narrower,
        // so the width is left to follow the columns. Still redrawn, as the
        // callers expect: the first one has just added the detail rows.
        if (Horizontal)
        {
            RefreshVisibleItems();
            return;
        }

        // Draw everything, including what is folded away and the game-only
        // rows, purely to find out how wide the worst case is.
        _visibleItems.Clear();
        foreach (var entry in _allItems)
            _visibleItems.Add(entry.Item);

        SizeToContent = SizeToContent.WidthAndHeight;
        UpdateLayout();

        double widest = ActualWidth;

        _widthLocked = true;
        Width = widest;

        RefreshVisibleItems();   // puts back what should actually be on screen
    }

    // --- Vertical or horizontal ---------------------------------------------

    /// <summary>The sections side by side rather than stacked - the WidgetLayout setting.</summary>
    private bool Horizontal => string.Equals(_settings.WidgetLayout, "horizontal", StringComparison.OrdinalIgnoreCase);

    /// <summary>The panel the rows are laid out in, once the list has made it - see SectionsPanel_Loaded.</summary>
    private SectionsPanel? _sectionsPanel;

    /// <summary>The layout the panel was last set to, so a settings change can tell it moved.</summary>
    private bool? _horizontalApplied;

    /// <summary>
    /// The list makes its panel from a template, so it can't be named in the
    /// XAML: it says when it is there instead, and takes the layout then.
    /// </summary>
    private void SectionsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _sectionsPanel = sender as SectionsPanel;
        if (_sectionsPanel is not null)
            _sectionsPanel.IsSpare = item => item is not null && _spareItems.Contains(item);
        ApplyLayoutMode();
    }

    /// <summary>
    /// Switches between stacked and side by side, live.
    ///
    /// Side by side, the width is let go of - it follows the columns, so
    /// folding one narrows the strip. Stacked again, it is pinned to the
    /// widest layout once more, as at startup, so folding there moves only
    /// the bottom edge - see <see cref="LockPanelWidth"/>.
    /// </summary>
    private void ApplyLayoutMode()
    {
        _horizontalApplied = Horizontal;

        if (_sectionsPanel is not null)
        {
            _sectionsPanel.Columns = Horizontal;
            _sectionsPanel.SeparatorBrush = _theme.Separator;
        }

        if (Horizontal)
        {
            _widthLocked = false;
            Width = double.NaN;
            RefreshVisibleItems();
        }
        else if (_detailsBuilt)
        {
            RelockWidth();
        }
        else
        {
            RefreshVisibleItems();   // the width is pinned once the sensor lists exist
        }
    }

    /// <summary>
    /// Keeps the widget out of Alt+Tab. A tool window still draws and still
    /// takes mouse input, it just isn't a "real" app window to the shell.
    /// </summary>
    private TopmostGuard? _topmost;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;

        int style = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OnWindowMessage);

            // Always on top kept on top: Windows can take it away without WPF
            // noticing - see TopmostGuard. Also checked on every tick.
            _topmost = new TopmostGuard(this);
            _topmost.Attach(source);
        }

        // Here because a hotkey belongs to a window handle, and this is the
        // first moment the widget has one. The widget rather than the notepad,
        // because the notepad may never have been opened.
        _screenshotHotkey = new GlobalHotkey(this, id: 1);
        _screenshotHotkey.Pressed += () => _ = TakeScreenshotAsync();

        _overlayHotkey = new GlobalHotkey(this, id: 2);
        _overlayHotkey.Pressed += ToggleFpsOverlay;

        RegisterHotkeys();
    }

    /// <summary>
    /// Keeps the widget from taking focus when it is clicked.
    ///
    /// Two reasons. Clicking a heading on an unfocused window would otherwise
    /// spend the first click on activating it - press once, nothing happens,
    /// press again, it works. And a widget that floats over a game must never
    /// pull focus out of that game just because it was clicked.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEACTIVATE)
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(MA_NOACTIVATE);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();

        // Opening LibreHardwareMonitor loads a kernel driver and enumerates
        // hardware, which takes a few hundred ms. Off the UI thread so the
        // widget is on screen immediately.
        var hub = await Task.Run(() => new SensorHub(_settings.PreferredGpu, _settings.SensorIntervalMs));
        _hub = hub;

        // Name the hardware the readings actually come from, which for the GPU
        // is the card PickGpu settled on - not whatever Windows lists first.
        if (_header is not null)
        {
            _header.SetHardware(hub.CpuName, hub.GpuName);

            // The window already sized itself to the metric rows while these
            // names were still a placeholder, and SizeToContent does not revisit
            // that on its own - a long card name would hang past the border.
            // Toggling it forces one more measure pass.
            SizeToContent = SizeToContent.Manual;
            UpdateLayout();
            SizeToContent = SizeToContent.WidthAndHeight;
            UpdateLayout();
            RestorePosition();   // re-clamp in case it just grew off the screen edge
        }

        if (hub.StartupError is not null)
            _baseTooltip = "Temperatures unavailable\nRun DeskMon as administrator.\n\n" + hub.StartupError;

        _alerts = new AlertWatcher(_settings);

        _timer.Start();
        OnTick(this, EventArgs.Empty);   // don't wait a full interval for data

        // A minute in, so starting with Windows doesn't wait on the network,
        // then once a day for a PC that is never switched off.
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            await CheckForUpdatesAsync();
        };
        _updateTimer.Interval = TimeSpan.FromMinutes(1);
        _updateTimer.Start();

        _updateCheckApplied = _settings.CheckForUpdates ? _settings.UpdateRepository.Trim() : string.Empty;
        _updateSoon.Tick += async (_, _) =>
        {
            _updateSoon.Stop();
            await CheckForUpdatesAsync();
        };

        // After the first tick, not before: the PresentMon search walks
        // folders, and the readings shouldn't wait on a tooltip.
        //
        // Checked once at startup: installing either program means restarting
        // DeskMon anyway for it to be picked up, so a stale answer can't linger.
        _missingNote = Prerequisites.Describe(await Task.Run(() => Prerequisites.Check(_settings)));
        UpdateTooltip();
    }

    /// <summary>
    /// The whole polling policy lives here:
    ///   visible      -> read sensors every IntervalMs
    ///   fullscreen   -> hide, read nothing, just re-check every HiddenCheckMs
    ///   locked/asleep-> do nothing at all
    /// </summary>
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_paused || _hub is null)
            return;

        // A second line of defence behind the guard's own watching: should the
        // flag ever go without a message saying so, it is back within a tick.
        _topmost?.Check();

        // And the flag kept is not the whole of it: another program's topmost
        // window - a borderless game, Task Manager - covers this one as soon as
        // it is clicked. Put back above it, here and for the other windows.
        _topmost?.KeepAbove();
        foreach (var window in PanelWindows())
            window.KeepAbove();

        UpdateGameState();
        UpdateGameTitle();   // the playtime moves on while the game runs
        CheckpointSession();

        // Pinging only while there is a game to be lagging; Run is a no-op
        // while nothing changes, so this can be said every tick.
        _ping.Run(_inGame ? _settings.PingHost : null);

        // Opt-in old behaviour: vanish completely during a game instead of
        // acting as an overlay.
        if (_settings.PauseWhenFullscreen && _inGame)
        {
            if (!_hidden)
            {
                _hidden = true;
                Hide();
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, _settings.HiddenCheckMs));
            }
            return;
        }

        // Both re-baselines below take a CPU-time sample, and so does a read.
        // The two share one set of counters with no lock of their own, so a
        // prime overlapping a read still in flight would hand that read - or
        // the next one - a delta measured against the wrong sample: a CPU
        // figure that is simply wrong for one tick. Waiting a tick costs
        // nothing.
        if (_reading && (_hidden || _needsPrime))
            return;

        if (_hidden)
        {
            _hidden = false;
            if (!_userHidden)   // a game ending must not undo "hide the widget"
                Show();
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, _settings.IntervalMs));

            // CPU load is a delta between two samples. Without this the first
            // value after a game would be the average over the whole session.
            var resumed = _hub;
            _needsPrime = false;   // this is that re-baseline
            await PrimeAsync(resumed);
            return;
        }

        // Same delta, same problem, different cause: nothing sampled the CPU-time
        // counters while the session was locked or the machine asleep, so the
        // first reading afterwards would be the average over all of it.
        if (_needsPrime)
        {
            _needsPrime = false;
            var resuming = _hub;
            await PrimeAsync(resuming);
            return;
        }

        // While a game runs, back off to the slower interval - and while the
        // widget is hidden from the tray too: nothing is drawn then, but the
        // readings still feed the alerts and the session's peaks, which used
        // to stop entirely - no temperature alert for someone playing with
        // only the FPS overlay up, and a session log missing its worst figures.
        double wantedInterval = _inGame || _userHidden
            ? Math.Max(250, _settings.GameIntervalMs)
            : Math.Max(100, _settings.IntervalMs);
        if (Math.Abs(_timer.Interval.TotalMilliseconds - wantedInterval) > 1)
            _timer.Interval = TimeSpan.FromMilliseconds(wantedInterval);

        if (_reading)
            return;   // a slow read must not queue up more reads behind it

        _reading = true;
        try
        {
            var hub = _hub;

            // The program lists behind RAM and SWAP are the costly part of a
            // read that nothing else uses; kept fresh only while one is open.
            hub.MemoryListsShown = !_userHidden
                && _rows.Exists(row => row.IsExpanded && row.Key is "Usage/RAM" or "Usage/SWAP");

            var snapshot = await Task.Run(hub.Read);
            AddSessionEnergy(snapshot);

            // FPS is measured by a separate process, not by SensorHub. One read
            // for all three figures, so the average and the lows describe the
            // same frames.
            if (_inGame)
            {
                var frames = _fps.Current;
                snapshot.Fps = frames.Fps;
                snapshot.FpsOnePercentLow = frames.OnePercentLow;
                snapshot.FpsPointOnePercentLow = frames.PointOnePercentLow;
                snapshot.Latency = frames.Latency;

                // Read like the rest of the card, but shown only now - see
                // SensorSnapshot.GameGpuLoad.
                snapshot.GameGpuLoad = snapshot.GpuLoad;
                snapshot.Ping = _ping.Latest;
            }

            if (!_detailsBuilt)
                BuildDetailRows(snapshot);

            foreach (var row in _rows)
                row.Apply(snapshot);

            if (_detailsBuilt && _rows.Exists(row => row.DetailsChanged))
                RebuildDetailRows(snapshot);

            if (_alerts is not null)
                foreach (string alert in _alerts.Check(snapshot))
                    Alert(alert);

            if (!_trimmed)
            {
                _trimmed = true;
                TrimMemory();   // hand back everything JIT and startup needed
            }

            // A young-object GC every half minute. .NET sizes that generation
            // from the CPU's L3 cache - 96 MB on a 7800X3D - and left to
            // itself held about 50 MB for a widget that makes 100 KB of
            // garbage a second and keeps 12 MB, collecting every few minutes.
            // Collected this often it stays at a few MB, for about 0.1 ms each.
            // The setting meant for it (GCgen0size) is only read from an
            // environment variable, not from the app's own config, and a
            // user-wide variable would change every .NET program.
            if (DateTime.UtcNow - _lastYoungCollect >= YoungCollectEvery)
            {
                _lastYoungCollect = DateTime.UtcNow;
                GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            }
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>
    /// Takes a fresh CPU-time baseline, marked as a read in flight. Without
    /// the mark, the next tick could start a read while this was still on the
    /// worker - the two share NativeStats' counters, which have no lock, and
    /// that overlap is the wrong CPU figure the checks in OnTick exist to stop.
    /// </summary>
    private async Task PrimeAsync(SensorHub hub)
    {
        _reading = true;
        try
        {
            await Task.Run(hub.Prime);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>
    /// Slots in the detail rows for every expandable metric, once the first
    /// snapshot has shown which sensors this machine has. Runs once.
    ///
    /// It cannot be done in BuildRows: the sensor library is opened after the
    /// window is already up, so when the rows are built nothing yet knows
    /// whether this chip reports two temperatures or a dozen.
    /// </summary>
    private void BuildDetailRows(SensorSnapshot snapshot)
    {
        _detailsBuilt = true;

        // Walked backwards so that inserting never shifts an index still to come.
        for (int i = _allItems.Count - 1; i >= 0; i--)
        {
            var entry = _allItems[i];
            if (entry.Item is not MetricRow row || !row.HasDetails)
                continue;

            var details = row.BuildDetails(snapshot);

            // Inserted back to front at the same spot, which lands them in order.
            for (int d = details.Count - 1; d >= 0; d--)
                _allItems.Insert(i + 1, entry with { Item = details[d], DetailOf = row });
        }

        // Now that every row that could ever be drawn exists, the widest
        // possible layout can be measured. This also redraws, so a row left
        // unfolded in settings.json appears.
        LockPanelWidth();
    }

    /// <summary>
    /// Swaps in new detail rows for every list whose length has changed for a
    /// while - see <see cref="MetricRow.DetailsChanged"/>. Rare: a drive
    /// plugged in or taken out.
    ///
    /// Only DetailRow entries are taken out: rows folded under a metric hang
    /// on it the same way (DetailOf), and they are not part of its list.
    /// </summary>
    private void RebuildDetailRows(SensorSnapshot snapshot)
    {
        foreach (var row in _rows.Where(r => r.DetailsChanged).ToList())
        {
            _allItems.RemoveAll(entry => entry.Item is DetailRow && entry.DetailOf == row);

            int at = _allItems.FindIndex(entry => entry.Item == row);
            if (at < 0)
            {
                row.RebuildDetails(snapshot);   // not drawn - clears the flag all the same
                continue;
            }

            var entryOfRow = _allItems[at];
            var details = row.RebuildDetails(snapshot);
            for (int d = details.Count - 1; d >= 0; d--)
                _allItems.Insert(at + 1, entryOfRow with { Item = details[d], DetailOf = row });
        }

        // Measured again, as at startup: a longer drive name can widen the
        // panel. Side by side, the width follows the columns anyway.
        if (Horizontal)
            RefreshVisibleItems();
        else
            RelockWidth();
    }

    /// <summary>
    /// Collects and releases the working set. Startup (JIT, XAML parsing,
    /// hardware enumeration) leaves well over 100 MB resident that this process
    /// will never touch again; a widget has no business holding onto it.
    /// Deliberately only done at startup and when a game takes over, never on
    /// the polling path, since the pages have to fault back in afterwards.
    /// </summary>
    private static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch { /* purely an optimisation */ }
    }

    // --- Game detection ---------------------------------------------------

    /// <summary>
    /// Decides whether a game is running and, if so, which process it is.
    ///
    /// Tracking is sticky: once a fullscreen app has been spotted it stays the
    /// tracked game until that process exits, so clicking the widget or tabbing
    /// out doesn't make the FPS section flicker away while the game is still up.
    /// </summary>
    private void UpdateGameState()
    {
        // First, because quitting the game is the only thing that ends a
        // session and it has to be seen wherever it happens - including long
        // after tracking let go of it, which is what Alt+Tab out and then
        // quitting from the desktop looks like. Before the rest, so a pid
        // Windows has handed to something new can never be mistaken for the
        // session still being open.
        //
        // Not ended on the spot, though: kept open for RelaunchGrace in case
        // the game comes back - see BeginOrResumeSession for the way back.
        // Its line is written now all the same, so it is in the history the
        // moment the game is gone instead of three minutes later; a game that
        // does come back takes it out again. The end written is still the
        // moment it exited.
        if (_sessionProcess is not null && _sessionExitedAt is null && SessionProcessExited())
        {
            _sessionExitedAt = ExitTimeOf(_sessionProcess) ?? DateTime.Now;
            LogExitedSession();
        }

        if (_sessionExitedAt is { } exitedAt && DateTime.Now - exitedAt >= RelaunchGrace)
            FinishSession();

        var foreground = ForegroundState.Inspect(_handle);
        bool isGame = false;
        bool listed = false;
        bool measurable = foreground.Pid != 0 && foreground.Pid != _ownPid;

        // The name cache is by pid, and Windows hands an exited process's pid
        // to new ones. Forgotten whenever nothing measurable is in front - the
        // desktop, a DeskMon window - which is what follows a game quitting, so
        // a program given the old game's pid is looked up afresh.
        if (!measurable)
            _namedPid = 0;

        // Asked first, and whatever the window looks like: the list exists for
        // the games the fullscreen test below can't see, played in a window.
        if (measurable && IsListedGame(foreground.Pid))
        {
            isGame = true;
            listed = true;
        }
        else if (foreground.IsFullscreen && measurable)
        {
            // Answering this needs the executable's name, so the answer is kept
            // for as long as that process stays in front: a fullscreen video
            // would otherwise re-ask on every single tick.
            // The tracked game passed the NotGames test when it was picked up,
            // so it isn't asked again: that test opens the process by pid,
            // which enumerates every process on the machine, and this runs on
            // every tick for as long as the game is in front.
            if (_inGame && foreground.Pid == _gamePid)
                isGame = true;
            else if (foreground.Pid == _rejectedPid)
                isGame = false;
            else if (IsNotAGame(foreground.Pid))
                _rejectedPid = foreground.Pid;
            else
                isGame = true;
        }
        else
        {
            _rejectedPid = 0;   // nothing fullscreen in front; decide afresh next time
        }

        if (isGame)
        {
            TrackGame(foreground.Pid, listed);
            return;
        }

        // Nothing in front counts as a game - but a tracked game can still be
        // running behind whatever is, so stickiness is only given up once the
        // game's own window goes away. Deliberately reached for an excluded app
        // too, or a game that exited behind a fullscreen video would stay in the
        // GAME section until something else happened.
        if (_inGame && (HasGameExited() || !TrackedWindowStillFullscreen(foreground.IsFullscreen)))
            StopTrackingGame();
    }

    /// <summary>
    /// Whether this process is on the "never a game" list in settings.json.
    ///
    /// The window tests in <see cref="ForegroundState"/> cannot help here: a
    /// fullscreen YouTube video has no title bar and covers the monitor, which
    /// is exactly what a game looks like. Only the name of the program tells
    /// them apart, so this is a list rather than a rule.
    /// </summary>
    private bool IsNotAGame(int pid) => IsOnList(pid, _settings.NotGames);

    /// <summary>
    /// Whether this process is on the "always a game" list - for games played
    /// in a window, which the fullscreen test never picks up.
    /// </summary>
    private bool IsListedGame(int pid) => IsOnList(pid, _settings.AlwaysGames);

    /// <summary>
    /// Whether the process's executable is named on <paramref name="names"/>.
    /// An empty list answers without looking the process up at all.
    /// </summary>
    private bool IsOnList(int pid, List<string>? names)
    {
        if (names is null || names.Count == 0 || ProcessNameOf(pid) is not { } name)
            return false;

        foreach (string entry in names)
        {
            // Task Manager shows "Arc.exe", so accept it written either way.
            string candidate = entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? entry[..^4]
                : entry;

            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The process's name without ".exe", or null if it can't be opened. See <see cref="_namedPid"/>.</summary>
    private string? ProcessNameOf(int pid)
    {
        if (pid == _namedPid)
            return _namedProcess;

        _namedPid = pid;
        try
        {
            using var process = Process.GetProcessById(pid);
            _namedProcess = process.ProcessName;   // no extension, and cheaper than MainModule
        }
        catch
        {
            _namedProcess = null;
        }

        return _namedProcess;
    }

    /// <summary>
    /// Whether the tracked game still owns a screen. Stickiness exists so that
    /// clicking the widget doesn't drop game mode while the game is still up -
    /// but it must not outlive the game's fullscreen window, or a window that
    /// was misread as a game would hold game mode until the process exited,
    /// which for something like Explorer is never.
    ///
    /// When the window can't be inspected the answer is "yes": dropping a real
    /// game by mistake is worse than keeping it a little too long.
    /// </summary>
    /// <param name="foregroundFullscreen">
    /// What this tick's ForegroundState.Inspect found - passed in rather than
    /// inspected again, which looked the whole screen over twice a tick while
    /// tabbed out of a game.
    /// </param>
    private bool TrackedWindowStillFullscreen(bool foregroundFullscreen)
    {
        if (_gameProcess is null)
            return true;

        try
        {
            IntPtr window = GameWindow();

            if (window == IntPtr.Zero)
                return true;

            // A listed game never needed to be fullscreen to count, so it
            // counts for as long as its window is on screen - clicking past a
            // windowed game to another program leaves it measured, as tabbing
            // out of a borderless one does. Minimised, it is put away.
            if (_gameListed)
                return !IsIconic(window);

            // Exclusive-fullscreen games still report a window covering the
            // monitor, so this holds for them too.
            return ForegroundState.IsFullscreenLike(window)
                   || foregroundFullscreen;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Cheap: asks the handle opened when the game was first seen.</summary>
    private bool HasGameExited()
    {
        if (_gameProcess is null)
            return true;

        try
        {
            return _gameProcess.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private void TrackGame(int pid, bool listed)
    {
        // Before the early return: a game first seen fullscreen can turn out
        // to be listed too, the moment it is windowed.
        _gameListed = listed;

        if (_gamePid == pid && _inGame)
            return;   // already measuring this one

        if (_inGame)
            StopTrackingGame(refresh: false);   // a different fullscreen app took over

        _gamePid = pid;
        try
        {
            _gameProcess = Process.GetProcessById(pid);
            _gameName = FriendlyName(_gameProcess);
        }
        catch
        {
            _gameProcess = null;
            _gameName = null;
        }

        _inGame = true;
        UpdateDrift();   // each step redraws the whole widget - not worth it over a game

        // A hotkey Windows refused earlier is tried again now - a game is
        // when the screenshot and overlay keys are wanted. Does nothing for
        // ones already registered.
        RegisterHotkeys();
        ApplyOpacity();   // fade back, so the panel stops competing with the game
        ApplyLayout();    // the game's folded sections, if it has its own

        // Starts the history's clock, or carries on with a session this is
        // merely a return to.
        bool newSession = BeginOrResumeSession(pid, _gameName);

        // Title the section after what is actually running: "GAME The Witcher 3",
        // or "GAME: The Witcher 3" if the heading is set to "GAME:".
        UpdateGameTitle();

        // PauseWhenFullscreen hides the widget for the whole game, so there is
        // no FPS row to fill. Measuring anyway would spend a child process and
        // an ETW trace session on a number nobody can see - which is the exact
        // overhead that setting is chosen to avoid.
        if (_settings.MeasureFps && !_settings.PauseWhenFullscreen)
            _fps.Start(pid, _settings.PresentMonPath);

        // Placed by this game's window, so it lands on the monitor being played on.
        _overlayAnchor = IntPtr.Zero;
        UpdateOverlay();

        // Start the peaks over, so they describe this game rather than
        // everything that has happened since logon. Only for a new session:
        // clearing them on the way back from an Alt+Tab would leave both the
        // panel and the history reporting the worst of the stretch played after
        // tabbing back, as though the hour before it had never happened.
        if (newSession)
        {
            foreach (var row in _rows)
                row.ResetPeak();

            // Hand memory back right when the game wants it - once per game,
            // not on every return from Alt+Tab: it is a full blocking,
            // compacting collection, and repeating it each time just has the
            // pages fault straight back in.
            TrimMemory();
        }
        RefreshVisibleItems();
        UpdateTooltip();

        // Bring up this game's notepad. ShowFor deliberately does not activate
        // the window: taking focus here would tab the player out of the game
        // they just launched.
        if (_settings.AutoOpenNotesWithGame)
            Notes().ShowFor(_gameName);
        else
            _notes?.ShowFor(_gameName);   // already open: at least switch it over
    }

    /// <summary>
    /// A readable name for the game, preferring what the publisher put in the
    /// executable ("The Witcher 3: Wild Hunt") over the file name ("witcher3").
    /// Falls back through the window title to the process name, since neither
    /// version info nor a window title is guaranteed to exist.
    /// </summary>
    private static string? FriendlyName(Process process)
    {
        try
        {
            string? description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                return Shorten(description);
        }
        catch
        {
            // Protected or cross-architecture process; the fallbacks still work.
        }

        try
        {
            string title = process.MainWindowTitle;
            if (!string.IsNullOrWhiteSpace(title))
                return Shorten(title);
        }
        catch { }

        try
        {
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps a long name from stretching the widget. The title also trims with
    /// an ellipsis in the UI; this just stops anything absurd reaching it.
    /// </summary>
    private static string Shorten(string value)
    {
        value = value.Trim();
        return value.Length <= 32 ? value : value[..31].TrimEnd() + "…";
    }

    /// <summary>
    /// Gives up tracking. Deliberately says nothing to the history: this runs
    /// every time you tab out of a game, and writing a line here is exactly how
    /// one evening used to end up as a column of sessions in the notes.
    /// </summary>
    private void StopTrackingGame(bool refresh = true)
    {
        // Before _fps.Stop(), which forgets the frame counts this session's
        // average is built from.
        HarvestFrames();
        _fps.Stop();

        _gameProcess?.Dispose();
        _gameProcess = null;
        _gameName = null;
        _gamePid = 0;
        _inGame = false;
        UpdateDrift();
        _gameListed = false;
        _namedPid = 0;   // the game may have exited, freeing its pid - see UpdateGameState
        UpdateGameTitle();
        ApplyOpacity();
        ApplyLayout();

        _overlayAnchor = IntPtr.Zero;
        UpdateOverlay();   // goes with the game

        // A session whose process could never be opened cannot be watched for
        // an exit, so tracking letting go of it is the only end it will ever
        // get. Without this it would sit there open and swallow the next one.
        if (_sessionPid != 0 && _sessionProcess is null)
            FinishSession();

        // Back to the general notes now that no game is running. SwitchTo, not
        // ShowFor: the notes that were open should change, but a closed window
        // must not spring open just because a game exited.
        _notes?.SwitchTo(null);

        if (refresh)
        {
            RefreshVisibleItems();
            UpdateTooltip();
        }
    }

    /// <summary>
    /// What follows the GAME heading: the game and how long it has been played
    /// in all - "Dawnwalker (playtime: 13.3 hours)" - or N/A with no game.
    ///
    /// The time is the history plus the session running now, so it counts up
    /// while you play instead of jumping when you quit. Cheap enough to call
    /// every tick: SetSuffix only redraws when the text actually changed, which
    /// in tenths of an hour is every six minutes.
    /// </summary>
    private void UpdateGameTitle()
    {
        string title = NoGameTitle;

        if (_inGame && _gameName is { } name)
        {
            // The name alone for something in front of the game whose session
            // is open - an overlay - rather than "(playtime: 0 min)": it has no
            // session of its own, so zero would be a made-up figure.
            // The name alone, too, while the playtime is still being looked up.
            title = _sessionPid == _gamePid && _sessionPlayedKnown
                ? $"{name} (playtime: {GamePlaytime.Describe(_sessionPlayedBefore + (DateTime.Now - _sessionStartedAt))})"
                : name;
        }

        foreach (var section in _gameSections)
            section.SetSuffix(title);
    }

    /// <summary>
    /// Opens a session for <paramref name="pid"/>, or keeps the one already
    /// open for it. True when this is a new one.
    ///
    /// The pid test is the whole point. Tracking is taken up again every time
    /// you come back to a game, so this runs many times over one evening, and
    /// before sessions were told apart from tracking each of those runs ended
    /// up as its own summary line - every one of them timing nothing but the
    /// stretch the panel happened to be watching.
    /// </summary>
    private bool BeginOrResumeSession(int pid, string? game)
    {
        // Not while it waits after an exit: Windows reuses pids, and a new
        // program given the old number is not the game coming back.
        if (_sessionPid == pid && _sessionExitedAt is null)
            return false;   // the same game, back in front

        // The same game under a new pid - it restarted itself, or a launcher
        // handed over to it. The session carries on with the new process; the
        // minute it took to restart counts as played.
        if (_sessionPid != 0 && game is not null
            && string.Equals(game, _sessionGame, StringComparison.OrdinalIgnoreCase))
        {
            TakeBackLoggedLine();
            WatchSessionProcess(pid);
            return false;
        }

        // Something else fullscreen in front while the game still runs behind
        // it - an overlay such as NVIDIA's Alt+Z, Game Bar, a window tabbed to.
        // It used to end the session there and then, and tabbing back to the
        // game started a second one. The session ends when the game does; the
        // newcomer is shown on the widget but not logged meanwhile.
        if (_sessionPid != 0 && _sessionProcess is not null && _sessionExitedAt is null)
            return false;

        FinishSession();    // a different game: whatever was open is over

        _sessionPid = pid;
        _sessionGame = game;
        _sessionStartedAt = DateTime.Now;
        _sessionStats = new FrameStats();
        _sessionEnergyWh = null;
        _lastEnergyAt = default;
        _sessionDriver = _sessionBuild = null;

        // Off the tick: the registry read is quick, but the tick is what the
        // game's first seconds are measured on. And the look at what else is
        // running, a while in - see CheckAlongsideAsync.
        _ = ReadSessionVersionsAsync(_sessionStartedAt);
        if (game is not null && _settings.GameStartCheck)
            _ = CheckAlongsideAsync(game, pid, _sessionStartedAt);

        // Read once per session: the log can't gain a line for this game while
        // its own session is still the one open, and Steam only adds this
        // session to its count when the game closes. The same choice of source
        // as the session history makes - see GamePlaytime.
        _sessionPlayedBefore = TimeSpan.Zero;
        _sessionPlayedKnown = game is null;
        if (game is not null)
            _ = LookUpPlayedBeforeAsync(game, pid);

        WatchSessionProcess(pid);
        return true;
    }

    /// <summary>
    /// Counts lookups, so one finishing after its session has been replaced
    /// by another doesn't put the old game's hours on the new game's heading.
    /// </summary>
    private int _playedLookup;

    /// <summary>
    /// Whether <see cref="_sessionPlayedBefore"/> holds the looked-up figure
    /// yet. Until it does, the heading shows the game's name alone: "(playtime:
    /// 2 min)" for a game with 140 hours would be a wrong figure for a moment.
    /// </summary>
    private bool _sessionPlayedKnown;

    /// <summary>
    /// The new session's game's playtime, looked up on a worker thread. Done
    /// inline, it parsed every Steam manifest in the library, queried GOG
    /// Galaxy's database and read the save files on the UI thread - inside the
    /// polling tick, and so freezing every DeskMon window just as the game
    /// started. The ids it finds are written to the settings back here, on the
    /// UI thread they belong to.
    /// </summary>
    private async Task LookUpPlayedBeforeAsync(string game, int pid)
    {
        int ticket = ++_playedLookup;
        try
        {
            var found = await Task.Run(() => GamePlaytime.Look(game, _settings, _sessions, pid));
            GamePlaytime.Remember(game, _settings, found.SteamFromProcess, found.GogFromProcess);

            if (ticket != _playedLookup || !string.Equals(_sessionGame, game, StringComparison.OrdinalIgnoreCase))
                return;

            _sessionPlayedBefore = found.Playtime.Time;
            _sessionPlayedKnown = true;
            UpdateGameTitle();
        }
        catch (Exception ex)
        {
            // Only the heading's figure is lost; the session itself is logged regardless.
            App.Log("looking up playtime", ex);
        }
    }

    /// <summary>
    /// The driver and Windows build for the session's line, read on a worker
    /// thread. <paramref name="session"/> is the session's start, so a read
    /// that finishes after its session has been replaced is dropped rather
    /// than put on the next one.
    /// </summary>
    private async Task ReadSessionVersionsAsync(DateTime session)
    {
        try
        {
            string? gpu = _hub?.GpuName;
            var (driver, build) = await Task.Run(() => Hardware.SystemInfo.QuickVersions(gpu));
            if (session != _sessionStartedAt || _sessionPid == 0)
                return;

            // Spaces out: a part of the line is split at " · ", and read back
            // as one word after "driver" - see SessionRecord.
            _sessionDriver = driver?.Replace(" ", string.Empty);
            _sessionBuild = build?.Replace(" ", string.Empty);
        }
        catch (Exception ex)
        {
            App.Log("reading the driver for a session", ex);   // the line goes without them
        }
    }

    /// <summary>How long into a session before looking at what else is running: past the game's own loading.</summary>
    private static readonly TimeSpan AlongsideCheckAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Once per session, a while in: one quiet notification if something heavy
    /// is running beside the game - a browser holding gigabytes, Windows Update
    /// or a Defender scan eating CPU - since that is the usual answer to "why
    /// does it stutter tonight". Nothing at all when nothing stands out.
    /// See GameStartCheck for what counts.
    /// </summary>
    private async Task CheckAlongsideAsync(string game, int pid, DateTime session)
    {
        try
        {
            await Task.Delay(AlongsideCheckAfter);
            if (session != _sessionStartedAt || _sessionPid == 0)
                return;   // the game has gone, or another took its place

            var found = await Task.Run(() => Sensors.GameStartCheck.Run(_sessionPid));
            if (session != _sessionStartedAt || found.Count == 0)
                return;

            _tray?.Notify("DeskMon", $"Running alongside {game}: {string.Join("  ·  ", found)}", warning: false);
        }
        catch (Exception ex)
        {
            App.Log("checking what runs beside the game", ex);
        }
    }

    /// <summary>
    /// Adds this tick's CPU and GPU power to the session's energy: watts times
    /// the real time since the last reading, so the slower in-game interval
    /// counts as much as it should. Only while the game is running - not the
    /// minute an exited game is waited on - and a gap of three missed readings
    /// (the PC locked or asleep) is not filled in with a guess.
    /// </summary>
    private void AddSessionEnergy(SensorSnapshot snapshot)
    {
        if (_sessionPid == 0 || _sessionExitedAt is not null)
            return;

        var now = DateTime.Now;
        var since = now - _lastEnergyAt;
        _lastEnergyAt = now;

        if (snapshot.CpuPower is null && snapshot.GpuPower is null)
            return;

        // A gap is three readings missed, and never under 10 s: a fixed 10 s
        // skipped every reading for anyone with the game interval set that
        // slow, and the session logged no energy at all.
        var gap = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(10).Ticks, _timer.Interval.Ticks * 3));
        if (since <= TimeSpan.Zero || since > gap)
            return;   // the first reading, or after a gap: only a start point

        double watts = (snapshot.CpuPower ?? 0) + (snapshot.GpuPower ?? 0);
        _sessionEnergyWh = (_sessionEnergyWh ?? 0) + watts * since.TotalHours;
    }

    /// <summary>
    /// Points the open session at <paramref name="pid"/>: a new session's
    /// game, or the same game come back as a new process.
    ///
    /// A handle of the session's own, because tracking disposes its handle the
    /// moment you tab out - and the exit this one is held for usually happens
    /// long after that.
    /// </summary>
    private void WatchSessionProcess(int pid)
    {
        _sessionProcess?.Dispose();
        _sessionPid = pid;
        _sessionExitedAt = null;

        try
        {
            _sessionProcess = Process.GetProcessById(pid);
        }
        catch
        {
            _sessionProcess = null;   // see the fallback in StopTrackingGame
        }
    }

    /// <summary>
    /// Ends the open session and writes its line. Does nothing when there is
    /// none, so callers never have to check.
    /// </summary>
    /// <param name="note">
    /// Added to the line when the session is being closed for some reason other
    /// than the game quitting, so that a short line is not read months later as
    /// a short evening.
    /// </param>
    private void FinishSession(string? note = null)
    {
        if (_sessionPid == 0)
            return;

        // Ended properly, logged or not: there is nothing left to recover.
        _sessions.ClearOpenSession();
        _lastCheckpoint = default;

        // Whatever the monitor still holds belongs to this session - usually
        // nothing, since tracking normally stops first, but quitting straight
        // out of a game ends the session while PresentMon is still counting.
        HarvestFrames();

        string? game = _sessionGame;
        var started = _sessionStartedAt;

        // The game's own exit time in preference to "now": a game quit while
        // the machine was locked is only noticed when polling resumes, and the
        // hours in between were not time played. Nor were the minutes spent
        // waiting to see whether it would restart.
        var ended = _sessionExitedAt ?? ExitTimeOf(_sessionProcess) ?? DateTime.Now;

        // A game that had already quit was not cut short by DeskMon closing
        // while its session waited out RelaunchGrace.
        if (_sessionExitedAt is not null)
            note = null;
        _sessionExitedAt = null;

        var frames = _sessionStats;
        var extras = SessionExtras();

        _sessionProcess?.Dispose();
        _sessionProcess = null;
        _sessionGame = null;
        _sessionPid = 0;
        _sessionStats = new FrameStats();
        _sessionEnergyWh = null;
        _sessionDriver = _sessionBuild = null;

        // Written when the game quit, and it never came back - see
        // LogExitedSession. A second line now would log the session twice.
        bool alreadyLogged = _loggedLine is not null;
        _loggedLine = null;
        if (alreadyLogged)
            return;

        if (!_settings.LogSessionToFile || game is null)
            return;   // nothing to file it under, or switched off

        if (ended - started < ShortestLoggedSession)
            return;

        _sessions.Append(game, DescribeSession(started, ended, frames, extras, note));
        RefreshAfterSession();
    }

    /// <summary>
    /// The line in the log for the session waiting out RelaunchGrace, written
    /// when its game quit; null while the game runs, or if nothing was written.
    /// Kept so the game coming back can take it out again.
    /// </summary>
    private string? _loggedLine;

    /// <summary>
    /// Writes the session's line the moment its game is seen to have quit,
    /// rather than once RelaunchGrace is over: three minutes of an empty
    /// history after quitting read as the session not having been logged at
    /// all. The session itself stays open for the grace - see
    /// TakeBackLoggedLine for a game that comes back.
    /// Under FinishSession's rules: nothing when logging is off or the session
    /// is too short - FinishSession then decides as it always did.
    /// </summary>
    private void LogExitedSession()
    {
        if (_sessionGame is null || _sessionExitedAt is not { } ended || !_settings.LogSessionToFile)
            return;
        if (ended - _sessionStartedAt < ShortestLoggedSession)
            return;

        // What HarvestFrames would add, without taking it, as CheckpointSession
        // does: harvesting here and again when tracking stops would count the
        // game's frames twice.
        var frames = _sessionStats.Copy();
        if (_gamePid == _sessionPid)
            frames.Add(_fps.SessionTotals);

        string line = DescribeSession(_sessionStartedAt, ended, frames, SessionExtras(), null);
        if (_sessions.Append(_sessionGame, line) is not null)
            return;   // not written: FinishSession tries again at the end of the grace

        _loggedLine = line;

        // Its line is in the log now; a recovery after a crash would add it again.
        _sessions.ClearOpenSession();
        RefreshAfterSession();
    }

    /// <summary>
    /// The game came back within RelaunchGrace, so the session carries on:
    /// the line written when it quit comes out of the log, and one line for
    /// the whole stretch is written when it really ends.
    /// </summary>
    private void TakeBackLoggedLine()
    {
        if (_loggedLine is not { } line || _sessionGame is null)
            return;

        _loggedLine = null;
        if (SessionRecord.Parse(line) is { } logged && _sessions.Delete(_sessionGame, logged) is { } problem)
            App.Log("taking back a session line", problem);
        RefreshAfterSession();
    }

    /// <summary>
    /// The notepad shows the last session under the notes, and the history
    /// window lists them, so both are redrawn whenever the log changes.
    ///
    /// Guarded, because this runs inside the polling tick, and an error on
    /// the UI thread shuts DeskMon down: a fault in redrawing either window
    /// must not take the widget away - perhaps in the middle of the next
    /// game. The line is already written; the windows catch up on reopening.
    /// </summary>
    private void RefreshAfterSession()
    {
        try
        {
            _notes?.RefreshLastSession();
            _sessionsWindow?.Refresh();
        }
        catch (Exception ex)
        {
            App.Log("refreshing after a session", ex);
        }
    }

    /// <summary>
    /// Saves the open session's line as it would read if the session ended
    /// now, so that DeskMon stopping without closing - a crash, a power cut,
    /// being killed - costs at most the last minute rather than the evening.
    /// The next start writes it into the log; see
    /// <see cref="SessionHistory.RecoverOpenSession"/>.
    ///
    /// Under the rules FinishSession would apply: nothing for a session too
    /// short to be logged, and the game's own exit time once it has quit and
    /// the session is only waiting to see whether it comes back.
    /// </summary>
    private void CheckpointSession()
    {
        // Nor once its line is in the log: recovered after a crash, the saved
        // copy would put the session there twice.
        if (_sessionPid == 0 || _sessionGame is null || !_settings.LogSessionToFile || _loggedLine is not null)
            return;

        var now = DateTime.Now;
        if (now - _lastCheckpoint < CheckpointEvery)
            return;

        var ended = _sessionExitedAt ?? now;
        if (ended - _sessionStartedAt < ShortestLoggedSession)
            return;

        _lastCheckpoint = now;

        // What HarvestFrames would add, without taking it: the monitor's
        // counts must still be there for the real harvest later.
        var frames = _sessionStats.Copy();
        if (_gamePid == _sessionPid)
            frames.Add(_fps.SessionTotals);

        // A game that had already quit wasn't cut short by anything.
        string? note = _sessionExitedAt is null ? "cut short, DeskMon stopped unexpectedly" : null;

        _sessions.SaveOpenSession(_sessionGame, DescribeSession(_sessionStartedAt, ended, frames, SessionExtras(), note));
    }

    /// <summary>What the line carries besides the frames and peaks: the energy, the driver and the build.</summary>
    private sealed record SessionExtrasInfo(double? EnergyWh, string? Driver, string? WindowsBuild);

    private SessionExtrasInfo SessionExtras() => new(_sessionEnergyWh, _sessionDriver, _sessionBuild);

    /// <summary>
    /// Adds what PresentMon has counted so far to the session's running totals,
    /// because <see cref="FpsMonitor.Stop"/> forgets them - and a session
    /// interrupted by an Alt+Tab is measured in several runs.
    ///
    /// Harmless to call twice: the counters are cleared with the chains, so a
    /// second look at a stopped monitor adds nothing.
    /// </summary>
    private void HarvestFrames()
    {
        // Only frames from the session's own game: PresentMon may be watching
        // an overlay or another window in front of it, which the session now
        // outlasts, and its frame rate is not the game's.
        if (_sessionPid == 0 || _gamePid != _sessionPid)
            return;

        _sessionStats.Add(_fps.SessionTotals);
    }

    /// <summary>
    /// Whether the game whose session is open has gone. Only ever asked with a
    /// handle in hand; without one there is nothing to ask, and the answer "it
    /// exited" would end every session on the next tick.
    /// </summary>
    private bool SessionProcessExited()
    {
        try
        {
            return _sessionProcess is null || _sessionProcess.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>When the process ended, or null while it is still running.</summary>
    private static DateTime? ExitTimeOf(Process? process)
    {
        try
        {
            // ExitTime throws on a live process, so HasExited has to come first.
            return process is { HasExited: true } ? process.ExitTime : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The one line that records a session: when it ran, for how long, its
    /// average frame rate, and the worst figure every row saw.
    ///
    /// The peaks need no extra bookkeeping - they are reset when a session
    /// starts and left alone on a return from Alt+Tab, so they already describe
    /// this session and nothing else. The frame rate does, because the panel
    /// only ever shows a rolling window; see <see cref="HarvestFrames"/>.
    ///
    /// Every row is included rather than a chosen few, so a metric added to
    /// MetricRegistry turns up here too without this method knowing about it.
    /// </summary>
    private string DescribeSession(DateTime started, DateTime ended, FrameStats frames, SessionExtrasInfo extras, string? note)
    {
        var line = new StringBuilder();
        // Invariant like the figures below: in a format string ':' means the
        // culture's time separator, and a culture that uses '.' would write a
        // line SessionRecord.Parse can't read back.
        line.Append(FormattableString.Invariant($"{started:yyyy-MM-dd HH:mm}–{ended:HH:mm} · {Describe(ended - started)}"));

        // Frame times, averaged and then inverted - not an average of rates;
        // the same reasoning as FpsMonitor's percentiles.
        if (frames.AverageFps is double average)
            line.Append($" · avg {average:0} fps");

        // Over the whole session, unlike the LOW and WORST peaks further on,
        // which are the lowest the widget's last-25-seconds figure dipped to -
        // and so mostly a loading screen. These are what the performance
        // score is made of; see PerformanceScore.
        if (frames.OnePercentLow is double low)
            line.Append(FormattableString.Invariant($" · 1% low {low:0} fps"));
        if (frames.PointOnePercentLow is double lowest)
            line.Append(FormattableString.Invariant($" · 0.1% low {lowest:0} fps"));
        if (frames.JitterMs is double jitter)
            line.Append(FormattableString.Invariant($" · jitter {jitter:0.0} ms"));

        // The hitches the average and the lows can hide - see FrameStats.Stutters.
        if (frames.Stutters is long stutters)
            line.Append(FormattableString.Invariant($" · stutters {stutters}"));

        // CPU and GPU only, not the PC from the wall - the LOG window says so.
        if (extras.EnergyWh is double energy)
            line.Append(FormattableString.Invariant($" · energy {energy:0} Wh"));

        // So a session played on a new driver or after a Windows update can be
        // marked against the one before - see SessionsWindow's change marks.
        if (extras.Driver is { } driver)
            line.Append($" · driver {driver}");
        if (extras.WindowsBuild is { } build)
            line.Append($" · windows {build}");

        foreach (var row in _rows)
        {
            if (row.WorstText is not { } worst)
                continue;   // nothing was ever read for this one

            line.Append($" · {row.Label} {(row.LowerIsWorse ? '↓' : '↑')}{worst}");
        }

        if (note is not null)
            line.Append($" · {note}");

        return line.ToString();
    }

    /// <summary>"2 h 14 min", or just minutes below an hour.</summary>
    private static string Describe(TimeSpan length)
        => length.TotalHours >= 1
            ? $"{(int)length.TotalHours} h {length.Minutes} min"
            : $"{(int)length.TotalMinutes} min";

    /// <summary>
    /// The version label's colour and size. Separate from the text, which never
    /// changes, because both of these follow settings that can - so this runs
    /// again when they do.
    ///
    /// Header colour and a size below the rows': the same treatment the peak
    /// figures get, for the same reason. It is reference, not a reading.
    /// </summary>
    private void ApplyVersionStyle()
    {
        // Lit, like the toolbar toggles, while its window is open or the mouse
        // is on it - so it reads as something to click, not only a label.
        bool lit = VersionText.IsMouseOver || _versions is { IsVisible: true };
        VersionText.Foreground = lit ? _theme.Section : _theme.Header;
        VersionText.FontSize = Math.Max(8, _settings.FontSize - 2.5);

        // The clock is the version number's partner at the other corner.
        ClockText.Foreground = _theme.Header;
        ClockText.FontSize = VersionText.FontSize;
    }

    /// <summary>Fires on each new minute - see UpdateClock.</summary>
    private readonly DispatcherTimer _clockTimer = new();

    /// <summary>
    /// Shows the time and sets the timer for the next whole minute. Its own
    /// timer, not the polling tick: that one slows down in games, stops while
    /// paused and runs at whatever interval is set, so the minute would turn
    /// over up to seconds late. English day names, as the rest of the widget.
    /// </summary>
    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("dddd - yyyy-MM-dd - HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // A few milliseconds past the minute, so a timer that fires a hair
        // early still lands on the new one rather than showing the old again.
        _clockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond)
                               + TimeSpan.FromMilliseconds(50);
    }

    /// <summary>
    /// The version number opens and closes the versions window, as the VER
    /// button did. Handled here so the press doesn't also start dragging the
    /// widget, which every other part of the panel does.
    /// </summary>
    private void VersionText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ToggleWindow(_versions, ShowVersions);
        e.Handled = true;
    }

    private void VersionText_HoverChanged(object sender, MouseEventArgs e) => ApplyVersionStyle();

    /// <summary>
    /// Picks the opacity the current state calls for. Called on every change of
    /// <c>_inGame</c> rather than set once at startup, because the two states
    /// want different things from the panel and one value cannot serve both.
    ///
    /// Clamped, since a hand-edited 0 would leave the widget invisible and
    /// unclickable for a whole game - which reads as a bug, not as a setting.
    /// Snapped rather than faded: an animation would be re-composited over the
    /// game for its whole duration, and this happens as a game starts, which is
    /// the worst moment to ask anything of the GPU.
    /// </summary>
    private void ApplyOpacity()
    {
        Opacity = Math.Clamp(_inGame ? _settings.GameOpacity : _settings.Opacity, 0.1, 1.0);

        // The other windows fade back with the widget: they are sitting over
        // the same game. Unlike the widget they can be focused, and each lifts
        // its own dimming while it is the window being used - see
        // PanelWindow.DimDuringGames.
        foreach (var window in PanelWindows())
            window.SetGameRunning(_inGame);
    }

    /// <summary>
    /// The extra windows that exist at this moment. They are created on first
    /// use, so most of the time this is one or none.
    /// </summary>
    private IEnumerable<PanelWindow> PanelWindows()
    {
        if (_notes is not null) yield return _notes;
        if (_pictures is not null) yield return _pictures;
        if (_hardware is not null) yield return _hardware;
        if (_versions is not null) yield return _versions;
        if (_sessionsWindow is not null) yield return _sessionsWindow;
        if (_settingsWindow is not null) yield return _settingsWindow;
    }

    private void UpdateTooltip()
    {
        string text;

        if (!_inGame)
        {
            text = _baseTooltip;
        }
        else
        {
            string target = _gameName is null ? $"pid {_gamePid}" : $"{_gameName} (pid {_gamePid})";
            text = _fps.Error is { } error
                ? $"Measuring {target}\n\nFPS unavailable: {error}"
                : $"Measuring {target}";
        }

        // In both states: the missing programs are why rows read n/a, on the
        // desktop as much as in a game.
        if (_missingNote is not null)
            text += "\n\n" + _missingNote;

        if (_hotkeyNote is not null)
            text += "\n\n" + _hotkeyNote;

        if (_overlayHotkeyNote is not null)
            text += "\n\n" + _overlayHotkeyNote;

        Panel.ToolTip = text;
    }

    // --- FPS overlay ------------------------------------------------------

    /// <summary>
    /// Shows the overlay over a game that is being measured, hides it
    /// otherwise, and refreshes its figure. Runs on its own timer while it is
    /// up, and whenever a game, a setting or the hotkey changes what it should
    /// be doing - so it never needs to know which of those it was.
    ///
    /// Not over a game that isn't measured: with MeasureFps off, or the widget
    /// set to hide during games (which skips measuring), it would only ever
    /// show a dash. It doesn't depend on the widget being on screen, though -
    /// hiding the widget from the tray and keeping just the big figure is a
    /// sensible way to play.
    /// </summary>
    private void UpdateOverlay()
    {
        bool wanted = _settings.ShowFpsOverlay
                      && _inGame
                      && _settings.MeasureFps
                      && !_settings.PauseWhenFullscreen;

        if (!wanted)
        {
            _overlayTimer.Stop();
            _overlay?.Hide();
            return;
        }

        // Kept running while hidden for a tab-out, so it is back within half a
        // second of returning to the game rather than at the next tick.
        if (!_overlayTimer.IsEnabled)
            _overlayTimer.Start();

        if (!GameInFront())
        {
            _overlay?.Hide();
            return;
        }

        _overlay ??= new FpsOverlayWindow(_settings, _theme);
        _overlay.SetFps(_fps.CurrentFps);   // not Current: that also sorts for the lows
        _overlay.ShowOn(OverlayAnchor());
    }

    /// <summary>
    /// Whether the game is the window being used, for the overlay - which is
    /// topmost, and so would otherwise float over whatever was tabbed to: a
    /// browser on the game's monitor, a chat window over a windowed game.
    ///
    /// DeskMon's own windows count as the game: the notepad opened over it is
    /// part of playing, and the overlay staying up there is what is wanted.
    /// Nothing in front at all - Windows has that for a moment while switching
    /// - counts too, so an Alt+Tab doesn't blink it out and back.
    /// </summary>
    private bool GameInFront()
    {
        IntPtr front = GetForegroundWindow();
        if (front == IntPtr.Zero)
            return true;

        GetWindowThreadProcessId(front, out uint pid);
        return pid == _gamePid || pid == _ownPid;
    }

    /// <summary>
    /// The game's window, looked up again only once the one held has gone - a
    /// game that recreates its window on a mode switch, say.
    ///
    /// Failing that, the window in front - but only if it is the game's. The
    /// overlay can also be switched on from the settings window or its hotkey,
    /// and what is in front then may be anything: taking it unchecked put the
    /// overlay in the settings window's corner, for good, since that window is
    /// only ever hidden. With neither, zero - the primary monitor's corner -
    /// and it is looked for again on the next refresh.
    /// </summary>
    private IntPtr OverlayAnchor()
    {
        if (_overlayAnchor != IntPtr.Zero && IsWindow(_overlayAnchor))
            return _overlayAnchor;

        _overlayAnchor = TrackedGameWindow();

        if (_overlayAnchor == IntPtr.Zero)
        {
            IntPtr front = GetForegroundWindow();
            GetWindowThreadProcessId(front, out uint pid);

            if (_gamePid != 0 && pid == _gamePid)
                _overlayAnchor = front;
        }

        return _overlayAnchor;
    }

    /// <summary>
    /// The hotkey. Saved, like "Always on top", so the overlay stays the way
    /// it was left across a restart. Pressed outside a game it still flips the
    /// setting; the next game shows the result.
    /// </summary>
    private void ToggleFpsOverlay()
    {
        _settings.ShowFpsOverlay = !_settings.ShowFpsOverlay;
        _settings.Save();
        _settingsWindow?.RefreshValues();
        UpdateOverlay();
    }

    // --- Position ---------------------------------------------------------

    private void RestorePosition()
    {
        // Default spot: top-right of the primary work area.
        var work = SystemParameters.WorkArea;
        double left = _settings.Left ?? work.Right - ActualWidth - 24;
        double top = _settings.Top ?? work.Top + 24;

        // A monitor may have been unplugged since the position was saved.
        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxLeft = minLeft + SystemParameters.VirtualScreenWidth - ActualWidth;
        double maxTop = minTop + SystemParameters.VirtualScreenHeight - ActualHeight;

        Left = maxLeft > minLeft ? Math.Clamp(left, minLeft, maxLeft) : minLeft;
        Top = maxTop > minTop ? Math.Clamp(top, minTop, maxTop) : minTop;
    }

    /// <summary>
    /// The whole panel is the drag handle, and the section headings are also
    /// buttons, so the two have to be told apart.
    ///
    /// A heading never moves the widget. Pressing one starts watching the
    /// mouse instead: let go where you pressed and it was a click, which folds
    /// the section; move it up or down first and it was a drag, which moves
    /// the section - see <see cref="Panel_MouseMove"/>. That can be told apart
    /// here, unlike for the widget itself, because the mouse is followed by
    /// hand rather than by DragMove, which nudges the window by a pixel or two
    /// even when the cursor holds still. Headings are thin strips and the rest
    /// of the panel still drags the widget.
    /// </summary>
    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindSection(e.OriginalSource) is { } heading)
        {
            _pressedSection = heading;
            _pressedAt = e.GetPosition(Panel);

            // So the move and the release still come here when the mouse
            // leaves the heading - or the widget - on the way.
            Panel.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Same bargain as a heading, for the same reason: a row that unfolds a
        // list cannot also be a drag handle. Only rows that actually have a
        // list are affected - every other row still drags.
        if (FindExpandable(e.OriginalSource) is { } expandable)
        {
            ToggleMetric(expandable);
            e.Handled = true;
            return;
        }

        if (_settings.Locked)
            return;

        try
        {
            DragMove();   // blocks until the button is released
        }
        catch (InvalidOperationException)
        {
            return;       // button was already up; nothing moved
        }

        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    // --- Moving a section by its heading ----------------------------------

    /// <summary>The heading pressed and not yet let go of; null otherwise.</summary>
    private SectionRow? _pressedSection;

    /// <summary>Where it was pressed, in the panel's coordinates.</summary>
    private Point _pressedAt;

    /// <summary>The press has moved far enough to be a drag rather than a click.</summary>
    private bool _draggingSection;

    /// <summary>
    /// Moves the pressed section as the mouse goes - live, so the panel itself
    /// shows where it will land, rather than drawing a line to aim at.
    ///
    /// Only once the mouse has moved Windows' drag distance from where it was
    /// pressed: a hand never holds perfectly still, and a click that wobbled
    /// by a pixel must still fold the section, not move it nowhere.
    /// </summary>
    private void Panel_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedSection is not { } moving || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (!_draggingSection)
        {
            var at = e.GetPosition(Panel);
            if (Math.Abs(at.Y - _pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance
                && Math.Abs(at.X - _pressedAt.X) < SystemParameters.MinimumHorizontalDragDistance)
                return;

            _draggingSection = true;
            moving.IsDragging = true;
            Mouse.OverrideCursor = Horizontal ? Cursors.SizeWE : Cursors.SizeNS;
        }

        // Across when the sections are side by side, down when stacked.
        var position = e.GetPosition(RowsHost);
        DragSectionTo(moving, Horizontal ? position.X : position.Y);
    }

    /// <summary>A click folds the section; the end of a drag keeps the order it was left in.</summary>
    private void Panel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedSection is not { } section)
            return;

        bool dragged = _draggingSection;
        EndSectionDrag();

        if (!dragged)
            ToggleSection(section);

        e.Handled = true;
    }

    /// <summary>
    /// The capture can be taken away without a release - another window
    /// popping up, Alt+Tab. The section stays where it was dragged to.
    /// </summary>
    private void Panel_LostMouseCapture(object sender, MouseEventArgs e) => EndSectionDrag();

    /// <summary>
    /// Stops following the mouse, and saves the order if it changed. Clears
    /// the state before letting go of the capture, since letting go raises
    /// LostMouseCapture, which comes straight back here.
    /// </summary>
    private void EndSectionDrag()
    {
        // LostMouseCapture bubbles, so every toolbar button letting go of the
        // mouse after a click comes through here too - with nothing to end.
        if (_pressedSection is not { } section)
            return;

        bool dragged = _draggingSection;

        _pressedSection = null;
        _draggingSection = false;

        section.IsDragging = false;

        if (dragged)
            Mouse.OverrideCursor = null;   // only ever set by a drag

        if (Panel.IsMouseCaptured)
            Panel.ReleaseMouseCapture();

        if (dragged)
            _settings.Save();   // SectionOrder, once - not on every step of the drag
    }

    /// <summary>
    /// Puts the dragged section where the mouse is: after every other section
    /// whose middle is above the mouse, before the rest.
    ///
    /// Measured against the middles rather than the edges, and with the
    /// dragged section itself left out, so a short section dragged past a tall
    /// one doesn't swap back and forth: once it has moved, the mouse has to go
    /// back past the other one's middle - its new middle - to move it again.
    /// </summary>
    /// <param name="at">Where the mouse is along the way the sections run: down when stacked, across when side by side.</param>
    private void DragSectionTo(SectionRow moving, double at)
    {
        var sections = VisibleSectionBounds();
        int current = sections.FindIndex(s => s.Section == moving);
        if (current < 0)
            return;

        var others = sections.Where(s => s.Section != moving).ToList();
        int index = others.Count(s => (s.Start + s.End) / 2 < at);

        SectionRow? before = index < others.Count ? others[index].Section : null;
        SectionRow? nowBefore = current + 1 < sections.Count ? sections[current + 1].Section : null;

        if (before != nowBefore)
            MoveSection(moving, before);
    }

    /// <summary>
    /// Each section on screen with the stretch of the panel it covers, along
    /// the way the sections run: stacked, from the top of its heading to the
    /// bottom of its last visible row; side by side, its column's left and
    /// right edges. In the order drawn.
    /// </summary>
    private List<(SectionRow Section, double Start, double End)> VisibleSectionBounds()
    {
        var bounds = new List<(SectionRow Section, double Start, double End)>();
        bool across = Horizontal;

        foreach (var item in _visibleItems)
        {
            if (RowsHost.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement box)
                continue;

            var corner = box.TranslatePoint(new Point(0, 0), RowsHost);
            double start = across ? corner.X : corner.Y;
            double end = start + (across ? box.ActualWidth : box.ActualHeight);

            // A row belongs to the section above it; side by side every row
            // is as wide as its column, so taking the furthest edge is the same.
            if (item is SectionRow section)
                bounds.Add((section, start, end));
            else if (bounds.Count > 0)
                bounds[^1] = (bounds[^1].Section, bounds[^1].Start, Math.Max(bounds[^1].End, end));
        }

        return bounds;
    }

    /// <summary>
    /// Moves a section, with all its rows, to just before <paramref name="before"/>
    /// - or to the end with null - and redraws. The order is written into the
    /// settings here but saved when the drag ends.
    ///
    /// Works on the whole list, not just what is on screen: a game section
    /// hidden while no game runs keeps its place relative to the others.
    /// </summary>
    private void MoveSection(SectionRow moving, SectionRow? before)
    {
        // The list as groups: each heading with every row that hangs off it,
        // detail rows included. The hardware names belong to no section and
        // stay on top.
        var loose = new List<PanelItem>();
        var groups = new List<List<PanelItem>>();

        foreach (var entry in _allItems)
        {
            if (entry.Item is SectionRow)
                groups.Add(new List<PanelItem> { entry });
            else if (entry.Section is { } owner && groups.Find(g => g[0].Item == owner) is { } group)
                group.Add(entry);
            else
                loose.Add(entry);
        }

        int from = groups.FindIndex(g => g[0].Item == moving);
        if (from < 0)
            return;

        var moved = groups[from];
        groups.RemoveAt(from);

        int to = before is null ? groups.Count : groups.FindIndex(g => g[0].Item == before);
        groups.Insert(to < 0 ? groups.Count : to, moved);

        _allItems.Clear();
        _allItems.AddRange(loose);
        foreach (var group in groups)
            _allItems.AddRange(group);

        _settings.SectionOrder = groups.Select(g => ((SectionRow)g[0].Item).Key).ToList();

        RefreshVisibleItems();
    }

    /// <summary>Walks up from whatever was clicked to the section it belongs to.</summary>
    private static SectionRow? FindSection(object? source)
    {
        for (var element = source as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: SectionRow section })
                return section;
        }

        return null;
    }

    /// <summary>
    /// Walks up from whatever was clicked to a metric row that has a list to
    /// unfold. A row without one is not found, so it keeps dragging the widget.
    /// </summary>
    private static MetricRow? FindExpandable(object? source)
    {
        for (var element = source as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: MetricRow { CanExpand: true } row })
                return row;
        }

        return null;
    }

    private void ToggleSection(SectionRow section)
    {
        section.IsCollapsed = !section.IsCollapsed;

        // Into whichever layout is showing: folding a section mid-game folds
        // it in the game's layout, and leaves the desktop's as it was.
        var collapsed = CollapsedNow;
        collapsed.RemoveAll(key => string.Equals(key, section.Key, StringComparison.OrdinalIgnoreCase));

        if (section.IsCollapsed)
            collapsed.Add(section.Key);

        _settings.Save();
        RefreshVisibleItems();
    }

    /// <summary>
    /// The folded sections of the layout in use: the game's while one runs and
    /// GameLayout is on, the desktop's otherwise.
    /// </summary>
    private List<string> CollapsedNow
        => _inGame && _settings.GameLayout ? _settings.CollapsedSectionsInGame : _settings.CollapsedSections;

    /// <summary>
    /// Folds and unfolds the sections to match the layout in use - called as a
    /// game starts and ends, so the widget turns compact for the game and back
    /// for the desktop by itself.
    /// </summary>
    private void ApplyLayout()
    {
        var collapsed = CollapsedNow;

        foreach (var entry in _allItems)
            if (entry.Item is SectionRow section)
                section.IsCollapsed = collapsed.Contains(section.Key, StringComparer.OrdinalIgnoreCase);

        // Always, not only when a fold changed: which sections are shown at
        // all differs between the two layouts too (HiddenSections).
        RefreshVisibleItems();
    }

    // --- Alerts and updates -------------------------------------------------

    /// <summary>
    /// Says a reading has stayed in the red: in the notification area, and
    /// under the FPS overlay while it is up - over a fullscreen game Windows
    /// holds notifications back, so the overlay is where it can be seen.
    /// </summary>
    private void Alert(string text)
    {
        int seconds = Math.Max(5, _settings.AlertAfterSeconds);
        string held = seconds % 60 == 0
            ? (seconds == 60 ? "a minute" : $"{seconds / 60} minutes")
            : $"{seconds} seconds";

        _tray?.Notify("DeskMon", $"{text} - in the red for over {held}.", warning: true);

        if (_overlay is { IsVisible: true })
            _overlay.ShowAlert(text);
    }

    /// <summary>
    /// Asks for the newest release, and announces one newer than this - once
    /// per version, so the same news isn't repeated every day. Clicking the
    /// notification opens the release page.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates || string.IsNullOrWhiteSpace(_settings.UpdateRepository))
            return;

        var release = await AppUpdate.CheckAsync(_settings.UpdateRepository);
        _versions?.RefreshUpdate();

        if (release is null || release.Version.ToString() == _settings.UpdateAnnounced)
            return;

        _settings.UpdateAnnounced = release.Version.ToString();
        _settings.Save();

        _tray?.Notify("DeskMon update", $"DeskMon {release.Version} is out - you have {AppUpdate.Running}. Click to see what's new.",
                      warning: false, clicked: () => OpenLink(release.Url));
    }

    private static void OpenLink(string url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser to open it in */ }
    }

    private void ToggleMetric(MetricRow row)
    {
        row.IsExpanded = !row.IsExpanded;

        _settings.ExpandedMetrics.RemoveAll(
            key => string.Equals(key, row.Key, StringComparison.OrdinalIgnoreCase));

        if (row.IsExpanded)
            _settings.ExpandedMetrics.Add(row.Key);

        _settings.Save();
        RefreshVisibleItems();
    }

    // --- Context menu -----------------------------------------------------
    //
    // The same commands as the tray menu, which calls the same methods below.

    /// <summary>
    /// The ticks, read fresh each time the menu opens - the tray menu and the
    /// settings window can change both, and this way none of them has to
    /// remember to tell the others.
    /// </summary>
    private void WidgetMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuOnTop.IsChecked = _settings.AlwaysOnTop;
        MenuLocked.IsChecked = _settings.Locked;
    }

    /// <summary>
    /// A program in the RAM or SWAP list: a menu of its own with End, so the
    /// widget that named the culprit can deal with it without Task Manager.
    /// A sensor or drive row has no Key and gets the widget's menu as before.
    ///
    /// Two clicks, like the session history's DELETE: the first turns the
    /// item into "Click again…" in the warning colour, so ending thirty
    /// browser processes is a decision, not a slip of the right hand. The
    /// dark menu's items have no submenus, which is why not "End > Yes".
    /// </summary>
    private void DetailRow_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DetailRow { Key: { } name } } strip)
            return;

        // Handled, so the panel's own menu doesn't open on top of this one.
        e.Handled = true;

        string? why = MemoryUsers.WhyNotEnd(name, out int count);
        string what = count > 1 ? $"End {name} ({count} processes)" : $"End {name}";

        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = strip,
        };

        var end = new System.Windows.Controls.MenuItem
        {
            Header = why is null ? what : $"{what} - {why}",
            IsEnabled = why is null,
            StaysOpenOnClick = true,
        };

        bool armed = false;
        end.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                end.Header = count > 1
                    ? $"Click again to end all {count} {name} processes"
                    : $"Click again to end {name}";
                end.Foreground = _theme.Warn;
                return;
            }

            menu.IsOpen = false;

            // Unsaved work in it is lost, which the second click has already
            // accepted; only a failure is worth a notification.
            if (MemoryUsers.End(name) is { } error)
                _tray?.Notify("DeskMon", $"Couldn't end {name}: {error}", warning: true);
        };

        menu.Items.Add(end);
        menu.IsOpen = true;
    }

    /// <summary>Through the tray, so its own item turns into "Show widget".</summary>
    private void MenuHideWidget_Click(object sender, RoutedEventArgs e) => _tray?.HideWidget();

    private void MenuOnTop_Click(object sender, RoutedEventArgs e) => SetAlwaysOnTop(MenuOnTop.IsChecked);

    private void MenuLocked_Click(object sender, RoutedEventArgs e) => SetLocked(MenuLocked.IsChecked);

    private void MenuResetPeaks_Click(object sender, RoutedEventArgs e) => ResetPeaks();

    // These three are also tick boxes in the settings window, which is told so
    // its tick follows - see SettingsWindow.RefreshValues.

    private void SetAlwaysOnTop(bool on)
    {
        _settings.AlwaysOnTop = on;
        Topmost = on;
        _settings.Save();
        _settingsWindow?.RefreshValues();
    }

    private void SetLocked(bool on)
    {
        _settings.Locked = on;
        _settings.Save();
        _settingsWindow?.RefreshValues();
    }

    /// <summary>
    /// Forgets every row's worst reading. Nothing is saved: peaks describe the
    /// session, so carrying them across a restart would be a lie.
    /// </summary>
    private void ResetPeaks()
    {
        foreach (var row in _rows)
            row.ResetPeak();
    }

    /// <summary>
    /// Opens the notepad for whatever is running, or the general notes when
    /// nothing is. Asked for explicitly, so this one does take focus.
    /// </summary>
    private void MenuNotes_Click(object sender, RoutedEventArgs e)
        => Notes().ShowAndFocus(_gameName);

    // --- Toolbar ----------------------------------------------------------
    //
    // The three window buttons are toggles, so the button that opened a window
    // puts it away again - the menu items above stay plain "open this", since a
    // menu entry called "Hardware info" that closes the hardware window would
    // be a surprise rather than a convenience.

    private void ToolHardware_Click(object sender, RoutedEventArgs e)
        => ToggleWindow(_hardware, () => _ = ShowHardwareAsync());

    private void ToolSessions_Click(object sender, RoutedEventArgs e)
        => ToggleWindow(_sessionsWindow, () => ShowSessions(null));

    private void ToolNotes_Click(object sender, RoutedEventArgs e)
        => ToggleWindow(_notes, () => Notes().ShowAndFocus(_gameName));

    private void ToolSettings_Click(object sender, RoutedEventArgs e)
        => ToggleWindow(_settingsWindow, () => SettingsPanel().ShowAndFocus());

    /// <summary>
    /// The settings window, created on first use like the others. Its two
    /// events are subscribed here rather than at the call site, so they are
    /// wired exactly once however often the window is opened and closed.
    /// </summary>
    private SettingsWindow SettingsPanel()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = Track(new SettingsWindow(_settings, _theme));
            _settingsWindow.Changed += ApplySettings;
            _settingsWindow.ReloadRequested += RestartForSettings;
        }

        return _settingsWindow;
    }

    /// <summary>
    /// Re-reads the settings into the running widget, for the settings window.
    ///
    /// Everything here is cheap and idempotent, so it can run on every
    /// keystroke in that window rather than needing to know what changed. The
    /// rows hold no copies of their own - see IPanelRow - so telling their
    /// bindings to look again is what repaints the panel in the new colours and
    /// column widths.
    ///
    /// The polling intervals need nothing: the tick works them out from the
    /// settings every time round. Neither do MeasureFps, PresentMonPath,
    /// NotGames or PauseWhenFullscreen, which are read where they are used.
    /// </summary>
    private void ApplySettings()
    {
        _theme.Apply(_settings);

        Topmost = _settings.AlwaysOnTop;
        FontSize = SafeFontSize();
        FontFamily = SafeFont(_settings.FontFamily);
        ApplyOpacity();

        Panel.Background = _theme.Background;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        _backdrop.Strength = _settings.WidgetBackdrop;
        ApplyVersionStyle();

        foreach (var entry in _allItems)
            (entry.Item as IPanelRow)?.Refresh();

        _sessionsWindow?.RedrawChart();   // the bar gradient

        // Only when what decides visibility actually changed: this runs on
        // every keystroke in the settings window, and rebuilding the list
        // redraws every row on the panel.
        if (_visibilityApplied != VisibilitySettings())
            RefreshVisibleItems();

        // The column lines follow the theme like the heading lines do; the
        // layout itself only when it was changed, since that re-measures.
        if (_sectionsPanel is not null)
            _sectionsPanel.SeparatorBrush = _theme.Separator;
        if (_horizontalApplied is not null && _horizontalApplied != Horizontal)
            ApplyLayoutMode();

        _overlay?.ApplyStyle();
        UpdateOverlay();   // ShowFpsOverlay, MeasureFps or PauseWhenFullscreen may have changed

        // A release page just set, or the check just switched on, is looked
        // up now rather than at the next daily check. Each keystroke in the
        // box comes here; only a value that stays put a moment is asked about.
        // The art lookups are kept per game; one made with the store switched
        // off found less than it would now, so they are made again.
        if (_onlineArtApplied != _settings.OnlineGameArt)
        {
            _onlineArtApplied = _settings.OnlineGameArt;
            GameArt.Forget();
        }

        string updateCheck = _settings.CheckForUpdates ? _settings.UpdateRepository.Trim() : string.Empty;
        if (updateCheck != _updateCheckApplied)
        {
            _updateCheckApplied = updateCheck;
            _updateSoon.Stop();
            if (updateCheck.Length > 0)
                _updateSoon.Start();
            _versions?.RefreshUpdate();
        }

        // The pinned width was measured from the old font and column widths, so
        // it has to be measured again - once typing pauses. Measuring lays out
        // every row there is, folded or not, and doing that on each keystroke
        // in the settings window made typing in it visibly lag.
        //
        // Not before the sensor lists exist, though: measuring without them
        // would pin a width too narrow for them, and the startup path that
        // normally does this would then find the width already pinned and leave
        // it that way.
        //
        // The hotkeys wait for the pause too: typed a key at a time,
        // "Ctrl+Shift+F12" passes through "Ctrl+Shift+F" and "Ctrl+Shift+F1",
        // and each of those would be registered - taken from every other
        // program - for the moment it lasted.
        _relayout.Stop();
        _relayout.Start();
    }

    /// <summary>
    /// What ApplySettings leaves until typing pauses: the width re-measure
    /// and the hotkeys.
    /// </summary>
    private void OnSettingsSettled()
    {
        _relayout.Stop();
        RegisterHotkeys();

        if (_detailsBuilt)
            RelockWidth();
    }

    /// <summary>Drops the pinned width and measures it again. See <see cref="ApplySettings"/>.</summary>
    private void RelockWidth()
    {
        _widthLocked = false;
        Width = double.NaN;
        LockPanelWidth();
    }

    /// <summary>
    /// (Re)registers the screenshot and FPS overlay hotkeys from the settings.
    /// A failure - the combination taken by another program, or not a
    /// combination at all - goes in the tooltip rather than a dialog: nothing
    /// else about the widget is wrong, and the settings window shows what was
    /// typed.
    /// </summary>
    private void RegisterHotkeys()
    {
        // | rather than ||, so the second is registered even when the first changed.
        bool changed =
            Reregister(_screenshotHotkey, _settings.ScreenshotHotkey,
                       ref _registeredHotkey, ref _hotkeyNote, "Screenshot hotkey")
            | Reregister(_overlayHotkey, _settings.FpsOverlayHotkey,
                         ref _registeredOverlayHotkey, ref _overlayHotkeyNote, "FPS overlay hotkey");

        if (changed)
            UpdateTooltip();
    }

    /// <summary>
    /// One hotkey of <see cref="RegisterHotkeys"/>. Does nothing - and says so
    /// - when the text is the one already registered, so unchanged settings
    /// don't take the key away from Windows and hand it back.
    ///
    /// Only a combination Windows took counts as registered. One that was
    /// refused - held by another program at logon, say - used to be recorded
    /// all the same and never tried again, so the hotkey stayed dead until the
    /// setting was changed; now every call tries it again (see TrackGame).
    /// True when the tooltip's note changed.
    /// </summary>
    private static bool Reregister(GlobalHotkey? hotkey, string gesture,
                                   ref string? registered, ref string? note, string name)
    {
        if (hotkey is null || registered == gesture)
            return false;

        string? error = hotkey.Register(gesture);
        registered = error is null ? gesture : null;

        string? wanted = error is null ? null : $"{name}: {error}";
        bool changed = wanted != note;
        note = wanted;
        return changed;
    }

    // --- Screenshots -------------------------------------------------------

    /// <summary>
    /// Puts a screenshot of the game into its notes - from the hotkey in a
    /// game, or the notepad's camera button.
    ///
    /// DeskMon's own windows are kept out of the picture while it is taken,
    /// and put back straight after. The full-size image is saved beside the
    /// notes and a smaller copy goes into them; see Notes/Screenshot.cs for
    /// why there are two.
    /// </summary>
    private async Task TakeScreenshotAsync()
    {
        if (_capturing)
            return;

        _capturing = true;
        try
        {
            // The game's window when one is tracked: the camera button is
            // pressed in the notepad, but it is still the game that is wanted.
            IntPtr target = TrackedGameWindow();
            if (target == IntPtr.Zero)
                target = GetForegroundWindow();

            // The notes the picture belongs with: the game's own, or whichever
            // are open when no game is running.
            string game = _inGame && _gameName is not null
                ? _gameName
                : _notes?.CurrentGame ?? NotesStore.GeneralNotes;

            var ours = OwnWindowHandles();
            System.Drawing.Bitmap shot;

            Screenshot.ExcludeFromCapture(ours, exclude: true);
            try
            {
                // Windows needs a frame or two to recompose the screen without
                // them; capturing straight away still catches them.
                await Task.Delay(150);
                shot = await Task.Run(() => Screenshot.CaptureMonitorOf(target));
            }
            finally
            {
                Screenshot.ExcludeFromCapture(ours, exclude: false);
            }

            using (shot)
            {
                var notes = Notes();
                notes.ShowFor(game);

                if (Screenshot.IsBlank(shot))
                {
                    notes.ShowMessage("The screenshot came out black - a game in exclusive fullscreen " +
                                      "can't be captured. Borderless or windowed works.");
                    return;
                }

                var takenAt = DateTime.Now;
                int quality = _settings.ScreenshotQuality;
                string path = _notesStore.ScreenshotPathFor(game, takenAt, Screenshot.ExtensionFor(quality));

                // Named to the second, and the hotkey can be pressed twice in
                // one: the second would have been saved over the first, leaving
                // the notes with two pictures of one file. Moved on a second
                // instead, so the name still reads as when it was taken.
                while (System.IO.File.Exists(path))
                {
                    takenAt = takenAt.AddSeconds(1);
                    path = _notesStore.ScreenshotPathFor(game, takenAt, Screenshot.ExtensionFor(quality));
                }

                // Saving a 4K image and scaling it down take a while; not on the UI thread.
                var preview = await Task.Run(() =>
                {
                    Screenshot.Save(shot, path, quality);
                    return Screenshot.Preview(shot, takenAt);
                });

                // The notes may have moved on while the file was saved - the
                // game exiting takes them to General, or another was picked -
                // and the picture would have gone into those. Back to the
                // game it was taken for, whose folder the file is in.
                if (!string.Equals(notes.CurrentGame, game, StringComparison.OrdinalIgnoreCase))
                    notes.ShowFor(game);

                notes.InsertScreenshot(preview, path, takenAt);
            }
        }
        catch (Exception ex)
        {
            _notes?.ShowMessage("Screenshot failed: " + ex.Message);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>The tracked game's main window, or zero when there is no game or no window.</summary>
    private IntPtr TrackedGameWindow()
    {
        if (!_inGame || _gameProcess is null)
            return IntPtr.Zero;

        try
        {
            return GameWindow();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// The tracked game's main window, remembered between ticks.
    ///
    /// Process.Refresh and MainWindowHandle walk every top-level window on the
    /// desktop to find it, and this is asked every tick while you are tabbed
    /// out. A window that still exists and still belongs to the game is kept,
    /// as OverlayAnchor keeps its own; only when it has gone - closed, hidden,
    /// or the game swapped a splash window for the real one - is it looked up
    /// again. Visible is part of the test because MainWindowHandle only ever
    /// gives a visible window: a hidden splash kept here would stand in for
    /// the game's real one. Throws as MainWindowHandle does; the callers catch.
    /// </summary>
    private IntPtr GameWindow()
    {
        if (_gameProcess is null)
            return IntPtr.Zero;

        if (_gameWindow != IntPtr.Zero && _gameWindowPid == _gameProcess.Id
            && IsWindow(_gameWindow) && IsWindowVisible(_gameWindow)
            && GetWindowThreadProcessId(_gameWindow, out uint owner) != 0 && owner == _gameWindowPid)
        {
            return _gameWindow;
        }

        _gameProcess.Refresh();
        _gameWindow = _gameProcess.MainWindowHandle;
        _gameWindowPid = _gameProcess.Id;
        return _gameWindow;
    }

    private IntPtr _gameWindow;
    private int _gameWindowPid;

    /// <summary>The widget and every DeskMon window on screen - what a capture must not include.</summary>
    private List<IntPtr> OwnWindowHandles()
    {
        var handles = new List<IntPtr> { _handle };

        foreach (var window in PanelWindows())
            if (window.IsVisible)
                handles.Add(new WindowInteropHelper(window).Handle);

        if (_overlay is { IsVisible: true })
            handles.Add(new WindowInteropHelper(_overlay).Handle);

        return handles;
    }

    /// <summary>
    /// Shows the window, or puts it away if it is already up.
    ///
    /// Close(), not Hide(): all three cancel their own closing and hide instead,
    /// and on the way they save their placement - and the notes their text.
    /// Hiding directly would skip that, so the button would quietly behave
    /// differently from the window's own ✕.
    /// </summary>
    private void ToggleWindow(Window? window, Action show)
    {
        if (window is { IsVisible: true })
            window.Close();
        else
            show();

        // Normally redundant - the visibility change has already called this -
        // but it also puts the toggle back when a click changed nothing, which
        // is what a failed show would otherwise leave lit.
        RefreshToolButtons();
    }

    /// <summary>
    /// Lights each toggle according to what is actually on screen.
    ///
    /// The clicks deliberately do not decide this. A window can also be put
    /// away by its own ✕, and the notes open themselves when a game starts, so
    /// a toggle that remembered what it was last told would sooner or later
    /// claim a window was up when it wasn't. Every one of these windows reports
    /// through IsVisibleChanged instead - see <see cref="Track"/>.
    /// </summary>
    private void RefreshToolButtons()
    {
        ToolHardware.IsChecked = _hardware is { IsVisible: true };
        ToolSessions.IsChecked = _sessionsWindow is { IsVisible: true };
        _notes?.SetSessionsOpen(_sessionsWindow is { IsVisible: true });   // the notes' ALL SESSIONS, with LOG
        ApplyVersionStyle();   // the version number is the versions window's toggle now
        ToolNotes.IsChecked = _notes is { IsVisible: true };
        ToolSettings.IsChecked = _settingsWindow is { IsVisible: true };
    }

    /// <summary>
    /// Subscribes a window to the toolbar, at the one place each is created.
    /// </summary>
    private T Track<T>(T window) where T : Window
    {
        window.IsVisibleChanged += (_, _) => RefreshToolButtons();

        // A window opened during a game has to start faded like the rest -
        // this is the one place all of them are created, so it is the one place
        // that can say so.
        if (window is PanelWindow panel)
            panel.SetGameRunning(_inGame);

        return window;
    }

    /// <summary>
    /// What the machine is, rather than what it is doing. Kept out of the
    /// widget so the panel stays about live numbers.
    /// </summary>
    private void MenuHardware_Click(object sender, RoutedEventArgs e) => _ = ShowHardwareAsync();

    private async Task ShowHardwareAsync()
    {
        // Re-read only on an actual open. The tray and menu also land here
        // while the window is already up, and bringing it forward should not
        // ask nvidia.com again each time.
        bool opening = _hardware is not { IsVisible: true };

        _hardware ??= Track(new HardwareWindow(_settings, _theme));
        _hardware.ShowAndFocus();

        // Names come from the sensor library so the card listed is the one the
        // temperatures are actually read from.
        if (opening)
            await _hardware.LoadAsync(_hub?.CpuName, _hub?.GpuName);
    }

    private void MenuVersions_Click(object sender, RoutedEventArgs e) => ShowVersions();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel().ShowAndFocus();

    /// <summary>
    /// What has changed, version by version. Created on first use and then kept
    /// like the other windows, so reopening it keeps the scroll position.
    /// </summary>
    private void ShowVersions()
    {
        _versions ??= Track(new VersionsWindow(_settings, _theme));
        _versions.ShowAndFocus();
    }

    private void MenuSessions_Click(object sender, RoutedEventArgs e) => ShowSessions(null);

    /// <summary>
    /// The session history, opened on <paramref name="game"/> - or, with none
    /// given, on the game running now, else the one played last.
    /// </summary>
    private void ShowSessions(string? game)
    {
        if (_sessionsWindow is null)
        {
            _sessionsWindow = Track(new SessionsWindow(_settings, _theme, _sessions, _notesStore.ScreenshotFolderFor));

            // The notepad's last-session footer may be the session just
            // deleted, and would otherwise show it until the next game ends.
            _sessionsWindow.SessionDeleted += () => _notes?.RefreshLastSession();
        }

        _sessionsWindow.ShowAndFocus(game ?? _gameName);
    }

    private void MenuNotesFolder_Click(object sender, RoutedEventArgs e) => OpenNotesFolder();

    /// <summary>Explorer on the notes folder - no need to open the notepad for that.</summary>
    private void OpenNotesFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_notesStore.FolderPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch { /* nothing useful to do */ }
    }

    /// <summary>
    /// The notes window is created on first use and then kept: closing it only
    /// hides it, so notes, caret and scroll position survive until exit.
    /// </summary>
    private NotesWindow Notes()
    {
        if (_notes is null)
        {
            _notes = Track(new NotesWindow(_settings, _theme, _notesStore, _sessions));

            // Here, at the one place it is created, so it is wired exactly once.
            _notes.ScreenshotRequested += () => _ = TakeScreenshotAsync();
            // A toggle, like LOG: open on this game's sessions, or put away.
            // ToggleWindow ends by relighting the toolbar, ALL SESSIONS with it.
            _notes.SessionsRequested += game => ToggleWindow(_sessionsWindow, () => ShowSessions(game));
            // A toggle, like the widget's own window buttons - see ToggleWindow.
            // The button is put right afterwards whatever happened: clicking a
            // toggle flips it before anything is asked, so a show that failed
            // would otherwise leave it lit over no window.
            _notes.PicturesRequested += () =>
            {
                ToggleWindow(_pictures, ShowPictures);
                _notes.SetPicturesOpen(_pictures is { IsVisible: true });
            };

            // Hidden with the notes: it shows their game's pictures, and on
            // its own it would be a window about notes nobody has open.
            // Along with them, through its own Close - which hides smoothly, so
            // its picture and banner aren't kept in the frame Windows shows on
            // reopening. The Hide below is only for the notes hidden some other way.
            _notes.Hiding += () => _pictures?.Close();
            _notes.IsVisibleChanged += (_, _) =>
            {
                if (!_notes.IsVisible)
                    _pictures?.Hide();
            };
        }

        return _notes;
    }

    /// <summary>
    /// The open game's pictures. Made here, like every panel window, so it
    /// fades with the others during a game, is kept out of screenshots, and
    /// closes with DeskMon.
    /// </summary>
    private void ShowPictures()
    {
        if (_pictures is null)
        {
            _pictures = Track(new PicturesWindow(_settings, _theme, Notes()));

            // Docked beside the notes, but never over the widget: the two are
            // both always on top, and the one clicked last would hide the other.
            _pictures.KeepClearOf = () => IsVisible ? new Rect(Left, Top, ActualWidth, ActualHeight) : null;

            // The notes' PICTURES button lit while this is up, however it came
            // and went: that button, its own ✕, or hidden with the notes.
            _pictures.IsVisibleChanged += (_, _) => _notes?.SetPicturesOpen(_pictures.IsVisible);
        }

        _pictures.ShowAndFocus();
    }

    private void MenuOpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsFile();

    private void OpenSettingsFile()
    {
        if (!System.IO.File.Exists(AppSettings.FilePath))
            _settings.Save();   // write the file so there is something to open

        // Guarded like every other Process.Start: a menu click that fails
        // should say so, not end up as an unhandled error in error.log. The
        // path quoted, so a folder with a space in it still opens.
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _tray?.Notify("DeskMon", "Couldn't open settings.json: " + ex.Message, warning: true);
        }
    }

    private void MenuReload_Click(object sender, RoutedEventArgs e) => RestartForSettings();

    /// <summary>
    /// Restart in place so hand-edited settings.json takes effect. Simpler and
    /// more predictable than re-applying two dozen properties live.
    /// </summary>
    private void RestartForSettings()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;

        // Must NOT save on this path: these settings were loaded at startup, so
        // writing them back would overwrite the very edits we are reloading.
        // The position is already saved at the end of every drag.
        _restarting = true;

        // The open session written now, before the new copy starts: it
        // recovers whatever session was left open, and racing this copy's own
        // close, it could write the same evening twice - its last checkpoint,
        // then this copy's line. Ended here, there is nothing left to recover.
        FinishSession("cut short, settings reloaded");

        // Let go of the single-instance lock first, or the replacement sees
        // this process still holding it and exits immediately.
        var app = Application.Current as App;
        app?.ReleaseSingleInstanceLock();

        // Started before anything is closed: if Windows won't start it, this
        // copy carries on as it was - lock taken back, windows still its own -
        // rather than running on half shut down, with a second copy free to
        // start beside it.
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _restarting = false;
            app?.TakeSingleInstanceLock();

            // Carrying on: the game still running gets a session again,
            // since the one above was ended for a restart that didn't happen.
            if (_inGame && _gamePid != 0)
                BeginOrResumeSession(_gamePid, _gameName);
            _tray?.Notify("DeskMon", "Couldn't restart to reload the settings: " + ex.Message, warning: true);
            return;
        }

        PrepareForShutdown();
        Application.Current.Shutdown();
    }

    // --- Rebuild from the source ------------------------------------------------

    /// <summary>
    /// Build.ps1 in the folder above the program's - E:\Nicke\Program\Build.ps1
    /// for app\DeskMon.exe - or null when DeveloperRebuild is off or there is
    /// no such script, which is everywhere but the machine DeskMon is written on.
    /// </summary>
    private string? BuildScript()
    {
        if (!_settings.DeveloperRebuild)
            return null;

        try
        {
            string script = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "Build.ps1"));
            return System.IO.File.Exists(script) ? script : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A rebuild is under way; the button does nothing until it is over.</summary>
    private bool _rebuilding;

    /// <summary>
    /// Compiles the source first, and only when that works hands over to
    /// Build.ps1, which closes DeskMon properly, publishes over app\ and starts
    /// it again.
    ///
    /// The compile comes first because Build.ps1 closes DeskMon before it
    /// builds: a typo in the source would otherwise leave no widget running at
    /// all, and the error in a console window that had already closed. Checked
    /// here, a failed build costs nothing - DeskMon carries on and says why.
    ///
    /// No UAC prompt: DeskMon runs elevated, so the script it starts does too,
    /// and its own re-launch as administrator is skipped.
    /// </summary>
    private async void ToolRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_rebuilding || BuildScript() is not { } script)
            return;

        string root = System.IO.Path.GetDirectoryName(script)!;
        string project = System.IO.Path.Combine(root, "src", "DeskMon", "DeskMon.csproj");

        _rebuilding = true;
        ToolRebuild.IsEnabled = false;
        ToolRebuildText.Text = "BUILDING…";

        try
        {
            var (ok, output) = await Task.Run(() => CompileCheck(project));
            if (!ok)
            {
                App.Log("rebuild", output);
                _tray?.Notify("DeskMon build failed", FirstError(output) + "\nThe full output is in error.log.", warning: true);
                return;
            }

            // A window of its own, so the publish can be watched - it closes
            // when Build.ps1 is done and DeskMon is back.
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = root };
            start.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
            Process.Start(start);
        }
        catch (Exception ex)
        {
            App.Log("rebuild", ex);
            _tray?.Notify("DeskMon build failed", ex.Message, warning: true);
        }
        finally
        {
            // Back to normal for the moment until Build.ps1 closes DeskMon -
            // or for good, if the build failed.
            _rebuilding = false;
            ToolRebuild.IsEnabled = true;
            ToolRebuildText.Text = "BUILD";
        }
    }

    /// <summary>
    /// A plain dotnet build of the project, for whether it compiles at all.
    /// Debug, into its own bin\ folder, so app\DeskMon.exe - running this very
    /// moment - is never touched. Given five minutes; a first build after a
    /// clean restore can take one.
    /// </summary>
    private static (bool Ok, string Output) CompileCheck(string project)
    {
        // Where the installer puts it, when there: a process started before
        // .NET was installed - DeskMon at logon - may not have it on PATH yet.
        // Build.ps1 makes the same allowance.
        string installed = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

        var start = new ProcessStartInfo(System.IO.File.Exists(installed) ? installed : "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // -nodeReuse:false: MSBuild otherwise leaves its worker processes
        // running after the build, and Build.ps1's publish straight after
        // could find files under obj\ still held by them - "being used by
        // another process", now and then.
        foreach (string arg in new[] { "build", project, "-nologo", "-v", "q", "-nodeReuse:false" })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet could not be started.");

        // Both streams read at once, or a full stderr pipe could stall the build.
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(5 * 60 * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, "dotnet build took more than five minutes and was stopped.");
        }

        return (process.ExitCode == 0, output + error.Result);
    }

    /// <summary>The first compiler error in the output, short enough for a notification.</summary>
    private static string FirstError(string output)
    {
        string line = output.Split('\n')
                            .Select(l => l.Trim())
                            .FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))
                      ?? "The build did not succeed.";

        // "E:\...\NotesWindow.xaml.cs(12,5): error CS1002: ; expected [..csproj]" -
        // the file name and the message, without the folders or the project.
        int bracket = line.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracket > 0)
            line = line[..bracket];

        // Cut at ": error", not at the first colon - that is the drive letter's.
        int at = line.IndexOf(": error ", StringComparison.Ordinal);
        if (at > 0)
            line = System.IO.Path.GetFileName(line[..at]) + line[at..];

        return line.Length <= 200 ? line : line[..199] + "…";
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        PrepareForShutdown();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// The notes window refuses to close so it can be reused, so it has to be
    /// told that this time the app really is going away.
    /// </summary>
    private void PrepareForShutdown()
    {
        if (_notes is not null)
        {
            _notes.SaveNow();
            _notes.AllowClose();
        }

        _pictures?.AllowClose();
        _hardware?.AllowClose();
        _versions?.AllowClose();
        _sessionsWindow?.AllowClose();
        _settingsWindow?.AllowClose();
        _overlay?.AllowClose();
    }

    // --- Idle handling ----------------------------------------------------

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        => SetPaused(e.Reason is SessionSwitchReason.SessionLock
                                or SessionSwitchReason.ConsoleDisconnect
                                or SessionSwitchReason.RemoteDisconnect);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) SetPaused(true);
        if (e.Mode == PowerModes.Resume) SetPaused(false);
    }

    /// <summary>
    /// Stops or restarts polling. Pausing also marks the CPU-time baseline
    /// stale, because nothing samples those counters while paused - see the
    /// prime in <see cref="OnTick"/>.
    /// </summary>
    private void SetPaused(bool paused)
    {
        if (paused)
            _needsPrime = true;

        _paused = paused;

        // Through the dispatcher: SystemEvents may call from a thread of its
        // own, and the drift's clock belongs to the UI thread.
        Dispatcher.BeginInvoke(new Action(UpdateDrift));
    }

    /// <summary>
    /// The background moves only while someone can see it move. Every step
    /// redraws the whole see-through widget, and it went on stepping ten
    /// times a second while the widget was hidden from the tray, or the PC
    /// was locked or asleep - as well as over a game, where it already stopped.
    /// </summary>
    private void UpdateDrift() => _backdrop.Drifting = !_inGame && !_userHidden && !_paused;

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Stop();
        _relayout.Stop();
        _overlayTimer.Stop();
        _clockTimer.Stop();

        if (!_restarting)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Save();
        }

        StopTrackingGame(refresh: false);   // also kills the PresentMon child process

        // A session normally ends when the game does, so one still open here is
        // one DeskMon is walking out on - quitting the widget, or reloading
        // settings, mid-game. It is written anyway, because this is the only
        // chance to write it at all and losing the evening would be worse, but
        // the line says so: nothing measured it after this point.
        FinishSession("cut short, DeskMon closed");

        if (_notes is not null)
        {
            _notes.SaveNow();
            _notes.AllowClose();
            _notes.Close();
        }

        if (_pictures is not null)
        {
            _pictures.AllowClose();
            _pictures.Close();
        }

        if (_hardware is not null)
        {
            _hardware.AllowClose();
            _hardware.Close();
        }

        if (_versions is not null)
        {
            _versions.AllowClose();
            _versions.Close();
        }

        if (_sessionsWindow is not null)
        {
            _sessionsWindow.AllowClose();
            _sessionsWindow.Close();
        }

        // Last of the windows: closing it flushes any edit still waiting on its
        // save timer, and that write must not race the one below.
        if (_settingsWindow is not null)
        {
            _settingsWindow.AllowClose();
            _settingsWindow.Close();
        }

        if (_overlay is not null)
        {
            _overlay.AllowClose();
            _overlay.Close();
        }

        _screenshotHotkey?.Dispose();   // give the key combinations back to Windows
        _overlayHotkey?.Dispose();
        _tray?.Dispose();   // otherwise the icon lingers in the tray
        _fps.Dispose();
        _ping.Dispose();
        _updateTimer.Stop();
        _updateSoon.Stop();
        _hub?.Dispose();
    }

    private static FontFamily SafeFont(string name)
    {
        try { return new FontFamily(name); }
        catch { return new FontFamily("Consolas"); }
    }

    /// <summary>
    /// The text size, kept to what WPF takes. 0, a negative or NaN made
    /// setting FontSize throw - and in the constructor, before the tray icon
    /// existed, that left DeskMon running with no window, holding the
    /// single-instance lock, so no later start could show one either.
    /// </summary>
    private double SafeFontSize()
        => double.IsFinite(_settings.FontSize) ? Math.Clamp(_settings.FontSize, 6, 48) : 13;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// The pseudo-handle for this process. Process.GetCurrentProcess().Handle
    /// opens a real handle instead, and that one was never disposed - a handle
    /// leaked per trim, for nothing. This needs no closing at all.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
