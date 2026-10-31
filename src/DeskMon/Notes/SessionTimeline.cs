using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DeskMon.Notes;

/// <summary>
/// A session over time: one point every 10 seconds with the frame rate, its
/// 1% low and the CPU and GPU temperatures. The log line keeps a session's
/// totals - its average and the worst it reached - which can't say *when*
/// it dipped or ran hot; this can, for the session history's timeline.
///
/// A small text file per session beside the game's log, named by the
/// session's start minute - "Factorio.timeline\2026-10-25 02-39.txt" - not by
/// its ID, since IDs shift when a session before it is deleted. Three hours
/// is about 1,100 lines, some 20 KB. Plain text like the logs, so it can be
/// read in Notepad.
/// </summary>
public sealed class SessionTimeline
{
    /// <summary>How often a point is taken - 10 s: enough to show a dip, small enough to keep for every session.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    /// <param name="At">Since the session started.</param>
    /// <param name="Loading">Taken while FrameStats was still leaving the loading screen out - shaded on the chart.</param>
    /// <param name="Ping">The average ping over the point, ms; null with no ping host or no answer.</param>
    /// <param name="Limit">What held the frame rate back for most of the point - see Bottleneck.</param>
    public sealed record Point(TimeSpan At, double? Fps, double? Low, double? CpuTemp, double? GpuTemp, bool Loading,
                               double? Ping = null, Bottleneck.Kind? Limit = null);

    public DateTime Start { get; }

    private readonly List<Point> _points = new();
    public IReadOnlyList<Point> Points => _points;

    public SessionTimeline(DateTime start) => Start = start;

    private SessionTimeline(DateTime start, List<Point> points)
    {
        Start = start;
        _points = points;
    }

    // --- Recording ---------------------------------------------------------------
    //
    // Every tick's readings are averaged into the point, rather than the one
    // reading at the 10 s mark: a single tick can land on a hitch and draw a
    // dip that wasn't there.

    private DateTime _bucketStart;
    private double _fps, _low, _cpu, _gpu, _ping;
    private int _fpsCount, _lowCount, _cpuCount, _gpuCount, _pingCount;
    private readonly Dictionary<Bottleneck.Kind, int> _limits = new();
    private bool _loading;

    /// <summary>One tick's readings; a point is written each time <see cref="Every"/> has passed.</summary>
    public void Sample(DateTime now, double? fps, double? low, double? cpuTemp, double? gpuTemp, bool loading,
                       double? ping = null, Bottleneck.Kind? limit = null)
    {
        if (_bucketStart == default)
            _bucketStart = now;

        if (fps is double f && f > 0) { _fps += f; _fpsCount++; }
        if (low is double l && l > 0) { _low += l; _lowCount++; }
        if (cpuTemp is double c) { _cpu += c; _cpuCount++; }
        if (gpuTemp is double g) { _gpu += g; _gpuCount++; }
        if (ping is double p) { _ping += p; _pingCount++; }
        if (limit is Bottleneck.Kind k) _limits[k] = _limits.GetValueOrDefault(k) + 1;
        _loading |= loading && fps is not null;

        // In UTC: when the clocks go back, local time runs backwards for a
        // moment and no point would be written for the hour that followed.
        var nowUtc = now.ToUniversalTime();
        if (nowUtc - _bucketStart.ToUniversalTime() < Every)
            return;

        _points.Add(new Point(_bucketStart.ToUniversalTime() - Start.ToUniversalTime(),
                              _fpsCount > 0 ? _fps / _fpsCount : null,
                              _lowCount > 0 ? _low / _lowCount : null,
                              _cpuCount > 0 ? _cpu / _cpuCount : null,
                              _gpuCount > 0 ? _gpu / _gpuCount : null,
                              _loading,
                              _pingCount > 0 ? _ping / _pingCount : null,
                              // The verdict most ticks of the point had.
                              _limits.Count > 0 ? _limits.MaxBy(l => l.Value).Key : null));

        _bucketStart = now;
        _fps = _low = _cpu = _gpu = _ping = 0;
        _fpsCount = _lowCount = _cpuCount = _gpuCount = _pingCount = 0;
        _limits.Clear();
        _loading = false;
    }

    // --- File ----------------------------------------------------------------------

    /// <summary>The heading's start, written and read back by the same text - see Load.</summary>
    private const string StartedPrefix = "# DeskMon session timeline · started ";

