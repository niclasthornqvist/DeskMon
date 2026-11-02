using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DeskMon.Sensors;

/// <summary>
/// What is busiest beside the game right now - the heaviest program on the CPU
/// and on the disk - so a dip in a session can be laid next to what else was
/// going on: a Defender scan, Windows Update, a sync. GameStartCheck looks once
/// per session; a dip can come at any time, so this keeps looking.
///
/// Every ten seconds, the length of a timeline point, on a timer of its own
/// like PingMonitor: never on the UI thread, and a slow look can't hold up the
/// readings. Only while the session's game is running. One system-wide
/// snapshot that opens no process, so a look costs next to nothing beside the
/// game it is watching.
/// </summary>
internal sealed class BusyWatch : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    // Below these nothing is said: every PC has something ticking over, and a
    // name beside every dip would blame programs that took nothing from it.
    private const double CpuFlagPercent = 5;
    private const double DiskFlagMbPerSecond = 20;

    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile int _pid;
    private volatile bool _fresh = true;
    private volatile string? _latest;
    private int _looking;

    // Only the timer's own look touches these, one at a time - see _looking.
    private Dictionary<int, GameStartCheck.Look>? _before;
    private TimeSpan _beforeAt;
    private string? _gameFolder;
    private readonly Dictionary<string, bool> _gameHelpers = new();

    public BusyWatch() => _timer = new Timer(_ => Look(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>"Defender scan 35% CPU + 120 MB/s disk"; null when nothing stands out, or before a second look.</summary>
    public string? Latest => _latest;

    /// <summary>Starts watching beside the game <paramref name="pid"/>, or stops with 0. Cheap to say every tick.</summary>
    public void Run(int pid)
    {
        if (pid == _pid)
            return;

        _pid = pid;
        _latest = null;

        // A rate from a look before a gap would be spread over the whole gap.
        _fresh = true;
        _timer.Change(pid == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.Zero,
                      pid == 0 ? Timeout.InfiniteTimeSpan : Every);
    }

    private void Look()
    {
        int pid = _pid;
        if (pid == 0 || Interlocked.Exchange(ref _looking, 1) == 1)
            return;

        try
        {
            if (_fresh)
            {
                _fresh = false;
                _before = null;
                _gameHelpers.Clear();
                _gameFolder = GameStartCheck.FolderOf(pid);
            }

            var now = GameStartCheck.Sample(pid);
            var at = _clock.Elapsed;
            if (_before is { } before && (at - _beforeAt).TotalSeconds is var seconds and > 0)
                _latest = Describe(now.Values, before, seconds);

            _before = now;
            _beforeAt = at;
        }
        catch (Exception ex)
        {
            // On a timer thread: let out, this would take the program down.
            App.Log("looking at what is busy beside the game", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _looking, 0);
        }
    }

    private sealed record Usage(string Name, double CpuPercent, double DiskMbPerSecond, List<GameStartCheck.Look> Processes);

    private string? Describe(IEnumerable<GameStartCheck.Look> now, Dictionary<int, GameStartCheck.Look> before, double seconds)
    {
        double cores = Environment.ProcessorCount;
        var usage = new List<Usage>();

        // By the name people know a program by, as a browser is dozens of
        // processes and only their total means anything.
        foreach (var group in now.GroupBy(p => GameStartCheck.Name(p.Name)))
        {
            double cpuMs = 0, ioBytes = 0;
            foreach (var process in group)
            {
                // Seen both times, or there is nothing to measure against. The
                // name too, as Windows reuses process ids.
                if (before.TryGetValue(process.Pid, out var earlier) && earlier.Name == process.Name)
                {
                    cpuMs += Math.Max(0, (process.Cpu - earlier.Cpu).TotalMilliseconds);
                    ioBytes += Math.Max(0, process.Io - earlier.Io);
                }
            }

            usage.Add(new Usage(group.Key, cpuMs / (seconds * 1000) / cores * 100,
                                ioBytes / (1024 * 1024) / seconds, group.ToList()));
        }

        var cpu = usage.Where(u => u.CpuPercent >= CpuFlagPercent)
                       .OrderByDescending(u => u.CpuPercent)
                       .FirstOrDefault(u => !IsGameHelper(u));
        var disk = usage.Where(u => u.DiskMbPerSecond >= DiskFlagMbPerSecond)
                        .OrderByDescending(u => u.DiskMbPerSecond)
                        .FirstOrDefault(u => !IsGameHelper(u));

        string? cpuText = cpu is null ? null : string.Create(CultureInfo.InvariantCulture, $"{cpu.CpuPercent:0}% CPU");
        string? diskText = disk is null ? null : string.Create(CultureInfo.InvariantCulture, $"{disk.DiskMbPerSecond:0} MB/s disk");

        return (cpu, disk) switch
        {
            (null, null) => null,
            (not null, null) => $"{Clean(cpu.Name)} {cpuText}",
            (null, not null) => $"{Clean(disk.Name)} {diskText}",
            _ when cpu!.Name == disk!.Name => $"{Clean(cpu.Name)} {cpuText} + {diskText}",
            _ => $"{Clean(cpu!.Name)} {cpuText} + {Clean(disk!.Name)} {diskText}",
        };
    }

    /// <summary>
    /// Started from the game's own folder - its launcher, its crash handler -
    /// so part of playing it. Asked only of what stands out, and once per
    /// name: a process's path is slow to get.
    /// </summary>
    private bool IsGameHelper(Usage usage)
    {
        if (_gameFolder is not { } folder)
            return false;

        if (!_gameHelpers.TryGetValue(usage.Name, out bool helper))
        {
            helper = usage.Processes.Any(p => GameStartCheck.FolderOf(p.Pid) is { } path
                                              && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
            _gameHelpers[usage.Name] = helper;
        }
        return helper;
    }

    /// <summary>A name that can't break the two plain-text files this ends up in: the timeline splits on commas, the log on " · ".</summary>
    private static string Clean(string name) => name.Replace(',', ' ').Replace('·', ' ');

    public void Dispose() => _timer.Dispose();
}
