using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DeskMon.Hardware;

/// <summary>
/// Is the graphics driver or the BIOS out of date?
///
/// Only NVIDIA can actually be asked: its driver download page runs on a
/// lookup service that answers plain requests with the newest driver for a
/// given card. AMD and Intel have nothing like it, and board makers' BIOS
/// pages are each their own website - ASUS's needs an internal product ID that
/// only its web pages know. So everything else is judged by age, which can't
/// name the version to get but needs no network at all.
///
/// Run each time the hardware window is opened, and never on a timer.
/// </summary>
public static class UpdateCheck
{
    /// <summary>What to tell the user. Outdated ones are shown in the warning colour.</summary>
    public sealed record Verdict(string Text, bool Outdated);

    /// <summary>
    /// A year, for both. BIOS updates come every few months while a board is
    /// current, so a year without one usually means one was missed. Kept the
    /// same for graphics drivers so there's one rule to remember.
    /// </summary>
    private const int OldAfterMonths = 12;

    private const string NvidiaProducts =
        "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";

    // osID 57 is "Windows 10 64-bit", which NVIDIA also serves to Windows 11;
    // it has no 64-bit Windows 11 entry of its own. dch=1 is the only driver
    // type made for current Windows; isWHQL=1 leaves out betas.
    private const string NvidiaLatest =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        "?func=DriverManualLookup&psid={0}&pfid={1}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";

    /// <summary>
    /// For the whole check, both requests together. Short, because the window
    /// shows "checking…" until this returns, and a slow answer is worth less
    /// than a quick "couldn't check". HttpClient.Timeout alone was per request,
    /// so two slow ones could keep "checking…" up for twice as long.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = CheckTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskMon/" + Changelog.Current);
        return client;
    }

    /// <summary>
    /// Asks NVIDIA for the newest Game Ready driver for this card and compares.
    /// Falls back to the age rule if NVIDIA can't be reached or doesn't know
    /// the card, saying so - a silent fallback would read as "checked online".
    /// </summary>
    public static async Task<Verdict> NvidiaDriverAsync(string gpuName, string? installed, DateTime? installedDate)
    {
        // Said in the fallback, because "not reachable" and "doesn't know
        // this card" call for different things - wait, or stop expecting it.
        string why = "NVIDIA could not be reached";
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            var latest = await LatestNvidiaDriverAsync(gpuName, timeout.Token).ConfigureAwait(false);
            if (latest is null)
                why = "NVIDIA's list has no card by this name";

            if (latest is not null && installed is not null)
            {
                if (Version.TryParse(installed, out var have)
                    && Version.TryParse(latest.Value.Version, out var newest))
                {
                    // Only newer counts. A Studio or hotfix driver can be ahead of
                    // the newest Game Ready one, and is not something to "update" from.
                    if (newest <= have)
                        return new Verdict("Newest driver from NVIDIA", Outdated: false);

                    string when = latest.Value.Released is { } date ? $" ({date:yyyy-MM-dd})" : string.Empty;
                    return new Verdict($"Newer driver available: {latest.Value.Version}{when}", Outdated: true);
                }

                // Reached NVIDIA fine, so "could not be reached" would be wrong.
                why = "the versions could not be compared";
            }
        }
        catch
        {
            // Offline, blocked, timed out, or the reply changed shape - all
            // land on the age rule below.
        }

        return Age("Driver", installedDate, $" ({why})")
               ?? new Verdict($"Could not check for a newer driver: {why}", Outdated: false);
    }

    /// <summary>
    /// Null while it is young enough, rather than a reassuring line: age alone
    /// can't tell "up to date" from "not updated for a while".
    /// </summary>
    public static Verdict? Age(string what, DateTime? date, string? suffix = null)
    {
        if (date is null)
            return null;

        int months = MonthsSince(date.Value);
        return months >= OldAfterMonths
            ? new Verdict($"{what} is {months} months old - look for an update{suffix}", Outdated: true)
            : null;
    }

    /// <summary>Whole months, not days / 30, so "12 months" turns over on the same date a year on.</summary>
    private static int MonthsSince(DateTime date)
    {
        var now = DateTime.Today;
        int months = (now.Year - date.Year) * 12 + now.Month - date.Month;
        if (now.Day < date.Day)
            months--;
        return Math.Max(0, months);
    }

    /// <summary>"NVIDIA GeForce ..." from the sensor library, or "GeForce ..." / "Quadro ..." from WMI.</summary>
    public static bool IsNvidia(string gpuName)
        => gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
           || gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
           || gpuName.Contains("Quadro", StringComparison.OrdinalIgnoreCase);

    // ConfigureAwait(false) on the requests: parsing a 90 KB product list has
    // no reason to queue up behind the UI thread.
    private static async Task<(string Version, DateTime? Released)?> LatestNvidiaDriverAsync(string gpuName, CancellationToken cancel)
    {
        // The card's IDs first. Not cached: the list grows with every new
        // card, and this only happens when someone opens the window.
        var products = XDocument.Parse(await Http.GetStringAsync(NvidiaProducts, cancel).ConfigureAwait(false));
        string wanted = WithoutVendor(gpuName);

        string? series = null, product = null;
        foreach (var item in products.Descendants("LookupValue"))
        {
            // Newer cards are listed as "NVIDIA GeForce ...", older ones as
            // "GeForce ...", and the sensor library may use either.
            if (WithoutVendor((string?)item.Element("Name") ?? string.Empty)
                    .Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                // A few names appear twice, as desktop and as notebook. The
                // first is the desktop one, and both get the same driver.
                series = (string?)item.Attribute("ParentID");
                product = (string?)item.Element("Value");
                break;
            }
        }

        if (series is null || product is null)
            return null;

        string json = await Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture, NvidiaLatest,
                                                              Uri.EscapeDataString(series), Uri.EscapeDataString(product)),
                                              cancel)
                                .ConfigureAwait(false);
        using var reply = JsonDocument.Parse(json);

        var info = reply.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
        string? version = info.GetProperty("Version").GetString();
        if (string.IsNullOrEmpty(version))
            return null;

        // "Tue Oct 06, 2026"
        DateTime? released = info.TryGetProperty("ReleaseDateTime", out var raw)
                             && DateTime.TryParseExact(raw.GetString(), "ddd MMM dd, yyyy", CultureInfo.InvariantCulture,
                                                       DateTimeStyles.None, out var date)
            ? date
            : null;

        return (version, released);
    }

    private static string WithoutVendor(string name)
        => name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? name[7..].Trim() : name.Trim();
}