    /// <summary>The file a session's timeline is kept in, beside its game's log.</summary>
    /// <param name="logPath">The game's log - see SessionHistory.PathFor.</param>
    public static string PathFor(string logPath, DateTime start)
        => Path.Combine(FolderFor(logPath), start.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture) + ".txt");

    /// <summary>The game's folder of timelines: "Factorio.log" has "Factorio.timeline".</summary>
    public static string FolderFor(string logPath) => Path.ChangeExtension(logPath, ".timeline");

    /// <summary>
    /// The points as the file holds them: a heading, then "seconds,fps,low,cpu,gpu,loading"
    /// per line, empty where nothing was read. Static, from a copy of the
    /// points, so AtomicFile's writer can make it while the tick adds more.
    /// </summary>
    public static string ToText(DateTime start, IReadOnlyList<Point> points)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine($"{StartedPrefix}{start.ToString("yyyy-MM-dd HH:mm:ss", inv)} · a point every {Every.TotalSeconds:0} s");
        text.AppendLine("# seconds,fps,1% low,cpu °C,gpu °C,loading,ping ms,limit");

        static string Cell(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty;
        foreach (var p in points)
            text.AppendLine(string.Join(",", ((int)p.At.TotalSeconds).ToString(inv), Cell(p.Fps), Cell(p.Low),
                                        Cell(p.CpuTemp), Cell(p.GpuTemp), p.Loading ? "1" : "0",
                                        Cell(p.Ping), p.Limit is Bottleneck.Kind k ? LimitLetter(k) : string.Empty));
        return text.ToString();
    }

    /// <summary>
    /// The timeline for the session that started at <paramref name="start"/>,
    /// or null when it has none - logged before 1.73, or its file deleted.
    /// A line that doesn't read is skipped rather than losing the rest: the
    /// file is plain text anyone can edit.
    /// </summary>
    public static SessionTimeline? Load(string logPath, DateTime start)
    {
        try
        {
            string path = PathFor(logPath, start);
            if (!File.Exists(path))
                return null;

            var points = new List<Point>();
            // The exact start from the heading, to the second: the log keeps it
            // to the minute only, and the points count from the real start, so
            // clock times worked out from the log's could be up to 59 s early.
            var exact = start;
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith(StartedPrefix, StringComparison.Ordinal) && line.Length >= StartedPrefix.Length + 19
                    && DateTime.TryParseExact(line.Substring(StartedPrefix.Length, 19), "yyyy-MM-dd HH:mm:ss",
                                              CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)
                    && started - start < TimeSpan.FromMinutes(1) && started >= start)
                    exact = started;

                if (line.Length == 0 || line[0] == '#')
                    continue;

                string[] cells = line.Split(',');
                if (cells.Length < 6 || !int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                    continue;

                static double? Read(string cell)
                    => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
                // Ping and limit from 1.73's second build on; a file without
                // them reads as before.
                points.Add(new Point(TimeSpan.FromSeconds(seconds), Read(cells[1]), Read(cells[2]),
                                     Read(cells[3]), Read(cells[4]), cells[5] == "1",
                                     cells.Length > 6 ? Read(cells[6]) : null,
                                     cells.Length > 7 ? LimitFrom(cells[7]) : null));
            }

            return points.Count > 0 ? new SessionTimeline(exact, points) : null;
        }
        catch (Exception ex)
        {
            App.Log("reading a session timeline", ex);
            return null;
        }
    }

    private static string LimitLetter(Bottleneck.Kind kind) => kind switch
    {
        Bottleneck.Kind.Gpu => "g",
        Bottleneck.Kind.Cpu => "c",
        Bottleneck.Kind.Target => "t",
        _ => "o",
    };

    private static Bottleneck.Kind? LimitFrom(string cell) => cell switch
    {
        "g" => Bottleneck.Kind.Gpu,
        "c" => Bottleneck.Kind.Cpu,
        "t" => Bottleneck.Kind.Target,
        "o" => Bottleneck.Kind.Other,
        _ => null,
    };

    /// <summary>Deletes the timelines of <paramref name="starts"/>; quiet about ones already gone.</summary>
    public static void Delete(string logPath, IEnumerable<DateTime> starts)
    {
        foreach (var start in starts)
        {
            try
            {
                File.Delete(PathFor(logPath, start));
            }
            catch (Exception ex)
            {
                App.Log("deleting a session timeline", ex);
            }
        }
    }

    /// <summary>The game's whole folder of timelines, for DELETE ALL.</summary>
    public static void DeleteAll(string logPath)
    {
        try
        {
            string folder = FolderFor(logPath);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            App.Log("deleting a game's timelines", ex);
        }
    }

    /// <summary>The highest frame rate in it, for the chart's scale; null with no frame rate at all.</summary>
    public double? HighestFps => _points.Select(p => p.Fps).OfType<double>().DefaultIfEmpty().Max() is double max && max > 0 ? max : null;
}
