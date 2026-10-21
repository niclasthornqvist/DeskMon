using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskMon.Notes;

namespace DeskMon.Ui;

/// <summary>
/// Where a playtime figure comes from, as a small icon beside it: Steam, GOG
/// Galaxy, the game's own saves, or DeskMon's session logs. Drawn on the same 11 x 11 grid
/// and in the same 1px stroke as the toolbar icons (ToolIcon, in
/// PanelStyles.xaml), so they sit among them as one set.
/// </summary>
internal static class PlaytimeIcons
{
    /// <summary>
    /// Steam's piston, simplified: the big wheel with its hub, the small wheel,
    /// and the rod between them - recognisable at this size, and drawn in the
    /// panel's line style rather than pasted in as the filled logo.
    /// </summary>
    private static readonly Geometry Steam = Frozen(
        "M4,4 A2.5,2.5 0 1 1 9,4 A2.5,2.5 0 1 1 4,4 " +
        "M6,4 A0.5,0.5 0 1 1 7,4 A0.5,0.5 0 1 1 6,4 " +
        "M1.5,8.5 A1.5,1.5 0 1 1 4.5,8.5 A1.5,1.5 0 1 1 1.5,8.5 " +
        "M4.2,7.6 L5,5.9");

    /// <summary>
    /// GOG's badge, simplified: a rounded square with a G in it - the logo is
    /// the letters in a rounded box, and one letter is what reads at 11 px.
    /// </summary>
    private static readonly Geometry Gog = Frozen(
        "M2.5,1.5 H8.5 A1,1 0 0 1 9.5,2.5 V8.5 A1,1 0 0 1 8.5,9.5 H2.5 A1,1 0 0 1 1.5,8.5 V2.5 A1,1 0 0 1 2.5,1.5 Z " +
        "M7.1,4.1 A2,2 0 1 0 7.5,5.5 H5.5");

    /// <summary>A floppy disk - "save" in every program's toolbar: body with a corner cut, shutter on top, label below.</summary>
    private static readonly Geometry Saves = Frozen(
        "M1.5,1.5 H8 L9.5,3 V9.5 H1.5 Z M3.5,1.5 V4 H7.5 V1.5 M3.5,9.5 V6.5 H7.5 V9.5");

    /// <summary>The clock from the widget's LOG button: DeskMon's own session history.</summary>
    private static readonly Geometry Logged = Frozen(
        "M0.5,5 A4.5,4.5 0 1 1 9.5,5 A4.5,4.5 0 1 1 0.5,5 M5,2.5 V5 H7");

    public static Geometry For(PlaytimeSource source) => source switch
    {
        PlaytimeSource.Steam => Steam,
        PlaytimeSource.Gog => Gog,
        PlaytimeSource.Saves => Saves,
        _ => Logged,
    };

    /// <summary>The icon, ready to place, in <paramref name="stroke"/> - the colour of the text it sits beside.</summary>
    public static Path Make(PlaytimeSource source, Brush stroke) => new()
    {
        Data = For(source),
        Stroke = stroke,
        StrokeThickness = 1,
        Width = 11,
        Height = 11,
        Stretch = Stretch.None,
        SnapsToDevicePixels = true,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
