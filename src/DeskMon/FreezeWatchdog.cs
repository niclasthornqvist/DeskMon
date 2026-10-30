using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace DeskMon;

/// <summary>
/// Notices when the UI thread stops answering and leaves a memory dump of
/// DeskMon behind, so a freeze can be traced afterwards.
///
/// A freeze throws nothing, so error.log stayed empty and the crash restart
/// never ran: all that was left was Windows' "stopped responding" line, which
/// says nothing about where it was stuck. .NET can't read another thread's
/// stack from inside the process, so a dump - read later with dotnet-dump or
/// Visual Studio - is the one way to see it.
///
/// It only records. Restarting a frozen copy risks two widgets, two tray icons
/// and a session recovered twice - the trouble the crash restart already had.
/// </summary>
internal static class FreezeWatchdog
{
    /// <summary>
    /// How long the UI thread may not answer before it counts as frozen. Well
    /// above anything legitimate - a settings re-layout, a big picture
    /// loading - yet before Windows' own hang prompt makes you close it.
    /// </summary>
    private static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A gap this long between two of the watchdog's own checks means the PC
    /// was asleep, not that the widget froze: the tick count keeps running
    /// through sleep, so without this every wake-up would look like a freeze.
    /// </summary>
    private static readonly TimeSpan SleptAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Room left on the disk before a dump is written: a full dump can be a few
    /// hundred MB, and a full disk would break the notes and sessions saves.
    /// </summary>
    private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Dumps per run. More than one, as a second freeze can have another
    /// cause than the first; few, as a long freeze must not fill the disk.
    /// </summary>
    private const int MaxDumps = 3;

    /// <summary>
    /// Beside the cache, not with settings.json: a dump is large and belongs in
    /// Local, not Roaming. Numbered within a run and overwritten by the next
    /// run, so at most <see cref="MaxDumps"/> files are ever kept.
    /// </summary>
    private static string DumpPath(int number)
        => Path.Combine(Path.GetDirectoryName(DataFolder.Cache) ?? DataFolder.Root,
                        number == 1 ? "freeze.dmp" : $"freeze-{number}.dmp");

    /// <summary>When the UI thread last ran a ping, in Environment.TickCount64.</summary>
    private static long _answeredAt;

    /// <summary>A ping is queued and not yet run. Volatile: set on the UI thread, read on the watchdog's.</summary>
    private static volatile bool _pending;

    /// <summary>
    /// Starts watching the given UI thread. A background thread rather than a
    /// timer: a timer callback goes through the thread pool, which a busy
    /// sensor read can hold up, and would then report freezes that weren't.
    /// </summary>
    public static void Start(Dispatcher ui)
    {
        Interlocked.Exchange(ref _answeredAt, Environment.TickCount64);

        // dbghelp loaded now, while all is well, rather than at the first
        // freeze: loading a DLL takes the loader lock, and a UI thread stuck
        // while holding it would leave the dump waiting behind it.
        try { NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dbghelp.dll")); }
        catch { /* the dump says so if it can't */ }

        var thread = new Thread(() => Watch(ui))
        {
            IsBackground = true,   // never keeps DeskMon alive on the way out
            Name = "DeskMon freeze watchdog",
        };
        thread.Start();
    }

    private static void Watch(Dispatcher ui)
    {
        long lastCheck = Environment.TickCount64;
        long frozenSince = 0;
        bool frozen = false;
        int dumps = 0;

        while (!ui.HasShutdownFinished)
        {
            Thread.Sleep(PingEvery);

            long now = Environment.TickCount64;
            if (now - lastCheck > SleptAfter.TotalMilliseconds)
                Interlocked.Exchange(ref _answeredAt, now);   // back from sleep - start the clock again
            lastCheck = now;

            long silent = now - Interlocked.Read(ref _answeredAt);

            // Only one ping out at a time: a frozen thread would otherwise
            // collect one per second and run them all on waking.
            if (!_pending)
            {
                _pending = true;
                ui.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    Interlocked.Exchange(ref _answeredAt, Environment.TickCount64);
                    _pending = false;
                }));
            }

            if (silent < FrozenAfter.TotalMilliseconds)
            {
                // Says how long it lasted, so a dump can be told apart from a
                // freeze that cleared by itself and one that never did. From
                // the last answer before it to the first after: counting from
                // when it was noticed, plus the threshold, also counted the
                // time the dump took and came out a guess.
                if (frozen)
                    App.Log("freeze", $"The widget answered again after {(Interlocked.Read(ref _answeredAt) - frozenSince) / 1000} s.");
                frozen = false;
                continue;
            }

            // Stopped at a breakpoint is not a freeze.
            if (frozen || Debugger.IsAttached)
                continue;

            frozen = true;
            frozenSince = now - silent;   // the last answer

            // Before the dump, and on a line of its own: a freeze that stops
            // the whole process also stops the dump from being taken until it
            // is over - and by then whatever was stuck has returned. This
            // reads only DeskMon's own list, so it is written at once.
            App.Log("freeze", $"The widget stopped answering. {Activity.Describe()} {Memory()}");

            if (dumps >= MaxDumps)
            {
                App.Log("freeze", $"The widget hasn't answered for {silent / 1000} s. No dump - {MaxDumps} were already written this run.");
                continue;
            }

