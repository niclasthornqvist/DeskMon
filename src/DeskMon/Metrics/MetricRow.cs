using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DeskMon.Sensors;

namespace DeskMon.Metrics;

/// <summary>
/// The bindable view of one <see cref="Metric"/>. Holds the formatted text, the
/// bar length and the colour, and raises change notifications so a refresh only
/// repaints the few values that actually changed.
/// </summary>
public sealed class MetricRow : INotifyPropertyChanged, IPanelRow
{
    private readonly Metric _metric;
    private readonly UiTheme _theme;
    private readonly AppSettings _settings;

    private string _value = "n/a";
    private double _barWidth;
    private Brush _valueBrush;
    private bool _isExpanded;
    private string _peak = string.Empty;
    private double? _worst;
    private string? _flag;

    public MetricRow(Metric metric, string sectionKey, UiTheme theme, AppSettings settings)
    {
        _metric = metric;
        _theme = theme;
        _settings = settings;
        _valueBrush = theme.Unavailable;

        // The section has to be part of the key: "CPU" is a row in Usage and a
        // row in Temperature, and they fold independently.
        Key = $"{sectionKey}/{metric.Label}";
        _isExpanded = settings.ExpandedMetrics.Contains(Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The arrows are drawn larger than the text they sit beside. They are the
    /// one thing on the panel you are meant to click, and at row size they read
    /// as punctuation rather than as a control.
    /// </summary>
    internal static double ArrowSize(AppSettings settings)
        => Math.Max(10, settings.FontSize + 2);

    public string Label => _metric.Label;
    public Brush LabelBrush => _theme.Label;

    /// <summary>The label while the mouse is over the row: the heading colour, brighter than a label.</summary>
    public Brush LabelHoverBrush => _theme.Section;

    /// <summary>
    /// What the row means, on hover: the metric's own explanation, then the
    /// levels it turns amber and red at, then what clicking it unfolds.
    ///
    /// The levels are worked out from the metric rather than written into each
    /// explanation, so changing a threshold in MetricRegistry can't leave the
    /// tooltip saying the old one. Null only for a row with nothing to say at
    /// all, which then shows the panel's own tooltip.
    /// </summary>
    public string? Tooltip
    {
        get
        {
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(_metric.Tooltip))
                lines.Add(_metric.Tooltip);

            if (Thresholds() is { } levels)
                lines.Add(levels);

            if (_folded.Count > 0)
                lines.Add($"Click to show {string.Join(", ", _folded.Select(r => r.Label))}.");
            else if (Details.Count > 0)
                lines.Add($"Click to show {_metric.DetailsAbout}.");

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
    }

    /// <summary>
    /// "Amber at 80 % or above, red at 95 % or above." - or "or below" for an
    /// inverted metric like FPS. Null for a metric with no thresholds, which
    /// never changes colour.
    /// </summary>
    private string? Thresholds()
    {
        string direction = _metric.Invert ? "or below" : "or above";
        var parts = new List<string>();

        if (!double.IsInfinity(_metric.Warn))
            parts.Add($"amber at {Level(_metric.Warn)} {direction}");
        if (!double.IsInfinity(_metric.Crit))
            parts.Add($"red at {Level(_metric.Crit)} {direction}");

        if (parts.Count == 0)
            return null;

        string sentence = string.Join(", ", parts) + ".";
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    private string Level(double value)
    {
        string number = value.ToString(_metric.Format, CultureInfo.InvariantCulture);
        return _metric.Unit.Length > 0 ? $"{number} {_metric.Unit}" : number;
    }

    // Read from the settings on every binding pass rather than captured when
    // the row is built, so the settings window can change them while the widget
    // is running. See IPanelRow.
    public double LabelWidth => _settings.LabelWidth;
    public double ValueWidth => _settings.ValueWidth;

    /// <summary>
    /// The whole groove. <see cref="BarWidth"/> is the filled part of it, which
    /// is why the two cannot share a name.
    /// </summary>
    // Floored at 1 like the height: a hand-edited 0 or negative width is not
    // a Width WPF accepts, and the bar's maths divides the track into blocks.
    public double TrackWidth => Math.Max(1, _settings.BarTrackWidth);

    /// <summary>Floored at 1, since a bar no pixels tall is not a setting.</summary>
    public double TrackHeight => Math.Max(1, _settings.BarTrackHeight);

    /// <summary>
    /// Half the thickness, so the ends stay semicircular whatever height the
    /// bar is given. A fixed radius would square the ends off as it grew.
    /// Square on flat, and on segmented and dots, where the mask shapes each
    /// block or dot itself - a pill's round ends would cut most of the first
    /// and last away. On the line style, half the line's thickness.
    /// </summary>
    public CornerRadius BarCorner => _theme.BarRoundEnds ? new(TrackLineThickness / 2) : default;

    /// <summary>
    /// The fill's own corners: inside an outlined track, smaller by the
    /// inset, so the fill's ends follow the outline's curve.
    /// </summary>
    public CornerRadius FillCorner
        => _theme.BarRoundEnds ? new(Math.Max(0, TrackLineThickness / 2 - TrackInset)) : default;

    /// <summary>The groove's colour - none on an outlined bar, where the outline is the groove.</summary>
    public Brush TrackBrush => _theme.BarOutlined ? Brushes.Transparent : _theme.BarTrack;

    /// <summary>The outlined style's outline, in the groove's colour.</summary>
    public Brush TrackBorderBrush => _theme.BarTrack;

    public Thickness TrackBorderThickness => _theme.BarOutlined ? new Thickness(1) : default;

    /// <summary>
    /// The gap between an outline and the fill inside it. None on a bar under
    /// 6 px: with the outline as well it would leave the fill a sliver.
    /// </summary>
    public Thickness TrackPadding => _theme.BarOutlined && BarThickness >= 6 ? new Thickness(1) : default;

    /// <summary>How far the fill sits inside the track on each side: outline plus gap.</summary>
    private double TrackInset => TrackBorderThickness.Left + TrackPadding.Left;

    /// <summary>
    /// How thick the drawn track is: the bar thickness, or on the line style a
    /// line of at most 2 px - the thickness setting sizes the knob instead.
    /// </summary>
    private double TrackLineThickness => _theme.BarLine ? Math.Min(2, BarThickness) : BarThickness;

    /// <summary>
    /// The bar's thickness: the setting, or with the value inside the bar at
    /// least the text's height - see <see cref="UiTheme.InBarHeight"/>.
    /// </summary>
    private double BarThickness => _theme.ValueInBar ? _theme.InBarHeight : TrackHeight;

    // --- The value inside the bar --------------------------------------------
    //
    // With ValueInBar the bar is 90% of the room between label and peak, so
    // its width in pixels is the layout's, not known here. Where the fill,
    // the peak tick and the knob end is said as a share of it instead - two
    // star columns, filled and not - which WPF turns into pixels itself.

    /// <summary>
    /// Which layout the row's template builds - beside the bar or inside it.
    /// Read by a trigger that swaps the template, so only the one in use
    /// exists; Refresh, on a settings change, makes it look again.
    /// </summary>
    public bool ValueInBar => _theme.ValueInBar;

    public double InBarHeight => _theme.InBarHeight;

    /// <summary>
    /// The bar length setting as 90% of the room, so the 5% either side is
    /// kept even in a widget sized exactly to its content.
    /// </summary>
    public double InBarMinWidth => Math.Ceiling(TrackWidth / 0.9);

    /// <summary>The filled share of the bar, and the rest - see the section note above.</summary>
    public GridLength FillShare => new(_fraction, GridUnitType.Star);
    public GridLength RestShare => new(1 - _fraction, GridUnitType.Star);

    /// <summary>Where the peak reached, as a share, for its tick on a bar sized by the layout.</summary>
    public GridLength PeakShare => new(_worst is double worst ? Fraction(worst) : 0, GridUnitType.Star);
    public GridLength PeakRestShare => new(1 - (_worst is double worst ? Fraction(worst) : 0), GridUnitType.Star);

    /// <summary>
    /// The fill's gradient, spread over the whole bar whatever its width -
    /// see <see cref="UiTheme.InBarFill"/> - or the plain bar colour without one.
    /// </summary>
    public Brush InBarFillBrush
        => _theme.InBarFill(ReferenceEquals(_valueBrush, _theme.Crit) ? 2 : ReferenceEquals(_valueBrush, _theme.Warn) ? 1 : 0,
                            _fraction)
           ?? BarBrush;

    /// <summary>
    /// The number on the bar: the bright heading colour while all is well,
    /// since green text on a coloured fill was hard to read, and the amber or
    /// red of a reading past its levels, which must still show.
    /// </summary>
    public Brush InBarValueBrush
        => ReferenceEquals(_valueBrush, _theme.Normal) ? _theme.Section : _valueBrush;

    /// <summary>
    /// The outline round the number, in the window colour so it reads over the
    /// fill as well as the empty track - dark round light text on a dark
    /// theme, light round dark text on a light one.
    /// </summary>
    public Brush InBarOutlineBrush => _theme.WindowAt(0xF0);

    /// <summary>The knob centred on the end of the fill, half of it past that end.</summary>
    public Thickness InBarKnobMargin => new(0, 0, -KnobSize / 2, 0);

    /// <summary>The reading's share of the bar, stepped like the fill - what the share columns are built from.</summary>
    private double _fraction;

    /// <summary>Tells the inside-the-bar layout the fill moved; nothing to do in the other layout.</summary>
    private void NoteFraction(double fraction)
    {
        if (!_theme.ValueInBar || _fraction == fraction)
        {
            _fraction = fraction;
            return;
        }
        _fraction = fraction;
        Set(nameof(FillShare));
        Set(nameof(RestShare));
        Set(nameof(InBarFillBrush));
    }

    /// <summary>
    /// The drawn track's height: the line's on the line style, the full bar
    /// thickness otherwise. Always a real height, never NaN for "fill": the
    /// track is centred so the line sits in the middle, and a centred element
    /// with no height is only as tall as its content - the track has none, so
    /// every bar shrank to nothing.
    /// </summary>
    public double TrackLineHeight => TrackLineThickness;

    /// <summary>Cuts the bar into blocks, dots or ticks - see <see cref="UiTheme.BarMask"/>.</summary>
    public Brush? BarMask => _theme.BarMask;

    /// <summary>The striped style's stripes, on the fill alone - see <see cref="UiTheme.BarFillMask"/>.</summary>
    public Brush? FillMask => _theme.BarFillMask;

    /// <summary>The line style's knob: as big as the bar thickness setting, so that setting still sizes something.</summary>
    public double KnobSize => TrackHeight;

    public Visibility KnobVisibility => _theme.BarLine ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Centred on the end of the fill, kept inside the track so an empty or full bar's knob still shows whole.</summary>
    public Thickness KnobMargin
        => new(Math.Clamp(BarWidth - KnobSize / 2, 0, Math.Max(0, TrackWidth - KnobSize)), 0, 0, 0);

    /// <summary>
    /// The knob's colour: what the gradient has reached at the reading - an
    /// absolute gradient on the knob itself would paint it in the start
    /// colour wherever it stood - or the bar's plain colour without one.
    /// </summary>
    public Brush KnobBrush
    {
        get => _knobBrush;
        private set => Set(ref _knobBrush, value, nameof(KnobBrush));
    }

    private Brush _knobBrush = Brushes.Transparent;

    /// <summary>
    /// The fill: the gradient when BarGradient is on, its end turned amber or
    /// red with the value - one gradient for every level left a calm bar beside
    /// a red number. Otherwise it follows the value's colour unless BarColor
    /// pins it, so the bar reddens with the number rather than needing to be
    /// read separately.
    /// </summary>
    public Brush BarBrush
        => _theme.BarGradient is { } calm
            ? (ReferenceEquals(_valueBrush, _theme.Crit) ? _theme.BarGradientCrit ?? calm
               : ReferenceEquals(_valueBrush, _theme.Warn) ? _theme.BarGradientWarn ?? calm
               : calm)
            : _theme.Bar ?? _valueBrush;

    /// <summary>
    /// Where the peak reached, as a tick on the bar: 2px wide, centred on the
    /// spot, and kept inside the track so a full peak still shows.
    /// </summary>
    public Thickness PeakMarkerMargin
        => _worst is double worst
            ? new Thickness(Math.Clamp(Fraction(worst) * TrackWidth - 2, 0, Math.Max(0, TrackWidth - 4)), -2, 0, -2)
            : default;

    /// <summary>
    /// The tick's thin dark edge, in the window colour: 1 px either side of
    /// its 2 px core, so it stays crisp over the bright fill and the pale track
    /// alike instead of melting into whichever is behind it.
    /// </summary>
    public Brush PeakMarkerEdgeBrush => _theme.WindowAt(0xE0);

    /// <summary>
    /// Only once there is a peak, with the bar showing and the setting on. Also
    /// hidden at the very start of the track, where it would only sit on the
    /// empty end and look like a stray mark.
    /// </summary>
    public Visibility PeakMarkerVisibility
        => _settings.ShowPeakMarker && _settings.ShowBars && _worst is double worst && Fraction(worst) > 0.01
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// The tick's colour: the gradient's colour at the peak, ending in amber
    /// or red if the peak crossed those levels, lightened part of the way to
    /// white. It reads as part of the bar rather than a stray white mark, yet
    /// stays brighter than the fill it may sit on. The heading colour when
    /// there is no gradient.
    /// </summary>
    public Brush PeakMarkerBrush
    {
        get
        {
            if (_theme.BarGradient is null || _worst is not double worst)
                return _theme.Section;

            Color start = _theme.BarGradientStart;
            Color end = _metric.Exceeds(worst, _metric.Crit) && _theme.Crit is SolidColorBrush crit ? crit.Color
                      : _metric.Exceeds(worst, _metric.Warn) && _theme.Warn is SolidColorBrush warn ? warn.Color
                      : _theme.BarGradientEnd;
            double at = Fraction(worst);
            byte Tint(byte a, byte b) => (byte)Math.Round(Math.Min(255, (a + (b - a) * at) + (255 - (a + (b - a) * at)) * 0.45));
            var colour = Color.FromRgb(Tint(start.R, end.R), Tint(start.G, end.G), Tint(start.B, end.B));

            // The last brush again when the colour hasn't moved: this is read
            // on every new peak, which a climbing reading sets every tick, and
            // on every settings change - a new brush each time added up over
            // a game for a tick that mostly looks the same.
            if (_peakMarkerBrush is { } cached && cached.Color == colour)
                return cached;

            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            _peakMarkerBrush = brush;
            return brush;
        }
    }

    /// <summary>The peak marker's last brush - see <see cref="PeakMarkerBrush"/>.</summary>
    private SolidColorBrush? _peakMarkerBrush;

    /// <summary>A reading's share of the bar, 0 to 1.</summary>
    private double Fraction(double value)
    {
        double span = _metric.Max - _metric.Min;
        return span > 0 ? Math.Clamp((value - _metric.Min) / span, 0, 1) : 0;
    }

    public Visibility BarVisibility
        => _settings.ShowBars ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>See <see cref="ArrowSize"/>.</summary>
    public double IndicatorFontSize => ArrowSize(_settings);

    public Visibility PeakVisibility
        => _settings.ShowPeaks ? Visibility.Visible : Visibility.Collapsed;

    public double PeakFontSize => Math.Max(8, _settings.FontSize - 2);

    /// <summary>
    /// Narrower than the value column: no unit is printed, since the row it
    /// sits on already says what the number is measured in. Floored so that a
    /// hand-edited 0 clips the digits rather than hiding them entirely.
    /// </summary>
    public double PeakWidth => Math.Max(16, _settings.PeakWidth);

    /// <summary>Quieter than the live value - it is history, not a reading.</summary>
    public Brush PeakBrush => _theme.Header;

    /// <summary>
    /// The worst reading since the peaks were last reset: the highest for most
    /// metrics, the lowest for an inverted one like FPS, where low is the bad
    /// direction. Marked with an arrow saying which way it went, and empty
    /// until there has been something to record.
    /// </summary>
    public string Peak
    {
        get => _peak;
        private set => Set(ref _peak, value, nameof(Peak));
    }

    /// <summary>
    /// The worst reading with its unit, e.g. "78 °C", or null if nothing has
    /// been recorded. For the session history's summary line, which wants it
    /// without the panel's arrow.
    /// </summary>
    public string? WorstText
        => _worst is double value
            ? (_metric.Unit.Length > 0
                ? value.ToString(_metric.Format, CultureInfo.InvariantCulture) + " " + _metric.Unit
                : value.ToString(_metric.Format, CultureInfo.InvariantCulture))
            : null;

    /// <summary>Which direction is the bad one - FPS and its lows are inverted.</summary>
    public bool LowerIsWorse => _metric.Invert;

    /// <summary>
    /// Starts the peak over - called when a game launches, so the figures
    /// describe that session rather than everything since logon, and from
    /// "Reset peaks" on the menu.
    /// </summary>
    public void ResetPeak()
    {
        _worst = null;
        Peak = string.Empty;
        NotePeakMarker();
    }

    /// <summary>The marker is worked out from _worst, so it has to be told when that moves.</summary>
    private void NotePeakMarker()
    {
        Set(nameof(PeakMarkerMargin));
        Set(nameof(PeakMarkerVisibility));
        Set(nameof(PeakMarkerBrush));
        Set(nameof(PeakShare));
        Set(nameof(PeakRestShare));
    }

    /// <summary>Stable id for settings.json, e.g. "Temperature/CPU".</summary>
    public string Key { get; }

    /// <summary>Whether this metric offers a list of the sensors behind it.</summary>
    public bool HasDetails => _metric.Details is not null;

    /// <summary>
    /// The lines of that list. Empty until <see cref="BuildDetails"/> has seen
    /// what this machine actually reports.
    /// </summary>
    public List<DetailRow> Details { get; } = new();

    /// <summary>
    /// Full metric rows that fold under this one - see <see cref="Metric.FoldsUnder"/>.
    /// The window hides and shows them; this row only needs to know they
    /// exist, for its arrow and its tooltip.
    /// </summary>
    private readonly List<MetricRow> _folded = new();

    /// <summary>Called once, while the rows are built.</summary>
    public void AddFolded(MetricRow row) => _folded.Add(row);

    /// <summary>
    /// Whether clicking the row unfolds anything: a sensor list that turned out
    /// to have entries on this machine, or rows folded under it.
    /// </summary>
    public bool CanExpand => Details.Count > 0 || _folded.Count > 0;

    /// <summary>
    /// The arrow only appears once there is something behind it, so a row whose
    /// machine reports nothing extra doesn't advertise a list that is empty.
    /// Hidden rather than collapsed, so it still takes its room: the peaks
    /// then end at the same place on every row, arrow or not. A hidden
    /// element isn't hit-tested, so the row keeps its drag area.
    /// </summary>
    public Visibility IndicatorVisibility
        => CanExpand ? Visibility.Visible : Visibility.Hidden;

    /// <summary>Says what the arrow unfolds - a sensor list, or which rows.</summary>
    public string IndicatorTooltip
        => _folded.Count > 0
            ? $"Show {string.Join(", ", _folded.Select(r => r.Label))}"
            : "Show " + _metric.DetailsAbout;

    /// <summary>
    /// A row folded under another is indented a little, so it reads as part of
    /// that row. Padding rather than margin: the label keeps its width, so the
    /// values and bars still line up with the rows around it.
    /// </summary>
    public Thickness LabelPadding => _metric.FoldsUnder is null ? default : new Thickness(8, 0, 0, 0);

    /// <summary>Right-pointing means there is more to see, down means it is open.</summary>
    public string Indicator => _isExpanded ? "▾" : "▸";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;

            _isExpanded = value;
            Set(nameof(IsExpanded));
            Set(nameof(Indicator));
        }
    }

    /// <summary>
    /// Creates the detail rows from the first snapshot that carries them.
    ///
    /// This cannot happen when the row is built: the sensor library is opened
    /// after the window is already on screen, so at construction time nothing
    /// knows whether this chip reports two temperatures or a dozen. Returns the
    /// new rows so the window can slot them in beneath this one.
    /// </summary>
    public IReadOnlyList<DetailRow> BuildDetails(SensorSnapshot snapshot)
    {
        if (_metric.Details is null || Details.Count > 0)
            return Array.Empty<DetailRow>();

        foreach (var reading in _metric.Details(snapshot))
        {
            var detail = new DetailRow(reading.Name, _metric.Unit, _metric.DetailNote, _theme, _settings);
            detail.Set(reading);
            Details.Add(detail);
        }

        if (Details.Count > 0)
        {
            Set(nameof(IndicatorVisibility));
            Set(nameof(Indicator));
            Set(nameof(CanExpand));   // the hand cursor
            Set(nameof(Tooltip));     // gains its "Click to show" line
        }

        return Details;
    }

    public string Value
    {
        get => _value;
        private set => Set(ref _value, value, nameof(Value));
    }

    public double BarWidth
    {
        get => _barWidth;
        private set
        {
            if (_barWidth == value)
                return;
            Set(ref _barWidth, value, nameof(BarWidth));
            // The knob follows the end of the fill.
            if (_theme.BarLine)
                Set(nameof(KnobMargin));
        }
    }

    public Brush ValueBrush
    {
        get => _valueBrush;
        private set
        {
            if (Equals(_valueBrush, value))
                return;

            _valueBrush = value;
            Set(nameof(ValueBrush));

            // The bar is painted in this colour unless BarColor overrides it,
            // so it has to be told as well - a computed property has nothing
            // else to notice the change.
            Set(nameof(BarBrush));
            Set(nameof(InBarFillBrush));
            Set(nameof(InBarValueBrush));
        }
    }

    /// <summary>
    /// What the amber mark beside the label says on hover - see
    /// <see cref="Metric.Flag"/>. Null while there is nothing to flag.
    /// </summary>
    public string? FlagTooltip
    {
        get => _flag;
        private set
        {
            if (_flag == value)
                return;

            _flag = value;
            Set(nameof(FlagTooltip));
            Set(nameof(FlagVisibility));
        }
    }

    public Visibility FlagVisibility => _flag is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The warning colour, since the mark is a warning however cool the number reads.</summary>
    public Brush FlagBrush => _theme.Warn;

    /// <summary>Pulls this row's number out of a snapshot and restyles it.</summary>
    public void Apply(SensorSnapshot snapshot)
    {
        // Before the early return below: the list has to keep up even on a tick
        // where the headline value is unavailable.
        ApplyDetails(snapshot);

        // Also before it: a flag is about something other than the value, and
        // must not stick on screen because the value went missing.
        try
        {
            FlagTooltip = _metric.Flag?.Invoke(snapshot);
        }
        catch
        {
            FlagTooltip = null;   // as with Read below: a bad lambda mustn't take the widget down
        }

        double? reading = null;
        try
        {
            reading = _metric.Read(snapshot);
        }
        catch
        {
            // A bad lambda in MetricRegistry shouldn't take down the widget.
        }

        if (reading is not double value || double.IsNaN(value))
        {
            Value = "n/a";
            BarWidth = 0;
            NoteFraction(0);
            ValueBrush = _theme.Unavailable;
            return;
        }

        string text = value.ToString(_metric.Format, CultureInfo.InvariantCulture);
        Value = _metric.Unit.Length > 0 ? text + " " + _metric.Unit : text;

        // Only the worst is kept. A live number cannot answer "what did it
        // actually reach while I was playing?", and that is the question.
        if (_worst is null || (_metric.Invert ? value < _worst : value > _worst))
        {
            _worst = value;
            Peak = (_metric.Invert ? "↓" : "↑")
                   + value.ToString(_metric.Format, CultureInfo.InvariantCulture);
            NotePeakMarker();
        }

        // A segmented or dotted bar fills whole blocks or dots: a partly lit
        // one reads as a rendering fault rather than a reading. Rounded to
        // the nearest, so the bar is never more than half a step off.
        double fraction = Fraction(value);
        if (_theme.BarSteps is int steps)
            fraction = Math.Round(fraction * steps) / steps;
        // Against the room inside an outline, not the whole track, so a full
        // reading fills to the outline and no further.
        BarWidth = fraction * Math.Max(0, TrackWidth - 2 * TrackInset);
        NoteFraction(fraction);

        ValueBrush = _metric.Exceeds(value, _metric.Crit) ? _theme.Crit
                   : _metric.Exceeds(value, _metric.Warn) ? _theme.Warn
                   : _theme.Normal;

        // Only told when the colour really moved: a new brush every tick
        // would redraw the knob even with the reading standing still.
        if (_theme.BarLine)
        {
            var knob = KnobColour(fraction);
            if (!(knob is SolidColorBrush now && _knobBrush is SolidColorBrush was && now.Color == was.Color))
                KnobBrush = knob;
        }
    }

    /// <summary>
    /// The gradient's colour at <paramref name="fraction"/> along it, ending
    /// in amber or red with the value as the bar's own gradient does; the
    /// bar's plain brush when there is no gradient.
    /// </summary>
    private Brush KnobColour(double fraction)
    {
        if (_theme.BarGradient is null)
            return BarBrush;

        Color start = _theme.BarGradientStart;
        Color end = ReferenceEquals(_valueBrush, _theme.Crit) && _theme.Crit is SolidColorBrush crit ? crit.Color
                  : ReferenceEquals(_valueBrush, _theme.Warn) && _theme.Warn is SolidColorBrush warn ? warn.Color
                  : _theme.BarGradientEnd;
        // With the gradient across the bar the fill always ends in the end
        // colour, so the knob at its tip does too, or it wouldn't match.
        if (_theme.GradientAcrossBar)
            fraction = 1;
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * fraction);

        var brush = new SolidColorBrush(Color.FromArgb(Mix(start.A, end.A), Mix(start.R, end.R), Mix(start.G, end.G), Mix(start.B, end.B)));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Refreshes the expanded list. The sensors were resolved once at startup
    /// and come back in the same order every time, so this is a walk down two
    /// lists rather than a lookup by name.
    /// </summary>
    private void ApplyDetails(SensorSnapshot snapshot)
    {
        if (_metric.Details is null)
            return;

        try
        {
            var readings = _metric.Details(snapshot);
            int shared = Math.Min(readings.Count, Details.Count);

            for (int i = 0; i < shared; i++)
                Details[i].Set(readings[i]);

            NoteDetailCount(readings.Count);
        }
        catch
        {
            // Same reasoning as Apply: a bad lambda in MetricRegistry must not
            // take the widget down.
        }
    }

    /// <summary>
    /// Set once the list has had a different length from its lines for
    /// <see cref="DetailCountSettles"/> - a drive plugged in or taken out, or a
    /// list that was empty on the first read and has entries now. The window
    /// then calls <see cref="RebuildDetails"/>. Built once and never again, the
    /// list used to keep a removed drive's last figure forever and never show
    /// a new one.
    /// </summary>
    public bool DetailsChanged { get; private set; }

    /// <summary>
    /// How long a new length has to hold before the lines are rebuilt. A
    /// rebuild re-measures the whole panel, and a list that flickers by one
    /// for a tick - a sensor missing a single read - shouldn't do that each time.
    /// </summary>
    private static readonly TimeSpan DetailCountSettles = TimeSpan.FromSeconds(3);

    private int _otherCount = -1;
    private DateTime _otherCountSince;

    private void NoteDetailCount(int count)
    {
        if (count == Details.Count)
        {
            _otherCount = -1;
            return;
        }

        if (count != _otherCount)
        {
            _otherCount = count;
            _otherCountSince = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _otherCountSince >= DetailCountSettles)
            DetailsChanged = true;
    }

    /// <summary>
    /// Makes the lines again for the list as it is now. Returns them, for the
    /// window to put in place of the old ones.
    /// </summary>
    public IReadOnlyList<DetailRow> RebuildDetails(SensorSnapshot snapshot)
    {
        DetailsChanged = false;
        _otherCount = -1;
        Details.Clear();

        var rows = BuildDetails(snapshot);

        // BuildDetails only announces a list that has lines; one that has
        // just lost its last line has to take its arrow away as well.
        if (Details.Count == 0)
        {
            Set(nameof(IndicatorVisibility));
            Set(nameof(CanExpand));
            Set(nameof(Tooltip));
        }

        return rows;
    }

    /// <summary>
    /// Re-reads everything, for a settings change. The filled part of the bar
    /// is left to the next tick: it is derived from the reading, and a tick is
    /// a second away.
    /// </summary>
    public void Refresh()
    {
        Set(null);

        foreach (var detail in Details)
            detail.Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A null name is WPF's "every property on this object changed".</summary>
    private void Set(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
