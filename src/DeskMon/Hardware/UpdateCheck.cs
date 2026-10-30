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
/// NVIDIA, AMD and ASUS can actually be asked: NVIDIA's driver download page
/// and ASUS's support pages both run on lookup services that answer plain
/// requests, and AMD's driver page names its newest installer in plain HTML.
/// ASUS's BIOS list wants an internal product ID, which its own site search
/// hands out for a board's name. Intel, MSI, Gigabyte and ASRock all sit
/// behind bot protection that turns away anything but a real browser (tried
/// 2026-10-22), so those are judged by age, which can't name the version to
/// get but needs no network at all.
///
/// Run each time the hardware window is opened, and never on a timer.
/// </summary>
public static class UpdateCheck
{
    /// <summary>What to tell the user, and how up to date it is - which sets its colour.</summary>
    /// <param name="Short">The word or two the hardware window shows at the right of its row ("Newest", "Update available"); the full text is for the note under it and Copy.</param>
    public sealed record Verdict(string Text, Freshness Level, string? Short = null)
    {
        public string ShortText => Short ?? Text;
    }

    /// <summary>
    /// Green, yellow, red - or dim when nothing could be said. Levels rather
    /// than one "outdated" flag, so a BIOS a release behind doesn't look as
    /// alarming as one nobody has touched in two years.
    /// </summary>
    public enum Freshness
    {
        /// <summary>Not known: still checking, or the check couldn't answer.</summary>
        Unknown,
        /// <summary>Confirmed newest by the maker or Windows Update.</summary>
        Current,
        /// <summary>A newer one is out, or it is getting on - worth updating.</summary>
        Aging,
        /// <summary>Very old - <see cref="OldAfterMonths"/> or more.</summary>
        Old,
    }

    /// <summary>
    /// From here an installed version counts as getting on: driver and BIOS
    /// makers release every month or two, so three months is a missed update
    /// or two rather than a fresh install.
    /// </summary>
    private const int AgingAfterMonths = 3;

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

    // AMD's driver page links the newest Adrenalin installer - the one its
    // auto-detect tool fetches - as ".../amd-software-adrenalin-edition-
    // 26.10.1-minimalsetup-261018_web.exe": the version, then the build date.
    private const string AmdDrivers = "https://www.amd.com/en/support/download/drivers.html";

