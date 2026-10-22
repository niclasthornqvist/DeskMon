using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace DeskMon.Sensors;

/// <summary>
/// Download and upload speed across every connected network adapter, in
/// megabits per second - the unit internet speeds are sold in, so the figure
/// can be held up against the plan.
///
/// From the adapters' own byte counts, the difference since the last read.
/// Loopback, tunnels and Hyper-V's virtual switch are left out: they would
/// count the same bytes twice.
/// </summary>
internal sealed class NetworkStats
{
    /// <summary>
    /// Each adapter's counts at the last read, by its id. Per adapter rather
    /// than one total: an adapter that appears - a VPN connecting - brings
    /// every byte it has ever counted, and in a total that would read as one
    /// enormous second of download.
    /// </summary>
    private Dictionary<string, (long Received, long Sent)> _last = new();

    /// <summary>The other half of the pair with <see cref="_last"/> - see Read.</summary>
    private Dictionary<string, (long Received, long Sent)> _spare = new();
    private DateTime _at;

    /// <summary>
    /// The adapters worth counting, looked up again every <see cref="AdaptersEvery"/>.
    /// Listing them builds a fresh object for every adapter and asks Windows
    /// for all their addresses - the one costly call left in a tick. Adapters
    /// come and go far less often: a VPN connecting shows up within the
    /// interval, and its first reading is skipped anyway (see _last).
    /// </summary>
    private NetworkInterface[] _adapters = Array.Empty<NetworkInterface>();
    private DateTime _adaptersAt = DateTime.MinValue;
    private static readonly TimeSpan AdaptersEvery = TimeSpan.FromSeconds(10);

    public (double? Down, double? Up) Read()
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - _adaptersAt >= AdaptersEvery)
            {
                // Up is part of the list rather than asked each tick: .NET
                // takes it down once, when the list is made, so asking a kept
                // adapter would only repeat that. One that goes down meanwhile
                // stops counting bytes, which adds nothing to the figure.
                _adapters = Array.FindAll(NetworkInterface.GetAllNetworkInterfaces(), adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !IsVirtualSwitch(adapter));
                _adaptersAt = now;
            }

            // The spare dictionary, emptied, rather than a new one each tick:
            // the two take turns as this read and the last, so a reading costs
            // no allocation in a process that sits over games.
            var counts = _spare;
            counts.Clear();
            foreach (var adapter in _adapters)
            {
                // One adapter that won't report - some VPN and virtual ones
                // throw here, and so does one removed since the list was
                // made - costs its own bytes, not the whole reading.
                try
                {
                    var stats = adapter.GetIPStatistics();
                    counts[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                catch
                {
                }
            }

            double seconds = (now - _at).TotalSeconds;
            bool first = _last.Count == 0;

            // Only adapters seen both times, and only counts that went up: a
            // counter that went backwards is an adapter that was reset.
            long received = 0, sent = 0;
            foreach (var (id, (r, s)) in counts)
                if (_last.TryGetValue(id, out var before))
                {
                    received += Math.Max(0, r - before.Received);
                    sent += Math.Max(0, s - before.Sent);
                }

            _spare = _last;
            _last = counts;
            _at = now;

            return first || seconds <= 0
                ? (null, null)
                : (Mbit(received, seconds), Mbit(sent, seconds));
        }
        catch
        {
            return (null, null);
        }
    }

    private static double Mbit(long bytes, double seconds) => bytes * 8 / seconds / 1_000_000;

    /// <summary>
    /// Hyper-V's virtual switch - there with WSL, Docker or Windows Sandbox.
    /// Traffic through it is also traffic through the real adapter it is
    /// bridged to, so counting both would show everything twice.
    /// </summary>
    private static bool IsVirtualSwitch(NetworkInterface adapter)
        => adapter.Description.Contains("Hyper-V Virtual Ethernet", StringComparison.OrdinalIgnoreCase)
           || adapter.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);
}
