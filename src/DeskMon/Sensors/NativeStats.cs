using System;
using System.Runtime.InteropServices;

namespace DeskMon.Sensors;

/// <summary>
/// CPU load and memory figures straight from Win32. Deliberately not using
/// PerformanceCounter or WMI: both cost tens of milliseconds per read and spin
/// up extra threads, which is exactly the overhead this widget must not have
/// while a game is running. These two calls are a few microseconds each.
/// </summary>
internal sealed class NativeStats
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrev;

    /// <summary>
    /// Total CPU load since the previous call, as a percentage. Returns null on
    /// the very first call because a delta needs two samples.
    /// </summary>
    public double? ReadCpuLoad()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            return null;

        ulong idle = ToUInt64(idleFt);
        ulong kernel = ToUInt64(kernelFt);   // includes idle time
        ulong user = ToUInt64(userFt);

        if (!_hasPrev)
        {
            (_prevIdle, _prevKernel, _prevUser, _hasPrev) = (idle, kernel, user, true);
            return null;
        }

        double idleDelta = idle - _prevIdle;
        double totalDelta = (kernel - _prevKernel) + (user - _prevUser);

        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);

        if (totalDelta <= 0)
            return null;

        return Math.Clamp((1.0 - idleDelta / totalDelta) * 100.0, 0, 100);
    }

    /// <summary>
    /// Fills in the RAM and pagefile fields of <paramref name="snapshot"/>.
    /// </summary>
    public void ReadMemory(SensorSnapshot snapshot)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return;

        snapshot.RamLoad = status.dwMemoryLoad;
        snapshot.RamTotalGb = status.ullTotalPhys / BytesPerGb;
        snapshot.RamUsedGb = (status.ullTotalPhys - status.ullAvailPhys) / BytesPerGb;

        // What the page files really hold, from the kernel's own count. It
        // used to be worked out as commit charge minus RAM in use (psutil's
        // way), but commit counts memory programs have reserved and never
        // touched: a game reserving gigabytes read as a full page file - 100 %
        // through whole Factorio sessions on a 6.5 GB one that held 0 MB.
        if (ReadPageFiles() is not (double total, double used))
            return;

        snapshot.SwapTotalGb = total / BytesPerGb;
        snapshot.SwapUsedGb = used / BytesPerGb;
        snapshot.SwapLoad = total > 0 ? used / total * 100.0 : 0;
    }

    private const int SystemPageFileInformation = 18;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // Kept between reads: a tick shouldn't allocate. Grown if the list of
    // page files ever outgrows it - one entry per drive, so it rarely will.
    private IntPtr _pageFileBuffer = Marshal.AllocHGlobal(1024);
    private int _pageFileBufferSize = 1024;

    /// <summary>
    /// Size and use of all page files together, in bytes; (0, 0) with none.
    /// Null if Windows wouldn't say. Same cheap kernel call as the rest of
    /// this class - no WMI or performance counters (see the class summary).
    /// </summary>
    private (double Total, double Used)? ReadPageFiles()
    {
        int status, length;
        while ((status = NtQuerySystemInformation(SystemPageFileInformation, _pageFileBuffer, _pageFileBufferSize, out length))
               == StatusInfoLengthMismatch && _pageFileBufferSize < 1 << 16)
        {
            Marshal.FreeHGlobal(_pageFileBuffer);
            _pageFileBufferSize *= 4;
            _pageFileBuffer = Marshal.AllocHGlobal(_pageFileBufferSize);
        }
        if (status != 0)
            return null;

        // SYSTEM_PAGEFILE_INFORMATION entries, chained by their first field:
        // NextEntryOffset, TotalSize, TotalInUse, PeakUsage (all ULONG, in
        // pages), then the file's name. Walked only as far as Windows wrote
        // this time - nothing at all with no page file - so a longer list left
        // in the buffer by an earlier read is never counted.
        double pageSize = Environment.SystemPageSize;
        double total = 0, used = 0;
        int offset = 0;
        while (offset + 16 <= Math.Min(length, _pageFileBufferSize))
        {
            int next = Marshal.ReadInt32(_pageFileBuffer, offset);
            total += (uint)Marshal.ReadInt32(_pageFileBuffer, offset + 4) * pageSize;
            used += (uint)Marshal.ReadInt32(_pageFileBuffer, offset + 8) * pageSize;
            if (next <= 0)
                break;
            offset += next;
        }
        return (total, used);
    }

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
}
