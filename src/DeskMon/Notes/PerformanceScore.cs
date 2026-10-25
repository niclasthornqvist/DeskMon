using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeskMon.Metrics;

namespace DeskMon.Notes;

/// <summary>
/// A session's performance as one number out of 100, and a game's as the
/// average of its recent sessions - so the games that need their settings
/// turned down stand out in the sessions window without reading every column.
///
/// Worked out from what the log already holds, every time it is shown, rather
/// than written into the log: the line keeps its format, every session ever
/// logged gets a score, and changing TargetFps re-scores the whole history by
/// the same rule instead of leaving old and new scores on different scales.
///
/// Five parts, each worth a share of the 100:
///
///   Frame rate        35  average FPS against the target
///   1% lows           25  the session's 1% low against the target
///   Stability         20  that 1% low against the average: the dips you feel
///   Frame times       10  how much frame times vary against their average:
///                         even pacing, which the frame rate alone can't show
///   Thermals and RAM  10  the hottest CPU and GPU and the fullest RAM, against
///                         the widget's own amber and red levels
///
/// Each part scores from 0 to 1 and is multiplied by its share. A part the
/// session has no reading for is left out and the others share its points, so
/// a missing sensor neither costs nor earns anything. No score at all without
/// a measured frame rate: the frame rate is what the score is about.
///
/// The lows and frame times are over the session's every frame (see
/// FrameStats), written into the log from 1.57 on - from 1.66 the 1% low
/// leaves out frames of 100 ms or more, the loading screens; sessions logged
/// before keep the 1% low they were written with. Not the LOW and WORST
/// peaks: those are the lowest the widget's last-25-seconds figures dipped to,
/// which is a loading screen at 1 fps in almost every game. A session from
/// before 1.57 is scored on its frame rate and thermals only, and says so.
/// </summary>
public static class PerformanceScore
{
    /// <summary>One part of a score, for the breakdown in a tooltip.</summary>
    /// <param name="Detail">What was measured, e.g. "142 fps against a 144 target".</param>
    /// <param name="Points">What it earned, out of <paramref name="Of"/>.</param>
    public sealed record Part(string Name, string Detail, double Points, double Of);

    /// <param name="Partial">Logged before the session-wide lows were, so scored without them - see the class summary.</param>
    public sealed record Result(int Score, IReadOnlyList<Part> Parts, bool Partial);

    /// <summary>How many of a game's latest scored sessions make its score - recent enough that a patch or new settings show.</summary>
    public const int GameSessions = 10;

    private const double FrameRateShare = 35, LowShare = 25, StabilityShare = 20, FrameTimeShare = 10, ThermalShare = 10;

    private static AppSettings? _settings;

    /// <summary>For TargetFps, read every time a score is worked out so a change in the settings shows at once.</summary>
    public static void Use(AppSettings settings) => _settings = settings;

    /// <summary>The frame rate that earns full marks; floored so a hand-edited 0 can't divide by nothing.</summary>
    public static double Target => Math.Max(10, _settings?.TargetFps ?? 60);

    /// <summary>
    /// The target <paramref name="game"/> is scored against: its own, set in
    /// the sessions window, or <see cref="Target"/>. A game locked to 60 can't
    /// earn full marks against a 240 Hz monitor however well it runs.
    /// </summary>
    public static double TargetFor(string? game)
        => game is not null && _settings?.GameTargetFps.TryGetValue(game, out double own) == true && double.IsFinite(own)
            ? Math.Max(10, own)
            : Target;

    /// <summary>Whether <paramref name="game"/> has a target of its own rather than Settings' one.</summary>
    public static bool HasOwnTarget(string? game)
        => game is not null && _settings?.GameTargetFps.ContainsKey(game) == true;

    /// <summary>
    /// The session's score and how it was reached, or null when no frame rate
    /// was measured. Worked out once per session and target: the history's
    /// table asked three or four times a row - for the cell, its colour and
    /// the sort - on every redraw and every click on a heading. Weakly held,
    /// so a record re-read from the log takes its score with it when it goes.
    /// </summary>
    public static Result? For(SessionRecord session)
    {
        // The game's own target where it has one; kept with the score, so a
        // change to either target re-scores on the next ask.
        double target = TargetFor(session.Game);
        lock (Scores)
        {
            if (Scores.TryGetValue(session, out var known) && known.Target == target)
                return known.Result;
        }

        var result = Score(session, target);
        lock (Scores)
            Scores.AddOrUpdate(session, new Scored(target, result));
        return result;
    }

    private sealed record Scored(double Target, Result? Result);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionRecord, Scored> Scores = new();

