using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DeskMon;

/// <summary>
/// The colour strings from settings.json turned into brushes.
/// Frozen so WPF can share them across threads and skip change tracking.
/// </summary>
public sealed class UiTheme
{
    /// <summary>
    /// The notes' own starting font and size, and the windows' text size.
    ///
    /// These were the NotesFontFamily and NotesFontSize settings. The notepad
    /// now has its own font and size pickers that format the text itself, so
    /// a setting for it only decided what new text started as - and these
    /// are what that start had settled on anyway. The font stays fixed for
    /// the notes, since it is part of what they save; the windows around them
    /// take the theme's instead - see <see cref="PanelFont"/>.
    /// </summary>
    public const string PanelFontFamily = "Calibri";
    public const double PanelFontSize = 14;

    public UiTheme(AppSettings settings) => Apply(settings);

    /// <summary>
    /// Rebuilds every brush from the settings as they stand now, for a colour
    /// changed in the settings window.
    ///
    /// The brushes are replaced rather than edited, because they are frozen -
    /// which is what lets WPF share them across threads without change
    /// tracking, and is worth far more than being able to assign a Color. The
    /// rows reach these through properties, so telling their bindings to look
    /// again is all it takes for the panel to repaint; see
    /// <see cref="Metrics.IPanelRow"/>.
    ///
    /// Windows that copy a brush onto a control instead - the notepad's
    /// background, say - do not follow, and their settings are marked in the
    /// window as needing a reload.
    /// </summary>
    public void Apply(AppSettings settings)
    {
        Background = Make(settings.BackgroundColor, "#D9121216");
        Border = Make(settings.BorderColor, "#33FFFFFF");
        Label = Make(settings.LabelColor, "#FF9AA4B2");
        Header = Make(settings.HeaderColor, "#FF7F8A9B");
        Section = Make(settings.SectionColor, "#FFE2E8F0");
        Separator = Make(settings.SeparatorColor, "#2EFFFFFF");

        // Same panel as the widget, just less see-through: this one is for
        // reading and typing, not for glancing at over a wallpaper.
        NotesBackground = Make(settings.NotesBackgroundColor, "#F2121216");

        // Kept as a Color as well as a brush: saving notes has to take this
        // exact colour back out of the RTF, by value. See Notes/NotesRtf.cs.
        NotesTextColor = Parse(settings.NotesTextColor, "#FFE6EAF0");
        var notesText = new SolidColorBrush(NotesTextColor);
        notesText.Freeze();
        NotesText = notesText;

        // Null, not a fallback colour: nothing to pin the bar to means the bar
        // follows the value, which is a behaviour rather than a colour and so
        // cannot be expressed as one. See MetricRow.BarBrush.
        Bar = string.IsNullOrWhiteSpace(settings.BarColor)
            ? null
            : Make(settings.BarColor, "#FF4ADE80");

        // Spread over the whole track rather than the filled part, so a bar
        // shows how far along the gradient it got: a low reading stays at the
        // start colour, only a full one reaches the end. The fill sits at the
        // left of the track, so the track's width in pixels is where it ends -
        // and Apply runs again when that setting changes.
        BarGradientStart = Parse(settings.BarGradientStartColor, "#FF22D3EE");
        BarGradientEnd = Parse(settings.BarGradientEndColor, "#FFA855F7");
        BarGradient = settings.BarGradient
            ? MakeGradient(BarGradientStart, BarGradientEnd,
                           new Point(0, 0), new Point(Math.Max(1, settings.BarTrackWidth), 0))
            : null;

        BarTrack = Make(settings.BarTrackColor, "#26FFFFFF");

        Normal = Make(settings.NormalColor, "#FF4ADE80");
        Warn = Make(settings.WarnColor, "#FFFBBF24");
        Crit = Make(settings.CritColor, "#FFF87171");
        Unavailable = Make(settings.UnavailableColor, "#FF6B7280");

        // The chrome and Changed only when the look really changed: Apply runs
        // for every setting the settings window touches - each step of an
        // opacity slider's drag - and replacing the shared resources repaints
        // every control, while the windows redraw their tables and tiles.
        string look = Signature(settings);
        if (look == _look)
            return;

        _look = look;
        ApplyChrome(settings);
        Changed?.Invoke();
    }

    private string? _look;

    /// <summary>Every value a theme decides, in one string - what Changed compares.</summary>
    private static string Signature(AppSettings settings)
        => string.Join("|", ThemeCatalogue.Keys.Select(key => typeof(AppSettings).GetProperty(key)?.GetValue(settings)));

    // --- The windows' chrome ----------------------------------------------------
    //
    // Buttons, boxes, dropdowns, scrollbars, tooltips and menus used to be drawn
    // in fixed colours: white at a low opacity over the panel, a blue accent,
    // and near-black popups. That suits any dark panel, and nothing else - on
    // the Light theme they were white on white. So they are worked out here
    // from the theme instead: the shades are white over a dark panel and black
    // over a light one, the accent is the theme's, and the popups are the
    // window colour made solid. Ui/PanelStyles.xaml reaches them as
    // DynamicResources, which repaint the moment they are replaced here.

