using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DeskMon.Sensors;

/// <summary>
/// The DISK section: how fast every drive together is reading and writing, and
/// how full each drive is.
///
/// Throughput from Windows' own performance counters (PDH), the figures Task
/// Manager shows - not the hardware library, whose storage polling reads SMART
/// data and is one of the slow parts SensorHub leaves switched off. Space from
/// GetDiskFreeSpaceEx through DriveInfo, once a minute and on a thread of its
/// own: a drive doesn't fill up between two ticks, and asking a sleeping hard
/// drive spins it up, which can take seconds.
/// </summary>
internal sealed class DiskStats : IDisposable
{
    // A minute, not 10 s: every check of a sleeping hard drive wakes it, so a
    // short interval kept a second HDD from ever spinning down.
    private static readonly TimeSpan SpaceEvery = TimeSpan.FromSeconds(60);

    private IntPtr _query;
    private IntPtr _read;
    private IntPtr _write;
    private DateTime _spaceAt = DateTime.MinValue;

    // 1 while a space read runs, so a drive slow to wake never has two
    // threads waiting on it.
    private int _spaceBusy;

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

    /// <summary>How full the fullest drive is, in percent.</summary>
    public double? Fullest => _space.Fullest;

    /// <summary>Every drive's line for the list under the DISK row.</summary>
    public IReadOnlyList<SensorReading> Drives => _space.Drives;

    /// <summary>
    /// The drives and the fullest of them, worked out together once a read.
    /// One record behind one reference, swapped whole from the reading
    /// thread: the two can't be seen out of step, and the fullest is no
    /// longer worked out again with LINQ on every tick for a figure that
    /// changes once a minute.
    /// </summary>
    private sealed record Space(IReadOnlyList<SensorReading> Drives, double? Fullest);

    private volatile Space _space = new(Array.Empty<SensorReading>(), null);

    /// <summary>
    /// Reads the drives again if the last read is old enough. Only the first
    /// read waits, since the widget's rows are built from it; after that the
    /// read runs on its own thread and the figures follow a moment later, so
    /// a hard drive waking up never holds the sensor read - and with it the
    /// whole widget - for the seconds it takes.
    /// </summary>
    public void RefreshSpace()
    {
        if (DateTime.UtcNow - _spaceAt < SpaceEvery)
            return;
        if (Interlocked.CompareExchange(ref _spaceBusy, 1, 0) != 0)
            return;

        bool first = _spaceAt == DateTime.MinValue;
        _spaceAt = DateTime.UtcNow;

        if (first)
            ReadSpace();
        else
            // A thread of its own, not the pool: a slow drive could sit on a
            // pool thread for seconds, and the sensor reads use the pool too.
            Task.Factory.StartNew(ReadSpace, TaskCreationOptions.LongRunning);
    }

    private void ReadSpace()
    {
        try
        {
            // Fixed drives that are ready: not a card reader with no card,
            // not a DVD drive, not a network share that may take seconds to answer.
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                .OrderBy(d => d.Name)
                .ToList();

            var readings = drives
                .Select(d =>
                {
                    double used = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                    string name = d.Name.TrimEnd('\\');
                    return new SensorReading($"{name} {MemoryUsers.Size(d.TotalFreeSpace)} free", used);
                })
                .ToArray();
            _space = new Space(readings, readings.Length > 0 ? readings.Max(d => d.Value) : null);
        }
        catch
        {
            // Keeps the last figures.
        }
        finally
        {
            Volatile.Write(ref _spaceBusy, 0);
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
