using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// The bin on a picture - "🗑 DELETE" on a dark plate in its bottom-right
/// corner - shared by the pictures window, which makes it a button on each
/// thumbnail, and the notes window, which draws it on each picture (anything
/// put in the notes' document would be saved in the notes file). One
/// description here, so the two can't drift apart.
///
/// Always shown, labelled as well as drawn: an icon alone on a picture was
/// easy to miss and had to be hovered to be found. A first click arms it -
/// DELETE? in the warning colour - and a second within a few seconds deletes.
/// </summary>
public static class BinBadge
{
    public const double Height = 24;
    public const double PadLeft = 8, PadRight = 9, IconSize = 11, Gap = 5;

    /// <summary>Its gap from the picture's right and bottom edges - the date's own gap from the left.</summary>
    public const double Right = 7, Bottom = 6;

    public const double FontSize = 11;

    /// <summary>The pictures window's bin icon, in an 11 px box.</summary>
    public static readonly Geometry Icon = Frozen(Geometry.Parse(
        "M1.5,2.5 H9.5 M4.5,2.5 V0.5 H6.5 V2.5 M2.5,2.5 V9.5 H8.5 V2.5 M4.5,4.5 V7.5 M6.5,4.5 V7.5"));

    /// <summary>Dark and nearly opaque, so it reads on a bright sky and a dark cave alike - the NOT IN NOTES badge's plate.</summary>
    public static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0x16, 0x19, 0x1F)));

    /// <summary>A touch more solid under the mouse, so it reads as something to click.</summary>
    public static readonly Brush PlateHot = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x22, 0x26, 0x2E)));

    /// <summary>Near-white, whatever the theme: it sits on its own dark plate, not on the panel.</summary>
    public static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF0)));

    public static readonly FontFamily Font = new("Segoe UI");

    public static string Text(bool armed) => armed ? "DELETE?" : "DELETE";

    public static FormattedText Measure(bool armed, Brush ink, double pixelsPerDip)
        => new(Text(armed), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
               new Typeface(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
               FontSize, ink, pixelsPerDip);

    /// <summary>The whole badge's width with <paramref name="text"/> in it.</summary>
    public static double Width(FormattedText text) => PadLeft + IconSize + Gap + text.Width + PadRight;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
