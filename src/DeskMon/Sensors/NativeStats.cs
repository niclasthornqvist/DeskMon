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

        // Windows has no "swap used" counter. ullTotalPageFile / ullAvailPageFile
        // are the system *commit* limit and headroom, so the part of the commit
        // charge that doesn't fit in physical RAM is what actually lives in the
        // pagefile. Same derivation psutil uses for swap on Windows.
        double physUsed = status.ullTotalPhys - status.ullAvailPhys;
        double commitUsed = status.ullTotalPageFile - status.ullAvailPageFile;
        double swapTotal = Math.Max(0, (double)status.ullTotalPageFile - status.ullTotalPhys);
        double swapUsed = Math.Clamp(commitUsed - physUsed, 0, swapTotal);

        snapshot.SwapTotalGb = swapTotal / BytesPerGb;
        snapshot.SwapUsedGb = swapUsed / BytesPerGb;
        snapshot.SwapLoad = swapTotal > 0 ? swapUsed / swapTotal * 100.0 : 0;
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
}
