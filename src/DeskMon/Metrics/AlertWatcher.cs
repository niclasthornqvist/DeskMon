using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeskMon.Sensors;

namespace DeskMon.Metrics;

/// <summary>
/// Notices a reading that has stayed in the red - at or past its Crit level -
/// for a while, and says so once. The widget's colours already show it, but
/// only to someone looking at the widget; mid-game nobody is.
///
/// Which readings: every row in MetricRegistry with a Crit level that isn't
/// inverted, unless it says Alerts: false. That leaves out the frame rates - a
/// game running slowly is the game's business, not a sign of trouble with the
/// PC - and VRAM and LAT, whose red is only a colour. A row added to the
/// registry with a Crit level can alert without anything here changing.
/// </summary>
public sealed class AlertWatcher
{
    /// <summary>
    /// The least time between two alerts for the same reading, even when it
    /// recovered in between: a temperature hovering on its red line would
    /// otherwise announce itself every time it crossed back over.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    private readonly AppSettings _settings;
    private readonly List<(Metric Metric, string Name)> _watched;
    private readonly Dictionary<Metric, DateTime> _redSince = new();
    private readonly Dictionary<Metric, DateTime> _announced = new();

    /// <summary>
    /// Readings already announced and not yet recovered - out of the red for
    /// as long as it takes to alert - with when they last were red. A drive
    /// that stays 96 % full all day is said once, not every ten minutes; it
    /// is said again only after it has been dealt with and filled up again.
    /// </summary>
    private readonly Dictionary<Metric, DateTime> _stillRed = new();

    public AlertWatcher(AppSettings settings)
    {
        _settings = settings;
        _watched = MetricRegistry.Sections
            .SelectMany(s => s.Metrics.Select(m => (Section: s, Metric: m)))
            .Where(x => !double.IsInfinity(x.Metric.Crit) && !x.Metric.Invert && x.Metric.Alerts)
            .Select(x => (x.Metric, x.Metric.AlertName ?? $"{x.Metric.Label} {x.Section.Title.ToLowerInvariant()}"))
            .ToList();
    }

    /// <summary>
    /// Looks at one snapshot. Returns what has just become worth saying - "CPU
    /// temperature 92 °C" - or nothing. Call it for every reading; a reading
    /// that drops out of the red starts its count again.
    /// </summary>
    public IReadOnlyList<string> Check(SensorSnapshot snapshot)
    {
        if (!_settings.Alerts)
        {
            _redSince.Clear();
            _stillRed.Clear();
            return Array.Empty<string>();
        }

        var now = DateTime.UtcNow;
        var hold = TimeSpan.FromSeconds(Math.Max(5, _settings.AlertAfterSeconds));
        List<string>? said = null;

        foreach (var (metric, name) in _watched)
        {
            double? value;
            try { value = metric.Read(snapshot); }
            catch { value = null; }

            if (value is not double v || double.IsNaN(v) || !metric.Exceeds(v, metric.Crit))
            {
                _redSince.Remove(metric);

                // Recovered once it has stayed out of the red as long as it
                // had to stay in it to alert - not on a one-tick dip.
                if (_stillRed.TryGetValue(metric, out var lastRed) && now - lastRed >= hold)
                    _stillRed.Remove(metric);
                continue;
            }

            if (_stillRed.ContainsKey(metric))
            {
                _stillRed[metric] = now;   // announced already, and still red
                continue;
            }

            if (!_redSince.TryGetValue(metric, out var since))
            {
                _redSince[metric] = now;
                continue;
            }

            if (now - since < hold
                || (_announced.TryGetValue(metric, out var last) && now - last < Quiet))
                continue;

            _announced[metric] = now;
            _stillRed[metric] = now;
            string number = v.ToString(metric.Format, CultureInfo.InvariantCulture);
            (said ??= new()).Add(metric.Unit.Length > 0 ? $"{name} {number} {metric.Unit}" : $"{name} {number}");
        }

        return (IReadOnlyList<string>?)said ?? Array.Empty<string>();
    }
}
