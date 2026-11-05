using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DeskMon.Notes;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// The card that slides in beside the widget when an achievement unlocks:
/// its badge playing the unlock, "ACHIEVEMENT UNLOCKED" and its name.
///
/// Only shown when no game is running - the widget holds it back until the
/// game ends - so it never sits over play. Doesn't take the keyboard, goes by
/// itself after a few seconds (not while the mouse is on it), and opens the
/// achievements window when clicked. Several unlocks come one at a time.
/// </summary>
internal sealed class AchievementToast : Window
{
    /// <summary>How long it stays before going by itself.</summary>
    private static readonly TimeSpan Stay = TimeSpan.FromSeconds(7);

    /// <summary>The window's room round the card for its shadow, as SessionNotePrompt has.</summary>
    private const double ShadowMargin = 8;
    private const double Gap = 6;

    /// <summary>The badge, which plays its unlock as the card arrives - null on a player level-up card.</summary>
    private readonly Badge? _badge;

    /// <summary>The level's emblem on a player level-up card, which swells as the card arrives.</summary>
    private readonly FrameworkElement? _emblem;
    private readonly TranslateTransform _slide = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private Rect _near;
    private double _below;
    private bool _leaving;

    /// <summary>Clicked: the widget opens the achievements window.</summary>
    public event Action? Opened;

    /// <param name="near">The widget's place on screen, to stand beside.</param>
    /// <param name="below">How far down to stand, to keep clear of a session-note card already there.</param>
    /// <param name="more">
    /// Above 0, the card stands for this many unlocks at once, shown with the
    /// first of them's badge: a burst of unlocks gets a few cards and then
    /// this one, not a card each - see MainWindow.QueueToasts.
    /// </param>
    public AchievementToast(UiTheme theme, Achievements.Unlock unlock, Rect near, double below, int more = 0)
        : this(theme, near, below)
    {
        var achievement = unlock.Achievement;
        _badge = new Badge(achievement.Badge, 58, unlock.Level, achievement.Levels.Length, theme.IsLight,
                           achievement.BadgeLabel(unlock.Level)) { Opacity = 0 };

        // A first level is the achievement unlocked; a later one its tier,
        // in that tier's metal - the same colour as the badge's new rim. It
        // said LEVEL UP, which is now the player level's own card.
        bool tierUp = unlock.Level > 1 && more == 0;
        Build(theme, _badge,
              more > 0 ? $"AND {more} MORE" : tierUp ? $"{Achievements.LevelNames[unlock.Level - 1].ToUpperInvariant()} TIER UNLOCKED" : "ACHIEVEMENT UNLOCKED",
              tierUp ? new SolidColorBrush(Levels.Metal(unlock.Level)) : theme.Accent,
              more > 0 ? $"{more} more unlocked" : achievement.NameFor(unlock.Level),
              more > 0 ? $"{achievement.NameFor(unlock.Level)} and the rest - click to see them all." : achievement.HowFor(unlock.Level),
              achievement.Badge.From);
    }

    /// <summary>
    /// The player level gone up: the new level's emblem, its number and rank,
    /// and how far to the next - after the unlock cards that raised it.
    /// </summary>
    /// <param name="points">The points now, for how many more the next level takes.</param>
    public AchievementToast(UiTheme theme, int playerLevel, int points, Rect near, double below)
        : this(theme, near, below)
    {
        int tier = Achievements.RankTier(playerLevel);
        string rank = Achievements.RankName(playerLevel);
        bool top = playerLevel >= Achievements.TopLevel;
        int from = Achievements.PointsForLevel(playerLevel), to = Achievements.PointsForLevel(playerLevel + 1);
        double toNext = top ? 1 : (double)(points - from) / Math.Max(1, to - from);
        _emblem = new RankEmblem(playerLevel, rank, toNext, tier, theme.Shade(0x1F), size: 62)
        {
            RenderTransformOrigin = new Point(0.5, 0.45),
            RenderTransform = new ScaleTransform(1, 1),
        };

        // A new rank said as such - every second level brings one.
        bool newRank = playerLevel > 1 && Achievements.RankName(playerLevel - 1) != rank;
        Build(theme, _emblem, "LEVEL UP", new SolidColorBrush(Levels.Metal(tier)),
              $"Level {playerLevel}  ·  {rank}",
              top ? "The top level - every tier of every achievement."
                  : $"{(newRank ? "A new rank. " : "")}{to - points} more points to level {playerLevel + 1}.",
              Levels.Metal(tier));
    }

