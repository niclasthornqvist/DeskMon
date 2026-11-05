using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DeskMon.Ui;

/// <summary>The outline of a badge. Six, spread over the twenty, so neighbours rarely share one.</summary>
internal enum BadgeShape { Circle, Hexagon, Shield, Octagon, Square, Diamond }

/// <param name="From">The colour at the top left, lighter.</param>
/// <param name="To">The colour at the bottom right, deeper.</param>
/// <param name="Glyph">The symbol, as stroked path data on a 24 by 24 grid - see <see cref="Glyphs"/>.</param>
/// <param name="Label">A few letters drawn under the symbol instead of beside it - Century's "100".</param>
internal sealed record BadgeLook(BadgeShape Shape, Color From, Color To, string Glyph, string? Label = null);

/// <summary>
/// The symbols on the badges, drawn as strokes on a 24 by 24 grid like the
/// toolbar's icons - simple enough to read at the toast's 56 px and the
/// window's 72, and sharp at both, since they are vectors.
/// </summary>
internal static class Glyphs
{
    public const string Hourglass = "M7,3 H17 M7,21 H17 M8,3 V6 L12,12 L8,18 V21 M16,3 V6 L12,12 L16,18 V21 M10,18 H14";
    public const string Moon = "M15,4 A8,8 0 1 0 20,15 A6.5,6.5 0 1 1 15,4 Z M18.5,4 V7 M17,5.5 H20";
    public const string Stopwatch = "M5,13.5 A7,7 0 1 1 19,13.5 A7,7 0 1 1 5,13.5 M12,13.5 V9.5 M10,3 H14 M12,3 V6.5 M18,6.5 L19.5,5";
    public const string Pages = "M5,8 H15 V21 H5 Z M8,8 V5 H18 V18 H15 M8,12 H12 M8,15 H12";
    public const string Flag = "M6,21 V3.5 M6,4 H17 L14,8 L17,12 H6";
    public const string Sunrise = "M3,17 H21 M7,17 A5,5 0 0 1 17,17 M12,6 V9 M5,10 L7,12 M19,10 L17,12 M6,21 H18";
    public const string Swords = "M5,5 L18,18 M19,5 L6,18 M5,5 H8.5 M5,5 V8.5 M19,5 H15.5 M19,5 V8.5 M15,20.5 L20.5,15 M3.5,15 L9,20.5";
    public const string Flame = "M12,3 C16,8 18,10.5 18,14.5 A6,6 0 0 1 6,14.5 C6,11.5 7.5,9.5 9,7.5 C9.5,10 10.5,11.5 12,12 C12.5,9 12.5,6 12,3 Z";
    public const string Compass = "M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 M15.5,8.5 L13.4,13.4 L8.5,15.5 L10.6,10.6 Z";
    public const string Heart = "M12,20 C5,15 3,12 3,9 A4.5,4.5 0 0 1 12,7 A4.5,4.5 0 0 1 21,9 C21,12 19,15 12,20 Z";
    public const string Wave = "M3,9 C5.5,5.5 8.5,5.5 11,9 S16.5,12.5 21,9 M3,15 C5.5,11.5 8.5,11.5 11,15 S16.5,18.5 21,15";
    public const string Snowflake = "M12,3 V21 M4.2,7.5 L19.8,16.5 M19.8,7.5 L4.2,16.5 M10,4.5 L12,6.5 L14,4.5 M10,19.5 L12,17.5 L14,19.5";
    public const string Camera = "M3,8 H7 L9,5 H15 L17,8 H21 V19 H3 Z M8.5,13 A3.5,3.5 0 1 1 15.5,13 A3.5,3.5 0 1 1 8.5,13";
    public const string Quill = "M20,3 C13,4 8,9 6,18 M20,3 C19,10 13,15 7.5,16 M4,21 L6.5,17.5 M11,11 L14,11";
    public const string Signal = "M5,20 V16 M10,20 V12 M15,20 V8 M20,20 V4";
    public const string Laurel = "M7,4 C3,9 4,16 10,20 M17,4 C21,9 20,16 14,20 M5,9 L3,8 M5,13 L3,13 M7,17 L5,18 M19,9 L21,8 M19,13 L21,13 M17,17 L19,18";
    public const string Crown = "M4,17 L3,7 L8,11 L12,5 L16,11 L21,7 L20,17 Z M4,20.5 H20";
    public const string Return = "M19,12 A7,7 0 1 1 15,5.7 M15.5,2 L15.5,6 L11.5,6";
    // The pips as hair-short lines: with round caps they draw as dots, where
    // a line of no length at all may not be drawn.
    public const string Dice = "M4,4 H20 V20 H4 Z M8,8 L8.1,8 M16,8 L16.1,8 M12,12 L12.1,12 M8,16 L8.1,16 M16,16 L16.1,16";
    public const string Leaf = "M5,19 C5,9 11,5 20,4 C19,13 15,19 5,19 Z M5,19 L13,11";
    public const string Calendar = "M4,6 H20 V20 H4 Z M4,10 H20 M8,3 V7 M16,3 V7 M8,14 L8.1,14 M12,14 L12.1,14 M16,14 L16.1,14 M8,17 L8.1,17";
    public const string Pulse = "M3,12 H7 L9,7 L12,17 L14,10 L16,12 H21";
    public const string Bolt = "M13,3 L6,13 H11 L10,21 L18,10 H13 Z";
    public const string Album = "M4,8 H16 V20 H4 Z M7,8 V5 H19 V17 H16 M5.5,18 L8.5,14 L10.5,16 L12.5,13 L14.5,18";
}

