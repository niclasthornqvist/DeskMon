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
    public double TrackWidth => _settings.BarTrackWidth;

    /// <summary>Floored at 1, since a bar no pixels tall is not a setting.</summary>
    public double TrackHeight => Math.Max(1, _settings.BarTrackHeight);

    /// <summary>
    /// Half the thickness, so the ends stay semicircular whatever height the
    /// bar is given. A fixed radius would square the ends off as it grew.
    /// </summary>
    public CornerRadius BarCorner => new(TrackHeight / 2);

    /// <summary>The groove's colour.</summary>
    public Brush TrackBrush => _theme.BarTrack;

    /// <summary>
    /// The fill: the gradient when BarGradient is on. Otherwise it follows the
    /// value's colour unless BarColor pins it, so the bar reddens with the
    /// number rather than needing to be read separately.
    /// </summary>
    public Brush BarBrush => _theme.BarGradient ?? _theme.Bar ?? _valueBrush;

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
    /// </summary>
    public Visibility IndicatorVisibility
        => CanExpand ? Visibility.Visible : Visibility.Collapsed;

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
        private set => Set(ref _barWidth, value, nameof(BarWidth));
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
        }
    }

    /// <summary>Pulls this row's number out of a snapshot and restyles it.</summary>
    public void Apply(SensorSnapshot snapshot)
    {
        // Before the early return below: the list has to keep up even on a tick
        // where the headline value is unavailable.
        ApplyDetails(snapshot);

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
        }

        double span = _metric.Max - _metric.Min;
        double fraction = span > 0 ? (value - _metric.Min) / span : 0;
        BarWidth = Math.Clamp(fraction, 0, 1) * TrackWidth;

        ValueBrush = _metric.Exceeds(value, _metric.Crit) ? _theme.Crit
                   : _metric.Exceeds(value, _metric.Warn) ? _theme.Warn
                   : _theme.Normal;
    }

    /// <summary>
    /// Refreshes the expanded list. The sensors were resolved once at startup
    /// and come back in the same order every time, so this is a walk down two
    /// lists rather than a lookup by name.
    /// </summary>
    private void ApplyDetails(SensorSnapshot snapshot)
    {
        if (_metric.Details is null || Details.Count == 0)
            return;

        try
        {
            var readings = _metric.Details(snapshot);
            int shared = Math.Min(readings.Count, Details.Count);

            for (int i = 0; i < shared; i++)
                Details[i].Set(readings[i]);
        }
        catch
        {
            // Same reasoning as Apply: a bad lambda in MetricRegistry must not
            // take the widget down.
        }
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
