using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeskMon.Sensors;

/// <summary>
/// What else is running beside a game that could be costing it frames: a
/// program holding gigabytes of memory, or one busy on the CPU - Windows
/// Update installing, a Defender scan, a browser playing video. MainWindow
/// asks once per session, a little way in, and says it in one notification.
///
/// Two looks at every process three seconds apart - their CPU time and
/// memory - grouped by name, since a browser is dozens of processes and only
/// their total means anything. Once per session, on a worker thread: nothing
/// here runs while the game is being played beyond that one look.
/// </summary>
internal static class GameStartCheck
{
    private const long RamFlagBytes = 2L * 1024 * 1024 * 1024;   // 2 GB
    private const double CpuFlagPercent = 10;
    private const int MostShown = 3;
    private static readonly TimeSpan Between = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Windows' own background work under the names people know it by - its
    /// process names say nothing to most ("TiWorker"). Browsers by their brand.
    /// </summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TiWorker"] = "Windows Update",
        ["TrustedInstaller"] = "Windows Update",
        ["MoUsoCoreWorker"] = "Windows Update",
        ["wuauclt"] = "Windows Update",
        ["MsMpEng"] = "Defender scan",
        ["MpDefenderCoreService"] = "Defender scan",
        ["SearchIndexer"] = "Windows Search indexing",
        ["SearchProtocolHost"] = "Windows Search indexing",
        ["OneDrive"] = "OneDrive syncing",
        ["vmmem"] = "WSL / virtual machine",
        ["vmmemWSL"] = "WSL / virtual machine",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["opera"] = "Opera",
        ["brave"] = "Brave",
    };

    /// <summary>
    /// Part of Windows itself and always there - flagging them would only be
    /// noise. Memory Compression in particular holds gigabytes by design.
    /// </summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "smss", "csrss", "wininit",
        "services", "lsass", "dwm", "fontdrvhost", "DeskMon", "presentmon",
    };

    /// <summary>
    /// "Chrome 3.4 GB RAM", "Windows Update 14 % CPU" - at most three, the
    /// heaviest first. Empty when nothing stands out. Leaves out the game, and
    /// anything started from the game's own folder (its launcher, its crash
    /// handler), which is part of playing it. Blocks for three seconds: call
    /// it on a worker thread.
    /// </summary>
    public static IReadOnlyList<string> Run(int gamePid)
    {
        string? gameFolder = FolderOf(gamePid);

        var first = Sample(gamePid);
        Thread.Sleep(Between);
        var second = Sample(gamePid).Values;

        double cores = Environment.ProcessorCount;
        var flagged = new List<(string Text, double Weight)>();

        foreach (var group in second.GroupBy(p => Name(p.Name)))
        {
            long ram = group.Sum(p => p.Ram);

            // CPU over the three seconds, from processes seen both times; one
            // started in between has no first look to measure against.
            double cpuMs = 0;
            foreach (var process in group)
                if (first.TryGetValue(process.Pid, out var before) && before.Name == process.Name)
                    cpuMs += Math.Max(0, (process.Cpu - before.Cpu).TotalMilliseconds);
            double cpu = cpuMs / Between.TotalMilliseconds / cores * 100;

            bool heavy = ram >= RamFlagBytes, busy = cpu >= CpuFlagPercent;
            if (!heavy && !busy)
                continue;

            // Asked only of what stands out - a process's path is slow to get,
            // and asking all three hundred would make the look take seconds.
            if (gameFolder is not null && group.Any(p => FolderOf(p.Pid) is { } folder
                                                          && folder.StartsWith(gameFolder, StringComparison.OrdinalIgnoreCase)))
                continue;

            var parts = new List<string>();
            if (heavy)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{ram / (1024.0 * 1024 * 1024):0.0} GB RAM"));
            if (busy)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{cpu:0} % CPU"));

            // A busy CPU costs a game more than memory it isn't short of, so it
            // weighs more when choosing which three to say.
            flagged.Add(($"{group.Key} {string.Join(", ", parts)}", cpu * 2 + ram / (1024.0 * 1024 * 1024) * 5));
        }

        return flagged.OrderByDescending(f => f.Weight).Take(MostShown).Select(f => f.Text).ToList();
    }

    private sealed record Look(int Pid, string Name, long Ram, TimeSpan Cpu);

    /// <summary>
    /// Every process's memory and CPU time so far, by process id - from one
    /// NtQuerySystemInformation snapshot, the list Task Manager reads, rather
    /// than by opening each process. Opening them missed exactly the ones
    /// worth catching: Defender's engine is a protected process that refuses
    /// its CPU time even to an administrator, so a scan never showed. The
    /// snapshot has every process, and opens none of them.
    /// </summary>
    private static Dictionary<int, Look> Sample(int gamePid)
    {
        var looks = new Dictionary<int, Look>();

        int size = 1 << 20;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    // Processes started since the size was asked for: some room to spare.
                    size = Math.Max(size * 2, needed + (64 << 10));
                    continue;
                }
                if (status != 0)
                    return looks;

                for (IntPtr entry = buffer; ; )
                {
                    int next = Marshal.ReadInt32(entry, 0);
                    int pid = (int)Marshal.ReadIntPtr(entry, 80);
                    string name = ImageName(entry);

                    if (pid != 0 && pid != gamePid && name.Length > 0 && !Ignored.Contains(name))
                    {
                        // 100-ns units, user and kernel time together, as Process.TotalProcessorTime has it.
                        var cpu = TimeSpan.FromTicks(Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48));
                        long ram = (long)Marshal.ReadIntPtr(entry, 144);   // WorkingSetSize
                        looks[pid] = new Look(pid, name, ram, cpu);
                    }

                    if (next == 0)
                        break;
                    entry += next;
                }

                return looks;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return looks;
    }

    /// <summary>"chrome", as Process.ProcessName gives it - the snapshot has "chrome.exe".</summary>
    private static string ImageName(IntPtr entry)
    {
        int bytes = (ushort)Marshal.ReadInt16(entry, 56);   // UNICODE_STRING.Length
        IntPtr text = Marshal.ReadIntPtr(entry, 64);         // UNICODE_STRING.Buffer
        if (bytes == 0 || text == IntPtr.Zero)
            return string.Empty;

        string name = Marshal.PtrToStringUni(text, bytes / 2);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    // SYSTEM_PROCESS_INFORMATION, as laid out in a 64-bit process - which
    // DeskMon always is (win-x64 in the project). The offsets used above:
    // 0 NextEntryOffset, 40 UserTime, 48 KernelTime, 56 ImageName,
    // 80 UniqueProcessId, 144 WorkingSetSize.
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private static string Name(string processName)
        => Known.TryGetValue(processName, out string? known) ? known : processName;

    /// <summary>The folder a game was started from, so its own helpers can be told apart from the rest.</summary>
    private static string? FolderOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return FolderOf(process);
        }
        catch
        {
            return null;
        }
    }

    private static string? FolderOf(Process process)
    {
        try
        {
            string? path = process.MainModule?.FileName;
            return path is null ? null : Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;   // another architecture or protected - can't be the game's helper we'd recognise
        }
    }
}
