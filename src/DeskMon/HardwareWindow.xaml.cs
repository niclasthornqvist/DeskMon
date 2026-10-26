using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
///
/// One row per item - the CPU, the card, a drive - with its main check as a
/// word or two at the right, the smaller ones as short words in its grey
/// detail line, and a tinted note under it only when something is wrong. It
/// used to give every check a full green sentence of its own, which made a
/// healthy PC the longest and hardest to read.
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

        // A row with something to do on it - the stability count's "start
        // counting from now" - gets its own menu. Handled here, once, rather
        // than in the row template, since only those rows ever have one.
        Rows.MouseRightButtonUp += Rows_MouseRightButtonUp;

        _placementSave.Tick += (_, _) => SavePlacementNow();

        Closing += OnClosing;

        // The rows put out before it hides: Windows shows the last frame again
        // the instant it reopens, which would flash the old rows up before
        // the loading screen. Blank instead, and the spinner comes in from there.
        // The spinner stopped too: closed mid-load, it would turn on behind a
        // hidden window until the load ended, or for good if WMI hangs.
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
    /// <summary>
    /// <see cref="LoadCoreAsync"/> with nothing let out. Its callers start it
    /// and move on (the stability line's menu, the widget's toggle), so an
    /// error in drawing the rows went nowhere until a garbage collection
    /// happened to report it as an "unobserved task" - long after, if at all.
    /// </summary>
    public async Task LoadAsync(string? cpuName, string? gpuName)
    {
        // The number LoadCoreAsync is about to give this load.
        int generation = _loadGeneration + 1;
        try
        {
            await LoadCoreAsync(cpuName, gpuName);
        }
        catch (Exception ex)
        {
            App.Log("showing the hardware", ex);

            // A fault after the rows were drawn left them hidden behind the
            // spinner until the window was opened again. Taken down here -
            // unless a newer load has the loading screen up for itself.
            if (generation == _loadGeneration && Loading.Visibility == Visibility.Visible)
                ShowLoading(false);
        }
    }

    private async Task LoadCoreAsync(string? cpuName, string? gpuName)
    {
        int generation = ++_loadGeneration;

        // The loading screen every time, not last time's rows: those are a
        // reading from the past, and the window keeps its size either way.
        ShowLoading(true);

        // Kept for "start counting from now", which fills the window again.
        _cpuName = cpuName;
        _gpuName = gpuName;

        LoadingText.Text = "Reading hardware…";

        // Windows Update first, before the hardware is read: it needs nothing
        // from it, and it is by far the slowest answer (5 s or more when it
        // hasn't been asked in a while). Started here, it runs while the rest
        // is read instead of after it.
        var windowsTask = _settings.CheckDriverOnline ? UpdateCheck.WindowsAsync() : null;

        SystemInfo info;
        IReadOnlyList<Prerequisites.Missing> missing;
        try
        {
            // Side by side: neither needs the other, so the wait is the slower one's.
            var infoTask = Task.Run(() => SystemInfo.Collect(cpuName, gpuName, _settings.StabilityCountedFrom));
            var missingTask = Task.Run(() => Prerequisites.Check(_settings));
            info = await infoTask;
            missing = await missingTask;
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
            _rows.Add(NoteRow(new SystemInfo.Check($"Couldn't read the hardware: {ex.Message}", SystemInfo.Condition.Bad),
                              underItem: false, copy: true));
            Rows.Items.Refresh();
            ShowLoading(false);
            return;
        }
        if (generation != _loadGeneration)
            return;

        _rows.Clear();

        // --- Processor & graphics ------------------------------------------
        AddGroup("PROCESSOR & GRAPHICS");

        // The hardware-error count is the CPU's status - the one check that
        // says whether it is stable; the power plan and Intel's microcode
        // fix go into the detail line. A separate brand list per kind of
        // row: the motherboard string can name a chipset vendor ("AMD B650")
        // that is not who made the board.
        var cpuMain = info.CpuChecks.FirstOrDefault(IsErrorCount);
        var cpuRest = info.CpuChecks.Where(c => c != cpuMain).ToList();
        AddItem(Item("CPU", ShortChip(info.Cpu), info.Cpu, BrandLogos.ForChip(info.Cpu),
                     detail: Words(info.CpuDetail, cpuRest.Select(c => c.ShortText)),
                     full: Lines(info.CpuDetail, cpuRest.Select(c => c.Text)))
                    .WithStatus(cpuMain),
                info.CpuChecks);

        // The driver verdict is the card's status. NVIDIA's (or AMD's)
        // answer comes in afterwards, so the row says "Checking…" until then:
        // a slow network must not hold up the rest of the window. Without a
        // driver version there is nothing to compare, so no asking.
        string? driverMaker = info.Gpu is null || info.GpuDriverVersion is null ? null
            : UpdateCheck.IsNvidia(info.Gpu) ? "NVIDIA"
            : UpdateCheck.IsCurrentRadeon(info.Gpu) ? "AMD"
            : null;
        bool askDriver = _settings.CheckDriverOnline && driverMaker is not null;

        // Only with the online check on: off, nothing was going to be
        // asked, and "blocked" would be beside the point.
        var driverOffline = askDriver ? null
            : _settings.CheckDriverOnline && info.Gpu is not null && UpdateCheck.BlockedMaker(info.Gpu) is { } blockedGpu
            ? UpdateCheck.Blocked("Driver", blockedGpu, info.GpuDriverDate)
            : UpdateCheck.Age("Driver", info.GpuDriverDate);

        var gpu = Item("GPU", ShortChip(info.Gpu), info.Gpu, BrandLogos.ForChip(info.Gpu),
                       detail: Words(info.GpuDetail, info.GpuLink?.ShortText),
                       full: Lines(info.GpuDetail, info.GpuLink?.Text));
        gpu = askDriver ? gpu.WithStatus(Checking($"Checking {driverMaker} for a newer driver…"))
                        : gpu.WithStatus(ToCheck(driverOffline));
        AddItem(gpu, new[] { info.GpuLink, ToCheck(driverOffline) });
        var driverItem = askDriver ? gpu : null;

        // Each screen (or matching set of screens) with its refresh rate as
        // the status - the one setting that is commonly left wrong.
        bool firstDisplay = true;
        foreach (var display in info.Displays)
        {
            var (name, rest) = SplitFirst(display.Text);
            AddItem(Item(firstDisplay ? "Display" : string.Empty, name, null, BrandLogos.ForDisplay(display.Text),
                         detail: rest, full: Lines(rest))
                        .WithStatus(display.Refresh),
                    new[] { display.Refresh });
            firstDisplay = false;
        }

        // --- Memory & storage ----------------------------------------------
        AddGroup("MEMORY & STORAGE");
        AddMemory(info);

        // A drive's health is its status: the one line in this window that
        // can mean "back up now".
        bool firstDrive = true;
        foreach (var drive in info.Drives)
        {
            var (name, rest) = SplitFirst(drive.Text);
            var health = DriveStatus(drive);
            AddItem(Item(firstDrive ? "Storage" : string.Empty, name, null, BrandLogos.ForStorage(drive.Text),
                         detail: Words(rest, HealthExtras(drive.Health)),
                         full: Lines(rest))
                        .WithStatus(health),
                    new[] { health });
            firstDrive = false;
        }

        // --- Board & Windows -----------------------------------------------
        AddGroup("BOARD & WINDOWS");

        // The board by its product alone when its maker's logo is there to
        // say who made it - "ASUSTeK COMPUTER INC." before every name was
        // the longest part of the row. The BIOS rides along in the detail.
        var boardLogo = BrandLogos.ForBoard(info.Motherboard);
        string? boardName = boardLogo is not null && info.BoardProduct is not null ? info.BoardProduct : info.Motherboard;
        string? biosWords = info.BiosVersion is null ? info.Bios
            : info.BiosDate is { } biosDate ? $"BIOS {info.BiosVersion}  ·  {biosDate:yyyy-MM-dd}"
            : $"BIOS {info.BiosVersion}";

        // The system's name only when it says something the board doesn't: a
        // laptop's or a prebuilt's model. A shop's own name ("Inet_AB") or the
        // board's product again is noise - kept for Copy all the same.
        bool showSystem = info.SystemModel is { } system
                          && !system.Contains('_')
                          && (info.BoardProduct is null || !system.Contains(info.BoardProduct, StringComparison.OrdinalIgnoreCase));

        // ASUS's answer the same way as NVIDIA's: "Checking…" now, the
        // verdict when it comes. Other board makers can't be asked - see UpdateCheck.
        bool askBios = _settings.CheckDriverOnline && info.BoardVendor is not null && info.BoardProduct is not null
                       && info.BiosVersion is not null && UpdateCheck.IsAsus(info.BoardVendor);
        var biosOffline = askBios ? null
            : _settings.CheckDriverOnline && info.BoardVendor is not null && UpdateCheck.BlockedMaker(info.BoardVendor) is { } blockedBoard
            ? UpdateCheck.Blocked("BIOS", blockedBoard, info.BiosDate)
            : UpdateCheck.Age("BIOS", info.BiosDate);

        var board = Item("Board", boardName, info.Motherboard, boardLogo,
                         detail: Words(biosWords),
                         full: Lines(info.Bios is null ? null : $"BIOS {info.Bios}", showSystem ? null : info.SystemModel is { } hidden ? $"System {hidden}" : null));
        board = askBios ? board.WithStatus(Checking("Checking ASUS for a newer BIOS…"))
                        : board.WithStatus(ToCheck(biosOffline));
        AddItem(board, new[] { ToCheck(biosOffline) });
        var biosItem = askBios ? board : null;

        if (showSystem)
            AddItem(Item("System", info.SystemModel, null, BrandLogos.ForBoard(info.SystemModel), detail: null, full: Lines()),
                    Array.Empty<SystemInfo.Check>());

        // Windows Update's answer is its status, asked behind the same
        // setting - it asks Microsoft's servers. The crash count goes into
        // the detail while it is clean, and becomes a note when it isn't;
        // both the row and the note keep the right-click menu.
        var (osName, osRest) = SplitFirst(info.OperatingSystem);
        var stability = info.Stability;
        bool stabilityNote = stability is { Level: >= SystemInfo.Condition.Warning };
        var windows = Item("Windows", osName, info.OperatingSystem, osName is null ? null : BrandLogos.Windows,
                           detail: Words(osRest, info.UpSince is null ? null : $"up since {info.UpSince}",
                                         stability is not null && !stabilityNote ? stability.ShortText : null),
                           full: Lines(info.UpSince is null ? null : $"Up since {info.UpSince}", stability?.Text));
        windows = (windowsTask is not null ? windows.WithStatus(Checking("Asking Windows Update for waiting updates…")) : windows)
                  .WithStabilityMenu(stability is not null);
        _rows.Add(windows);
        if (stabilityNote)
            _rows.Add(NoteRow(stability!, underItem: true).WithStabilityMenu(true));
        var windowsItem = windowsTask is not null ? windows : null;

        // --- Gaming --------------------------------------------------------
        // Windows settings that change how games run, and the two that
        // decide whether some start at all: a two-column list of ticks, so
        // they read as a checklist rather than as facts about Windows.
        if (info.GamingChecks.Count > 0)
        {
            AddGroup("GAMING");
            _rows.Add(TogglesRow(info.GamingChecks));
            foreach (var check in info.GamingChecks.Where(c => c.Level >= SystemInfo.Condition.Warning))
                _rows.Add(NoteRow(check, underItem: true));
        }

        // --- Network -------------------------------------------------------
        // The link check of each adapter in use replaces its speed in the
        // detail ("linked at 1 Gbit/s" says the same and more). The ping
        // test is the first connected adapter's status, filled in when it is
        // done, behind the same setting: it sends pings out to the internet.
        // A disconnected adapter is greyed: listed, but plainly not in use.
        string pingHost = string.IsNullOrWhiteSpace(_settings.PingHost) ? NetworkCheck.DefaultHost : _settings.PingHost.Trim();
        InfoRow? pingItem = null;
        bool firstAdapter = true;
        int link = 0;
        foreach (var adapter in info.NetworkAdapters)
        {
            // Inside the loop, so a PC with no adapter found gets no empty heading.
            if (firstAdapter)
                AddGroup("NETWORK");

            var (name, rest) = SplitFirst(adapter.Description);
            var linkCheck = adapter.Connected && link < info.NetworkChecks.Count ? info.NetworkChecks[link++] : null;
            bool pingHere = adapter.Connected && pingItem is null && _settings.CheckDriverOnline;

            var row = Item(firstAdapter ? "Network" : string.Empty, name, null, BrandLogos.ForNetwork(adapter.Description),
                           detail: linkCheck?.ShortText ?? rest,
                           full: Lines(rest, linkCheck?.Text),
                           dim: !adapter.Connected);
            row = pingHere ? row.WithStatus(Checking($"Measuring ping to {pingHost}…")) : row.WithStatus(linkCheck);
            AddItem(row, new[] { linkCheck });
            if (pingHere)
                pingItem = row;
            firstAdapter = false;
        }

        // How far the usual game-server regions are, under the ping test and
        // behind the same setting - see NetworkCheck.Regions. Only with a
        // connection to test: without one every row would say "No answer".
        _regionRows.Clear();
        if (pingItem is not null)
        {
            bool firstRegion = true;
            foreach (var (region, _) in NetworkCheck.Regions)
            {
                var row = Item(firstRegion ? "Regions" : string.Empty, region, null, null, detail: null, full: new List<string>())
                          .WithStatus(Checking($"Measuring the way to {region}…"));
                AddItem(row, Array.Empty<SystemInfo.Check>());
                _regionRows.Add(row);
                firstRegion = false;
            }
        }

        // Here because this is the window people open to ask "what does this
        // machine have" - and on a fresh install, what it lacks is the part
        // that explains the n/a on the widget. As warning notes so it can't
        // be read as one more fact about the hardware. Copy picks these up
        // too, which is handy when asking someone for help.
        if (missing.Count > 0)
        {
            AddGroup("NEEDS INSTALLING", warning: true);
            foreach (var item in missing)
                _rows.Add(NoteRow(new SystemInfo.Check($"{item.Name} - {item.Effect}. Install: {item.Install}", SystemInfo.Condition.Warning),
                                  underItem: false, copy: true));
        }

        MarkGroupEnds();
        Rows.Items.Refresh();
        RowsScroller.ScrollToTop();

        // Both started before either is awaited, so the two run side by side
        // and the wait is the slower one's, not the two added together.
        var driverTask = driverItem is null ? null
            : driverMaker == "AMD" ? UpdateCheck.AmdDriverAsync(info.GpuDriverVersion, info.GpuDriverDate)
            : UpdateCheck.NvidiaDriverAsync(info.Gpu!, info.GpuDriverVersion, info.GpuDriverDate);
        var biosTask = biosItem is null ? null
            : UpdateCheck.AsusBiosAsync(info.BoardProduct!, info.BiosVersion, info.BiosDate);
        var pingTask = pingItem is null ? null : NetworkCheck.PingAsync(pingHost);
        var regionsTask = _regionRows.Count == 0 ? null : NetworkCheck.RegionsAsync();

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
            SetStatus(driverItem!, ToCheck(await driverTask), generation);
        if (biosTask is not null)
            SetStatus(biosItem!, ToCheck(await biosTask), generation);
        // Last: it is the slowest of the three by far.
        if (windowsTask is not null)
            SetStatus(windowsItem!, ToCheck(await windowsTask), generation);

        // Every update answer already in its row, so the rows appear finished
        // - all but the ping, which fills in after.
        if (allInTime && generation == _loadGeneration)
            ShowLoading(false);

        if (pingTask is not null)
            SetStatus(pingItem!, await pingTask, generation, pingAgain: true);
        if (regionsTask is not null)
            ShowRegions(await regionsTask, generation);
    }

    /// <summary>The region rows, in NetworkCheck.Regions' order, as they now stand - SetStatus swaps in a copy.</summary>
    private readonly List<InfoRow> _regionRows = new();

    private void ShowRegions(IReadOnlyList<SystemInfo.Check> checks, int generation)
    {
        for (int i = 0; i < _regionRows.Count && i < checks.Count; i++)
            if (SetStatus(_regionRows[i], checks[i], generation) is { } done)
                _regionRows[i] = done;
    }

    /// <summary>
    /// The memory as one row: the total, what it is made of, its speed and
    /// the smaller checks, with the error count as its status - it was a
    /// "Memory" row, a "Modules" row and four sentences. A mixed set of
    /// sticks keeps a row per kind under it, since there the kinds are the
    /// news; a matched kit (the usual) needs only the one.
    /// </summary>
    private void AddMemory(SystemInfo info)
    {
        string? total = info.MemoryTotal?.Split(" in ")[0];
        var main = info.MemoryChecks.FirstOrDefault(IsErrorCount);
        var rest = info.MemoryChecks.Where(c => c != main).ToList();
        var checkWords = rest.Select(c => c.ShortText);
        var checkLines = rest.Select(c => c.Text);

        if (info.MemoryModules.Count <= 1)
        {
            string? module = info.MemoryModules.FirstOrDefault();
            var (countSize, speed) = ParseModule(module);
            string? name = total is null ? countSize
                         : countSize is null ? total
                         : $"{total}  ·  {countSize}";
            AddItem(Item("Memory", name, info.MemoryTotal, BrandLogos.ForMemory(module),
                         detail: Words(speed, checkWords),
                         full: Lines(module, checkLines))
                        .WithStatus(main),
                    info.MemoryChecks);
            return;
        }

        AddItem(Item("Memory", total, info.MemoryTotal, null,
                     detail: Words(checkWords), full: Lines(checkLines))
                    .WithStatus(main),
                info.MemoryChecks);
        foreach (string module in info.MemoryModules)
        {
            var (name, moduleRest) = SplitFirst(module);
            AddItem(Item(string.Empty, name, null, BrandLogos.ForMemory(module), detail: moduleRest, full: Lines(moduleRest)),
                    Array.Empty<SystemInfo.Check>());
        }
    }

    /// <summary>
    /// The ping test's button: measures again into the same row, without
    /// reading the whole window again - a fresh look at the connection
    /// shouldn't mean waiting on WMI and the update checks too. The host is
    /// read from the settings now, so one changed since the window opened is
    /// the one pinged.
    /// </summary>
    private async void PingAgain_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InfoRow done)
            return;

        int generation = _loadGeneration;
        string host = string.IsNullOrWhiteSpace(_settings.PingHost) ? NetworkCheck.DefaultHost : _settings.PingHost.Trim();

        // Back to "measuring", with no button, so a second click can't start
        // a second test while the first is still running.
        var measuring = SetStatus(done, Checking($"Measuring ping to {host}…"), generation);
        if (measuring is null)
            return;

        // The regions again too, side by side with the ping: a fresh look
        // at the connection is a fresh look at all of it.
        for (int i = 0; i < _regionRows.Count; i++)
            if (SetStatus(_regionRows[i], Checking($"Measuring the way to {NetworkCheck.Regions[i].Name}…"), generation) is { } waiting)
                _regionRows[i] = waiting;
        var regionsTask = _regionRows.Count == 0 ? null : NetworkCheck.RegionsAsync();

        try
        {
            var ping = await NetworkCheck.PingAsync(host);
            SetStatus(measuring, ping, generation, pingAgain: true);
            if (regionsTask is not null)
                ShowRegions(await regionsTask, generation);
        }
        catch (Exception ex)
        {
            // async void: let out, this would take the program down. The
            // button comes back so the test can be tried again.
            App.Log("pinging again", ex);
            SetStatus(measuring, new SystemInfo.Check("Ping test failed", SystemInfo.Condition.Unknown, "Failed"), generation, pingAgain: true);
        }
    }

    /// <summary>
    /// The longest the loading screen waits on the online checks - see
    /// LoadAsync. Short: the driver and BIOS answers come in well under it,
    /// and waiting out a slow Windows Update (15 s, as it was) made the whole
    /// window feel slow for one line that can just as well fill in late.
    /// </summary>
    private static readonly TimeSpan MaxLoadingWait = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Puts a late answer - an update verdict, the ping - into its row's
    /// status, and a note under the row when it is bad news. A note from an
    /// earlier answer goes first, or pinging again would stack them up.
    /// Returns the row as it now stands, or null when a newer load has
    /// rebuilt the rows (IndexOf would miss anyway; checked so the intent
    /// doesn't hang on that coincidence).
    /// </summary>
    private InfoRow? SetStatus(InfoRow item, SystemInfo.Check? status, int generation, bool pingAgain = false)
    {
        if (generation != _loadGeneration)
            return null;
        int at = _rows.IndexOf(item);
        if (at < 0)
            return null;

        if (item.AttachedNote is { } old && _rows.IndexOf(old) is var oldAt and >= 0)
            _rows.RemoveAt(oldAt);

        var done = item.WithStatus(status, pingAgain);
        _rows[at] = done;
        if (status is { Level: >= SystemInfo.Condition.Warning })
        {
            var note = NoteRow(status, underItem: true);
            _rows.Insert(at + 1, note);
            done.AttachedNote = note;
        }

        // A note put in or taken out moves where a group ends.
        MarkGroupEnds();
        Rows.Items.Refresh();
        return done;
    }

    /// <summary>
    /// An item row, and a note under it for each of its checks that is a
    /// warning or worse. The note carries the full sentence: there the
    /// advice in it ("update the BIOS", "back it up now") is the point,
    /// where for a healthy check the short word at the right says enough.
    /// </summary>
    private void AddItem(InfoRow item, IEnumerable<SystemInfo.Check?> checks)
    {
        _rows.Add(item);
        foreach (var check in checks)
            if (check is { Level: >= SystemInfo.Condition.Warning })
                _rows.Add(NoteRow(check, underItem: true));
    }

    /// <summary>
    /// Drops the grey rule under the last item of each group: the next
    /// group's accent line follows right after it, and the two lines on top
    /// of each other read as a double border. Worked out over the whole list
    /// rather than when a row is added, since a late answer's note can land
    /// after an item and change which row ends its group.
    /// </summary>
    private void MarkGroupEnds()
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].LastInGroup = i + 1 == _rows.Count || _rows[i + 1].Kind == RowKind.Heading;
    }

    /// <summary>Starts a group of rows under a heading - see InfoRow.Heading.</summary>
    private void AddGroup(string heading, bool warning = false)
        => _rows.Add(InfoRow.Heading(heading, _theme, _settings, first: _rows.Count == 0, warning));

    /// <summary>
    /// One item. <paramref name="name"/> is what the row shows; <paramref name="fullName"/>,
    /// when it was shortened for the row, is what Copy and the tooltip give,
    /// with <paramref name="full"/> - the long forms of the detail and checks.
    /// </summary>
    private InfoRow Item(string label, string? name, string? fullName, BrandLogos.Logo? logo,
                         string? detail, List<string> full, bool dim = false)
        => new(RowKind.Item, _theme, _settings)
        {
            Label = label,
            Name = name ?? "unknown",
            FullName = fullName ?? name ?? "unknown",
            NameBrush = name is null || dim ? _theme.Unavailable : _theme.NotesText,
            Detail = detail,
            FullLines = full,
            LogoShape = logo?.Shape,
            LogoFill = logo is null ? null : logo.Fill ?? _theme.NotesText,
        };

    /// <summary>
    /// A tinted note with the full sentence - under an item it is about, or
    /// on its own (the missing prerequisites, a failed read). <paramref name="copy"/>
    /// for the ones on their own: a note under an item is already in that
    /// item's lines for Copy.
    /// </summary>
    private InfoRow NoteRow(SystemInfo.Check check, bool underItem, bool copy = false)
        => new InfoRow(RowKind.Note, _theme, _settings)
        {
            Name = check.Text,
            CopyText = copy ? check.Text : null,
            NoteIndent = underItem,
        }.WithStatus(check);

    /// <summary>The gaming checks as ticks, two to a line, each with its full sentence as the tooltip.</summary>
    private InfoRow TogglesRow(IReadOnlyList<SystemInfo.Check> checks)
        => new(RowKind.Toggles, _theme, _settings)
        {
            LogoShape = BrandLogos.Gaming.Shape,
            LogoFill = _theme.Label,
            Toggles = checks.Select(c => new Toggle(c, _theme)).ToList(),
        };

    /// <summary>A "Checking…" status, with what is being asked as its tooltip.</summary>
    private static SystemInfo.Check Checking(string what)
        => new(what, SystemInfo.Condition.Unknown, what.StartsWith("Measuring", StringComparison.Ordinal) ? "Measuring…" : "Checking…");

    /// <summary>An update verdict in the checks' terms, so one row type and one colour rule serve both.</summary>
    private static SystemInfo.Check? ToCheck(UpdateCheck.Verdict? verdict)
        => verdict is null ? null
         : new SystemInfo.Check(verdict.Text, verdict.Level switch
         {
             UpdateCheck.Freshness.Current => SystemInfo.Condition.Good,
             UpdateCheck.Freshness.Aging => SystemInfo.Condition.Warning,
             UpdateCheck.Freshness.Old => SystemInfo.Condition.Bad,
             _ => SystemInfo.Condition.Unknown,
         }, verdict.ShortText);

    /// <summary>
    /// A drive's health as a check for its status. The health line starts
    /// with the verdict ("Healthy", "Warning - back it up"), which becomes
    /// the word at the right; the wear and temperature after it go into the
    /// detail - see HealthExtras.
    /// </summary>
    private static SystemInfo.Check? DriveStatus(SystemInfo.Drive drive)
        => drive.Health is null ? null
         : new SystemInfo.Check(drive.Health, drive.Level, drive.Level switch
         {
             SystemInfo.Condition.Good => "Healthy",
             SystemInfo.Condition.Warning => "Check it",
             SystemInfo.Condition.Bad => "Back up now",
             _ => "No data",
         });

    private static IEnumerable<string> HealthExtras(string? health)
    {
        if (health is null)
            return Array.Empty<string>();
        var parts = health.Split(Separator);
        bool verdictFirst = parts[0].StartsWith("Healthy", StringComparison.Ordinal)
                            || parts[0].StartsWith("Warning", StringComparison.Ordinal)
                            || parts[0].StartsWith("Unhealthy", StringComparison.Ordinal);
        return verdictFirst ? parts.Skip(1) : parts;
    }

    /// <summary>"Errors" checks are an item's status where it has one - the CPU's and the memory's.</summary>
    private static bool IsErrorCount(SystemInfo.Check check)
        => check.ShortText.Contains("error", StringComparison.OrdinalIgnoreCase);

    /// <summary>The separator SystemInfo joins its parts with.</summary>
    private const string Separator = "  ·  ";

    /// <summary>"2 × Acer XV272 X  ·  1920 × 1080  ·  240 Hz" to its name and the rest, for the name and the detail line.</summary>
    private static (string? Head, string? Tail) SplitFirst(string? text)
    {
        if (text is null)
            return (null, null);
        int at = text.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? (text, null) : (text[..at], text[(at + Separator.Length)..]);
    }

    /// <summary>
    /// One memory line's "2 × 16 GB" and "6000 MT/s", picked out by their
    /// units - the maker and part number are left for the tooltip and Copy,
    /// since the logo says who made it and the part number is long.
    /// </summary>
    private static (string? CountSize, string? Speed) ParseModule(string? module)
    {
        if (module is null)
            return (null, null);
        var parts = module.Split(Separator);
        var count = Regex.Match(parts[0], @"^(\d+) × ");
        string? size = parts.FirstOrDefault(p => p.EndsWith(" GB", StringComparison.Ordinal));
        string? speed = parts.FirstOrDefault(p => p.EndsWith("MT/s", StringComparison.Ordinal));
        return (size is null ? null : count.Success ? $"{count.Groups[1].Value} × {size}" : size, speed);
    }

    /// <summary>
    /// A chip's name without what the logo next to it already says, and the
    /// marketing around it: "AMD Ryzen 7 7800X3D 8-Core Processor" to
    /// "Ryzen 7 7800X3D", "NVIDIA GeForce RTX 4070 SUPER" to "RTX 4070 SUPER".
    /// The full name stays for the tooltip and Copy.
    /// </summary>
    private static string? ShortChip(string? name)
    {
        if (name is null)
            return null;
        string s = Regex.Replace(name, @"\((R|TM)\)|®|™", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(\d+-Core\s+)?Processor\b.*$|\s+CPU\s*@.*$|\s+with Radeon Graphics.*$", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^(AMD|Intel|NVIDIA)\s+|^GeForce\s+|^Radeon\s+(?=RX)", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^GeForce\s+", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length > 0 ? s : name;
    }

    /// <summary>The detail line: the parts that are there, joined the way SystemInfo joins its own.</summary>
    private static string? Words(params object?[] parts)
    {
        var words = Flatten(parts).ToList();
        return words.Count > 0 ? string.Join(Separator, words) : null;
    }

    /// <summary>The long forms for Copy and the tooltip, one per line.</summary>
    private static List<string> Lines(params object?[] parts) => Flatten(parts).ToList();

    private static IEnumerable<string> Flatten(object?[] parts)
    {
        foreach (var part in parts)
        {
            if (part is string text)
            {
                if (text.Length > 0)
                    yield return text;
            }
            else if (part is IEnumerable<string> many)
            {
                foreach (var item in many)
                    if (item.Length > 0)
                        yield return item;
            }
        }
    }

    /// <summary>
    /// Right-click on the Windows row or the shutdown note: count crashes and
    /// shutdowns from now on - or from 30 days back again, once a start has
    /// been set. For shutdowns that were the power button rather than a
    /// fault, which Windows logs no differently.
    /// </summary>
    private void Rows_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not InfoRow { HasStabilityMenu: true })
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

    /// <summary>
    /// The loading screen in place of the rows, or the rows fading in in its
    /// place. The ring only spins while it is up: this window can be open
    /// over a game, and an animation left running behind the rows would cost
    /// a redraw every frame for nothing.
    /// </summary>
    private void ShowLoading(bool on)
    {
        Loading.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RowsScroller.Visibility = on ? Visibility.Hidden : Visibility.Visible;

        if (on)
        {
            // The ring turns at an even pace; the chip's core breathes slower
            // and out of step with it, so the two don't read as one blink.
            LoadingSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                });
            LoadingCore.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(700))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                });
        }
        else
        {
            StopDots();
        }

        if (!on)
            RowsScroller.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>The loading animation still, the loading screen left as it is - for a window going away mid-load.</summary>
    private void StopDots()
    {
        LoadingSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        LoadingCore.BeginAnimation(OpacityProperty, null);
    }

    // --- Chrome -----------------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleHeaderMouseDown(e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Handy when posting a spec somewhere or filing a support ticket. The
    /// full text, not the window's short forms: whoever reads it later has
    /// no tooltip to hover, and "No errors" alone doesn't say of what.
    /// </summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var row in _rows)
        {
            switch (row.Kind)
            {
                // A heading as a blank line and the heading alone, so the
                // pasted text keeps the same groups as the window.
                case RowKind.Heading:
                    if (text.Length > 0)
                        text.AppendLine();
                    text.AppendLine(row.Name);
                    break;

                case RowKind.Item:
                    text.AppendLine($"{row.Label,-14}{row.FullName}");
                    foreach (string line in row.FullLines.Append(row.StatusFull ?? string.Empty).Where(l => l.Length > 0))
                        text.AppendLine($"{string.Empty,-14}{line}");
                    break;

                case RowKind.Toggles:
                    foreach (var toggle in row.Toggles)
                        text.AppendLine($"{string.Empty,-14}{toggle.Full}");
                    break;

                case RowKind.Note when row.CopyText is not null:
                    text.AppendLine($"{string.Empty,-14}{row.CopyText}");
                    break;
            }
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

        // The moving parts - the arc and the chip's core - in the accent, the
        // still outline in the label colour, so the eye goes to the motion.
        LoadingTrack.Stroke = _theme.Separator;
        LoadingArc.Stroke = _theme.Accent;
        LoadingPins.Stroke = _theme.Label;
        LoadingBody.Stroke = _theme.Label;
        LoadingCore.Fill = _theme.Accent;
        LoadingText.Foreground = _theme.Label;

        TitleText.Foreground = _theme.Section;
        TitleText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        FontSize = UiTheme.PanelFontSize;
        FontFamily = _theme.PanelFont;
    }

    // --- Rows ---------------------------------------------------------------

    private enum RowKind { Heading, Item, Note, Toggles }

    /// <summary>
    /// The mark beside a status: a tick, "!", a cross or a dash, on a faint
    /// disc of the same colour. Drawn as strokes on a 12 px square so they
    /// stay crisp at any font size; frozen, since every row shares them.
    /// </summary>
    private static class StatusMarks
    {
        public static readonly Geometry Good = Make("M3.4,6.3 L5.2,8.1 L8.7,4.2");
        public static readonly Geometry Warning = Make("M6,3.2 V6.6 M6,8.7 V8.8");
        public static readonly Geometry Bad = Make("M4.2,4.2 L7.8,7.8 M7.8,4.2 L4.2,7.8");
        public static readonly Geometry Unknown = Make("M4,6 H8");

        private static Geometry Make(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }

        public static Geometry For(SystemInfo.Condition level) => level switch
        {
            SystemInfo.Condition.Good => Good,
            SystemInfo.Condition.Warning => Warning,
            SystemInfo.Condition.Bad => Bad,
            _ => Unknown,
        };
    }

    /// <summary>The theme's green, yellow or red for a level - the same colours as the widget's bars, so they mean one thing.</summary>
    private static Brush LevelBrush(UiTheme theme, SystemInfo.Condition level) => level switch
    {
        SystemInfo.Condition.Good => theme.Normal,
        SystemInfo.Condition.Warning => theme.Warn,
        SystemInfo.Condition.Bad => theme.Crit,
        _ => theme.Label,
    };

    /// <summary>A see-through copy of a level's colour, for the disc behind a mark and a note's background.</summary>
    private static Brush Tint(Brush brush, byte alpha)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Gray;
        var tint = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        tint.Freeze();
        return tint;
    }

    /// <summary>One gaming check as a tick and a word or two.</summary>
    private sealed class Toggle
    {
        public Toggle(SystemInfo.Check check, UiTheme theme)
        {
            Text = check.ShortText;
            Full = check.Text;
            Mark = StatusMarks.For(check.Level);
            MarkBrush = LevelBrush(theme, check.Level);
            MarkTint = Tint(MarkBrush, 0x33);
            TextBrush = check.Level >= SystemInfo.Condition.Warning ? MarkBrush : theme.NotesText;
        }

        public string Text { get; }
        public string Full { get; }
        public Geometry Mark { get; }
        public Brush MarkBrush { get; }
        public Brush MarkTint { get; }
        public Brush TextBrush { get; }
    }

    /// <summary>
    /// One row of the window: a heading, an item, a note or the gaming ticks.
    /// One class with a kind rather than four, because one ItemsControl
    /// template shows them all - each part of it collapsed unless the kind
    /// is its own. Static once built: a late answer replaces the row with a
    /// changed copy (WithStatus) rather than notifying.
    /// </summary>
    private sealed class InfoRow
    {
        private readonly UiTheme _theme;

        public InfoRow(RowKind kind, UiTheme theme, AppSettings settings)
        {
            _theme = theme;
            Kind = kind;
            LabelWidth = settings.HardwareLabelWidth;
            LabelBrush = theme.Label;
            DetailBrush = theme.Label;
            NameBrush = theme.NotesText;
            RuleBrush = theme.Separator;
            RowMargin = new Thickness(0);
        }

        /// <summary>
        /// A group's heading. Space above it, except for the first, which the
        /// window's own header rule already separates from the title. In the
        /// section colour like the widget's headings - or the warning colour,
        /// for the group that lists what is missing.
        /// </summary>
        public static InfoRow Heading(string text, UiTheme theme, AppSettings settings, bool first, bool warning = false)
            => new(RowKind.Heading, theme, settings)
            {
                Name = text,
                LabelBrush = warning ? theme.Warn : theme.Section,
                RowMargin = new Thickness(0, first ? 2 : 14, 0, 2),
            };

        public RowKind Kind { get; }

        public Visibility HeadingVisibility => Kind == RowKind.Heading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ItemVisibility => Kind == RowKind.Item ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoteVisibility => Kind == RowKind.Note ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TogglesVisibility => Kind == RowKind.Toggles ? Visibility.Visible : Visibility.Collapsed;

        public Thickness RowMargin { get; init; }
        public Brush RuleBrush { get; }

        /// <summary>No rule under the last row of a group - see MarkGroupEnds.</summary>
        public bool LastInGroup { get; set; }
        public Thickness RuleThickness => new(0, 0, 0, LastInGroup ? 0 : 1);

        /// <summary>A step under the rows' size, like the widget's section headings.</summary>
        public double HeadingFontSize => Math.Max(8, UiTheme.PanelFontSize - 1.5);

        /// <summary>The detail line a step smaller than the name, so the name is what the eye finds first.</summary>
        public double DetailFontSize => Math.Max(8, UiTheme.PanelFontSize - 1);

        public string Label { get; init; } = string.Empty;
        public Brush LabelBrush { get; init; }
        public double LabelWidth { get; }

        public string Name { get; init; } = string.Empty;
        public string FullName { get; init; } = string.Empty;
        public Brush NameBrush { get; init; }

        public string? Detail { get; init; }
        public Brush DetailBrush { get; }
        public Visibility DetailVisibility => Detail is null ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>The long forms of the detail and checks, for Copy and the tooltip.</summary>
        public List<string> FullLines { get; init; } = new();

        /// <summary>The name and everything behind the short forms, on hover.</summary>
        public string ToolTip => string.Join("\n", new[] { FullName }.Concat(FullLines).Append(StatusFull ?? string.Empty).Where(l => l.Length > 0));

        public Geometry? LogoShape { get; init; }
        public Brush? LogoFill { get; init; }

        // Tied to the panel font, so a bigger font in Settings brings bigger
        // logos instead of leaving them small next to the text.
        public double LogoHeight => Math.Round(UiTheme.PanelFontSize * 1.1);

        /// <summary>
        /// One width for the logo column on every row, so the names all start
        /// in one line whether their logo is a square mark, a wide wordmark
        /// or missing - a wordmark fits itself in by coming out shorter.
        /// </summary>
        public double LogoColumnWidth => LogoHeight * 2.6;

        /// <summary>
        /// Where the names start, for the parts that line up with them - a
        /// note under an item, the gaming ticks: the label column, the logo
        /// column and the gap after it.
        /// </summary>
        public double NameIndent => LabelWidth + LogoColumnWidth + LogoGap;
        public Thickness TogglesMargin => new(LogoColumnWidth + LogoGap, 0, 0, 0);

        /// <summary>
        /// The space between the logo and the name. One value for the item rows
        /// and for everything that lines up with the names, so widening it
        /// can't leave the notes and ticks a few pixels off.
        /// </summary>
        private const double LogoGap = 12;
        public Thickness LogoMargin => new(0, 0, LogoGap, 0);

        // The status at the right: a mark and a word or two.
        public string? StatusText { get; private set; }
        public string? StatusFull { get; private set; }
        public Geometry StatusMark { get; private set; } = StatusMarks.Unknown;
        public Brush? StatusBrush { get; private set; }
        public Brush? StatusTint { get; private set; }
        public Brush? StatusTextBrush { get; private set; }
        public Visibility StatusVisibility => StatusText is null ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>The ping-again button, on the ping test's row once it has an answer - see SetStatus.</summary>
        public Visibility RetryVisibility { get; private set; } = Visibility.Collapsed;

        /// <summary>A note's background: its level's colour, faint.</summary>
        public Brush? NoteTint { get; private set; }

        /// <summary>Under an item, a note lines up with the names rather than the labels; on its own it takes the full width.</summary>
        public bool NoteIndent { get; init; }
        public Thickness NoteMargin => new(NoteIndent ? NameIndent : 0, 2, 0, 4);

        /// <summary>What Copy gives for a note on its own; null for one under an item, which Copy already has.</summary>
        public string? CopyText { get; init; }

        /// <summary>The note a late answer put under this row, taken out again when the next answer comes - see SetStatus.</summary>
        public InfoRow? AttachedNote { get; set; }

        public bool HasStabilityMenu { get; private set; }

        public List<Toggle> Toggles { get; init; } = new();

        /// <summary>
        /// A copy with this status - or none. The word in the text colour
        /// while all is well and in the level's colour when it isn't, so a
        /// problem stands out without every green word shouting.
        /// </summary>
        public InfoRow WithStatus(SystemInfo.Check? status, bool pingAgain = false)
        {
            var copy = (InfoRow)MemberwiseClone();
            copy.AttachedNote = null;
            copy.RetryVisibility = pingAgain ? Visibility.Visible : Visibility.Collapsed;
            if (status is null)
            {
                copy.StatusText = copy.StatusFull = null;
                return copy;
            }

            var brush = LevelBrush(_theme, status.Level);
            copy.StatusText = status.ShortText;
            copy.StatusFull = status.Text;
            copy.StatusMark = StatusMarks.For(status.Level);
            copy.StatusBrush = brush;
            copy.StatusTint = Tint(brush, 0x33);
            copy.NoteTint = Tint(brush, 0x22);
            copy.StatusTextBrush = status.Level >= SystemInfo.Condition.Warning ? brush
                                 : status.Level == SystemInfo.Condition.Good ? _theme.NotesText
                                 : _theme.Label;
            return copy;
        }

        /// <summary>A copy that opens the "start counting from now" menu on right-click.</summary>
        public InfoRow WithStabilityMenu(bool on)
        {
            var copy = (InfoRow)MemberwiseClone();
            copy.HasStabilityMenu = on;
            return copy;
        }
    }
}
