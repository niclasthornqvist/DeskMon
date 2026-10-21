using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

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

    private DateTime _readAt = DateTime.MinValue;

    /// <summary>By what they hold in RAM right now - the working set.</summary>
    public IReadOnlyList<SensorReading> ByRam { get; private set; } = Array.Empty<SensorReading>();

    /// <summary>
    /// By what they have been given in all - private bytes, the commit charge -
    /// which is what fills the page file once RAM runs out.
    /// </summary>
    public IReadOnlyList<SensorReading> ByCommit { get; private set; } = Array.Empty<SensorReading>();

    /// <summary>Reads again if the last read is old enough. On the sensor worker thread.</summary>
    public void Refresh()
    {
        if (DateTime.UtcNow - _readAt < Every)
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
                                           by(p) / (1024.0 * 1024 * 1024), Size(by(p))))
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

    /// <summary>A process can exit between the snapshot and the read.</summary>
    private static long Safe(Func<long> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
