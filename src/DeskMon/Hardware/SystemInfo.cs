using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using Microsoft.Win32;
// Sizes and speeds in Invariant(...): a full stop as the decimal mark whatever
// the Windows language, like every other number in DeskMon - a Swedish PC
// showed "4,2 GHz" and "1,8 TB" here, and Copy gave different text per PC.
using static System.FormattableString;

namespace DeskMon.Hardware;

/// <summary>
/// One-off facts about the machine: what the parts are, rather than what they
/// are doing. Gathered once when the hardware window is opened.
///
/// Motherboard and BIOS come from the registry, which Windows fills from SMBIOS
/// at boot - instant, and no WMI round trip. Memory has to come from WMI, since
/// per-module brand and part numbers live in the SPD data that only WMI (or a
/// driver) will read. That query takes a few hundred milliseconds, which is why
/// nothing here runs on the UI thread or on a timer.
/// </summary>
public sealed class SystemInfo
{
    public string? Cpu { get; private set; }
    public string? Gpu { get; private set; }
    public string? Motherboard { get; private set; }
    public string? Bios { get; private set; }
    public string? SystemModel { get; private set; }
    public string? MemoryTotal { get; private set; }
    public IReadOnlyList<string> MemoryModules { get; private set; } = Array.Empty<string>();

    /// <summary>"8 cores / 16 threads · 4.2 GHz base · 96 MB L3", under the CPU name.</summary>
    public string? CpuDetail { get; private set; }

    /// <summary>"12 GB VRAM · driver 617.42 (2026-09-30)", under the graphics card.</summary>
    public string? GpuDetail { get; private set; }

    // The same facts as in the two lines above, kept apart for UpdateCheck
    // so it doesn't have to pick them back out of display text.
    public string? GpuDriverVersion { get; private set; }
    public DateTime? GpuDriverDate { get; private set; }
    public DateTime? BiosDate { get; private set; }

    // Likewise for the BIOS check, which asks the board's maker by board name.
    public string? BoardVendor { get; private set; }
    public string? BoardProduct { get; private set; }
    public string? BiosVersion { get; private set; }

    public string? OperatingSystem { get; private set; }

    /// <summary>When Windows last started, rather than an uptime: the window is
    /// filled once and then only hidden, so a duration would go stale while a
    /// point in time stays true.</summary>
    public string? UpSince { get; private set; }

    public IReadOnlyList<Drive> Drives { get; private set; } = Array.Empty<Drive>();

    /// <summary>
    /// One physical drive: what it is, and how it is holding up - "Healthy · 3%
    /// worn · 41 °C · 2,310 h on", or null if Windows says nothing about it.
    /// <paramref name="Level"/> colours that line: red when it is failing or
    /// has lost data - the time to make a backup.
    /// </summary>
    public sealed record Drive(string Text, string? Health, Condition Level);

    /// <summary>
    /// How a health check came out, for its colour: green, yellow, red - or
    /// dim when there's nothing to judge by. The same four steps as the
    /// update checks, so the colours mean one thing across the window.
    /// </summary>
    public enum Condition { Unknown, Good, Warning, Bad }

    /// <summary>
    /// One line of a health check, e.g. "Dual channel". <paramref name="Short"/>
    /// is the word or two the hardware window shows beside its item ("No
    /// errors", "EXPO on"); the full sentence stays for Copy, the tooltip and
    /// - when something is wrong - the note under the item, where the advice
    /// in it is the point.
    /// </summary>
    public sealed record Check(string Text, Condition Level, string? Short = null)
    {
        public string ShortText => Short ?? Text;
    }

    /// <summary>Lines under the memory modules: channels, speed, errors. See ReadMemoryChecks.</summary>
    public IReadOnlyList<Check> MemoryChecks { get; private set; } = Array.Empty<Check>();
    public IReadOnlyList<NetworkAdapter> NetworkAdapters { get; private set; } = Array.Empty<NetworkAdapter>();

    /// <summary>One physical adapter. Disconnected ones are listed too, greyed
    /// out, since "this machine has Wi-Fi" is worth knowing even when unused.</summary>
    public sealed record NetworkAdapter(string Description, bool Connected);

    /// <summary>
    /// Collects everything. Call from a background thread.
    /// <paramref name="cpuName"/> and <paramref name="gpuName"/> come from the
    /// sensor library, which already knows them and names the exact card the
    /// temperatures are read from. <paramref name="stabilityFrom"/> is
    /// AppSettings.StabilityCountedFrom.
    /// </summary>
    public static SystemInfo Collect(string? cpuName, string? gpuName, DateTime? stabilityFrom = null)
    {
        var info = new SystemInfo
        {
            Cpu = Clean(cpuName) ?? ReadCpuFromRegistry(),
            Gpu = Clean(gpuName) ?? ReadPrimaryGpuFromWmi(),
        };

        info.ReadBoardAndBios();
        info.ReadMemory();
        info.ReadCpuDetail();
        info.ReadCpuChecks();
        info.ReadGpuDetail();
        info.ReadOperatingSystem();
        info.ReadDrives();
        info.ReadNetwork();
        info.ReadGpuLink();
        info.ReadDisplays();
        info.ReadGamingChecks();
        info.ReadStability(stabilityFrom);
        return info;
    }

    /// <summary>
    /// The sensor library knows the CPU's name but not its layout, so this is
    /// WMI. MaxClockSpeed is the base clock despite its name; the boost clock
    /// is not reported anywhere short of a lookup table, so it is labelled
    /// "base" rather than passed off as the top speed.
    /// </summary>
    private void ReadCpuDetail()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor");
            using var results = searcher.Get();   // see ReadMemory

            // Summed, so a two-socket workstation shows all its cores; the
            // clock and cache are the same on every socket, so the first wins.
            uint cores = 0, threads = 0, mhz = 0, l3Kb = 0;
            foreach (ManagementBaseObject cpu in results)
            {
                using (cpu)
                {
                    cores += ToUint(cpu["NumberOfCores"]);
                    threads += ToUint(cpu["NumberOfLogicalProcessors"]);
                    if (mhz == 0) mhz = ToUint(cpu["MaxClockSpeed"]);
                    if (l3Kb == 0) l3Kb = ToUint(cpu["L3CacheSize"]);
                }
            }

            var parts = new List<string>();
            if (cores > 0)
                parts.Add(threads > cores ? $"{cores} cores / {threads} threads" : $"{cores} cores");
            if (mhz > 0)
                parts.Add(Invariant($"{mhz / 1000d:0.##} GHz base"));   // 4201 MHz reads 4.2, not 4.201
            if (l3Kb > 0)
                parts.Add(Invariant($"{l3Kb / 1024d:0.#} MB L3"));

