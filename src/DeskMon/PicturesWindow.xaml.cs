using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMon.Notes;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// The open game's screenshots, all together: to look through, open full
/// size, and delete.
///
/// It exists because deleting a picture in the notes only takes it out of the
/// text - the full-size file stays in notes\screenshots\Game, unseen, taking
/// a few MB each. Deleting it automatically along with the text would be
/// worse: Ctrl+Z, or cutting a picture to paste it lower down, would bring
/// back a picture whose file had already gone. So the files are dealt with
/// here, where you can see them, and each says whether the notes still use it.
///
/// One game at a time, the one the notes window has open - the folders are
/// per game, so Witcher 3's pictures never show under Last Epoch. Deleting
/// sends the file to the Recycle Bin, and takes the picture out of the notes
/// as well if they still show it.
/// </summary>
public partial class PicturesWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly NotesWindow _notes;

    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _allowClose;

    /// <summary>
    /// A delete asks twice, the way the session history's does: the first
    /// click arms the button, a second within this time deletes - in the
    /// window rather than a white Windows dialog on a dark panel.
    /// </summary>
    private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(4) };
    private Action? _undoArm;

    /// <summary>
    /// Thumbnail width on screen; a tile is this plus its padding. Set by
    /// <see cref="FitTiles"/> so the tiles fill each row - fixed, they left a
    /// strip of empty space down the right whenever the window wasn't an
    /// exact multiple of them wide.
    /// </summary>
    private double _thumbWidth = MinThumbWidth;

    /// <summary>The narrowest a thumbnail may get before a row drops to one column fewer.</summary>
    private const double MinThumbWidth = 160;

    private const double TilePadding = 6, TileGap = 8;

    /// <summary>16:9, the shape of nearly every screen - the shape of nearly every screenshot.</summary>
    private double ThumbHeight => Math.Round(_thumbWidth * 9 / 16);

    /// <summary>
    /// Decoded this wide whatever the tile's size, so widening the window
    /// doesn't need every thumbnail decoded again - and sharp up to about
    /// 320 px wide on a 1.5x screen.
    /// </summary>
    private const int ThumbPixels = 480;

    /// <summary>
    /// Decoded thumbnails, kept for as long as the file is unchanged - so
    /// switching games and back, scrolling back up, or the refresh after a
    /// delete doesn't decode the same 4K images again.
    ///
    /// Only the <see cref="ThumbsKept"/> most recently used: each is about
    /// half a megabyte, and kept for ever a game with 500 screenshots would
    /// have held a quarter of a gigabyte for a window opened once.
    /// </summary>
    private static readonly Dictionary<string, (DateTime Written, BitmapSource Image, long Used)> Thumbs =
        new(StringComparer.OrdinalIgnoreCase);

    private const int ThumbsKept = 100;
    private static long _thumbUse;

    /// <summary>
    /// How many screenshots one set of notes can hold before saving them gets
    /// slow enough to notice. Every picture's preview is stored inside the
    /// notes file and the file is written each time typing pauses; measured,
    /// five came to 2 MB and 25 ms a save, so twenty is around 100 ms - a
    /// pause you start to feel after a sentence.
    /// </summary>
    private const int ManyInNotes = 20;

    /// <summary>What a tile needs to have its thumbnail loaded and let go as it scrolls in and out of view.</summary>
    private sealed class TileParts
    {
        /// <summary>The thumbnail's area - the picture, its badge and hover outline - sized by FitTiles.</summary>
        public required FrameworkElement Frame { get; init; }
        public required Image Image { get; init; }
        public required string Path { get; init; }
        public bool Loading { get; set; }
    }

    // The panel's own shade, so the cards show on a light theme as on a dark one.
    private Brush CardBrush => _theme.Shade(0x0E);

    /// <summary>The card a shade lighter under the mouse, so the tile being pointed at stands out from its row.</summary>
    private Brush CardHoverBrush => _theme.Shade(0x1C);

    private Brush PlaceholderBrush => _theme.Shade(0x10);

    /// <summary>
    /// The accent the widget's toolbar lights up in, round the thumbnail under
    /// the mouse: that is the one a click will open.
    /// </summary>
    private Brush HoverOutlineBrush => _theme.Accent;

    /// <summary>Dark and nearly opaque, so a badge on the picture reads on a bright sky as well as a dark cave.</summary>
    private static readonly Brush BadgeBrush = Frozen(Color.FromArgb(0xD9, 0x16, 0x19, 0x1F));

    private const double ThumbCorner = 4;

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>The drawn photos behind the header - its own, not the notes' game picture.</summary>
    private readonly Banner _banner;

    public PicturesWindow(AppSettings settings, UiTheme theme, NotesWindow notes)
    {
        _settings = settings;
        _theme = theme;
        _notes = notes;

        InitializeComponent();
        // A drawing of its own - photos, as the notes' is a page and a pencil -
        // rather than the notes' game picture: docked side by side, the same
        // picture behind both read as one window split in two.
        _banner = new Banner(Panel, height: 200);
        _banner.Show(GameArt.Motif.Photos);
        ApplyTheme();

        // The tiles are drawn in the theme's colours, so they are drawn again -
        // and it docks afresh, since the glow's room changes the gap it needs.
        _theme.Changed += () =>
        {
            ApplyTheme();
            if (IsVisible)
            {
                Refresh();
                Dock();
            }
        };
        RestorePlacement(_settings.PicturesLeft, _settings.PicturesTop,
                         _settings.PicturesWidth, _settings.PicturesHeight);
        SetUpSortPicker();

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.NotesOpacity);

        // Read again whenever it could be out of date: other notes loaded, a
        // screenshot taken, or coming back to this window - a picture may have
        // been deleted from the notes since, which changes what is unused.
        _notes.CurrentGameChanged += RefreshIfShown;
        _notes.PicturesChanged += RefreshIfShown;
        Activated += (_, _) => Refresh();

        // The viewport rather than the window: it narrows when the scroll bar
        // appears, which would otherwise push the last tile in a row onto the next.
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0)
                FitTiles();

            // Scrolled, resized, or tiles moved by a refit: other tiles are near the view now.
            LoadVisibleSoon();
        };

        // Put away, it lets go of every thumbnail - the cache keeps the most
        // recent, so opening it again is still quick.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                Tiles.Children.Clear();
                _shown = null;
                EmptyState.Visibility = Visibility.Collapsed;   // so it fades in again on the next open
            }
        };

        // Put out on screen before the window hides, like the banner: Windows
        // shows the last frame again the instant it reopens, and with the
        // picture still in it, it flashed up fully before fading in.
        Hiding += () =>
        {
            EmptyState.BeginAnimation(OpacityProperty, null);
            EmptyState.Opacity = 0;
        };

        _disarm.Tick += (_, _) => Disarm();
        _placementSave.Tick += (_, _) => SavePlacementNow();
        Closing += OnClosing;
        LocationChanged += (_, _) => { PushToNotes(); QueuePlacementSave(); };
        SizeChanged += (_, _) => { PushToNotes(); QueuePlacementSave(); };

        // Docked to the notes: wherever they go, it goes - see Dock.
        _notes.LocationChanged += (_, _) => Dock();
        _notes.SizeChanged += (_, _) => Dock();
        _notes.StateChanged += (_, _) => Dock();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _sideChosen = false;   // pick the side afresh each time it opens
                Dock();
            }
        };
    }

    // --- Docked to the notes -------------------------------------------------
    //
    // The pictures belong to the notes - they are the open game's, and close
    // with the notepad - so the window sits against the notepad's side, as tall
    // as it, rather than wherever it was last left. Moving either moves both,
    // and resizing either's height resizes both; its outer edge still sets its
    // own width.

    /// <summary>The space between the two panels, so they read as a pair rather than one frame with a seam.</summary>
    private const double DockGap = 6;

    /// <summary>
    /// The space between the two windows that leaves <see cref="DockGap"/>
    /// between their panels. With the theme's glow on, each panel sits in from
    /// its window's edge to leave the glow room (UiTheme.GlowRoom), which
    /// pushed the panels 22 px apart; the windows overlap by that room instead,
    /// glow over glow, and the panels stay where they were.
    /// </summary>
    private double WindowGap => DockGap - _theme.GlowRoom.Left - _theme.GlowRoom.Right;

    /// <summary>
    /// Somewhere it should not dock over - the widget, set by it. Null, or a
    /// null answer, when there is nothing to keep clear of.
    /// </summary>
    public Func<Rect?>? KeepClearOf { get; set; }

    /// <summary>Overlapping by more than an edge: two windows side by side don't count.</summary>
    private static bool Overlaps(Rect a, Rect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>
    /// Which side of the notes it is on. Kept rather than worked out on every
    /// move, so dragging the notes across the screen doesn't flip it from side
    /// to side - it changes only when there is no longer room where it is.
    /// </summary>
    private bool _onLeft;
    private bool _sideChosen;

    /// <summary>This window is placing itself, so its own move and resize are not the user's.</summary>
    private bool _docking;

    /// <summary>
    /// The user is resizing this window by an edge. Its height and top go
    /// across to the notes as it changes, and following the notes back waits
    /// until the resize is over - the two would otherwise fight over the edge.
    /// </summary>
    private bool _resizing;

    /// <summary>Puts it against the notes' side, as tall as them. Nothing while either is hidden, maximised or minimised.</summary>
    private void Dock()
    {
        if (_resizing || !IsVisible || !_notes.IsVisible
            || _notes.WindowState != WindowState.Normal || WindowState != WindowState.Normal)
            return;

        var area = WorkAreaOf(_notes);
        double width = Width;
        double notesLeft = _notes.Left, notesWidth = _notes.ActualWidth;
        double gap = WindowGap;

        var right = new Rect(notesLeft + notesWidth + gap, _notes.Top, width, _notes.ActualHeight);
        var left = new Rect(notesLeft - gap - width, _notes.Top, width, _notes.ActualHeight);

        // Best: on the screen and clear of the widget. Next: on the screen.
        // Docked on the widget, it covered it - both are always on top, so
        // whichever was clicked last won, and the widget seemed to vanish
        // behind "always on top".
        int Score(Rect place)
        {
            bool onScreen = place.Left >= area.Left && place.Right <= area.Right;
            bool clear = KeepClearOf?.Invoke() is not { } avoid || !Overlaps(avoid, place);
            return onScreen ? (clear ? 2 : 1) : 0;
        }

        int rightScore = Score(right), leftScore = Score(left);

        if (!_sideChosen)
        {
            _onLeft = leftScore > rightScore;   // the right, unless the left is better
            _sideChosen = true;
        }
        else if (_onLeft ? rightScore > leftScore : leftScore > rightScore)
        {
            _onLeft = !_onLeft;   // only for a better side, so it doesn't flip back and forth
        }

        _docking = true;
        try
        {
            Left = _onLeft ? notesLeft - gap - width : notesLeft + notesWidth + gap;
            Top = _notes.Top;
            Height = _notes.ActualHeight;
        }
        finally
        {
            _docking = false;
        }
    }

    /// <summary>
    /// The user changed this window's top or height - by its top or bottom
    /// edge - so the notes take the same, and the pair stays level.
    ///
    /// Compared before it is set, because the size change from Dock arrives a
    /// layout pass after Dock has finished, looking like the user's: by then
    /// it matches the notes, so it does nothing, and nothing goes back and forth.
    /// </summary>
    private void PushToNotes()
    {
        if (_docking || !IsVisible || !_notes.IsVisible
            || _notes.WindowState != WindowState.Normal || WindowState != WindowState.Normal)
            return;

        if (Math.Abs(_notes.Top - Top) > 0.5)
            _notes.Top = Top;

        // ActualHeight, not Height: Height can be unset (NaN), and NaN
        // compared with anything would never call for a change.
        if (Math.Abs(_notes.ActualHeight - ActualHeight) > 0.5)
            _notes.Height = ActualHeight;
    }

    /// <summary>
    /// The edge against the notes isn't one: dragging it would pull the two
    /// apart. The corners on that side become plain top and bottom edges.
    /// </summary>
    protected override int AdjustResizeEdge(int edge) => (_onLeft, edge) switch
    {
        (false, HTLEFT) => 0,
        (false, HTTOPLEFT) => HTTOP,
        (false, HTBOTTOMLEFT) => HTBOTTOM,
        (true, HTRIGHT) => 0,
        (true, HTTOPRIGHT) => HTTOP,
        (true, HTBOTTOMRIGHT) => HTBOTTOM,
        _ => edge,
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // When a resize by an edge starts and ends. Moving this window never
        // goes through here - its header moves the notes instead.
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_ENTERSIZEMOVE)
                {
                    _resizing = true;
                }
                else if (msg == WM_EXITSIZEMOVE)
                {
                    _resizing = false;
                    PushToNotes();
                    Dock();   // squared up against the notes once the edge is let go
                }

                return IntPtr.Zero;
            });
    }

    private const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

    /// <summary>
    /// The work area of the monitor the window is on, in WPF units - what the
    /// pictures must fit beside the notes within.
    /// </summary>
    private static Rect WorkAreaOf(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);

        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                        area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    public void ShowAndFocus()
    {
        Refresh();

        if (!IsOpen)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Dock();   // against the notes, wherever it was left last time

        Topmost = _settings.NotesAlwaysOnTop;
        Activate();
    }

    private void RefreshIfShown()
    {
        if (IsVisible)
            Refresh();
    }

    // --- Listing -----------------------------------------------------------

    /// <param name="Favourite">Starred - see PictureFavourites.</param>
    private sealed record Picture(string Path, string Name, DateTime Taken, long Bytes, bool InNotes, bool Favourite);

    /// <summary>The open game's pictures, in the order picked above them, each marked with whether the notes use it.</summary>
    private List<Picture> Read()
    {
        string folder = _notes.PictureFolder;
        if (!Directory.Exists(folder))
            return new List<Picture>();

        var inUse = _notes.PicturesInUse();
        var favourites = PictureFavourites.Load(folder);

        var pictures = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            .Select(f => new Picture(f.FullName, f.Name,
                                     NotesPictures.TimeFromFileName(f.FullName) ?? f.LastWriteTime,
                                     f.Length, inUse.Contains(f.Name), favourites.Contains(f.Name)));

        return Sorted(pictures).ToList();
    }

    /// <summary>The pictures as last listed, in their order - what the viewer steps through.</summary>
    private List<Picture> _listed = new();

    // --- Order ---------------------------------------------------------------

    /// <param name="Key">What PicturesSort holds.</param>
    /// <param name="Label">What the dropdown shows - ToString is what it draws.</param>
    private sealed record SortChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly SortChoice[] SortChoices =
    {
        new("newest", "Newest first"),
        new("oldest", "Oldest first"),
        new("largest", "Largest first"),
        new("smallest", "Smallest first"),
        new("unused", "Not in notes first"),
        new("favourites", "Favourites first"),
    };

    /// <summary>
    /// The pictures in the order picked. Anything that ties - two the same
    /// size, or every picture still in the notes - goes newest first, so the
    /// order is the same every time rather than whatever the folder listing
    /// happened to return.
    /// </summary>
    private IEnumerable<Picture> Sorted(IEnumerable<Picture> pictures) => _settings.PicturesSort switch
    {
        "oldest" => pictures.OrderBy(p => p.Taken),
        "largest" => pictures.OrderByDescending(p => p.Bytes).ThenByDescending(p => p.Taken),
        "smallest" => pictures.OrderBy(p => p.Bytes).ThenByDescending(p => p.Taken),
        "unused" => pictures.OrderBy(p => p.InNotes).ThenByDescending(p => p.Taken),
        "favourites" => pictures.OrderByDescending(p => p.Favourite).ThenByDescending(p => p.Taken),
        _ => pictures.OrderByDescending(p => p.Taken),
    };

    /// <summary>
    /// Fills the dropdown and selects the saved order before listening to it,
    /// so selecting it here isn't taken as the user picking it.
    /// </summary>
    private void SetUpSortPicker()
    {
        foreach (var choice in SortChoices)
            SortPicker.Items.Add(choice);

        SortPicker.SelectedItem = SortChoices.FirstOrDefault(c => c.Key == _settings.PicturesSort) ?? SortChoices[0];

        SortPicker.SelectionChanged += (_, _) =>
        {
            if (SortPicker.SelectedItem is not SortChoice choice || choice.Key == _settings.PicturesSort)
                return;

            _settings.PicturesSort = choice.Key;
            _settings.Save();

            // The same pictures in a new order look unchanged to Refresh's
            // check, which compares what is there, not where it is.
            _shown = null;
            Refresh();
            Scroller.ScrollToTop();   // the start of the new order, not the middle of the old one
        };
    }

    /// <summary>What the tiles were last built from, so a refresh that finds nothing new leaves them be.</summary>
    private string? _shown;

    /// <summary>
    /// Rebuilds the tiles - but only when something changed. This runs every
    /// time the window is activated, and the click that activates it is
    /// usually aimed at a tile: rebuilding them under the mouse between the
    /// press and the release would swallow that click, and blink the window.
    /// </summary>
    private void Refresh()
    {
        List<Picture> pictures;
        try
        {
            pictures = Read();
        }
        catch (Exception ex)
        {
            _shown = null;
            Tiles.Children.Clear();
            ShowEmpty(false);   // not "no pictures": the folder couldn't be read, which the summary says
            ShowSummary(Array.Empty<Picture>(), "Couldn't read the pictures folder: " + ex.Message);
            return;
        }

        // Pictures the notes show with no file behind them - deleted, and gone
        // from the Recycle Bin too. They can't be tiles, having no file, so
        // they are counted on a card instead of passing unnoticed. By the
        // file each one links to, not by name in this folder: one copied in
        // from another game's notes lives in that game's folder, and isn't
        // missing at all.
        var pathsInNotes = _notes.PicturePathsInNotes();
        int inNotes = pathsInNotes.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int missing = pathsInNotes.Count(path => !File.Exists(path));

        string shown = $"{_notes.CurrentGame}|{inNotes}|{missing}|" + string.Join("|", pictures.Select(p => $"{p.Name}:{p.Bytes}:{p.InNotes}:{p.Favourite}"));
        if (shown == _shown)
            return;
        _shown = shown;
        _listed = pictures;

        Disarm();
        Tiles.Children.Clear();
        foreach (var picture in pictures)
            Tiles.Children.Add(Tile(picture));
        FitTiles();
        LoadVisibleSoon();
        ShowEmpty(pictures.Count == 0);

        ShowSummary(pictures, null, missing);
        WarnIfManyInNotes(inNotes);

        var unused = pictures.Where(p => !p.InNotes).ToList();
        CleanButton.Visibility = unused.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanText.Text = $"DELETE UNUSED ({unused.Count})";
        CleanButton.ToolTip = "Delete every picture that is no longer in any notes\nThey go to the Recycle Bin.";
        CleanButton.Tag = unused;
    }

    /// <summary>
    /// The cards along the top - NOTES, PICTURES, SIZE, and NOT IN NOTES when
    /// some are unused - drawn like the session history's (see StatCard), so
    /// the two windows read alike. They were two lines of text, the game's
    /// name above "12 pictures · 48.2 MB · 3 not in notes", which had to be
    /// read through to find a number.
    ///
    /// The unused count is in the warning colour with its explanation on
    /// hover; each such picture is marked on its tile too. A problem replaces
    /// the cards with what went wrong, in the line under them.
    /// </summary>
    /// <param name="missing">Pictures the notes show whose files are gone - see Refresh.</param>
    private void ShowSummary(IReadOnlyList<Picture> pictures, string? problem, int missing = 0)
    {
        SummaryText.Inlines.Clear();
        Stats.Children.Clear();

        if (problem is not null)
        {
            SummaryText.Inlines.Add(new System.Windows.Documents.Run(problem) { Foreground = _theme.Warn });
            ShowSummaryLine();
            return;
        }

        Stats.Children.Add(StatCard.Make(_theme, "NOTES", _notes.CurrentGame,
                                         "Whose pictures these are - the notes open in the notepad"));

        // Even at none: the empty picture in the middle of the window says
        // how to take one, and the count still belongs with the other cards.
        Stats.Children.Add(StatCard.Make(_theme, "PICTURES", pictures.Count.ToString(CultureInfo.InvariantCulture)));

        // Next to PICTURES, which it explains: the notes show more than are
        // listed. In the critical colour - unlike NOT IN NOTES, these can't be
        // put right from here.
        if (missing > 0)
            Stats.Children.Add(StatCard.Make(_theme, "MISSING", missing.ToString(CultureInfo.InvariantCulture),
                                             "In the notes, but the file is gone - deleted, and not in the Recycle Bin either\n" +
                                             "Only the small preview in the notes is left, so it can't be listed here.",
                                             valueBrush: _theme.Crit));

        if (pictures.Count > 0)
            Stats.Children.Add(StatCard.Make(_theme, "SIZE", Size(pictures.Sum(p => p.Bytes)),
                                             "The space the full-size files take on disk"));

        int unused = pictures.Count(p => !p.InNotes);
        if (unused > 0)
            Stats.Children.Add(StatCard.Make(_theme, "NOT IN NOTES", unused.ToString(CultureInfo.InvariantCulture),
                                             "Taken out of the notes, but the files are still here\n" +
                                             "ADD TO NOTES puts one back; DELETE UNUSED clears them all.",
                                             valueBrush: _theme.Warn));

        ShowSummaryLine();
    }

    /// <summary>
    /// The line under the cards only while it has something to say - a
    /// warning added after the cards, or a problem - so it leaves no gap.
    /// Called again by anything that adds to it.
    /// </summary>
    private void ShowSummaryLine()
        => SummaryText.Visibility = SummaryText.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The picture in the middle of an empty window, and what to do about it:
    /// "No pictures yet", whose they would be, and how to take one - with the
    /// hotkey as it is set, since that is the one that works in a game.
    ///
    /// It fades and rises into place as it appears, once: nothing about it
    /// moves after that, because this window can be open over a game.
    /// </summary>
    private void ShowEmpty(bool empty)
    {
        bool wasShown = EmptyState.Visibility == Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (!empty)
            return;

        EmptyText.Text = $"Screenshots you take of {_notes.CurrentGame} will show up here.";

        string hotkey = _settings.ScreenshotHotkey?.Trim() ?? "";
        EmptyHint.Text = hotkey.Length > 0
            ? $"Press {hotkey} in the game, or SCREENSHOT in the notes."
            : "Press SCREENSHOT in the notes to take one.";

        if (wasShown)
            return;   // already there, switching between two empty games: no need to arrive again

        // After a pause, not at once: the window's first frame - banner, tiles,
        // layout - takes long enough to swallow most of a fade that starts with
        // it, so it popped in half-way and jumped the rest. Waiting lets the
        // window settle first, so the whole rise is seen.
        var delay = TimeSpan.FromMilliseconds(250);
        var duration = new Duration(TimeSpan.FromMilliseconds(450));
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };

        // The starting place as the base value too: an animation waiting out
        // its BeginTime shows the base value, which would flash the picture
        // fully in place for the length of the pause.
        EmptyState.BeginAnimation(OpacityProperty, null);
        EmptyShift.BeginAnimation(TranslateTransform.YProperty, null);
        EmptyState.Opacity = 0;
        EmptyShift.Y = 16;

        EmptyState.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease });
        EmptyShift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(16, 0, duration) { BeginTime = delay, EasingFunction = ease });
    }

    /// <summary>
    /// Says so when the notes hold enough pictures to slow their saving - and
    /// why, and what helps, since nothing else would connect a pause after
    /// typing with the pictures. A suggestion, not a limit: the pictures are
    /// yours, and nothing is taken away.
    /// </summary>
    private void WarnIfManyInNotes(int inNotes)
    {
        if (inNotes < ManyInNotes)
            return;

        // A break only after something: the cards are above now, not a line of text.
        if (SummaryText.Inlines.Count > 0)
            SummaryText.Inlines.Add(new System.Windows.Documents.LineBreak());
        SummaryText.Inlines.Add(new System.Windows.Documents.Run(
            $"These notes show {inNotes} pictures, so saving can pause briefly after you type. " +
            "Taking out the ones you no longer need keeps it smooth.")
        {
            Foreground = _theme.Warn,
            ToolTip = "Each picture's preview is stored inside the notes file, which is written every time typing pauses.",
        });
        ShowSummaryLine();
    }

    // --- A tile ------------------------------------------------------------

    /// <summary>
    /// One picture: the thumbnail, which opens it full size; when it was taken
    /// and how big it is; whether the notes still show it; and its Delete -
    /// and, when they don't show it, a way to put it back.
    /// </summary>
    private UIElement Tile(Picture picture)
    {
        var tile = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(TilePadding),
            Margin = new Thickness(0, 0, TileGap, TileGap),
            Width = _thumbWidth + 2 * TilePadding,
        };
        tile.MouseEnter += (_, _) => tile.Background = CardHoverBrush;
        tile.MouseLeave += (_, _) => tile.Background = CardBrush;

        var stack = new StackPanel();
        tile.Child = stack;

        // The thumbnail, with a placeholder the thumbnail's size until it is
        // decoded, so the tiles don't jump about as the images arrive one by
        // one. Clipped to rounded corners - ClipToBounds alone cuts square,
        // and the outline drawn round it on hover is rounded.
        var image = new Image { Stretch = Stretch.UniformToFill };
        var picturePart = new Border { Background = PlaceholderBrush, Child = image };

        // Drawn over the picture while the mouse is on it: the thumbnail you
        // are pointing at is the one a click opens. Not hit-testable, so the
        // mouse passes through it to the picture underneath.
        var outline = new Border
        {
            BorderBrush = HoverOutlineBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(ThumbCorner),
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
        };

        var frame = new Grid
        {
            Width = _thumbWidth,
            Height = ThumbHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = $"{picture.Name}\nClick to view it; right-click to copy, star or mark it up.",
        };

        // The menu made when it is asked for, not with the tile: a rebuild
        // makes every tile again, and with a few hundred pictures their menus
        // were most of the time a star or a delete took.
        frame.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = PictureMenu(picture);
            menu.PlacementTarget = frame;
            menu.IsOpen = true;
        };
        frame.Children.Add(picturePart);
        if (!picture.InNotes)
            frame.Children.Add(NotInNotesBadge());
        frame.Children.Add(outline);

        // The star: always there on a favourite, on hover on the rest, so the
        // tiles stay quiet but starring one is a click away.
        var star = Star(picture);
        star.Visibility = picture.Favourite ? Visibility.Visible : Visibility.Hidden;
        frame.Children.Add(star);

        // The bin and DELETE in the bottom-right corner, always there - the
        // same badge the notes draw on their pictures (BinBadge).
        frame.Children.Add(DeleteButton(picture));

        frame.SizeChanged += (_, e) =>
            picturePart.Clip = new RectangleGeometry(new Rect(e.NewSize), ThumbCorner, ThumbCorner);
        frame.MouseEnter += (_, _) =>
        {
            outline.Visibility = Visibility.Visible;
            star.Visibility = Visibility.Visible;
        };
        frame.MouseLeave += (_, _) =>
        {
            outline.Visibility = Visibility.Hidden;
            if (!picture.Favourite)
                star.Visibility = Visibility.Hidden;
        };
        frame.MouseLeftButtonUp += (_, _) => View(picture);
        stack.Children.Add(frame);

        // For FitTiles, which resizes tiles in place, and LoadVisible, which
        // gives a tile its thumbnail only once it is near the view.
        tile.Tag = new TileParts { Frame = frame, Image = image, Path = picture.Path };

        // When, and how big. "In notes" is no longer spelled out on every
        // tile: it is the usual case, and the unusual one is badged on the
        // picture itself - as the bin is, in the opposite corner.
        var foot = new Grid { Margin = new Thickness(1, 6, 0, 0) };
        stack.Children.Add(foot);

        var about = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        about.Children.Add(new TextBlock
        {
            Text = picture.Taken.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture),
            Foreground = _theme.NotesText,
            FontSize = SmallFont + 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        about.Children.Add(new TextBlock
        {
            Text = Size(picture.Bytes),
            Foreground = _theme.Label,
            FontSize = SmallFont,
            Margin = new Thickness(0, 1, 0, 0),
        });
        foot.Children.Add(about);

        // A picture taken out of the notes can go back in. On a line of its
        // own rather than beside the bin: a tile is narrow, and the date would
        // be cut short to make room.
        if (!picture.InNotes)
            stack.Children.Add(AddBackButton(picture));

        return tile;
    }

    /// <summary>
    /// "NOT IN NOTES" in the picture's top-left corner, so the ones taken out
    /// of the notes can be picked out of a screenful at a glance - as a line
    /// of small text under each tile, every tile looked the same.
    /// </summary>
    private UIElement NotInNotesBadge() => new Border
    {
        Background = BadgeBrush,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(5, 1, 5, 2),
        Margin = new Thickness(6),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Child = new TextBlock
        {
            Text = "NOT IN NOTES",
            Foreground = _theme.Warn,
            FontSize = Math.Max(8, SmallFont - 1),
            FontWeight = FontWeights.SemiBold,
        },
    };

    /// <summary>
    /// ★ or ☆ in the picture's top-right corner, on the dark badge so it shows
    /// on any picture. A click stars it or takes the star away - and only
    /// that, not the view a click on the picture opens.
    /// </summary>
    private UIElement Star(Picture picture)
    {
        var star = new Border
        {
            Background = BadgeBrush,
            CornerRadius = new CornerRadius(11),
            Width = 22,
            Height = 22,
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = picture.Favourite ? "Take it out of favourites" : "Add it to favourites",
            Child = new TextBlock
            {
                Text = picture.Favourite ? "★" : "☆",
                Foreground = picture.Favourite ? _theme.Accent : Brushes.White,
                FontFamily = SymbolFont,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        star.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleFavourite(picture.Path);
        };
        return star;
    }

    private void ToggleFavourite(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        if (PictureFavourites.Toggle(_notes.PictureFolder, name) is { } error)
        {
            // Nothing changed, so nothing to rebuild - only the reason to give.
            Say($"Couldn't change the star on {name}: {error}");
            return;
        }

        _shown = null;   // rebuilt with the star where it now is
        Refresh();
    }

    /// <summary>
    /// Right-click on a picture: the viewer, the clipboard, a star, mark-up,
    /// and the two ways out to Windows - in the menu style every DeskMon menu has.
    /// </summary>
    private ContextMenu PictureMenu(Picture picture)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };

        void Add(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        Add("View", () => View(picture));
        Add("Copy picture", () =>
        {
            if (PictureClipboard.Copy(picture.Path) is { } error)
                Say($"Couldn't copy {picture.Name}: {error}");
        });
        Add(picture.Favourite ? "Take out of favourites" : "Add to favourites", () => ToggleFavourite(picture.Path));
        Add("Mark up…", () => View(picture, markUp: true));
        menu.Items.Add(new Separator());
        Add("Open in Photos app", () => Open(picture.Path));
        Add("Show in folder", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{picture.Path}\"") { UseShellExecute = true });
            }
            catch { }
        });

        return menu;
    }

    /// <summary>
    /// The picture full screen in DeskMon's own viewer, which steps through the
    /// rest in the order they are listed here - optionally straight into mark-up.
    /// </summary>
    private void View(Picture picture, bool markUp = false)
    {
        int index = _listed.FindIndex(p => string.Equals(p.Path, picture.Path, StringComparison.OrdinalIgnoreCase));
        string folder = _notes.PictureFolder;

        var viewer = new PictureViewer(
            _theme,
            _listed.Select(p => p.Path),
            Math.Max(0, index),
            this,
            isFavourite: path => PictureFavourites.Load(folder).Contains(System.IO.Path.GetFileName(path)),
            toggleFavourite: path => PictureFavourites.Toggle(folder, System.IO.Path.GetFileName(path)),
            changed: () =>
            {
                _shown = null;
                RefreshIfShown();
            },
            replaced: _notes.PictureReplaced,   // the notes' preview of it made again
            delete: path =>
            {
                // The notes delete it - out of them too if they show it - and
                // say PicturesChanged, which lists the pictures here again.
                Thumbs.Remove(path);
                _shown = null;
                return _notes.DeletePicture(path);
            });

        if (markUp)
            viewer.MarkUpOnOpen();
        viewer.Show();
    }

    /// <summary>
    /// A line under the cards, in the warning colour - for something that
    /// didn't work. A break only after something already there, and the line
    /// shown, since it is collapsed while empty - see ShowSummaryLine.
    /// </summary>
    private void Say(string text)
    {
        if (SummaryText.Inlines.Count > 0)
            SummaryText.Inlines.Add(new System.Windows.Documents.LineBreak());
        SummaryText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = _theme.Warn });
        ShowSummaryLine();
    }

    /// <summary>
    /// Puts a picture that is no longer in the notes back into them. The
    /// thumbnail still opens it full size, as on every tile, so a look at it
    /// first doesn't put it in by accident.
    /// </summary>
    private static readonly Geometry PlusIcon = FrozenGeometry("M5.5,1.5 V9.5 M1.5,5.5 H9.5");

    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    private static Geometry FrozenGeometry(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private Button AddBackButton(Picture picture)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Path
        {
            Style = (Style)FindResource("ToolIcon"),
            Data = PlusIcon,   // the plus from the notes' NEW
        });
        content.Children.Add(new TextBlock { Text = "ADD TO NOTES", VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Style = (Style)FindResource("ToolButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            // Pulled left by the button's padding, so the icon, not the hover
            // background, lines up with the date above.
            Margin = new Thickness(-5, 6, 0, 0),
            Content = content,
            ToolTip = "Add this picture back to the notes\nIt goes in at the end of them.",
        };

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;   // one click, one copy

            // The notes say PicturesChanged once it is in, and the refresh
            // that follows rebuilds this tile as "in notes", without the button.
            if (await _notes.AddPictureBack(picture.Path) is { } error)
            {
                button.IsEnabled = true;
                Say($"Couldn't add {picture.Name} back: {error}");   // shows the line, collapsed while empty
            }
        };

        return button;
    }

    /// <summary>
    /// The bin, as a badge in the thumbnail's bottom-right corner: the bin and
    /// DELETE on a dark plate, the same as the notes draw on their pictures
    /// (see BinBadge). Armed by a first click - DELETE? in the warning colour -
    /// and deleted by a second, so a stray click costs nothing. A button, so
    /// the click is its own and doesn't also open the picture underneath.
    /// </summary>
    private Button DeleteButton(Picture picture)
    {
        BareButton ??= SealedBareButton();

        var icon = new System.Windows.Shapes.Path
        {
            Data = BinBadge.Icon,
            Stroke = BinBadge.Ink,
            StrokeThickness = 1,
            Width = BinBadge.IconSize,
            Height = BinBadge.IconSize,
            Margin = new Thickness(0, 0, BinBadge.Gap, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        var label = new TextBlock
        {
            Text = BinBadge.Text(armed: false),
            Foreground = BinBadge.Ink,
            FontFamily = BinBadge.Font,
            FontSize = BinBadge.FontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plate = new Border
        {
            Background = BinBadge.Plate,
            CornerRadius = new CornerRadius(BinBadge.Height / 2),
            Height = BinBadge.Height,
            Padding = new Thickness(BinBadge.PadLeft, 0, BinBadge.PadRight, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
        };

        var button = new Button
        {
            Template = BareButton,
            Content = plate,
            Cursor = Cursors.Hand,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, BinBadge.Right, BinBadge.Bottom),
            ToolTip = picture.InNotes
                ? "Delete this picture\nIt goes to the Recycle Bin and is taken out of the notes too."
                : "Delete this picture\nIt goes to the Recycle Bin.",
        };
        button.MouseEnter += (_, _) => plate.Background = BinBadge.PlateHot;
        button.MouseLeave += (_, _) => plate.Background = BinBadge.Plate;

        void Ink(Brush brush)
        {
            icon.Stroke = brush;
            label.Foreground = brush;
        }

        button.Click += (_, _) =>
        {
            if (_undoArm is null || !ReferenceEquals(button.Tag, "armed"))
            {
                Arm(button, () =>
                {
                    button.Tag = null;
                    label.Text = BinBadge.Text(armed: false);
                    Ink(BinBadge.Ink);
                });
                button.Tag = "armed";
                label.Text = BinBadge.Text(armed: true);
                Ink(_theme.Warn);
                return;
            }

            Disarm();
            Delete(new[] { picture });
        };

        return button;
    }

    /// <summary>A button that is only its content - the bin's plate is its look. One for every tile, made once.</summary>
    private static ControlTemplate? BareButton;

    private static ControlTemplate SealedBareButton()
    {
        var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        template.Seal();
        return template;
    }

    /// <summary>
    /// As many columns as fit at <see cref="MinThumbWidth"/>, then every tile
    /// widened to share the row out between them. Resized in place, not
    /// rebuilt: this runs as the window is dragged wider, and rebuilding would
    /// also let go of a Delete that had been armed.
    /// </summary>
    private void FitTiles()
    {
        double width = Scroller.ViewportWidth;
        if (width <= 0)
            return;

        // Every tile's gap counted, the last one's too: the wrap measures it,
        // and a row a few pixels over would drop its last tile to the next.
        double perTile = MinThumbWidth + 2 * TilePadding + TileGap;
        int columns = Math.Max(1, (int)(width / perTile));
        double thumb = Math.Floor(width / columns - TileGap - 2 * TilePadding);

        if (Math.Abs(thumb - _thumbWidth) < 1)
            return;

        _thumbWidth = thumb;
        foreach (var tile in Tiles.Children.OfType<Border>())
        {
            tile.Width = _thumbWidth + 2 * TilePadding;
            if (tile.Tag is TileParts parts)
            {
                parts.Frame.Width = _thumbWidth;
                parts.Frame.Height = ThumbHeight;
            }
        }
    }

    private bool _loadQueued;

    /// <summary>
    /// After the next layout pass, when the tiles have their places - and once
    /// however many scroll events arrive before then.
    /// </summary>
    private void LoadVisibleSoon()
    {
        if (_loadQueued)
            return;

        _loadQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _loadQueued = false;
            LoadVisible();
        }));
    }

    /// <summary>
    /// Thumbnails for the tiles on screen and a screenful either side, so a
    /// scroll finds them ready; tiles further off let theirs go. Memory then
    /// follows what is near the view, not how many pictures a game has - a
    /// tile showing its thumbnail holds it, so with every tile loaded, five
    /// hundred pictures would hold five hundred thumbnails however few were
    /// on screen.
    /// </summary>
    private void LoadVisible()
    {
        double screen = Scroller.ViewportHeight;
        if (screen <= 0)
            return;

        var wanted = new List<(TileParts Parts, double Distance)>();
        foreach (var tile in Tiles.Children.OfType<Border>())
        {
            if (tile.Tag is not TileParts parts)
                continue;

            double top = tile.TranslatePoint(new Point(0, 0), Scroller).Y;
            bool near = top + tile.ActualHeight >= -screen && top <= 2 * screen;

            if (near && parts.Image.Source is null && !parts.Loading)
            {
                // How far off the view it is: 0 on screen, else the gap to it.
                double distance = top + tile.ActualHeight < 0 ? -(top + tile.ActualHeight)
                                : top > screen ? top - screen
                                : 0;
                wanted.Add((parts, distance));
            }
            else if (!near && parts.Image.Source is not null)
            {
                parts.Image.Source = null;
            }
        }

        // The ones in view queued first, then outwards: decodes are taken a
        // couple at a time (see DecodeGate), so the order is the order they
        // appear in, and what is looked at doesn't wait behind what isn't.
        foreach (var (parts, _) in wanted.OrderBy(w => w.Distance))
            _ = LoadThumbAsync(parts);
    }

    /// <summary>
    /// At most two thumbnails decoded at once. Every tile near the view used
    /// to start its own at the same moment, and a PNG - the usual screenshot -
    /// is decoded at full size before it is scaled down, about 33 MB for a 4K
    /// one: thirty at once took every core and several hundred MB, and this
    /// window can be open beside the notes over a game.
    /// </summary>
    private static readonly System.Threading.SemaphoreSlim DecodeGate = new(2);

    // --- Deleting ------------------------------------------------------------

    private void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (CleanButton.Tag is not List<Picture> unused || unused.Count == 0)
            return;

        if (!ReferenceEquals(CleanText.Tag, "armed"))
        {
            Arm(CleanButton, () =>
            {
                CleanText.Tag = null;
                CleanText.Text = $"DELETE UNUSED ({unused.Count})";
                CleanButton.ClearValue(ForegroundProperty);
            });
            CleanText.Tag = "armed";
            CleanText.Text = $"DELETE {unused.Count}? CLICK AGAIN";
            CleanButton.Foreground = _theme.Warn;
            return;
        }

        Disarm();
        Delete(unused);
    }

    /// <summary>Only one button armed at a time: arming another lets the last one go.</summary>
    private void Arm(Button button, Action undo)
    {
        Disarm();
        _undoArm = undo;
        _disarm.Start();
    }

    private void Disarm()
    {
        _disarm.Stop();
        var undo = _undoArm;
        _undoArm = null;
        undo?.Invoke();
    }

    /// <summary>
    /// To the Recycle Bin first, out of the notes second: if Windows refuses
    /// the file, the notes are left as they were rather than missing a picture
    /// whose file is still there.
    /// </summary>
    /// <summary>A delete is under way; a second click waits for it rather than sending the same files again.</summary>
    private bool _deleting;

    /// <remarks>
    /// All the files in one shell call, off the UI thread - see
    /// RecycleBin.SendAllAsync. One by one on this thread, DELETE UNUSED froze
    /// every DeskMon window for seconds with a hundred pictures to go.
    /// </remarks>
    private async void Delete(IReadOnlyList<Picture> pictures)
    {
        if (_deleting)
            return;

        _deleting = true;
        _shown = null;   // the refresh below must rebuild, whatever it finds

        try
        {
            foreach (var picture in pictures)
                Thumbs.Remove(picture.Path);

            List<string> left;
            string? problem = null;
            try
            {
                left = await RecycleBin.SendAllAsync(pictures.Select(p => p.Path).ToList());
            }
            catch (Exception ex)
            {
                left = pictures.Select(p => Path.GetFullPath(p.Path)).ToList();
                problem = ex.Message;
            }

            var stillThere = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
            var failed = new List<string>();

            foreach (var picture in pictures)
            {
                if (stillThere.Contains(Path.GetFullPath(picture.Path)))
                {
                    failed.Add(picture.Name);
                    continue;
                }

                if (picture.InNotes)
                    _notes.RemovePicture(picture.Name);
            }

            Refresh();

            if (failed.Count > 0)
                Say($"Couldn't delete {string.Join(", ", failed)}: {problem ?? "Windows refused"}");
        }
        finally
        {
            _deleting = false;
        }
    }

    // --- Thumbnails ----------------------------------------------------------

    /// <summary>
    /// Decoded in the background at the size shown, not full size: a 4K PNG
    /// is 30 MB once decoded, and a dozen of them would be a few hundred MB
    /// for pictures 176 px wide. Read whole and closed at once (OnLoad), so
    /// the file isn't held open and can still be deleted.
    /// </summary>
    private async Task LoadThumbAsync(TileParts parts)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(parts.Path); }
        catch { return; }

        if (Thumbs.TryGetValue(parts.Path, out var cached) && cached.Written == written)
        {
            Thumbs[parts.Path] = cached with { Used = ++_thumbUse };
            parts.Image.Source = cached.Image;
            return;
        }

        parts.Loading = true;
        BitmapSource? thumb;
        await DecodeGate.WaitAsync();
        try
        {
            // Gone while it waited its turn - the window put away, or the
            // tiles rebuilt for another game: no decode for a tile that is
            // no longer there to show it.
            thumb = IsVisible && parts.Frame.IsLoaded
                ? await Task.Run(() => Decode(parts.Path, ThumbPixels))
                : null;
        }
        finally
        {
            DecodeGate.Release();
            parts.Loading = false;
        }
        if (thumb is null)
            return;

        Remember(parts.Path, written, thumb);
        parts.Image.Source = thumb;

        // It may have scrolled far off while decoding; the next pass decides.
        LoadVisibleSoon();
    }

    /// <summary>Into the cache, and the least recently used out once it holds more than <see cref="ThumbsKept"/>.</summary>
    private static void Remember(string path, DateTime written, BitmapSource thumb)
    {
        Thumbs[path] = (written, thumb, ++_thumbUse);

        if (Thumbs.Count <= ThumbsKept)
            return;

        string oldest = Thumbs.MinBy(entry => entry.Value.Used).Key;
        Thumbs.Remove(oldest);
    }

    private static BitmapSource? Decode(string path, int width)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();   // made on a worker thread, shown on the UI one
            return bitmap;
        }
        catch
        {
            return null;   // a damaged file stays a grey placeholder
        }
    }

    // --- Helpers -------------------------------------------------------------

    private double SmallFont => Math.Max(9, UiTheme.PanelFontSize - 3);

    /// <summary>"3.6 MB", "280 KB".</summary>
    private static string Size(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : Math.Max(1, bytes / 1024) + " KB";

    private void Open(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* no viewer for the type, or it refused - nothing useful to say */ }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = _notes.PictureFolder;
        try
        {
            // Made if it doesn't exist yet, so the button never does nothing.
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // --- Chrome ------------------------------------------------------------

    /// <summary>
    /// Moves the notes, which this window then follows - so the header drags
    /// the pair as one, wherever it is grabbed. No double-click to maximise:
    /// a maximised pictures window would cover the notes it is docked to.
    /// </summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
            return;

        try
        {
            if (_notes.IsVisible && _notes.WindowState == WindowState.Normal)
                _notes.DragMove();
            else
                DragMove();
        }
        catch (InvalidOperationException)
        {
            // Button was already up; nothing moved.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

        _settings.PicturesLeft = Left;
        _settings.PicturesTop = Top;
        _settings.PicturesWidth = Width;
        _settings.PicturesHeight = Height;
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

        Foreground = _theme.NotesText;
        FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        FontFamily = _theme.PanelFont;

        // The empty state: a heading clearly larger than the window's text,
        // then the explanation, then the how-to a step quieter again.
        EmptyTitle.Foreground = _theme.Section;
        EmptyTitle.FontSize = UiTheme.PanelFontSize + 4;
        EmptyText.Foreground = _theme.Label;
        EmptyHint.Foreground = _theme.Header;
        EmptyHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 2);
    }
}
