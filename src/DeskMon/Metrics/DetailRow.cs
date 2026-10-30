using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace DeskMon.Metrics;

/// <summary>
/// One line of an expanded metric's list: a sensor's own name and its current
/// value.
///
/// Deliberately plainer than a <see cref="MetricRow"/> - no bar, no warning
/// colours, a smaller and dimmer font. These are reference readings, there to
/// be compared against another tool or to answer "which one is it showing?".
/// The row above them is still the number the widget is for, and it should stay
/// the thing the eye lands on.
/// </summary>
public sealed class DetailRow : INotifyPropertyChanged, IPanelRow
{
    private readonly UiTheme _theme;
    private readonly AppSettings _settings;
    private readonly string _unit;
    private readonly string _note;
    private string _value = "n/a";
    private string _label;

    /// <param name="note">What these rows are, for the tooltip - see <see cref="Metric.DetailNote"/>.</param>
    public DetailRow(string name, string unit, string note, UiTheme theme, AppSettings settings)
    {
        _theme = theme;
        _settings = settings;
        _unit = unit;
        _note = note;
        _label = name;
    }

    /// <summary>
    /// The sensor's name as the library reports it, e.g. "CCD1 (Tdie)" - or
    /// for a list of things that change, like the programs using the most
    /// memory, whichever is in this place now.
    /// </summary>
    public string Label
    {
        get => _label;
        private set
        {
            if (_label == value)
                return;

            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
        }
    }

    /// <summary>
    /// The full name - a long one is cut short with "…" on the panel - and
    /// what these rows are, as a card like the metric rows' (see Ui.InfoTip).
    /// A "Right-click …" line in the note goes to the card's foot with the
    /// other hints, rather than reading as part of the explanation.
    /// </summary>
    public object Tooltip
    {
        get
        {
            var lines = _note.Split('\n');
            bool IsHint(string line) => line.StartsWith("Right-click", System.StringComparison.Ordinal)
                                        || line.StartsWith("Click", System.StringComparison.Ordinal);
            return Ui.InfoTip.Make(_theme, null, Label,
                                   string.Join("\n", lines.Where(l => !IsHint(l))),
                                   hints: lines.Where(IsHint));
        }
    }

    public double ValueWidth => _settings.ValueWidth;
    /// <summary>
    /// One point under a metric row: smaller keeps the list from competing
    /// with the row above, but at 1.5 the names and values were hard to read.
    /// </summary>
    public double FontSize => System.Math.Max(8, _settings.FontSize - 1);

    /// <summary>Re-reads everything, for a settings change.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public Brush LabelBrush => _theme.Label;

    /// <summary>
    /// The label colour - still quieter than a measured value, which keeps its
    /// warning colours, but the header colour made the readings hard to see.
    /// </summary>
    public Brush ValueBrush => _theme.Label;

    public string Value
    {
        get => _value;
        private set
        {
            if (_value == value)
                return;

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    /// <summary>
    /// The program's process name when this row is one of the memory lists -
    /// what right-click > End acts on. Null for a sensor or a drive, which
    /// get no menu of their own.
    /// </summary>
    public string? Key { get; private set; }

    /// <summary>Name and value both - the name changes in a list of things that come and go.</summary>
    public void Set(Sensors.SensorReading reading)
    {
        Label = reading.Name;
        Key = reading.Key;

        if (reading.Text is { } text)
            Value = text;
        else
            SetValue(reading.Value);
    }

    /// <summary>
    /// One decimal, unlike the row above. These are for comparing against
    /// another tool, and that is where the tenths matter.
    /// </summary>
    public void SetValue(double? reading)
    {
        if (reading is not double value || double.IsNaN(value))
        {
            Value = "n/a";
            return;
        }

        string text = value.ToString("0.0", CultureInfo.InvariantCulture);
        Value = _unit.Length > 0 ? text + " " + _unit : text;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