/// <summary>
/// The level colours - bronze, silver, gold, platinum - for a badge's rim and
/// the pips under it: the badge's own colour says which achievement, the
/// metal says how far.
/// </summary>
internal static class Levels
{
    private static readonly Color[] Metals =
    {
        Color.FromRgb(0xCD, 0x7F, 0x32),   // bronze
        Color.FromRgb(0xC9, 0xD1, 0xDC),   // silver
        Color.FromRgb(0xF6, 0xC4, 0x45),   // gold
        Color.FromRgb(0xB9, 0xF2, 0xFF),   // platinum, icy so it reads apart from silver
    };

    /// <param name="level">1 for the first level.</param>
    public static Color Metal(int level) => Metals[Math.Clamp(level, 1, Metals.Length) - 1];
}

/// <summary>
/// A badge: its shape filled with its colours, a ring inside the rim, a soft
/// highlight over the top, and its symbol in white - or, locked, the same in
/// a flat grey, so what it will look like is there to be earned.
///
/// Drawn on a 100 by 100 canvas scaled to the size asked, so the strokes keep
/// their weight relative to the badge at any size. Carries the parts its
/// unlock animation moves - see <see cref="PlayUnlock"/>.
/// </summary>
internal sealed class Badge : Grid
{
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly RotateTransform _tilt = new(0);
    private readonly Ellipse _burst;
    private readonly Rectangle _sweep;
    private readonly TranslateTransform _sweepAt = new(-140, 0);