            dumps++;
            App.Log("freeze", $"The widget hasn't answered for {silent / 1000} s. {WriteDump(dumps)}");
        }
    }

    /// <summary>
    /// "RAM free 15.2 GB; DeskMon 168 MB in memory." The freeze during an
    /// antivirus scan left the UI thread idle in Windows, with no way to tell
    /// from the dump whether DeskMon had been paged out. A working set far
    /// under its usual size, or RAM nearly gone, says it was.
    /// </summary>
    private static string Memory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            string ram = GlobalMemoryStatusEx(ref status)
                ? (status.AvailPhys / (1024d * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
                : "unknown";
            return $"RAM free {ram}; DeskMon {Environment.WorkingSet / (1024 * 1024)} MB in memory.";
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// Writes the dump and says how it went, for the log line. Into a temporary
    /// file first, so a dump cut short - the process killed half-way - never
    /// replaces the last good one.
    /// </summary>
    private static string WriteDump(int number)
    {
        string path = DumpPath(number);
        string partial = path + ".partial";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (Path.GetPathRoot(Path.GetFullPath(path)) is { } root
                && new DriveInfo(root).AvailableFreeSpace < MinFreeBytes)
                return "No dump - less than 2 GB free on the disk.";

            // From a snapshot of the process, not the process itself. Dumping
            // DeskMon directly suspended every one of its threads for the whole
            // write - 34 s for 69 MB once, the widget dead meanwhile - and
            // Microsoft warns that a dump taken that way from inside can hang
            // for good on a lock a suspended thread holds. A snapshot is a
            // copy-on-write clone made in a moment; the dump is read from the
            // clone while DeskMon carries on. (.NET's createdump can't help:
            // since .NET 8 it no longer dumps a process by its id.)
            IntPtr self = GetCurrentProcess();
            int error = PssCaptureSnapshot(self, SnapshotFlags, ContextAll, out IntPtr snapshot);
            if (error != 0)
                return $"The dump failed (snapshot error {error}).";

            try
            {
                using var file = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                var callback = new MiniDumpCallbackInformation { Routine = Marshal.GetFunctionPointerForDelegate(SnapshotCallback) };

                // What dotnet-dump and Visual Studio need for managed stacks
                // and the heap, without the mapped DLL images a full dump adds.
                const uint type = MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithUnloadedModules
                                | MiniDumpWithPrivateReadWriteMemory | MiniDumpWithFullMemoryInfo
                                | MiniDumpWithThreadInfo;

                if (!MiniDumpWriteDump(snapshot, (uint)Environment.ProcessId, file.SafeFileHandle, type,
                                       IntPtr.Zero, IntPtr.Zero, ref callback))
                    return $"The dump failed (error {Marshal.GetLastWin32Error()}).";
            }
            finally
            {
                PssFreeSnapshot(self, snapshot);
            }

            File.Move(partial, path, overwrite: true);
            return $"Memory dump: {path} ({new FileInfo(path).Length / (1024 * 1024)} MB).";
        }
        catch (Exception ex)
        {
            try { File.Delete(partial); } catch { /* best effort */ }
            return $"The dump failed: {ex.Message}";
        }
    }

    // --- The snapshot and the dump ---------------------------------------------

    /// <summary>
    /// Memory cloned copy-on-write, handles and threads with their full
    /// context - what a dump needs - taken outside any job DeskMon is in,
    /// with the clone's memory given back as it is freed.
    /// </summary>
    private const uint SnapshotFlags =
        0x00000001     // PSS_CAPTURE_VA_CLONE
        | 0x00000004   // PSS_CAPTURE_HANDLES
        | 0x00000008   // PSS_CAPTURE_HANDLE_NAME_INFORMATION
        | 0x00000010   // PSS_CAPTURE_HANDLE_BASIC_INFORMATION
        | 0x00000020   // PSS_CAPTURE_HANDLE_TYPE_SPECIFIC_INFORMATION
        | 0x00000040   // PSS_CAPTURE_HANDLE_TRACE
        | 0x00000080   // PSS_CAPTURE_THREADS
        | 0x00000100   // PSS_CAPTURE_THREAD_CONTEXT
        | 0x00000200   // PSS_CAPTURE_THREAD_CONTEXT_EXTENDED
        | 0x04000000   // PSS_CREATE_BREAKAWAY_OPTIONAL
        | 0x08000000   // PSS_CREATE_BREAKAWAY
        | 0x20000000   // PSS_CREATE_USE_VM_ALLOCATIONS
        | 0x80000000;  // PSS_CREATE_RELEASE_FREE_MEMORY

    /// <summary>CONTEXT_ALL on x64: every register, so the stacks can be walked.</summary>
    private const uint ContextAll = 0x0010001F;

    /// <summary>
    /// Tells dbghelp the handle it was given is a snapshot, not a process -
    /// without it MiniDumpWriteDump refuses the handle. Kept in a static field
    /// so the delegate can't be collected while native code holds it.
    /// </summary>
    private static readonly MiniDumpCallback SnapshotCallback = (_, input, output) =>
    {
        // MINIDUMP_CALLBACK_INPUT is packed to 4 bytes: ProcessId, then the
        // 8-byte ProcessHandle, then CallbackType at 12. Status is the
        // output's first field; S_FALSE there means "yes, a snapshot".
        if (Marshal.ReadInt32(input, 12) == IsProcessSnapshotCallback)
            Marshal.WriteInt32(output, 1);
        return true;
    };

    private const int IsProcessSnapshotCallback = 16;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool MiniDumpCallback(IntPtr param, IntPtr input, IntPtr output);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MiniDumpCallbackInformation
    {
        public IntPtr Routine;
        public IntPtr Param;
    }

    private const uint MiniDumpWithDataSegs = 0x00000001;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithPrivateReadWriteMemory = 0x00000200;
    private const uint MiniDumpWithFullMemoryInfo = 0x00000800;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern int PssCaptureSnapshot(IntPtr process, uint captureFlags, uint threadContextFlags, out IntPtr snapshot);

    [DllImport("kernel32.dll")]
    private static extern int PssFreeSnapshot(IntPtr process, IntPtr snapshot);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle file,
                                                 uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam,
                                                 ref MiniDumpCallbackInformation callback);
}
