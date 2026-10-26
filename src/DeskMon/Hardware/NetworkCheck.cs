using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
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
            return new SystemInfo.Check($"Could not ping {host} - no connection?", SystemInfo.Condition.Bad, "No connection");
        }

        if (times.Count == 0)
            return new SystemInfo.Check($"No answer from {host} - no internet connection, or it blocks pings", SystemInfo.Condition.Bad, "No answer");

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
        return new SystemInfo.Check($"Ping to {host}: {median} ms, jitter {jitter:0} ms, {lossText}", level, lost == 0 ? $"{median} ms" : $"{median} ms, {loss:0}% lost");
    }

    /// <summary>
    /// Where game servers usually are, and a server of Amazon's in each. The
    /// ping test's 1.1.1.1 answers from whichever Cloudflare site is nearest,
    /// so it measures the line to the internet - not how far the games are.
    /// These are fixed places, the ones online games host in for players here.
    /// </summary>
    public static readonly (string Name, string Host)[] Regions =
    {
        ("Stockholm", "ec2.eu-north-1.amazonaws.com"),
        ("Frankfurt", "ec2.eu-central-1.amazonaws.com"),
        ("London", "ec2.eu-west-2.amazonaws.com"),
        ("US East", "ec2.us-east-1.amazonaws.com"),
    };

    /// <summary>Three connections each: the quickest is the trip itself, the others had a queue in the way.</summary>
    private const int RegionTries = 3;

    /// <summary>
    /// How far each of <see cref="Regions"/> is, all at once so the wait is
    /// the slowest one's. Timed as the opening of a connection rather than a
    /// ping: most cloud servers don't answer pings, but every one of these
    /// answers on 443 - and opening the connection is one trip there and
    /// back, so it reads like a ping. The name is looked up before the clock
    /// starts, so DNS isn't counted.
    /// </summary>
    public static async Task<IReadOnlyList<SystemInfo.Check>> RegionsAsync()
        => await Task.WhenAll(Regions.Select(r => RegionAsync(r.Name, r.Host))).ConfigureAwait(false);

    private static async Task<SystemInfo.Check> RegionAsync(string name, string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address is null)
                return NoAnswer(name, host);

            long? best = null;
            for (int i = 0; i < RegionTries; i++)
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var timeout = new CancellationTokenSource(TimeoutMs * 2);
                var clock = Stopwatch.StartNew();
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, 443), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception) when (i < RegionTries - 1)
                {
                    continue;   // one failed try is a lost packet; the next may get through
                }
                catch
                {
                    break;
                }
                long ms = clock.ElapsedMilliseconds;
                best = best is null ? ms : Math.Min(best.Value, ms);
            }

            if (best is not long time)
                return NoAnswer(name, host);

            // Not "bad" when far: 110 ms to US East is the ocean, not the PC.
            // Good while it is close enough for any game; a plain neutral
            // number past that, rather than a warning nobody can act on.
            var level = time < 100 ? SystemInfo.Condition.Good : SystemInfo.Condition.Unknown;
            return new SystemInfo.Check($"{name}: {time} ms to {host} (connection time, quickest of {RegionTries})", level, $"{time} ms");
        }
        catch
        {
            return NoAnswer(name, host);
        }
    }

    private static SystemInfo.Check NoAnswer(string name, string host)
        => new($"{name}: no answer from {host}", SystemInfo.Condition.Unknown, "No answer");
}