    private static Result? Score(SessionRecord session, double target)
    {
        if (session.AverageFps is not double average || average <= 0)
            return null;

        var parts = new List<(string Name, string Detail, double Value, double Share)>
        {
            ("Frame rate", $"{Fps(average)} average against a {Fps(target)} target",
             Clamp(average / target), FrameRateShare),
        };

        if (session.OnePercentLow is double low)
        {
            parts.Add(("1% lows", $"{Fps(low)} against the target", Clamp(low / target), LowShare));

            // Full marks with the 1% low at 70% of the average or closer, none
            // at 30% or below: by then the dips are what the game feels like,
            // whatever the average says.
            parts.Add(("Stability", $"1% low at {Percent(low / average)} of the average",
                       Between(low / average, 0.3, 0.7), StabilityShare));
        }

        if (session.JitterMs is double jitter)
        {
            // Against the average frame time, so 3 ms of variation counts the
            // same at 60 fps as 1.2 ms at 144: full marks at 15% or less of
            // the average, none at 60% - frames that uneven feel like stutter.
            double share = jitter / (1000 / average);
            parts.Add(("Frame times", $"vary by {jitter.ToString("0.0", CultureInfo.InvariantCulture)} ms, {Percent(share)} of the average",
                       1 - Between(share, 0.15, 0.6), FrameTimeShare));
        }

        if (Thermals(session) is { } thermal)
            parts.Add(("Thermals and RAM", thermal.Detail, thermal.Value, ThermalShare));

        // The shares of the parts there are, scaled back up to 100.
        double shares = parts.Sum(p => p.Share);
        var scored = parts
            .Select(p => new Part(p.Name, p.Detail, p.Value * p.Share * 100 / shares, p.Share * 100 / shares))
            .ToList();

        return new Result((int)Math.Round(scored.Sum(p => p.Points)), scored,
                          Partial: session.OnePercentLow is null && session.JitterMs is null);
    }

    /// <summary>
    /// The game's score: its latest <see cref="GameSessions"/> scored sessions,
    /// weighted by how long each ran, so a two-minute launcher session doesn't
    /// count as much as an evening. Null when none has a score.
    /// </summary>
    public static int? ForGame(IEnumerable<SessionRecord> sessions)
    {
        var recent = sessions
            .Select(s => (Session: s, Result: For(s)))
            .Where(x => x.Result is not null)
            .TakeLast(GameSessions)
            .ToList();
        if (recent.Count == 0)
            return null;

        // Weighted by minutes, but never by less than one, so a session
        // written with no length still counts a little rather than not at all.
        double weights = recent.Sum(x => Math.Max(1, x.Session.Length.TotalMinutes));
        double total = recent.Sum(x => x.Result!.Score * Math.Max(1, x.Session.Length.TotalMinutes));
        return (int)Math.Round(total / weights);
    }

    /// <summary>A word for the score, for the card and the tooltips.</summary>
    public static string Grade(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 60 => "Fair",
        >= 40 => "Poor",
        _ => "Needs optimising",
    };

    /// <summary>The band a score is in, for its colour: 0 good, 1 fair, 2 poor - the widget's green, amber and red.</summary>
    public static int Band(int score) => score >= 75 ? 0 : score >= 50 ? 1 : 2;

    /// <summary>
    /// The worst of the CPU and GPU temperatures and RAM use: full marks below
    /// the widget's amber level, none at its red one. The worst rather than an
    /// average, since one part running hot is the problem whatever the others do.
    /// </summary>
    private static (double Value, string Detail)? Thermals(SessionRecord session)
    {
        (double Value, string Detail)? worst = null;

        foreach (var (label, unit) in new[] { ("CPU", "°C"), ("GPU", "°C"), ("RAM", "%") })
        {
            if (session.Find(label, unit)?.Number is not double reading || FindMetric(label, unit) is not { } metric)
                continue;
            if (double.IsInfinity(metric.Warn) || double.IsInfinity(metric.Crit) || metric.Crit <= metric.Warn)
                continue;

            double value = 1 - Between(reading, metric.Warn, metric.Crit);
            if (worst is null || value < worst.Value.Value)
                worst = (value, $"{label} at {reading.ToString("0", CultureInfo.InvariantCulture)} {unit} at the most" +
                                (value >= 1 ? "" : $", amber from {metric.Warn:0} {unit}"));
        }

        return worst;
    }

    private static Metric? FindMetric(string label, string unit)
        => MetricRegistry.Sections.SelectMany(s => s.Metrics).FirstOrDefault(m => m.Label == label && m.Unit == unit);

    /// <summary>0 at or below <paramref name="none"/>, 1 at or above <paramref name="full"/>, in a straight line between.</summary>
    private static double Between(double value, double none, double full)
        => Clamp((value - none) / (full - none));

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static string Fps(double value) => value.ToString("0", CultureInfo.InvariantCulture) + " fps";

    private static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
