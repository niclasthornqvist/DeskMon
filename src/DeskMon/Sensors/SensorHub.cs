using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace DeskMon.Sensors;

/// <summary>
/// The single place that talks to hardware. Opened once at startup and polled
/// from a background thread; call <see cref="Read"/> to get a snapshot.
///
/// Two deliberate choices keep this cheap enough to leave running during a game:
///  - Only the CPU and GPU are enabled. Motherboard, storage, memory and PSU
///    polling are the expensive ones (SMBus and SMART reads that can stall for
///    milliseconds) and nothing here needs them.
///  - Sensors are looked up by name exactly once, at startup, and then held as
///    references. IHardware.Sensors hands out a fresh array on every call, so
///    searching by name each tick allocated a few dozen arrays per second for
///    no reason.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly NativeStats _native = new();
    private readonly MemoryUsers _memoryUsers = new();
    private readonly DiskStats _disk = new();
    private readonly NetworkStats _network = new();
    private readonly Computer _computer;
    private readonly List<IHardware> _polled = new();

    /// <summary>
    /// Held while the hardware is being touched. Reads run on a worker thread
    /// and Dispose runs on the UI thread as the app exits, so without this
    /// closing the library could pull the hardware out from under a read that
    /// was still in flight.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>The disk counters' own <see cref="_gate"/> - see Read.</summary>
    private readonly object _diskGate = new();
    private bool _diskClosed;

    /// <summary>How long hardware-library values are reused. See <see cref="RefreshHardware"/>.</summary>
    private readonly TimeSpan _hardwareEvery;

    private volatile HardwareValues _hardware = new();
    private DateTime _hardwareReadAt = DateTime.MinValue;

    // Resolved once in the constructor; null means "this machine doesn't report it".
    private ISensor? _cpuTemp;
    private ISensor? _cpuPower;
    private ISensor[] _cpuClocks = Array.Empty<ISensor>();
    private ISensor? _gpuTemp;
    private ISensor? _gpuLoad;
    private ISensor? _gpuMemoryLoad;
    private ISensor? _gpuPower;

    // Every temperature each device reports, for the expandable rows. Resolved
    // once with the rest, so unfolding a row costs nothing per tick.
    private ISensor[] _cpuTemps = Array.Empty<ISensor>();
    private ISensor[] _gpuTemps = Array.Empty<ISensor>();

    private bool _opened;

    /// <summary>Set when the hardware library failed to start, for the tooltip.</summary>
    public string? StartupError { get; private set; }

    /// <summary>Name of the CPU being measured, e.g. "AMD Ryzen 7 7800X3D".</summary>
    public string? CpuName { get; private set; }

    /// <summary>
    /// Name of the graphics card being measured. This is the card the
    /// temperature actually comes from, not just the first one Windows lists.
    /// </summary>
    public string? GpuName { get; private set; }

    /// <param name="preferredGpu">"auto", "nvidia", "amd" or "intel".</param>
    /// <param name="hardwareIntervalMs">
    /// How long to reuse the hardware library's values before asking it again.
    /// Zero or less asks every time, which is what this used to do - see
    /// <see cref="RefreshHardware"/> for what that costs.
    /// </param>
    /// <param name="allHardware">
    /// Only for --list-sensors: also enumerate motherboard, memory, storage,
    /// network, fan controllers and PSU so every available sensor shows up.
    /// Never used while the widget is running - those are the slow ones.
    /// </param>
    public SensorHub(string preferredGpu = "auto", int hardwareIntervalMs = 0, bool allHardware = false)
    {
        _hardwareEvery = hardwareIntervalMs > 0
            ? TimeSpan.FromMilliseconds(hardwareIntervalMs)
            : TimeSpan.Zero;

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = allHardware,
            IsMotherboardEnabled = allHardware,
            IsStorageEnabled = allHardware,
            IsNetworkEnabled = allHardware,
            IsControllerEnabled = allHardware,
            IsPsuEnabled = allHardware,
            IsBatteryEnabled = allHardware,
        };

        try
        {
            _computer.Open();
            _opened = true;
            ResolveSensors(preferredGpu);
        }
        catch (Exception ex)
        {
            // Almost always "driver could not be loaded", i.e. not elevated.
            // Load/RAM/swap still work, so carry on with temperatures as n/a.
            StartupError = ex.Message;
        }

        Prime();
    }

    private void ResolveSensors(string preferredGpu)
    {
        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpu = PickGpu(_computer.Hardware, preferredGpu);

        CpuName = cpu?.Name;
        GpuName = gpu?.Name;

        if (cpu is not null) _polled.Add(cpu);
        if (gpu is not null) _polled.Add(gpu);

        // A device's sensor list is only populated after its first update.
        foreach (var hardware in _polled)
            hardware.Update();

        if (cpu is not null)
        {
            // AMD reports "Core (Tctl/Tdie)", Intel reports "CPU Package"; the
            // final fallback is the hottest temperature the chip reports, so an
            // unknown CPU still shows something sensible.
            _cpuTemp = Find(cpu, SensorType.Temperature,
                            "Core (Tctl/Tdie)", "CPU Package", "Package",
                            "Core Average", "Core Max", "CPU Socket")
                       ?? Hottest(cpu);

            _cpuPower = Find(cpu, SensorType.Power, "Package", "CPU Package", "CPU Cores");

            _cpuClocks = Sensors(cpu)
                .Where(s => s.SensorType == SensorType.Clock
                            && s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            _cpuTemps = Temperatures(cpu);
        }

        if (gpu is not null)
        {
            _gpuTemp = Find(gpu, SensorType.Temperature, "GPU Core", "GPU Package", "Core", "GPU Hot Spot")
                       ?? Hottest(gpu);
            _gpuLoad = Find(gpu, SensorType.Load, "GPU Core", "D3D 3D");
            _gpuMemoryLoad = Find(gpu, SensorType.Load, "GPU Memory", "GPU Memory Used");
            _gpuPower = Find(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU PPT");

            _gpuTemps = Temperatures(gpu);
        }
    }

    /// <summary>Every temperature sensor a device exposes, in the library's order.</summary>
    private static ISensor[] Temperatures(IHardware hardware)
        => Sensors(hardware).Where(s => s.SensorType == SensorType.Temperature).ToArray();

    /// <summary>
    /// Takes the first CPU-time sample without reporting it. CPU load is a delta
    /// between two samples, so without a baseline the first reading - or the
    /// first one after a long pause - would be meaningless.
    /// </summary>
    public void Prime() => _native.ReadCpuLoad();

    /// <summary>
    /// Takes one reading. Runs on a worker thread - never call from the UI
    /// thread: asking the hardware library for a fresh reading takes tens of
    /// milliseconds, almost all of it inside the graphics driver.
    ///
    /// Load, memory and swap come from Win32 and are read every time, because
    /// they cost microseconds and are the numbers that actually move. The
    /// hardware library's values are reused between refreshes - see
    /// <see cref="RefreshHardware"/> for why that matters.
    /// </summary>
    public SensorSnapshot Read()
    {
        var snapshot = new SensorSnapshot();

        snapshot.CpuLoad = _native.ReadCpuLoad();
        _native.ReadMemory(snapshot);

        // Windows' own counters, like the two above - none of them touches the
        // hardware library. The lists refresh every few seconds on their own.
        _memoryUsers.Refresh();
        snapshot.TopRamUsers = _memoryUsers.ByRam;
        snapshot.TopCommitUsers = _memoryUsers.ByCommit;

        // Under a lock of its own, which Dispose takes too: closing the PDH
        // query while a read on the worker was inside PdhCollectQueryData
        // could pull it out from under the call. Not _gate - the hardware
        // refresh holds that for up to 75 ms, and the disk has no need to wait.
        lock (_diskGate)
        {
            if (!_diskClosed)
            {
                (snapshot.DiskRead, snapshot.DiskWrite) = _disk.Throughput();
                _disk.RefreshSpace();
                snapshot.DiskFullest = _disk.Fullest;
                snapshot.Drives = _disk.Drives;
            }
        }

        (snapshot.NetDown, snapshot.NetUp) = _network.Read();

        if (!_opened)
            return snapshot;

        if (DateTime.UtcNow - _hardwareReadAt >= _hardwareEvery)
            RefreshHardware();

        // Whatever the last refresh found, fresh or not.
        var hardware = _hardware;
        snapshot.CpuTemp = hardware.CpuTemp;
        snapshot.CpuPower = hardware.CpuPower;
        snapshot.CpuClock = hardware.CpuClock;
        snapshot.GpuTemp = hardware.GpuTemp;
        snapshot.GpuLoad = hardware.GpuLoad;
        snapshot.GpuMemoryLoad = hardware.GpuMemoryLoad;
        snapshot.GpuPower = hardware.GpuPower;
        snapshot.CpuTemperatures = hardware.CpuTemperatures;
        snapshot.GpuTemperatures = hardware.GpuTemperatures;

        return snapshot;
    }

    /// <summary>
    /// Asks the hardware library for new values. This is the expensive part of
    /// the program, and the reason it has an interval of its own.
    ///
    /// Measured on this machine: the CPU takes 2.4 ms, and the graphics card
    /// takes <em>75 ms</em> - about 21 ms of CPU with the rest spent blocked
    /// inside NVIDIA's driver, and the thread visits every core on the way,
    /// because per-core registers have to be read from the core that owns them.
    /// None of that is expensive in CPU terms, but talking to the display
    /// driver for 75 ms at a time, next to a game that is also talking to it,
    /// is the one thing here with any real chance of costing a frame.
    ///
    /// Temperatures move over seconds, so there is nothing to gain from asking
    /// every tick. Load and memory, which do move quickly, are not affected -
    /// they come from Win32 and are still read every time.
    /// </summary>
    private void RefreshHardware()
    {
        try
        {
            lock (_gate)
            {
                // Checked again inside the gate: the app may have closed the
                // library while this read was waiting for it.
                if (!_opened)
                    return;

                foreach (var device in _polled)
                    device.Update();

                _hardware = new HardwareValues
                {
                    CpuTemp = _cpuTemp?.Value,
                    CpuPower = _cpuPower?.Value,
                    CpuClock = HighestClock(),
                    GpuTemp = _gpuTemp?.Value,
                    GpuLoad = _gpuLoad?.Value,
                    GpuMemoryLoad = _gpuMemoryLoad?.Value,
                    GpuPower = _gpuPower?.Value,
                    CpuTemperatures = ReadAll(_cpuTemps),
                    GpuTemperatures = ReadAll(_gpuTemps),
                };
            }
        }
        catch (Exception ex)
        {
            StartupError ??= ex.Message;
        }
        finally
        {
            // Even on failure, so a broken sensor cannot turn into a retry
            // every tick.
            _hardwareReadAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// One set of readings from the hardware library, reused until the next
    /// refresh. Replaced wholesale rather than mutated, so a reader on another
    /// thread always sees one consistent set.
    /// </summary>
    private sealed class HardwareValues
    {
        public double? CpuTemp { get; init; }
        public double? CpuPower { get; init; }
        public double? CpuClock { get; init; }
        public double? GpuTemp { get; init; }
        public double? GpuLoad { get; init; }
        public double? GpuMemoryLoad { get; init; }
        public double? GpuPower { get; init; }
        public IReadOnlyList<SensorReading> CpuTemperatures { get; init; } = Array.Empty<SensorReading>();
        public IReadOnlyList<SensorReading> GpuTemperatures { get; init; } = Array.Empty<SensorReading>();
    }

    /// <summary>
    /// Name and value for a resolved sensor set. The sensors were already read
    /// by the Update above, so this only copies what is there - one small array
    /// per snapshot, and the names are the library's own strings rather than
    /// copies.
    /// </summary>
    private static SensorReading[] ReadAll(ISensor[] sensors)
    {
        if (sensors.Length == 0)
            return Array.Empty<SensorReading>();

        var readings = new SensorReading[sensors.Length];
        for (int i = 0; i < sensors.Length; i++)
            readings[i] = new SensorReading(sensors[i].Name, sensors[i].Value);

        return readings;
    }

    /// <summary>Fastest core right now - a plain average hides boost behaviour.</summary>
    private double? HighestClock()
    {
        double? best = null;
        foreach (var sensor in _cpuClocks)
            if (sensor.Value is float value && (best is null || value > best))
                best = value;
        return best;
    }

    /// <summary>
    /// Every sensor the library can see, as text. Run "DeskMon.exe --list-sensors"
    /// to dump this to sensors.txt - that's how you find the exact name to use
    /// when adding a metric that isn't in SensorSnapshot yet.
    /// </summary>
    public string DumpSensors()
    {
        var text = new StringBuilder();

        if (StartupError is not null)
            text.AppendLine($"!! sensor library error: {StartupError}").AppendLine();

        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();
            text.AppendLine($"[{hardware.HardwareType}] {hardware.Name}");

            foreach (var sensor in Sensors(hardware).OrderBy(s => s.SensorType).ThenBy(s => s.Name))
            {
                string value = sensor.Value.HasValue ? sensor.Value.Value.ToString("0.##") : "-";
                text.AppendLine($"    {sensor.SensorType,-12} {sensor.Name,-28} {value}");
            }
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// Discrete card first - on a desktop Ryzen with integrated graphics the
    /// iGPU shows up too, and its temperature isn't what anyone wants to see.
    /// </summary>
    private static IHardware? PickGpu(IEnumerable<IHardware> hardware, string preferred)
    {
        var gpus = hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia
                                                      or HardwareType.GpuAmd
                                                      or HardwareType.GpuIntel).ToList();
        if (gpus.Count == 0)
            return null;

        var wanted = preferred.ToLowerInvariant() switch
        {
            "nvidia" => HardwareType.GpuNvidia,
            "amd" => HardwareType.GpuAmd,
            "intel" => HardwareType.GpuIntel,
            _ => (HardwareType?)null,
        };

        if (wanted is not null)
            return gpus.FirstOrDefault(g => g.HardwareType == wanted) ?? gpus[0];

        return gpus.FirstOrDefault(g => g.HardwareType == HardwareType.GpuNvidia)
               ?? gpus.FirstOrDefault(g => g.HardwareType == HardwareType.GpuAmd)
               ?? gpus[0];
    }

    /// <summary>First sensor matching one of <paramref name="names"/>, in order.</summary>
    private static ISensor? Find(IHardware hardware, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var match = Sensors(hardware).FirstOrDefault(
                s => s.SensorType == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return null;
    }

    private static ISensor? Hottest(IHardware hardware)
        => Sensors(hardware)
            .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
            .OrderByDescending(s => s.Value!.Value)
            .FirstOrDefault();

    /// <summary>Sensors of the device and of its sub-devices, flattened.</summary>
    private static IEnumerable<ISensor> Sensors(IHardware hardware)
        => hardware.Sensors.Concat(hardware.SubHardware.SelectMany(sub => sub.Sensors));

    public void Dispose()
    {
        // After any read in progress - see Read.
        lock (_diskGate)
        {
            _diskClosed = true;
            _disk.Dispose();
        }

        // Waits for an in-flight read to finish first - at most a few
        // milliseconds, and it saves closing the library from underneath one.
        lock (_gate)
        {
            if (!_opened) return;
            try { _computer.Close(); } catch { /* shutting down anyway */ }
            _opened = false;
        }
    }
}
