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

    /// <summary>
    /// The loading screen left out of the frame figures - see FrameStats.
    /// Null before 1.73, whose figures still counted it: those sessions' lows
    /// and score can read lower than the game ran, and their tooltip says so.
    /// </summary>
    public TimeSpan? LoadingLeftOut { get; private init; }

    /// <summary>What held the frame rate back, as shares of play in percent - see Bottleneck. Null before 1.73.</summary>
    public IReadOnlyDictionary<Bottleneck.Kind, int>? Limits { get; private init; }

    /// <summary>
    /// The connection over the session: average ping, jitter (ms) and the share
    /// of pings lost - see PingTotals. Null before 1.73, or with no ping host set.
    /// </summary>
    public double? PingMs { get; private init; }
    public double? PingJitterMs { get; private init; }
    public double? PingLossPercent { get; private init; }

    /// <summary>The frame cap the game sat at - see FrameStats.CapFps. Null when it wasn't held at one, or before 1.73.</summary>
    public int? CapFps { get; private init; }

    /// <summary>A measured session from before loading screens were left out - see <see cref="LoadingLeftOut"/>.</summary>
    public bool CountedLoading => AverageFps is not null && LoadingLeftOut is null;

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

    private static readonly Regex LoadingPart = new(@"^loading (?<s>\d+) s$");

    private static readonly Regex CapPart = new(@"^cap (?<fps>\d+) fps$");

    // Lower-case "ping", unlike the PING peak ("PING ↑17 ms"), which PeakPart reads.
    private static readonly Regex PingPart = new(@"^ping (?<ms>[\d.]+) ms ±(?<jitter>[\d.]+) loss (?<loss>[\d.]+) %$");

    private static readonly Regex EnergyPart = new(@"^energy (?<wh>[\d.]+) Wh$");

    // Before PeakPart, which "driver 581.57" doesn't match anyway (no arrow) -
    // but checked first all the same, so a version can never end up a note.
    private static readonly Regex DriverPart = new(@"^driver (?<v>\S+)$");

    private static readonly Regex WindowsPart = new(@"^windows (?<v>\S+)$");

    private static readonly Regex PeakPart =new(@"^(?<label>\S+) (?<dir>[↑↓])(?<value>[\d.]+)(?: (?<unit>\S+))?$");

    /// <summary>
    /// Whether Parse would take the line as a session: its first part a date
    /// and times. For counting sessions without parsing all of each line -
    /// see SessionHistory.LastSession. The same test Parse makes first.
    /// </summary>
    public static bool StartsLikeSession(string line)
    {
        int end = line.IndexOf(" · ", StringComparison.Ordinal);
        var when = When.Match((end < 0 ? line : line[..end]).Trim());
        return when.Success
               && DateTime.TryParseExact($"{when.Groups["date"].Value} {when.Groups["from"].Value}",
                                         "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
               && TimeSpan.TryParseExact(when.Groups["to"].Value, @"hh\:mm", CultureInfo.InvariantCulture, out _);
    }

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
        TimeSpan? loading = null;
        int? cap = null;
        IReadOnlyDictionary<Bottleneck.Kind, int>? limits = null;
        double? ping = null, jitterPing = null, loss = null;
        string? driver = null, windows = null;
        TimeSpan? length = null;
        var peaks = new List<Peak>();
        var notes = new List<string>();

        foreach (string part in parts.Skip(1))
        {
            // TryParse and a sane limit: a mistyped number that overflowed threw
            // out of here, and the catch in SessionHistory.Sessions then showed
            // the whole game as having no sessions. Too big, it stays a note.
            if (part.Length > 0 && Duration.Match(part) is { Success: true } duration
                && TryWhole(duration.Groups["h"], out int hours) && TryWhole(duration.Groups["m"], out int minutes)
                && hours <= MostHours && minutes <= MostHours)
            {
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

            if (LoadingPart.Match(part) is { Success: true } load
                && int.TryParse(load.Groups["s"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            {
                loading = TimeSpan.FromSeconds(seconds);
                continue;
            }

            if (CapPart.Match(part) is { Success: true } capped
                && int.TryParse(capped.Groups["fps"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int capFps))
            {
                cap = capFps;
                continue;
            }

            if (Bottleneck.Parse(part) is { } shares)
            {
                limits = shares;
                continue;
            }

            if (PingPart.Match(part) is { Success: true } net
                && double.TryParse(net.Groups["ms"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pingMs)
                && double.TryParse(net.Groups["jitter"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pingJitter)
                && double.TryParse(net.Groups["loss"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pingLoss))
            {
                (ping, jitterPing, loss) = (pingMs, pingJitter, pingLoss);
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

        // An end the clock put before the start needn't be past midnight: a
        // session across the autumn change can run 02:50-02:20 on one night,
        // and was shown ending the next day. The length, which is measured,
        // says which day fits.
        if (length is { } ran && end.Date > start.Date)
        {
            var expected = start + ran;
            var sameDay = end.AddDays(-1);
            if ((sameDay - expected).Duration() < (end - expected).Duration())
                end = sameDay;
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
            LoadingLeftOut = loading,
            CapFps = cap,
            Limits = limits,
            PingMs = ping,
            PingJitterMs = jitterPing,
            PingLossPercent = loss,
            EnergyWh = energy,
            Driver = driver,
            WindowsBuild = windows,
            Peaks = peaks,
            Note = notes.Count > 0 ? string.Join(" · ", notes) : null,
            Raw = line,
        };
    }

    /// <summary>Far past any session, and well inside what a TimeSpan can hold.</summary>
    private const int MostHours = 1_000_000;

    /// <summary>A group of digits as a number; a group that didn't match is zero.</summary>
    private static bool TryWhole(Group group, out int value)
    {
        value = 0;
        return !group.Success || int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
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