    /// <summary>The window itself, the same for both kinds of card; Build fills it.</summary>
    private AchievementToast(UiTheme theme, Rect near, double below)
    {
        _near = near;
        _below = below;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "DeskMon - achievement unlocked";
        FontFamily = theme.PanelFont;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Cursor = Cursors.Hand;

        Opacity = 0;
        Loaded += (_, _) => Arrive();

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = Stay };
        _timer.Tick += (_, _) => Leave();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_leaving) _timer.Start(); };
        MouseLeftButtonUp += (_, _) =>
        {
            Opened?.Invoke();
            Leave();
        };
    }

    /// <summary>
    /// The card: the picture on the left, and beside it a small line in its
    /// colour, the name big, and a line under it. Edged in the picture's own
    /// colour, so the card belongs to it.
    /// </summary>
    private void Build(UiTheme theme, FrameworkElement picture, string kicker, Brush kickerBrush, string title, string line, Color edge)
    {
        var words = new StackPanel { Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 230 };
        words.Children.Add(new TextBlock
        {
            Text = kicker, Foreground = kickerBrush, FontWeight = FontWeights.SemiBold,
            FontSize = Math.Max(9, UiTheme.PanelFontSize - 3),
        });
        words.Children.Add(new TextBlock
        {
            Text = title, Foreground = theme.NotesText, FontWeight = FontWeights.SemiBold,
            FontSize = UiTheme.PanelFontSize + 2, Margin = new Thickness(0, 1, 0, 1),
        });
        words.Children.Add(new TextBlock
        {
            Text = line, Foreground = theme.Label, TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(9, UiTheme.PanelFontSize - 2),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(picture);
        row.Children.Add(words);

        Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Padding = new Thickness(12, 10, 14, 10),
            CornerRadius = new CornerRadius(10),
            Background = theme.Popup,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, edge.R, edge.G, edge.B)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
            RenderTransform = _slide,
            Child = row,
        };
    }

    /// <summary>Moves with the widget, as the note card does, so it stays at its side.</summary>
    public void Follow(Rect near, double below)
    {
        _near = near;
        _below = below;
        if (IsLoaded)
            Place();
    }

    /// <summary>Slid in from the widget's side, faded in, then the badge's unlock.</summary>
    private void Arrive()
    {
        Place();
        double from = _onRight ? -28 : 28;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        _badge?.PlayUnlock(TimeSpan.FromMilliseconds(180));
        // The emblem swells and settles, as it does in the window on a new level.
        if (_emblem?.RenderTransform is ScaleTransform grow)
        {
            var pulse = new DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(900))
            {
                BeginTime = TimeSpan.FromMilliseconds(180),
                EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut },
            };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        if (!IsMouseOver)
            _timer.Start();
    }

    /// <summary>Faded and slid back out, then closed - which lets the widget show the next one.</summary>
    private void Leave()
    {
        if (_leaving)
            return;
        _leaving = true;
        _timer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => Close();
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, _onRight ? -16 : 16, TimeSpan.FromMilliseconds(260)));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Whether it stands to the widget's right - it slides in from that side.</summary>
    private bool _onRight;

    /// <summary>
    /// To the widget's left, or its right when the widget is against the left
    /// edge of its screen - the note card's rule - and kept on that screen.
    /// </summary>
    private void Place()
    {
        var area = WorkAreaAround(_near);
        double width = ActualWidth, height = ActualHeight;

        double left = _near.Left - Gap - (width - ShadowMargin);
        _onRight = left + ShadowMargin < area.Left;
        if (_onRight)
            left = _near.Right + Gap - ShadowMargin;
        double top = _near.Top - ShadowMargin + _below;

        Left = Math.Clamp(left, area.Left - ShadowMargin, Math.Max(area.Left - ShadowMargin, area.Right - width + ShadowMargin));
        Top = Math.Clamp(top, area.Top - ShadowMargin, Math.Max(area.Top - ShadowMargin, area.Bottom - height + ShadowMargin));
    }

    /// <summary>The work area of the widget's screen, in WPF's units - as SessionNotePrompt finds it.</summary>
    private Rect WorkAreaAround(Rect near)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        var centre = target.TransformToDevice.Transform(new Point(near.Left + near.Width / 2, near.Top + near.Height / 2));
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)centre.X, (int)centre.Y)).WorkingArea;
        return new Rect(target.TransformFromDevice.Transform(new Point(area.Left, area.Top)),
                        target.TransformFromDevice.Transform(new Point(area.Right, area.Bottom)));
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
