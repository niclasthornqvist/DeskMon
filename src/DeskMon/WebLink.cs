using System;
using System.Diagnostics;

namespace DeskMon;

/// <summary>
/// Opens a web address in the default browser, as the signed-in user.
///
/// DeskMon runs as administrator, and Process.Start with a URL hands that on: a
/// browser started that way ran elevated, every page in it included. Explorer
/// passes the address to the shell already running, which isn't elevated.
/// </summary>
internal static class WebLink
{
    /// <summary>Throws what Process.Start throws. Anything but http and https is refused: Explorer would run it.</summary>
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only web addresses are opened.", nameof(url));

        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(uri.AbsoluteUri);
        using var explorer = Process.Start(start);
    }
}
