using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace DeskMon.Sensors;

/// <summary>
/// The programs using the most memory, for the lists under RAM and SWAP - so a
/// red RAM row says what to close, not only that something should be.
///
/// Grouped by program name: a browser is thirty processes, and thirty lines of
/// "chrome" would push the rest off the list while none of them alone looked big.
///
/// Read from the one system-wide process snapshot Process.GetProcesses takes,
/// which already carries every process's memory figures - no process is opened,
/// so it works for elevated and protected ones too, and costs a few milliseconds.
/// Even so only every few seconds: memory use moves slowly, and the list is
/// there to be read, not watched.
/// </summary>
internal sealed class MemoryUsers
{
    /// <summary>How many programs each list shows.</summary>
    public const int Shown = 5;

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    /// <summary>
    /// While neither list is unfolded. Not never: the widget's width is
    /// measured with the lists filled, and a list opened later should not
    /// start from something half a day old - but listing every process every
    /// five seconds, over a game, for lists nobody can see was waste.
    /// </summary>
    private static readonly TimeSpan EveryWhileFolded = TimeSpan.FromSeconds(30);

    private DateTime _readAt = DateTime.MinValue;

    /// <summary>By what they hold in RAM right now - the working set.</summary>
    public IReadOnlyList<SensorReading> ByRam { get; private set; } = Array.Empty<SensorReading>();

    /// <summary>
    /// By what they have been given in all - private bytes, the commit charge -
    /// which is what fills the page file once RAM runs out.
    /// </summary>
    public IReadOnlyList<SensorReading> ByCommit { get; private set; } = Array.Empty<SensorReading>();

    /// <summary>Reads again if the last read is old enough. On the sensor worker thread.</summary>
    /// <param name="shown">Whether the RAM or SWAP list is unfolded - see <see cref="EveryWhileFolded"/>.</param>
    public void Refresh(bool shown)
    {
        if (DateTime.UtcNow - _readAt < (shown ? Every : EveryWhileFolded))
            return;
        _readAt = DateTime.UtcNow;

        var processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcesses();

            // Idle and System report figures that aren't a program's.
            var programs = processes
                .Where(p => p.Id > 4)
                .Select(p => (Name: p.ProcessName, Ram: Safe(() => p.WorkingSet64), Commit: Safe(() => p.PrivateMemorySize64)))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Count: g.Count(), Ram: g.Sum(p => p.Ram), Commit: g.Sum(p => p.Commit)))
                .ToList();

            ByRam = Top(programs, p => p.Ram);
            ByCommit = Top(programs, p => p.Commit);
        }
        catch
        {
            // Keeps the last list; the next refresh tries again.
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static IReadOnlyList<SensorReading> Top(List<(string Name, int Count, long Ram, long Commit)> programs,
                                                    Func<(string Name, int Count, long Ram, long Commit), long> by)
        => programs
            .OrderByDescending(by)
            .Take(Shown)
            .Select(p => new SensorReading(Short(p.Name) + (p.Count > 1 ? $" ×{p.Count}" : string.Empty),
                                           by(p) / (1024.0 * 1024 * 1024), Size(by(p)), Key: p.Name))
            .ToArray();

    /// <summary>
    /// Long program names cut short here, not only by the panel: the widget's
    /// width is measured once, with whatever happened to be in this list at
    /// the time, and a "MicrosoftEdgeWebView2Runtime" there would widen it for
    /// good. The full name of a cut one is rarely needed to know what it is.
    /// </summary>
    private static string Short(string name) => name.Length <= 18 ? name : name[..17] + "…";

    /// <summary>"1.4 GB", or "640 MB" under a gigabyte.</summary>
    internal static string Size(long bytes)
        => bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1 << 20)).ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Why the program can't be ended from the list, or null if it can - and
    /// how many processes it is now. On the UI thread, as the menu opens.
    ///
    /// The point of the check: DeskMon runs as administrator, so ending a
    /// process Windows marks as critical (csrss, wininit, smss…) would succeed
    /// and blue-screen the PC. Every process of the name is asked, and one that
    /// can't be asked counts as critical - guessing wrong costs a crash.
    /// </summary>
    internal static string? WhyNotEnd(string name, out int count)
    {
        var processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcessesByName(name);
            count = processes.Length;

            if (count == 0)
                return "It has already closed.";

            int self = Environment.ProcessId;
            foreach (var process in processes)
            {
                // Idle and System are never a program, and IsProcessCritical
                // can't be asked about them.
                if (process.Id <= 4)
                    return "It is part of Windows.";
                if (process.Id == self)
                    return "That's DeskMon itself - use Exit instead.";
                if (IsCritical(process.Id) is not false)
                    return "Windows needs it to keep running.";
            }

            return null;
        }
        catch
        {
            count = 0;
            return "Couldn't check whether Windows needs it.";
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// Ends every process of the program, checking again first - the list can
    /// have been open a while, and a critical process could have taken the
    /// name since. Returns what went wrong, or null if all of them ended.
    /// </summary>
    internal static string? End(string name)
    {
        if (WhyNotEnd(name, out _) is { } reason)
            return reason;

        var processes = Array.Empty<Process>();
        int failed = 0;
        string? firstError = null;
        try
        {
            processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                try
                {
                    // Each on its own: a browser's child that has already gone
                    // with its parent shouldn't stop the rest being ended.
                    process.Kill(entireProcessTree: false);
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    // InvalidOperationException is "already exited", which is the goal.
                    failed++;
                    firstError ??= ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        return failed == 0 ? null : $"{failed} of {processes.Length} wouldn't end: {firstError}";
    }

    /// <summary>True, false, or null when the process couldn't be opened or asked.</summary>
    private static bool? IsCritical(int processId)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            return IsProcessCritical(handle, out bool critical) ? critical : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>A process can exit between the snapshot and the read.</summary>
    private static long Safe(Func<long> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