    /// <summary>Raised after every Apply, so the windows can repaint what they colour themselves - see their ApplyTheme.</summary>
    public event Action? Changed;

    /// <summary>The window colour is pale: shades are drawn in black over it rather than white.</summary>
    public bool IsLight { get; private set; }

    /// <summary>What lights up - see AppSettings.AccentColor - and a brighter and a faint version of it.</summary>
    public Color AccentColor { get; private set; }
    public Brush Accent { get; private set; } = Brushes.Transparent;
    public Brush AccentLit { get; private set; } = Brushes.Transparent;
    public Brush AccentSoft { get; private set; } = Brushes.Transparent;

    /// <summary>A solid, slightly lifted window colour, for menus, tooltips and the chart's empty slots.</summary>
    public Brush Popup { get; private set; } = Brushes.Transparent;

    /// <summary>The font of every window but the widget; see AppSettings.WindowFontFamily.</summary>
    public FontFamily PanelFont { get; private set; } = new(PanelFontFamily);

    /// <summary>The corners of the widget and the windows; see AppSettings.CornerRadius.</summary>
    public CornerRadius Corner { get; private set; }

    /// <summary>The accent-coloured light round the panels, or null with Glow off.</summary>
    public System.Windows.Media.Effects.Effect? Glow { get; private set; }

    /// <summary>Room left round a panel for its glow to show in, inside the window's own edge.</summary>
    public Thickness GlowRoom => Glow is null ? new Thickness(0) : new Thickness(GlowRadius / 2);

    private const double GlowRadius = 16;

    private readonly Dictionary<byte, Brush> _shades = new();

