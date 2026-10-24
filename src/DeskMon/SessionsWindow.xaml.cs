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

    /// <summary>The session clicked in the table - what Delete would delete.</summary>
    private SessionRecord? _selected;

    /// <summary>
    /// Delete asks twice: the first click arms it, a second within this time
    /// deletes. In the window itself rather than a Windows dialog, which would
    /// be the one white thing on a dark panel - and a deleted session is gone.
    /// </summary>
    private readonly DispatcherTimer _deleteArmed = new() { Interval = TimeSpan.FromSeconds(4) };

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
    private sealed record Column<T>(string Key, string Header, double Width, bool Number,
                                    Func<T, string> Text, Func<T, IComparable?> Sort,
                                    bool DescendingFirst = true,
                                    Func<T, PlaytimeSource>? Source = null,
                                    Func<T, int?>? Score = null);

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
        };

        foreach (var metric in MetricRegistry.Sections.SelectMany(s => s.Metrics))
        {
            // Copied out, so each lambda keeps its own row rather than the loop's last.
            string label = metric.Label, unit = metric.Unit;
            string key = PeakKey(label, unit);
            if (columns.Any(c => c.Key == key))
                continue;

            // "CLK MHz" needs the room "CPU %" does not.
            columns.Add(new(key, PeakHeader(label, unit), unit.Length > 2 ? 66 : 56, true,
                            s => s.Find(label, unit)?.Value ?? "–",
                            s => s.Find(label, unit)?.Number));
        }

        return columns.ToArray();
    }

    private static ChartMeasure[] BuildChartMeasures()
    {
        var measures = new List<ChartMeasure>
        {
            new("avg", "Average FPS", s => s.AverageFps),
            new("length", "Minutes played", s => s.Length.TotalMinutes),
            new("score", "Performance score", s => PerformanceScore.For(s)?.Score),
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
        _theme.Changed += () =>
        {
            ApplyTheme();
            if (!IsLoaded)
                return;
            if (_allGames)
                ShowAllGamesSummary();
            else
                ShowSummary(GamePicker.SelectedItem as string);
            ShowRows();
            DrawChart();
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

    private void FillPicker(object? wanted)
    {
        var games = _history.Games();

        // All games first, above the names, so it reads as a view of the list
        // rather than one more game in it.
        var items = new List<object>();
        if (games.Count > 0)
            items.Add(AllGames);
        items.AddRange(games);

        object? pick = wanted is AllGamesEntry && games.Count > 0
            ? AllGames
            : games.FirstOrDefault(g => string.Equals(g, wanted as string, StringComparison.OrdinalIgnoreCase))
              ?? MostRecent(games);

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
            ShowSelection(GamePicker.SelectedItem, _history.Games());
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

    private async void ShowSelection(object? item, IReadOnlyList<string> games)
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
                totals = await System.Threading.Tasks.Task.Run(() => games.Select(Total).OfType<GameTotal>().ToList());
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
            _games = Array.Empty<GameTotal>();
            _sessions = game is null ? Array.Empty<SessionRecord>() : _history.Sessions(game);
            ShowSummary(game);
        }

        // Kept across a refresh, found again by its line since the records
        // are new objects. Gone if it isn't there any more, so Delete can
        // never act on a session the table no longer shows.
        _selected = _selected is null ? null : _sessions.FirstOrDefault(s => s.Raw == _selected.Raw);
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

        SummaryText.Inlines.Clear();
        Stats.Children.Clear();

        if (game is null || _sessions.Count == 0)
        {
            AddSummary("No sessions yet. One is logged each time you quit a game after playing for a minute or more.");
            return;
        }

        var total = TimeSpan.FromTicks(_sessions.Sum(s => s.Length.Ticks));
        var first = _sessions[0].Start;
        var last = _sessions[^1].Start;

        // The whole playtime goes first, but is looked up on a worker and put
        // in front of the other cards when it arrives - see AddPlaytimeAsync.
        _ = AddPlaytimeAsync(game, total);

        AddStat("LOGGED", SessionRecord.Describe(total), "Time in the sessions DeskMon logged", PlaytimeSource.Logged);
        AddStat("SESSIONS", _sessions.Count.ToString(CultureInfo.InvariantCulture));

        if (WeightedFps(_sessions) is double fps)
            AddStat("AVERAGE", $"{fps:0} fps",
                    "Weighted by how long each session ran, so a few minutes in a menu don't outweigh an evening");

        if (PerformanceScore.ForGame(_sessions) is int score)
            AddStat("SCORE", $"{score}  {PerformanceScore.Grade(score)}",
                    $"Performance out of 100, from the last {PerformanceScore.GameSessions} sessions with a frame rate, " +
                    "weighted by how long each ran.\n" +
                    $"Full marks need {PerformanceScore.Target:0} fps - the target frame rate in Settings.\n" +
                    "Point at a session for how its score was reached.",
                    valueBrush: ScoreBrush(score));

        AddSummary($"First played {first.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}  ·  " +
                   $"last {last.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}");
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
    private void AddStat(string name, string value, string? tooltip = null, PlaytimeSource? source = null,
                         Brush? valueBrush = null, int? at = null)
    {
        FrameworkElement? icon = null;
        if (source is { } from)
        {
            icon = PlaytimeIcons.Make(from, _theme.Label);
            icon.Margin = new Thickness(0, 0, 5, 0);
        }

        // Shared with the pictures window's cards - see StatCard.
        var card = StatCard.Make(_theme, name, value, tooltip, icon, valueBrush);
        if (at is int index)
            Stats.Children.Insert(Math.Clamp(index, 0, Stats.Children.Count), card);
        else
            Stats.Children.Add(card);
    }

    /// <summary>
    /// Time across every game, and the one it mostly went to. No average frame
    /// rate: one number over games that run at 60 and at 400 describes none of them.
    /// </summary>
    private void ShowAllGamesSummary()
    {
        SummaryText.Inlines.Clear();
        Stats.Children.Clear();

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

        if (playtime > total)
            AddStat("PLAYTIME", GamePlaytime.Describe(playtime), "Every game's whole playtime, from Steam, GOG Galaxy or their saves where known");

        AddStat("LOGGED", SessionRecord.Describe(total), "Time in the sessions DeskMon logged");
        AddStat("SESSIONS", sessions.ToString(CultureInfo.InvariantCulture));
        AddStat("GAMES", _games.Count.ToString(CultureInfo.InvariantCulture));

        AddSummary("Most played ");
        AddSummary(most.Name, bright: true);
        AddSummary($" ({GamePlaytime.Describe(most.Playtime.Time)})  ·  last played ");
        AddSummary(latest.Name, bright: true);
    }

    private void AddSummary(string text, bool bright = false)
        => SummaryText.Inlines.Add(new System.Windows.Documents.Run(text)
        {
            Foreground = bright ? _theme.NotesText : _theme.Label,
            FontWeight = bright ? FontWeights.SemiBold : FontWeights.Normal,
        });

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
    private void DrawChart()
    {
        Chart.Children.Clear();

        // Shown whenever there are sessions, even if none recorded the picked
        // reading - hiding the picker along with the bars would leave no way
        // to pick something else.
        bool any = !_allGames && _sessions.Count > 0;
        ChartTitle.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Chart.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        double width = Chart.ActualWidth;
        double height = Chart.ActualHeight;
        if (!any || width <= 0 || height <= 0)
            return;

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

        HighestSwatch.Visibility = anyBar ? Visibility.Visible : Visibility.Collapsed;
        HighestText.Visibility = HighestSwatch.Visibility;
        LowestSwatch.Visibility = markLowest ? Visibility.Visible : Visibility.Collapsed;
        LowestText.Visibility = LowestSwatch.Visibility;
        AverageSwatch.Visibility = HighestSwatch.Visibility;
        AverageText.Visibility = HighestSwatch.Visibility;

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
            AverageText.Text = "average " + BarValue(average);
            HighestText.Text = "highest " + BarValue(values.Max());
            LowestText.Text = "lowest " + BarValue(lowest);
        }

        if (!anyBar)
        {
            var none = new TextBlock
            {
                Text = "Not recorded in these sessions",
                Foreground = _theme.Unavailable,
                FontSize = SmallFont,
                Width = width,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetTop(none, height / 2 - 8);
            Chart.Children.Add(none);
            return;
        }

        const double AxisWidth = 30;   // room for the value labels
        const double LabelRoom = 16;   // a bar's value label above the tallest bar
        const double Gap = 2;          // surface gap between bars

        double highest = shown.Max(s => measure.Value(s) ?? 0);
        double top = NiceCeiling(highest);
        double plotLeft = AxisWidth;
        double plotWidth = width - AxisWidth;
        double plotTop = LabelRoom;
        // Room under the baseline for the lower half of the "0" label, which is
        // centred on its gridline like the others. With only 1 px there, the
        // chart's clipping cut the 0 off at the bottom.
        const double ZeroRoom = 8;
        double plotHeight = height - LabelRoom - ZeroRoom;
        double baseline = plotTop + plotHeight;

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
        // Bars take 60% of their slot, so the gaps between sessions stay
        // visible, and stop at 24 px for the same reason.
        double slot = plotWidth / Math.Max(shown.Count, 8);
        double barWidth = Math.Min(24, Math.Max(2, Math.Min(slot * 0.6, slot - Gap)));

        // Every bar's value above it when the slots are wide enough for the
        // widest of them - a few sessions, or a wide window. Squeezed, only the
        // highest and lowest keep theirs: forty numbers run together read as
        // none, and the rest are a hover away in the tooltip.
        double labelSize = Math.Max(8, SmallFont - 1);
        bool labelAll = values.Max(v => ValueWidth(BarValue(v), labelSize)) <= slot - 2;

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
                var (normal, lit) = isHighest ? (HighestBrush, HighestHover)
                                  : isLowest ? (LowestBrush, LowestHover)
                                  : PlainBar(barHeight, plotHeight);

                // Rounded at the data end only; the foot sits square on the baseline.
                var bar = new Border
                {
                    Width = barWidth,
                    Height = barHeight,
                    Background = normal,
                    CornerRadius = new CornerRadius(Math.Min(3, barWidth / 2), Math.Min(3, barWidth / 2), 0, 0),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, baseline - barHeight);
                Chart.Children.Add(bar);

                if (labelAll || isHighest || isLowest)
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
                    Chart.Children.Add(label);
                }

                // Not "Highest {name}": the peak measures are already named
                // "Highest CPU °C", and it would say it twice. In the bar's
                // own colour, so the tooltip says which mark it is.
                if (isHighest)
                    (headline, headlineBrush) = ($"Highest in the chart: {value:0}", HighestBrush);
                else if (isLowest)
                    (headline, headlineBrush) = ($"Lowest in the chart: {value:0}", LowestBrush);

                hover.MouseEnter += (_, _) => bar.Background = lit;
                hover.MouseLeave += (_, _) => bar.Background = normal;
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
        });
    }

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

        // In the bar's own pixels: its foot is at barHeight, the plot's top
        // plotHeight above that.
        var foot = new Point(0, barHeight);
        var top = new Point(0, barHeight - plotHeight);
        Color start = _theme.BarGradientStart, end = _theme.BarGradientEnd;
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
            ShowSelection(GamePicker.SelectedItem, _history.Games());
        }
        else
            DrawChart();
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

    private IReadOnlyList<Column<SessionRecord>> VisibleSessionColumns()
        => AllSessionColumns.Where(c => AlwaysShown(c.Key) || _settings.SessionColumns.Contains(c.Key)).ToList();

    private void ShowRows()
    {
        DeleteButton.IsEnabled = !_allGames && _selected is not null;

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
                      s => SessionTip(s), mark: s => s.CutShort ? "cut short" : null,
                      open: Select, selected: s => ReferenceEquals(s, _selected));
    }

    /// <summary>A click picks the session for Delete; a click on the picked one lets it go again.</summary>
    private void Select(SessionRecord session)
    {
        _selected = ReferenceEquals(session, _selected) ? null : session;
        DisarmDelete();
        ShowRows();
    }

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
                              Func<T, object> tooltip, Func<T, string?>? mark, Action<T>? open,
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

        Rows.Children.Clear();
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

            if (mark?.Invoke(item) is { } note)
            {
                var text = new TextBlock
                {
                    Text = note,
                    Foreground = _theme.Warn,
                    FontSize = SmallFont,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(text, columns.Count);
                row.Children.Add(text);
            }

            // Hover like the other lists, so the tooltip reads as belonging to the row.
            row.MouseEnter += (_, _) => row.Background = _theme.BarTrack;
            row.MouseLeave += (_, _) => row.Background = rest;
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
        AddColumns(TableHeader, columns);

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
            TableHeader.Children.Add(heading);
        }
    }

    /// <summary>The fixed columns, then one that takes the rest - for the "cut short" mark.</summary>
    private static void AddColumns<T>(Grid grid, IReadOnlyList<Column<T>> columns)
    {
        foreach (var column in columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

        foreach (var column in AllSessionColumns.Where(c => !AlwaysShown(c.Key)))
        {
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

        double needed = VisibleSessionColumns().Sum(c => c.Width) + Around;
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
        if (_selected is null || GamePicker.SelectedItem is not string game)
            return;

        if (!_deleteArmed.IsEnabled)
        {
            // In the warning colour, and said in full, so the second click
            // is a decision and not a double-click that happened to land.
            DeleteText.Text = $"CLICK AGAIN TO DELETE ID {_selected.Id}";
            DeleteButton.Foreground = _theme.Warn;
            _deleteArmed.Start();
            return;
        }

        DisarmDelete();

        if (_history.Delete(game, _selected) is { } error)
        {
            AddSummary(SummaryText.Inlines.Count > 0
                ? $"\nCouldn't delete the session: {error}"
                : $"Couldn't delete the session: {error}");
            SummaryText.Inlines.LastInline.Foreground = _theme.Warn;
            return;
        }

        _selected = null;
        FillPicker(game);
        SessionDeleted?.Invoke();
    }

    private void DisarmDelete()
    {
        _deleteArmed.Stop();
        DeleteText.Text = _selected is null ? "DELETE" : $"DELETE: SESSION ID {_selected.Id}";
        DeleteButton.ClearValue(ForegroundProperty);
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
        var header = new List<string> { "ID", "Date", "Start", "End", "Length (min)", "Avg FPS", "1% low", "0.1% low", "Jitter (ms)" };
        header.AddRange(peaks.Select(p => p.Unit.Length > 0 ? $"{p.Label} {p.Unit}" : p.Label));
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
            };

            // A session logged before a row existed leaves its cell empty.
            foreach (var (label, unit) in peaks)
                row.Add(s.Peaks.FirstOrDefault(p => p.Label == label && p.Unit == unit)?.Value ?? string.Empty);

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

        PickerLabel.Foreground = _theme.Label;
        ChartTitleText.Foreground = _theme.Label;
        ChartTitleText.FontSize = SmallFont;
        AverageSwatch.Background = AverageBrush;
        AverageText.Foreground = _theme.Label;
        AverageText.FontSize = SmallFont;
        HighestSwatch.Background = HighestBrush;
        HighestText.Foreground = _theme.Label;
        HighestText.FontSize = SmallFont;
        LowestSwatch.Background = LowestBrush;
        LowestText.Foreground = _theme.Label;
        LowestText.FontSize = SmallFont;
        GameName.Foreground = Brushes.White;   // over a picture, not the panel: as bright as it gets
        GameNameBar.Background = BarBrush;

        Foreground = _theme.NotesText;
        FontSize = UiTheme.PanelFontSize - 1;
        FontFamily = _theme.PanelFont;
    }
}
