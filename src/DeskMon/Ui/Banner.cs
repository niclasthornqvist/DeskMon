using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskMon.Notes;

namespace DeskMon.Ui;

/// <summary>
/// A picture behind the top of a panel window, fading out into the panel
/// before the window's content starts - the sessions window's banner, for
/// the other windows. The open game's picture in the notes and pictures
/// windows, a drawn motif in the rest.
///
/// Put in by code rather than in each window's XAML: it takes the panel's
/// content and lays itself underneath, so a window needs one line to have one.
/// It never takes a click.
/// </summary>
public sealed class Banner
{
    private readonly Border _layer;
    private double _strength;

    /// <summary>
    /// How strongly the picture shows, changed while it is up - for the
    /// widget's, which has a setting. Straight to the new strength, no fade:
    /// it follows a slider being dragged.
    /// </summary>
    public double Strength
    {
        get => _strength;
        set
        {
            _strength = Math.Clamp(value, 0, 1);
            if (_layer.IsVisible && _layer.Background is not null)
            {
                _layer.BeginAnimation(UIElement.OpacityProperty, null);
                _layer.Opacity = _strength;
            }
        }
    }

    /// <summary>Counts what was asked for, so a slow lookup for a game already left behind doesn't land.</summary>
    private int _ticket;

    private static readonly TimeSpan FadeOutTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(700);

