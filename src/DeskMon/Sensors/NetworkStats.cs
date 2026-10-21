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
    private DateTime _at;

    public (double? Down, double? Up) Read()
    {
        try
        {
            var counts = new Dictionary<string, (long Received, long Sent)>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || IsVirtualSwitch(adapter))
                    continue;

                // One adapter that won't report - some VPN and virtual ones
                // throw here - costs its own bytes, not the whole reading.
                try
                {
                    var stats = adapter.GetIPStatistics();
                    counts[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                catch
                {
                }
            }

            var now = DateTime.UtcNow;
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
