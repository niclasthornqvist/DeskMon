using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using Microsoft.Win32;

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

    public string? OperatingSystem { get; private set; }

    /// <summary>When Windows last started, rather than an uptime: the window is
    /// filled once and then only hidden, so a duration would go stale while a
    /// point in time stays true.</summary>
    public string? UpSince { get; private set; }

    public IReadOnlyList<string> Drives { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<NetworkAdapter> NetworkAdapters { get; private set; } = Array.Empty<NetworkAdapter>();

    /// <summary>One physical adapter. Disconnected ones are listed too, greyed
    /// out, since "this machine has Wi-Fi" is worth knowing even when unused.</summary>
    public sealed record NetworkAdapter(string Description, bool Connected);

    /// <summary>
    /// Collects everything. Call from a background thread.
    /// <paramref name="cpuName"/> and <paramref name="gpuName"/> come from the
    /// sensor library, which already knows them and names the exact card the
    /// temperatures are read from.
    /// </summary>
    public static SystemInfo Collect(string? cpuName, string? gpuName)
    {
        var info = new SystemInfo
        {
            Cpu = Clean(cpuName) ?? ReadCpuFromRegistry(),
            Gpu = Clean(gpuName) ?? ReadPrimaryGpuFromWmi(),
        };

        info.ReadBoardAndBios();
        info.ReadMemory();
        info.ReadCpuDetail();
        info.ReadGpuDetail();
        info.ReadOperatingSystem();
        info.ReadDrives();
        info.ReadNetwork();
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
                parts.Add($"{mhz / 1000d:0.##} GHz base");   // 4201 MHz reads 4.2, not 4.201
            if (l3Kb > 0)
                parts.Add($"{l3Kb / 1024d:0.#} MB L3");

            CpuDetail = parts.Count > 0 ? string.Join("  ·  ", parts) : null;
        }
        catch
        {
            // No detail line; the CPU name above it is still there.
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
            parts.Add(gb >= 1 ? $"{gb:0.#} GB VRAM" : $"{bytes / (1024 * 1024)} MB VRAM");
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
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");
            using var results = searcher.Get();   // see ReadMemory

            var drives = new List<(int Order, string Text)>();
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
                        drives.Add((int.TryParse(disk["DeviceId"] as string, out int id) ? id : int.MaxValue,
                                    string.Join("  ·  ", parts)));
                }
            }

            // Disk 0 first, the way Disk Management lists them.
            drives.Sort((a, b) => a.Order.CompareTo(b.Order));
            Drives = drives.ConvertAll(d => d.Text);
        }
        catch
        {
            // No storage rows.
        }
    }

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
            ? $"{bytes / 1e12:0.#} TB"
            : $"{bytes / 1e9:0} GB";

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
                "SELECT InterfaceDescription, MediaConnectState, Speed FROM MSFT_NetAdapter " +
                "WHERE HardwareInterface = TRUE AND Virtual = FALSE");
            using var results = searcher.Get();   // see ReadMemory

            var adapters = new List<NetworkAdapter>();
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
                }
            }

            // Connected first: that is the one in use.
            adapters.Sort((a, b) => b.Connected.CompareTo(a.Connected));
            NetworkAdapters = adapters;
        }
        catch
        {
            // No network rows.
        }
    }

    private static string LinkSpeed(ulong bitsPerSecond)
        => bitsPerSecond >= 1_000_000_000
            ? $"{bitsPerSecond / 1e9:0.#} Gbit/s"
            : $"{bitsPerSecond / 1e6:0} Mbit/s";

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

            string? biosVendor = Clean(key.GetValue("BIOSVendor") as string);
            string? biosVersion = Clean(key.GetValue("BIOSVersion") as string);
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

            var modules = new List<string>();
            ulong totalBytes = 0;

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

                    var parts = new List<string>();
                    string? name = Join(vendor, part);
                    if (name is not null) parts.Add(name);
                    if (capacity > 0) parts.Add($"{capacity / (1024d * 1024 * 1024):0.#} GB");
                    if (speed > 0) parts.Add($"{speed} MT/s");
                    if (slot is not null) parts.Add(slot);

                    if (parts.Count > 0)
                        modules.Add(string.Join("  ·  ", parts));
                }
            }

            MemoryModules = modules;
            if (totalBytes > 0)
                MemoryTotal = $"{totalBytes / (1024d * 1024 * 1024):0.#} GB in {modules.Count} " +
                              (modules.Count == 1 ? "module" : "modules");
        }
        catch
        {
            // WMI disabled or the class is unavailable - the rows just read "unknown".
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

    private static ulong ToUlong(object? value)
        => value is null ? 0 : ulong.TryParse(value.ToString(), NumberStyles.Integer,
                                              CultureInfo.InvariantCulture, out ulong result) ? result : 0;

    private static uint ToUint(object? value)
        => value is null ? 0 : uint.TryParse(value.ToString(), NumberStyles.Integer,
                                             CultureInfo.InvariantCulture, out uint result) ? result : 0;
}
