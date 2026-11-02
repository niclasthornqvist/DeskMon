using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskMon.Sensors;

/// <summary>
/// A handle of its own on a game, kept for its whole session, for whether it
/// has gone and when.
///
/// Process can't do this. One from GetProcessById holds no handle until asked,
/// so by then the game is gone: ExitTime threw "Process was not started by this
/// object", and a game quit while the PC was locked logged the hours until
/// unlock as played. A handle held from the start also keeps the process id
/// from being reused, so a new program can't pass for the game.
/// </summary>
internal sealed class ProcessWatch : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WaitObject0 = 0;

    private readonly SafeProcessHandle? _handle;

    /// <summary>Its name, for telling it from a reused id when there is no handle.</summary>
    private readonly string? _name;

    public int Id { get; }

    private ProcessWatch(int pid, SafeProcessHandle? handle, string? name = null)
    {
        Id = pid;
        _handle = handle;
        _name = name;
    }

    /// <summary>
    /// The watch on <paramref name="pid"/>, or null when it is already gone.
    /// Only the limited rights: anything more is what anti-cheat refuses.
    /// </summary>
    public static ProcessWatch? Open(int pid)
    {
        var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, pid);
        if (!handle.IsInvalid)
            return new ProcessWatch(pid, handle);

        handle.Dispose();

        // Refused is not gone: anti-cheat can turn the handle down for a game
        // very much alive, and treating that as gone ended its session at once.
        return NameOf(pid) is { } name ? new ProcessWatch(pid, null, name) : null;
    }

    /// <summary>
    /// Whether it has gone. Without a handle, the id still running under the
    /// same name decides. A name, as Windows gives that without a handle: the
    /// id alone let any program given it later keep the session open. Only a
    /// new copy of the same program can still pass, in this already rare case.
    /// </summary>
    public bool HasExited
        => _handle is null ? NameOf(Id) != _name : WaitForSingleObject(_handle, 0) == WaitObject0;

    /// <summary>When it ended; null while it runs, or without a handle.</summary>
    public DateTime? ExitTime
    {
        get
        {
            if (_handle is null || !HasExited || !GetProcessTimes(_handle, out _, out long exit, out _, out _) || exit <= 0)
                return null;
            return DateTime.FromFileTime(exit);
        }
    }

    /// <summary>The name of what runs as <paramref name="pid"/>, or null when nothing does.</summary>
    private static string? NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _handle?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
}
