using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeskMon.Sensors;

/// <summary>
/// Whether a session's lag came from home or from further out. The ping to
/// the internet goes through the router, so laid side by side the two say
/// where the trouble is: when the internet ping jumps and the router's jumps
/// with it, it is the Wi-Fi or the router; when the router stays calm, it is
/// past the house.
///
/// Counts moments of lag rather than comparing averages, which a calm session
/// with one bad stretch would hide. One slow reply is not a moment: routers
/// answer pings last, after the real traffic, so a lone slow answer says
/// little. A moment is a lost ping, or a second slow one in a row.
/// </summary>
internal sealed class LagWatch
{
    /// <summary>The "usual" is the median of the last 30 good answers, about a minute's worth, so one bad stretch can't move it.</summary>
    private const int UsualOver = 30;
    private const int UsualNeeds = 10;

    private readonly List<double> _pings = new(), _routers = new();
    private bool _lastSlow;
    private int _home, _internet;

    /// <summary>One pair of pings, the two null when lost.</summary>
    public void Add(double? ping, double? router)
    {
        double? usualPing = Usual(_pings), usualRouter = Usual(_routers);

        // Twice the usual and 30 ms over it: at a usual of 4 ms, 8 ms is twice
        // as much and still nothing anyone would feel.
        bool slow = ping is double ms && usualPing is double usual && ms >= Math.Max(usual * 2, usual + 30);
        bool lost = ping is null;

        if (lost || (slow && _lastSlow))
        {
            // The router "with it" when it took at least half of the jump -
            // its own jitter of a few ms under a 300 ms spike isn't the cause.
            double jump = ping is double rise && usualPing is double before ? rise - before : 20;
            bool routerToo = router is null
                             || (router is double near && usualRouter is double calm && near - calm >= Math.Max(10, jump / 2));
            if (routerToo)
                _home++;
            else
                _internet++;
        }
        _lastSlow = slow;

        // A spike stays out of its own usual, or a long one would become
        // normal - for the router too: a long Wi-Fi problem then stopped
        // looking like the router's halfway through and went down as the internet's.
        if (ping is double good && !slow)
            Remember(_pings, good);
        if (router is double reply && !(usualRouter is double calmer && reply >= Math.Max(calmer * 3, calmer + 20)))
            Remember(_routers, reply);
    }

    /// <summary>"lag home 3 internet 12" for the log; both 0 is a session with none, which is worth saying too.</summary>
    public string ToLogPart() => FormattableString.Invariant($"lag home {_home} internet {_internet}");

    private static void Remember(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > UsualOver)
            list.RemoveAt(0);
    }

    private static double? Usual(List<double> list)
    {
        if (list.Count < UsualNeeds)
            return null;
        var sorted = list.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// The router's address, or null without one. The adapter in use, not the
    /// first with a gateway: VirtualBox's and the like report one too, and an
    /// off adapter can keep its old one. Slow enough to belong off the UI thread.
    /// </summary>
    public static string? FindRouter()
    {
        try
        {
            // Windows' own answer first, so the list's order doesn't decide.
            // Stable, so the rest stay as listed for a best adapter with no
            // gateway of its own - a VPN - where the first with one is the router.
            int best = GetBestInterface(InternetAddress, out uint index) == 0 ? (int)index : -1;
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .OrderByDescending(adapter => IsAdapter(adapter, best));

            foreach (var adapter in adapters)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var gateway = adapter.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gateway is not null)
                    return gateway.ToString();
            }
        }
        catch (Exception ex)
        {
            App.Log("finding the router", ex);
        }
        return null;
    }

    private static bool IsAdapter(NetworkInterface adapter, int index)
    {
        try
        {
            return adapter.GetIPProperties().GetIPv4Properties()?.Index == index;
        }
        catch (NetworkInformationException)
        {
            return false;   // no IPv4 on it
        }
    }

    /// <summary>1.1.1.1 as GetBestInterface wants it. Only a route is looked up; nothing is sent.</summary>
    private static readonly uint InternetAddress = BitConverter.ToUInt32(new byte[] { 1, 1, 1, 1 });

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint index);
}
