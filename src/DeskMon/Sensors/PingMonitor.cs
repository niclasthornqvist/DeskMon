using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace DeskMon.Sensors;

/// <summary>
/// The round trip to a server you choose (PingHost), every two seconds while a
/// game runs - the delay an online game feels, measured the same way.
///
/// Only while a game runs, and only when a host is set: pinging a server all
/// day is traffic to someone else's machine for a number nobody looks at.
/// On a timer of its own, not the sensor read: a ping can take up to a second
/// to time out, and the widget's other readings mustn't wait for it.
/// </summary>
internal sealed class PingMonitor : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private const int TimeoutMs = 1000;

    private readonly Timer _timer;
    private readonly Ping _ping = new();
    private string? _host;
    private int _busy;

    /// <summary>The last round trip in milliseconds; null when off, or the last ping got no answer.</summary>
    public double? Latest { get; private set; }

    public PingMonitor() => _timer = new Timer(_ => Send(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>Starts pinging <paramref name="host"/>, or stops with null or empty.</summary>
    public void Run(string? host)
    {
        host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        if (host == _host)
            return;

        _host = host;
        Latest = null;
        _timer.Change(host is null ? Timeout.InfiniteTimeSpan : TimeSpan.Zero,
                      host is null ? Timeout.InfiniteTimeSpan : Every);
    }

    private async void Send()
    {
        // One at a time: a slow answer must not stack pings up behind it.
        if (_host is not { } host || Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            var reply = await _ping.SendPingAsync(host, TimeoutMs);
            if (host == _host)
                Latest = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch
        {
            Latest = null;   // no network, or a name that doesn't resolve
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _ping.Dispose();
    }
}
