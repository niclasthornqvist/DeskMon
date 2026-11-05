using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DeskMon;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The crash watcher never gets here: Program.Main sends it off before
        // WPF starts - see StartWatcher.

        // The shared styles at application level as well as in each window,
        // for the tooltip style in them: a tooltip opens in a popup of its
        // own, and the application's resources are the one place sure to be
        // searched from there. Done here because App.xaml's resources are
        // never loaded - see the note in it.
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/DeskMon;component/Ui/PanelStyles.xaml", UriKind.Relative),
        });

        // Before anything reads a setting or a note, the dumps below included:
        // the data moves out of app\ on the first start of 1.65 - see DataFolder.
        DataFolder.Prepare();

        // The sensor dumps are checked before the single-instance lock on
        // purpose. They are diagnostics, not a second widget: taking the mutex
        // first meant that asking for a dump while the widget was running -
        // which, with autostart, is always - exited silently and wrote no file,
        // exactly when the dump was wanted. They never show a window, so two
        // copies of DeskMon are never on screen at once.
        if (HasFlag(e.Args, "--list-sensors"))
        {
            DumpSensorsAndExit(openInNotepad: true);
            return;
        }

        // Same dump plus the values the widget would show, written silently.
        // Useful when something reads "n/a" and you want to know why.
        if (HasFlag(e.Args, "--selftest"))
        {
            DumpSensorsAndExit(openInNotepad: false);
            return;
        }

        // Started by a copy that was going down - see RestartAfterCrash. It
        // is waited for until it has really gone, not a fixed moment: Windows
        // Error Reporting can hold a crashed process for seconds while it
        // writes a dump, and one shutting down cleanly is still ending its
        // session. Started over it, this copy put up a second tray icon and
        // PresentMon, and could recover that session a second time.
        int afterCrash = Array.IndexOf(e.Args, AfterCrashFlag);
        StartedAfterCrash = afterCrash >= 0;
        if (StartedAfterCrash)
            WaitForExit(afterCrash + 1 < e.Args.Length ? e.Args[afterCrash + 1] : null);

        // The scheduled task and a manual double-click can easily both fire;
        // a second copy would just draw on top of the first.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // Only once this copy holds the lock: a copy that leaves above must
        // not leave a watcher behind for a widget that never was.
        StartWatcher();

        // A widget that lives at the edge of the screen should never greet the
        // user with a crash dialog; record it and carry on - see OnUnhandledException.
        DispatcherUnhandledException += OnUnhandledException;

        // That hook only ever sees the UI thread, and plenty happens off it: a
        // sensor read, a notes save, the hardware query. Those used to leave
        // nothing behind at all - no dialog, no log, just a process that went
        // away or a feature that stopped - while the troubleshooting guide sent
        // you to error.log. Now they land there too.
        // Such an error ends the process - .NET leaves no way to carry on -
        // so a fresh copy is started on the way out, unless that would be a loop.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("background thread", args.ExceptionObject);
            if (args.IsTerminating)
                RestartAfterCrash();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("unobserved task", args.Exception);
            args.SetObserved();   // logged, so don't let it tear the process down
        };

        // A freeze throws nothing, so none of the hooks above ever see one -
        // the watchdog leaves a dump behind instead. See FreezeWatchdog.
        // Only once the widget is on screen: started before it, the window
        // being built counted as a freeze, and the dump written then paused
        // the whole start - the hardware loading too - for half a minute.
        Activity.SetUiThread();   // so the freeze log can say "the UI thread"
        var main = new MainWindow();
        main.ContentRendered += (_, _) => FreezeWatchdog.Start(Dispatcher);
        main.Show();
    }

    private static bool HasFlag(string[] args, string flag)
        => Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "DeskMon.exe --list-sensors" writes every sensor on the machine to
    /// sensors.txt and opens it - the reference list for adding new metrics.
    /// </summary>
    private void DumpSensorsAndExit(bool openInNotepad)
    {
        // Beside settings.json in the user's folder: app\ may be locked (see DataFolder).
        string path = Path.Combine(DataFolder.Root, "sensors.txt");
        try
        {
            var settings = AppSettings.Load();
            // Reads every time: a one-shot dump wants fresh values, not reused ones.
            using var hub = new Sensors.SensorHub(settings.PreferredGpu, 0, allHardware: true);

            // Two reads: CPU load is a delta, so the first one is always null.
            hub.Read();
            Thread.Sleep(600);
            var snapshot = hub.Read();

            var report = new System.Text.StringBuilder();
            report.AppendLine("=== What the widget reads ===");
            report.AppendLine($"  CpuLoad       {Show(snapshot.CpuLoad)} %");
            report.AppendLine($"  RamLoad       {Show(snapshot.RamLoad)} %   ({Show(snapshot.RamUsedGb)} / {Show(snapshot.RamTotalGb)} GB)");
            report.AppendLine($"  SwapLoad      {Show(snapshot.SwapLoad)} %   ({Show(snapshot.SwapUsedGb)} / {Show(snapshot.SwapTotalGb)} GB)");
            report.AppendLine($"  CpuTemp       {Show(snapshot.CpuTemp)} C");
            report.AppendLine($"  GpuTemp       {Show(snapshot.GpuTemp)} C");
            report.AppendLine($"  CpuPower      {Show(snapshot.CpuPower)} W");
            report.AppendLine($"  CpuClock      {Show(snapshot.CpuClock)} MHz");
            report.AppendLine($"  GpuLoad       {Show(snapshot.GpuLoad)} %");
            report.AppendLine($"  GpuMemoryLoad {Show(snapshot.GpuMemoryLoad)} %");
            report.AppendLine($"  GpuPower      {Show(snapshot.GpuPower)} W");
            report.AppendLine();
            report.AppendLine("=== Every sensor on this machine ===");
            report.AppendLine(hub.DumpSensors());

            Directory.CreateDirectory(DataFolder.Root);   // a first run may not have it yet
            File.WriteAllText(path, report.ToString());

            if (openInNotepad)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "DeskMon --list-sensors");
        }

        Shutdown();
    }

    private static string Show(double? value)
        => value?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";

    /// <summary>When the UI thread's recent errors happened - see <see cref="OnUnhandledException"/>.</summary>
    private readonly Queue<DateTime> _recentErrors = new();

    private const int ErrorsBeforeGivingUp = 5;
    private static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Logs the error and carries on. This used to shut DeskMon down on the
    /// first one, and much of the UI runs in async void handlers - the tick,
    /// picture loading, the art lookups - so any one unexpected fault made the
    /// widget vanish mid-game with nothing to show for it but error.log. One
    /// feature failing once is better than the whole program going.
    ///
    /// Only an error repeating fast - five within a minute, which is a fault
    /// on the polling path that every tick hits again - still shuts down, so
    /// a broken state can't spin and fill the log for hours.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui thread", e.Exception);
        e.Handled = true;

        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (now - _recentErrors.Peek() > ErrorWindow)
            _recentErrors.Dequeue();

        if (_recentErrors.Count >= ErrorsBeforeGivingUp)
        {
            Log("ui thread", $"{ErrorsBeforeGivingUp} errors within a minute - shutting down.");
            RestartAfterCrash();   // a fresh start often clears the broken state; the guard stops a loop
            Shutdown();
        }
    }

    private const string AfterCrashFlag = "--after-crash";

    /// <summary>
    /// Up to 15 s for the copy that started this one to be gone - by its
    /// process id, passed after --after-crash. Two seconds, as before, when
    /// there is no id to go by (a copy started by hand with the flag).
    /// </summary>
    private static void WaitForExit(string? pid)
    {
        if (!int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id))
        {
            Thread.Sleep(2000);
            return;
        }

        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(id);
            old.WaitForExit(15_000);
        }
        catch
        {
            // Already gone - nothing to wait for.
        }
    }

    /// <summary>This copy was started by one that crashed - MainWindow says so once the tray icon is up.</summary>
    public static bool StartedAfterCrash { get; private set; }

    /// <summary>
    /// When the recent crash restarts happened, one UTC time a line - kept
    /// outside app\, which may be what is broken. A file written by an older
    /// build holds one such line, and reads the same way.
    /// </summary>
    private static string CrashStampPath => Path.Combine(Path.GetTempPath(), "DeskMon-crash-restart.txt");

    private static readonly TimeSpan CrashRestartGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Restarts allowed within <see cref="CrashRestartGap"/>. One was too few:
    /// a graphics driver update can take minutes, and a copy restarted in the
    /// middle of it can crash on the driver again - after which the guard
    /// left no widget until the next logon. Three still stops a copy broken
    /// at startup from restarting for ever.
    /// </summary>
    private const int CrashRestartsAllowed = 3;

    /// <summary>
    /// How long the watcher waits before each restart in a row: the first
    /// soon, later ones longer, so a driver still being installed has time to
    /// finish before the sensors are opened on it again. See WatchWidget.
    /// </summary>
    private static readonly TimeSpan[] CrashRestartDelays =
        { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Starts a fresh copy of DeskMon as this one goes down from an error, so
    /// a crash mid-game brings the widget back within seconds rather than
    /// leaving nothing until the next logon. The open session is not lost:
    /// the new copy recovers it from its checkpoint, marked "cut short".
    ///
    /// At most three times in 10 minutes: something broken at startup would
    /// otherwise crash and restart for ever. The single-instance lock is let
    /// go first, as Reload settings does, or the new copy would see this one
    /// and leave. Silent on failure - this runs while already failing.
    /// Can't help with DeskMon being killed or a native crash, which never
    /// reach .NET's handlers - the watcher covers the crash, see StartWatcher.
    /// </summary>
    private void RestartAfterCrash()
    {
        try
        {
            if (RestartedTooRecently())
                return;

            // The new copy reads settings.json and the open session as it
            // starts; whatever this one still has queued goes down first.
            // Short: the process is already failing.
            AtomicFile.Flush(TimeSpan.FromSeconds(2));
            ReleaseSingleInstanceLock();
            StartFreshCopy(Environment.ProcessId);
        }
        catch (Exception ex)
        {
            Log("restart after a crash", ex);
        }
    }

    /// <summary>
    /// The guard of <see cref="CrashRestartsAllowed"/> restarts in 10 minutes,
    /// shared by RestartAfterCrash and the watcher through the same stamp
    /// file - so a crash that both of them see counts once, and a copy broken
    /// at startup can't be brought back for ever by either.
    /// </summary>
    private static bool RestartedTooRecently()
    {
        if (RecentRestarts().Count < CrashRestartsAllowed)
            return false;

        Log("restart after a crash", $"Not restarting: DeskMon already restarted after a crash {CrashRestartsAllowed} times in the last 10 minutes.");
        return true;
    }

    /// <summary>
    /// The restarts within <see cref="CrashRestartGap"/>, from the stamp file.
    /// Empty when there is none or it can't be read: a guard that can't be
    /// read must not stop the one restart that would bring the widget back.
    /// </summary>
    private static List<DateTime> RecentRestarts()
    {
        var recent = new List<DateTime>();
        try
        {
            if (!File.Exists(CrashStampPath))
                return recent;

            var cutoff = DateTime.UtcNow - CrashRestartGap;
            foreach (string line in File.ReadAllLines(CrashStampPath))
                if (DateTime.TryParse(line.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    && at.ToUniversalTime() > cutoff)
                    recent.Add(at.ToUniversalTime());
        }
        catch
        {
            // Unreadable: counted as none - see above.
        }
        return recent;
    }

    /// <summary>
    /// Starts the new copy, stamped first so the guard holds even if the
    /// start itself is what fails. With the old copy's id, so the new one can
    /// wait until it has gone - see WaitForExit. Outside the logon task's job,
    /// as the watcher is - see StartOutsideJob.
    /// </summary>
    private static void StartFreshCopy(int oldPid)
    {
        if (Environment.ProcessPath is not { } exe)
            return;

        // The restarts still inside the window, and this one: older lines are
        // dropped so the file never grows.
        var stamps = RecentRestarts();
        stamps.Add(DateTime.UtcNow);
        File.WriteAllLines(CrashStampPath,
            stamps.ConvertAll(at => at.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));

        StartOutsideJob(exe, $"{AfterCrashFlag} {oldPid}");
        Log("restart after a crash", "Started a fresh copy of DeskMon.");
    }

    /// <summary>
    /// Starts <paramref name="exe"/> outside any job object DeskMon runs in.
    ///
    /// The logon task runs DeskMon inside a job, and Task Scheduler can end
    /// everything in it once the task's own process ends - which for a crash
    /// is the moment the watcher has to do its work, and the copy it starts
    /// would be in the same job. Broken away, both outlive it. A job that
    /// doesn't allow that is told so in error.log and the program is started
    /// inside it, as before - no worse than without this.
    /// Throws as Process.Start does; the callers catch.
    /// </summary>
    private static void StartOutsideJob(string exe, string arguments)
    {
        if (IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob) && inJob)
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder($"\"{exe}\" {arguments}");
            if (CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, CreateBreakawayFromJob,
                              IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var started))
            {
                CloseHandle(started.Process);
                CloseHandle(started.Thread);
                return;
            }

            Log("starting outside the job", $"Windows wouldn't let {Path.GetFileName(exe)} {arguments} leave DeskMon's job (error {Marshal.GetLastWin32Error()}), so it was started inside it.");
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        })?.Dispose();
    }

    private const uint CreateBreakawayFromJob = 0x01000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine,
                                             IntPtr processAttributes, IntPtr threadAttributes,
                                             [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
                                             IntPtr environment, string? currentDirectory,
                                             ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Internal for Program.Main, which looks for it before WPF is started.</summary>
    internal const string WatchFlag = "--watch";

    /// <summary>
    /// Starts the crash watcher: a second DeskMon.exe with no window that
    /// waits for this one to end - see <see cref="WatchWidget"/>.
    ///
    /// A crash in native code - the one on 2026-11-04 was the NVIDIA driver being
    /// replaced during an update while LibreHardwareMonitor read the GPU's
    /// power through it - kills the process on the spot. None of .NET's
    /// handlers run, so RestartAfterCrash never got the chance and the widget
    /// stayed gone until the next logon. Only another process can see that.
    ///
    /// A process of its own rather than Windows' RegisterApplicationRestart:
    /// that waits for 60 s of uptime, may ask first, and can bring DeskMon
    /// back without the admin rights the sensors need. The watcher is started
    /// from this elevated copy, so what it starts is elevated too - and
    /// outside the logon task's job, or it could be ended with the copy it
    /// watches (see StartOutsideJob).
    /// Silent on failure - the widget runs as before, just unwatched.
    /// </summary>
    private static void StartWatcher()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe)
                return;

            StartOutsideJob(exe, $"{WatchFlag} {Environment.ProcessId}");
        }
        catch (Exception ex)
        {
            Log("crash watcher", ex);
        }
    }

    /// <summary>
    /// The watcher's whole life: wait for the widget to end, and start it
    /// again only if it was a native crash.
    ///
    /// Told apart by the exit code. A crash ends with an NTSTATUS error,
    /// 0xC0000005 and the like. Everything else is left alone: Exit in the
    /// tray, Reload settings and a clean close by Build.ps1 end with 0, being
    /// killed from Task Manager or by Build.ps1 ends with 1 or -1, and a .NET
    /// exception ends with 0xE0434352 and is RestartAfterCrash's to restart.
    /// Ctrl+C / log-off's 0xC000013A is an NTSTATUS code too, but a deliberate end.
    /// </summary>
    internal static void WatchWidget(string? pid)
    {
        if (!int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id))
            return;

        try
        {
            // A handle of its own, opened now while the widget runs, not a
            // Process from GetProcessById: that holds no handle of its own, so
            // its ExitCode threw "Process was not started by this object" once
            // the widget had gone - the watcher logged that and never restarted
            // anything. Only waiting and the exit code are asked for.
            using var widget = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, id);
            if (widget.IsInvalid)
                return;   // gone before the watcher got to it - ended at once, nothing to tell

            WaitForSingleObject(widget, Infinite);
            if (!GetExitCodeProcess(widget, out uint code))
            {
                Log("crash watcher", $"Couldn't read how DeskMon ended (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            if ((code & 0xF0000000) != 0xC0000000 || code == 0xC000013A)
                return;

            Log("crash watcher", $"DeskMon ended with 0x{code:X8} - a crash in native code, which .NET can't catch.");
            if (RestartedTooRecently())
                return;

            // A wait first, longer for each restart in a row: started at once,
            // the new copy opened the sensors on a driver still being replaced
            // and could crash on it the same way - see CrashRestartDelays.
            int earlier = Math.Min(RecentRestarts().Count, CrashRestartDelays.Length - 1);
            Thread.Sleep(CrashRestartDelays[earlier]);

            // Started by hand meanwhile: that copy holds the lock, and a second
            // would only see it and leave.
            if (Mutex.TryOpenExisting(SingleInstanceName, out var running))
            {
                running.Dispose();
                Log("crash watcher", "Not restarting: DeskMon was started again meanwhile.");
                return;
            }

            StartFreshCopy(id);
        }
        catch (Exception ex)
        {
            Log("crash watcher", ex);
        }
    }

    /// <summary>How much error.log may grow before it is rotated.</summary>
    private const long MaxLogBytes = 256 * 1024;

    /// <summary>
    /// Appends to error.log in the user's DeskMon folder. Best-effort and silent by design - this runs
    /// when something has already gone wrong, so it must not throw on top.
    ///
    /// The file is rotated rather than left to grow, because a fault that
    /// repeats on the polling path would otherwise write a line every second
    /// for as long as the widget is up.
    /// </summary>
    internal static void Log(string context, object? error)
    {
        // One writer at a time: errors arrive from worker threads too - an
        // unobserved task, a background lookup - and two appends at once made
        // one of them fail, quietly losing the line that said what went wrong.
        lock (LogLock)
        {
            try
            {
                // With the rest of the user's data - see DataFolder - made if
                // need be, since this can be the first thing ever written there.
                Directory.CreateDirectory(DataFolder.Root);
                string path = Path.Combine(DataFolder.Root, "error.log");

                var existing = new FileInfo(path);
                if (existing.Exists && existing.Length > MaxLogBytes)
                    File.Move(path, path + ".old", overwrite: true);

                // Spelled out rather than "u", which puts a Z on it: that says
                // UTC, and this is local time, so every line read an hour or
                // two off against anything else on the PC.
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                File.AppendAllText(path, $"{now}  [{context}]  {error}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* nothing useful left to do */ }
        }
    }

    private static readonly object LogLock = new();

    /// <summary>
    /// Drops the single-instance lock early. Needed before restarting in place
    /// ("Reload settings"): the replacement process checks the lock immediately,
    /// which is long before this process has finished shutting down, and it
    /// would otherwise see an instance already running and exit - leaving no
    /// widget at all.
    /// </summary>
    public void ReleaseSingleInstanceLock()
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    /// <summary>
    /// Takes the lock back after a restart that never happened - Windows
    /// refused to start the replacement - so a later double-click still finds
    /// this copy running instead of starting a second one beside it.
    /// </summary>
    public void TakeSingleInstanceLock()
        => _singleInstance ??= new Mutex(initiallyOwned: true, SingleInstanceName, out _);

    private const string SingleInstanceName = @"Local\DeskMon.SingleInstance";

    protected override void OnExit(ExitEventArgs e)
    {
        // The writes queued off the UI thread - the position saved as the
        // widget closed, the last settings change - die with the process if
        // they haven't run. Bounded, so a dead drive can't hold the exit.
        AtomicFile.Flush(TimeSpan.FromSeconds(5));

        ReleaseSingleInstanceLock();
        base.OnExit(e);
    }
}
