using System;
using System.Collections.Generic;
using DeskMon.Sensors;

namespace DeskMon.Metrics;

/// <summary>
/// One line on the widget. Everything the UI needs to draw a row lives here, so
/// adding a metric never means touching the window or the sensor plumbing.
/// </summary>
/// <param name="Label">Text in the left column.</param>
/// <param name="Unit">Suffix after the number, e.g. "%" or "°C".</param>
/// <param name="Read">
/// Pulls the value out of a sensor snapshot. Return null when the value is
/// unavailable (missing sensor, no driver) and the row shows "n/a".
/// </param>
/// <param name="Max">Value that fills the bar completely.</param>
/// <param name="Warn">Value at and above which the row turns amber.</param>
/// <param name="Crit">Value at and above which the row turns red.</param>
/// <param name="Min">Value where the bar starts (useful for temperatures).</param>
/// <param name="Format">Standard .NET numeric format for the value.</param>
/// <param name="Invert">
/// Set for metrics where *low* is bad, such as FPS: the Warn and Crit
/// thresholds then trigger when the value falls below them instead of rising
/// above them.
/// </param>
/// <param name="Tooltip">
/// Optional. What this row means, in a sentence, shown on hover.
///
/// For rows whose label has to be short enough for the left column but whose
/// meaning is not obvious from it - the frame-rate lows above all. Leave it
/// null and hovering the row shows the panel's own tooltip, as before.
/// </param>
/// <param name="Details">
/// Optional. Where the row's expandable list comes from - every related sensor,
/// with its own name. Set this and the row gets a '>' that unfolds the list;
/// leave it null and the row behaves exactly as before.
///
/// This is for a reading that is really a family of readings: <c>CpuTemp</c> is
/// one of six temperatures the chip reports, and which one a given tool shows is
/// otherwise impossible to tell. The list is read-only reference - no bars, no
/// thresholds - because the row above it is still the number that matters.
/// </param>
/// <param name="FoldsUnder">
/// Optional. The label of an earlier row in the same section that this row
/// folds away under: that row gets the arrow, and this one shows only while
/// it is unfolded.
///
/// Unlike <paramref name="Details"/>, the folded row stays a full metric row,
/// with its bar, colour and peak - and stays in the session history. That is
/// why the frame-rate lows fold this way rather than as a sensor list: they
/// have thresholds and a worst-of-the-session that a plain list would lose.
/// </param>
/// <param name="DetailsAbout">
/// What <paramref name="Details"/> lists, for "Click to show ..." - "the
/// programs using the most memory". Defaults to the sensors behind the reading.
/// </param>
/// <param name="DetailNote">The second line of each listed row's tooltip, saying what the row is.</param>
/// <param name="AlertName">
/// What an alert calls this reading - "CPU temperature". Defaults to the label
/// and the section's title. Only a row with a <paramref name="Crit"/> level
/// can raise an alert, and only one that isn't inverted: a frame rate low for
/// a minute is a game's business, not a health problem.
/// </param>
/// <param name="Flag">
/// Optional. A warning that isn't a level of the reading itself - the CPU
/// slowing down to cool off, which the temperature alone can't show. Returns
/// what to say about it on hover, or null when there is nothing to flag; the
/// row then shows a small amber mark beside its label.
/// </param>
/// <param name="Alerts">
/// False for a row whose red is only a colour: VRAM full is how many games
/// run on purpose, so a Crit level alone must not raise an alert over one.
/// </param>
public sealed record Metric(
    string Label,
    string Unit,
    Func<SensorSnapshot, double?> Read,
    double Max = 100,
    double Warn = double.PositiveInfinity,
    double Crit = double.PositiveInfinity,
    double Min = 0,
    string Format = "0",
    bool Invert = false,
    string? Tooltip = null,
    Func<SensorSnapshot, IReadOnlyList<SensorReading>>? Details = null,
    string? FoldsUnder = null,
    string DetailsAbout = "every sensor behind this reading",
    string DetailNote = "One of the sensors behind the reading above, as the hardware reports it.",
    string? AlertName = null,
    Func<SensorSnapshot, string?>? Flag = null,
    bool Alerts = true)
{
    /// <summary>
    /// Whether a threshold has been passed. For an inverted metric such as FPS
    /// that means dropping below it rather than rising above it. Here rather
    /// than on MetricRow so the FPS overlay colours its figure the same way.
    /// </summary>
    public bool Exceeds(double value, double threshold)
    {
        if (double.IsInfinity(threshold))
            return false;   // threshold not set

        return Invert ? value <= threshold : value >= threshold;
    }
}
