using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DeskMon.Metrics;

namespace DeskMon.Notes;

/// <summary>
/// One line of the session history, taken apart so it can be shown properly
/// rather than as the raw line.
///
/// The log stays exactly as it is written - one plain line per session, the
/// format the README documents and that a spreadsheet can import - and this
/// reads it back. Anything a line has that this does not recognise is kept
/// as a note rather than dropped, so a hand-edited or future line still shows
/// everything it says.
/// </summary>
public sealed class SessionRecord
{
    public DateTime Start { get; private init; }
    public DateTime End { get; private init; }

    /// <summary>
    /// How long it ran - the length the log recorded, which DeskMon measured
    /// to the second. The start and end are written to the minute only, so
    /// the difference between them can be a minute out either way: 08:47 to
    /// 08:51 reads as four minutes for a session that ran three and a bit.
    /// </summary>
    public TimeSpan Length => _length ?? End - Start;

    private TimeSpan? _length;

    /// <summary>The whole session's average frame rate, or null if nothing was measured.</summary>
    public double? AverageFps { get; private init; }

    /// <summary>
    /// The frame rate over the slowest 1% and 0.1% of the whole session's
    /// frames, and how much frame times varied (their standard deviation, ms).
    /// Null in sessions logged before 1.57, which only kept the LOW and WORST
    /// peaks - the lowest the widget's last-25-seconds figures ever dipped to.
    /// </summary>
    public double? OnePercentLow { get; private init; }
    public double? PointOnePercentLow { get; private init; }
    public double? JitterMs { get; private init; }

    /// <summary>How many frames were stutters - see FrameStats.Stutters. Null before 1.65.</summary>
    public long? Stutters { get; private init; }

    /// <summary>
    /// What the CPU and GPU drew over the session, in watt-hours - their own
    /// power sensors only, not the whole PC from the wall. Null before 1.65,
    /// or when neither reports its power.
    /// </summary>
    public double? EnergyWh { get; private init; }

    /// <summary>
    /// The GPU driver and Windows build the session was played on, for
    /// marking a session where either changed since the one before. Null
    /// before 1.65, or when they couldn't be read.
    /// </summary>
    public string? Driver { get; private init; }
    public string? WindowsBuild { get; private init; }

    /// <summary>Stutters per hour played, so long and short sessions compare fairly.</summary>
    public double? StuttersPerHour
        => Stutters is long count && Length.TotalHours > 0 ? count / Length.TotalHours : null;

    /// <summary>The worst each row reached, in the order the line lists them.</summary>
    public IReadOnlyList<Peak> Peaks { get; private init; } = Array.Empty<Peak>();

    /// <summary>Anything else on the line, e.g. "cut short, DeskMon closed".</summary>
    public string? Note { get; private init; }

    /// <summary>The line as it is in the file, for copying or a tooltip.</summary>
    public string Raw { get; private init; } = string.Empty;

    /// <summary>
    /// The session's number in its game's history, the first one played being
    /// 1 - what the table's ID column and DELETE show, so the button says which
    /// row it will take out. Counted from the log's order when it is read, not
    /// written into it, so the log keeps its format; deleting a session moves
    /// the ones after it down by one.
    /// </summary>
    public int Id { get; internal set; }

    /// <summary>
    /// The game whose log it came from, set when the log is read - the line
    /// itself doesn't name it. Null for a line parsed on its own. For the
    /// score, which may use a target of the game's own.
    /// </summary>
    public string? Game { get; internal set; }

    /// <summary>The session ended because DeskMon did, not the game - so it may have run longer.</summary>
    public bool CutShort => Note?.Contains("cut short", StringComparison.OrdinalIgnoreCase) == true;

    /// <param name="Label">The row's label on the widget: CPU, FPS, LAT…</param>
    /// <param name="LowerIsWorse">FPS and its lows: the worst is the lowest.</param>
    /// <param name="Value">The number, as written.</param>
    /// <param name="Unit">"%", "°C", "ms" - empty for the frame rates.</param>
    public sealed record Peak(string Label, bool LowerIsWorse, string Value, string Unit)
    {
        /// <summary>
        /// The widget section the row belongs to - "Usage", "Temperature",
        /// "Game" - found by label and unit, since CPU is a row in two of them
        /// and only the unit tells them apart. Null for a row no longer in
        /// MetricRegistry.
        /// </summary>
        public string? Section => MetricRegistry.Sections
            .FirstOrDefault(s => s.Metrics.Any(m => m.Label == Label && m.Unit == Unit))?.Title;

        /// <summary>"↑76 °C", the way the widget's peak column writes it.</summary>
        public string Text => (LowerIsWorse ? "↓" : "↑") + Value + (Unit.Length > 0 ? " " + Unit : string.Empty);

        public double? Number
            => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : null;
    }

    /// <summary>The peak for a row, e.g. ("CPU", "°C"), or null if the line has none.</summary>
    public Peak? Find(string label, string unit)
        => Peaks.FirstOrDefault(p => p.Label == label && p.Unit == unit);

