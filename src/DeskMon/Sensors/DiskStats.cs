using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DeskMon.Sensors;

/// <summary>
/// The DISK section: how fast every drive together is reading and writing, and
/// how full each drive is.
///
/// Throughput from Windows' own performance counters (PDH), the figures Task
/// Manager shows - not the hardware library, whose storage polling reads SMART
/// data and is one of the slow parts SensorHub leaves switched off. Space from
/// GetDiskFreeSpaceEx through DriveInfo, every few seconds: a drive doesn't
/// fill up between two ticks.
/// </summary>
internal sealed class DiskStats : IDisposable
{
    private static readonly TimeSpan SpaceEvery = TimeSpan.FromSeconds(10);

    private IntPtr _query;
    private IntPtr _read;
    private IntPtr _write;
    private DateTime _spaceAt = DateTime.MinValue;

    public DiskStats()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
                return;

            // English names, so a Swedish or German Windows finds them too.
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _read);
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _write);

            // A rate needs two samples; this is the first.
            PdhCollectQueryData(_query);
        }
        catch
        {
            _query = IntPtr.Zero;
        }
    }

    /// <summary>Megabytes per second, read and written since the last call. Null when the counters aren't there.</summary>
    public (double? Read, double? Write) Throughput()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
            return (null, null);

        return (Rate(_read), Rate(_write));
    }

    private static double? Rate(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
            return null;

        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var value) == 0 && value.CStatus == 0
            ? value.DoubleValue / (1024 * 1024)
            : null;
    }

    /// <summary>How full the fullest drive is, in percent, and every drive's line for the list under it.</summary>
    public double? Fullest { get; private set; }

    public IReadOnlyList<SensorReading> Drives { get; private set; } = Array.Empty<SensorReading>();

    /// <summary>Reads the drives again if the last read is old enough.</summary>
    public void RefreshSpace()
    {
        if (DateTime.UtcNow - _spaceAt < SpaceEvery)
            return;
        _spaceAt = DateTime.UtcNow;

        try
        {
            // Fixed drives that are ready: not a card reader with no card,
            // not a DVD drive, not a network share that may take seconds to answer.
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                .OrderBy(d => d.Name)
                .ToList();

            Drives = drives
                .Select(d =>
                {
                    double used = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                    string name = d.Name.TrimEnd('\\');
                    return new SensorReading($"{name} {MemoryUsers.Size(d.TotalFreeSpace)} free", used);
                })
                .ToArray();

            Fullest = Drives.Count > 0 ? Drives.Max(d => d.Value) : null;
        }
        catch
        {
            // Keeps the last figures.
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
