using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Metrics;
using DeskMon.Notes;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// The session history, one game at a time: how much it has been played, a
/// chart of one reading session by session, and every session as a row. Or,
/// under "All games", every game side by side.
///
/// Everything comes from the .log files SessionHistory writes, read when the
/// window opens, when another game is picked, and when a session ends. The
/// files stay the record - this window only reads them.
/// </summary>
public partial class SessionsWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly SessionHistory _history;

    /// <summary>The selected game's sessions, oldest first.</summary>
    private IReadOnlyList<SessionRecord> _sessions = Array.Empty<SessionRecord>();

    /// <summary>Every game's totals, filled only while "All games" is picked.</summary>
    private IReadOnlyList<GameTotal> _games = Array.Empty<GameTotal>();

    private bool _allGames;

    // Kept apart, so looking at All games and back doesn't lose how the
    // sessions were sorted. In memory only: a sort is a question asked now,
    // not a preference worth carrying into the next start.
    private (string Key, bool Descending) _sessionSort = ("date", true);
    private (string Key, bool Descending) _gameSort = ("played", true);

    /// <summary>
    /// The sessions clicked in the table - what Delete would delete. A list,
    /// not one session, so Ctrl+click can gather several and clearing out a
    /// run of test sessions isn't one delete per row.
    /// </summary>
    private readonly List<SessionRecord> _selected = new();

    /// <summary>
    /// Delete asks twice: the first click arms it, a second within this time
    /// deletes. In the window itself rather than a Windows dialog, which would
    /// be the one white thing on a dark panel - and a deleted session is gone.
    /// </summary>
    private readonly DispatcherTimer _deleteArmed = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Which of Delete and Delete all the timer is armed for: one shared
    /// timer, so arming one disarms the other and a click meant for one can
    /// never confirm the other.
    /// </summary>
    private Button? _armedButton;

    /// <summary>
    /// After a session is deleted. The notepad shows the game's last session
    /// under the notes, and the one just deleted may be it.
    /// </summary>
    public event Action? SessionDeleted;

    private Brush SelectedBrush => _theme.AccentSoft;

    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _allowClose;
    private bool _filling;   // the picker is being filled, not picked from

    /// <summary>Sessions shown in the chart: enough to see a trend, few enough that each bar is still a bar.</summary>
    private const int ChartSessions = 40;

    /// <summary>
    /// The bars' colour: the accent the toolbars already light up in - with
    /// the default blue on the dark panel about 7:1 against the background,
    /// well past the 3:1 a chart mark needs. A single series, so one colour
    /// and no legend.
    /// </summary>
    private Brush BarBrush => _theme.Accent;
    private Brush BarHover => _theme.AccentLit;

    /// <summary>
    /// The highest bar, in orange: opposite blue on the colour wheel, so it
    /// stands out at a glance even among 40 bars, and not the theme's amber
    /// warning colour - the highest 1% low is the best session, not a problem.
    /// </summary>
    private static readonly Brush HighestBrush = Frozen(Color.FromRgb(0xFB, 0x92, 0x3C));
    private static readonly Brush HighestHover = Frozen(Color.FromRgb(0xFD, 0xBA, 0x74));

    /// <summary>
    /// The lowest bar, in a soft red - asked for over the old pink, which read
    /// as a second highlight rather than the bottom. Kept pale, well short of
    /// the theme's critical red, since lowest is the worst for frame rates but
    /// the best for temperatures and must not look like an alarm.
    /// </summary>
    private static readonly Brush LowestBrush = Frozen(Color.FromRgb(0xF2, 0x8B, 0x8B));
    private static readonly Brush LowestHover = Frozen(Color.FromRgb(0xF8, 0xB4, 0xB4));

    /// <summary>
    /// The two marked bars' own gradients, for when the plain bars have one:
    /// solid, they were the only flat bars in the chart. Each a deeper shade
    /// of its colour at the foot rising to a lighter one, with the plain
    /// colour in between - so it still reads as orange or soft red and still
    /// matches its label and key. The red's foot kept rose rather than deep,
    /// for the reason LowestBrush is pale.
    /// </summary>
    private static readonly Color HighestFoot = Color.FromRgb(0xEA, 0x6A, 0x12);
    private static readonly Color HighestTip = Color.FromRgb(0xFC, 0xC3, 0x7E);
    private static readonly Color LowestFoot = Color.FromRgb(0xE5, 0x6F, 0x7A);
    private static readonly Color LowestTip = Color.FromRgb(0xF9, 0xB8, 0xB0);

    /// <summary>
    /// The average, drawn as a dashed line across the bars rather than as a
    /// bar of its own: no session is the average. Green, which is apart from
    /// the blue bars, the orange and the red in lightness as well as hue.
    /// </summary>
    private static readonly Brush AverageBrush = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <param name="Sort">Null for "not recorded", which sorts last either way.</param>
    /// <param name="DescendingFirst">
    /// The direction of the first click. Biggest and newest first for numbers
    /// and dates - "the longest session" is the usual question - and A to Z
    /// for names.
    /// </param>
    /// <param name="Source">For a playtime column: where each row's figure is from, shown as that source's icon before it.</param>
    /// <param name="Score">For a score column: the score, which colours the figure green, amber or red.</param>
    /// <param name="Group">
    /// The heading over this column and its neighbours ("Frame rate", "GPU"),
    /// empty for none. Columns of one group sit side by side, set off from the
    /// next group, so the figures to compare are found together.
    /// </param>
    private sealed record Column<T>(string Key, string Header, double Width, bool Number,
                                    Func<T, string> Text, Func<T, IComparable?> Sort,
                                    bool DescendingFirst = true,
                                    Func<T, PlaytimeSource>? Source = null,
                                    Func<T, int?>? Score = null,
                                    string Group = "");

    /// <summary>
    /// One game's line in the All games table. Played is what DeskMon logged;
    /// Playtime is the best total there is - Steam's, the saves', or that same
    /// logged time when neither knows the game.
    /// </summary>
    /// <param name="Score">The performance score of its recent sessions - see PerformanceScore.ForGame.</param>
    private sealed record GameTotal(string Name, int Sessions, TimeSpan Played, double? AverageFps,
                                    DateTime FirstPlayed, DateTime LastPlayed, Playtime Playtime,
                                    int? Score);

    /// <summary>What the chart can show. Value null means the session didn't record it.</summary>
    private sealed record ChartMeasure(string Key, string Name, Func<SessionRecord, double?> Value);

    // GroupOrder and KeyOrder before AllSessionColumns, which sorts by them:
    // static fields are set in the order they are written, so declared below
    // it they were still null when it was built, and the window failed to open.

    /// <summary>The groups left to right: when and how well, then the frame rate, then what limits it.</summary>
    private static readonly string[] GroupOrder = { "", "Frame rate", "GPU", "CPU", "Memory", "Disk", "Network", "Energy" };

    /// <summary>
    /// Within a group, the order to read it in: the average before the lows,
    /// load before heat. Keys not here follow in the widget's order.
    /// </summary>
    private static readonly string[] KeyOrder =
    {
        "id", "date", "time", "length", "score",
        "avg", "FPS|", "LOW|", "WORST|", "stutters", "LAT|ms",
        "GPU|%", "VRAM|%", "GPU|°C",
        "CPU|%", "CPU|°C",
        "RAM|%", "SWAP|%",
        "energy", "cost",
    };

    /// <summary>
    /// Every column the session table can have, in the order it shows them.
    ///
    /// Every peak MetricRegistry has is offered, rather than a hand-picked few,
    /// so a row added to the widget becomes a column here without touching this
    /// file. Peaks of rows no longer on the widget are left out - but they are
    /// still in the row's tooltip.
    /// </summary>
    private static readonly Column<SessionRecord>[] AllSessionColumns = BuildSessionColumns();

    /// <summary>The chart's choices, built from the same peaks as the columns.</summary>
    private static readonly ChartMeasure[] ChartMeasures = BuildChartMeasures();

    // "Logged" beside "Playtime", rather than "Played": the two differ for a
    // game played before DeskMon, and the names have to say which is which.
    // Narrower than before so the extra column fits the default width.
    private static readonly Column<GameTotal>[] GameColumns =
    {
        new("game", "Game", 150, false, g => g.Name, g => g.Name, DescendingFirst: false),
        new("sessions", "Sessions", 60, true, g => g.Sessions.ToString(CultureInfo.InvariantCulture), g => g.Sessions),
        new("played", "Logged", 74, true, g => SessionRecord.Describe(g.Played), g => g.Played),
        // Wider than its figure alone, for the icon saying where it is from.
        new("playtime", "Playtime", 92, true, g => Short(g.Playtime.Time), g => g.Playtime.Time,
            Source: g => g.Playtime.Source),
        new("avg", "Avg FPS", 62, true, g => Whole(g.AverageFps), g => g.AverageFps),
        // Lowest first on the first click: the games that need work are the question.
        new("score", "Score", 52, true, g => Whole(g.Score), g => g.Score, DescendingFirst: false, Score: g => g.Score),
        // 10 wider for the gap text columns leave on their left (ColumnGap).
        new("last", "Last played", 114, false, g => g.LastPlayed.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture), g => g.LastPlayed),
    };

    /// <summary>"143.4 h" - GamePlaytime.Describe's "hours" spelled short, for a column.</summary>
    private static string Short(TimeSpan played)
        => played.TotalHours >= 1
            ? played.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) + " h"
            : $"{Math.Max(0, (int)played.TotalMinutes)} min";

    private static Column<SessionRecord>[] BuildSessionColumns()
    {
        var columns = new List<Column<SessionRecord>>
        {
            // The number DELETE names, so the row it will take out can be found.
            new("id", "ID", 40, true, s => s.Id.ToString(CultureInfo.InvariantCulture), s => s.Id),
            // Widths take in the gap text columns now leave on their left (see ColumnGap).
            new("date", "Date", 118, false, s => s.Start.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture), s => s.Start),
            // By time of day, not by date: "which sessions were late at night" is
            // the only reason to sort by this column, and Date already does dates.
            // The dash spaced: unspaced, "20:14–22:45" ran together into one
            // long number.
            new("time", "Time", 108, false, s => $"{s.Start:HH:mm} – {s.End:HH:mm}", s => s.Start.TimeOfDay, DescendingFirst: false),
            new("length", "Length", 74, true, s => Compact(s.Length), s => s.Length),
            new("avg", "Avg FPS", 62, true, s => Whole(s.AverageFps), s => s.AverageFps),
            new("score", "Score", 52, true, s => Whole(PerformanceScore.For(s)?.Score), s => PerformanceScore.For(s)?.Score,
                DescendingFirst: false, Score: s => PerformanceScore.For(s)?.Score),
            // The hitches the average and the lows can hide - see FrameStats.Stutters.
            new("stutters", "Stutters", 62, true, s => s.Stutters?.ToString(CultureInfo.InvariantCulture) ?? "–", s => s.Stutters),
            // "CPU+GPU" in the heading itself: it is their own power sensors,
            // not the PC from the wall, and a bare "kWh" would claim the latter.
            new("energy", "CPU+GPU kWh", 84, true, s => Kwh(s.EnergyWh), s => s.EnergyWh),
            // Only offered with a price set - see VisibleSessionColumns.
            new("cost", "Cost", 64, true, s => Cost(s.EnergyWh), s => s.EnergyWh),
        };

        // The section each peak comes from, for a row GroupOf doesn't name.
        var sectionOf = new Dictionary<string, string>();
        foreach (var section in MetricRegistry.Sections)
            foreach (var metric in section.Metrics)
            {
                // Copied out, so each lambda keeps its own row rather than the loop's last.
                string label = metric.Label, unit = metric.Unit;
                string key = PeakKey(label, unit);
                if (columns.Any(c => c.Key == key))
                    continue;

                sectionOf[key] = section.Title;
                // "CLK MHz" needs the room "CPU %" does not.
                columns.Add(new(key, PeakHeader(label, unit), unit.Length > 2 ? 66 : 56, true,
                                s => s.Find(label, unit)?.Value ?? "–",
                                s => s.Find(label, unit)?.Number));
            }

        // Reordered by group, not left in the widget's order: there CPU load
        // sits in Usage and CPU heat in Temperature, so the table put CPU %
        // and CPU °C far apart and Avg FPS a dozen columns from Min FPS.
        // OrderBy is stable, so a group keeps the order of KeyOrder, and rows
        // it doesn't list keep the widget's order after them.
        return columns
            .Select(c => c with { Group = GroupOf(c.Key, sectionOf.GetValueOrDefault(c.Key, string.Empty)) })
            .Select((c, index) => (Column: c, Index: index))
            .OrderBy(x => GroupRank(x.Column.Group))
            .ThenBy(x => Array.IndexOf(KeyOrder, x.Column.Key) is int at and >= 0 ? at : KeyOrder.Length + x.Index)
            .Select(x => x.Column)
            .ToArray();
    }

    /// <summary>
    /// Which group a column is in. A row added to the widget later that isn't
    /// named here goes under its section's title, so it still gets a group.
    /// </summary>
    private static string GroupOf(string key, string section) => key switch
    {
        "id" or "date" or "time" or "length" or "score" => string.Empty,
        "avg" or "stutters" or "FPS|" or "LOW|" or "WORST|" or "LAT|ms" => "Frame rate",
        "GPU|%" or "VRAM|%" or "GPU|°C" => "GPU",
        "CPU|%" or "CPU|°C" => "CPU",
        "RAM|%" or "SWAP|%" => "Memory",
        "energy" or "cost" => "Energy",
        _ => section,
    };

    /// <summary>A group's place; one not in GroupOrder goes before Energy, which stays last.</summary>
    private static double GroupRank(string group)
        => Array.IndexOf(GroupOrder, group) is int at and >= 0 ? at : GroupOrder.Length - 1.5;

    private static ChartMeasure[] BuildChartMeasures()
    {
        var measures = new List<ChartMeasure>
        {
            new("avg", "Average FPS", s => s.AverageFps),
            new("length", "Minutes played", s => s.Length.TotalMinutes),
            new("score", "Performance score", s => PerformanceScore.For(s)?.Score),
            // Per hour, not the count: a four-hour evening would otherwise
            // always look worse than a twenty-minute one.
            new("stutters", "Stutters per hour", s => s.StuttersPerHour),
            new("energy", "CPU+GPU energy (Wh)", s => s.EnergyWh),
        };

        foreach (var metric in MetricRegistry.Sections.SelectMany(s => s.Metrics))
        {
            string label = metric.Label, unit = metric.Unit;
            string key = PeakKey(label, unit);
            if (measures.Any(m => m.Key == key))
                continue;

            // Said as what the bar is: a session's worst moment, which for the
            // frame rates is the lowest and for everything else the highest.
            string name = metric.Invert ? PeakHeader(label, unit) : $"Highest {PeakHeader(label, unit)}";
            measures.Add(new(key, name, s => s.Find(label, unit)?.Number));
        }

        return measures.ToArray();
    }

    /// <summary>"CPU|°C" - as AppSettings.SessionColumns stores it.</summary>
    private static string PeakKey(string label, string unit) => $"{label}|{unit}";

    /// <summary>
    /// The frame-rate rows in words, since their widget labels (FPS, LOW,
    /// WORST) are the session's lowest values here and would read as averages.
    /// The rest as label and unit, which tells CPU load from CPU heat.
    /// </summary>
    private static string PeakHeader(string label, string unit) => (label, unit) switch
    {
        ("FPS", "") => "Min FPS",
        ("LOW", "") => "1% low",
        ("WORST", "") => "0.1% low",
        _ => unit.Length > 0 ? $"{label} {unit}" : label,
    };

    private static string Whole(double? value)
        => value is double v ? v.ToString("0", CultureInfo.InvariantCulture) : "–";

    /// <summary>"0.31" - watt-hours as kWh, to two places, since a session is rarely a whole one.</summary>
    /// <summary>"3 min", "40 s" - how often a stutter came, for the tooltip.</summary>
    private static string Every(TimeSpan gap)
        => gap.TotalMinutes >= 1 ? $"{gap.TotalMinutes:0} min" : $"{Math.Max(1, gap.TotalSeconds):0} s";

    private static string Kwh(double? wh)
        => wh is double v ? (v / 1000).ToString("0.00", CultureInfo.InvariantCulture) : "–";

    /// <summary>
    /// The price per kWh and currency the Cost column works with - the
    /// settings', copied in by ShowRows. Static because the columns are built
    /// once for every window; worked out when shown rather than stored with
    /// the session, so a new price applies to every session at once.
    /// </summary>
    private static double _energyPrice;
    private static string _energyCurrency = string.Empty;

    /// <summary>"1.25 kr" - what the session's energy cost at the price in Settings.</summary>
    private static string Cost(double? wh)
    {
        if (wh is not double v || _energyPrice <= 0)
            return "–";
        string cost = (v / 1000 * _energyPrice).ToString("0.00", CultureInfo.InvariantCulture);
        return _energyCurrency.Length > 0 ? $"{cost} {_energyCurrency}" : cost;
    }

    /// <summary>
    /// "2h 05m" - a session's length for the Length column. Shorter than
    /// Describe's "2 h 5 min", whose four words a column of them made hard to
    /// scan, and the minutes in two digits so they line up down the
    /// right-aligned column. Describe stays for sentences and tooltips.
    /// </summary>
    private static string Compact(TimeSpan length)
        => length.TotalHours >= 1
            ? $"{(int)length.TotalHours}h {length.Minutes:00}m"
            : $"{Math.Max(0, (int)length.TotalMinutes)}m";

    /// <summary>
    /// Space on the left of a text column that follows another. Number columns
    /// keep 10 on their right, but text started flush against whatever came
    /// before it, so the ID ran into the date and the date into the time.
    /// </summary>
    private const double ColumnGap = 10;

    /// <summary>A score in the widget's own green, amber or red - good, fair, needs work.</summary>
    private Brush ScoreBrush(int score) => PerformanceScore.Band(score) switch
    {
        0 => _theme.Normal,
        1 => _theme.Warn,
        _ => _theme.Crit,
    };

    /// <summary>The entry at the top of the game picker; a type of its own so no game's name can collide with it.</summary>
    private sealed class AllGamesEntry
    {
        public override string ToString() => "All games";
    }

    private static readonly AllGamesEntry AllGames = new();

    /// <summary>The theme changed while the window was hidden - see the constructor.</summary>
    private bool _themeStale;

    /// <summary>Holds a redraw for a theme change back until the changes pause - see the constructor.</summary>
    private readonly DispatcherTimer _rethemeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void Retheme()
    {
        _themeStale = false;
        ApplyTheme();
        if (!IsLoaded)
            return;
        if (_allGames)
            ShowAllGamesSummary();
        else
            ShowSummary(GamePicker.SelectedItem as string);
        ShowRows();
        DrawChart();
    }

    public SessionsWindow(AppSettings settings, UiTheme theme, SessionHistory history,
                          Func<string, string> screenshotFolder)
    {
        _settings = settings;
        _theme = theme;
        _history = history;
        _screenshotFolder = screenshotFolder;

        InitializeComponent();
        ApplyTheme();

        // The cards, the table and the chart are drawn in the theme's colours,
        // so they are drawn again - from what is already read, not the files.
        // Hidden, only noted, and redrawn on the next showing: the theme
        // changes on every step of a colour being dragged in the settings
        // window, and redrawing the cards, table and chart each time made that
        // drag lag - for a window nobody could see.
        // Shown, it waits for the changes to pause rather than redrawing on
        // each: a colour drag fires this on every step, and every redraw also
        // started a new playtime lookup on a worker and rebuilt the table.
        // 150 ms after the last step is soon enough to look immediate.
        _rethemeTimer.Tick += (_, _) =>
        {
            _rethemeTimer.Stop();
            if (IsVisible)
                Retheme();
            else
                _themeStale = true;
        };
        _theme.Changed += () =>
        {
            if (IsVisible)
            {
                _rethemeTimer.Stop();
                _rethemeTimer.Start();
            }
            else
                _themeStale = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _themeStale)
                Retheme();
        };
        RestorePlacement(_settings.SessionsLeft, _settings.SessionsTop,
                         _settings.SessionsWidth, _settings.SessionsHeight);

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.SessionsOpacity);

        // Hooked up after the saved choice is selected, so restoring it isn't
        // taken for a pick and written straight back to settings.json.
        ChartPicker.ItemsSource = ChartMeasures.Select(m => m.Name).ToList();
        ChartPicker.SelectedIndex = Math.Max(0, Array.FindIndex(ChartMeasures, m => m.Key == _settings.SessionsChart));
        ChartPicker.SelectionChanged += ChartPicker_SelectionChanged;

        _placementSave.Tick += (_, _) => SavePlacementNow();
        _deleteArmed.Tick += (_, _) => DisarmDelete();
        Closing += OnClosing;

        // Every opening is the same: the banner and title fade in from
        // nothing, once. Hidden, they are put out straight away, so anything
        // that changes them meanwhile - another game picked from the notes, a
        // lookup finishing - just swaps them, unseen, and never leaves a fade
        // half done for the opening to cut short.
        // And put out as the window is closed, while it is still on screen,
        // so Windows' last picture of it has no banner to flash up on the
        // next opening - see PanelWindow.HideSmoothly.
        Hiding += PutOutArt;

        IsVisibleChanged += (_, e) =>
        {
            PutOutArt();

            if (e.NewValue is true)
            {
                _openedAt = DateTime.UtcNow;
                if (Backdrop.Background is not null)
                    FadeInArt(OpenDelay, OpenFadeIn);
            }
        };
        LocationChanged += (_, _) => QueuePlacementSave();
        SizeChanged += (_, _) => QueuePlacementSave();
    }

    // --- Showing a game ------------------------------------------------------

    /// <summary>
    /// Shows <paramref name="game"/>'s history if it has one. Without one -
    /// opened from the widget or the tray - whatever was looked at last time,
    /// All games included; failing that, the game played most recently.
    /// </summary>
    public void ShowAndFocus(string? game)
    {
        FillPicker(game ?? (object?)LastLookedAt());

        if (!IsOpen)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Topmost = _settings.NotesAlwaysOnTop;
        Activate();
    }

    /// <summary>
    /// Reads everything again, keeping the selected game - for when a session
    /// has just been written. A game played for the first time appears in the
    /// picker at the same moment.
    /// </summary>
    public void Refresh()
    {
        if (IsVisible)
            FillPicker(GamePicker.SelectedItem);
    }

    /// <summary>Counts fills, so a slow read finishing after a newer one doesn't put the older list back.</summary>
    private int _fillTicket;

    /// <summary>
    /// The logs are listed, and the latest found, on a worker: this runs when
    /// a game quits with the window open - inside the widget's tick - and on
    /// the UI thread it opened every log and stat'ed it there.
    /// </summary>
    private async void FillPicker(object? wanted)
    {
        int ticket = ++_fillTicket;
        IReadOnlyList<string> games;
        string? recent;
        try
        {
            (games, recent) = await System.Threading.Tasks.Task.Run(() =>
            {
                var found = _history.Games();
                return (found, MostRecent(found));
            });
        }
        catch (Exception ex)
        {
            App.Log("listing the session logs", ex);
            return;
        }

        if (ticket != _fillTicket)
            return;

        // All games first, above the names, so it reads as a view of the list
        // rather than one more game in it.
        var items = new List<object>();
        if (games.Count > 0)
            items.Add(AllGames);
        items.AddRange(games);

        object? pick = wanted is AllGamesEntry && games.Count > 0
            ? AllGames
            : games.FirstOrDefault(g => string.Equals(g, wanted as string, StringComparison.OrdinalIgnoreCase))
              ?? recent;

        _filling = true;
        try
        {
            GamePicker.ItemsSource = items;
            GamePicker.SelectedItem = pick;
        }
        finally
        {
            _filling = false;
        }

        ShowSelection(pick, games);
    }

    /// <summary>The game whose log was written last - the last one played.</summary>
    private string? MostRecent(IReadOnlyList<string> games)
        => games
            .Select(g => (Game: g, At: LastWritten(_history.PathFor(g))))
            .OrderByDescending(x => x.At)
            .Select(x => x.Game)
            .FirstOrDefault();

    private static DateTime LastWritten(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
            ShowSelection(GamePicker.SelectedItem, null);   // All games lists the logs itself, on its worker
    }

    /// <summary>What SessionsGame says was looked at last: All games, a game's name, or nothing yet.</summary>
    private object? LastLookedAt()
        => _settings.SessionsGame == AllGamesSetting ? AllGames
         : string.IsNullOrEmpty(_settings.SessionsGame) ? null
         : _settings.SessionsGame;

    /// <summary>SessionsGame's value for All games. No game's name is a lone "*": Windows forbids it in the log's file name.</summary>
    private const string AllGamesSetting = "*";

    /// <summary>Counts selections, so All games' totals arriving after another game was picked are dropped.</summary>
    private int _selectionTicket;

    /// <param name="games">Every game with a log, if the caller has just listed them; null to list them on the worker.</param>
    private async void ShowSelection(object? item, IReadOnlyList<string>? games)
    {
        int ticket = ++_selectionTicket;
        _allGames = item is AllGamesEntry;

        // Remembered for the next opening. Only when it changed: a session
        // being written re-shows the same game, and needn't touch the file.
        string looked = _allGames ? AllGamesSetting : item as string ?? string.Empty;
        if (looked.Length > 0 && looked != _settings.SessionsGame)
        {
            _settings.SessionsGame = looked;
            _settings.Save();
        }

        // Neither means anything across games: there is no one log to open,
        // and the game table's columns are not the sessions' readings.
        OpenLogButton.Visibility = _allGames ? Visibility.Collapsed : Visibility.Visible;
        ColumnsButton.Visibility = _allGames ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.Visibility = _allGames ? Visibility.Collapsed : Visibility.Visible;

        if (_allGames)
        {
            _sessions = Array.Empty<SessionRecord>();

            // On a worker thread: every game's log read and its playtime looked
            // up in Steam, Galaxy and the saves. This runs when a game exits
            // with this view open - inside the widget's polling tick - and on
            // the UI thread it froze every DeskMon window while it went through
            // the list. Total only reads; nothing here writes the settings.
            List<GameTotal> totals;
            try
            {
                totals = await System.Threading.Tasks.Task.Run(() => (games ?? _history.Games()).Select(Total).OfType<GameTotal>().ToList());
            }
            catch (Exception ex)
            {
                App.Log("totalling all games", ex);
                totals = new List<GameTotal>();
            }

            // Another game picked, or another refresh, while this was read.
            if (ticket != _selectionTicket)
                return;

            _games = totals;
            ShowAllGamesSummary();
        }
        else
        {
            string? game = item as string;

            // On a worker, as All games' totals are: a long log is read and
            // parsed line by line, and this runs in the widget's tick when a
            // game quits with the window open.
            IReadOnlyList<SessionRecord> sessions;
            try
            {
                sessions = game is null
                    ? Array.Empty<SessionRecord>()
                    : await System.Threading.Tasks.Task.Run(() => _history.Sessions(game));
            }
            catch (Exception ex)
            {
                App.Log("reading a session log", ex);
                sessions = Array.Empty<SessionRecord>();
            }

            if (ticket != _selectionTicket)
                return;   // another game picked meanwhile

            _games = Array.Empty<GameTotal>();
            _sessions = sessions;
            _changes = ChangesIn(_sessions);
            ShowSummary(game);
        }

        // Kept across a refresh, found again by its line since the records
        // are new objects. Gone if it isn't there any more, so Delete can
        // never act on a session the table no longer shows.
        var kept = _selected.Select(old => _sessions.FirstOrDefault(s => s.Raw == old.Raw))
                            .OfType<SessionRecord>().Distinct().ToList();
        _selected.Clear();
        _selected.AddRange(kept);
        DisarmDelete();

        DrawChart();
        ShowRows();
        ShowArt();
    }

    // --- Banner ------------------------------------------------------------------

    /// <summary>A game's screenshots folder, for the banner of a game Steam has no art for.</summary>
    private readonly Func<string, string> _screenshotFolder;

    /// <summary>
    /// Each game's banner once found, null for "has none" - so going back to
    /// a game, or a session being written, doesn't read Steam's folders again.
    /// Emptied by REFRESH.
    /// </summary>
    private readonly Dictionary<string, GameArt.Art> _art = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All games' key in <see cref="_art"/>; no game's name can start with a NUL.</summary>
    private const string AllGamesArt = "\0all";

    /// <summary>Counts lookups, so one for a game already left behind doesn't land on the next.</summary>
    private int _artTicket;

    /// <summary>How strongly the picture shows: enough to be the game's, faint enough that the text over it reads.</summary>
    private const double BackdropOpacity = 0.32;

    /// <summary>
    /// The picked game's banner, or for All games a collage of the most played.
    /// Looked up on a worker thread - it reads Steam's manifests and decodes a
    /// large JPEG - and faded in when it arrives, so the window opens at once.
    /// </summary>
    private async void ShowArt()
    {
        int ticket = ++_artTicket;
        string? key = _allGames ? AllGamesArt : GamePicker.SelectedItem as string;
        if (key is null)
        {
            SetBackdrop(null, null, null);
            return;
        }

        if (!_art.TryGetValue(key, out var art))
        {
            // The last game's picture and logo out now, not when this one's
            // arrive: they would sit over this game's figures meanwhile.
            // Hidden, simply gone - the opening must not fade the old one in.
            if (IsVisible)
                FadeOutArt(null);
            else
                ClearArt();

            // What the worker needs, read here: settings are the UI thread's.
            var games = (_allGames
                    ? _games.OrderByDescending(g => g.Playtime.Time).Select(g => g.Name)
                    : new[] { key })
                .Take(GameArt.CollageGames * 2)   // spares, for games without a picture
                .Select(g => (g, GamePlaytime.RememberedSteamAppId(g, _settings), _screenshotFolder(g)))
                .ToList();
            bool collage = _allGames;
            bool online = _settings.OnlineGameArt;

            try
            {
                // One game through the shared lookup, so the notes and the
                // pictures window reuse it; the collage is this window's own.
                art = collage
                    ? await GameArt.OwnThread(() => new GameArt.Art(GameArt.Collage(games, online), null))
                    : await GameArt.ForAsync(games[0].Item1, games[0].Item2, games[0].Item3, online);
            }
            catch
            {
                art = new GameArt.Art(null, null);
            }

            // Bounded like GameArt's own cache, which this holds on to as
            // well: kept for good, every banner looked at stayed in memory.
            // Cleared outright rather than one at a time - it is only a
            // shortcut, and the shared lookup still has the recent ones.
            if (_art.Count >= 8)
                _art.Clear();
            _art[key] = art;
            if (ticket != _artTicket)
                return;
        }

        SetBackdrop(art.Picture ?? GameArt.Drawn(_allGames ? GameArt.Motif.Cards : GameArt.Motif.Gamepad), art.Logo, _allGames ? "All games" : key);
    }

    /// <summary>How long the old game's banner and title take to go, and the new ones to come.</summary>
    private static readonly TimeSpan ArtFadeOut = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ArtFadeIn = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// How long an opening waits before the picture starts to come in: the
    /// window appears, settles, and then the art fades into it - rather than
    /// both arriving in the same instant.
    /// </summary>
    internal static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The opening's fade: slow, an entrance rather than a change.</summary>
    private static readonly TimeSpan OpenFadeIn = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// The banner and the title in its corner. A change of game is the old
    /// pair fading out, then the new one fading in - never a swap in one
    /// frame. Left alone when it is the same one again, as after a session
    /// is written.
    /// </summary>
    private void SetBackdrop(ImageSource? picture, ImageSource? logo, string? name)
    {
        // Hidden: swapped without a sound. Opening the window fades in
        // whatever is there then - see the IsVisibleChanged handler.
        if (!IsVisible)
        {
            if (picture is null)
                ClearArt();
            else
                SetArt(picture, logo, name);
            return;
        }

        if (picture is null)
        {
            FadeOutArt(ClearArt);
            return;
        }

        if (Backdrop.Background is ImageBrush shown && ReferenceEquals(shown.ImageSource, picture)
            && ReferenceEquals(GameLogo.Source, logo) && GameName.Text == name)
        {
            // The same, but faded right out while it was being looked up
            // again: back in. One still fading in is left to finish.
            if (Backdrop.Opacity < 0.01)
                FadeInArt();
            return;
        }

        // Already out of sight - the first picture, or faded while it was
        // looked up: straight in. Otherwise the old one goes first.
        if (Backdrop.Background is null || (Backdrop.Opacity < 0.01 && GameTitle.Opacity < 0.01))
            RevealArt(picture, logo, name);
        else
            FadeOutArt(() => RevealArt(picture, logo, name));
    }

    /// <summary>
    /// Both layers to nothing, from wherever they are - half faded in is fine.
    /// <paramref name="then"/> runs at the end, but not if another fade took
    /// over first: WPF drops the replaced animation without its Completed, so
    /// clicking quickly through games only ever shows the last one picked.
    /// </summary>
    private void FadeOutArt(Action? then)
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
        var title = new System.Windows.Media.Animation.DoubleAnimation(0, ArtFadeOut) { EasingFunction = ease };
        if (then is not null)
            title.Completed += (_, _) => then();

        Backdrop.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, ArtFadeOut) { EasingFunction = ease });
        GameTitle.BeginAnimation(OpacityProperty, title);

        // Drifting a little to the right on its way out, the way it came in.
        if (GameTitle.RenderTransform is not TranslateTransform)
            GameTitle.RenderTransform = new TranslateTransform();
        GameTitle.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(8, ArtFadeOut) { EasingFunction = ease });
    }

    private void RevealArt(ImageSource picture, ImageSource? logo, string? name)
    {
        SetArt(picture, logo, name);

        // Art that was still being looked up when the window opened, and
        // arrives within the opening's delay, waits out the rest of it - so
        // every opening looks alike, whether the art was ready or not.
        // The opening's slow fade too, for art that arrives while the window
        // is still opening; a change of game later on is the quicker one.
        var since = DateTime.UtcNow - _openedAt;
        var left = OpenDelay - since;
        FadeInArt(left > TimeSpan.Zero ? left : TimeSpan.Zero,
                  since < OpenDelay + OpenFadeIn ? OpenFadeIn : ArtFadeIn);
    }

    /// <summary>When the window was last opened, for <see cref="RevealArt"/>.</summary>
    private DateTime _openedAt;

    /// <summary>The banner and title to nothing at once, no fade - the window is closing or opening.</summary>
    private void PutOutArt()
    {
        foreach (var layer in new UIElement[] { Backdrop, GameTitle })
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = 0;
        }
    }

    /// <summary>Nothing behind the header: no picture, no logo, no name.</summary>
    private void ClearArt()
    {
        Backdrop.Background = null;
        GameLogo.Source = null;
        GameName.Text = string.Empty;
    }

    /// <summary>The picture, logo and name put in place - without a fade, which is the caller's.</summary>
    private void SetArt(ImageSource picture, ImageSource? logo, string? name)
    {
        Backdrop.Background = new ImageBrush(picture)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentY = AlignmentY.Center,
        };

        // The logo is the name, in the game's own lettering; the written name
        // only when there is none.
        GameLogo.Source = logo;
        GameLogo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        GameNamePanel.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        GameName.Text = name ?? string.Empty;

        // A link only for a game: the All games collage has no one page.
        bool link = !_allGames && !string.IsNullOrEmpty(name);
        foreach (var title in new FrameworkElement[] { GameLogo, GameNamePanel })
        {
            title.Cursor = link ? Cursors.Hand : null;
            title.ToolTip = link ? $"Open {name} on the Steam store" : null;
        }
    }

    /// <summary>
    /// Grows the logo a little under the mouse - with the hand cursor, what
    /// says it is a button: on its own it reads as part of the picture.
    /// </summary>
    private void GameTitle_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement title && title.Cursor == Cursors.Hand)
            ScaleTitle(title, 1.06);
    }

    private void GameTitle_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement title)
            ScaleTitle(title, 1);
    }

    private static void ScaleTitle(FrameworkElement title, double to)
    {
        if (title.RenderTransform is not ScaleTransform scale)
            title.RenderTransform = scale = new ScaleTransform();

        var grow = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 1 ? 120 : 200))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>
    /// The game's page on the Steam store, in the browser. Its app id from
    /// the art lookup, from the last time it ran from Steam, or - a GOG copy
    /// whose art came from the cache - the store's search by name; the
    /// store's search page when none of them knows it.
    /// </summary>
    private async void GameTitle_Click(object sender, MouseButtonEventArgs e)
    {
        // Not while faded out: the title still takes clicks at opacity 0.
        if (_allGames || GamePicker.SelectedItem is not string game || GameTitle.Opacity < 0.5)
            return;

        string? id = (_art.TryGetValue(game, out var art) ? art.SteamAppId : null)
                     ?? GamePlaytime.RememberedSteamAppId(game, _settings)
                     ?? await GameArt.OwnThread(() => GameArt.StoreAppId(game));

        // Digits only: the id goes into a URL, and settings.json is hand-editable.
        string url = id is not null && id.Length > 0 && id.All(char.IsAsciiDigit)
            ? $"https://store.steampowered.com/app/{id}/"
            : "https://store.steampowered.com/search/?term=" + Uri.EscapeDataString(game);

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("opening the Steam page", ex);
        }
    }

    /// <summary>
    /// The banner up to its strength and the title in a moment after it,
    /// gliding the last few pixels in from the right, so it reads as laid
    /// over the banner rather than part of it. From wherever they are, so a
    /// fade-in that interrupts a fade-out doesn't jump.
    /// </summary>
    /// <param name="after">
    /// A wait before it starts - <see cref="OpenDelay"/> when the window is
    /// opened, so the window is up and drawn before the picture comes into it.
    /// </param>
    /// <param name="length">How long the fade takes; <see cref="ArtFadeIn"/> when left out.</param>
    private void FadeInArt(TimeSpan after = default, TimeSpan? length = null)
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var fade = length ?? ArtFadeIn;

        // The title follows a little after the picture - a sixth of the fade.
        var delay = after + TimeSpan.FromTicks(fade.Ticks / 6);

        Backdrop.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(BackdropOpacity, fade) { BeginTime = after, EasingFunction = ease });

        if (GameTitle.RenderTransform is not TranslateTransform)
            GameTitle.RenderTransform = new TranslateTransform();
        GameTitle.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, fade) { BeginTime = delay, EasingFunction = ease });
        GameTitle.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(8, 0, fade) { BeginTime = delay, EasingFunction = ease });
    }

    /// <summary>Null for a game whose log has no sessions left in it - it would only be a row of dashes.</summary>
    private GameTotal? Total(string game)
    {
        var sessions = _history.Sessions(game);
        if (sessions.Count == 0)
            return null;

        var logged = TimeSpan.FromTicks(sessions.Sum(s => s.Length.Ticks));
        return new GameTotal(game, sessions.Count, logged, WeightedFps(sessions),
                             sessions[0].Start, sessions[^1].Start,
                             GamePlaytime.For(game, _settings, _history, logged: logged),
                             PerformanceScore.ForGame(sessions));
    }

    /// <summary>
    /// The average frame rate, weighted by how long each session ran: a
    /// two-minute session at 300 fps on the menu screen would otherwise count
    /// as much as a three-hour evening at 140.
    /// </summary>
    private static double? WeightedFps(IEnumerable<SessionRecord> sessions)
    {
        var measured = sessions.Where(s => s.AverageFps is not null && s.Length > TimeSpan.Zero).ToList();
        return measured.Count > 0
            ? measured.Sum(s => s.AverageFps!.Value * s.Length.TotalMinutes) / measured.Sum(s => s.Length.TotalMinutes)
            : null;
    }

    // --- Summary ---------------------------------------------------------------

    /// <summary>
    /// Time played, sessions, the average frame rate and - when something
    /// knows more than DeskMon's logs - the whole playtime, as cards; the
    /// first and last day played on a quiet line under them.
    /// </summary>
    private void ShowSummary(string? game)
    {
        // A playtime still being looked up belongs to the cards cleared here,
        // even when it is the same game again - those are about to be redrawn.
        _playtimeTicket++;

        ClearSummary();
        Stats.Children.Clear();
        Dates.Children.Clear();
        ClearLastSession();

        if (game is null || _sessions.Count == 0)
        {
            AddSummary("No sessions yet. One is logged each time you quit a game after playing for a minute or more.");
            return;
        }

        var total = TimeSpan.FromTicks(_sessions.Sum(s => s.Length.Ticks));
        var first = _sessions[0].Start;
        // When the last session ended, not began: with the time shown, "last
        // played 21:19" for an evening that went on to midnight would be wrong.
        var last = _sessions[^1].End;

        // The whole playtime goes first, but is looked up on a worker and put
        // in front of the other cards when it arrives - see AddPlaytimeAsync.
        _ = AddPlaytimeAsync(game, total);

        AddStat("LOGGED", SessionRecord.Describe(total), "Time in the sessions DeskMon logged", PlaytimeSource.Logged);
        AddStat("SESSIONS", _sessions.Count.ToString(CultureInfo.InvariantCulture), iconData: SessionsCardIcon);

        if (WeightedFps(_sessions) is double fps)
            AddStat("AVERAGE", $"{fps:0} fps",
                    "Weighted by how long each session ran, so a few minutes in a menu don't outweigh an evening",
                    iconData: AverageCardIcon);

        if (PerformanceScore.ForGame(_sessions) is int score)
        {
            string target = PerformanceScore.HasOwnTarget(game)
                ? $"Full marks need {PerformanceScore.TargetFor(game):0} fps - this game's own target."
                : $"Full marks need {PerformanceScore.Target:0} fps - the target frame rate in Settings.";
            var card = AddStat("SCORE", $"{score}  {PerformanceScore.Grade(score)}",
                               $"Performance out of 100, from the last {PerformanceScore.GameSessions} sessions with a frame rate, " +
                               "weighted by how long each ran.\n" +
                               target + "\nClick to give this game a target of its own.\n" +
                               "Point at a session for how its score was reached.",
                               valueBrush: ScoreBrush(score), iconData: ScoreCardIcon);
            card.ContextMenu = TargetMenu(game);
            ShowTargetOnCard(card, game);
        }

        // Both in one form, date and time, without the weekday: the two read
        // as a pair, and "Sun" made the second chip wider for nothing.
        const string When = "d MMM yyyy  ·  HH:mm";
        Dates.Children.Add(SummaryChip("First played", first.ToString(When, CultureInfo.InvariantCulture), CalendarIcon, dot: false));
        Dates.Children.Add(SummaryChip("Last played", last.ToString(When, CultureInfo.InvariantCulture), CalendarIcon, dot: true));

        ShowLastSession(_sessions[^1]);
    }

    /// <summary>
    /// The LAST SESSION row under the cards: "how did it go last time?"
    /// answered without scrolling the table. Icons rather than captions,
    /// so four chips fit beside the logo in a narrow window; each says
    /// what it is in its tooltip.
    /// </summary>
    private void ShowLastSession(SessionRecord session)
    {
        LastSessionPanel.Visibility = Visibility.Visible;

        Border Chip(string value, string icon, string tip, Brush? valueBrush = null)
        {
            var chip = SummaryChip(string.Empty, value, icon, valueBrush: valueBrush);
            // In a row here, not stacked down the right edge like the dates.
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            chip.Margin = new Thickness(0, 0, 8, 4);
            chip.ToolTip = tip;
            LastSession.Children.Add(chip);
            return chip;
        }

        Chip(session.Start.ToString("d MMM  ·  HH:mm", CultureInfo.InvariantCulture), CalendarIcon,
             $"Started {session.Start.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}");
        Chip(SessionRecord.Describe(session.Length), ClockIcon, "How long it ran");

        if (session.AverageFps is double fps)
            Chip($"{fps:0} fps", PulseIcon, "Average frame rate over the session");

        if (PerformanceScore.For(session)?.Score is int score)
            Chip($"{score}  {PerformanceScore.Grade(score)}", StarIcon,
                 "Its performance score - point at its row in the table for how it was reached",
                 ScoreBrush(score));
    }

    /// <summary>Hidden, not just emptied, so its heading doesn't stand over nothing.</summary>
    private void ClearLastSession()
    {
        LastSession.Children.Clear();
        LastSessionPanel.Visibility = Visibility.Collapsed;
    }

    // The cards' icons, on PlaytimeIcons' 11 px grid. The chips' pulse and
    // star are drawn bigger, so these are their own, smaller versions.

    /// <summary>A stack of sessions: a card with two more behind it.</summary>
    private const string SessionsCardIcon = "M1.5,4.5 H9.5 V9.5 H1.5 Z M2.5,3 H8.5 M3.5,1.5 H7.5";

    /// <summary>The pulse line, for a frame rate.</summary>
    private const string AverageCardIcon = "M0.5,5.5 H2.5 L4,2 L6,9 L7.5,4.5 H10.5";

    /// <summary>A star, for the score.</summary>
    private const string ScoreCardIcon = "M5.5,1 L6.8,3.9 L10,4.2 L7.6,6.3 L8.3,9.5 L5.5,7.8 L2.7,9.5 L3.4,6.3 L1,4.2 L4.2,3.9 Z";

    /// <summary>The widget's GAME section gamepad, for All games' count of games.</summary>
    private const string GamesCardIcon = "M2.5,3 H8.5 A2,2 0 0 1 10.5,5 V7 A2,2 0 0 1 8.5,9 H2.5 A2,2 0 0 1 0.5,7 V5 A2,2 0 0 1 2.5,3 Z " +
                                         "M3,4.8 V7.2 M1.8,6 H4.2";

    /// <summary>A clock face with its two hands.</summary>
    private const string ClockIcon = "M6,1.5 A4.5,4.5 0 1 1 5.99,1.5 Z M6,3.5 V6 L7.8,7.2";

    /// <summary>A pulse line, the widget's own sign for activity - here the frame rate.</summary>
    private const string PulseIcon = "M0.5,6 H3 L4.5,2 L6.5,9.5 L8,5 H10.5";

    /// <summary>A five-pointed star, for the score.</summary>
    private const string StarIcon = "M6,1 L7.4,4.2 L11,4.6 L8.3,6.9 L9.1,10.5 L6,8.6 L2.9,10.5 L3.7,6.9 L1,4.6 L4.6,4.2 Z";

    /// <summary>A calendar: the page, the line under its header and the two rings.</summary>
    private const string CalendarIcon = "M1.5,3 H10.5 V10.5 H1.5 Z M1.5,5.5 H10.5 M4,1.5 V4 M8,1.5 V4";

    /// <summary>A trophy: the cup with its two handles, the stem and the foot.</summary>
    private const string TrophyIcon = "M3.5,1.5 H8.5 V4.5 A2.5,2.5 0 0 1 3.5,4.5 Z M3.5,2.5 H1.5 V3.5 A2,2 0 0 0 3.7,5.5 " +
                                      "M8.5,2.5 H10.5 V3.5 A2,2 0 0 1 8.3,5.5 M6,7 V9 M3.5,10.5 H8.5 M4.5,10.5 V9.5 H7.5 V10.5";

    /// <summary>
    /// One of the chips under the cards - first and last played for a game,
    /// most and last played for All games: an icon, a small caption and the
    /// figure, in the theme's accent, made like the chart's average / highest
    /// / lowest chips so the two rows match. They were one dim line of small
    /// text that was easy to miss.
    /// </summary>
    /// <param name="dot">Fills in a day on the calendar, so first and last played differ at a glance.</param>
    /// <param name="valueBrush">The figure's colour when it carries meaning - a score's green, amber or red.</param>
    private Border SummaryChip(string caption, string value, string iconData, bool dot = false, Brush? valueBrush = null)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconData),
            Stroke = _theme.Accent,
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var art = new Grid { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        art.Children.Add(icon);
        if (dot)
            art.Children.Add(new Border
            {
                Width = 2.5, Height = 2.5, Background = _theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6.5, 6.8, 0, 0),
            });

        // At most 240 wide, cut short with "…": a long game's name under Most
        // played widened the whole column of chips, which took the room from
        // the cards beside it. The full text is in the tooltip instead.
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(art);
        row.Children.Add(text);

        // Stacked down the right edge of the window, so right-aligned with
        // a gap under each - see Dates in the XAML.
        var chip = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(9, 3, 10, 3),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Child = row,
            // The whole text, for one cut short; a caller with more to say sets its own.
            ToolTip = caption.Length > 0 ? $"{caption}: {value}" : value,
        };
        StyleChip(chip, text, _theme.Accent);
        SetChip(text, caption, value);
        if (valueBrush is not null)
            text.Inlines.LastInline.Foreground = valueBrush;
        return chip;
    }

    /// <summary>
    /// The target under SCORE's figure - "Target 144 fps ▾" - and a left click
    /// that opens the target menu too. The menu was only on a right-click,
    /// which nothing on screen hinted at, so the per-game target went unfound.
    /// The ▾ says it opens a list, as on the window's dropdowns.
    /// </summary>
    private void ShowTargetOnCard(FrameworkElement card, string game)
    {
        if (card is not Border { Child: Panel content } border)
            return;

        content.Children.Add(new TextBlock
        {
            Text = PerformanceScore.HasOwnTarget(game)
                ? $"Target {PerformanceScore.TargetFor(game):0} fps (this game) ▾"
                : $"Target {PerformanceScore.Target:0} fps ▾",
            Foreground = _theme.Label,
            FontSize = SmallFont - 1,
            Margin = new Thickness(0, 1, 0, 0),
        });

        border.Cursor = Cursors.Hand;
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (border.ContextMenu is { } menu)
            {
                menu.PlacementTarget = border;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        };
    }

    /// <summary>Frame rates offered for a game's own target: the common caps and refresh rates.</summary>
    private static readonly int[] TargetChoices = { 30, 60, 90, 120, 144, 165, 240 };

    /// <summary>
    /// SCORE's right-click menu: a target frame rate for this game alone, or
    /// back to Settings' one. For games locked below the monitor's rate -
    /// Factorio runs at 60 by design and scored "Needs optimising" against
    /// 240 while running perfectly. Every score on screen is redone at once.
    /// </summary>
    private ContextMenu TargetMenu(string game)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        bool own = PerformanceScore.HasOwnTarget(game);
        double current = PerformanceScore.TargetFor(game);

        void Pick(double? fps)
        {
            if (fps is double value)
                _settings.GameTargetFps[game] = value;
            else
                _settings.GameTargetFps.Remove(game);
            _settings.Save();
            ShowSelection(GamePicker.SelectedItem, null);
        }

        var settings = new MenuItem
        {
            Header = $"Settings' target ({PerformanceScore.Target:0} fps)",
            IsCheckable = true,
            IsChecked = !own,
        };
        settings.Click += (_, _) => Pick(null);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());

        foreach (int fps in TargetChoices)
        {
            var item = new MenuItem
            {
                Header = $"{fps} fps for {game}",
                IsCheckable = true,
                IsChecked = own && current == fps,
            };
            item.Click += (_, _) => Pick(fps);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Counts lookups, so one for a game already left behind doesn't land on the next.</summary>
    private int _playtimeTicket;

    /// <summary>
    /// The whole playtime as the first card, when something knows more than
    /// DeskMon's logs - the figure the widget's GAME heading counts up from.
    /// Left out when the logs are all there is, since it would be the logged
    /// time again. Named for where it is from, with that source's icon.
    ///
    /// On a worker thread, as the All games totals already were. This runs
    /// when a game exits with the window open - inside the widget's tick -
    /// and that is when Steam has just rewritten localconfig.vdf, several MB
    /// to parse again, and GOG Galaxy's database may have to be copied before
    /// it can be read. On the UI thread it froze every DeskMon window for it.
    /// </summary>
    private async System.Threading.Tasks.Task AddPlaytimeAsync(string game, TimeSpan logged)
    {
        int ticket = ++_playtimeTicket;

        Playtime playtime;
        try
        {
            // Look, not For: with no process to go by there is nothing new to
            // remember, so nothing here writes the settings.
            playtime = (await System.Threading.Tasks.Task.Run(
                () => GamePlaytime.Look(game, _settings, _history, logged: logged))).Playtime;
        }
        catch (Exception ex)
        {
            App.Log("looking up playtime", ex);
            return;
        }

        // Another game picked, the summary redrawn, or All games shown meanwhile.
        if (ticket != _playtimeTicket || _allGames
            || !string.Equals(GamePicker.SelectedItem as string, game, StringComparison.OrdinalIgnoreCase))
            return;

        if (playtime.Source == PlaytimeSource.Logged)
            return;

        AddStat(playtime.Source switch
                {
                    PlaytimeSource.Steam => "STEAM PLAYTIME",
                    PlaytimeSource.Gog => "GOG PLAYTIME",
                    _ => "PLAYTIME FROM SAVES",
                },
                GamePlaytime.Describe(playtime.Time),
                $"Your whole playtime, {GamePlaytime.Where(playtime.Source)}",
                playtime.Source,
                at: 0);
    }

    /// <summary>
    /// One figure on a card: its name in small capitals above, the figure in
    /// larger bright type below. The same faint card as the notes window's
    /// toolbar and last session, so it reads as one family.
    /// </summary>
    /// <param name="source">For a time figure: where it is from, shown as that source's icon before the name.</param>
    /// <param name="valueBrush">The figure's colour, when it should say good or bad - the score's; bright text otherwise.</param>
    /// <param name="at">Where among the cards; the end when left out. The playtime arrives last but goes first.</param>
    /// <returns>The card, for one that needs more - SCORE's right-click menu.</returns>
    /// <param name="iconData">For a card that isn't a time: its own icon, drawn like the playtime ones.</param>
    private FrameworkElement AddStat(string name, string value, string? tooltip = null, PlaytimeSource? source = null,
                                     Brush? valueBrush = null, int? at = null, string? iconData = null)
    {
        FrameworkElement? icon = null;
        if (source is { } from)
            icon = PlaytimeIcons.Make(from, _theme.Label);
        else if (iconData is not null)
            // The playtime icons' 11 px grid, 1 px line and colour, so every
            // card's icon reads as one set - the time cards had one and the
            // rest stood bare beside them.
            icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(iconData),
                Stroke = _theme.Label,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 11,
                Height = 11,
                Stretch = Stretch.None,
                SnapsToDevicePixels = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
        if (icon is not null)
            icon.Margin = new Thickness(0, 0, 5, 0);

        // Shared with the pictures window's cards - see StatCard.
        var card = StatCard.Make(_theme, name, value, tooltip, icon, valueBrush);
        if (at is int index)
            Stats.Children.Insert(Math.Clamp(index, 0, Stats.Children.Count), card);
        else
            Stats.Children.Add(card);
        return card;
    }

    /// <summary>
    /// Time across every game, and the one it mostly went to. No average frame
    /// rate: one number over games that run at 60 and at 400 describes none of them.
    /// </summary>
    private void ShowAllGamesSummary()
    {
        ClearSummary();
        Stats.Children.Clear();
        Dates.Children.Clear();
        ClearLastSession();   // one game's; All games has no single last session to show

        if (_games.Count == 0)
        {
            AddSummary("No sessions yet. One is logged each time you quit a game after playing for a minute or more.");
            return;
        }

        var total = TimeSpan.FromTicks(_games.Sum(g => g.Played.Ticks));
        var playtime = TimeSpan.FromTicks(_games.Sum(g => g.Playtime.Time.Ticks));
        int sessions = _games.Sum(g => g.Sessions);
        // By the whole playtime, not the logged time: a game with 140 hours on
        // Steam isn't "less played" because DeskMon only saw two of them.
        var most = _games.MaxBy(g => g.Playtime.Time)!;
        var latest = _games.MaxBy(g => g.LastPlayed)!;

        // The same icons as one game's cards: the clock on LOGGED, and on
        // PLAYTIME the source most of its hours come from. One icon, not one
        // per source: three side by side looked odd, and the tooltip says the
        // total is from several.
        var mostFrom = _games.Where(g => g.Playtime.Source != PlaytimeSource.Logged)
                             .GroupBy(g => g.Playtime.Source)
                             .MaxBy(group => group.Sum(g => g.Playtime.Time.Ticks))?.Key;
        if (playtime > total)
            AddStat("PLAYTIME", GamePlaytime.Describe(playtime), "Every game's whole playtime, from Steam, GOG Galaxy or their saves where known",
                    mostFrom);

        AddStat("LOGGED", SessionRecord.Describe(total), "Time in the sessions DeskMon logged", PlaytimeSource.Logged);
        AddStat("SESSIONS", sessions.ToString(CultureInfo.InvariantCulture), iconData: SessionsCardIcon);
        AddStat("GAMES", _games.Count.ToString(CultureInfo.InvariantCulture), iconData: GamesCardIcon);

        // As chips, like one game's first and last played in the same place.
        Dates.Children.Add(SummaryChip("Most played", $"{most.Name}  ·  {GamePlaytime.Describe(most.Playtime.Time)}", TrophyIcon));
        Dates.Children.Add(SummaryChip("Last played", latest.Name, CalendarIcon, dot: true));
    }

    private void AddSummary(string text, bool bright = false)
    {
        // Shown only with something in it - see ClearSummary.
        SummaryText.Visibility = Visibility.Visible;
        SummaryText.Inlines.Add(new System.Windows.Documents.Run(text)
        {
            Foreground = bright ? _theme.NotesText : _theme.Label,
            FontWeight = bright ? FontWeights.SemiBold : FontWeights.Normal,
        });
    }

    /// <summary>
    /// Empties the line under the cards and hides it: an empty TextBlock still
    /// takes a whole line's height, which stood as a gap over LAST SESSION.
    /// </summary>
    private void ClearSummary()
    {
        SummaryText.Inlines.Clear();
        SummaryText.Visibility = Visibility.Collapsed;
    }

    // --- Chart -----------------------------------------------------------------

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private ChartMeasure SelectedMeasure
        => ChartMeasures[Math.Clamp(ChartPicker.SelectedIndex, 0, ChartMeasures.Length - 1)];

    private void ChartPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.SessionsChart = SelectedMeasure.Key;
        _settings.Save();
        DrawChart();
    }

    /// <summary>
    /// The picked reading per session, as bars, oldest on the left - for "did
    /// that patch or driver change anything?". Bars rather than a line because
    /// sessions are separate evenings, not a continuous measurement, and a
    /// line would draw a trend through the days in between.
    ///
    /// One value axis starting at zero, so bar heights compare honestly, with
    /// faint gridlines at round numbers. Every bar has a tooltip, and its
    /// hover area is the full height of the chart rather than the bar alone,
    /// so a short bar is as easy to point at as a tall one.
    /// </summary>
    /// <summary>Whether the last draw showed the empty chart's picture - see ChartEmptyState.</summary>
    private bool _chartEmptyShown;

    /// <summary>
    /// What the chart shows when no session recorded the picked reading: a
    /// small drawn chart with empty, dashed bars, a title and what to do
    /// about it - the same kind of picture the pictures window shows with no
    /// pictures, where one dim line of small text was easy to take for a
    /// broken chart. Drawn, not an image file, so it is sharp and in the
    /// theme's colours (by reference, so it follows a theme change).
    /// It never takes a click, so the chart's own tooltips stay reachable.
    /// </summary>
    private FrameworkElement ChartEmptyState(string reading, double width, double height, bool arriving)
    {
        var art = new Canvas { Width = 100, Height = 66, HorizontalAlignment = HorizontalAlignment.Center };

        System.Windows.Shapes.Rectangle Box(double left, double top, double w, double h, double radius,
                                            string? fill, string? stroke, double thickness = 1.2, bool dashed = false)
        {
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = h, RadiusX = radius, RadiusY = radius, StrokeThickness = thickness,
            };
            if (fill is not null) box.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fill);
            if (stroke is not null) box.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
            if (dashed) box.StrokeDashArray = new DoubleCollection { 2, 2 };
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
            art.Children.Add(box);
            return box;
        }

        // The card, its baseline, and three bars of different heights drawn
        // as dashed outlines: a chart whose bars are missing.
        Box(0, 0, 88, 58, 7, "Theme.Ov.0A", "Theme.Ov.2B");
        Box(12, 47, 64, 1.2, 0, "Theme.Ov.33", null, 0);
        Box(18, 31, 12, 15, 2, null, "Theme.Ov.4D", dashed: true);
        Box(36, 17, 12, 29, 2, null, "Theme.Ov.4D", dashed: true);
        Box(54, 24, 12, 22, 2, null, "Theme.Ov.4D", dashed: true);

        // A badge on the corner with a dash in it: "nothing here".
        Box(72, 40, 26, 26, 13, "Theme.Popup", "Theme.Accent.99", 1.5);
        var dash = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M80.5,53 H89.5"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Theme.Accent");
        art.Children.Add(dash);

        var stack = new StackPanel { Width = width, IsHitTestVisible = false };
        stack.Children.Add(art);
        stack.Children.Add(new TextBlock
        {
            Text = "Not recorded in these sessions",
            Foreground = _theme.Section,
            FontSize = UiTheme.PanelFontSize + 2,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 3),
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{reading} wasn't logged for any of them - pick another reading above.",
            Foreground = _theme.Label,
            FontSize = SmallFont,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 380,
        });

        // Centred in the chart by its own measured height, which depends on
        // the font and whether the line wraps.
        stack.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetTop(stack, Math.Max(0, (height - stack.DesiredSize.Height) / 2));

        if (arriving)
        {
            // The pictures window's fade and rise, shorter and with a shorter
            // wait: the chart is part of a window already on screen. Once,
            // and still after that, since this window can be open over a game.
            var shift = new TranslateTransform(0, 12);
            stack.RenderTransform = shift;
            stack.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(120);
            var duration = new Duration(TimeSpan.FromMilliseconds(400));
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            stack.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(12, 0, duration) { BeginTime = delay, EasingFunction = ease });
        }

        return stack;
    }

    private void DrawChart()
    {
        Chart.Children.Clear();

        // Shown whenever there are sessions, even if none recorded the picked
        // reading - hiding the picker along with the bars would leave no way
        // to pick something else.
        bool any = !_allGames && _sessions.Count > 0;
        ChartTitle.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Chart.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        // Its line goes with it: under All games, with no chart, it would
        // separate the cards from the table for no reason.
        ChartRule.Visibility = Chart.Visibility;

        double width = Chart.ActualWidth;
        double height = Chart.ActualHeight;
        if (!any || width <= 0 || height <= 0)
        {
            _chartEmptyShown = false;
            return;
        }

        var measure = SelectedMeasure;
        var shown = _sessions.Skip(Math.Max(0, _sessions.Count - ChartSessions)).ToList();

        // The bars that will be drawn: a session that didn't record the
        // reading, or recorded nothing, has none, so it can't be the lowest.
        var values = shown.Select(s => measure.Value(s)).OfType<double>().Where(v => v > 0).ToList();
        bool anyBar = values.Count > 0;

        // The key only for what there is a bar to explain. A lowest only when
        // it differs from the highest - with one bar, or every bar level, the
        // same bar would be both.
        double lowest = anyBar ? values.Min() : 0;
        bool markLowest = anyBar && lowest < values.Max();

        HighestChip.Visibility = anyBar ? Visibility.Visible : Visibility.Collapsed;
        LowestChip.Visibility = markLowest ? Visibility.Visible : Visibility.Collapsed;
        AverageChip.Visibility = HighestChip.Visibility;
        // The heading only over figures: with no bars the chart says why itself.
        ChartTitleText.Visibility = HighestChip.Visibility;

        // The figures beside their keys: the colour alone only said which bar
        // was highest, and finding its value meant hovering it or reading it
        // off the axis. Taken from the bars drawn, so they match what is seen.
        // The average outside the block: the chart draws its line too.
        double average = 0;
        if (anyBar)
        {
            // Average FPS weighted by session length, as the AVG FPS card is:
            // a plain mean let short menu sessions at 300+ fps pull it far
            // above the card's figure. The other readings are a plain mean -
            // weighting minutes played by minutes played would favour long ones.
            average = measure.Key == "avg" && WeightedFps(shown) is double weighted
                ? weighted
                : values.Average();
            SetChip(AverageText, "Average", BarValue(average));
            SetChip(HighestText, "Highest", BarValue(values.Max()));
            SetChip(LowestText, "Lowest", BarValue(lowest));
        }

        // Fades in only when it first appears: the chart is redrawn on every
        // resize and refresh, and arriving again each time would flicker.
        bool arriving = !anyBar && !_chartEmptyShown;
        _chartEmptyShown = !anyBar;

        if (!anyBar)
        {
            Chart.Children.Add(ChartEmptyState(measure.Name, width, height, arriving));
            return;
        }

        const double AxisWidth = 30;   // room for the value labels
        const double LabelRoom = 16;   // a bar's value label above the tallest bar
        const double Gap = 2;          // surface gap between bars

        double highest = shown.Max(s => measure.Value(s) ?? 0);
        double plotLeft = AxisWidth;
        double plotWidth = width - AxisWidth;
        double plotTop = LabelRoom;
        // Room under the baseline for the lower half of the "0" label, which is
        // centred on its gridline like the others. With only 1 px there, the
        // chart's clipping cut the 0 off at the bottom.
        const double ZeroRoom = 8;
        // Under that, each bar's session ID - the number in the table's ID
        // column - so a bar can be found in the table without hovering it.
        double idSize = Math.Max(8, SmallFont - 2);
        double idRoom = idSize + 4;
        double plotHeight = height - LabelRoom - ZeroRoom - idRoom;
        double baseline = plotTop + plotHeight;

        // The top of the scale high enough that the tallest bar's value label
        // fits under the top gridline, not just the bar: rounding 57 up to 60
        // put the bar right under the line and its "57" across and above it.
        // The bar may reach at most the plot's height less one label's room.
        double fits = plotHeight > LabelRoom ? highest * plotHeight / (plotHeight - LabelRoom) : highest;
        double top = NiceCeiling(fits);

        // Gridlines at a quarter steps of a round top value; labels on the left
        // in the muted text colour, so the axis stays out of the bars' way.
        for (int step = 0; step <= 4; step++)
        {
            double value = top * step / 4;
            double y = Math.Round(baseline - plotHeight * step / 4) + 0.5;

            // Fully qualified: Shapes also has a Path, which would collide with System.IO's.
            Chart.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = plotLeft, X2 = width, Y1 = y, Y2 = y,
                Stroke = step == 0 ? _theme.Border : _theme.Separator,
                StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = value.ToString("0", CultureInfo.InvariantCulture),
                Foreground = _theme.Header,
                FontSize = SmallFont - 1,
                Width = AxisWidth - 6,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            Chart.Children.Add(label);
        }

        // Slots spread over the whole width, but never wider than an eighth of
        // it: a game with three sessions would otherwise draw three slabs.
        // Neon bars: thin, at most 11 px, so the glow around them is what fills
        // the slot - wide bars with a glow merged into one bright block.
        double slot = plotWidth / Math.Max(shown.Count, 8);
        double barWidth = Math.Min(NeonBarWidth, Math.Max(2, Math.Min(slot * 0.6, slot - Gap)));

        // The sessions picked in the table: a faint accent column behind each
        // one's slot, so they can be found in the chart without counting IDs.
        // Behind the glow and bars, and drawn even without a bar, so a session
        // that didn't record this reading still shows where it would be.
        var picked = new HashSet<int>(_selected.Select(p => shown.FindIndex(s => ReferenceEquals(s, p))).Where(i => i >= 0));
        foreach (int i in picked)
        {
            var column = new Border
            {
                Width = slot,
                Height = plotHeight + ZeroRoom,
                Background = _theme.AccentAt(0x26),
                CornerRadius = new CornerRadius(Math.Min(4, slot / 2)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(column, plotLeft + i * slot);
            Canvas.SetTop(column, plotTop);
            Chart.Children.Add(column);
        }

        // With one picked its value and ID are always shown; with several,
        // forced labels on neighbouring bars would run into each other.
        int onlyPicked = picked.Count == 1 ? picked.First() : -1;

        // The glow: a copy of every bar in one layer behind them, blurred as a
        // whole. One effect for the chart instead of one per bar - each WPF
        // effect renders its element off-screen, and forty of them is real GPU
        // work in a window that can sit over a game. A copy rather than a
        // DropShadowEffect so each glow keeps its own bar's colour.
        var glow = new Canvas
        {
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 10,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            },
        };
        Chart.Children.Add(glow);

        // The bars' value labels in a layer of their own, added last so they
        // sit over the average line, with the window-coloured halo round them:
        // where the green dashes ran through a number it was hard to read.
        // One effect on the layer, not one per label, for the glow's reason.
        var valueLabels = new Canvas { IsHitTestVisible = false, Effect = _theme.TextHalo };

        // Every bar's value above it when the slots are wide enough for the
        // widest of them - a few sessions, or a wide window. Squeezed, only the
        // highest and lowest keep theirs: forty numbers run together read as
        // none, and the rest are a hover away in the tooltip.
        double labelSize = Math.Max(8, SmallFont - 1);
        bool labelAll = values.Max(v => ValueWidth(BarValue(v), labelSize)) <= slot - 2;

        // Every ID when they fit side by side, else every second, fifth or
        // tenth - counted from the newest, so the latest session always has
        // its number. Run together they would read as one long number.
        double idWidth = ValueWidth(shown[^1].Id.ToString(CultureInfo.InvariantCulture), idSize) + 4;
        int idEvery = new[] { 1, 2, 5, 10 }.FirstOrDefault(n => n * slot >= idWidth, 10);

        for (int i = 0; i < shown.Count; i++)
        {
            var session = shown[i];
            double x = plotLeft + i * slot + (slot - barWidth) / 2;

            var hover = new Border
            {
                Width = slot,
                Height = plotHeight,
                Background = Brushes.Transparent,
            };
            string? headline = null;
            Brush? headlineBrush = null;
            LazyTip(hover, () => SessionTip(session, headline, headlineBrush));
            Canvas.SetLeft(hover, plotLeft + i * slot);
            Canvas.SetTop(hover, plotTop);

            if (measure.Value(session) is double value && value > 0)
            {
                double barHeight = Math.Max(2, plotHeight * Math.Min(1, value / top));

                // Every bar that ties for highest or lowest, not just the
                // first: picking one of two equal sessions would claim a
                // difference there isn't.
                bool isHighest = value >= highest;
                bool isLowest = markLowest && value <= lowest;
                var (normal, lit) = isHighest ? MarkedBar(HighestFoot, HighestTip, HighestBrush, HighestHover, barHeight, plotHeight)
                                  : isLowest ? MarkedBar(LowestFoot, LowestTip, LowestBrush, LowestHover, barHeight, plotHeight)
                                  : PlainBar(barHeight, plotHeight);

                // The picked session's bar stays in its hover colour, so it
                // stands out of the column as well as being inside it.
                bool isPicked = picked.Contains(i);
                if (isPicked)
                    normal = lit;

                // Rounded at both ends, a lit tube rather than a block standing
                // on the baseline - the neon look.
                var bar = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = normal,
                    CornerRadius = new CornerRadius(barWidth / 2),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, baseline - barHeight);
                Chart.Children.Add(bar);

                // Its glow: 2 px wider so the blur has more colour to spread
                // and still shows beside a 2 px bar on a squeezed chart.
                var halo = new Border
                {
                    Width = barWidth + 2,
                    Height = barHeight,
                    Background = normal,
                    CornerRadius = new CornerRadius(barWidth / 2 + 1),
                };
                Canvas.SetLeft(halo, x - 1);
                Canvas.SetTop(halo, baseline - barHeight);
                glow.Children.Add(halo);

                // The picked one always has its number, even squeezed: having
                // picked it, its value is what you came to the chart for.
                if (labelAll || isHighest || isLowest || i == onlyPicked)
                {
                    // In the bar's own colour when it is a marked one, so the
                    // number says which mark it is; dim otherwise.
                    string text = BarValue(value);
                    double textWidth = ValueWidth(text, labelSize);
                    var label = new TextBlock
                    {
                        Text = text,
                        FontSize = labelSize,
                        Foreground = isHighest ? HighestBrush : isLowest ? LowestBrush : _theme.Label,
                        IsHitTestVisible = false,
                    };
                    // Centred over the bar, but kept inside the plot at the ends.
                    double left = Math.Clamp(x + barWidth / 2 - textWidth / 2, plotLeft, width - textWidth);
                    Canvas.SetLeft(label, left);
                    Canvas.SetTop(label, baseline - barHeight - labelSize - 4);
                    valueLabels.Children.Add(label);
                }

                // Not "Highest {name}": the peak measures are already named
                // "Highest CPU °C", and it would say it twice. In the bar's
                // own colour, so the tooltip says which mark it is.
                if (isHighest)
                    (headline, headlineBrush) = ($"Highest in the chart: {value:0}", HighestBrush);
                else if (isLowest)
                    (headline, headlineBrush) = ($"Lowest in the chart: {value:0}", LowestBrush);

                // The glow lights up with its bar, or the hovered bar would
                // sit brighter than its own halo.
                hover.MouseEnter += (_, _) => bar.Background = halo.Background = lit;
                hover.MouseLeave += (_, _) => bar.Background = halo.Background = normal;
            }

            // A new driver or Windows build: a small accent triangle under the
            // bar, in the room the 0 label leaves below the baseline - clear of
            // the value labels above. Whether or not the session has a bar, so
            // the change still shows on a reading it didn't record.
            if (_changes.ContainsKey(session))
            {
                double middle = plotLeft + i * slot + slot / 2;
                Chart.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new(middle - 3.5, baseline + 7), new(middle + 3.5, baseline + 7), new(middle, baseline + 2) },
                    Fill = _theme.Accent,
                    IsHitTestVisible = false,
                });
            }

            // Below the change triangle's row, whether or not there is a bar.
            // A picked session's ID in the accent. A lone pick's always, and a
            // neighbour's that would run into it gives way, or the two read as
            // one number.
            bool idPicked = picked.Contains(i);
            bool crowdsPicked = onlyPicked >= 0 && i != onlyPicked && Math.Abs(i - onlyPicked) * slot < idWidth;
            if (i == onlyPicked || ((shown.Count - 1 - i) % idEvery == 0 && !crowdsPicked))
            {
                string id = session.Id.ToString(CultureInfo.InvariantCulture);
                double idTextWidth = ValueWidth(id, idSize);
                var idLabel = new TextBlock
                {
                    Text = id,
                    FontSize = idSize,
                    Foreground = idPicked ? _theme.Accent : _theme.Header,
                    FontWeight = idPicked ? FontWeights.SemiBold : FontWeights.Normal,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(idLabel, Math.Clamp(plotLeft + i * slot + slot / 2 - idTextWidth / 2, plotLeft, width - idTextWidth));
                Canvas.SetTop(idLabel, baseline + ZeroRoom);
                Chart.Children.Add(idLabel);
            }

            Chart.Children.Add(hover);
        }

        // After the bars, so the line stays visible where it crosses them.
        // Dashed, so it reads as a level rather than as one more gridline.
        // Not hit-testable: the bars' tooltips under it must still work.
        double averageY = Math.Round(baseline - plotHeight * Math.Min(1, average / top)) + 0.5;
        Chart.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = plotLeft, X2 = width, Y1 = averageY, Y2 = averageY,
            Stroke = AverageBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            // Toned down: at full strength the bright green drew the eye
            // away from the bars, which are what the chart is about.
            Opacity = AverageOpacity,
        });
        Chart.Children.Add(valueLabels);
    }

    /// <summary>
    /// One of the average / highest / lowest chips over the chart: the
    /// window's own colour with a little of the chip's mixed in, mostly
    /// opaque so it reads over the game's art as well as over the panel, and
    /// an outline in the chip's colour. The figure in the theme's text colour
    /// rather than the chip's, which would be faint on a light theme.
    /// </summary>
    private void StyleChip(Border chip, TextBlock text, Brush colour)
    {
        // The accent brush may not be a plain colour in every theme; the
        // accent's own colour stands in for it then.
        Color c = colour is SolidColorBrush solid ? solid.Color : _theme.AccentColor, w = _theme.WindowColor;
        byte Mix(byte window, byte tint) => (byte)Math.Round(window * 0.82 + tint * 0.18);

        var fill = new SolidColorBrush(Color.FromArgb(0xD9, Mix(w.R, c.R), Mix(w.G, c.G), Mix(w.B, c.B)));
        fill.Freeze();
        var edge = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B));
        edge.Freeze();

        chip.Background = fill;
        chip.BorderBrush = edge;
        text.Foreground = _theme.NotesText;
        text.FontSize = UiTheme.PanelFontSize;
    }

    /// <summary>
    /// A chip's words: the caption small and dim, the figure larger and
    /// bold, so the number is what the eye lands on.
    /// </summary>
    private void SetChip(TextBlock text, string caption, string value)
    {
        text.Inlines.Clear();
        // No caption run at all for an icon-only chip - see ShowLastSession -
        // or its two spaces would pad the figure on the left.
        if (caption.Length > 0)
            text.Inlines.Add(new System.Windows.Documents.Run(caption + "  ")
        {
            Foreground = _theme.Label,
            FontSize = SmallFont,
        });
        text.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = FontWeights.SemiBold });
    }

    /// <summary>The average line's opacity, which its key in the legend shares so the two still match.</summary>
    private const double AverageOpacity = 0.55;

    /// <summary>The chart's neon bars at their widest - see DrawChart.</summary>
    private const double NeonBarWidth = 11;

    /// <summary>
    /// An unmarked bar's colour and its hover: the blue, or with BarGradient on
    /// the settings' gradient rising from the baseline. The gradient spans the
    /// whole plot height rather than the bar, so like the widget's bars only a
    /// tall one reaches the end colour. The highest and lowest keep their
    /// orange and red, which is what makes them findable.
    /// </summary>
    private (Brush Normal, Brush Lit) PlainBar(double barHeight, double plotHeight)
    {
        if (!_settings.BarGradient)
            return (BarBrush, BarHover);

        return GradientBar(_theme.BarGradientStart, _theme.BarGradientEnd, barHeight, plotHeight);
    }

    /// <summary>
    /// The highest or lowest bar: its own gradient when the plain bars have
    /// one, laid out the same way so all bars agree, else its solid colour.
    /// </summary>
    private (Brush Normal, Brush Lit) MarkedBar(Color foot, Color tip, Brush solid, Brush solidLit,
                                                double barHeight, double plotHeight)
        => _settings.BarGradient ? GradientBar(foot, tip, barHeight, plotHeight) : (solid, solidLit);

    /// <summary>A bar's gradient from <paramref name="start"/> at its foot, and the lighter one for its hover.</summary>
    private (Brush Normal, Brush Lit) GradientBar(Color start, Color end, double barHeight, double plotHeight)
    {
        // In the bar's own pixels: its foot is at barHeight, the plot's top
        // plotHeight above that - or, with BarGradientSpan "bar", the bar's
        // own top, so every bar shows the whole gradient as the widget's do.
        var foot = new Point(0, barHeight);
        var top = new Point(0, _theme.GradientAcrossBar ? 0 : barHeight - plotHeight);
        return (UiTheme.MakeGradient(start, end, foot, top),
                UiTheme.MakeGradient(UiTheme.Lighten(start, 0.35), UiTheme.Lighten(end, 0.35), foot, top));
    }

    /// <summary>The target the scores on screen were worked out for, so a new one re-scores them.</summary>
    private double _scoredFor = PerformanceScore.Target;

    /// <summary>
    /// For a settings change: the chart reads its colours from the theme only
    /// while drawing. A new target frame rate changes every score - in the
    /// cards, the table and a chart of scores - so it shows the lot again;
    /// only then, since this runs on every keystroke in the settings window.
    /// </summary>
    public void RedrawChart()
    {
        if (!IsLoaded)
            return;

        if (_scoredFor != PerformanceScore.Target)
        {
            _scoredFor = PerformanceScore.Target;
            ShowSelection(GamePicker.SelectedItem, null);
        }
        else
            DrawChart();

        // A new energy price or currency: the Cost column comes, goes or
        // changes - only then, for the same every-keystroke reason.
        if (_energyPrice != _settings.EnergyPrice || _energyCurrency != (_settings.EnergyCurrency?.Trim() ?? string.Empty))
        {
            FitColumns();
            ShowRows();
        }
    }

    /// <summary>A bar's value as its label says it: whole, with a decimal only under 10 where it matters.</summary>
    private static string BarValue(double value)
        => value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);

    /// <summary>How wide <paramref name="text"/> is drawn in the window's font, for whether labels fit.</summary>
    private double ValueWidth(string text, double size)
        => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                             new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                             size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// A round number at or above <paramref name="value"/> that divides into
    /// four tidy gridlines: 60, 120, 240 rather than 217.
    /// </summary>
    private static double NiceCeiling(double value)
    {
        foreach (double nice in new double[] { 20, 40, 60, 80, 120, 160, 200, 240, 300, 400, 600, 800, 1000 })
            if (value <= nice)
                return nice;

        return Math.Ceiling(value / 200) * 200;
    }

    // --- Table -----------------------------------------------------------------

    /// <summary>
    /// ID and Date always - the ID is what DELETE names, and a row with no
    /// date can't be told from the next; the rest as picked. Score too: it is
    /// the column for finding the sessions that went badly, and a settings.json
    /// from before it existed would otherwise hide it.
    /// </summary>
    private static bool AlwaysShown(string key) => key is "id" or "date" or "score";

    /// <summary>What changed since the session before, per session that had a change - see ChangesIn.</summary>
    private Dictionary<SessionRecord, List<Change>> _changes = new();

    /// <param name="Mark">The word in the table and on the chart: "new driver", "Windows update".</param>
    /// <param name="Detail">What it was and is now, for the tooltip: "driver 576.80 → 581.57".</param>
    private sealed record Change(string Mark, string Detail);

    /// <summary>
    /// The sessions played on a different GPU driver or Windows build than
    /// the one before them, in this game - for "did that update change
    /// anything?", which an FPS drop usually raises. Compared with the last
    /// session that recorded one, so a session logged before 1.65 (with
    /// neither) is skipped rather than read as a change from "unknown".
    /// </summary>
    private static Dictionary<SessionRecord, List<Change>> ChangesIn(IReadOnlyList<SessionRecord> sessions)
    {
        var changes = new Dictionary<SessionRecord, List<Change>>();
        string? driver = null, build = null;

        foreach (var session in sessions)   // oldest first, as the log has them
        {
            var found = new List<Change>();
            if (session.Driver is { } newDriver)
            {
                if (driver is not null && driver != newDriver)
                    found.Add(new Change("new driver", $"GPU driver {driver} → {newDriver}"));
                driver = newDriver;
            }
            if (session.WindowsBuild is { } newBuild)
            {
                if (build is not null && build != newBuild)
                    found.Add(new Change("Windows update", $"Windows build {build} → {newBuild}"));
                build = newBuild;
            }
            if (found.Count > 0)
                changes[session] = found;
        }

        return changes;
    }

    /// <summary>
    /// The words after a session's row: "cut short" in the warning colour,
    /// since it changes what the numbers mean, and a driver or Windows change
    /// in the accent - news, not a fault.
    /// </summary>
    private IReadOnlyList<(string Text, Brush Brush)>? SessionMarks(SessionRecord session)
    {
        var marks = new List<(string, Brush)>();
        if (session.CutShort)
            marks.Add(("cut short", _theme.Warn));
        if (_changes.TryGetValue(session, out var changes))
            foreach (var change in changes)
                marks.Add((change.Mark, _theme.Accent));
        return marks.Count > 0 ? marks : null;
    }

    /// <summary>Cost only with a price set: without one it would be a column of dashes.</summary>
    private IReadOnlyList<Column<SessionRecord>> VisibleSessionColumns()
        => AllSessionColumns.Where(c => (AlwaysShown(c.Key) || _settings.SessionColumns.Contains(c.Key))
                                        && (c.Key != "cost" || _settings.EnergyPrice > 0)).ToList();

    private void ShowRows()
    {
        DeleteButton.IsEnabled = !_allGames && _selected.Count > 0;
        DeleteAllButton.IsEnabled = !_allGames && _sessions.Count > 0;
        // The games table's rows open a game rather than pick it, so the
        // hint would be wrong there.
        PickHint.Visibility = !_allGames && _sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _energyPrice = _settings.EnergyPrice;
        _energyCurrency = _settings.EnergyCurrency?.Trim() ?? string.Empty;

        if (_allGames)
            ShowTable(GameColumns, _games, _gameSort,
                      sort => _gameSort = sort,
                      GameTooltip, mark: null,
                      open: g => FillPicker(g.Name), selected: null);
        else
            // Newest first underneath the sort, so equal values - every
            // session with no latency, say - still come newest first.
            ShowTable(VisibleSessionColumns(), _sessions.Reverse().ToList(), _sessionSort,
                      sort => _sessionSort = sort,
                      s => SessionTip(s), mark: SessionMarks,
                      open: Select, selected: s => _selected.Contains(s));
    }

    /// <summary>
    /// A click picks the session for Delete, and a click on the only picked
    /// one lets it go again. Ctrl+click adds or removes one without touching
    /// the rest, as in Explorer, so several can go in one delete.
    /// </summary>
    private void Select(SessionRecord session)
    {
        var before = _selected.ToList();
        bool had = _selected.Contains(session);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (had)
                _selected.Remove(session);
            else
                _selected.Add(session);
        }
        else
        {
            // A plain click on one of several picked keeps just that one,
            // as Explorer does; only on the lone pick does it let go.
            bool lone = had && _selected.Count == 1;
            _selected.Clear();
            if (!lone)
                _selected.Add(session);
        }
        DisarmDelete();

        // Only the rows whose pick changed are repainted. ShowRows rebuilt the
        // whole table - some twenty elements a session, every session - on
        // every click, which grew slow as a game's history did.
        foreach (var changed in before.Concat(_selected).Distinct())
            if (_sessionRows.TryGetValue(changed, out var row))
                row.Background = _selected.Contains(changed) ? SelectedBrush : Brushes.Transparent;
        DeleteButton.IsEnabled = !_allGames && _selected.Count > 0;

        // The chart marks the picked session too - see DrawChart.
        DrawChart();
    }

    /// <summary>
    /// Each session's row in the table as last built, so a click can repaint
    /// its background without building the table again - see Select.
    /// </summary>
    private readonly Dictionary<SessionRecord, Grid> _sessionRows = new();

    /// <summary>
    /// The heading row and every row under it. One method for both tables, so
    /// sorting, hover and alignment can't drift apart between them.
    ///
    /// <paramref name="mark"/> goes in the space after the last column: the
    /// sessions' "cut short", since a session DeskMon was closed in the middle
    /// of may have run longer than it says. <paramref name="open"/> makes the
    /// rows clickable, and <paramref name="selected"/> lights the picked one.
    /// </summary>
    private void ShowTable<T>(IReadOnlyList<Column<T>> columns, IReadOnlyList<T> items,
                              (string Key, bool Descending) sort, Action<(string, bool)> setSort,
                              Func<T, object> tooltip, Func<T, IReadOnlyList<(string Text, Brush Brush)>?>? mark, Action<T>? open,
                              Func<T, bool>? selected)
    {
        // A sort by a column just hidden falls back to the first one,
        // rather than ordering the rows by something no longer on screen.
        var sortBy = columns.FirstOrDefault(c => c.Key == sort.Key);
        if (sortBy is null)
        {
            sortBy = columns[0];
            sort = (sortBy.Key, sortBy.DescendingFirst);
            setSort(sort);
        }

        BuildHeader(columns, sort, setSort);

        // The groups' dividing lines once, full height over the whole table,
        // in the same columns as the rows. They used to be added to every row
        // - about seven elements a row, thousands for a long-played game,
        // rebuilt on every sort and refresh.
        GroupLines.Children.Clear();
        GroupLines.ColumnDefinitions.Clear();
        AddColumns(GroupLines, columns);
        AddGroupLines(GroupLines, columns, 1);

        Rows.Children.Clear();
        _sessionRows.Clear();
        bool firstRow = true;
        foreach (var item in Sorted(items, sortBy, sort.Descending))
        {
            // The lit blue the widget's toolbar uses for "this one is open":
            // the same meaning, picked rather than merely pointed at.
            Brush rest = selected?.Invoke(item) == true ? SelectedBrush : Brushes.Transparent;

            var row = new Grid
            {
                Background = rest,
                Cursor = open is null ? null : Cursors.Hand,
            };
            LazyTip(row, () => tooltip(item));
            AddColumns(row, columns);
            if (item is SessionRecord record)
                _sessionRows[record] = row;   // for Select's repaint

            // The widget's row hover (MainWindow's RowHover): the same faint
            // strip, faded in quickly and out a little slower, over a picked
            // row's blue as well. It was a plain swap to the bar-track grey,
            // too faint to follow along a line of dates. First, so it is drawn
            // behind the text; its colour by reference, so it follows the theme.
            var hover = new Border { Opacity = 0, IsHitTestVisible = false };
            hover.SetResourceReference(Border.BackgroundProperty, "Theme.Ov.14");
            Grid.SetColumnSpan(hover, columns.Count + 1);
            row.Children.Add(hover);

            // A thin line between rows, so a session's date, time and length
            // read as one line across the gaps between the columns.
            if (!firstRow)
            {
                var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = _theme.Separator, IsHitTestVisible = false };
                Grid.SetColumnSpan(rule, columns.Count + 1);
                row.Children.Add(rule);
            }
            firstRow = false;

            for (int i = 0; i < columns.Count; i++)
            {
                var cell = new TextBlock
                {
                    Text = columns[i].Text(item),
                    Foreground = columns[i].Score?.Invoke(item) is int score ? ScoreBrush(score)
                               : i == 0 ? _theme.Label : _theme.NotesText,
                    TextAlignment = columns[i].Number ? TextAlignment.Right : TextAlignment.Left,
                    Margin = new Thickness(columns[i].Number || i == 0 ? 0 : ColumnGap, 3, columns[i].Number ? 10 : 0, 3),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                // The source's icon in the line before the figure, dim, so a
                // column of Steam hours and save hours can be told apart at a
                // glance. Inline rather than a cell of its own: the figures
                // stay right-aligned under the heading, icon and all.
                if (columns[i].Source?.Invoke(item) is { } source)
                {
                    var icon = PlaytimeIcons.Make(source, _theme.Label);
                    icon.Margin = new Thickness(0, 0, 5, 0);
                    cell.Inlines.Clear();
                    cell.Inlines.Add(new System.Windows.Documents.InlineUIContainer(icon)
                    {
                        BaselineAlignment = BaselineAlignment.Center,
                    });
                    cell.Inlines.Add(new System.Windows.Documents.Run(columns[i].Text(item)));
                }
                Grid.SetColumn(cell, i);
                row.Children.Add(cell);
            }

            if (mark?.Invoke(item) is { } marks)
            {
                // Each in its own colour - see SessionMarks - one line however many.
                var text = new TextBlock
                {
                    FontSize = SmallFont,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                for (int m = 0; m < marks.Count; m++)
                {
                    if (m > 0)
                        text.Inlines.Add(new System.Windows.Documents.Run("  ·  ") { Foreground = _theme.Header });
                    text.Inlines.Add(new System.Windows.Documents.Run(marks[m].Text) { Foreground = marks[m].Brush });
                }
                Grid.SetColumn(text, columns.Count);
                row.Children.Add(text);
            }

            // Hover like the other lists, so the tooltip reads as belonging to the row.
            row.MouseEnter += (_, _) => hover.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromSeconds(0.12)));
            row.MouseLeave += (_, _) => hover.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromSeconds(0.2)));
            if (open is not null)
                row.MouseLeftButtonUp += (_, _) => open(item);

            Rows.Children.Add(row);
        }
    }

    /// <summary>
    /// Headings that sort when clicked, the sorted one brighter and with an
    /// arrow. The arrow goes on the side the text is aligned to, so a number
    /// column's heading still lines up with the numbers under it.
    /// </summary>
    private void BuildHeader<T>(IReadOnlyList<Column<T>> columns, (string Key, bool Descending) sort,
                                Action<(string, bool)> setSort)
    {
        TableHeader.Children.Clear();
        TableHeader.ColumnDefinitions.Clear();
        TableHeader.RowDefinitions.Clear();
        AddColumns(TableHeader, columns);

        // A line of group names over the headings - "Frame rate" over Avg FPS
        // to Latency - so a group reads as one even with a single column of it
        // shown. Only for a table that has groups: All games has none, and an
        // empty line would only push its table down.
        bool grouped = columns.Any(c => c.Group.Length > 0);
        if (grouped)
        {
            TableHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TableHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int start = 0; start < columns.Count;)
            {
                int end = start + 1;
                while (end < columns.Count && !StartsGroup(columns, end))
                    end++;

                if (columns[start].Group.Length > 0)
                {
                    // Underlined across its columns, the gap before it left
                    // out, so each group's line stops short of the next one's.
                    var name = new Border
                    {
                        BorderBrush = _theme.Separator,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(StartsGroup(columns, start) ? GroupGap : 0, 0, 4, 3),
                        Child = new TextBlock
                        {
                            Text = columns[start].Group,
                            Foreground = _theme.Header,
                            FontSize = SmallFont,
                            TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 0, 0, 1),
                        },
                    };
                    Grid.SetColumn(name, start);
                    Grid.SetColumnSpan(name, end - start);
                    TableHeader.Children.Add(name);
                }
                start = end;
            }
        }

        AddGroupLines(TableHeader, columns, grouped ? 2 : 1);

        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            bool sorted = column.Key == sort.Key;
            string arrow = sort.Descending ? "▼" : "▲";

            var heading = new TextBlock
            {
                Text = !sorted ? column.Header
                     : column.Number ? $"{arrow} {column.Header}"
                     : $"{column.Header} {arrow}",
                Foreground = sorted ? _theme.Label : _theme.Header,
                FontSize = SmallFont,
                TextAlignment = column.Number ? TextAlignment.Right : TextAlignment.Left,
                // The same gap as the cells, so each heading starts over its column.
                Margin = new Thickness(column.Number || i == 0 ? 0 : ColumnGap, 0, column.Number ? 10 : 0, 0),
                Background = Brushes.Transparent,   // the whole cell clicks, not just the letters
                Cursor = Cursors.Hand,
                ToolTip = $"Sort by {column.Header}",
            };

            heading.MouseLeftButtonUp += (_, _) =>
            {
                // A second click on the same heading turns it round.
                setSort(column.Key == sort.Key ? (column.Key, !sort.Descending) : (column.Key, column.DescendingFirst));
                ShowRows();
            };

            Grid.SetColumn(heading, i);
            if (grouped)
                Grid.SetRow(heading, 1);
            TableHeader.Children.Add(heading);
        }
    }

    /// <summary>The fixed columns, then one that takes the rest - for the "cut short" mark.</summary>
    private void AddColumns<T>(Grid grid, IReadOnlyList<Column<T>> columns)
    {
        for (int i = 0; i < columns.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth(columns[i], i) + (StartsGroup(columns, i) ? GroupGap : 0)) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    /// <summary>
    /// Extra room before the first column of a group, with the dividing line
    /// in its middle. Only number columns start a group after the first, and
    /// those are right-aligned, so the room shows up as space on their left.
    /// </summary>
    private const double GroupGap = 14;

    private static bool StartsGroup<T>(IReadOnlyList<Column<T>> columns, int index)
        => index > 0 && columns[index].Group != columns[index - 1].Group;

    /// <summary>
    /// A faint upright line where a group begins, so the eye stays inside one
    /// group while reading along a row. Hit-test off, so clicks still reach the row.
    /// </summary>
    private void AddGroupLines<T>(Grid grid, IReadOnlyList<Column<T>> columns, int rowSpan)
    {
        for (int i = 1; i < columns.Count; i++)
        {
            if (!StartsGroup(columns, i))
                continue;
            var line = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(GroupGap / 2, 0, 0, 0),
                Background = _theme.Separator,
                IsHitTestVisible = false,
            };
            Grid.SetColumn(line, i);
            Grid.SetRowSpan(line, rowSpan);
            grid.Children.Add(line);
        }
    }

    /// <summary>
    /// The column's set width, or wider if its heading needs it - with room
    /// for the sort arrow whether or not it is sorted, so sorting never makes
    /// the columns jump. Fixed widths alone cut "▼ Stutters" to "▼ Stutter",
    /// and a theme with a wider font cut "CPU+GPU kWh" even unsorted.
    /// </summary>
    private double ColumnWidth<T>(Column<T> column, int index)
    {
        // The heading's own margin - see BuildHeader - and a few pixels spare.
        // Measured in the font the heading row really has: the theme's window
        // font reaches it through the tree, not through this window's own
        // FontFamily, which ValueWidth reads.
        double margin = column.Number ? 10 : index == 0 ? 0 : ColumnGap;
        var font = (FontFamily)TableHeader.GetValue(System.Windows.Documents.TextElement.FontFamilyProperty);
        double text = new FormattedText("▼ " + column.Header, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                        new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                        SmallFont, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        return Math.Max(column.Width, Math.Ceiling(text + margin + 4));
    }

    /// <summary>
    /// Rows without the value last, whichever way round: a session that
    /// recorded no frame rate is not the slowest one, and not the fastest.
    /// OrderBy is stable, so equal values keep the order they came in.
    /// </summary>
    private static IEnumerable<T> Sorted<T>(IReadOnlyList<T> items, Column<T> column, bool descending)
    {
        var known = items.Where(i => column.Sort(i) is not null);
        var missing = items.Where(i => column.Sort(i) is null);

        var ordered = descending
            ? known.OrderByDescending(column.Sort, Comparer<IComparable?>.Default)
            : known.OrderBy(column.Sort, Comparer<IComparable?>.Default);

        return ordered.Concat(missing);
    }

    /// <summary>
    /// A checklist of every column but Date. Stays open between clicks, since
    /// picking columns is usually picking several.
    /// </summary>
    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = ColumnsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        // Cost only once a price is set, as in the table - ticking it would show nothing.
        string? group = null;
        foreach (var column in AllSessionColumns.Where(c => !AlwaysShown(c.Key) && (c.Key != "cost" || _settings.EnergyPrice > 0)))
        {
            // A line between groups, as in the table, so a list of twenty
            // is read as seven short ones.
            if (group is not null && column.Group != group)
                menu.Items.Add(new Separator());
            group = column.Group;

            var item = new MenuItem
            {
                Header = column.Header,
                IsCheckable = true,
                IsChecked = _settings.SessionColumns.Contains(column.Key),
                StaysOpenOnClick = true,
            };

            item.Click += (_, _) =>
            {
                _settings.SessionColumns.Remove(column.Key);
                if (item.IsChecked)
                    _settings.SessionColumns.Add(column.Key);
                _settings.Save();

                FitColumns();
                ShowRows();
            };

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "Back to the default columns" };
        reset.Click += (_, _) =>
        {
            _settings.SessionColumns = AppSettings.DefaultSessionColumns();
            _settings.Save();
            ShowRows();
        };
        menu.Items.Add(reset);

        menu.IsOpen = true;
    }

    /// <summary>
    /// Widens the window when a column was added that no longer fits, rather
    /// than leaving the last columns cut off at the edge. Never narrows it:
    /// the width is the user's, and taking a column away shouldn't move it.
    /// </summary>
    private void FitColumns()
    {
        // The margins, the scroll bar, and room for "cut short".
        const double Around = 100;

        // With the group gaps, which AddColumns adds on top of each width.
        var columns = VisibleSessionColumns();
        double needed = columns.Select(ColumnWidth).Sum()
                      + Enumerable.Range(0, columns.Count).Count(i => StartsGroup(columns, i)) * GroupGap
                      + Around;
        if (Width < needed)
            Width = Math.Min(needed, SystemParameters.WorkArea.Width);
    }

    private static string GameTooltip(GameTotal game)
        => $"{game.Name}\n" +
           $"{game.Sessions} {(game.Sessions == 1 ? "session" : "sessions")}  ·  {SessionRecord.Describe(game.Played)} logged" +
           (game.AverageFps is double fps ? $"  ·  {fps:0} fps on average" : string.Empty) +
           (game.Score is int score
               ? $"\nPerformance score {score} - {PerformanceScore.Grade(score)}, from its last {PerformanceScore.GameSessions} sessions"
               : string.Empty) +
           // Where the Playtime column's figure is from, which the column itself has no room to say.
           $"\nPlaytime {GamePlaytime.Describe(game.Playtime.Time)} {GamePlaytime.Where(game.Playtime.Source)}" +
           $"\nFirst played {game.FirstPlayed.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}  ·  " +
           $"last {game.LastPlayed.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}" +
           "\nClick to see its sessions";

    /// <summary>
    /// A session in full, for tooltips: the day as a heading, then the time,
    /// length and average on a dim line, then every peak as a table grouped by
    /// widget section - the whole log line, readable.
    ///
    /// It used to be text: a line per section with the readings strung along
    /// it, so each line's CPU, GPU... started wherever the one before ended,
    /// and nothing lined up. As a table, every reading has a column of its
    /// own, so the first of each line - CPU, CPU, FPS - sits under the others,
    /// as in the notes window's last session.
    /// </summary>
    /// <param name="headline">A line above it all, in <paramref name="headlineBrush"/>: the chart's "Highest in the chart: 142".</param>
    /// <summary>
    /// A tooltip made when it is about to show, not when its row is. The table
    /// can hold hundreds of sessions and is rebuilt on every sort; making each
    /// row's card up front would build hundreds that are never seen.
    ///
    /// The placeholder only has to be non-null for the tooltip to open at all;
    /// it is replaced in ToolTipOpening, before anything is shown, which is
    /// what WPF reads.
    /// </summary>
    private static void LazyTip(FrameworkElement element, Func<object> make)
    {
        element.ToolTip = string.Empty;
        element.ToolTipOpening += (_, _) => element.ToolTip = make();
    }

    private ToolTip SessionTip(SessionRecord session, string? headline = null, Brush? headlineBrush = null)
    {
        var card = new StackPanel();

        if (headline is not null)
            card.Children.Add(new TextBlock
            {
                Text = headline,
                Foreground = headlineBrush ?? _theme.NotesText,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5),
            });

        card.Children.Add(new TextBlock
        {
            Text = $"Session ID {session.Id}  ·  " + session.Start.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
            Foreground = _theme.NotesText,
            FontWeight = FontWeights.SemiBold,
        });

        string when = $"{session.Start:HH:mm}–{session.End:HH:mm}  ·  {SessionRecord.Describe(session.Length)}";
        if (session.AverageFps is double fps)
            when += $"  ·  {fps:0} fps average";
        if (session.OnePercentLow is double low)
            when += $"  ·  1% low {low:0}";
        card.Children.Add(new TextBlock { Text = when, Foreground = _theme.Label, Margin = new Thickness(0, 1, 0, 0) });

        // The stutters as a rate as well as a count - "37" alone says little
        // without the length beside it - and the energy with what it covers.
        var extra = new List<string>();
        if (session.Stutters is long stutters)
        {
            string rate = stutters > 0 && session.Length.TotalMinutes > 0
                ? $" - about 1 every {Every(session.Length / stutters)}"
                : string.Empty;
            extra.Add(stutters == 1 ? $"1 stutter{rate}" : $"{stutters} stutters{rate}");
        }
        if (session.EnergyWh is double wh)
        {
            string cost = _energyPrice > 0 ? $" ({Cost(wh)})" : string.Empty;
            extra.Add($"CPU+GPU {Kwh(wh)} kWh{cost} - the whole PC draws more");
        }
        if (extra.Count > 0)
            card.Children.Add(new TextBlock { Text = string.Join("  ·  ", extra), Foreground = _theme.Label, Margin = new Thickness(0, 1, 0, 0) });

        // In the accent, as in the table: the update to line an FPS change up with.
        if (_changes.TryGetValue(session, out var changes))
            foreach (var change in changes)
                card.Children.Add(new TextBlock { Text = change.Detail, Foreground = _theme.Accent, Margin = new Thickness(0, 3, 0, 0) });

        var sections = session.PeaksBySection().ToList();
        if (sections.Count > 0)
        {
            card.Children.Add(new Border { Height = 1, Background = _theme.Separator, Margin = new Thickness(0, 7, 0, 5) });

            var table = new Grid();
            int readings = sections.Max(s => s.Peaks.Count);
            for (int i = 0; i < 1 + readings * 2; i++)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            void Cell(string text, Brush brush, int row, int column, double right)
            {
                var cell = new TextBlock { Text = text, Foreground = brush, Margin = new Thickness(0, 1, right, 1) };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                table.Children.Add(cell);
            }

            for (int row = 0; row < sections.Count; row++)
            {
                var (section, peaks) = sections[row];
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Cell(section, _theme.Header, row, 0, 14);
                for (int i = 0; i < peaks.Count; i++)
                {
                    Cell(peaks[i].Label, _theme.Label, row, 1 + i * 2, 5);
                    Cell(peaks[i].Text, _theme.NotesText, row, 2 + i * 2, 16);
                }
            }

            card.Children.Add(table);
            card.Children.Add(new TextBlock
            {
                Text = "The worst each reached:  ↑ highest   ↓ lowest",
                Foreground = _theme.Header,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
            });
        }

        // The score, and what it was made of: which part cost the points is
        // what says where to look - the frame rate, the dips, or the heat.
        if (PerformanceScore.For(session) is { } result)
        {
            card.Children.Add(new Border { Height = 1, Background = _theme.Separator, Margin = new Thickness(0, 7, 0, 5) });
            card.Children.Add(new TextBlock
            {
                Text = $"Performance score {result.Score} - {PerformanceScore.Grade(result.Score)}",
                Foreground = ScoreBrush(result.Score),
                FontWeight = FontWeights.SemiBold,
            });

            var parts = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            for (int i = 0; i < 3; i++)
                parts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            for (int row = 0; row < result.Parts.Count; row++)
            {
                var part = result.Parts[row];
                parts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Points as earned of what the part is worth, right-aligned so they line up.
                var cells = new (string Text, Brush Brush, TextAlignment Align, double Right)[]
                {
                    (part.Name, _theme.Label, TextAlignment.Left, 14),
                    ($"{part.Points:0} / {part.Of:0}", _theme.NotesText, TextAlignment.Right, 14),
                    (part.Detail, _theme.Header, TextAlignment.Left, 0),
                };
                for (int column = 0; column < cells.Length; column++)
                {
                    var cell = new TextBlock
                    {
                        Text = cells[column].Text,
                        Foreground = cells[column].Brush,
                        TextAlignment = cells[column].Align,
                        Margin = new Thickness(0, 1, cells[column].Right, 1),
                    };
                    Grid.SetRow(cell, row);
                    Grid.SetColumn(cell, column);
                    parts.Children.Add(cell);
                }
            }
            card.Children.Add(parts);

            if (result.Partial)
                card.Children.Add(new TextBlock
                {
                    Text = "Frame rate and thermals only: logged before DeskMon measured a session's lows and frame times.",
                    Foreground = _theme.Header,
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0),
                });
        }

        // "cut short, DeskMon closed" in the warning colour, since it changes
        // what every number above means; anything else on the line plainly.
        if (session.Note is { } note)
            card.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = session.CutShort ? _theme.Warn : _theme.Label,
                Margin = new Thickness(0, 6, 0, 0),
            });

        // Wider than the tooltip style's 340, which suits a sentence: a table
        // doesn't wrap, and a long Game line would be cut off at the edge.
        return new ToolTip { Content = card, MaxWidth = 640 };
    }

    // --- Chrome ------------------------------------------------------------------

    private double SmallFont => Math.Max(9, UiTheme.PanelFontSize - 2.5);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleHeaderMouseDown(e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The window already reads again when a session is written; this is for
    /// the log changed by hand in Notepad, which nothing tells DeskMon about.
    /// </summary>
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // A new screenshot, or Steam's art updated, shows on a refresh.
        _art.Clear();
        GameArt.Forget();
        FillPicker(GamePicker.SelectedItem);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0 || GamePicker.SelectedItem is not string game)
            return;

        if (!Arm(DeleteButton, DeleteText, $"CLICK AGAIN TO DELETE {PickedName()}"))
            return;

        DisarmDelete();
        // Session lines are now written on AtomicFile's writer (M1); any
        // still queued land first, or the rewrite below could drop them.
        AtomicFile.Flush(TimeSpan.FromSeconds(3));
        FinishDelete(game, _history.Delete(game, _selected), _selected.Count == 1 ? "the session" : "the sessions");
    }

    /// <summary>
    /// Every session of the game on show, for starting its history over - a
    /// new PC, or a log full of test runs. The shown game only, never every
    /// game: that would be one misclick away from losing all of it.
    /// </summary>
    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allGames || _sessions.Count == 0 || GamePicker.SelectedItem is not string game)
            return;

        // The count in the warning, so it says what is about to go.
        if (!Arm(DeleteAllButton, DeleteAllText, $"CLICK AGAIN TO DELETE ALL {_sessions.Count} SESSIONS"))
            return;

        DisarmDelete();
        AtomicFile.Flush(TimeSpan.FromSeconds(3));   // a queued line would otherwise bring the log back
        FinishDelete(game, _history.DeleteAll(game), "the sessions");
    }

    /// <summary>
    /// The first click of a delete: arms <paramref name="button"/> and returns
    /// false, or true when it was already armed and this click confirms it.
    /// In the warning colour, and said in full, so the second click is a
    /// decision and not a double-click that happened to land.
    /// </summary>
    private bool Arm(Button button, TextBlock text, string warning)
    {
        if (_deleteArmed.IsEnabled && _armedButton == button)
            return true;

        // Disarms the other one first, so only one button ever waits for a second click.
        DisarmDelete();
        text.Text = warning;
        button.Foreground = _theme.Warn;
        _armedButton = button;
        _deleteArmed.Start();
        return false;
    }

    /// <summary>After a delete: the error in the summary, or the window read again.</summary>
    private void FinishDelete(string game, string? error, string what)
    {
        if (error is not null)
        {
            AddSummary(SummaryText.Inlines.Count > 0
                ? $"\nCouldn't delete {what}: {error}"
                : $"Couldn't delete {what}: {error}");
            SummaryText.Inlines.LastInline.Foreground = _theme.Warn;
            return;
        }

        _selected.Clear();
        FillPicker(game);
        SessionDeleted?.Invoke();
    }

    /// <summary>"ID 123" for one picked session, "3 SESSIONS" for several.</summary>
    private string PickedName()
        => _selected.Count == 1 ? $"ID {_selected[0].Id}" : $"{_selected.Count} SESSIONS";

    private void DisarmDelete()
    {
        _deleteArmed.Stop();
        _armedButton = null;
        DeleteText.Text = _selected.Count == 0 ? "DELETE"
                        : _selected.Count == 1 ? $"DELETE: SESSION ID {_selected[0].Id}"
                        : $"DELETE: {_selected.Count} SESSIONS";
        DeleteButton.ClearValue(ForegroundProperty);
        DeleteAllText.Text = "DELETE ALL";
        DeleteAllButton.ClearValue(ForegroundProperty);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (GamePicker.SelectedItem is not string game)
            return;

        string path = _history.PathFor(game);
        if (!File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* nothing useful to do */ }
    }

    /// <summary>
    /// The game's sessions as a CSV, for a spreadsheet. The log itself is
    /// already one line per session, but in DeskMon's own wording ("CPU 78 °C,
    /// FPS ↓41"); this splits it into one column per figure, which is what a
    /// spreadsheet needs to chart or sort it.
    ///
    /// Only what the log keeps - each session's averages and peaks. There is
    /// no second-by-second record to export.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (GamePicker.SelectedItem is not string game || _sessions.Count == 0)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export sessions",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            // A game's name can hold characters a file name can't.
            FileName = string.Concat($"{game} sessions".Split(Path.GetInvalidFileNameChars())) + ".csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            // With a BOM: without one Excel reads the file as the local code
            // page and "°C" turns into "Â°C".
            File.WriteAllText(dialog.FileName, SessionsCsv(_sessions), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            AddSummary(SummaryText.Inlines.Count > 0
                ? $"\nCouldn't export the sessions: {ex.Message}"
                : $"Couldn't export the sessions: {ex.Message}");
            SummaryText.Inlines.LastInline.Foreground = _theme.Warn;
        }
    }

    /// <summary>
    /// Commas and a full stop for the decimals, whatever the PC's region - the
    /// form every spreadsheet's import understands. Swedish Excel opening it by
    /// double-click splits on semicolons instead, but Data > From Text reads it.
    /// </summary>
    internal static string SessionsCsv(IReadOnlyList<SessionRecord> sessions)
    {
        // One column per reading any session has, in the order they first
        // appear. Keyed with the unit, since CPU is both a load and a temperature.
        var peaks = sessions
            .SelectMany(s => s.Peaks)
            .Select(p => (p.Label, p.Unit))
            .Distinct()
            .ToList();

        var csv = new System.Text.StringBuilder();
        // Cost only with a price set, as in the table - and worked out at that
        // price, since the log keeps the energy rather than what it cost.
        bool costed = _energyPrice > 0;
        string costHeader = _energyCurrency.Length > 0 ? $"Cost ({_energyCurrency})" : "Cost";

        var header = new List<string> { "ID", "Date", "Start", "End", "Length (min)", "Avg FPS", "1% low", "0.1% low", "Jitter (ms)",
                                        "Stutters", "CPU+GPU energy (Wh)" };
        if (costed)
            header.Add(costHeader);
        header.AddRange(peaks.Select(p => p.Unit.Length > 0 ? $"{p.Label} {p.Unit}" : p.Label));
        header.Add("GPU driver");
        header.Add("Windows build");
        header.Add("Note");
        csv.AppendLine(string.Join(",", header.Select(Cell)));

        var inv = CultureInfo.InvariantCulture;
        foreach (var s in sessions)
        {
            var row = new List<string>
            {
                s.Id.ToString(inv),
                s.Start.ToString("yyyy-MM-dd", inv),
                s.Start.ToString("HH:mm", inv),
                s.End.ToString("HH:mm", inv),
                s.Length.TotalMinutes.ToString("0.0", inv),
                s.AverageFps?.ToString("0.0", inv) ?? string.Empty,
                s.OnePercentLow?.ToString("0.0", inv) ?? string.Empty,
                s.PointOnePercentLow?.ToString("0.0", inv) ?? string.Empty,
                s.JitterMs?.ToString("0.00", inv) ?? string.Empty,
                s.Stutters?.ToString(inv) ?? string.Empty,
                s.EnergyWh?.ToString("0", inv) ?? string.Empty,
            };
            if (costed)
                row.Add(s.EnergyWh is double wh ? (wh / 1000 * _energyPrice).ToString("0.00", inv) : string.Empty);

            // A session logged before a row existed leaves its cell empty.
            foreach (var (label, unit) in peaks)
                row.Add(s.Peaks.FirstOrDefault(p => p.Label == label && p.Unit == unit)?.Value ?? string.Empty);

            row.Add(s.Driver ?? string.Empty);
            row.Add(s.WindowsBuild ?? string.Empty);
            row.Add(s.Note ?? string.Empty);
            csv.AppendLine(string.Join(",", row.Select(Cell)));
        }

        return csv.ToString();
    }

    /// <summary>
    /// Quoted only when it has to be: a note can hold a comma, and a quote in
    /// it is doubled, as CSV wants. A cell starting with = + - or @ gets a
    /// leading apostrophe, so a spreadsheet shows a game's note as text rather
    /// than running it as a formula.
    /// </summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            value = "'" + value;

        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>Hidden rather than closed, so it can be shown again later.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacementNow();

        if (_allowClose)
            return;

        e.Cancel = true;
        HideSmoothly();   // banner put out on screen first - see PanelWindow
    }

    public void AllowClose() => _allowClose = true;

    private void QueuePlacementSave()
    {
        _placementSave.Stop();
        _placementSave.Start();
    }

    private void SavePlacementNow()
    {
        if (!_placementSave.IsEnabled)
            return;

        _placementSave.Stop();

        if (WindowState != WindowState.Normal || !IsVisible)
            return;

        _settings.SessionsLeft = Left;
        _settings.SessionsTop = Top;
        _settings.SessionsWidth = Width;
        _settings.SessionsHeight = Height;
        _settings.Save();
    }

    private void ApplyTheme()
    {
        Panel.Background = _theme.NotesBackground;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        HeaderRule.Background = _theme.Separator;
        TableRule.Background = _theme.Separator;

        // Dim and as small as the notes' status line: a hint, not content.
        PickHint.Foreground = _theme.Header;
        PickHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);

        // Over the picture, inside its fade: the window colour, strongest on
        // the left where the cards and text are and gone by two thirds of the
        // way across - so the art, and the game's logo at the right, keep
        // their colour, as Steam's library page does it.
        var scrim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(0xD0, _theme.WindowColor.R, _theme.WindowColor.G, _theme.WindowColor.B), 0));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, _theme.WindowColor.R, _theme.WindowColor.G, _theme.WindowColor.B), 0.4));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, _theme.WindowColor.R, _theme.WindowColor.G, _theme.WindowColor.B), 0.7));
        scrim.Freeze();
        double inner = Math.Max(0, _theme.Corner.TopLeft - 1);   // inside the panel's 1 px border
        Backdrop.CornerRadius = new CornerRadius(inner, inner, 0, 0);
        Backdrop.Child = new Border { Background = scrim, CornerRadius = Backdrop.CornerRadius };

        // And a halo round the lines that sit on the picture without a card.
        SummaryText.Effect = _theme.TextHalo;
        ChartTitle.Effect = _theme.TextHalo;

        TitleText.Foreground = _theme.Section;
        TitleText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);

        // A heading, so styled as the cards' captions are: the section colour,
        // small capitals spaced a little - it has to read as over the chips.
        ChartTitleText.Foreground = _theme.Section;
        ChartTitleText.FontSize = Math.Max(8, SmallFont - 1);
        ChartTitleText.FontWeight = FontWeights.SemiBold;
        // The same heading over the last session's chips.
        LastSessionTitle.Foreground = ChartTitleText.Foreground;
        LastSessionTitle.FontSize = ChartTitleText.FontSize;
        LastSessionTitle.FontWeight = FontWeights.SemiBold;
        AverageSwatch.Background = AverageBrush;
        AverageSwatch.Opacity = AverageOpacity;
        HighestSwatch.Background = HighestBrush;
        LowestSwatch.Background = LowestBrush;
        StyleChip(AverageChip, AverageText, AverageBrush);
        StyleChip(HighestChip, HighestText, HighestBrush);
        StyleChip(LowestChip, LowestText, LowestBrush);
        GameName.Foreground = Brushes.White;   // over a picture, not the panel: as bright as it gets
        GameNameBar.Background = BarBrush;

        Foreground = _theme.NotesText;
        FontSize = UiTheme.PanelFontSize - 1;
        FontFamily = _theme.PanelFont;
    }
}
