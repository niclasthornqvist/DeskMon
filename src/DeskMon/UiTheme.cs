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

        // The same gradient ending in amber or red, for a reading past its
        // warning or critical level. With one gradient for every reading, the
        // bar stayed calm next to a red number - the start colour is kept so
        // the bar still looks like itself, only its tip gives the alarm.
        Point from = new(0, 0), to = new(Math.Max(1, settings.BarTrackWidth), 0);
        BarGradientWarn = settings.BarGradient
            ? MakeGradient(BarGradientStart, Parse(settings.WarnColor, "#FFFBBF24"), from, to)
            : null;
        BarGradientCrit = settings.BarGradient
            ? MakeGradient(BarGradientStart, Parse(settings.CritColor, "#FFF87171"), from, to)
            : null;

        // Top to bottom over the heading's accent, relative so it fits
        // whatever height the font gives it. The gradient's colours even with
        // BarGradient off: they are the theme's accent pair either way.
        var accent = new LinearGradientBrush(BarGradientStart, BarGradientEnd, 90);
        accent.Freeze();
        SectionAccent = accent;

        ApplyBarStyle(settings);
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

    /// <summary>The short bar before each section heading.</summary>
    public Brush SectionAccent { get; private set; } = Brushes.Transparent;

    /// <summary>The gradient ending in the warning or critical colour; null when BarGradient is off.</summary>
    public Brush? BarGradientWarn { get; private set; }
    public Brush? BarGradientCrit { get; private set; }

    /// <summary>
    /// Cuts the bar into blocks, dots or ticks, null for a whole one. A mask
    /// over the track and its fill together, so both are cut alike and the
    /// gaps line up.
    /// </summary>
    public Brush? BarMask { get; private set; }

    /// <summary>The striped style's stripes, over the filled part only - the empty track stays plain.</summary>
    public Brush? BarFillMask { get; private set; }

    /// <summary>
    /// How many steps the bar fills in - blocks or dots - or null for a smooth
    /// fill. A partly lit block or dot reads as a drawing fault, not a reading.
    /// </summary>
    public int? BarSteps { get; private set; }

    /// <summary>Pill-shaped ends. Not on flat, nor where the mask shapes each block or dot itself.</summary>
    public bool BarRoundEnds { get; private set; } = true;

    /// <summary>The outlined style: the track a thin outline, the fill inset inside it.</summary>
    public bool BarOutlined { get; private set; }

    /// <summary>The line-and-knob style: a thin line, and a knob at the reading.</summary>
    public bool BarLine { get; private set; }

    /// <summary>How many blocks a segmented bar has, whatever its length.</summary>
    public const int BarSegments = 12;

    /// <summary>
    /// The value drawn inside its bar, which fills 90% of the room between
    /// label and peak - the ValueInBar setting, with bars shown.
    /// </summary>
    public bool ValueInBar { get; private set; }

    /// <summary>How tall the bar is with the value inside: the thickness setting, but never less than the text.</summary>
    public double InBarHeight { get; private set; } = 16;

    /// <summary>
    /// Set while the value is inside the bar: the masks' tiles are drawn for
    /// this nominal width and stretched to the bar's real one, which is 90%
    /// of the row's room and not known here. Null tiles in fixed pixels.
    /// </summary>
    private double? _tileStretchWidth;

    /// <summary>Fills already made by <see cref="InBarFill"/>, by level and percent - see there.</summary>
    private readonly Dictionary<(int Level, int Percent), Brush> _inBarFills = new();

    /// <summary>
    /// The gradient for a fill that is <paramref name="fraction"/> of an
    /// inside-the-bar track whose width isn't known in pixels: drawn
    /// relative to the fill, and stretched so the whole gradient spans the
    /// whole track - a low reading stays at the start colour as on a normal
    /// bar. Level 0 is calm, 1 warning, 2 critical. Kept by whole percent, so
    /// a reading moving every tick reuses a hundred brushes at most rather
    /// than making one each time.
    /// </summary>
    public Brush? InBarFill(int level, double fraction)
    {
        if (BarGradient is null)
            return null;

        int percent = Math.Clamp((int)Math.Round(fraction * 100), 1, 100);
        if (_inBarFills.TryGetValue((level, percent), out var cached))
            return cached;

        Color end = level switch
        {
            2 when Crit is SolidColorBrush crit => crit.Color,
            1 when Warn is SolidColorBrush warn => warn.Color,
            _ => BarGradientEnd,
        };
        var brush = new LinearGradientBrush(BarGradientStart, end, new Point(0, 0), new Point(100.0 / percent, 0));
        brush.Freeze();
        _inBarFills[(level, percent)] = brush;
        return brush;
    }

    /// <summary>
    /// Works out the bar's look from BarStyle once per settings change, so
    /// each row only reads it: rounded, flat, segmented, dots, striped,
    /// ticks, outlined or line. Anything else - a hand-edited typo - is
    /// rounded, the default.
    /// </summary>
    private void ApplyBarStyle(AppSettings settings)
    {
        ValueInBar = settings.ValueInBar && settings.ShowBars;
        InBarHeight = Math.Max(Math.Max(1, settings.BarTrackHeight), Math.Ceiling(settings.FontSize * 1.35) + 2);
        _inBarFills.Clear();

        double width = Math.Max(1, settings.BarTrackWidth);
        // With the value inside, the bar is as tall as its text, so the dots
        // and blocks are drawn for that height - and their tiles stretch
        // with a bar that is now 90% of whatever room the row has.
        double height = ValueInBar ? InBarHeight : Math.Max(1, settings.BarTrackHeight);
        _tileStretchWidth = ValueInBar ? width : null;
        string style = (settings.BarStyle ?? string.Empty).Trim().ToLowerInvariant();

        BarMask = null;
        BarFillMask = null;
        BarSteps = null;
        BarRoundEnds = true;
        BarOutlined = false;
        BarLine = false;

        switch (style)
        {
            case "flat":
                BarRoundEnds = false;
                break;
            case "segmented":
                BarMask = MakeSegments(width, height);
                BarSteps = BarSegments;
                BarRoundEnds = false;
                break;
            case "dots":
                BarMask = MakeDots(width, height, out int dots);
                BarSteps = dots;
                BarRoundEnds = false;
                break;
            case "striped":
                BarFillMask = MakeStripes(height);
                break;
            case "ticks":
                BarMask = MakeTicks(width, height);
                break;
            case "outlined":
                BarOutlined = true;
                break;
            case "outlined-striped":
                // Both at once: the two are independent in MetricRow - the
                // outline and inset are the track's, the stripes the fill's.
                BarOutlined = true;
                BarFillMask = MakeStripes(height);
                break;
            case "line":
                BarLine = true;
                break;
        }
    }

    /// <summary>
    /// Round dots, as many as fit the track with a gap of a third of their
    /// size between - so a thicker bar has fewer, bigger dots. Spread to fill
    /// the track exactly, so the last dot ends where the track does.
    /// </summary>
    private Brush MakeDots(double width, double height, out int count)
    {
        double gap = Math.Max(1, Math.Round(height / 3));
        count = Math.Max(2, (int)Math.Floor((width + gap) / (height + gap)));
        double pitch = width / count;
        double size = Math.Max(1, Math.Min(height, pitch - gap));

        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pitch, height))));
        tile.Children.Add(new GeometryDrawing(Brushes.Black, null,
            new EllipseGeometry(new Rect((pitch - gap - size) / 2, (height - size) / 2, size, size))));
        return Tiled(tile, pitch, height);
    }

    /// <summary>
    /// A notch every tenth of the track, so 70 against 80 percent is judged
    /// without reading the number. 1 px wide - enough to see, too little to
    /// break the bar into blocks.
    /// </summary>
    private Brush MakeTicks(double width, double height)
    {
        double pitch = width / 10;
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pitch, height))));
        tile.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, Math.Max(1, pitch - 1), height))));
        return Tiled(tile, pitch, height);
    }

    /// <summary>
    /// Diagonal stripes, full and two-thirds strength in turn: dimmed rather
    /// than cut out, so the fill keeps its colour and its length reads as
    /// clearly as a plain one's. A repeating gradient with hard stops - one
    /// brush, no tiles.
    /// </summary>
    private static Brush MakeStripes(double height)
    {
        double step = Math.Max(4, height);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(step, step),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xA8, 0, 0, 0), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xA8, 0, 0, 0), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>A tile repeated along the track from its left end, frozen to share between every row.</summary>
    private Brush Tiled(Drawing tile, double pitch, double height)
    {
        // Inside-the-bar: tiles as a share of the bar, stretched to it, so
        // the same number of blocks or dots spans a bar of any width.
        var brush = _tileStretchWidth is double nominal
            ? new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, pitch / nominal, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill,
            }
            : new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, pitch, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// A tiled mask of <see cref="BarSegments"/> blocks with a 2px gap. Built
    /// once per settings change and shared by every row - one frozen brush
    /// costs nothing per tick, unlike twelve elements on each row.
    /// </summary>
    private Brush MakeSegments(double width, double height)
    {
        double pitch = width / BarSegments;
        double gap = pitch > 5 ? 2 : 1;
        double corner = Math.Min(2, height / 4);

        // The transparent rectangle spans the whole tile, so the drawing's
        // bounds are the pitch and the gap is kept rather than stretched away.
        var tile = new DrawingGroup();
        tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pitch, height))));
        tile.Children.Add(new GeometryDrawing(Brushes.Black, null,
            new RectangleGeometry(new Rect(0, 0, Math.Max(1, pitch - gap), height), corner, corner)));

        // Through Tiled like the newer masks, so it stretches too when the
        // value is inside the bar.
        return Tiled(tile, pitch, height);
    }

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