    private static readonly System.Text.RegularExpressions.Regex AmdInstaller = new(
        @"amd-software-adrenalin-edition-(?<version>\d+\.\d+\.\d+)-minimalsetup-(?<date>\d{6})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // One chipset's page stands for all: every desktop chipset's page, AM4,
    // AM5 and Threadripper alike, links the same "amd_chipset_software_
    // 8.09.28.1124.exe" (checked 2026-10-30), so the board's chipset needn't
    // be worked out first.
    private const string AmdChipsets = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/b650.html";

    private static readonly System.Text.RegularExpressions.Regex AmdChipsetInstaller = new(
        @"amd_chipset_software_(?<version>\d+(\.\d+){1,3})\.exe",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ASUS's site search: a board's name in, its product ID (DataId) out.
    private const string AsusSearch =
        "https://odinapi.asus.com/recent-data/apiv2/SearchSuggestion" +
        "?SystemCode=asus&WebsiteCode=global&SearchKey={0}&SearchType=ProductsAll&RowLimit=8";

    // Every BIOS for that product, newest first, betas included. model and
    // pdhashedid are what ASUS's own page sends too; the ID alone is enough.
    private const string AsusBios =
        "https://www.asus.com/support/webapi/ProductV2/GetPDBIOS" +
        "?website=global&model=&pdhashedid=&pdid={0}&cpu=&siteID=www&sitelang=";

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
                        return new Verdict("Newest driver from NVIDIA", Freshness.Current, "Newest");

                    string when = latest.Value.Released is { } date ? $" ({date:yyyy-MM-dd})" : string.Empty;
                    return new Verdict($"Newer driver available: {latest.Value.Version}{when}", Behind(installedDate), "Update available");
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
               ?? new Verdict($"Could not check for a newer driver: {why}", Freshness.Unknown, "Can't check");
    }

    /// <summary>
    /// Asks ASUS for the newest BIOS for this board and compares, the same way
    /// as the NVIDIA check - including the fallback to the age rule, saying
    /// why, when ASUS can't be reached or doesn't know the board.
    /// </summary>
    public static async Task<Verdict> AsusBiosAsync(string board, string? installed, DateTime? installedDate)
    {
        string why = "ASUS could not be reached";
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            var latest = await LatestAsusBiosAsync(board, timeout.Token).ConfigureAwait(false);
            if (latest is null)
                why = "ASUS's list has no board by this name";

            if (latest is not null && installed is not null)
            {
                // ASUS numbers its BIOSes as plain counters - 3842, then 3881.
                if (int.TryParse(installed, NumberStyles.None, CultureInfo.InvariantCulture, out int have)
                    && int.TryParse(latest.Value.Version, NumberStyles.None, CultureInfo.InvariantCulture, out int newest))
                {
                    if (newest <= have)
                        return new Verdict("Newest BIOS from ASUS", Freshness.Current, "Newest");

                    string when = latest.Value.Released is { } date ? $" ({date:yyyy-MM-dd})" : string.Empty;
                    return new Verdict($"Newer BIOS available: {latest.Value.Version}{when}", Behind(installedDate), "Update available");
                }

                // Laptops' BIOSes are named like "X515EA.312"; reached ASUS fine all the same.
                why = "the versions could not be compared";
            }
        }
        catch
        {
            // As in NvidiaDriverAsync: all land on the age rule.
        }

        return Age("BIOS", installedDate, $" ({why})")
               ?? new Verdict($"Could not check for a newer BIOS: {why}", Freshness.Unknown, "Can't check");
    }

    /// <summary>
    /// Asks AMD's driver page for the newest Adrenalin version and compares,
    /// with the same fallback to the age rule as the others.
    /// </summary>
    public static async Task<Verdict> AmdDriverAsync(string? installed, DateTime? installedDate)
    {
        string why = "AMD could not be reached";
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            string page = await Http.GetStringAsync(AmdDrivers, timeout.Token).ConfigureAwait(false);
            var match = AmdInstaller.Match(page);
            if (!match.Success)
                why = "AMD's page no longer names its newest driver";

            // Only an Adrenalin-style version ("26.9.2") can be compared. Without
            // the Radeon software, SystemInfo has only Windows' "32.0.21025…",
            // which would always look newer.
            if (match.Success && installed is not null
                && System.Text.RegularExpressions.Regex.IsMatch(installed, @"^\d{2}\.\d{1,2}\.\d{1,2}$"))
            {
                if (Version.TryParse(installed, out var have)
                    && Version.TryParse(match.Groups["version"].Value, out var newest))
                {
                    if (newest <= have)
                        return new Verdict("Newest driver from AMD", Freshness.Current, "Newest");

                    // "261018" is 2026-10-18.
                    string when = DateTime.TryParseExact(match.Groups["date"].Value, "yyMMdd", CultureInfo.InvariantCulture,
                                                         DateTimeStyles.None, out var date)
                        ? $" ({date:yyyy-MM-dd})"
                        : string.Empty;
                    return new Verdict($"Newer driver available: {newest}{when}", Behind(installedDate), "Update available");
                }
            }
            else if (match.Success)
            {
                why = "the versions could not be compared";
            }
        }
        catch
        {
            // As in NvidiaDriverAsync: all land on the age rule.
        }