    /// <param name="level">The level reached, 0 for locked - drawn grey.</param>
    /// <param name="levels">How many the achievement has: with one, the rim is the badge's own colour, not a metal.</param>
    /// <param name="label">The figure to draw in place of the look's own Label - the level's target; see Achievement.BadgeLabel.</param>
    public Badge(BadgeLook look, double size, int level, int levels, bool lightTheme, string? label = null)
    {
        bool unlocked = level > 0;
        label ??= look.Label;
        Width = Height = size;
        var canvas = new Canvas { Width = 100, Height = 100 };
        var shape = Outline(look.Shape);

        // Behind the badge, for the unlock: a burst of the badge's colour
        // that grows out past its edge and fades.
        _burst = new Ellipse
        {
            Width = 140, Height = 140, Opacity = 0,
            Fill = new RadialGradientBrush(Color.FromArgb(0xCC, look.From.R, look.From.G, look.From.B),
                                           Color.FromArgb(0, look.To.R, look.To.G, look.To.B)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_burst, -20);
        Canvas.SetTop(_burst, -20);
        canvas.Children.Add(_burst);

        // Locked: a flat shade of the panel, darker or lighter by the theme,
        // with the symbol faint on it.
        Color grey = lightTheme ? Color.FromRgb(0xD5, 0xDA, 0xE2) : Color.FromRgb(0x2A, 0x2F, 0x3A);
        Color greyRim = lightTheme ? Color.FromRgb(0xB4, 0xBC, 0xC8) : Color.FromRgb(0x3C, 0x43, 0x50);
        Brush fill = unlocked
            ? new LinearGradientBrush(look.From, look.To, new Point(0.15, 0), new Point(0.85, 1))
            : new SolidColorBrush(grey);
        // The rim is the level: bronze to platinum, a little heavier than the
        // plain rim so the metal reads at the toast's size. Gold and platinum
        // get a second thin ring outside it as well, so the top two stand
        // apart from the first two by more than a colour.
        bool metal = unlocked && levels > 1;
        Color rimColor = !unlocked ? greyRim : metal ? Levels.Metal(level) : Darker(look.To, 0.35);
        if (metal && level >= 3)
        {
            var outer = shape.Clone();
            outer.Transform = new ScaleTransform(1.05, 1.05, 50, 50);
            canvas.Children.Add(new Path { Data = outer, Stroke = new SolidColorBrush(Color.FromArgb(0xB0, rimColor.R, rimColor.G, rimColor.B)), StrokeThickness = 1.6 });
        }
        canvas.Children.Add(new Path { Data = shape, Fill = fill, Stroke = new SolidColorBrush(rimColor), StrokeThickness = metal ? 4.5 : 3 });

        // The ring inside the rim: the same outline at 84 %, in a thin light
        // line, which makes it read as a struck medal rather than a sticker.
        var ring = shape.Clone();
        ring.Transform = new ScaleTransform(0.84, 0.84, 50, 50);
        canvas.Children.Add(new Path
        {
            Data = ring,
            Stroke = new SolidColorBrush(Color.FromArgb(unlocked ? (byte)0x55 : (byte)0x30, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.6,
        });

        if (unlocked)
        {
            // Light catching the top half - clipped to the shape, so it never
            // spills past the rim.
            var highlight = new Ellipse
            {
                Width = 120, Height = 70,
                Fill = new LinearGradientBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90),
                Clip = Clipped(shape, 10, 8),   // placed at (-10, -8), so the shape sits that much further in
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(highlight, -10);
            Canvas.SetTop(highlight, -8);
            canvas.Children.Add(highlight);
        }

        // The symbol, centred in the middle 42 units - a little higher when
        // a label sits under it. The level's plaque hangs below the shape
        // now (see the end), so the symbol no longer moves up for it.
        bool plaque = metal;
        double top = label is null ? 29 : 22;
        var glyph = Geometry.Parse(look.Glyph).Clone();
        glyph.Transform = new MatrixTransform(1.75, 0, 0, 1.75, 29, top);
        canvas.Children.Add(new Path
        {
            Data = glyph,
            Stroke = new SolidColorBrush(unlocked ? Colors.White : Color.FromArgb(0x70, greyRim.R, greyRim.G, greyRim.B)),
            StrokeThickness = 4.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = unlocked ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.35 } : null,
        });

        if (label is { } figure)
        {
            var text = new TextBlock
            {
                Text = figure, Width = 100, TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Bahnschrift SemiBold, Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 19,
                Foreground = new SolidColorBrush(unlocked ? Colors.White : Color.FromArgb(0x90, greyRim.R, greyRim.G, greyRim.B)),
            };
            Canvas.SetTop(text, 58);
            canvas.Children.Add(text);
        }

        // The shine that crosses the badge as it unlocks: a slanted band of
        // light, clipped to the shape, parked off to the left until then.
        _sweep = new Rectangle
        {
            Width = 60, Height = 140, Opacity = 0,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0),
                new(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 0.5),
                new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1),
            }, new Point(0, 0.5), new Point(1, 0.5)),
            RenderTransform = new TransformGroup { Children = { new SkewTransform(-20, 0), _sweepAt } },
            IsHitTestVisible = false,
        };
        var sweepLayer = new Canvas { Width = 100, Height = 100, Clip = shape, IsHitTestVisible = false };
        Canvas.SetTop(_sweep, -20);
        sweepLayer.Children.Add(_sweep);
        canvas.Children.Add(sweepLayer);

        // A badge with levels is drawn a tenth smaller, held to the top, so
        // the level's plaque can hang across its bottom edge like a medal's
        // ribbon. Laid inside the shape, it sat flush with the flat bottom of
        // an octagon or a square - Early Bird, Century - and the badge looked
        // cut off below the stars. The same size locked or not, so picking
        // a level doesn't make the badge jump.
        FrameworkElement drawn = canvas;
        if (levels > 1)
        {
            canvas.RenderTransform = new ScaleTransform(0.9, 0.9, 50, 44);
            var medal = new Canvas { Width = 100, Height = 100 };
            medal.Children.Add(canvas);
            if (plaque)
                AddPlaque(medal, level);
            drawn = medal;
        }

        Children.Add(new Viewbox { Child = drawn, Stretch = Stretch.Uniform });
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _pop, _tilt } };
    }

    /// <summary>
    /// The unlock: the badge pops in from small with a little tilt and a
    /// spring past full size, a burst of its colour flares out behind it, and
    /// a shine crosses its face. Under a second all told - long enough to be
    /// noticed, short enough not to hold anything up.
    /// </summary>
    /// <param name="delay">For several at once, so they go off one after another.</param>
    public void PlayUnlock(TimeSpan delay = default)
    {
        var start = delay;
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = start });
        var grow = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(620)) { BeginTime = start, EasingFunction = spring };
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _tilt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(620)) { BeginTime = start, EasingFunction = spring });

        var flare = start + TimeSpan.FromMilliseconds(160);
        var burstScale = (ScaleTransform)_burst.RenderTransform;
        var spread = new DoubleAnimation(0.6, 1.55, TimeSpan.FromMilliseconds(900)) { BeginTime = flare, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        burstScale.BeginAnimation(ScaleTransform.ScaleXProperty, spread);
        burstScale.BeginAnimation(ScaleTransform.ScaleYProperty, spread);
        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = flare };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new QuadraticEase()));
        _burst.BeginAnimation(OpacityProperty, fade);

        var shine = start + TimeSpan.FromMilliseconds(480);
        _sweep.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 1, TimeSpan.Zero) { BeginTime = shine });
        _sweepAt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-90, 140, TimeSpan.FromMilliseconds(650)) { BeginTime = shine, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>
    /// The level said on the badge itself: a plaque of its metal across the
    /// bottom of the rim, with one star for Bronze up to four for Platinum.
    /// The rim's colour alone left silver and platinum, and bronze and gold
    /// at a small size, hard to tell apart; a count of stars is not.
    /// </summary>
    private static void AddPlaque(Canvas canvas, int level)
    {
        var metal = Levels.Metal(level);
        // Sized for the window's 64 px badge: at 17 units high the stars
        // were specks, and at 20 still small to count at a glance. Platinum's
        // four, 76 wide, still fit inside the badge's width.
        const double height = 22, starGap = 16, starRadius = 7.2;
        double width = 12 + level * starGap;
        // Centred on the shrunken shape's bottom edge, at 90 (44 + 52 x 0.9):
        // half on the badge, half below it, whatever the shape.
        double left = 50 - width / 2, top = 90 - height / 2;

        var plate = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(height / 2),
            Background = new LinearGradientBrush(Lighter(metal, 0.35), Darker(metal, 0.15), 90),
            BorderBrush = new SolidColorBrush(Darker(metal, 0.45)),
            BorderThickness = new Thickness(1.4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.45 },
        };
        Canvas.SetLeft(plate, left);
        Canvas.SetTop(plate, top);
        canvas.Children.Add(plate);

        // The stars dark on the metal, so they show on the pale silver and
        // platinum as well as on bronze.
        var stars = new GeometryGroup();
        for (int i = 0; i < level; i++)
            stars.Children.Add(Star(left + 6 + starGap / 2 + i * starGap, top + height / 2 + 0.4, starRadius));   // a hair low: a star's weight sits above its middle
        canvas.Children.Add(new Path { Data = stars, Fill = new SolidColorBrush(Darker(metal, 0.62)) });
    }

    /// <summary>A five-pointed star round (x, y), point up.</summary>
    private static Geometry Star(double x, double y, double radius)
    {
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        for (int i = 0; i < 10; i++)
        {
            double r = i % 2 == 0 ? radius : radius * 0.45;
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            var point = new Point(x + r * Math.Cos(angle), y + r * Math.Sin(angle));
            if (i == 0)
                figure.StartPoint = point;
            else
                figure.Segments.Add(new LineSegment(point, true));
        }
        return new PathGeometry(new[] { figure });
    }

    internal static Color Lighter(Color c, double by)
        => Color.FromRgb((byte)(c.R + (255 - c.R) * by), (byte)(c.G + (255 - c.G) * by), (byte)(c.B + (255 - c.B) * by));

    /// <summary>On the 100 by 100 grid, a little in from the edge so the rim's stroke isn't cut off.</summary>
    private static Geometry Outline(BadgeShape shape) => Geometry.Parse(shape switch
    {
        BadgeShape.Hexagon => "M50,4 L90,27 L90,73 L50,96 L10,73 L10,27 Z",
        BadgeShape.Shield => "M50,4 L88,15 V47 C88,71 71,87 50,96 C29,87 12,71 12,47 V15 Z",
        BadgeShape.Octagon => "M32,4 H68 L96,32 V68 L68,96 H32 L4,68 V32 Z",
        BadgeShape.Square => "M26,6 H74 A20,20 0 0 1 94,26 V74 A20,20 0 0 1 74,94 H26 A20,20 0 0 1 6,74 V26 A20,20 0 0 1 26,6 Z",
        BadgeShape.Diamond => "M50,3 L97,50 L50,97 L3,50 Z",
        _ => "M50,4 A46,46 0 1 1 49.99,4 Z",
    });

    /// <summary>The shape as a clip for an element placed at (left, top) on the grid - clips are in the element's own space.</summary>
    private static Geometry Clipped(Geometry shape, double left, double top)
    {
        var clip = shape.Clone();
        clip.Transform = new TranslateTransform(left, top);
        return clip;
    }

    internal static Color Darker(Color c, double by)
        => Color.FromRgb((byte)(c.R * (1 - by)), (byte)(c.G * (1 - by)), (byte)(c.B * (1 - by)));
}