    /// <param name="panel">The window's rounded Panel border.</param>
    /// <param name="height">
    /// How far down the picture reaches before it has faded away - the
    /// window's header and toolbars, not its text. Null for the whole window,
    /// for the widget, whose height changes as its sections fold.
    /// </param>
    /// <param name="strength">How strongly it shows at the top: enough to be seen, faint enough that the text over it reads.</param>
    /// <param name="drift">
    /// For the widget: the picture at a fixed size, tiled, and drifting slowly
    /// - see <see cref="Brush"/>. Stretched to fill, as the windows have it,
    /// it was zoomed right in on the wide strip and jumped to a new zoom
    /// whenever a list unfolded and the widget changed width.
    /// </param>
    public Banner(Border panel, double? height, double strength = 0.3, bool drift = false)
    {
        _strength = strength;
        _drift = drift;

        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0, 0, 0), 0.45));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
        mask.Freeze();

        // Out over the panel's padding, to its edges; inside its 1 px border,
        // so rounded a pixel less than the panel - whatever the theme makes that.
        var padding = panel.Padding;
        _layer = new Border
        {
            VerticalAlignment = height is null ? VerticalAlignment.Stretch : VerticalAlignment.Top,
            Height = height ?? double.NaN,
            Margin = new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom),
            IsHitTestVisible = false,
            Opacity = 0,
            OpacityMask = mask,
        };

        void FitCorners()
        {
            double inner = Math.Max(0, panel.CornerRadius.TopLeft - 1);
            _layer.CornerRadius = height is null ? new CornerRadius(inner) : new CornerRadius(inner, inner, 0, 0);
        }
        FitCorners();
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(Border.CornerRadiusProperty, typeof(Border))
            .AddValueChanged(panel, (_, _) => FitCorners());

        // Every opening is the same: one fade in, from nothing. Put out as
        // the window hides, so a picture changed while it is hidden is just
        // swapped (see Show) and never leaves a fade half done for the
        // opening to cut short - which used to leave the old picture up.
        _layer.IsVisibleChanged += (_, e) =>
        {
            _layer.BeginAnimation(UIElement.OpacityProperty, null);
            _layer.Opacity = 0;

            if (e.NewValue is true)
            {
                _openedAt = DateTime.UtcNow;
                if (_layer.Background is not null)
                    FadeIn(OpenTime, OpenDelay);
            }
        };

        // Put out while the window is still on screen, as it is about to hide -
        // see PanelWindow.HideSmoothly. The widget isn't a PanelWindow and
        // hides only during games, so it goes without.
        bool hooked = false;
        void Hook()
        {
            if (hooked || Window.GetWindow(panel) is not PanelWindow window)
                return;
            hooked = true;
            window.Hiding += PutOut;
        }
        Hook();
        if (!hooked)
            panel.Loaded += (_, _) => Hook();

        var content = panel.Child;
        panel.Child = null;
        var layers = new Grid();
        layers.Children.Add(_layer);
        if (content is not null)
            layers.Children.Add(content);
        panel.Child = layers;
    }

    /// <summary>A drawn motif - for a window that isn't about one game.</summary>
    public void Show(GameArt.Motif motif)
    {
        _ticket++;
        Show(GameArt.Drawn(motif));
    }

    /// <summary>
    /// The game's picture, looked up as the sessions window does - Steam's
    /// art, a screenshot, the store - or <paramref name="fallback"/> drawn
    /// when there is none. The last picture fades out at once when this one
    /// has to be looked up, rather than staying over the new game meanwhile.
    /// </summary>
    public async void ShowGame(string game, AppSettings settings, string screenshotFolder, GameArt.Motif fallback)
    {
        int ticket = ++_ticket;
        var lookup = GameArt.ForAsync(game, GamePlaytime.RememberedSteamAppId(game, settings),
                                      screenshotFolder, settings.OnlineGameArt);
        if (!lookup.IsCompleted)
        {
            // Hidden, the old picture goes at once: opening the window now
            // must not fade in the last game's while this one is found.
            if (_layer.IsVisible)
                FadeOut(null);
            else
                _layer.Background = null;
        }

        var art = await lookup;
        if (ticket == _ticket)
            Show(art.Picture ?? GameArt.Drawn(fallback));
    }

    /// <summary>
    /// The old picture out, then the new one in - never a swap in one frame.
    /// Nothing happens when it is the same picture again.
    /// </summary>
    private void Show(ImageSource picture)
    {
        if (_layer.Background is ImageBrush shown && ReferenceEquals(shown.ImageSource, picture))
        {
            if (_layer.IsVisible && _layer.Opacity < 0.01)
                FadeIn();   // faded right out while it was looked up again
            return;
        }

        // Hidden: swapped without a fade; the opening fades it in.
        if (!_layer.IsVisible)
        {
            _layer.Background = Brush(picture);
            return;
        }

        if (_layer.Background is null || _layer.Opacity < 0.01)
            Reveal(picture);
        else
            FadeOut(() => Reveal(picture));
    }

    private void Reveal(ImageSource picture)
    {
        _layer.Background = Brush(picture);

        // A picture that arrives within the opening's delay - still being
        // looked up when the window opened - waits out the rest of it.
        var since = DateTime.UtcNow - _openedAt;
        var left = OpenDelay - since;
        FadeIn(since < OpenDelay + OpenTime ? OpenTime : null,
               after: left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    /// <summary>When the window was last opened, for <see cref="Reveal"/>.</summary>
    private DateTime _openedAt;

    /// <summary>To nothing at once, no fade - the window is closing.</summary>
    private void PutOut()
    {
        _layer.BeginAnimation(UIElement.OpacityProperty, null);
        _layer.Opacity = 0;
    }

    private readonly bool _drift;

    /// <summary>How wide one copy of the picture is when drifting, in pixels - half its own size, so a wide strip shows much more of it than a stretched fill did.</summary>
    private const double DriftWidth = 300;

    /// <summary>
    /// How long one full loop of the drift takes. Slow enough to be noticed
    /// only when you look for it - a moving picture behind the numbers must
    /// never pull the eye away from them.
    /// </summary>
    private static readonly TimeSpan DriftLoop = TimeSpan.FromMinutes(4);

    private ImageBrush Brush(ImageSource picture)
    {
        if (!_drift)
            return new(picture) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };

        // Fixed in pixels rather than relative to the widget, so the picture
        // keeps its size and place when the widget changes width. Tiled with
        // every other copy mirrored, so the copies meet without a seam and
        // the strip is covered however wide it gets.
        double width = DriftWidth;
        double height = picture.Width > 0 ? DriftWidth * picture.Height / picture.Width : DriftWidth;
        var brush = new ImageBrush(picture)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.FlipXY,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, width, height),
        };

        // Two copies along and down is where the mirrored pattern repeats, so
        // the loop starts again on the same picture and never visibly jumps.
        // Ten frames a second: the movement is under 4 px a second, so more
        // would only redraw the widget - which sits over games - for nothing.
        var loop = new RectAnimation(new Rect(0, 0, width, height),
                                     new Rect(-2 * width, -2 * height, width, height), DriftLoop)
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(loop, 10);

        // The last picture's clock stopped first: a running clock stays in
        // WPF's timing tree whether or not its brush is still drawn, so each
        // new picture would have left one more ticking ten times a second.
        _driftClock?.Controller?.Stop();

        // Through a clock rather than BeginAnimation, so it can be paused in
        // place during a game and carry on from the same spot afterwards.
        _driftClock = loop.CreateClock();
        brush.ApplyAnimationClock(TileBrush.ViewportProperty, _driftClock);
        if (!_drifting)
            _driftClock.Controller?.Pause();
        return brush;
    }

    private AnimationClock? _driftClock;
    private bool _drifting = true;

    /// <summary>
    /// Whether the drift moves. Off while a game runs: every step redraws the
    /// whole see-through widget on the CPU and has the compositor take it
    /// again, which is cheap on the desktop but not wanted on top of a game.
    /// Paused rather than stopped, so the picture stays where it was.
    /// </summary>
    public bool Drifting
    {
        get => _drifting;
        set
        {
            if (_drifting == value)
                return;

            _drifting = value;
            if (value)
                _driftClock?.Controller?.Resume();
            else
                _driftClock?.Controller?.Pause();
        }
    }

    private void FadeIn(TimeSpan? time = null, TimeSpan after = default)
        => _layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(_strength, time ?? FadeInTime)
        {
            BeginTime = after,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    /// <summary>The fade in for a window being shown: slower than a change of picture, as an entrance.</summary>
    private static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(1600);

    /// <summary>
    /// How long an opening waits before the picture starts to come in, so the
    /// window is up and drawn first and the art then fades into it - rather
    /// than both arriving in the same instant.
    /// </summary>
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// To nothing from wherever it is. <paramref name="then"/> runs at the end,
    /// unless another fade took over first - WPF drops the replaced animation
    /// without its Completed - so quick changes only ever show the last.
    /// </summary>
    private void FadeOut(Action? then)
    {
        var fade = new DoubleAnimation(0, FadeOutTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        if (then is not null)
            fade.Completed += (_, _) => then();
        _layer.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
