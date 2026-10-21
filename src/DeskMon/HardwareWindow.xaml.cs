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
    /// does. This one matters twice over: SizeChanged also fires while the
    /// window re-measures itself around the rows that just arrived.
    /// </summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private bool _allowClose;
    private bool _loaded;

    /// <summary>
    /// Bumped by every load. Closing and reopening while NVIDIA is still being
    /// asked starts a second load; without this the first one could finish
    /// last and put its older rows back over the newer ones.
    /// </summary>
    private int _loadGeneration;

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
        RestorePlacement(_settings.HardwareLeft, _settings.HardwareTop,
                         _settings.HardwareWidth, null);

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.HardwareOpacity);
        Rows.ItemsSource = _rows;

        _placementSave.Tick += (_, _) => SavePlacementNow();

        Closing += OnClosing;
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

        // "Reading…" only the first time. On a reopen the last rows stay up
        // until the fresh ones replace them, rather than the window collapsing
        // to one line and growing back on every open.
        if (!_loaded)
            Show("Reading…", string.Empty);
        _loaded = true;

        var info = await Task.Run(() => SystemInfo.Collect(cpuName, gpuName));
        var missing = await Task.Run(() => Prerequisites.Check(_settings));
        if (generation != _loadGeneration)
            return;

        _rows.Clear();
        // A separate brand list per kind of row: the motherboard string can
        // name a chipset vendor ("AMD B650") that is not who made the board.
        Add("CPU", info.Cpu, BrandLogos.ForChip(info.Cpu));
        AddDetail(info.CpuDetail);
        Add("Graphics", info.Gpu, BrandLogos.ForChip(info.Gpu));
        AddDetail(info.GpuDetail);

        // NVIDIA's answer is filled in afterwards, into a row that says it is
        // checking: a slow network must not hold up the rest of the window.
        // Without a driver version there is nothing to compare, so no asking.
        InfoRow? driverCheck = null;
        if (_settings.CheckDriverOnline && info.Gpu is not null && info.GpuDriverVersion is not null
            && UpdateCheck.IsNvidia(info.Gpu))
        {
            driverCheck = VerdictRow(new UpdateCheck.Verdict("Checking NVIDIA for a newer driver…", Outdated: false));
            _rows.Add(driverCheck);
        }
        else
        {
            AddVerdict(UpdateCheck.Age("Driver", info.GpuDriverDate));
        }

        Add("Memory", info.MemoryTotal);

        // One row per stick, labelled only once so the column stays clean.
        // Each stick gets its own logo, since mixed kits do happen.
        bool firstModule = true;
        foreach (string module in info.MemoryModules)
        {
            Add(firstModule ? "Modules" : string.Empty, module, BrandLogos.ForMemory(module));
            firstModule = false;
        }

        bool firstDrive = true;
        foreach (string drive in info.Drives)
        {
            Add(firstDrive ? "Storage" : string.Empty, drive, BrandLogos.ForStorage(drive));
            firstDrive = false;
        }

        Add("Motherboard", info.Motherboard, BrandLogos.ForBoard(info.Motherboard));
        Add("BIOS", info.Bios);
        AddVerdict(UpdateCheck.Age("BIOS", info.BiosDate));
        Add("System", info.SystemModel);
        Add("OS", info.OperatingSystem, info.OperatingSystem is null ? null : BrandLogos.Windows);
        Add("Up since", info.UpSince);

        // A disconnected adapter goes in the "unknown" grey: still listed,
        // but plainly not the one in use.
        bool firstAdapter = true;
        foreach (var adapter in info.NetworkAdapters)
        {
            _rows.Add(new InfoRow(firstAdapter ? "Network" : string.Empty, adapter.Description,
                                  _theme, _settings, known: adapter.Connected,
                                  logo: BrandLogos.ForNetwork(adapter.Description)));
            firstAdapter = false;
        }

        // Here because this is the window people open to ask "what does this
        // machine have" - and on a fresh install, what it lacks is the part
        // that explains the n/a on the widget. In the warning colour so it
        // can't be read as one more fact about the hardware. Copy picks these
        // up too, which is handy when asking someone for help.
        bool firstMissing = true;
        foreach (var item in missing)
        {
            _rows.Add(new InfoRow(firstMissing ? "Missing" : string.Empty,
                                  $"{item.Name} - {item.Effect}. Install: {item.Install}",
                                  _theme, _settings, known: true, warning: true));
            firstMissing = false;
        }

        Rows.Items.Refresh();
        ResizeToContent();

        if (driverCheck is not null)
        {
            var verdict = await UpdateCheck.NvidiaDriverAsync(info.Gpu!, info.GpuDriverVersion, info.GpuDriverDate);
            // A newer load has rebuilt _rows, so IndexOf would already miss -
            // checked anyway so the intent doesn't hang on that coincidence.
            if (generation != _loadGeneration)
                return;
            int at = _rows.IndexOf(driverCheck);
            if (at >= 0)
            {
                _rows[at] = VerdictRow(verdict);
                Rows.Items.Refresh();
                ResizeToContent();
            }
        }
    }

    /// <summary>
    /// An update verdict, as a line under the row it is about. Outdated ones
    /// in the warning colour, the rest ("newest", "couldn't check") dimmed
    /// like the other second lines.
    /// </summary>
    private InfoRow VerdictRow(UpdateCheck.Verdict verdict)
        => new(string.Empty, verdict.Text, _theme, _settings, known: true,
               warning: verdict.Outdated, secondary: !verdict.Outdated);

    private void AddVerdict(UpdateCheck.Verdict? verdict)
    {
        if (verdict is not null)
            _rows.Add(VerdictRow(verdict));
    }

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

    private void Show(string label, string value)
    {
        _rows.Clear();
        _rows.Add(new InfoRow(label, value, _theme, _settings, known: false));
        Rows.Items.Refresh();
    }

    /// <summary>
    /// The window sizes to its content, and the content arrives asynchronously,
    /// so it has to be told to measure again once the rows are in.
    /// </summary>
    private void ResizeToContent()
    {
        if (!IsLoaded)
            return;

        UpdateLayout();
        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
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
            text.AppendLine($"{row.Label,-14}{row.Value}");

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
        _settings.Save();
    }

    private void ApplyTheme()
    {
        Panel.Background = _theme.NotesBackground;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        HeaderRule.Background = _theme.Separator;

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
        }

        public string Label { get; }
        public string Value { get; }
        public Brush LabelBrush { get; }
        public Brush ValueBrush { get; }
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