            CpuDetail = parts.Count > 0 ? string.Join("  ·  ", parts) : null;
        }
        catch
        {
            // No detail line; the CPU name above it is still there.
        }
    }

    /// <summary>Lines under the CPU: power plan, hardware errors, Intel's microcode fix. See ReadCpuChecks.</summary>
    public IReadOnlyList<Check> CpuChecks { get; private set; } = Array.Empty<Check>();

    /// <summary>
    /// Each check on its own, so one that can't be read leaves the others.
    /// After ReadCpuDetail - the microcode check needs the CPU's name.
    /// </summary>
    private void ReadCpuChecks()
    {
        var checks = new List<Check>();

        if (PowerPlanCheck() is { } plan)
            checks.Add(plan);

        // WHEA 18 is a machine check the CPU couldn't recover from (the PC
        // crashed), 19 one it corrected. Both point at the CPU or its
        // overclock - PBO, Curve Optimizer, an undervolt - being unstable.
        int errors = CountEvents(
            "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=18 or EventID=19) " +
            "and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
        checks.Add(errors > 0
            ? new Check($"{errors} CPU hardware error{(errors == 1 ? "" : "s")} in the last 30 days - an overclock or undervolt may be unstable", Condition.Bad,
                        $"{errors} error{(errors == 1 ? "" : "s")}")
            : new Check("No CPU hardware errors logged in the last 30 days", Condition.Good, "No errors"));

        if (RaptorLakeCheck() is { } microcode)
            checks.Add(microcode);

        CpuChecks = checks;
    }

    /// <summary>
    /// Power saver caps the CPU's clock, and games lose frames to it - easy to
    /// leave on after a laptop's battery trip. Any other plan is fine.
    ///
    /// Named in English, like the rest of the window: Windows only hands out
    /// a plan's name in the PC's own language ("Balanserat"). Windows' own
    /// plans are known by their fixed ids; a custom one can't be translated,
    /// so it is named by the built-in plan it was made from - which every
    /// plan records, and which is also what decides whether it is a saver.
    /// </summary>
    private static Check? PowerPlanCheck()
    {
        IntPtr active = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out active) != 0)
                return null;
            var scheme = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(active);

            // 0 power saver, 1 high performance, 2 balanced.
            var noSubgroup = NoSubgroup;
            var personality = Personality;
            uint basedOn = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref noSubgroup, ref personality, out uint value) == 0
                ? value
                : uint.MaxValue;
            string? baseName = basedOn switch
            {
                0 => "Power saver",
                1 => "High performance",
                2 => "Balanced",
                _ => null,
            };

            string name = KnownPlans.TryGetValue(scheme, out var known) ? known
                        : baseName is not null ? $"custom, based on {baseName}"
                        : "custom";

            // The plan's own name when Windows knows it, else what it was
            // copied from - "High performance" says more than "custom".
            string shortName = KnownPlans.TryGetValue(scheme, out var knownShort) ? knownShort : baseName ?? "Custom";

            return basedOn == 0
                ? new Check($"Power plan: {name} - it holds the CPU back; Balanced or better for games", Condition.Warning, "Power saver plan")
                : new Check($"Power plan: {name}", Condition.Good, $"{shortName} plan");
        }
        catch
        {
            return null;
        }
        finally
        {
            if (active != IntPtr.Zero)
                LocalFree(active);
        }
    }

    /// <summary>Windows' own plans, by the ids they have on every PC.</summary>
    private static readonly Dictionary<Guid, string> KnownPlans = new()
    {
        [new Guid("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Balanced",
        [new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "High performance",
        [new Guid("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Power saver",
        [new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Ultimate Performance",
    };

    // NO_SUBGROUP_GUID and GUID_POWERSCHEME_PERSONALITY from winnt.h: the
    // hidden setting that says which built-in plan a plan was made from.
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid Personality = new("245d8541-3943-4422-b025-13a784f679b7");

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
                                                     ref Guid powerSettingGuid, out uint acValueIndex);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// Intel's 13th and 14th gen desktop chips (and HX laptops) degrade under
    /// a microcode bug that asks for too much voltage - crashes first, then
    /// lasting damage. Intel's fix is microcode 0x12B (September 2024), which
    /// arrives with a BIOS update. Null for every other CPU, which this
    /// doesn't concern.
    /// </summary>
    private Check? RaptorLakeCheck()
    {
        if (Cpu is null
            || !System.Text.RegularExpressions.Regex.IsMatch(Cpu, @"\bi[579]-1[34]\d{3}(K|KF|KS|F|HX)?\b",
                                                              System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("Update Revision") is not byte[] bytes || bytes.Length < 8)
                return null;

            // Eight bytes; on Intel the revision is the upper half.
            uint revision = BitConverter.ToUInt32(bytes, 4);
            return revision >= 0x12B
                ? new Check($"Has Intel's fix for the 13th/14th gen instability (microcode 0x{revision:X})", Condition.Good, "Microcode fixed")
                : new Check($"Missing Intel's fix for the 13th/14th gen instability (microcode 0x{revision:X}, needs 0x12B) - update the BIOS", Condition.Bad, "Microcode fix missing");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// From the display adapter's driver key rather than WMI: Win32_VideoController's
    /// AdapterRAM is a 32-bit field and tops out at 4 GB, while the registry
    /// keeps the full 64-bit size. The key is matched to the card by name, so a
    /// machine with an iGPU as well gets the details of the card actually shown.
    /// </summary>
    private void ReadGpuDetail()
    {
        if (Gpu is null)
            return;

        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null)
                return;

            foreach (string sub in adapters.GetSubKeyNames())
            {
                // The class key also holds "Properties", which is locked down.
                if (sub.Length != 4 || !char.IsDigit(sub[0]))
                    continue;

                // Per key, so one adapter's locked key (a remote-display or
                // virtual adapter's, say) can't hide the card listed after it.
                RegistryKey? key;
                try { key = adapters.OpenSubKey(sub); }
                catch { continue; }

                using (key)
                {
                    if (key is not null && TryReadCard(key, Gpu))
                        return;
                }
            }
        }
        catch
        {
            // No detail line.
        }
    }

    /// <summary>Fills the GPU detail from one adapter's driver key, if it is the card shown.</summary>
    private bool TryReadCard(RegistryKey key, string gpuName)
    {
        string? desc = Clean(key.GetValue("DriverDesc") as string);
        if (desc is null || !SameCard(desc, gpuName))
            return false;

        var parts = new List<string>();

        if (key.GetValue("HardwareInformation.qwMemorySize") is long bytes && bytes > 0)
        {
            double gb = bytes / (1024d * 1024 * 1024);
            parts.Add(gb >= 1 ? Invariant($"{gb:0.#} GB VRAM") : $"{bytes / (1024 * 1024)} MB VRAM");
        }

        string? driver = DriverVersionForPeople(desc, key);
        DateTime? date = DriverDate(key.GetValue("DriverDate") as string);
        if (driver is not null)
            parts.Add(date is null ? $"driver {driver}" : $"driver {driver} ({date:yyyy-MM-dd})");

        GpuDriverVersion = driver;
        GpuDriverDate = date;
        GpuDetail = parts.Count > 0 ? string.Join("  ·  ", parts) : null;
        return true;
    }

    /// <summary>
    /// The GPU driver (as the vendor's app shows it) and the Windows build
    /// ("26300.6899"), for the session log - so a session played on a new
    /// driver or after a Windows update can be marked. From the registry
    /// only, the same keys the hardware window reads but without its WMI
    /// queries: about a millisecond. Either is null when it can't be read.
    /// </summary>
    public static (string? Driver, string? WindowsBuild) QuickVersions(string? gpuName)
    {
        string? driver = null, build = null;

        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is not null && gpuName is not null)
            {
                foreach (string sub in adapters.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !char.IsDigit(sub[0]))
                        continue;   // "Properties" and the like - see ReadGpuDetail
                    try
                    {
                        using var key = adapters.OpenSubKey(sub);
                        if (key is not null && Clean(key.GetValue("DriverDesc") as string) is { } desc && SameCard(desc, gpuName))
                        {
                            driver = DriverVersionForPeople(desc, key);
                            break;
                        }
                    }
                    catch
                    {
                        // A locked adapter key; try the next.
                    }
                }
            }
        }
        catch
        {
            // No driver version.
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null && Clean(key.GetValue("CurrentBuildNumber") as string) is { } number)
                build = key.GetValue("UBR") is int patch ? $"{number}.{patch}" : number;
        }
        catch
        {
            // No build.
        }

        return (driver, build);
    }

    /// <summary>Equal, or one inside the other: the sensor library and the
    /// driver can disagree on a "(TM)" or a trailing word.</summary>
    private static bool SameCard(string driverDesc, string gpuName)
        => driverDesc.Equals(gpuName, StringComparison.OrdinalIgnoreCase)
           || driverDesc.Contains(gpuName, StringComparison.OrdinalIgnoreCase)
           || gpuName.Contains(driverDesc, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The version the vendor's own app shows, rather than Windows' four-part one.
    /// NVIDIA's is the last five digits of the Windows version (32.0.16.1742 is
    /// 617.42). AMD writes the Adrenalin version next to the driver. Anything
    /// else - Intel included - goes by the Windows version as is.
    /// </summary>
    private static string? DriverVersionForPeople(string driverDesc, RegistryKey key)
    {
        string? windows = Clean(key.GetValue("DriverVersion") as string);

        if (driverDesc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && windows is not null)
        {
            string[] v = windows.Split('.');
            if (v.Length == 4)
            {
                string digits = v[2] + v[3].PadLeft(4, '0');
                if (digits.Length >= 5)
                {
                    string last5 = digits[^5..];
                    return $"{last5[..3]}.{last5[3..]}";
                }
            }
        }

        if (Clean(key.GetValue("RadeonSoftwareVersion") as string) is { } adrenalin)
            return adrenalin;

        return windows;
    }

    /// <summary>The registry writes it as "9-30-2026", month first, whatever the PC's language.</summary>
    private static DateTime? DriverDate(string? usDate)
        => DateTime.TryParseExact(usDate, "M-d-yyyy", CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// ProductName still says "Windows 10" on Windows 11 - Microsoft never
    /// changed it, for compatibility - so the build number decides: 22000 and
    /// up is Windows 11.
    /// </summary>
    private void ReadOperatingSystem()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                string? product = Clean(key.GetValue("ProductName") as string);
                string? release = Clean(key.GetValue("DisplayVersion") as string);
                string? build = Clean(key.GetValue("CurrentBuildNumber") as string);
                object? ubr = key.GetValue("UBR");

                if (product is not null && int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
                    product = product.Replace("Windows 10", "Windows 11");

                var parts = new List<string>();
                if (product is not null) parts.Add(release is null ? product : $"{product} {release}");
                if (build is not null) parts.Add(ubr is int patch ? $"build {build}.{patch}" : $"build {build}");
                OperatingSystem = parts.Count > 0 ? string.Join("  ·  ", parts) : null;
            }
        }
        catch
        {
            // Left as unknown.
        }

        // TickCount64 counts from when the kernel started. With Fast Startup,
        // shutting down hibernates the kernel instead of ending it, so this can
        // be days earlier than the last power-on - which is also what Task
        // Manager's "Up time" says, and is the truth about how long Windows has run.
        var started = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        UpSince = started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The Storage namespace rather than Win32_DiskDrive, because only it says
    /// SSD or HDD and NVMe or SATA. USB, SD and virtual disks are skipped: a
    /// stick that happens to be plugged in is not part of the machine.
    /// </summary>
    private void ReadDrives()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
            using var results = searcher.Get();   // see ReadMemory

            var counters = ReadReliability();
            var drives = new List<(int Order, Drive Drive)>();
            foreach (ManagementBaseObject disk in results)
            {
                using (disk)
                {
                    uint bus = ToUint(disk["BusType"]);
                    // 7 USB, 12 SD, 13 MMC, 14 virtual, 15 file-backed virtual
                    if (bus is 7 or 12 or 13 or 14 or 15)
                        continue;

                    string? name = Clean(disk["FriendlyName"] as string);
                    ulong size = ToUlong(disk["Size"]);
                    string? kind = DriveKind(ToUint(disk["MediaType"]), bus);

                    var parts = new List<string>();
                    if (name is not null) parts.Add(name);
                    if (size > 0) parts.Add(DriveSize(size));
                    if (kind is not null) parts.Add(kind);

                    if (parts.Count > 0)
                    {
                        string deviceId = disk["DeviceId"] as string ?? string.Empty;
                        // A missing status is "unknown" (5), not ToUint's 0 - which
                        // would call a drive Windows said nothing about healthy.
                        uint status = disk["HealthStatus"] is null ? 5 : ToUint(disk["HealthStatus"]);
                        var (health, level) = DriveHealth(status,
                                                            counters.GetValueOrDefault(deviceId));
                        drives.Add((int.TryParse(deviceId, out int id) ? id : int.MaxValue,
                                    new Drive(string.Join("  ·  ", parts), health, level)));
                    }
                }
            }

            // Disk 0 first, the way Disk Management lists them.
            drives.Sort((a, b) => a.Order.CompareTo(b.Order));
            Drives = drives.ConvertAll(d => d.Drive);
        }
        catch
        {
            // No storage rows.
        }
    }

    /// <summary>The figures a drive keeps on itself (SMART), as Windows hands them on.</summary>
    private sealed record Reliability(uint? Wear, uint? Temperature, uint? PowerOnHours, ulong Errors);

    /// <summary>
    /// Every drive's reliability counters, by DeviceId - the same id
    /// MSFT_PhysicalDisk uses. Read here, once, as the Hardware window opens,
    /// and never on a tick: asking a drive for them can stall for a moment,
    /// which is why the sensor side leaves storage alone.
    ///
    /// Needs administrator, which DeskMon has. Without it, or on a drive that
    /// reports nothing, the line just has fewer figures.
    /// </summary>
    private static Dictionary<string, Reliability> ReadReliability()
    {
        var counters = new Dictionary<string, Reliability>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Wear, Temperature, PowerOnHours, ReadErrorsUncorrected, WriteErrorsUncorrected " +
                "FROM MSFT_StorageReliabilityCounter");
            using var results = searcher.Get();   // see ReadMemory

            foreach (ManagementBaseObject counter in results)
            {
                using (counter)
                {
                    if (counter["DeviceId"] is not string id)
                        continue;

                    // Null means "not reported", which is not the same as 0 -
                    // a drive at 0% wear says so, one that doesn't know says nothing.
                    counters[id] = new Reliability(
                        counter["Wear"] is null ? null : ToUint(counter["Wear"]),
                        counter["Temperature"] is null ? null : ToUint(counter["Temperature"]),
                        counter["PowerOnHours"] is null ? null : ToUint(counter["PowerOnHours"]),
                        ToUlong(counter["ReadErrorsUncorrected"]) + ToUlong(counter["WriteErrorsUncorrected"]));
                }
            }
        }
        catch
        {
            // No counters - the lines show the health status alone.
        }
        return counters;
    }

    /// <summary>
    /// "Healthy · 3% worn · 41 °C · 2,310 h on". Wear is how much of the drive's
    /// rated writes are used up, which on an SSD is the figure that says how
    /// long it has left. Errors are only mentioned when there are some: an
    /// uncorrected one is data the drive lost, and the reason to warn.
    ///
    /// The colour is the worst of its parts: yellow from 70% wear or 70 °C
    /// (where NVMe drives start slowing themselves down), red from 90% wear,
    /// any lost data, or Windows itself calling the drive unhealthy.
    /// </summary>
    private static (string? Text, Condition Level) DriveHealth(uint status, Reliability? counters)
    {
        var parts = new List<string>();

        // HealthStatus: 0 healthy, 1 warning, 2 unhealthy, 5 unknown.
        var level = status switch
        {
            0 => Condition.Good,
            1 => Condition.Warning,
            2 => Condition.Bad,
            _ => Condition.Unknown,
        };
        switch (status)
        {
            case 0: parts.Add("Healthy"); break;
            case 1: parts.Add("Warning - back it up"); break;
            case 2: parts.Add("Unhealthy - back it up now"); break;
        }

        if (counters is not null)
        {
            if (counters.Wear is uint wear)
            {
                parts.Add($"{wear}% worn");
                level = Worst(level, wear >= 90 ? Condition.Bad : wear >= 70 ? Condition.Warning : Condition.Good);
            }
            if (counters.Temperature is uint temp and > 0)
            {
                parts.Add($"{temp} °C");
                if (temp >= 70)
                    level = Worst(level, Condition.Warning);
            }
            if (counters.PowerOnHours is uint hours and > 0)
                parts.Add(hours.ToString("#,0", CultureInfo.InvariantCulture) + " h on");
            if (counters.Errors > 0)
            {
                parts.Add($"{counters.Errors} uncorrected error{(counters.Errors == 1 ? "" : "s")}");
                level = Condition.Bad;
            }
        }

        return (parts.Count > 0 ? string.Join("  ·  ", parts) : null, level);
    }

    /// <summary>The more serious of two - the enum runs Unknown, Good, Warning, Bad.</summary>
    private static Condition Worst(Condition a, Condition b) => a > b ? a : b;

    /// <summary>MediaType 3 is HDD, 4 SSD; BusType 17 NVMe, 11 SATA.</summary>
    private static string? DriveKind(uint media, uint bus)
    {
        string? type = media switch { 3 => "HDD", 4 => "SSD", _ => null };
        return bus switch
        {
            17 => "NVMe SSD",   // every NVMe drive is solid state, whatever MediaType says
            11 when type is not null => $"SATA {type}",
            _ => type,
        };
    }

    /// <summary>
    /// Decimal units, as on the box and in the model name: a "1TB" drive is
    /// 1,000,204,886,016 bytes, which would read as 931.5 GB in binary units
    /// and look like the wrong drive.
    /// </summary>
    private static string DriveSize(ulong bytes)
        => bytes >= 1_000_000_000_000
            ? Invariant($"{bytes / 1e12:0.#} TB")
            : Invariant($"{bytes / 1e9:0} GB");

    /// <summary>
    /// MSFT_NetAdapter rather than .NET's NetworkInterface, because only it
    /// knows which adapters are real hardware: VPN tunnels, Hyper-V switches
    /// and WSL's vEthernet all look like network cards otherwise.
    /// </summary>
    private void ReadNetwork()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2",
                "SELECT InterfaceDescription, MediaConnectState, Speed, NdisPhysicalMedium FROM MSFT_NetAdapter " +
                "WHERE HardwareInterface = TRUE AND Virtual = FALSE");
            using var results = searcher.Get();   // see ReadMemory

            var adapters = new List<NetworkAdapter>();
            var checks = new List<Check>();
            foreach (ManagementBaseObject adapter in results)
            {
                using (adapter)
                {
                    string? name = Clean(adapter["InterfaceDescription"] as string);
                    if (name is null)
                        continue;

                    bool connected = ToUint(adapter["MediaConnectState"]) == 1;
                    ulong speed = ToUlong(adapter["Speed"]);

                    // The link speed, not the card's top speed: a 2.5 GbE
                    // card on a gigabit switch says 1 Gbit/s, which is the
                    // part worth knowing.
                    string text = !connected ? $"{name}  ·  not connected"
                                : speed > 0 ? $"{name}  ·  {LinkSpeed(speed)}"
                                : name;
                    adapters.Add(new NetworkAdapter(text, connected));

                    if (connected && speed > 0)
                        checks.Add(LinkCheck(ToUint(adapter["NdisPhysicalMedium"]), speed));
                }
            }

            // Connected first: that is the one in use.
            adapters.Sort((a, b) => b.Connected.CompareTo(a.Connected));
            NetworkAdapters = adapters;
            NetworkChecks = checks;
        }
        catch
        {
            // No network rows.
        }
    }

    /// <summary>
    /// A cable linked at 100 Mbit/s instead of 1 Gbit/s or more is nearly
    /// always a damaged cable or a loose plug - gigabit needs all eight wires,
    /// and drops to 100 when one pair fails. Wi-Fi isn't judged: its rate
    /// moves with the room, but the line says a cable is steadier for games.
    /// </summary>
    private static Check LinkCheck(uint medium, ulong speed)
    {
        // NdisPhysicalMedium 9 is native 802.11 - Wi-Fi.
        if (medium == 9)
            return new Check($"Wi-Fi at {LinkSpeed(speed)} - a cable gives a steadier ping in games", Condition.Unknown, $"Wi-Fi at {LinkSpeed(speed)}");

        return speed >= 1_000_000_000
            ? new Check($"Cable linked at {LinkSpeed(speed)}", Condition.Good, $"linked at {LinkSpeed(speed)}")
            : new Check($"Cable linked at only {LinkSpeed(speed)} - usually a damaged cable or loose plug; it should be 1 Gbit/s or more", Condition.Warning,
                        $"only {LinkSpeed(speed)}");
    }

    /// <summary>Lines under the network adapters: the link of each one in use. Ping is measured by the window - see NetworkCheck.</summary>
    public IReadOnlyList<Check> NetworkChecks { get; private set; } = Array.Empty<Check>();

    private static string LinkSpeed(ulong bitsPerSecond)
        => bitsPerSecond >= 1_000_000_000
            ? Invariant($"{bitsPerSecond / 1e9:0.#} Gbit/s")
            : Invariant($"{bitsPerSecond / 1e6:0} Mbit/s");

    /// <summary>
    /// Windows mirrors the SMBIOS tables here, so this is the same data the
    /// BIOS screen and msinfo32 show.
    /// </summary>
    private void ReadBoardAndBios()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is null)
                return;

            string? boardVendor = Clean(key.GetValue("BaseBoardManufacturer") as string);
            string? boardProduct = Clean(key.GetValue("BaseBoardProduct") as string);
            Motherboard = Join(boardVendor, boardProduct);
            BoardVendor = boardVendor;
            BoardProduct = boardProduct;

            string? biosVendor = Clean(key.GetValue("BIOSVendor") as string);
            string? biosVersion = Clean(key.GetValue("BIOSVersion") as string);
            BiosVersion = biosVersion;
            string? biosDate = Clean(key.GetValue("BIOSReleaseDate") as string);

            // SMBIOS fixes this as mm/dd/yyyy, whatever the PC's language.
            if (DateTime.TryParseExact(biosDate, new[] { "MM/dd/yyyy", "M/d/yyyy" }, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var released))
                BiosDate = released;

            // "1654 (American Megatrends, 2024-05-13)" reads better than three rows.
            string version = biosVersion ?? "unknown";
            var extras = new List<string>();
            if (biosVendor is not null) extras.Add(biosVendor);
            if (biosDate is not null) extras.Add(biosDate);
            Bios = extras.Count > 0 ? $"{version}  ({string.Join(", ", extras)})" : version;

            SystemModel = Join(Clean(key.GetValue("SystemManufacturer") as string),
                               Clean(key.GetValue("SystemProductName") as string));
        }
        catch
        {
            // Leave whatever was read; the window shows "unknown" for the rest.
        }
    }

    private void ReadMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, " +
                "DeviceLocator, BankLabel FROM Win32_PhysicalMemory");

            // Each stick as its description (name, size, speed) and its slot,
            // kept apart so matching sticks can be told apart from their slots
            // and folded into one line below.
            var modules = new List<(string Description, string? Slot)>();
            ulong totalBytes = 0;

            // For the checks under the modules: what each runs at, what its
            // standard speed is, and which channel it sits in.
            var configured = new List<uint>();
            var rated = new List<uint>();
            var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool channelsKnown = true;

            // The collection holds a WMI enumerator of its own, released only
            // by disposing it - disposing the searcher and each row is not enough.
            using var results = searcher.Get();

            foreach (ManagementBaseObject module in results)
            {
                using (module)
                {
                    string? vendor = Clean(module["Manufacturer"] as string);
                    string? part = Clean(module["PartNumber"] as string);

                    // DeviceLocator is numbered per channel, so two sticks in
                    // different channels both report "DIMM 1" - which reads as
                    // if they were in the same slot. BankLabel names the
                    // channel, which is the part worth knowing.
                    string? slot = Clean(module["BankLabel"] as string)
                                   ?? Clean(module["DeviceLocator"] as string);

                    ulong capacity = ToUlong(module["Capacity"]);
                    totalBytes += capacity;

                    // ConfiguredClockSpeed is what it actually runs at (XMP/EXPO);
                    // Speed is only what the module is rated for.
                    uint speed = ToUint(module["ConfiguredClockSpeed"]);
                    if (speed == 0) speed = ToUint(module["Speed"]);

                    configured.Add(ToUint(module["ConfiguredClockSpeed"]));
                    rated.Add(ToUint(module["Speed"]));

                    // "P0 CHANNEL A" - the letter is the channel. A board that
                    // labels its banks some other way leaves the channel check out
                    // rather than guess.
                    var channel = System.Text.RegularExpressions.Regex.Match(
                        module["BankLabel"] as string ?? string.Empty, @"CHANNEL\s*([A-Z])",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (channel.Success)
                        channels.Add(channel.Groups[1].Value);
                    else
                        channelsKnown = false;

                    var parts = new List<string>();
                    string? name = Join(vendor, part);
                    if (name is not null) parts.Add(name);
                    if (capacity > 0) parts.Add(Invariant($"{capacity / (1024d * 1024 * 1024):0.#} GB"));
                    if (speed > 0) parts.Add($"{speed} MT/s");
                    if (parts.Count > 0 || slot is not null)
                        modules.Add((string.Join("  ·  ", parts), slot));
                }
            }

            MemoryModules = GroupModules(modules);
            if (totalBytes > 0)
                MemoryTotal = Invariant($"{totalBytes / (1024d * 1024 * 1024):0.#} GB in {modules.Count} ") +
                              (modules.Count == 1 ? "module" : "modules");

            var checks = new List<Check>();
            if (ChannelCheck(configured.Count, channelsKnown ? channels.Count : 0) is { } channelCheck)
                checks.Add(channelCheck);
            if (SpeedCheck(configured, rated) is { } speedCheck)
                checks.Add(speedCheck);
            checks.AddRange(MemoryErrorChecks());
            MemoryChecks = checks;
        }
        catch
        {
            // WMI disabled or the class is unavailable - the rows just read "unknown".
        }
    }

    /// <summary>
    /// A matched kit as one line - "2 × Corsair … · 16 GB · 6000 MT/s" - rather
    /// than the same stick twice with only the slot told apart, each with its
    /// own copy of the maker's logo. The slots go from a grouped line: the
    /// channel check under the modules already says whether they sit right.
    /// A lone stick keeps its slot, and sticks that differ stay one line each,
    /// since there the slot is what tells them apart. In first-seen order, so
    /// a mixed set lists the way the board numbers its slots.
    /// </summary>
    private static List<string> GroupModules(List<(string Description, string? Slot)> modules)
    {
        var lines = new List<string>();
        foreach (var group in modules.GroupBy(m => m.Description))
        {
            int count = group.Count();
            string? slot = group.First().Slot;
            lines.Add(count > 1 && group.Key.Length > 0 ? $"{count} × {group.Key}"
                    : group.Key.Length == 0 ? slot ?? string.Empty
                    : slot is null ? group.Key
                    : $"{group.Key}  ·  {slot}");
        }
        return lines;
    }

    /// <summary>
    /// One module, or every module on one channel, halves the memory
    /// bandwidth - the CPU reads from the channels side by side. Games notice:
    /// it shows in the 1% lows more than the average. Null when the board
    /// labels its banks in a way that doesn't name the channel.
    /// </summary>
    private static Check? ChannelCheck(int modules, int channels)
    {
        if (modules == 1)
            return new Check("One module - single channel. A second, matching one would double the memory bandwidth", Condition.Warning, "single channel");
        if (channels == 0)
            return null;
        if (channels == 1)
            return new Check("All modules on one channel - moving one to the other channel's slot doubles the bandwidth (see the board's manual)", Condition.Warning, "one channel");
        return new Check(channels == 2 ? "Dual channel" : $"{channels} channels", Condition.Good,
                         channels == 2 ? "dual channel" : $"{channels} channels");
    }

    /// <summary>
    /// Whether EXPO/XMP is on. Speed is the module's standard (JEDEC) speed and
    /// ConfiguredClockSpeed what it runs at; above standard means the profile
    /// is on. At standard it may be off - or the kit may simply not be rated
    /// faster, which nothing here can tell. So that case is said, not judged.
    /// </summary>
    private static Check? SpeedCheck(List<uint> configured, List<uint> rated)
    {
        if (configured.Count == 0 || configured.Contains(0) || rated.Contains(0))
            return null;

        uint running = configured.Min();
        uint standard = rated.Max();

        return running > standard
            ? new Check($"EXPO/XMP on - running at {running} MT/s, above the standard {standard}", Condition.Good, "EXPO/XMP on")
            : new Check($"Running at the standard {running} MT/s - if the kit is rated faster, turn on EXPO/XMP in the BIOS", Condition.Unknown, "EXPO/XMP off");
    }

    /// <summary>
    /// What Windows has logged about the memory: corrected memory errors in
    /// the last 30 days (WHEA event 47 - on memory that can correct them, a
    /// sign it is failing or an EXPO/XMP profile is unstable), and the last
    /// Windows Memory Diagnostic result (1201 passed, 1202 found errors).
    ///
    /// No logged errors is said gently: ordinary desktop memory can't notice
    /// its own errors, so on most PCs a crash is the only sign. That is why the
    /// line also points at Memory Diagnostic when it has never been run.
    /// </summary>
    private static IEnumerable<Check> MemoryErrorChecks()
    {
        var checks = new List<Check>();

        int corrected = CountEvents(
            "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and EventID=47 " +
            "and TimeCreated[timediff(@SystemTime) <= 2592000000]]]");
        checks.Add(corrected > 0
            ? new Check($"{corrected} corrected memory error{(corrected == 1 ? "" : "s")} in the last 30 days - a module may be failing, or EXPO/XMP is unstable", Condition.Bad,
                        $"{corrected} error{(corrected == 1 ? "" : "s")}")
            : new Check("No memory errors logged by Windows in the last 30 days", Condition.Good, "No errors"));

        var (id, when) = LastEvent(
            "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1201 or EventID=1202)]]");
        checks.Add(id switch
        {
            1201 => new Check($"Passed Windows Memory Diagnostic on {when:yyyy-MM-dd}", Condition.Good, "test passed"),
            1202 => new Check($"Windows Memory Diagnostic found errors on {when:yyyy-MM-dd} - a module may be failing", Condition.Bad, "test failed"),
            _ => new Check("Never tested - Windows Memory Diagnostic (mdsched in Start) checks it at the next restart", Condition.Unknown, "never tested"),
        });

        return checks;
    }

    /// <summary>How many System log events match - 0 if the log can't be read.</summary>
    private static int CountEvents(string query)
    {
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                    System.Diagnostics.Eventing.Reader.PathType.LogName, query));
            int count = 0;
            // Capped: a failing module can log thousands, and the number only
            // needs to say "some".
            for (var e = reader.ReadEvent(); e is not null && count < 1000; e = reader.ReadEvent())
            {
                e.Dispose();
                count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The newest matching System log event's id and time, or (null, null).</summary>
    private static (int? Id, DateTime? When) LastEvent(string query)
    {
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                    System.Diagnostics.Eventing.Reader.PathType.LogName, query) { ReverseDirection = true });
            using var e = reader.ReadEvent();
            return e is null ? (null, null) : (e.Id, e.TimeCreated);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadCpuFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Clean(key?.GetValue("ProcessorNameString") as string);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadPrimaryGpuFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();   // see ReadMemory

            foreach (ManagementBaseObject gpu in results)
            {
                using (gpu)
                {
                    if (Clean(gpu["Name"] as string) is { } name)
                        return name;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Trims, and treats the placeholders OEMs leave behind as absent.</summary>
    private static string? Clean(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        return value is "To be filled by O.E.M." or "Default string" or "System Product Name"
                     or "To Be Filled By O.E.M." or "Unknown" or "None" or "N/A"
            ? null
            : value;
    }

    /// <summary>"ASUS" + "TUF GAMING B650-PLUS" without repeating the vendor.</summary>
    private static string? Join(string? vendor, string? product)
    {
        if (vendor is null) return product;
        if (product is null) return vendor;

        return product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
            ? product
            : $"{vendor} {product}";
    }

    // --- Gaming checkup: PCIe link, displays, Windows settings, stability ---

    /// <summary>The graphics card's PCIe link, under the card. Null if it couldn't be read.</summary>
    public Check? GpuLink { get; private set; }

    /// <summary>
    /// A card in the wrong slot, or on a riser that can't carry all sixteen
    /// lanes, runs at x8 or x4 and loses frames without any other sign. Only
    /// the width is judged: the card drops its link speed (the PCIe
    /// generation) when idle to save power, so a low speed here means nothing.
    /// Read from the device's own properties - the same figures GPU-Z shows.
    /// </summary>
    private void ReadGpuLink()
    {
        if (Gpu is null)
            return;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            using var results = searcher.Get();   // see ReadMemory

            string? deviceId = null;
            foreach (ManagementBaseObject card in results)
            {
                using (card)
                {
                    if (Clean(card["Name"] as string) is { } name && SameCard(name, Gpu))
                        deviceId ??= card["PNPDeviceID"] as string;
                }
            }

            if (deviceId is null || CM_Locate_DevNodeW(out uint node, deviceId, 0) != 0)
                return;

            uint? width = PciProperty(node, 10), maxWidth = PciProperty(node, 12), maxSpeed = PciProperty(node, 11);
            if (width is not uint w || maxWidth is not uint max || w == 0)
                return;

            // Speed is coded 1-5 for PCIe 1.0 to 5.0.
            string generation = maxSpeed is uint s and >= 1 and <= 6 ? $", PCIe {s}.0" : string.Empty;
            GpuLink = w >= max
                ? new Check($"Full PCIe link: x{w}{generation}", Condition.Good,
                            maxSpeed is uint g and >= 1 and <= 6 ? $"PCIe {g}.0 x{w}" : $"PCIe x{w}")
                : new Check($"PCIe link only x{w} of its x{max} - is it in the top slot? A riser cable can do this too", Condition.Warning,
                            $"PCIe x{w} of x{max}");
        }
        catch
        {
            // No link line.
        }
    }

    /// <summary>One DEVPKEY_PciDevice_* value (pciprop.h) as a number, or null.</summary>
    private static uint? PciProperty(uint node, uint pid)
    {
        var key = new DevPropKey { Fmtid = new Guid("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"), Pid = pid };
        var buffer = new byte[4];
        uint size = 4;
        return CM_Get_DevNode_PropertyW(node, ref key, out uint type, buffer, ref size, 0) == 0 && type == 7   // DEVPROP_TYPE_UINT32
            ? BitConverter.ToUInt32(buffer, 0)
            : null;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type,
                                                       byte[] buffer, ref uint size, uint flags);

    /// <summary>One screen: its name and mode, and whether it runs at the best refresh rate it has.</summary>
    public sealed record Display(string Text, Check Refresh);

    public IReadOnlyList<Display> Displays { get; private set; } = Array.Empty<Display>();

    /// <summary>
    /// A 144 or 240 Hz screen left at 60 Hz is one of the most common slips -
    /// after a new screen, a new cable, or a driver reinstall that reset it.
    /// The best rate is looked for at the resolution in use, since a higher
    /// rate at a lower resolution isn't what anyone wants.
    /// </summary>
    private void ReadDisplays()
    {
        try
        {
            var names = MonitorNames();

            // Name, mode and best rate per screen, put into lines once all are
            // in - so matching screens can share one (see GroupDisplays).
            var screens = new List<(string Name, string Setting, uint Current, uint Best)>();

            for (uint i = 0; ; i++)
            {
                var device = new DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevicesW(null, i, ref device, 0))
                    break;
                if ((device.State & 1) == 0)   // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
                    continue;

                var current = NewDevMode();
                if (!EnumDisplaySettingsW(device.Name, -1, ref current))   // ENUM_CURRENT_SETTINGS
                    continue;

                uint best = current.Frequency;
                for (int mode = 0; ; mode++)
                {
                    var other = NewDevMode();
                    if (!EnumDisplaySettingsW(device.Name, mode, ref other))
                        break;
                    if (other.Width == current.Width && other.Height == current.Height && other.Frequency > best)
                        best = other.Frequency;
                }

                // The monitor's own name ("ROG XG27AQ") from its EDID, matched
                // by device path; Windows' own string is "Generic PnP Monitor".
                var monitor = new DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
                string path = EnumDisplayDevicesW(device.Name, 0, ref monitor, 1)   // EDD_GET_DEVICE_INTERFACE_NAME
                    ? MonitorPath(monitor.Id)
                    : string.Empty;
                string name = path.Length > 0
                              && names.FirstOrDefault(n => n.Key.StartsWith(path + "_", StringComparison.OrdinalIgnoreCase)).Value is { } known
                    ? known
                    : $"Display {screens.Count + 1}";

                // The maker in front, since the EDID name is often the model
                // alone - "XV272 X" says nothing to anyone who doesn't know
                // it's an Acer. It is also what the row's logo is found by.
                // Left off when the name already says it ("DELL S2721DGF").
                if (MonitorMaker(path) is { } maker && !name.Contains(maker, StringComparison.OrdinalIgnoreCase))
                    name = $"{maker} {name}";

                string setting = $"{current.Width} × {current.Height}  ·  {current.Frequency} Hz";
                screens.Add((name, setting, current.Frequency, best));
            }

            Displays = GroupDisplays(screens);
        }
        catch
        {
            // No display rows.
        }
    }

    /// <summary>
    /// Matching screens - same model, same mode, same best rate - as one line,
    /// "2 × XV272 X · 1920 × 1080 · 240 Hz", with one refresh line for them
    /// all: two identical rows and two identical checks under them only made
    /// the reader compare them to find there was no difference. Screens that
    /// differ in any of those stay a line each, since then the difference is
    /// the news - one of a pair left at 60 Hz shows on its own. Unnamed
    /// screens are numbered apart, so they never fold together.
    /// </summary>
    private static List<Display> GroupDisplays(List<(string Name, string Setting, uint Current, uint Best)> screens)
    {
        var displays = new List<Display>();
        foreach (var group in screens.GroupBy(s => s))
        {
            int count = group.Count();
            var (name, setting, current, best) = group.Key;

            // "Both" for a pair, "All 3" past that, so the line still reads as
            // a sentence about every screen it stands for.
            string who = count == 2 ? "Both" : $"All {count}";
            var refresh = current >= best
                ? new Check(count == 1 ? $"At its highest refresh rate, {current} Hz"
                                       : $"{who} at their highest refresh rate, {current} Hz", Condition.Good, "Max Hz")
                : new Check(count == 1 ? $"Can run at {best} Hz, but is set to {current} - Settings > Display > Advanced display"
                                       : $"{who} can run at {best} Hz, but are set to {current} - Settings > Display > Advanced display",
                            Condition.Warning, $"{current} of {best} Hz");

            displays.Add(new Display(count == 1 ? $"{name}  ·  {setting}" : $"{count} × {name}  ·  {setting}", refresh));
        }
        return displays;
    }

    /// <summary>
    /// Each monitor's EDID name, by its instance path ("DISPLAY\GSM5B7F\5&amp;…_0").
    /// From WmiMonitorID, whose name comes as an array of character codes.
    /// </summary>
    private static Dictionary<string, string> MonitorNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
            using var results = searcher.Get();   // see ReadMemory
            foreach (ManagementBaseObject monitor in results)
            {
                using (monitor)
                {
                    if (monitor["InstanceName"] is string instance && monitor["UserFriendlyName"] is ushort[] codes)
                    {
                        string name = new string(codes.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                        if (name.Length > 0)
                            names[instance] = name;
                    }
                }
            }
        }
        catch
        {
            // Named "Display 1" and so on instead.
        }
        return names;
    }

    /// <summary>
    /// The monitor's maker from the three-letter code its EDID starts with -
    /// the "GSM" in "DISPLAY\GSM5B7F\…" is LG. Only the makers of screens
    /// people buy: a laptop's built-in panel reports whoever made the panel
    /// (AU Optronics, BOE), which isn't the brand on the lid, so those get
    /// nothing rather than a name that would read as wrong.
    /// </summary>
    private static string? MonitorMaker(string path)
    {
        string[] parts = path.Split('\\');
        if (parts.Length < 2 || parts[1].Length < 3)
            return null;

        return parts[1][..3].ToUpperInvariant() switch
        {
            "ACR" => "Acer",
            "ACI" or "AUS" => "ASUS",
            "SAM" or "SEC" => "Samsung",
            "GSM" => "LG",
            "DEL" => "Dell",
            "HWP" or "HPN" => "HP",
            "LEN" => "Lenovo",
            "MSI" => "MSI",
            "AOC" => "AOC",
            "BNQ" => "BenQ",
            "PHL" => "Philips",
            "SNY" => "Sony",
            "VSC" => "ViewSonic",
            "IVM" => "iiyama",
            "GBT" => "Gigabyte",
            "XMI" => "Xiaomi",
            "HWV" => "Huawei",
            "EIZ" => "EIZO",
            "NEC" => "NEC",
            "APP" => "Apple",
            _ => null,
        };
    }

    /// <summary>"\\?\DISPLAY#GSM5B7F#5&amp;2c03a83e&amp;0&amp;UID4352#{…}" to "DISPLAY\GSM5B7F\5&amp;2c03a83e&amp;0&amp;UID4352".</summary>
    private static string MonitorPath(string interfacePath)
    {
        string[] parts = interfacePath.Split('#');
        return parts.Length >= 4 ? $"{parts[0].Replace(@"\\?\", string.Empty)}\\{parts[1]}\\{parts[2]}" : string.Empty;
    }

    private static DevMode NewDevMode() => new() { Size = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>() };

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
                                                 CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public int State;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Id;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Key;
    }

    /// <summary>DEVMODEW, display half. Only the size, resolution and refresh rate are used.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
                                                 CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DevMode
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, Width, Height, DisplayFlags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string device, int modeIndex, ref DevMode devMode);

    /// <summary>Game Mode, GPU scheduling, Memory integrity, Secure Boot, TPM - the "GAMING" group.</summary>
    public IReadOnlyList<Check> GamingChecks { get; private set; } = Array.Empty<Check>();

    /// <summary>
    /// Windows settings that change how games run, and the two that decide
    /// whether some games start at all. Each from the registry, or for the
    /// TPM from Windows' TPM service, which - unlike WMI's Win32_Tpm -
    /// answers without administrator.
    /// </summary>
    private void ReadGamingChecks()
    {
        var checks = new List<Check>();

        // Missing means never changed, and Game Mode is on by default.
        checks.Add(RegistryInt(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == 0
            ? new Check("Game Mode off - Windows gives games less priority over background work", Condition.Warning, "Game Mode off")
            : new Check("Game Mode on", Condition.Good, "Game Mode"));

        // 2 on, 1 off; missing is Windows' default, which depends on the card
        // and driver - so said as such rather than guessed.
        checks.Add(RegistryInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") switch
        {
            2 => new Check("Hardware-accelerated GPU scheduling on", Condition.Good, "GPU scheduling"),
            1 => new Check("Hardware-accelerated GPU scheduling off - DLSS and FSR frame generation need it", Condition.Warning, "GPU scheduling off"),
            _ => new Check("Hardware-accelerated GPU scheduling left at Windows' default", Condition.Unknown, "GPU scheduling: default"),
        });

        // Dim either way: it is a security trade-off, not a fault. Microsoft's
        // own advice for gaming PCs is that turning it off can help.
        checks.Add(RegistryInt(Registry.LocalMachine,
                               @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") == 1
            ? new Check("Memory integrity on - safer, but can cost a few % in some games (Windows Security > Device security)", Condition.Unknown, "Memory integrity on")
            : new Check("Memory integrity off", Condition.Unknown, "Memory integrity off"));

        // Missing means a legacy BIOS without Secure Boot at all.
        checks.Add(RegistryInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") == 1
            ? new Check("Secure Boot on", Condition.Good, "Secure Boot")
            : new Check("Secure Boot off - some games' anti-cheat won't start without it (Valorant, Battlefield 6, Call of Duty)", Condition.Warning, "Secure Boot off"));

        checks.Add(TpmVersion() switch
        {
            2 => new Check("TPM 2.0 on", Condition.Good, "TPM 2.0"),
            1 => new Check("TPM 1.2 - anti-cheat that needs a TPM wants 2.0", Condition.Warning, "TPM 1.2"),
            _ => new Check("No TPM found - turn on fTPM (AMD) or PTT (Intel) in the BIOS; some anti-cheat needs it", Condition.Warning, "No TPM"),
        });

        GamingChecks = checks;
    }

    private static int? RegistryInt(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>1 or 2 for TPM 1.2 or 2.0, 0 for none or unreadable.</summary>
    private static uint TpmVersion()
    {
        try
        {
            var info = new TbsDeviceInfo();
            return Tbsi_GetDeviceInfo((uint)System.Runtime.InteropServices.Marshal.SizeOf<TbsDeviceInfo>(), out info) == 0
                ? info.TpmVersion
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TbsDeviceInfo
    {
        public uint StructVersion, TpmVersion, TpmInterfaceType, TpmImpRevision;
    }

    [System.Runtime.InteropServices.DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TbsDeviceInfo info);

    /// <summary>Crashes and unexpected shutdowns in the last 30 days, under Windows.</summary>
    public Check? Stability { get; private set; }

    /// <summary>
    /// Kernel-Power 41 is Windows starting again without having shut down -
    /// a crash, a freeze that needed the power button, or a power cut.
    /// BugCheck 1001 is a blue screen. A blue screen also leaves a 41 behind,
    /// so they are counted as one line: "6 unexpected shutdowns, 2 of them
    /// blue screens".
    ///
    /// <paramref name="countedFrom"/> starts the count later than 30 days back
    /// - "start counting from now" on the line, for shutdowns that were known
    /// and harmless. Windows can't tell the power button held from a freeze
    /// (neither leaves a mark in the event), so the user has to say so.
    /// </summary>
    private void ReadStability(DateTime? countedFrom)
    {
        var month = TimeSpan.FromDays(30);
        var span = countedFrom is { } from && DateTime.Now - from < month ? DateTime.Now - from : month;
        bool sinceReset = span < month;

        // timediff is in milliseconds; never below one, which would match nothing at all.
        string window = $"TimeCreated[timediff(@SystemTime) <= {Math.Max(1, (long)span.TotalMilliseconds)}]";
        int shutdowns = CountEvents($"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {window}]]");
        int blueScreens = CountEvents($"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and {window}]]");

        string period = sinceReset ? $"since {countedFrom:yyyy-MM-dd HH:mm}" : "in the last 30 days";
        int total = Math.Max(shutdowns, blueScreens);

        Stability = blueScreens > 0
            ? new Check($"{total} unexpected shutdown{(total == 1 ? "" : "s")} {period}, " +
                        $"{blueScreens} of them blue screen{(blueScreens == 1 ? "" : "s")}", Condition.Bad,
                        $"{total} crash{(total == 1 ? "" : "es")}")
            : shutdowns > 0
            ? new Check($"{shutdowns} unexpected shutdown{(shutdowns == 1 ? "" : "s")} {period} - a crash, a freeze, the power button held, or a power cut. Right-click to start counting from now", Condition.Warning,
                        $"{shutdowns} shutdown{(shutdowns == 1 ? "" : "s")}")
            : new Check($"No crashes or unexpected shutdowns {period}", Condition.Good, "no crashes");
    }

    private static ulong ToUlong(object? value)
        => value is null ? 0 : ulong.TryParse(value.ToString(), NumberStyles.Integer,
                                              CultureInfo.InvariantCulture, out ulong result) ? result : 0;

    private static uint ToUint(object? value)
        => value is null ? 0 : uint.TryParse(value.ToString(), NumberStyles.Integer,
                                             CultureInfo.InvariantCulture, out uint result) ? result : 0;
}