/// <summary>
/// The player's level, big, for the achievements window's top right corner:
/// a crest in the rank's metal with the level number on it, a ring round it
/// that fills towards the next level, and the rank's name under it.
///
/// The metal moves from bronze through silver and gold to platinum as the
/// level rises (Achievements.RankTier), so how far along you are reads from
/// the colour before the number is read.
/// </summary>
internal sealed class RankEmblem : StackPanel
{
    /// <param name="level">The player level, 1 to 20.</param>
    /// <param name="toNext">How far towards the next level, 0 to 1 - full at the top level.</param>
    /// <param name="tier">The metal, 1 for bronze to 4 for platinum.</param>
    /// <param name="track">The empty part of the ring, in the window's own shade.</param>
    public RankEmblem(int level, string rank, double toNext, int tier, Brush track, double size = 104)
    {
        var metal = Levels.Metal(tier);
        var canvas = new Canvas { Width = 100, Height = 100 };

        // The ring: the whole circle faint, the share done in the metal,
        // starting at the top and going round clockwise.
        canvas.Children.Add(new Ellipse { Width = 92, Height = 92, Stroke = track, StrokeThickness = 5 });
        Canvas.SetLeft(canvas.Children[^1], 4);
        Canvas.SetTop(canvas.Children[^1], 4);
        double share = Math.Clamp(toNext, 0, 1);
        if (share >= 0.999)
        {
            var full = new Ellipse { Width = 92, Height = 92, Stroke = new SolidColorBrush(metal), StrokeThickness = 5 };
            Canvas.SetLeft(full, 4);
            Canvas.SetTop(full, 4);
            canvas.Children.Add(full);
        }
        else if (share > 0.005)
        {
            double angle = share * 2 * Math.PI;
            var end = new Point(50 + 46 * Math.Sin(angle), 50 - 46 * Math.Cos(angle));
            var arc = new PathFigure { StartPoint = new Point(50, 4), IsClosed = false };
            arc.Segments.Add(new ArcSegment(end, new Size(46, 46), 0, share > 0.5, SweepDirection.Clockwise, true));
            canvas.Children.Add(new Path
            {
                Data = new PathGeometry(new[] { arc }), Stroke = new SolidColorBrush(metal), StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }

        // The crest: a hexagon of the metal inside the ring, lit from the top
        // left, with a thin line inside its rim as on the badges.
        var crest = Geometry.Parse("M50,14 L81,32 L81,68 L50,86 L19,68 L19,32 Z");
        canvas.Children.Add(new Path
        {
            Data = crest,
            Fill = new LinearGradientBrush(Badge.Lighter(metal, 0.3), Badge.Darker(metal, 0.35), new Point(0.2, 0), new Point(0.8, 1)),
            Stroke = new SolidColorBrush(Badge.Darker(metal, 0.5)), StrokeThickness = 2.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.45 },
        });
        var inner = crest.Clone();
        inner.Transform = new ScaleTransform(0.84, 0.84, 50, 50);
        canvas.Children.Add(new Path { Data = inner, Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.4 });

        // The number: dark on the pale metals, white on bronze and gold, so
        // it reads on each.
        bool pale = tier is 2 or 4;
        var number = new TextBlock
        {
            Text = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 100, TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Bahnschrift SemiBold, Segoe UI"), FontWeight = FontWeights.Bold, FontSize = 34,
            Foreground = new SolidColorBrush(pale ? Badge.Darker(metal, 0.72) : Colors.White),
            Effect = pale ? null : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.4 },
        };
        Canvas.SetTop(number, 28);
        canvas.Children.Add(number);

        Children.Add(new Viewbox { Width = size, Height = size, Child = canvas });
        Children.Add(new TextBlock
        {
            Text = rank.ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeights.SemiBold, FontSize = Math.Max(9, UiTheme.PanelFontSize - 2),
            Foreground = new SolidColorBrush(metal), Margin = new Thickness(0, 2, 0, 0),
        });
    }
}
