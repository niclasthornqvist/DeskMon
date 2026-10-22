using System;
using System.Collections.Generic;

namespace DeskMon.Sensors;

/// <summary>
/// Notices the CPU slowing itself down to cool off - for the mark on the CPU
/// temperature row, so a sudden frame-rate dip that the temperature alone
/// doesn't explain has its reason on screen.
///
/// The chip doesn't say so in anything the hardware library reads, so it is
/// worked out from what it does: busy, hot, and running well under the clock
/// it managed under load a moment ago. Only heat is caught this way. A CPU
/// held back by its power limit runs a lower clock from the start, which looks
/// the same as a CPU that was never asked for more.
///
/// Read on the sensor worker thread only, once per tick.
/// </summary>
internal sealed class ThrottleWatch
{
    /// <summary>
    /// Below this the clock follows the work rather than the heat - a half-idle
    /// CPU parks its cores and drops its clock on purpose.
    /// </summary>
    private const double BusyLoad = 50;

    /// <summary>
    /// The CPU temperature row's amber level. Under it nothing is being cooled
    /// off, whatever the clock does.
    /// </summary>
    private const double HotTemp = 80;

    /// <summary>
    /// How far under the recent best the clock has to be. Boost wanders by a
    /// few percent from one read to the next; this is well past that.
    /// </summary>
    private const double Drop = 0.85;

    /// <summary>How far back the best clock under load is remembered.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long it has to hold before the mark shows. Time rather than a count
    /// of ticks: the hardware values are reused between refreshes, so three
    /// ticks can be one reading seen three times.
    /// </summary>
    private static readonly TimeSpan Settles = TimeSpan.FromSeconds(3);

    private readonly Queue<(DateTime At, double Clock)> _busyClocks = new();
    private DateTime? _since;

    /// <summary>
    /// The clock it was managing before it slowed down, while it is throttling;
    /// null otherwise. Fed every tick, including ones with nothing to go on, so
    /// a sensor that stops reporting clears the mark rather than freezing it.
    /// </summary>
    public double? Update(double? load, double? temp, double? clock)
    {
        var now = DateTime.UtcNow;

        // Not while it is throttling: a minute of being held down would age the
        // good clock out, and the slowed one would become the new best - the
        // mark would go out while the CPU was still as slow as ever.
        while (_since is null && _busyClocks.Count > 0 && now - _busyClocks.Peek().At > Window)
            _busyClocks.Dequeue();

        if (load is not double l || temp is not double t || clock is not double c || c <= 0)
        {
            _since = null;
            return null;
        }

        // The best is taken before this reading joins it, so a drop is measured
        // against what came before rather than against itself.
        double best = 0;
        foreach (var entry in _busyClocks)
            best = Math.Max(best, entry.Clock);

        // Capped, since nothing is aged out while it throttles: a game held
        // hot for an hour would otherwise keep an hour of ticks.
        if (l >= BusyLoad && _busyClocks.Count < 600)
            _busyClocks.Enqueue((now, c));

        if (l < BusyLoad || t < HotTemp || best <= 0 || c > best * Drop)
        {
            _since = null;
            return null;
        }

        _since ??= now;
        return now - _since >= Settles ? best : null;
    }
}