    /// <summary>
    /// The panel's own shade at <paramref name="alpha"/>: white over a dark
    /// panel, black over a light one. What the cards, hover rows and dividers
    /// are drawn in, so they show on any theme.
    /// </summary>
    public Brush Shade(byte alpha)
    {
        if (!_shades.TryGetValue(alpha, out var brush))
            _shades[alpha] = brush = Solid(IsLight ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255));
        return brush;
    }

    /// <summary>The windows' background colour, opaque - see AppSettings.NotesBackgroundColor.</summary>
    public Color WindowColor { get; private set; }

    /// <summary>
    /// The window colour at <paramref name="alpha"/>: a frosted backing for
    /// text laid over a game's picture, which in the panel's own colour reads
    /// on a light theme as on a dark one.
    /// </summary>
    public Brush WindowAt(byte alpha) => Solid(Color.FromArgb(alpha, WindowColor.R, WindowColor.G, WindowColor.B));

    /// <summary>
    /// A soft halo in the window colour round text over a picture - so a bright
    /// patch of the art behind a word can't swallow it.
    /// </summary>
    public System.Windows.Media.Effects.Effect TextHalo { get; private set; } = null!;

    /// <summary>The accent at <paramref name="alpha"/>, for tints like a selected row.</summary>
    public Brush AccentAt(byte alpha)
        => Solid(Color.FromArgb(alpha, AccentColor.R, AccentColor.G, AccentColor.B));

    /// <summary>The overlay alphas Ui/PanelStyles.xaml and the windows' XAML use, as Theme.Ov.XX.</summary>
    private static readonly byte[] XamlShades =
        { 0x08, 0x0A, 0x0E, 0x0F, 0x10, 0x14, 0x1A, 0x1C, 0x1F, 0x26, 0x2B, 0x2E, 0x33, 0x38, 0x40, 0x4D, 0x59, 0x5C, 0x8F, 0x99 };

    /// <summary>And the accent alphas, as Theme.Accent.XX; Theme.Accent is the solid one.</summary>
    private static readonly byte[] XamlAccents = { 0x14, 0x40, 0x4D, 0x50, 0x70, 0x80, 0x99 };

    private void ApplyChrome(AppSettings settings)
    {
        var window = Parse(settings.NotesBackgroundColor, "#F2121216");
        IsLight = Luminance(window) > 0.5;
        WindowColor = Color.FromRgb(window.R, window.G, window.B);
        _shades.Clear();

        var halo = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = WindowColor,
            BlurRadius = 8,
            ShadowDepth = 0,
            Opacity = 1,
        };
        halo.Freeze();
        TextHalo = halo;

        AccentColor = Parse(settings.AccentColor, "#FF60A5FA");
        Accent = Solid(AccentColor);
        AccentLit = Solid(Lighten(AccentColor, 0.35));
        AccentSoft = AccentAt(0x40);

        // Solid: a popup's own window can't be relied on to be see-through.
        var solid = Color.FromRgb(window.R, window.G, window.B);
        var shade = IsLight ? Colors.Black : Colors.White;
        Popup = Solid(Mix(solid, shade, 0.06));

        PanelFont = new FontFamily(string.IsNullOrWhiteSpace(settings.WindowFontFamily) ? PanelFontFamily : settings.WindowFontFamily);
        Corner = new CornerRadius(Math.Clamp(settings.CornerRadius, 0, 24));
        Glow = settings.Glow
            ? MakeGlow(string.IsNullOrWhiteSpace(settings.GlowColor) ? AccentColor : Parse(settings.GlowColor, "#FF60A5FA"))
            : null;

        if (System.Windows.Application.Current is not { } app)
            return;

        var resources = app.Resources;
        resources["Theme.Text"] = NotesText;
        resources["Theme.TextDim"] = Label;
        resources["Theme.Disabled"] = Unavailable;
        resources["Theme.Popup"] = Popup;
        resources["Theme.PopupEdge"] = Solid(Mix(solid, shade, 0.2));
        resources["Theme.MenuHot"] = Solid(Mix(solid, shade, 0.14));
        resources["Theme.Knob"] = Solid(Mix(NotesTextColor, solid, 0.2));
        resources["Theme.Accent"] = Accent;

        // Dark on a pale accent and white on a deep one, for the tick in a
        // ticked box - it sits on the accent, not on the panel.
        resources["Theme.OnAccent"] = Solid(Luminance(AccentColor) > 0.55 ? Color.FromRgb(0x10, 0x13, 0x1A) : Colors.White);

        foreach (byte alpha in XamlShades)
            resources[$"Theme.Ov.{alpha:X2}"] = Shade(alpha);
        foreach (byte alpha in XamlAccents)
        {
            resources[$"Theme.Accent.{alpha:X2}"] = AccentAt(alpha);
            resources[$"Theme.AccentColor.{alpha:X2}"] = Color.FromArgb(alpha, AccentColor.R, AccentColor.G, AccentColor.B);
        }

        resources["Theme.Font"] = PanelFont;
        resources["Theme.Corner"] = Corner;
        resources["Theme.CornerSmall"] = new CornerRadius(Math.Min(4, Corner.TopLeft));
    }

    private static System.Windows.Media.Effects.Effect MakeGlow(Color accent)
    {
        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = accent,
            BlurRadius = GlowRadius,
            ShadowDepth = 0,
            Opacity = 0.7,
        };
        glow.Freeze();
        return glow;
    }

    /// <summary>
    /// A panel's corners, glow and the room the glow needs - what every window
    /// and the widget do with them, in one place so they can't drift apart.
    /// </summary>
    public void Dress(System.Windows.Controls.Border panel)
    {
        panel.CornerRadius = Corner;
        panel.Effect = Glow;
        panel.Margin = GlowRoom;
    }

    private static double Luminance(Color colour)
        => (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255;

    /// <summary><paramref name="from"/> moved <paramref name="amount"/> of the way to <paramref name="to"/>, as an opaque colour.</summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private static Brush Solid(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    // Settable only from Apply, and given placeholders so the compiler can see
    // they are never null - the constructor assigns all of them through Apply,
    // which it cannot prove.
    public Brush Background { get; private set; } = Brushes.Transparent;
    public Brush Border { get; private set; } = Brushes.Transparent;
    public Brush Label { get; private set; } = Brushes.Transparent;
    public Brush Header { get; private set; } = Brushes.Transparent;
    public Brush Section { get; private set; } = Brushes.Transparent;
    public Brush Separator { get; private set; } = Brushes.Transparent;
    public Brush NotesBackground { get; private set; } = Brushes.Transparent;
    public Brush NotesText { get; private set; } = Brushes.Transparent;

    /// <summary>The notes text colour by value, for stripping it out of saved RTF.</summary>
    public Color NotesTextColor { get; private set; }
    /// <summary>The bar's fill, or null for "whatever colour the value is".</summary>
    public Brush? Bar { get; private set; }

    /// <summary>The widget's bar gradient, or null when BarGradient is off. Takes precedence over <see cref="Bar"/>.</summary>
    public Brush? BarGradient { get; private set; }

    /// <summary>The gradient's two ends by value, for the sessions chart, which lays it out per bar.</summary>
    public Color BarGradientStart { get; private set; }
    public Color BarGradientEnd { get; private set; }

    /// <summary>The groove behind the bar.</summary>
    public Brush BarTrack { get; private set; } = Brushes.Transparent;

    public Brush Normal { get; private set; } = Brushes.Transparent;
    public Brush Warn { get; private set; } = Brushes.Transparent;
    public Brush Crit { get; private set; } = Brushes.Transparent;
    public Brush Unavailable { get; private set; } = Brushes.Transparent;

    private static Brush Make(string value, string fallback)
    {
        var brush = new SolidColorBrush(Parse(value, fallback));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// A frozen gradient between two points in the painted element's own
    /// pixels, rather than relative to its size - so a short bar shows the
    /// start of the gradient instead of all of it squeezed in.
    /// </summary>
    public static Brush MakeGradient(Color start, Color end, Point from, Point to)
    {
        var brush = new LinearGradientBrush(start, end, from, to)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>The colour moved a share of the way to white, alpha kept: for a hover.</summary>
    public static Color Lighten(Color colour, double amount)
    {
        byte Mix(byte channel) => (byte)Math.Round(channel + (255 - channel) * amount);
        return Color.FromArgb(colour.A, Mix(colour.R), Mix(colour.G), Mix(colour.B));
    }

    private static Color Parse(string value, string fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return (Color)ColorConverter.ConvertFromString(fallback);
        }
    }
}
