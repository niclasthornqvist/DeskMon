using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeskMon.Notes;

/// <summary>
/// What held a session's frame rate back: the graphics card, the processor,
/// nothing (it ran at its target), or something else. A low score said a game
/// ran badly but not why, and "do I turn the graphics down or is it the CPU?"
/// is the first question after one.
///
/// Judged every tick while the game's frames count, from loads the widget
/// already reads:
///
///   At target  the frame rate at 95% of the game's target or above
///   GPU        the graphics card at 90% load or more
///   CPU        the card under 85%, and the busiest core at 90% or more -
///              or the CPU as a whole at 85%. Games are mostly held back by
///              one thread, so the busiest core says more than the average
///   Other      neither busy: a frame cap the target doesn't know, the
///              engine waiting on itself, the disk
///
/// The moments between (the card at 85-90%) go to Other rather than a guess.
/// </summary>
public sealed class Bottleneck
{
    public enum Kind { Target, Gpu, Cpu, Other }

    /// <summary>One moment's verdict, or null when there's too little to judge - no frame rate, or no GPU load.</summary>
    public static Kind? Judge(double? fps, double target, double? gpuLoad, double? cpuCoreMax, double? cpuLoad)
    {
        if (fps is not double f || f <= 0 || gpuLoad is not double gpu)
            return null;

        if (f >= target * 0.95)
            return Kind.Target;
        if (gpu >= 90)
            return Kind.Gpu;
        if (gpu < 85 && (cpuCoreMax >= 90 || cpuLoad >= 85))
            return Kind.Cpu;
        return Kind.Other;
    }

    private readonly Dictionary<Kind, double> _seconds = new();
    private DateTime _last;

    /// <summary>
    /// Adds the time since the last sample to <paramref name="kind"/>. By
    /// time, not by tick: the widget ticks slower while hidden, and a tick
    /// there is worth more of the session than one while shown.
    /// </summary>
    public void Add(DateTime now, Kind? kind)
    {
        // Capped, so a gap - DeskMon paused, the PC asleep - can't hand one
        // verdict an hour.
        double seconds = _last == default ? 0 : Math.Min(5, (now - _last).TotalSeconds);
        _last = now;
        if (kind is Kind k && seconds > 0)
            _seconds[k] = _seconds.GetValueOrDefault(k) + seconds;
    }

    /// <summary>The share of judged time per verdict, in whole percent; empty when nothing was judged.</summary>
    public IReadOnlyDictionary<Kind, int> Shares()
    {
        double total = _seconds.Values.Sum();
        return total < 30
            ? new Dictionary<Kind, int>()   // under half a minute says nothing
            : _seconds.ToDictionary(p => p.Key, p => (int)Math.Round(p.Value * 100 / total));
    }

    // --- In the log --------------------------------------------------------------

    /// <summary>"limit gpu 72 cpu 18 target 6 other 4", or null with nothing judged.</summary>
    public string? ToLogPart()
    {
        var shares = Shares();
        if (shares.Count == 0)
            return null;
        return "limit " + string.Join(" ", shares.OrderByDescending(s => s.Value)
                                              .Select(s => $"{Word(s.Key)} {s.Value.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static readonly Regex LogPart = new(@"^limit(?: (?<kind>gpu|cpu|target|other) (?<share>\d+))+$");

    /// <summary>The shares read back from a log line's part, or null when it isn't one.</summary>
    public static IReadOnlyDictionary<Kind, int>? Parse(string part)
    {
        var match = LogPart.Match(part);
        if (!match.Success)
            return null;

        var shares = new Dictionary<Kind, int>();
        var kinds = match.Groups["kind"].Captures;
        var values = match.Groups["share"].Captures;
        for (int i = 0; i < kinds.Count; i++)
            if (int.TryParse(values[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int share))
                shares[FromWord(kinds[i].Value)] = share;
        return shares;
    }

    private static string Word(Kind kind) => kind switch
    {
        Kind.Gpu => "gpu",
        Kind.Cpu => "cpu",
        Kind.Target => "target",
        _ => "other",
    };

    private static Kind FromWord(string word) => word switch
    {
        "gpu" => Kind.Gpu,
        "cpu" => Kind.Cpu,
        "target" => Kind.Target,
        _ => Kind.Other,
    };

    // --- In words ------------------------------------------------------------------

    /// <summary>
    /// "Mostly GPU-limited" and so on - the one with the most time, or
    /// "Mixed" when none had 60% of it, since a 40 / 35 split has no one cause.
    /// </summary>
    public static string Verdict(IReadOnlyDictionary<Kind, int> shares)
    {
        var top = shares.MaxBy(s => s.Value);
        if (top.Value < 60)
            return "Mixed - no one limit";
        return top.Key switch
        {
            Kind.Gpu => "Mostly GPU-limited",
            Kind.Cpu => "Mostly CPU-limited",
            Kind.Target => "At its target - nothing holding it back",
            _ => "Mostly limited by something else",
        };
    }

    /// <summary>What a verdict means for what to do about it - the tooltip's dim note.</summary>
    public static string Advice(IReadOnlyDictionary<Kind, int> shares)
    {
        var top = shares.MaxBy(s => s.Value);
        if (top.Value < 60)
            return "the limit moved between the parts";
        return top.Key switch
        {
            Kind.Gpu => "lower resolution or graphics settings for more fps",
            Kind.Cpu => "graphics settings won't help much - simulation, mods or view distance will",
            Kind.Target => "it ran as fast as asked",
            _ => "a frame cap, the game engine or the disk",
        };
    }

    /// <summary>"GPU 72% · CPU 18% · at target 6% · other 4%", most first.</summary>
    public static string Split(IReadOnlyDictionary<Kind, int> shares)
        => string.Join("  ·  ", shares.OrderByDescending(s => s.Value).Select(s => $"{Name(s.Key)} {s.Value}%"));

    /// <summary>A verdict as one short word, for the timeline's hover.</summary>
    public static string Name(Kind kind) => kind switch
    {
        Kind.Gpu => "GPU",
        Kind.Cpu => "CPU",
        Kind.Target => "at target",
        _ => "other",
    };
}
