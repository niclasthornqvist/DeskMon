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
    public double? Latest
    {
        get
        {
            double value = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestBits));
            return double.IsNaN(value) ? null : value;
        }
        private set => Interlocked.Exchange(ref _latestBits, BitConverter.DoubleToInt64Bits(value ?? double.NaN));
    }

    /// <summary>
    /// <see cref="Latest"/> as a double's bits, NaN for none: written on a
    /// timer thread and read on the UI thread, where a double? - 16 bytes -
    /// could be read half old and half new. One 64-bit word, read and written
    /// whole through Interlocked.
    /// </summary>
    private long _latestBits = BitConverter.DoubleToInt64Bits(double.NaN);

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

        // Named for the freeze log - see Activity: the name lookup before the
        // ping isn't covered by its timeout. Ended in finally, below.
        var step = Activity.Begin("ping to " + host);
        try
        {
            var reply = await _ping.SendPingAsync(host, TimeoutMs);
            if (host == _host)
            {
                Latest = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
                Count(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
        }
        catch
        {
            Latest = null;   // no network, or a name that doesn't resolve
        }
        finally
        {
            step.Dispose();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // --- Running counts, for a session's ping quality -----------------------------
    //
    // The session log kept only the highest ping, which one spike decides. These
    // say how the connection was over the whole session: the average, the
    // jitter (how far each ping is from the one before - what makes a game
    // feel uneven) and the share lost. Counted from start; a session takes the
    // difference between its start and end, see PingTotals.Since.

    private readonly object _countGate = new();
    private long _sent, _lost, _deltas;
    private double _rttSum, _deltaSum;
    private double? _previous;

    /// <summary>
    /// A ping that went out and got an answer, or none. An exception - no
    /// network, a name that won't resolve - isn't counted: nothing was sent,
    /// and counting it would read as a lossy line rather than an unplugged one.
    /// </summary>
    private void Count(double? rtt)
    {
        lock (_countGate)
        {
            _sent++;
            if (rtt is not double ms)
            {
                _lost++;
                _previous = null;   // the next answer's change isn't from this gap
                return;
            }

            _rttSum += ms;
            if (_previous is double before)
            {
                _deltaSum += Math.Abs(ms - before);
                _deltas++;
            }
            _previous = ms;
        }
    }

    /// <summary>The counts so far; read on the UI thread while the timer adds to them.</summary>
    public PingTotals Totals
    {
        get
        {
            lock (_countGate)
                return new PingTotals(_sent, _lost, _rttSum, _deltaSum, _deltas);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _ping.Dispose();
    }
}

/// <summary>A ping's running counts - see PingMonitor.Totals - and what a stretch of them comes to.</summary>
internal readonly record struct PingTotals(long Sent, long Lost, double RttSum, double DeltaSum, long Deltas)
{
    /// <summary>What was counted after <paramref name="start"/> - a session's own share of the counts.</summary>
    public PingTotals Since(PingTotals start)
        => new(Sent - start.Sent, Lost - start.Lost, RttSum - start.RttSum, DeltaSum - start.DeltaSum, Deltas - start.Deltas);

    public double? AverageMs => Sent - Lost > 0 ? RttSum / (Sent - Lost) : null;
    public double? JitterMs => Deltas > 0 ? DeltaSum / Deltas : null;
    public double LossPercent => Sent > 0 ? Lost * 100.0 / Sent : 0;

    /// <summary>
    /// "ping 18 ms ±6 loss 0.4 %" for the log, or null with too few pings to
    /// say anything - under a minute's worth at one every 2 s.
    /// </summary>
    public string? ToLogPart()
        => Sent < 30 || AverageMs is not double average
            ? null
            : System.FormattableString.Invariant($"ping {average:0} ms ±{JitterMs ?? 0:0} loss {LossPercent:0.#} %");
}
