using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Hardware;
using DeskMon.Sensors;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// What the machine is made of, as opposed to what it is doing. Gathered
/// again each time the window is opened, so a driver installed since the
/// last look (and NVIDIA's answer about it) is not left stale until a restart.
/// </summary>
public partial class HardwareWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly List<InfoRow> _rows = new();

    /// <summary>
    /// Collapses a drag or resize into one write, the way the notes window
    /// does.
    /// </summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private bool _allowClose;

    /// <summary>
    /// Bumped by every load. Closing and reopening while NVIDIA is still being
    /// asked starts a second load; without this the first one could finish
    /// last and put its older rows back over the newer ones.
    /// </summary>
    private int _loadGeneration;
    private string? _cpuName, _gpuName;

    /// <summary>The stability line as last built, for its right-click menu.</summary>
    private InfoRow? _stabilityRow;

    public HardwareWindow(AppSettings settings, UiTheme theme)
    {
        _settings = settings;
        _theme = theme;

        InitializeComponent();
        new Banner(Panel, height: 210).Show(Notes.GameArt.Motif.Chip);
        ApplyTheme();
        // The rows keep the colours they were made with; they are made again
        // each time the window opens.
        _theme.Changed += ApplyTheme;
        // The height too, now that the window keeps one size rather than
        // fitting its rows - as the other windows remember theirs.
        RestorePlacement(_settings.HardwareLeft, _settings.HardwareTop,
                         _settings.HardwareWidth, _settings.HardwareHeight);

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.HardwareOpacity);
        Rows.ItemsSource = _rows;

        // A row with something to do on it - the stability line's "start
        // counting from now" - gets its own menu. Handled here, once, rather
        // than in the row template, since only one row ever has one.
        Rows.MouseRightButtonUp += Rows_MouseRightButtonUp;

        _placementSave.Tick += (_, _) => SavePlacementNow();

        Closing += OnClosing;

        // The rows put out before it hides: Windows shows the last frame again
        // the instant it reopens, which would flash the old rows up before
        // the loading screen. Blank instead, and the dots come in from there.
        // The dots stopped too: closed mid-load, they would pulse on behind a
        // hidden window until the load ended - 15 s, or for good if WMI hangs.
        Hiding += () =>
        {
            StopDots();
            RowsScroller.Visibility = Visibility.Hidden;
        };

        // Reopened within the hide's short wait, it was never hidden, so no
        // fresh load comes (see MainWindow.ShowHardwareAsync): the rows back as they
        // were, or the window would stay blank. A real open puts the loading
        // screen up straight after this, before anything is drawn.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && Loading.Visibility != Visibility.Visible)
                RowsScroller.Visibility = Visibility.Visible;
        };

        LocationChanged += (_, _) =>
        {
            FitToScreen();   // dragged lower down the screen: shorter, and the rows scroll
            QueuePlacementSave();
        };
        SizeChanged += (_, _) => QueuePlacementSave();
        SourceInitialized += (_, _) => FitToScreen();
    }

    /// <summary>
    /// Caps the window at the bottom of the screen it is on, so it grows to
    /// fit its rows only as far as there is room and the rest scrolls.
    ///
    /// The screen it is on, not the main one: on a second monitor of another
    /// size, the main screen's height would be the wrong limit. Its work area,
    /// so the taskbar isn't covered. Converted from pixels, which is what
    /// Windows measures screens in, to the units WPF lays out in.
    /// </summary>
    private void FitToScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return;

        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        double bottom = target.TransformFromDevice.Transform(new Point(0, area.Bottom)).Y;

        // A small gap above the edge, so the rounded corners and border show.
        MaxHeight = Math.Max(MinHeight, bottom - Top - 8);
    }

    /// <summary>
    /// Fills the window. The sensor library already knows the CPU and the exact
    /// graphics card the temperatures come from, so those are passed in rather
    /// than looked up again. The rest is collected off the UI thread, because
    /// the memory query goes through WMI.
    /// </summary>
    public async Task LoadAsync(string? cpuName, string? gpuName)
    {
        int generation = ++_loadGeneration;

        // The loading screen every time, not last time's rows: those are a
        // reading from the past, and the window keeps its size either way.
        ShowLoading(true);

        // Kept for "start counting from now", which fills the window again.
        _cpuName = cpuName;
        _gpuName = gpuName;

        LoadingText.Text = "Reading hardware…";
        SystemInfo info;
        IReadOnlyList<Prerequisites.Missing> missing;
        try
        {
            info = await Task.Run(() => SystemInfo.Collect(cpuName, gpuName, _settings.StabilityCountedFrom));
            missing = await Task.Run(() => Prerequisites.Check(_settings));
        }
        catch (Exception ex)
        {
            // A WMI fault or the like: said in place of the rows. Left to
            // throw, the loading screen stayed up for good with nothing to
            // say why - the only way out was closing the window.
            App.Log("reading the hardware", ex);
            if (generation != _loadGeneration)
                return;
            _rows.Clear();
            _rows.Add(new InfoRow("Couldn't read", ex.Message, _theme, _settings, known: true, warning: true));
            Rows.Items.Refresh();
            ShowLoading(false);
            return;
        }
        if (generation != _loadGeneration)
            return;

        _rows.Clear();
        // A separate brand list per kind of row: the motherboard string can
        // name a chipset vendor ("AMD B650") that is not who made the board.
        AddGroup("PROCESSOR & GRAPHICS");
        Add("CPU", info.Cpu, BrandLogos.ForChip(info.Cpu));
        AddDetail(info.CpuDetail);

        // Power plan, hardware errors, Intel's microcode fix - SystemInfo.CpuChecks.
        foreach (var check in info.CpuChecks)
            AddCheck(check.Text, check.Level);
        Add("Graphics", info.Gpu, BrandLogos.ForChip(info.Gpu));
        AddDetail(info.GpuDetail);

        // NVIDIA's answer is filled in afterwards, into a row that says it is
        // checking: a slow network must not hold up the rest of the window.
        // Without a driver version there is nothing to compare, so no asking.
        // A current Radeon is asked of AMD the same way.
        InfoRow? driverCheck = null;
        string? driverMaker = info.Gpu is null || info.GpuDriverVersion is null ? null
            : UpdateCheck.IsNvidia(info.Gpu) ? "NVIDIA"
            : UpdateCheck.IsCurrentRadeon(info.Gpu) ? "AMD"
            : null;
        if (_settings.CheckDriverOnline && driverMaker is not null)
        {
            driverCheck = VerdictRow(new UpdateCheck.Verdict($"Checking {driverMaker} for a newer driver…", UpdateCheck.Freshness.Unknown));
            _rows.Add(driverCheck);
        }
        else
        {
            // Only with the online check on: off, nothing was going to be
            // asked, and "blocked" would be beside the point.
            AddVerdict(_settings.CheckDriverOnline && info.Gpu is not null && UpdateCheck.BlockedMaker(info.Gpu) is { } maker
                ? UpdateCheck.Blocked("Driver", maker, info.GpuDriverDate)
                : UpdateCheck.Age("Driver", info.GpuDriverDate));
        }

        // Under the card and its driver: whether it gets all its PCIe lanes.
        if (info.GpuLink is { } link)
            AddCheck(link.Text, link.Level);

        // The screens it drives, each with whether it runs at its best refresh rate.
        bool firstDisplay = true;
        foreach (var display in info.Displays)
        {
            Add(firstDisplay ? "Display" : string.Empty, display.Text);
            AddCheck(display.Refresh.Text, display.Refresh.Level);
            firstDisplay = false;
        }

        AddGroup("MEMORY & STORAGE");
        Add("Memory", info.MemoryTotal);

        // One row per stick, labelled only once so the column stays clean.
        // Each stick gets its own logo, since mixed kits do happen.
        bool firstModule = true;
        foreach (string module in info.MemoryModules)
        {
            Add(firstModule ? "Modules" : string.Empty, module, BrandLogos.ForMemory(module));
            firstModule = false;
        }

        // Under the last module, about the memory as a whole: channels,
        // EXPO/XMP and errors - see SystemInfo.MemoryChecks.
        foreach (var check in info.MemoryChecks)
            AddCheck(check.Text, check.Level);

        bool firstDrive = true;
        foreach (var drive in info.Drives)
        {
            Add(firstDrive ? "Storage" : string.Empty, drive.Text, BrandLogos.ForStorage(drive.Text));
            firstDrive = false;

            // Under its drive, like the GPU's driver line - green when healthy,
            // red when it's failing: the one line in this window that can
            // mean "back up now".
            if (drive.Health is { } health)
                AddCheck(health, drive.Level);
        }

        AddGroup("BOARD & BIOS");
        Add("Motherboard", info.Motherboard, BrandLogos.ForBoard(info.Motherboard));
        Add("BIOS", info.Bios);

        // ASUS's answer the same way as NVIDIA's: a "checking" row now, the
        // verdict when it comes. Other board makers can't be asked - see UpdateCheck.
        InfoRow? biosCheck = null;
        if (_settings.CheckDriverOnline && info.BoardVendor is not null && info.BoardProduct is not null
            && info.BiosVersion is not null && UpdateCheck.IsAsus(info.BoardVendor))
        {
            biosCheck = VerdictRow(new UpdateCheck.Verdict("Checking ASUS for a newer BIOS…", UpdateCheck.Freshness.Unknown));
            _rows.Add(biosCheck);
        }
        else
        {
            // As for the driver above.
            AddVerdict(_settings.CheckDriverOnline && info.BoardVendor is not null && UpdateCheck.BlockedMaker(info.BoardVendor) is { } maker
                ? UpdateCheck.Blocked("BIOS", maker, info.BiosDate)
                : UpdateCheck.Age("BIOS", info.BiosDate));
        }
        Add("System", info.SystemModel);
        AddGroup("WINDOWS");
        Add("OS", info.OperatingSystem, info.OperatingSystem is null ? null : BrandLogos.Windows);

        // Under the OS row, filled in like the driver and BIOS checks. Behind
        // the same setting: Windows Update asks Microsoft's servers.
        InfoRow? windowsCheck = null;
        if (_settings.CheckDriverOnline)
        {
            windowsCheck = VerdictRow(new UpdateCheck.Verdict("Asking Windows Update for waiting updates…", UpdateCheck.Freshness.Unknown));
            _rows.Add(windowsCheck);
        }
        Add("Up since", info.UpSince);

        // Crashes since about a month back, under how long it has been up.
        _stabilityRow = null;
        if (info.Stability is { } stability)
        {
            _stabilityRow = CheckRow(stability.Text, stability.Level);
            _rows.Add(_stabilityRow);
        }

        // Windows settings that change how games run, and the two that
        // decide whether some start at all - their own group, so they read
        // as a checklist rather than as facts about Windows.
        if (info.GamingChecks.Count > 0)
        {
            AddGroup("GAMING");
            foreach (var check in info.GamingChecks)
                AddCheck(check.Text, check.Level);
        }

        // A disconnected adapter goes in the "unknown" grey: still listed,
        // but plainly not the one in use.
        bool firstAdapter = true;
        foreach (var adapter in info.NetworkAdapters)
        {
            // Inside the loop, so a PC with no adapter found gets no empty heading.
            if (firstAdapter)
                AddGroup("NETWORK");
            _rows.Add(new InfoRow(firstAdapter ? "Network" : string.Empty, adapter.Description,
                                  _theme, _settings, known: adapter.Connected,
                                  logo: BrandLogos.ForNetwork(adapter.Description)));
            firstAdapter = false;
        }

        // The link of each adapter in use, then a ping test filled in when
        // it's done, like the update checks. Behind the same setting: it
        // sends pings out to the internet.
        foreach (var check in info.NetworkChecks)
            AddCheck(check.Text, check.Level);

        string pingHost = string.IsNullOrWhiteSpace(_settings.PingHost) ? NetworkCheck.DefaultHost : _settings.PingHost.Trim();
        InfoRow? pingCheck = null;
        if (_settings.CheckDriverOnline && !firstAdapter)
        {
            pingCheck = CheckRow($"Measuring ping to {pingHost}…", SystemInfo.Condition.Unknown);
            _rows.Add(pingCheck);
        }

        // Here because this is the window people open to ask "what does this
        // machine have" - and on a fresh install, what it lacks is the part
        // that explains the n/a on the widget. In the warning colour so it
        // can't be read as one more fact about the hardware. Copy picks these
        // up too, which is handy when asking someone for help.
        bool firstMissing = true;
        foreach (var item in missing)
        {
            if (firstMissing)
                AddGroup("NEEDS INSTALLING", warning: true);
            _rows.Add(new InfoRow(firstMissing ? "Missing" : string.Empty,
                                  $"{item.Name} - {item.Effect}. Install: {item.Install}",
                                  _theme, _settings, known: true, warning: true));
            firstMissing = false;
        }

        Rows.Items.Refresh();
        RowsScroller.ScrollToTop();

        // Both started before either is awaited, so the two run side by side
        // and the wait is the slower one's, not the two added together.
        var driverTask = driverCheck is null ? null
            : driverMaker == "AMD" ? UpdateCheck.AmdDriverAsync(info.GpuDriverVersion, info.GpuDriverDate)
            : UpdateCheck.NvidiaDriverAsync(info.Gpu!, info.GpuDriverVersion, info.GpuDriverDate);
        var biosTask = biosCheck is null ? null
            : UpdateCheck.AsusBiosAsync(info.BoardProduct!, info.BiosVersion, info.BiosDate);
        var windowsTask = windowsCheck is null ? null : UpdateCheck.WindowsAsync();
        var pingTask = pingCheck is null ? null : NetworkCheck.PingAsync(pingHost);

        // The loading screen stays up until the online checks are in too, so
        // the window opens on every answer rather than on "Checking…" rows
        // that change under the reader. Capped, though: a slow network or a
        // Windows Update that takes its time leaves the rest unread for no
        // reason, so after MaxLoadingWait the rows show and the late answers
        // fill in as before. Not the ping test: it measures for a while, and
        // its row filling in late reads as a measurement still running.
        var pending = new List<Task>();
        if (driverTask is not null) pending.Add(driverTask);
        if (biosTask is not null) pending.Add(biosTask);
        if (windowsTask is not null) pending.Add(windowsTask);

        bool allInTime = true;
        if (pending.Count > 0)
        {
            LoadingText.Text = "Checking for updates…";
            var all = Task.WhenAll(pending);
            allInTime = await Task.WhenAny(all, Task.Delay(MaxLoadingWait)) == all;
            if (generation != _loadGeneration)
                return;
        }
        if (!allInTime)
            ShowLoading(false);

        if (driverTask is not null)
            ReplaceRow(driverCheck!, await driverTask, generation);
        if (biosTask is not null)
            ReplaceRow(biosCheck!, await biosTask, generation);
        // Last: it is the slowest of the three by far.
        if (windowsTask is not null)
            ReplaceRow(windowsCheck!, await windowsTask, generation);

        // Every update answer already in its row, so the rows appear finished
        // - all but the ping, which fills in after.
        if (allInTime && generation == _loadGeneration)
            ShowLoading(false);

        if (pingTask is not null)
        {
            var ping = await pingTask;
            Replace(pingCheck!, CheckRow(ping.Text, ping.Level), generation);
        }
    }

    /// <summary>The longest the loading screen waits on the online checks - see LoadAsync.</summary>
    private static readonly TimeSpan MaxLoadingWait = TimeSpan.FromSeconds(15);

    /// <summary>Puts an online check's verdict where its "checking…" row was.</summary>
    private void ReplaceRow(InfoRow checking, UpdateCheck.Verdict verdict, int generation)
        => Replace(checking, VerdictRow(verdict), generation);

    /// <summary>Puts any finished row where its "checking…" row was - the ping test's too.</summary>
    private void Replace(InfoRow checking, InfoRow done, int generation)
    {
        // A newer load has rebuilt _rows, so IndexOf would already miss -
        // checked anyway so the intent doesn't hang on that coincidence.
        if (generation != _loadGeneration)
            return;
        int at = _rows.IndexOf(checking);
        if (at >= 0)
        {
            _rows[at] = done;
            Rows.Items.Refresh();
        }
    }

    /// <summary>
    /// An update verdict, as a line under the row it is about, in the
    /// widget's own colours for good, warning and critical - so green, yellow
    /// and red mean the same here as on the bars. "Checking…" and "couldn't
    /// check" stay dimmed like the other second lines: no news isn't good news.
    /// </summary>
    private InfoRow VerdictRow(UpdateCheck.Verdict verdict)
        => new(string.Empty, verdict.Text, _theme, _settings, known: true, secondary: true)
        {
            ValueBrush = verdict.Level switch
            {
                UpdateCheck.Freshness.Current => _theme.Normal,
                UpdateCheck.Freshness.Aging => _theme.Warn,
                UpdateCheck.Freshness.Old => _theme.Crit,
                _ => _theme.Label,
            },
        };

    /// <summary>
    /// A health check's line, in the same green, yellow and red as the update
    /// verdicts - one colour language for the whole window.
    /// </summary>
    private void AddCheck(string text, SystemInfo.Condition level)
        => _rows.Add(CheckRow(text, level));

    private InfoRow CheckRow(string text, SystemInfo.Condition level)
        => new(string.Empty, text, _theme, _settings, known: true, secondary: true)
        {
            ValueBrush = level switch
            {
                SystemInfo.Condition.Good => _theme.Normal,
                SystemInfo.Condition.Warning => _theme.Warn,
                SystemInfo.Condition.Bad => _theme.Crit,
                _ => _theme.Label,
            },
        };

    /// <summary>
    /// Right-click on the stability line: count crashes and shutdowns from now
    /// on - or from 30 days back again, once a start has been set. For
    /// shutdowns that were the power button rather than a fault, which
    /// Windows logs no differently.
    /// </summary>
    private void Rows_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_stabilityRow is null
            || (e.OriginalSource as FrameworkElement)?.DataContext != _stabilityRow)
            return;

        e.Handled = true;

        var menu = new System.Windows.Controls.ContextMenu { Style = (Style)FindResource("DarkContextMenu") };

        var fromNow = new System.Windows.Controls.MenuItem { Header = "Start counting from now" };
        fromNow.Click += (_, _) => SetStabilityStart(DateTime.Now);
        menu.Items.Add(fromNow);

        if (_settings.StabilityCountedFrom is not null)
        {
            var month = new System.Windows.Controls.MenuItem { Header = "Count the last 30 days again" };
            month.Click += (_, _) => SetStabilityStart(null);
            menu.Items.Add(month);
        }

        menu.IsOpen = true;
    }

    private void SetStabilityStart(DateTime? from)
    {
        _settings.StabilityCountedFrom = from;
        _settings.Save();
        _ = LoadAsync(_cpuName, _gpuName);
    }

    private void AddVerdict(UpdateCheck.Verdict? verdict)
    {
        if (verdict is not null)
            _rows.Add(VerdictRow(verdict));
    }

    /// <summary>Starts a group of rows under a heading - see InfoRow.Heading.</summary>
    private void AddGroup(string heading, bool warning = false)
        => _rows.Add(InfoRow.Heading(heading, _theme, _settings, first: _rows.Count == 0, warning));

    private void Add(string label, string? value, BrandLogos.Logo? logo = null)
        => _rows.Add(new InfoRow(label, value ?? "unknown", _theme, _settings,
                                 known: value is not null, logo: logo));

    /// <summary>
    /// A second line under the row above it, unlabelled and in the label
    /// colour so it reads as part of that row rather than as a fact of its
    /// own. Skipped entirely when unknown: an "unknown" with no label next to
    /// it would not say what is unknown.
    /// </summary>
    private void AddDetail(string? value)
    {
        if (value is not null)
            _rows.Add(new InfoRow(string.Empty, value, _theme, _settings, known: true, secondary: true));
    }

    /// <summary>
    /// The loading screen in place of the rows, or the rows fading in in its
    /// place. The dots only pulse while it is up: this window can be open
    /// over a game, and an animation left running behind the rows would cost
    /// a redraw every frame for nothing.
    /// </summary>
    private void ShowLoading(bool on)
    {
        Loading.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RowsScroller.Visibility = on ? Visibility.Hidden : Visibility.Visible;

        var dots = new[] { LoadingDot1, LoadingDot2, LoadingDot3 };
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].BeginAnimation(OpacityProperty, !on ? null
                : new System.Windows.Media.Animation.DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(150 * i),   // one after another, a wave
                });
            dots[i].Opacity = 0.2;   // the base value, shown before each dot's turn starts
        }

        if (!on)
            RowsScroller.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>The dots still, the loading screen left as it is - for a window going away mid-load.</summary>
    private void StopDots()
    {
        foreach (var dot in new[] { LoadingDot1, LoadingDot2, LoadingDot3 })
            dot.BeginAnimation(OpacityProperty, null);
    }

    // --- Chrome -----------------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleHeaderMouseDown(e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Handy when posting a spec somewhere or filing a support ticket.</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var row in _rows)
        {
            // A heading as a blank line and the heading alone, so the pasted
            // text keeps the same groups as the window.
            if (row.IsHeading)
            {
                if (text.Length > 0)
                    text.AppendLine();
                text.AppendLine(row.Value);
                continue;
            }
            text.AppendLine($"{row.Label,-14}{row.Value}");
        }

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch
        {
            // Another process can hold the clipboard open; not worth a dialog.
        }
    }

    /// <summary>Hidden rather than closed, so it can be shown again later.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacementNow();   // whatever a drag left pending goes down with it

        if (_allowClose)
            return;

        e.Cancel = true;
        HideSmoothly();   // banner put out on screen first - see PanelWindow
    }

    public void AllowClose() => _allowClose = true;

    public void ShowAndFocus()
    {
        if (!IsOpen)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Topmost = _settings.NotesAlwaysOnTop;
        Activate();
    }

    /// <summary>Restarts the delay, so the write lands once the window stops moving.</summary>
    private void QueuePlacementSave()
    {
        _placementSave.Stop();
        _placementSave.Start();
    }

    /// <summary>Writes a pending placement change now, if there is one.</summary>
    private void SavePlacementNow()
    {
        if (!_placementSave.IsEnabled)
            return;

        _placementSave.Stop();
        SavePlacement();
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
            return;

        _settings.HardwareLeft = Left;
        _settings.HardwareTop = Top;
        _settings.HardwareWidth = Width;
        _settings.HardwareHeight = Height;
        _settings.Save();
    }

    private void ApplyTheme()
    {
        Panel.Background = _theme.NotesBackground;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        HeaderRule.Background = _theme.Separator;

        foreach (var dot in new[] { LoadingDot1, LoadingDot2, LoadingDot3 })
            dot.Fill = _theme.Accent;
        LoadingText.Foreground = _theme.Label;

        TitleText.Foreground = _theme.Section;
        TitleText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        FontSize = UiTheme.PanelFontSize;
        FontFamily = _theme.PanelFont;
    }

    /// <summary>One label/value pair. Static once built, so no notifications.</summary>
    private sealed class InfoRow
    {
        public InfoRow(string label, string value, UiTheme theme, AppSettings settings, bool known,
                       bool warning = false, BrandLogos.Logo? logo = null, bool secondary = false)
        {
            Label = label;
            Value = value;
            LabelBrush = warning ? theme.Warn : theme.Label;
            ValueBrush = warning ? theme.Warn
                       : !known ? theme.Unavailable
                       : secondary ? theme.Label
                       : theme.NotesText;
            LabelWidth = settings.HardwareLabelWidth;
            LogoShape = logo?.Shape;
            LogoFill = logo is null ? null : logo.Fill ?? theme.NotesText;
            LogoVisibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
            RuleBrush = theme.Separator;

            // A second line hangs right under its row - no gap above it - so the
            // driver, a drive's health or an update verdict reads as part of
            // the thing it is about rather than as a fact of its own.
            RowMargin = secondary ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 3);
        }

        /// <summary>
        /// A group's heading. Space above it, except for the first, which the
        /// window's own header rule already separates from the title. In the
        /// section colour like the widget's headings - or the warning colour,
        /// for the group that lists what is missing.
        /// </summary>
        public static InfoRow Heading(string text, UiTheme theme, AppSettings settings, bool first, bool warning = false)
            => new(string.Empty, text, theme, settings, known: true, warning: warning)
            {
                IsHeading = true,
                LabelBrush = warning ? theme.Warn : theme.Section,
                RowMargin = new Thickness(0, first ? 2 : 14, 0, 4),
            };

        public bool IsHeading { get; private init; }
        public Visibility HeadingVisibility => IsHeading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FactVisibility => IsHeading ? Visibility.Collapsed : Visibility.Visible;
        public Thickness RowMargin { get; private init; }
        public Brush RuleBrush { get; }

        /// <summary>A step under the rows' size, like the widget's section headings.</summary>
        public double HeadingFontSize => Math.Max(8, UiTheme.PanelFontSize - 1.5);

        public string Label { get; }
        public string Value { get; }
        public Brush LabelBrush { get; private init; }

        /// <summary>Settable at creation, for an update verdict's green, yellow or red - see VerdictRow.</summary>
        public Brush ValueBrush { get; init; }
        public double LabelWidth { get; }
        public Geometry? LogoShape { get; }
        public Brush? LogoFill { get; }
        public Visibility LogoVisibility { get; }

        // Tied to the panel font, so a bigger font in Settings brings bigger
        // logos instead of leaving them small next to the text.
        public double LogoHeight => Math.Round(UiTheme.PanelFontSize * 1.1);

        // Wordmarks like Samsung's are six times wider than tall; at full row
        // height they would take a third of the window from the value. Capped,
        // they come out shorter instead.
        public double LogoMaxWidth => LogoHeight * 4;
    }
}
