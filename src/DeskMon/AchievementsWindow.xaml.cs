using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Notes;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// Personal achievements - see <see cref="Achievements"/>. A card for each:
/// unlocked ones in their colours with the date, locked ones grey with how
/// far along they are and what it takes.
///
/// The widget works the achievements out and hands them over (Fill); this
/// window only shows them. An unlock not seen yet plays its animation here
/// the first time it is on screen, then counts as seen. Hidden rather than
/// closed, like the other windows.
/// </summary>
public partial class AchievementsWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Waits for a theme change to settle before the cards are made again - see the constructor.</summary>
    private readonly DispatcherTimer _retheme = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _allowClose;

    /// <summary>What was last shown, to draw again for a theme change.</summary>
    private IReadOnlyList<Achievements.Result>? _results;
    private Achievements.Book? _book;

    /// <summary>
    /// The badges whose latest level hasn't been seen, waiting for the window
    /// to be on screen - with every level key they stand for, so a badge that
    /// went from none to Silver marks both seen.
    /// </summary>
    private readonly List<(Badge Badge, List<string> Keys)> _toAnimate = new();

    /// <summary>The player level is above the one last shown: the emblem plays its level-up once on screen.</summary>
    private bool _emblemLevelUp;

    public AchievementsWindow(AppSettings settings, UiTheme theme)
    {
        _settings = settings;
        _theme = theme;

        InitializeComponent();
        new Banner(Panel, height: 180).Show(GameArt.Motif.Gamepad);
        ApplyTheme();
        // The cards are made again only once the theme has settled: Changed
        // comes on every step of a colour or text-size drag in the settings
        // window, and 24 cards with their badges and shadows each step made
        // the drag lag. Hidden, only noted - ShowAchievements fills it anew.
        _retheme.Tick += (_, _) =>
        {
            _retheme.Stop();
            if (IsVisible && _results is not null && _book is not null)
                Fill(_results, _book);
        };
        _theme.Changed += () =>
        {
            ApplyTheme();
            if (IsVisible)
            {
                _retheme.Stop();
                _retheme.Start();
            }
        };
        RestorePlacement(_settings.AchievementsLeft, _settings.AchievementsTop,
                         _settings.AchievementsWidth, _settings.AchievementsHeight);
        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.AchievementsOpacity);

        _placementSave.Tick += (_, _) => SavePlacementNow();
        _disarmClear.Tick += (_, _) => DisarmClear();
        // Put away half-asked, it opens again unarmed.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                DisarmClear();
                _picked.Clear();   // it opens again on the levels reached
            }
        };
        Closing += OnClosing;
        LocationChanged += (_, _) => QueuePlacementSave();
        SizeChanged += (_, _) => QueuePlacementSave();

        // The animations wait for the window to be drawn: started before, they
        // would have run their course out of sight.
        ContentRendered += (_, _) => AnimateUnseen();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(AnimateUnseen, DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// Shows these results: the count over the top and a card each. Unlocks
    /// not seen yet get their animation once the window is on screen.
    /// </summary>
    internal void Fill(IReadOnlyList<Achievements.Result> results, Achievements.Book book)
    {
        _results = results;
        _book = book;
        _toAnimate.Clear();
        Sections.Children.Clear();

        // The player level, from the points the tiers unlocked are worth -
        // see Achievements.PlayerLevel. The bar and the emblem's ring are the
        // way to the next level, not a share of all tiers: that one barely
        // moved in a week, while the next level is always in sight.
        int tiers = results.Sum(r => r.Achievement.Levels.Length);
        int unlocked = results.Sum(r => book.LevelOf(r.Achievement));
        int points = results.Sum(r => Achievements.PointsFor(r.Achievement, book.LevelOf(r.Achievement)));
        int player = Achievements.PlayerLevel(points);
        bool top = player >= Achievements.TopLevel;
        int from = Achievements.PointsForLevel(player), to = Achievements.PointsForLevel(player + 1);
        double toNext = top ? 1 : (double)(points - from) / Math.Max(1, to - from);

        CountText.Text = $"Level {player}  ·  {Achievements.RankName(player)}";
        LevelText.Text = top
            ? $"{points} points  ·  the top level  ·  {unlocked} of {tiers} tiers unlocked"
            : $"{points} points  ·  {to - points} more to level {player + 1}  ·  {unlocked} of {tiers} tiers unlocked";
        var metal = Ui.Levels.Metal(Achievements.RankTier(player));
        CountFill.Background = new SolidColorBrush(metal);
        RankHost.Content = new RankEmblem(player, Achievements.RankName(player), toNext, Achievements.RankTier(player), _theme.Shade(0x1F))
        {
            ToolTip = $"Level {player} - {Achievements.RankName(player)}\n" +
                      "Every tier unlocked gives points: Bronze 1, Silver 2, Gold 3, Platinum 5.\n" +
                      $"{points} of {Achievements.MaxPoints} points; level {Achievements.TopLevel} is every tier of every achievement.",
        };
        // After a CLEAR ALL, said where the count starts, so locked badges
        // over a long history don't read as the checks being broken.
        CountHint.Text = book.CountFrom is { } since
            ? $"Counting from {since.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}, when they were cleared. Hours are DeskMon's own from then on."
            : "For how you play, not what you did in a game. Worked out from your sessions, notes and screenshots.";
        // The fill follows the track as the window is resized - one handler,
        // however often this runs.
        _share = toNext;
        CountTrack.SizeChanged -= KeepFill;
        CountTrack.SizeChanged += KeepFill;
        KeepFill(null, null);

        // A level above the one the window last showed: the emblem plays its
        // level-up once on screen - see AnimateUnseen.
        _emblemLevelUp = player > book.LevelSeen;

        Filters(results, book);

        // The cards shown under the filter picked, and the ones closest to
        // their next tier first, in a section of their own - only under All,
        // where they are otherwise somewhere down the scroll.
        var shown = results.Where(r => Passes(r, book, _filter)).ToList();
        var made = new List<FrameworkElement>();
        if (_filter == Filter.All)
        {
            var close = AlmostThere(results, book);
            if (close.Count > 0)
                Section(AlmostThereName, close, book, made, done: -1, of: -1);
        }

        // A heading per category, in the catalogue's order, each with how many
        // of its levels are done, then its cards - all one height, the
        // tallest any of them needs (see Card), so the grid is even and a
        // level picked moves nothing.
        foreach (string category in Achievements.Categories)
        {
            var own = shown.Where(r => r.Achievement.Category == category).ToList();
            if (own.Count == 0)
                continue;

            // The count is of the whole category, filtered or not: "2 / 16"
            // is how far the category is along, not how many cards are shown.
            var all = results.Where(r => r.Achievement.Category == category).ToList();
            Section(category, own, book, made,
                    done: all.Sum(r => book.LevelOf(r.Achievement)),
                    of: all.Sum(r => r.Achievement.Levels.Length));
        }

        if (Sections.Children.Count == 0)
            Sections.Children.Add(new TextBlock
            {
                Text = _filter switch
                {
                    Filter.Unlocked => "Nothing unlocked yet - play a while and they come.",
                    Filter.Progress => "Nothing on the way to its next tier just now.",
                    _ => "Every one unlocked. Nothing left locked.",
                },
                Foreground = _theme.Header, Margin = new Thickness(6, 12, 6, 0), TextWrapping = TextWrapping.Wrap,
            });

        double height = made.Count == 0 ? 0 : made.Max(card => card.Tag is double tall ? tall : 0);
        foreach (var card in made)
            card.Height = Math.Ceiling(height);

        if (IsVisible)
            Dispatcher.BeginInvoke(AnimateUnseen, DispatcherPriority.Background);
    }

    // --- Sections -----------------------------------------------------------

    /// <summary>The almost-there section's name, also its key in the folded set.</summary>
    private const string AlmostThereName = "Almost there";

    /// <summary>How many almost-there cards - one row in a window of usual width.</summary>
    private const int AlmostThereCount = 3;

    /// <summary>
    /// The achievements closest to their next tier, closest first - the next
    /// goal in plain sight. Only ones under way: a "once is all it takes"
    /// level is either done or not, so it is never almost there.
    /// </summary>
    private static List<Achievements.Result> AlmostThere(IReadOnlyList<Achievements.Result> results, Achievements.Book book)
        => results
            .Select(r => (Result: r, Share: ShareToNext(r, book)))
            .Where(x => x.Share is > 0 and < 1)
            .OrderByDescending(x => x.Share)
            .Take(AlmostThereCount)
            .Select(x => x.Result)
            .ToList();

    /// <summary>How far towards its next tier, 0 to 1 - or -1 with every tier unlocked or none with a target worth a bar.</summary>
    private static double ShareToNext(Achievements.Result result, Achievements.Book book)
    {
        var a = result.Achievement;
        int level = book.LevelOf(a);
        if (level >= a.Levels.Length || a.Levels[level] <= 1)
            return -1;
        return Math.Clamp(result.Measure.Value / a.Levels[level], 0, 1);
    }

    /// <summary>
    /// One section: its heading - folds it when clicked - the accent line,
    /// and unless folded, its cards. Folded cards aren't made at all, which
    /// also spares their badges being drawn.
    /// </summary>
    /// <param name="done">Tiers unlocked in it, for "3 / 24" and its bar - below 0 for none, as almost there has.</param>
    private void Section(string name, List<Achievements.Result> own, Achievements.Book book,
                         List<FrameworkElement> made, int done, int of)
    {
        bool folded = book.Folded.Contains(name);

        var heading = new DockPanel
        {
            Margin = new Thickness(6, Sections.Children.Count == 0 ? 2 : 14, 6, 2),
            Background = Brushes.Transparent,   // so the gaps take the click too
            Cursor = Cursors.Hand,
            ToolTip = folded ? "Click to unfold" : "Click to fold",
        };
        // A chevron that points down when open, right when folded - the
        // notes window's list fold says the same with it.
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(folded ? "M2,0 L6,4 L2,8" : "M0,2 L4,6 L8,2"),
            Stroke = _theme.Section, StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
        };
        heading.Children.Add(chevron);

        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run(name.ToUpperInvariant()) { Foreground = _theme.Section, FontWeight = FontWeights.SemiBold });
        if (done >= 0)
            title.Inlines.Add(new Run($"   {done} / {of}") { Foreground = _theme.Header });
        else if (folded)
            title.Inlines.Add(new Run($"   {own.Count}") { Foreground = _theme.Header });
        heading.Children.Add(title);

        // A short bar after the count, so the categories can be held up
        // against each other at a glance - 3 of 24 and 9 of 16 read alike
        // as figures, not as bars.
        if (done >= 0 && of > 0)
        {
            var bar = new Grid { Width = 70, Height = 3, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = _theme.Shade(0x1F) });
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Width = 70.0 * done / of, Background = _theme.Accent,
            });
            heading.Children.Add(bar);
        }

        heading.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!book.Folded.Add(name))
                book.Folded.Remove(name);
            book.SaveLater();
            Refill(keepScroll: true);
        };
        Sections.Children.Add(heading);

        // The accent line under each heading, so where one category ends
        // and the next begins reads at a glance in a long scroll. A
        // resource reference, not a copied brush, so it follows the theme.
        var rule = new Border { Height = 1, Margin = new Thickness(6, 2, 6, folded ? 0 : 6) };
        rule.SetResourceReference(Border.BackgroundProperty, "AccentRule");
        Sections.Children.Add(rule);

        if (folded)
            return;
        var cards = new WrapPanel();
        foreach (var result in own)
        {
            var card = Card(result, book);
            made.Add(card);
            cards.Children.Add(card);
        }
        Sections.Children.Add(cards);
    }

    /// <summary>
    /// The cards made again for a fold or a filter - at the same place in the
    /// scroll for a fold, so the heading clicked stays under the mouse; at
    /// the top for a filter, as it is a different list.
    /// </summary>
    private void Refill(bool keepScroll)
    {
        if (_results is null || _book is null)
            return;
        double offset = Scroller.VerticalOffset;
        Fill(_results, _book);
        if (keepScroll)
        {
            Scroller.UpdateLayout();
            Scroller.ScrollToVerticalOffset(offset);
        }
        else
            Scroller.ScrollToTop();
    }

    // --- Filters ------------------------------------------------------------

    private enum Filter { All, Unlocked, Progress, Locked }

    /// <summary>The filter picked - for as long as DeskMon runs, back to All on a restart.</summary>
    private Filter _filter = Filter.All;

    /// <summary>
    /// Unlocked: at least Bronze. In progress: a next tier with some way made
    /// towards it - so an unlocked card on its way to Silver is both.
    /// Locked: not even Bronze.
    /// </summary>
    private static bool Passes(Achievements.Result result, Achievements.Book book, Filter filter) => filter switch
    {
        Filter.Unlocked => book.LevelOf(result.Achievement) > 0,
        Filter.Progress => ShareToNext(result, book) > 0,
        Filter.Locked => book.LevelOf(result.Achievement) == 0,
        _ => true,
    };

    /// <summary>A chip per filter, each with how many cards it shows; the one picked filled in the accent.</summary>
    private void Filters(IReadOnlyList<Achievements.Result> results, Achievements.Book book)
    {
        FilterRow.Children.Clear();
        foreach (var (filter, label) in new[] { (Filter.All, "ALL"), (Filter.Unlocked, "UNLOCKED"), (Filter.Progress, "IN PROGRESS"), (Filter.Locked, "LOCKED") })
        {
            bool on = filter == _filter;
            int count = results.Count(r => Passes(r, book, filter));
            var text = new TextBlock { FontSize = Math.Max(9, UiTheme.PanelFontSize - 2), FontWeight = FontWeights.SemiBold };
            text.Inlines.Add(new Run(label) { Foreground = on ? _theme.NotesText : _theme.Label });
            text.Inlines.Add(new Run($"  {count}") { Foreground = on ? _theme.NotesText : _theme.Header });
            var chip = new Border
            {
                Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1),
                Background = on ? _theme.Shade(0x26) : Brushes.Transparent,
                BorderBrush = on ? _theme.Accent : _theme.Shade(0x26),
                Cursor = Cursors.Hand,
                Child = text,
            };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (filter == _filter)
                    return;
                _filter = filter;
                Refill(keepScroll: false);
            };
            FilterRow.Children.Add(chip);
        }
    }

    /// <summary>The share unlocked, which the bar's fill keeps to as the window is resized.</summary>
    private double _share;

    private void KeepFill(object? sender, SizeChangedEventArgs? e)
        => CountFill.Width = Math.Max(0, CountTrack.ActualWidth * _share);

    /// <summary>
    /// One achievement's card: the badge with a button per level under it,
    /// and beside it the level shown - its name, what it takes, and when it
    /// was unlocked or how far along it is.
    ///
    /// It opens on the level reached (Bronze if none), with the way to the
    /// next one under it. The level buttons show any other level, locked or
    /// not: the next one's badge and target were otherwise nowhere to be seen.
    /// </summary>
    private FrameworkElement Card(Achievements.Result result, Achievements.Book book)
    {
        var achievement = result.Achievement;
        int level = book.LevelOf(achievement);

        // Cards with a level washed in their badge's colour from the top left
        // and edged in it, strong - a shade lifted and a faint edge read as
        // nearly the same grey as the locked ones beside them. Locked cards
        // stay flat and dim, so the difference is the colour itself.
        var c = achievement.Badge.From;
        var card = new Border
        {
            Width = 266, Margin = new Thickness(6), Padding = new Thickness(12),   // room for the level buttons, three to a row still - see LevelButtons
            CornerRadius = new CornerRadius(8),
            Background = level > 0
                ? new LinearGradientBrush(Color.FromArgb(0x40, c.R, c.G, c.B), Color.FromArgb(0x0C, c.R, c.G, c.B), new Point(0, 0), new Point(1, 1))
                : _theme.Shade(0x08),
            BorderBrush = level > 0 ? new SolidColorBrush(Color.FromArgb(0xC0, c.R, c.G, c.B)) : _theme.Shade(0x12),
            BorderThickness = new Thickness(1),
        };

        // A level picked by its button is remembered for as long as the
        // window is open: the cards are made again on every check - a
        // session ending, a note saved - and each went back to the level
        // reached, under the mouse of whoever was looking at Platinum.
        void Show(int shown, bool first)
            => card.Child = CardContent(result, book, level, shown, first, s =>
            {
                _picked[achievement.Id] = s;
                Show(s, first: false);
            });

        if (_picked.TryGetValue(achievement.Id, out int chosen))
            Show(Math.Clamp(chosen, 1, achievement.Levels.Length), first: true);
        else
            Show(Math.Max(1, level), first: true);

        // The tallest this card's content gets, over every level it can show
        // and the one it opens on - for Fill, which gives every card that
        // height, so a level picked never makes the card, or the row under
        // it, jump: a date, a bar, a wrapped line all take different room.
        double inner = card.Width - card.Padding.Left - card.Padding.Right - 2 * card.BorderThickness.Left;
        double tallest = Tall(CardContent(result, book, level, Math.Max(1, level), false, _ => { }), inner);
        for (int l = 1; l <= achievement.Levels.Length; l++)
            tallest = Math.Max(tallest, Tall(CardContent(result, book, level, l, false, _ => { }), inner));
        card.Tag = tallest + card.Padding.Top + card.Padding.Bottom + 2 * card.BorderThickness.Top;
        return card;
    }

    /// <summary>
    /// How tall this content is at this width, measured off screen in the
    /// window's own font - measured bare it took the system's, a different
    /// size, and came out short.
    /// </summary>
    private double Tall(FrameworkElement content, double width)
    {
        var probe = new Border { Child = content };
        TextElement.SetFontFamily(probe, FontFamily);
        TextElement.SetFontSize(probe, FontSize);
        TextOptions.SetTextFormattingMode(probe, TextFormattingMode.Display);
        probe.Measure(new Size(width, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    /// <summary>The level picked on each card, by achievement id - forgotten when the window is put away, so it opens on the levels reached.</summary>
    private readonly Dictionary<string, int> _picked = new();

    /// <param name="level">The level reached, 0 for none.</param>
    /// <param name="shown">The level the card is showing, 1 for Bronze - reached or not.</param>
    /// <param name="first">The card's first drawing: the one an unlock not seen yet animates in.</param>
    /// <param name="pick">Shows another level - the level buttons call it.</param>
    private FrameworkElement CardContent(Achievements.Result result, Achievements.Book book,
                                         int level, int shown, bool first, Action<int> pick)
    {
        var achievement = result.Achievement;
        int levels = achievement.Levels.Length;
        bool reached = shown <= level;
        double small = Math.Max(9, UiTheme.PanelFontSize - 3);

        // A level not reached yet is drawn in its colours but faded, so it
        // reads as what is to come rather than as earned - the same whether
        // the card opened on it or it was picked. A locked card opened as a
        // plain grey badge, though Bronze was the tier already shown, lit
        // under its button: the badge and the button disagreed.
        var badge = new Badge(achievement.Badge, 64, shown, levels, _theme.IsLight, achievement.BadgeLabel(shown))
        {
            Opacity = reached ? 1 : 0.4,
        };

        // Played when a level not seen yet is on screen - see AnimateUnseen.
        if (first)
        {
            var unseen = Enumerable.Range(1, level)
                .Select(l => Achievements.KeyFor(achievement.Id, l))
                .Where(key => !book.Seen.Contains(key))
                .ToList();
            if (unseen.Count > 0)
            {
                badge.Opacity = 0;   // until its animation brings it in
                _toAnimate.Add((badge, unseen));
            }
        }

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(badge);
        if (levels > 1)
            left.Children.Add(LevelButtons(achievement, level, shown, pick));

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // Room on the right for the tick in the corner, so a long name is cut
        // short before it rather than under it.
        var name = new TextBlock
        {
            FontWeight = FontWeights.SemiBold, FontSize = UiTheme.PanelFontSize, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, reached ? 18 : 0, 0),
        };
        name.Inlines.Add(new System.Windows.Documents.Run(achievement.Name) { Foreground = level > 0 ? _theme.NotesText : _theme.Label });
        text.Children.Add(name);
        if (levels > 1)
        {
            // The level shown on a line of its own, in its metal - dimmed, and
            // said to be locked, when it isn't reached yet. Beside the name it
            // cut "Weekend Warrior" short.
            var metal = Ui.Levels.Metal(shown);
            var tier = new TextBlock { FontSize = small, Margin = new Thickness(0, 1, 0, 0) };
            tier.Inlines.Add(new System.Windows.Documents.Run(Achievements.LevelNames[shown - 1].ToUpperInvariant())
            {
                Foreground = new SolidColorBrush(reached ? metal : Color.FromArgb(0x99, metal.R, metal.G, metal.B)),
                FontWeight = FontWeights.SemiBold,
            });
            if (!reached)
                tier.Inlines.Add(new System.Windows.Documents.Run("  ·  locked") { Foreground = _theme.Header });
            text.Children.Add(tier);
        }

        text.Children.Add(new TextBlock
        {
            Text = achievement.HowFor(shown), FontSize = small, Foreground = _theme.Header,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        if (reached)
        {
            book.Unlocked.TryGetValue(Achievements.KeyFor(achievement.Id, shown), out var when);
            text.Children.Add(new TextBlock
            {
                Text = "Unlocked " + when.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
                FontSize = small, Foreground = _theme.Accent, Margin = new Thickness(0, 5, 0, 0),
            });

            // On the level reached, the way to the next one under it - what
            // the card said before it had level buttons.
            if (shown == level && level < levels)
                Progress(text, result, level + 1, levels, small, towards: true);
        }
        else if (achievement.Levels[shown - 1] <= 1)
        {
            // One time is all it takes: a bar from nothing to one, and
            // "0 / 1 nights", said nothing that "not yet" doesn't.
            text.Children.Add(new TextBlock
            {
                Text = "Not yet", FontSize = small, Foreground = _theme.Header, Margin = new Thickness(0, 5, 0, 0),
            });
        }
        else
            Progress(text, result, shown, levels, small, towards: false);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(left);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // A tick in the badge's colour in the top right corner of the level
        // shown when it is unlocked - the plainest "this one is yours", which
        // the faded badge and a small date line left to be worked out.
        if (reached)
        {
            var bc = achievement.Badge.From;
            var tick = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(bc),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0),
                ToolTip = "Unlocked",
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M4.5,9.5 L7.5,12.5 L13.5,6"),
                    Stroke = Brushes.White, StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Margin = new Thickness(0, -1, 0, 0),
                },
            };
            Grid.SetColumnSpan(tick, 2);
            row.Children.Add(tick);
        }
        return row;
    }

    /// <summary>
    /// How far along towards <paramref name="toward"/>: a bar in that level's
    /// metal, so it is plain which one is being worked on, and the figures.
    /// </summary>
    /// <param name="towards">Under the level reached: says which comes next, as it isn't the one shown.</param>
    private void Progress(StackPanel text, Achievements.Result result, int toward, int levels, double small, bool towards)
    {
        var achievement = result.Achievement;
        double target = achievement.Levels[toward - 1];
        double share = Math.Clamp(result.Measure.Value / target, 0, 1);
        var metal = levels > 1 ? Ui.Levels.Metal(toward) : achievement.Badge.From;

        var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = _theme.Shade(0x1F) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, metal.R, metal.G, metal.B)),
        };
        bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * share;
        bar.Children.Add(fill);
        text.Children.Add(bar);

        string unit = achievement.Unit.Length > 0 ? " " + achievement.Unit : string.Empty;
        string next = towards && levels > 1 ? $"  ·  {Achievements.LevelNames[toward - 1]} next" : string.Empty;
        text.Children.Add(new TextBlock
        {
            Text = $"{Achievements.Figure(Math.Min(result.Measure.Value, target))} / {Achievements.Figure(target)}{unit}{next}",
            FontSize = small, Foreground = _theme.Label, Margin = new Thickness(0, 3, 0, 0),
            // Wraps rather than cut: with the bigger level buttons, "14 / 25
            // sessions · Silver next" lost its last letters.
            TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>
    /// A small round button per level under the badge, with the level's
    /// letter - B, S, G, P. Filled in its metal once reached, an outline of
    /// it until then; the one shown has a ring round it. Each shows its level.
    /// 18 px with a 10 px letter: at 13 and 8 they were hard to hit and the
    /// letters hard to read. The cards are widened to match - see Card.
    /// </summary>
    private FrameworkElement LevelButtons(Achievements.Achievement achievement, int level, int shown, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        for (int l = 1; l <= achievement.Levels.Length; l++)
        {
            int at = l;
            var metal = Ui.Levels.Metal(l);
            bool got = l <= level;
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                Background = got ? new SolidColorBrush(metal) : Brushes.Transparent,
                BorderBrush = new SolidColorBrush(got ? metal : Color.FromArgb(0x90, metal.R, metal.G, metal.B)),
                BorderThickness = new Thickness(1.4),
                Child = new TextBlock
                {
                    Text = Achievements.LevelNames[l - 1][..1], FontSize = 10, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = got ? new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x24)) : new SolidColorBrush(Color.FromArgb(0xC0, metal.R, metal.G, metal.B)),
                },
            };
            // The ring for the one shown: round the dot, a pixel off, so the
            // dot itself still says reached or not.
            var ring = new Border
            {
                Padding = new Thickness(1.5), Margin = new Thickness(0.5, 0, 0.5, 0), CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.2),
                BorderBrush = l == shown ? _theme.NotesText : Brushes.Transparent,
                Background = Brushes.Transparent,   // so the gap round the dot takes the click too
                Cursor = Cursors.Hand,
                Child = dot,
                ToolTip = $"{Achievements.LevelNames[l - 1]}{(got ? "" : " - locked")}\n{achievement.HowFor(l)}",
            };
            ring.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (at != shown)
                    pick(at);
            };
            row.Children.Add(ring);
        }
        return row;
    }

    /// <summary>
    /// Plays the levels not seen yet, one badge after another, and marks them
    /// seen - once the window is actually on screen, so they aren't spent unseen.
    /// </summary>
    private void AnimateUnseen()
    {
        if (!IsVisible || _book is null)
            return;

        // A new player level: the emblem swells and settles, once, and the
        // level counts as seen - played here, on screen, for the same reason
        // as the badges.
        if (_emblemLevelUp && RankHost.Content is FrameworkElement emblem)
        {
            _emblemLevelUp = false;
            _book.LevelSeen = Achievements.PlayerLevel(_book.PointsNow());
            _book.SaveLater();
            var grow = new System.Windows.Media.ScaleTransform(1, 1);
            emblem.RenderTransformOrigin = new Point(0.5, 0.45);
            emblem.RenderTransform = grow;
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(900))
            {
                BeginTime = TimeSpan.FromMilliseconds(200),
                EasingFunction = new System.Windows.Media.Animation.ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            grow.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
            grow.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
        }

        if (_toAnimate.Count == 0)
            return;

        for (int i = 0; i < _toAnimate.Count; i++)
        {
            var (badge, keys) = _toAnimate[i];
            // The first one scrolled to, not each in turn: that ended on the
            // last, and the window opened scrolled past the rest of them.
            if (i == 0)
                badge.BringIntoView();
            badge.PlayUnlock(TimeSpan.FromMilliseconds(250 + i * 350));
            foreach (string key in keys)
                _book.Seen.Add(key);
        }
        _toAnimate.Clear();
        _book.SaveLater();
    }

    // --- Clear all ----------------------------------------------------------

    /// <summary>CLEAR ALL, confirmed: the widget starts the achievements over and fills this window again.</summary>
    public event Action? ClearRequested;

    private bool _clearArmed;

    /// <summary>Puts the button back if the second click doesn't come - a stray first click mustn't stay loaded.</summary>
    private readonly DispatcherTimer _disarmClear = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Asks twice, like the notes' DELETE: twenty unlocks and their dates
    /// can't be had back once cleared, and the button sits by the close.
    /// </summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            // On the button, so the bin and the label turn together; a local
            // value outranks the template's hover colour.
            ClearButton.Foreground = _theme.Warn;
            ClearLabel.Text = "CLICK AGAIN TO CLEAR";
            _disarmClear.Stop();
            _disarmClear.Start();
            return;
        }

        DisarmClear();
        ClearRequested?.Invoke();
    }

    private void DisarmClear()
    {
        _disarmClear.Stop();
        _clearArmed = false;
        ClearButton.ClearValue(ForegroundProperty);
        ClearLabel.Text = "CLEAR ALL";
    }

    // --- Chrome -----------------------------------------------------------

    private void ApplyTheme()
    {
        Panel.Background = _theme.NotesBackground;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        HeaderRule.Background = _theme.Separator;
        TitleText.Foreground = _theme.Section;
        TitleText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);

        // The level line large, as the headline it is; its points and the
        // hint under it smaller. The bar's fill takes the rank's metal in Fill.
        CountText.Foreground = _theme.NotesText;
        CountText.FontSize = UiTheme.PanelFontSize + 6;
        LevelText.Foreground = _theme.Label;
        LevelText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 2);
        CountHint.Foreground = _theme.Header;
        CountHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);
        CountTrack.Background = _theme.Shade(0x1F);
        CountFill.Background = _theme.Accent;

        Foreground = _theme.Label;
        FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        FontFamily = _theme.PanelFont;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => HandleHeaderMouseDown(e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Hidden rather than closed, so it can be shown again later.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacementNow();
        if (_allowClose)
            return;
        e.Cancel = true;
        HideSmoothly();
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

    private void SavePlacementNow()
    {
        if (!_placementSave.IsEnabled)
            return;
        _placementSave.Stop();
        if (WindowState != WindowState.Normal || !IsVisible)
            return;

        _settings.AchievementsLeft = Left;
        _settings.AchievementsTop = Top;
        _settings.AchievementsWidth = Width;
        _settings.AchievementsHeight = Height;
        _settings.Save();
    }
}