    private static readonly Regex When = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}) (?<from>\d{2}:\d{2})\s*[–-]\s*(?<to>\d{2}:\d{2})$");

    private static readonly Regex Duration = new(@"^(?:(?<h>\d+) h)?\s*(?:(?<m>\d+) min)?$");

    private static readonly Regex Average = new(@"^avg (?<fps>[\d.]+) fps$");

    private static readonly Regex SessionLow = new(@"^(?<share>1|0\.1)% low (?<fps>[\d.]+) fps$");

    private static readonly Regex Jitter = new(@"^jitter (?<ms>[\d.]+) ms$");

    private static readonly Regex StutterPart = new(@"^stutters (?<n>\d+)$");

    private static readonly Regex EnergyPart = new(@"^energy (?<wh>[\d.]+) Wh$");

    // Before PeakPart, which "driver 581.57" doesn't match anyway (no arrow) -
    // but checked first all the same, so a version can never end up a note.
    private static readonly Regex DriverPart = new(@"^driver (?<v>\S+)$");

    private static readonly Regex WindowsPart = new(@"^windows (?<v>\S+)$");

    private static readonly Regex PeakPart =new(@"^(?<label>\S+) (?<dir>[↑↓])(?<value>[\d.]+)(?: (?<unit>\S+))?$");

    /// <summary>
    /// Reads a line as MainWindow.DescribeSession writes it. Null for a line
    /// that does not start with a date and time - the heading, a blank, or
    /// something that was never a session.
    /// </summary>
    public static SessionRecord? Parse(string line)
    {
        string[] parts = line.Split(" · ", StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return null;

        var when = When.Match(parts[0]);
        if (!when.Success
            || !DateTime.TryParseExact($"{when.Groups["date"].Value} {when.Groups["from"].Value}",
                                       "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var start)
            || !TimeSpan.TryParseExact(when.Groups["to"].Value, @"hh\:mm", CultureInfo.InvariantCulture, out var endTime))
            return null;

        // The end is written as a time only. One earlier than the start means
        // the session ran past midnight.
        var end = start.Date + endTime;
        if (end < start)
            end = end.AddDays(1);

        double? average = null, onePercent = null, pointOnePercent = null, jitter = null, energy = null;
        long? stutters = null;
        string? driver = null, windows = null;
        TimeSpan? length = null;
        var peaks = new List<Peak>();
        var notes = new List<string>();

        foreach (string part in parts.Skip(1))
        {
            if (part.Length > 0 && Duration.Match(part) is { Success: true } duration)
            {
                int hours = duration.Groups["h"].Success ? int.Parse(duration.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
                int minutes = duration.Groups["m"].Success ? int.Parse(duration.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                length = new TimeSpan(hours, minutes, 0);
                continue;
            }

            if (Average.Match(part) is { Success: true } avg
                && double.TryParse(avg.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps))
            {
                average = fps;
                continue;
            }

            if (SessionLow.Match(part) is { Success: true } low
                && double.TryParse(low.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lowFps))
            {
                if (low.Groups["share"].Value == "1")
                    onePercent = lowFps;
                else
                    pointOnePercent = lowFps;
                continue;
            }

            if (Jitter.Match(part) is { Success: true } varied
                && double.TryParse(varied.Groups["ms"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
            {
                jitter = ms;
                continue;
            }

            if (StutterPart.Match(part) is { Success: true } stutter
                && long.TryParse(stutter.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
            {
                stutters = count;
                continue;
            }

            if (EnergyPart.Match(part) is { Success: true } drawn
                && double.TryParse(drawn.Groups["wh"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double wh))
            {
                energy = wh;
                continue;
            }

            if (DriverPart.Match(part) is { Success: true } gpuDriver)
            {
                driver = gpuDriver.Groups["v"].Value;
                continue;
            }

            if (WindowsPart.Match(part) is { Success: true } build)
            {
                windows = build.Groups["v"].Value;
                continue;
            }

            if (PeakPart.Match(part) is { Success: true } peak)
            {
                peaks.Add(new Peak(peak.Groups["label"].Value,
                                   peak.Groups["dir"].Value == "↓",
                                   peak.Groups["value"].Value,
                                   peak.Groups["unit"].Success ? peak.Groups["unit"].Value : string.Empty));
                continue;
            }

            notes.Add(part);
        }

        return new SessionRecord
        {
            Start = start,
            End = end,
            _length = length,
            AverageFps = average,
            OnePercentLow = onePercent,
            PointOnePercentLow = pointOnePercent,
            JitterMs = jitter,
            Stutters = stutters,
            EnergyWh = energy,
            Driver = driver,
            WindowsBuild = windows,
            Peaks = peaks,
            Note = notes.Count > 0 ? string.Join(" · ", notes) : null,
            Raw = line,
        };
    }

    /// <summary>"2 h 3 min", or just minutes under an hour - as the log writes it.</summary>
    public static string Describe(TimeSpan length)
        => length.TotalHours >= 1
            ? $"{(int)length.TotalHours} h {length.Minutes} min"
            : $"{Math.Max(0, (int)length.TotalMinutes)} min";

    /// <summary>
    /// The peaks grouped by the widget section they belong to, in the widget's
    /// own order - so the history reads like the panel it was measured on.
    /// Peaks from a row no longer on the widget come last, under "Other".
    /// </summary>
    public IEnumerable<(string Section, IReadOnlyList<Peak> Peaks)> PeaksBySection()
    {
        var order = MetricRegistry.Sections.Select(s => s.Title).ToList();

        return Peaks
            .GroupBy(p => p.Section ?? "Other")
            .OrderBy(g => order.IndexOf(g.Key) is var i and >= 0 ? i : int.MaxValue)
            .Select(g => (g.Key, (IReadOnlyList<Peak>)g.ToList()));
    }
}
