using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace DeskMon.Hardware;

/// <summary>
/// A short ping test for the hardware window's network group: how quick the
/// connection is, how steady, and whether anything gets lost - the three
/// things that decide how an online game feels, and that a speed test
/// doesn't show.
///
/// Run each time the window opens, off the UI thread, like the update checks.
/// </summary>
public static class NetworkCheck
{
    /// <summary>
    /// Cloudflare's DNS when no PingHost is set: answers pings, sits close to
    /// almost everyone, and is up. With PingHost set, that server instead -
    /// the one the widget pings during games, so the two agree.
    /// </summary>
    public const string DefaultHost = "1.1.1.1";

    /// <summary>Ten pings: enough to see a lost one or a wobble, quick enough to wait for.</summary>
    private const int Count = 10;

    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(100);
    private const int TimeoutMs = 1000;

    public static async Task<SystemInfo.Check> PingAsync(string host)
    {
        var times = new List<long>();
        int lost = 0;

        try
        {
            using var ping = new Ping();
            for (int i = 0; i < Count; i++)
            {
                var reply = await ping.SendPingAsync(host, TimeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                    times.Add(reply.RoundtripTime);
                else
                    lost++;

                await Task.Delay(Spacing).ConfigureAwait(false);
            }
        }
        catch
        {
            // No network, or a host name that doesn't resolve.
            return new SystemInfo.Check($"Could not ping {host} - no connection?", SystemInfo.Condition.Bad);
        }

        if (times.Count == 0)
            return new SystemInfo.Check($"No answer from {host} - no internet connection, or it blocks pings", SystemInfo.Condition.Bad);

        // The median rather than the average, so one slow reply doesn't speak
        // for the other nine. Jitter is how far replies stray from it on
        // average - what a game feels as rubber-banding.
        times.Sort();
        long median = times[times.Count / 2];
        double jitter = times.Average(t => Math.Abs(t - median));
        double loss = lost * 100.0 / Count;

        // Under 40 ms is good for any game, under 100 playable; jitter over
        // 20 ms or any loss is noticeable, over 10% loss is broken.
        var level = median >= 100 || loss > 10 ? SystemInfo.Condition.Bad
                  : median >= 40 || jitter > 20 || lost > 0 ? SystemInfo.Condition.Warning
                  : SystemInfo.Condition.Good;

        string lossText = lost == 0 ? "no loss" : $"{loss:0}% lost";
        return new SystemInfo.Check($"Ping to {host}: {median} ms, jitter {jitter:0} ms, {lossText}", level);
    }
}