        return Age("Driver", installedDate, $" ({why})")
               ?? new Verdict($"Could not check for a newer driver: {why}", Freshness.Unknown, "Can't check");
    }

    /// <summary>
    /// Asks AMD's chipset page for the newest chipset software and compares,
    /// with the same fallback to the age rule as the others.
    /// </summary>
    public static async Task<Verdict> AmdChipsetAsync(string? installed, DateTime? installedDate)
    {
        string why = "AMD could not be reached";
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);
            string page = await Http.GetStringAsync(AmdChipsets, timeout.Token).ConfigureAwait(false);
            var match = AmdChipsetInstaller.Match(page);
            if (!match.Success)
                why = "AMD's page no longer names its newest chipset driver";

            if (match.Success && installed is not null
                && Version.TryParse(installed, out var have)
                && Version.TryParse(match.Groups["version"].Value, out var newest))
            {
                if (newest <= have)
                    return new Verdict("Newest chipset driver from AMD", Freshness.Current, "Newest");

                // The page's "Release Date 2026-10-21" sits just before the
                // download link; looked for there rather than anywhere on the
                // page, which also dates the release notes and other files.
                var date = System.Text.RegularExpressions.Regex.Match(
                    page[Math.Max(0, match.Index - 2000)..match.Index], @"\d{4}-\d{2}-\d{2}(?!.*\d{4}-\d{2}-\d{2})",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                string when = date.Success ? $" ({date.Value})" : string.Empty;
                return new Verdict($"Newer chipset driver available: {match.Groups["version"].Value}{when}",
                                   Behind(installedDate), "Update available");
            }

            if (match.Success)
                why = "the versions could not be compared";
        }
        catch
        {
            // As in NvidiaDriverAsync: all land on the age rule.
        }

        return Age("Chipset driver", installedDate, $" ({why})")
               ?? new Verdict($"Could not check for a newer chipset driver: {why}", Freshness.Unknown, "Can't check");
    }

    /// <summary>
    /// A Radeon the current Adrenalin driver is made for: RX 5000 and later.
    /// The RX 400/500 and Vega cards moved to a driver line of their own, so
    /// comparing them with the newest Adrenalin would call them out of date
    /// forever. Those, and integrated Radeon graphics, keep the age rule.
    /// </summary>
    public static bool IsCurrentRadeon(string gpuName)
    {
        var model = System.Text.RegularExpressions.Regex.Match(gpuName, @"\bRX\s?(\d{4})\b",
                                                               System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
               && model.Success
               && int.Parse(model.Groups[1].Value, CultureInfo.InvariantCulture) >= 5000;
    }

    /// <summary>
    /// The maker of a graphics card or board whose website turns DeskMon away
    /// (see the class summary), or null. Said in the window so a missing
    /// online check reads as "they won't answer", not as DeskMon forgetting
    /// to ask - or as "up to date".
    /// </summary>
    public static string? BlockedMaker(string name)
        => name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Arc ", StringComparison.OrdinalIgnoreCase) ? "Intel"
         : name.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("MSI", StringComparison.OrdinalIgnoreCase) ? "MSI"
         : name.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase) ? "Gigabyte"
         : name.Contains("ASRock", StringComparison.OrdinalIgnoreCase) ? "ASRock"
         : null;

    /// <summary>
    /// The age rule with the reason there's no online answer, or - while it is
    /// young - a line saying so, since silence would look like "checked, fine".
    /// </summary>
    public static Verdict Blocked(string what, string maker, DateTime? date)
        => Age(what, date, $" ({maker}'s website blocks automatic checks)")
           ?? new Verdict($"Can't check online: {maker}'s website blocks automatic checks - look on its site",
                          Freshness.Unknown, "Can't check");

    /// <summary>
    /// How long Windows Update may take to answer. Longer than the others:
    /// its search took 9 s here and can take far longer on a PC that hasn't
    /// asked in a while. The window shows "checking…" meanwhile, so nothing waits.
    /// </summary>
    private static readonly TimeSpan WindowsTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a Windows Update answer is reused - see WindowsAsync.</summary>
    private static readonly TimeSpan WindowsKept = TimeSpan.FromMinutes(5);

    private static readonly object SearchLock = new();
    private static Task<(System.Collections.Generic.List<string> Waiting, bool Restart)>? _search;
    private static DateTime _searchAt;

    /// <summary>
    /// Whether Windows has updates waiting, asked of Windows Update itself
    /// rather than of a web page. Its answer is right for every edition,
    /// channel and Insider build, where comparing a build number with
    /// Microsoft's release list would call an Insider build unknown.
    /// </summary>
    public static async Task<Verdict> WindowsAsync()
    {
        // One search shared by every opening of the window: one still running
        // is waited on again rather than started twice, and an answer is kept
        // for a few minutes. A search that times out runs on regardless, so
        // opening and closing the window used to stack them up behind each
        // other. A failed one is not kept - the next opening asks again.
        Task<(System.Collections.Generic.List<string> Waiting, bool Restart)> search;
        lock (SearchLock)
        {
            if (_search is null
                || (_search.IsCompleted && (_search.IsFaulted || DateTime.UtcNow - _searchAt > WindowsKept)))
            {
                // A thread of its own: the search can run for minutes, and
                // the pool's threads are the ones the widget's readings use.
                _search = Task.Factory.StartNew(SearchWindowsUpdate, CancellationToken.None,
                                                TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _searchAt = DateTime.UtcNow;
            }

            search = _search;
        }

        // The COM search can't be cancelled; on a timeout it is left to
        // finish on its own, and its answer goes nowhere.
        if (await Task.WhenAny(search, Task.Delay(WindowsTimeout)).ConfigureAwait(false) != search)
            return new Verdict("Could not check for Windows updates: Windows Update took too long to answer", Freshness.Unknown, "Can't check");

        try
        {
            var (waiting, restart) = await search.ConfigureAwait(false);

            // Before the waiting list: until the restart, updates already
            // installed aren't in use, and some won't be offered again until then.
            if (restart)
                return new Verdict("Restart to finish installing Windows updates", Freshness.Aging, "Restart needed");

            return waiting.Count switch
            {
                0 => new Verdict("Windows is up to date", Freshness.Current, "Up to date"),
                1 => new Verdict($"Windows update waiting: {waiting[0]}", Freshness.Aging, "1 update"),
                _ => new Verdict($"{waiting.Count} Windows updates waiting, the first: {waiting[0]}", Freshness.Aging, $"{waiting.Count} updates"),
            };
        }
        catch
        {
            return new Verdict("Could not check for Windows updates: Windows Update did not answer", Freshness.Unknown, "Can't check");
        }
    }

    /// <summary>
    /// Asks the Windows Update agent over COM - the same search the Settings
    /// page runs. Drivers are left out (Type='Software'): the driver row
    /// above already covers the one that matters, and Windows offers older
    /// driver versions than the makers do.
    /// </summary>
    private static (System.Collections.Generic.List<string> Waiting, bool Restart) SearchWindowsUpdate()
    {
        var waiting = new System.Collections.Generic.List<string>();

        Type session = Type.GetTypeFromProgID("Microsoft.Update.Session")
                       ?? throw new InvalidOperationException("Windows Update is not available");

        // Every COM object let go in the finally, not left to the finalizer:
        // a search result holds every update's details, and the window can be
        // opened many times in a session.
        var held = new System.Collections.Generic.List<object>();
        try
        {
            dynamic updateSession = Keep(held, Activator.CreateInstance(session)!);
            dynamic searcher = Keep(held, updateSession.CreateUpdateSearcher());
            dynamic result = Keep(held, searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'"));
            dynamic updates = Keep(held, result.Updates);

            foreach (dynamic update in updates)
            {
                Keep(held, update);
                string title = (string)update.Title;

                // Defender's virus definitions arrive several times a day and
                // install themselves; listing them would make the line amber
                // almost every time the window opens.
                if (title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase))
                    continue;

                waiting.Add(title);
            }

            bool restart = false;
            if (Type.GetTypeFromProgID("Microsoft.Update.SystemInfo") is { } info)
            {
                dynamic systemInfo = Keep(held, Activator.CreateInstance(info)!);
                restart = (bool)systemInfo.RebootRequired;
            }

            return (waiting, restart);
        }
        finally
        {
            foreach (object com in held)
            {
                try
                {
                    if (System.Runtime.InteropServices.Marshal.IsComObject(com))
                        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com);
                }
                catch { /* already released */ }
            }
        }
    }

    /// <summary>Notes a COM object for the release in SearchWindowsUpdate's finally, and hands it back.</summary>
    private static object Keep(System.Collections.Generic.List<object> held, object com)
    {
        held.Add(com);
        return com;
    }

    /// <summary>"ASUSTeK COMPUTER INC." as Windows has it from the board.</summary>
    public static bool IsAsus(string boardVendor)
        => boardVendor.Contains("ASUS", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string Version, DateTime? Released)?> LatestAsusBiosAsync(string board, CancellationToken cancel)
    {
        string? id = null;
        string search = await Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture, AsusSearch,
                                                                Uri.EscapeDataString(board)), cancel)
                                  .ConfigureAwait(false);
        using (var found = JsonDocument.Parse(search))
        {
            // Groups of suggestions ("Explore Product", …), each with its
            // products. Only an exact name will do: the search also suggests
            // the board's siblings, and "B650-PLUS" is not "B650-PLUS WIFI",
            // whose BIOS would not even flash onto it.
            foreach (var group in found.RootElement.GetProperty("Result").EnumerateArray())
            {
                if (!group.TryGetProperty("Content", out var products) || products.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var product in products.EnumerateArray())
                {
                    if (product.TryGetProperty("Title", out var title)
                        && string.Equals(title.GetString()?.Trim(), board.Trim(), StringComparison.OrdinalIgnoreCase)
                        && product.TryGetProperty("DataId", out var dataId))
                    {
                        id = dataId.ValueKind == JsonValueKind.Number ? dataId.GetRawText() : dataId.GetString();
                        break;
                    }
                }

                if (id is not null)
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
            return null;

        string json = await Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture, AsusBios,
                                                              Uri.EscapeDataString(id)), cancel)
                                .ConfigureAwait(false);
        using var reply = JsonDocument.Parse(json);

        (string Version, DateTime? Released)? newest = null;
        foreach (var group in reply.RootElement.GetProperty("Result").GetProperty("Obj").EnumerateArray())
        {
            foreach (var file in group.GetProperty("Files").EnumerateArray())
            {
                // IsRelease "0" is what ASUS's page labels "Beta Version".
                // A beta is not something to tell everyone to flash.
                if (file.TryGetProperty("IsRelease", out var release) && release.GetString() != "1")
                    continue;

                string? version = file.GetProperty("Version").GetString();
                if (string.IsNullOrEmpty(version))
                    continue;

                // "2026/06/29"
                DateTime? released = file.TryGetProperty("ReleaseDate", out var raw)
                                     && DateTime.TryParseExact(raw.GetString(), "yyyy/MM/dd", CultureInfo.InvariantCulture,
                                                               DateTimeStyles.None, out var date)
                    ? date
                    : null;

                // Newest first is how ASUS lists them, but by date to be sure.
                if (newest is null || (released ?? DateTime.MinValue) > (newest.Value.Released ?? DateTime.MinValue))
                    newest = (version, released);
            }
        }

        return newest;
    }

    /// <summary>
    /// Null while it is young enough, rather than a reassuring line: age alone
    /// can't tell "up to date" from "not updated for a while".
    /// </summary>
    public static Verdict? Age(string what, DateTime? date, string? suffix = null)
    {
        if (date is null)
            return null;

        // Yellow from three months, red from a year - see the two constants.
        int months = MonthsSince(date.Value);
        return months >= OldAfterMonths
            ? new Verdict($"{what} is {months} months old - look for an update{suffix}", Freshness.Old, $"{months} months old")
            : months >= AgingAfterMonths
            ? new Verdict($"{what} is {months} months old - there may be an update{suffix}", Freshness.Aging, $"{months} months old")
            : null;
    }

    /// <summary>
    /// A newer one is out: yellow, or red when the one installed is also very
    /// old - "a release behind" and "a year behind" are not the same news.
    /// </summary>
    private static Freshness Behind(DateTime? installedDate)
        => installedDate is { } date && MonthsSince(date) >= OldAfterMonths ? Freshness.Old : Freshness.Aging;

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
