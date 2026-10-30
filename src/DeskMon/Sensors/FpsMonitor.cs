using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DeskMon.Sensors;

/// <summary>
/// Frame rate of the running game, measured with Intel's PresentMon.
///
/// PresentMon is an ETW consumer: it listens to the frame-present events
/// Windows already emits and never touches the game process, so there is no
/// injection and nothing for an anti-cheat to object to. It needs
/// administrator rights for the trace session, which this app already has.
///
/// The child process only exists while a game is running - see
/// <see cref="Start"/> / <see cref="Stop"/> - so nothing is measured, parsed or
/// spawned while you are just looking at the desktop.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    /// <summary>
    /// Frames averaged for the headline rate. ~1.5 s at 60 fps, ~0.6 s at 144 -
    /// short on purpose, so the number on the panel tracks what is happening now.
    /// </summary>
    private const int RecentFrames = 90;

    /// <summary>
    /// Frames kept per swap chain. The percentiles need a far longer run than
    /// the headline average: 3600 frames is about 25 s at 144 fps and a minute
    /// at 60. Costs 29 KB per swap chain, and there is normally one.
    /// </summary>
    private const int HistoryFrames = 3600;

    /// <summary>
    /// Frames needed before a percentile is reported at all. A 1% low drawn
    /// from 50 frames is just the worst of 50, which is noise wearing a
    /// statistic's name - better to show n/a for a few seconds.
    /// </summary>
    private const int OnePercentFloor = 200;
    private const int PointOnePercentFloor = 1000;

    /// <summary>Report n/a rather than a stale number after this long with no frames.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A swap chain counts as live if it presented this recently. Loose enough
    /// to ride out a stutter at 30 fps, tight enough that a chain nothing
    /// presents on any more drops out of the running.
    /// </summary>
    private static readonly TimeSpan LiveWithin = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Latency samples kept per swap chain. Far fewer than the frame times:
    /// nothing takes a percentile of these, only a running average, and a
    /// latency figure that lagged seconds behind the game would be no use.
    /// </summary>
    private const int LatencyHistory = 240;

    /// <summary>How many chains to remember before dropping the quietest.</summary>
    private const int MaxChains = 8;

    private readonly object _lock = new();

    /// <summary>
    /// One swap chain's recent frame times, and when it last presented. The
    /// timestamp is what separates the chain the game is drawing on from one it
    /// has finished with - see <see cref="Current"/>.
    ///
    /// A ring buffer rather than a queue, because the percentiles need the whole
    /// history while the headline average needs only the newest slice of it, and
    /// an array can serve both without copying anything twice.
    /// </summary>
    private sealed class SwapChain
    {
        private readonly double[] _frames = new double[HistoryFrames];
        private int _next;

        /// <summary>How much of the buffer has been filled, up to its length.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// Every frame this chain has ever presented, not just the ones still in
        /// the buffer. The average in the session summary has to cover the whole
        /// session, and the buffer holds only the last 3600 frames - so the
        /// totals are accumulated as frames arrive rather than derived later -
        /// with the lows and jitter over the whole of it, for the performance score.
        /// The loading screen is left out - see FrameStats' "Loading left out",
        /// and the game's loading lock with it where it has one.
        /// </summary>
        public FrameStats Totals { get; }

        public SwapChain(double? loadingLockFps, double? menuAboveFps) => Totals = new(skipLoading: true, loadingLockFps, menuAboveFps);

        public DateTime LastFrameAt;

        /// <summary>
        /// Display latency, in its own ring because it does not arrive with
        /// every frame: a frame the compositor dropped was never on screen, so
        /// PresentMon leaves the column blank for it. Kept parallel to the
        /// frame times they would desync at the first dropped frame.
        /// </summary>
        private readonly double[] _latencies = new double[LatencyHistory];
        private int _nextLatency;

        public int LatencyCount { get; private set; }

        public void AddLatency(double milliseconds)
        {
            _latencies[_nextLatency] = milliseconds;
            _nextLatency = (_nextLatency + 1) % LatencyHistory;

            if (LatencyCount < LatencyHistory)
                LatencyCount++;
        }

        /// <summary>Mean of the newest <paramref name="wanted"/> latency samples.</summary>
        public double MeanLatency(int wanted)
        {
            int take = Math.Min(wanted, LatencyCount);
            if (take == 0)
                return 0;

            double total = 0;
            int index = _nextLatency;
            for (int i = 0; i < take; i++)
            {
                index = (index - 1 + LatencyHistory) % LatencyHistory;
                total += _latencies[index];
            }

            return total / take;
        }

        public void Add(double frameTime)
        {
            _frames[_next] = frameTime;
            _next = (_next + 1) % HistoryFrames;

            if (Count < HistoryFrames)
                Count++;

            Totals.Add(frameTime);
        }

        /// <summary>Mean frame time over the newest <paramref name="wanted"/> frames.</summary>
        public double MeanOfRecent(int wanted)
        {
            int take = Math.Min(wanted, Count);
            if (take == 0)
                return 0;

            double total = 0;
            int index = _next;
            for (int i = 0; i < take; i++)
            {
                index = (index - 1 + HistoryFrames) % HistoryFrames;
                total += _frames[index];
            }

            return total / take;
        }

        /// <summary>
        /// Copies the whole history into a scratch buffer for sorting, and says
        /// how many frames that was.
        /// </summary>
        public int CopyTo(double[] destination)
        {
            // Order does not matter - the caller sorts. Once wrapped, the live
            // frames are the whole array; before that, the first Count of it.
            // Both cases are the same copy.
            int take = Math.Min(Count, destination.Length);
            Array.Copy(_frames, 0, destination, 0, take);
            return take;
        }
    }

    /// <summary>
    /// Everything the panel needs from one look at the buffer, taken under a
    /// single lock so the average and the percentiles describe the same frames.
    /// </summary>
    public readonly record struct FpsReading(
        double? Fps,
        double? OnePercentLow,
        double? PointOnePercentLow,
        double? Latency,
        int Frames);

    /// <summary>
    /// Sorted scratch for the percentiles. Reused rather than allocated per
    /// read - this runs once a tick, and 3600 doubles each time would be real
    /// garbage for no reason. Only touched under <see cref="_lock"/>.
    /// </summary>
    private readonly double[] _sorted = new double[HistoryFrames];

    /// <summary>Frame times per swap chain. A game can present on several.</summary>
    private readonly Dictionary<string, SwapChain> _chains = new();

    /// <summary>Trace session name, also used to clean up a session we left behind.</summary>
    private const string SessionName = "DeskMon";

    private Process? _process;
    private string? _exePath;

    /// <summary>
    /// One PresentMon run's reading of its output: where its columns are, the
    /// last swap chain key, and the generation it belongs to. Its own object
    /// per run, not fields shared by all: Start reset those under the lock on
    /// the UI thread while the output thread read them without it, and a line
    /// from the old run already on its way when Stop unhooked it could then
    /// land in the new run's chains, read with the wrong columns.
    /// </summary>
    private sealed class OutputRun
    {
        public OutputRun(int generation) => Generation = generation;

        public int Generation { get; }
        public int SwapChainColumn = -1, FrameTimeColumn = -1, LatencyColumn = -1;

        /// <summary>The swap-chain key of the last frame read - see OnOutputLine. Only touched by its output thread.</summary>
        public string? LastChain;

        /// <summary>The handler hooked to its process, kept so it can be unhooked again.</summary>
        public DataReceivedEventHandler? Handler;
    }

    /// <summary>The run <see cref="_process"/> is reading. Under <see cref="_lock"/>.</summary>
    private OutputRun? _run;

    /// <summary>
    /// How often the lows are worked out again. They sort the whole 3600-frame
    /// history, under the lock PresentMon's reader waits on for every frame -
    /// fine at the default two-second game tick, four times a second at the
    /// fastest. Over a 25-second window a second's delay changes nothing.
    /// </summary>
    private static readonly TimeSpan LowsEvery = TimeSpan.FromSeconds(1);

    /// <summary>The lows as last worked out, for which chain and when. Under <see cref="_lock"/>.</summary>
    private (DateTime At, SwapChain Chain, double? One, double? PointOne, int Frames)? _lows;
    private DateTime _lastFrameAt;

    /// <summary>
    /// What the latency column is called, newest name first.
    ///
    /// DisplayLatency is the v2 metric: the time from the game starting work on
    /// a frame to that frame being on screen. MsUntilDisplayed is what the same
    /// figure was called before v2, kept so a different PresentMon build still
    /// reports something rather than n/a.
    /// </summary>
    private static readonly string[] LatencyColumns = { "DisplayLatency", "MsUntilDisplayed" };

    /// <summary>Why FPS is unavailable, for the tooltip. Null when fine.</summary>
    public string? Error { get; private set; }

    /// <summary>The process being measured, or null when not measuring.</summary>
    public int? TargetPid { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// The frame rate and its percentile lows, or a default reading when there
    /// is nothing trustworthy to report. All computed from the game's own frame
    /// timings rather than from how fast lines arrive, so they are immune to
    /// the child process's output buffering.
    /// </summary>
    public FpsReading Current
    {
        get
        {
            lock (_lock)
            {
                if (GameChain() is not { } busiest)
                    return default;

                double mean = busiest.MeanOfRecent(RecentFrames);
                double? fps = mean > 0 ? 1000.0 / mean : null;

                // Sorted again only once a second, or for a different chain -
                // see LowsEvery. The average above stays live every tick.
                var now = DateTime.UtcNow;
                if (_lows is not { } lows || !ReferenceEquals(lows.Chain, busiest) || now - lows.At >= LowsEvery)
                {
                    int counted = busiest.CopyTo(_sorted);
                    Array.Sort(_sorted, 0, counted);
                    lows = (now, busiest,
                            LowAverage(counted, 0.01, OnePercentFloor),
                            LowAverage(counted, 0.001, PointOnePercentFloor),
                            counted);
                    _lows = lows;
                }

                // A handful of samples first: the column is blank for frames
                // that never reached the screen, and in a windowed game it can
                // be blank throughout - which is n/a, not zero.
                double? latency = busiest.LatencyCount >= 5
                    ? busiest.MeanLatency(RecentFrames)
                    : null;

                return new FpsReading(fps, lows.One, lows.PointOne, latency, lows.Frames);
            }
        }
    }

    /// <summary>
    /// Just the frame rate, for the FPS overlay, which shows nothing else and
    /// asks twice a second. <see cref="Current"/> sorts the whole history for
    /// the percentile lows each time; this only averages the newest frames.
    /// The same chain and the same average, so the two never disagree.
    /// </summary>
    public double? CurrentFps
    {
        get
        {
            lock (_lock)
            {
                if (GameChain() is not { } busiest)
                    return null;

                double mean = busiest.MeanOfRecent(RecentFrames);
                return mean > 0 ? 1000.0 / mean : null;
            }
        }
    }

    /// <summary>
    /// The swap chain the game is drawing on, or null when there is nothing
    /// trustworthy to report. Call under <see cref="_lock"/>.
    ///
    /// Overlays and launchers present on their own swap chains; the busiest
    /// one is the game itself. Only chains still presenting are in the
    /// running: one that filled its window and then stopped - an overlay that
    /// was dismissed, or a chain the game replaced on a mode switch - keeps
    /// every frame it ever had, so it would outrank the live chain until that
    /// filled up too, and report frame times that stopped happening seconds ago.
    /// </summary>
    private SwapChain? GameChain()
    {
        var now = DateTime.UtcNow;
        if (_chains.Count == 0 || now - _lastFrameAt > StaleAfter)
            return null;

        SwapChain? busiest = null;
        foreach (var chain in _chains.Values)
        {
            if (now - chain.LastFrameAt > LiveWithin)
                continue;

            // Ties on Count broken by Seen: Count stops at the buffer's 3600,
            // so a game and an overlay both presenting long enough were equal,
            // and whichever the dictionary listed first won - the overlay too.
            if (busiest is null || chain.Count > busiest.Count
                || (chain.Count == busiest.Count && chain.Totals.Seen > busiest.Totals.Seen))
                busiest = chain;
        }

        return busiest is { Count: >= 5 } ? busiest : null;
    }

    /// <summary>
    /// Every frame this run has measured - for the session summary, which wants
    /// the whole session rather than the last few seconds.
    ///
    /// Sums rather than a finished frame rate, because one session is not
    /// necessarily one run: tabbing out of a game stops the measuring and
    /// coming back starts it again, so the caller has several of these to add
    /// up. Frames and milliseconds add; averages do not - averaging a
    /// two-minute run with a two-hour one as equals would be nonsense.
    ///
    /// Taken from the chain that presented the most frames, on the same
    /// reasoning as <see cref="Current"/>: overlays and launchers present too,
    /// and counting their frames alongside the game's would average two
    /// unrelated things together.
    ///
    /// Read it before <see cref="Stop"/>, which forgets the chains. A copy,
    /// so the caller can add runs together while this one keeps counting.
    /// </summary>
    public FrameStats SessionTotals
    {
        get
        {
            lock (_lock)
            {
                SwapChain? main = null;
                foreach (var chain in _chains.Values)
                    // Seen, not Frames: a game still on its loading screen holds
                    // its frames back, and an overlay would win on Frames alone.
                    if (main is null || chain.Totals.Seen > main.Totals.Seen)
                        main = chain;

                return main is null ? new FrameStats() : main.Totals.Copy();
            }
        }
    }

    // Below SessionTotals rather than between its doc comment and it, where
    // it had been put: that left SessionTotals undocumented and this with two.
    /// <summary>
    /// Whether the game's chain - the one <see cref="SessionTotals"/> would
    /// take - is past its loading screen. Asked every tick for the timeline,
    /// so without the copy SessionTotals makes.
    /// </summary>
    public bool SessionSettled
    {
        get
        {
            lock (_lock)
            {
                SwapChain? main = null;
                foreach (var chain in _chains.Values)
                    if (main is null || chain.Totals.Seen > main.Totals.Seen)
                        main = chain;
                return main?.Totals.Settled ?? false;
            }
        }
    }

    /// <summary>
    /// The frame rate across the slowest <paramref name="fraction"/> of frames -
    /// the "1% low" as benchmarkers use the term. Expects <see cref="_sorted"/>
    /// to hold <paramref name="frames"/> frame times in ascending order, so the
    /// slow ones - the stutters - are at the end.
    ///
    /// This averages the worst frames rather than taking the plain percentile
    /// (CapFrameX's P1), and the difference is the whole point. With 1% of
    /// frames stuttering badly, the percentile lands exactly on the boundary
    /// between the good frames and the bad ones and can report the healthy
    /// rate - hiding the very thing the row exists to show. Averaging those
    /// frames reports the stutter.
    ///
    /// It matters at all because the mean hides stutter completely: a game can
    /// average 140 fps and still feel broken if every half second it serves a
    /// 50 ms frame.
    ///
    /// Frame times are averaged and then inverted, not the other way round: k
    /// frames taking T milliseconds is k/T, whereas averaging fps values would
    /// weight a fast frame as heavily as a slow one and flatter the result.
    /// </summary>
    private double? LowAverage(int frames, double fraction, int floor)
    {
        if (frames < floor)
            return null;

        int worst = Math.Max(1, (int)Math.Round(fraction * frames));

        double total = 0;
        for (int i = frames - worst; i < frames; i++)
            total += _sorted[i];

        return total > 0 ? 1000.0 * worst / total : null;
    }

    /// <summary>
    /// Every start and stop of PresentMon, one after another on a worker
    /// thread. Only touched from the UI thread.
    ///
    /// Start used to do all of it inline, inside the widget's tick: wait up to
    /// six seconds for the last run to shut down (an Alt+Tab straight back
    /// into an exclusive-fullscreen game), search PATH and winget's folders,
    /// and check PresentMon's signature twice - each check hashing the whole
    /// file. Every DeskMon window froze meanwhile, right as the game came back.
    /// Queued, the order still holds - a stop's clean-up ends the trace session
    /// by name, and the next run reuses that name, so it must finish first -
    /// but nobody waits for it.
    /// </summary>
    private System.Threading.Tasks.Task _lifecycle = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>
    /// Bumped by every Start and Stop, under <see cref="_lock"/>. A queued
    /// start that finds it moved on was overtaken while it waited or worked,
    /// and must not leave its PresentMon running.
    /// </summary>
    private int _generation;

    /// <summary>A start is queued or under way for <see cref="TargetPid"/>.</summary>
    private volatile bool _starting;

    /// <summary>The measured game's loading lock - see Start. Written and read under _lock, where chains are made.</summary>
    private double? _loadingLockFps;

    /// <summary>The measured game's menu cut-off - see Start. Under _lock, like the lock.</summary>
    private double? _menuAboveFps;

    /// <summary>
    /// Starts measuring <paramref name="pid"/>. Does nothing if already
    /// measuring that process, or about to. Returns at once: PresentMon is
    /// found, checked and started on a worker thread - see <see cref="_lifecycle"/>.
    /// </summary>
    /// <param name="loadingLockFps">
    /// The frame rate this game's loading screens are locked at, from
    /// AppSettings.GameLoadingFps - seconds held there are left out of the
    /// session's figures, see FrameStats. Null for most games.
    /// </param>
    /// <param name="menuAboveFps">Seconds faster than this are menus and left out - see FrameStats' "Menus left out".</param>
    public void Start(int pid, string? presentMonPathOverride, double? loadingLockFps = null, double? menuAboveFps = null)
    {
        // Set even when this pid is already being measured: it's the same
        // game, so the same lock - and a chain made from here on uses it.
        lock (_lock)
        {
            _loadingLockFps = loadingLockFps;
            _menuAboveFps = menuAboveFps;
        }

        if (TargetPid == pid && (_starting || IsRunning))
            return;

        Stop();

        int generation;
        lock (_lock)
        {
            generation = ++_generation;
            _chains.Clear();
            _lastFrameAt = DateTime.UtcNow;
        }

        // A new game starts its count of restarts afresh; the same one keeps
        // counting, so a PresentMon that keeps dying stops being restarted.
        if (pid != _restartsFor)
        {
            _restartsFor = pid;
            _restarts = 0;
        }
        else if (_diedEarly)
        {
            _restarts++;
        }
        _diedEarly = false;

        TargetPid = pid;
        Error = null;
        _starting = true;

        _lifecycle = _lifecycle.ContinueWith(_ => Launch(pid, presentMonPathOverride, generation),
                                             System.Threading.CancellationToken.None,
                                             System.Threading.Tasks.TaskContinuationOptions.None,
                                             System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>
    /// What Start used to do inline: find PresentMon, check it, start it. On
    /// the lifecycle queue, so the run before it has finished shutting down.
    /// </summary>
    private void Launch(int pid, string? presentMonPathOverride, int generation)
    {
        // Named for the freeze log - see Activity. The signature checks hash
        // the file and ask Windows' trust provider, which can take a while.
        using var step = Activity.Begin("starting PresentMon", TimeSpan.FromSeconds(10));
        try
        {
            if (!IsCurrent(generation))
                return;   // overtaken while it waited its turn

            string? exe = ResolvePresentMon(presentMonPathOverride);
            if (exe is null)
            {
                Fail(generation, "PresentMon not found, or not signed by Intel. Install it with: winget install Intel.PresentMon.Console");
                return;
            }

            // Held open - read and run allowed, writing and replacing not - from
            // the check of its signature until it has started, so the file that
            // runs as administrator is the one that was checked. See HoldIfGenuine.
            using var held = HoldIfGenuine(exe);
            if (held is null)
            {
                Fail(generation, "PresentMon is no longer signed by Intel - not started.");
                return;
            }

            StartProcess(exe, pid, generation);
        }
        catch (Exception ex)
        {
            Fail(generation, ex.Message);
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_lock)
            return generation == _generation;
    }

    /// <summary>Says why, unless a newer Start or a Stop has made it nobody's business.</summary>
    private void Fail(int generation, string error)
    {
        if (!IsCurrent(generation))
            return;

        Error = error;
        _starting = false;
    }

    private void StartProcess(string exe, int pid, int generation)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // By name for a multi-process game, by id otherwise - see TargetArgs.
        foreach (string arg in TargetArgs(pid))
            info.ArgumentList.Add(arg);

        // v2 metrics gives the FrameTime column this reads. GPU and input
        // tracking are switched off because they add ETW work we never use.
        foreach (string arg in new[]
                 {
                     "--output_stdout",
                     "--no_console_stats",
                     "--v2_metrics",
                     "--no_track_gpu",
                     "--no_track_input",
                     // Clears a session left behind if we were killed last time.
                     "--stop_existing_session",
                     "--session_name", SessionName,
                     // Exits by itself, cleanly, the moment the game closes.
                     "--terminate_on_proc_exit",
                 })
        {
            info.ArgumentList.Add(arg);
        }

        var run = new OutputRun(generation);
        run.Handler = (_, e) => OnOutputLine(run, e);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += run.Handler;
        process.Exited += OnPresentMonExited;   // dying mid-game is noticed - see NeedsRestart
        process.ErrorDataReceived += (_, _) => { };   // drain, or the pipe can fill and block

        // A start that fails lets go of the process, handlers and all: Launch
        // only reports the error, and each failed restart - up to three a
        // game - used to leave one behind. One that started and then failed
        // to be read is ended too, or it would run unread until the game closed.
        bool started = false;
        try
        {
            started = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            process.OutputDataReceived -= run.Handler;
            process.Exited -= OnPresentMonExited;
            if (started)
                try { process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }

        lock (_lock)
        {
            if (generation == _generation)
            {
                _process = process;
                _run = run;
                _exePath = exe;
                _starting = false;
            }
        }

        if (ReferenceEquals(_process, process))
        {
            // Exited before it was _process - a PresentMon that fails at once -
            // was ignored by the handler; asked again now that it is.
            if (process.HasExited)
                OnPresentMonExited(process, EventArgs.Empty);
            return;
        }

        // Overtaken while it started - the game was left, or another took
        // over. Shut down here, before anything later on the queue can start
        // a run under the same session name.
        StopRun(process, exe, run.Handler);
    }

    /// <summary>
    /// Which process PresentMon should listen to.
    ///
    /// The window in front is not always the one that presents. Chromium-based
    /// games - NW.js (RPG Maker MV/MZ), Electron - draw from a separate GPU
    /// process, a child running the same exe. Followed by id, the window's
    /// process presents nothing at all: the session is logged but FPS stays
    /// n/a for the whole game. Followed by name, the GPU process is included,
    /// and the busiest-chain rule in <see cref="GameChain"/> picks its frames.
    ///
    /// Only when a process of the same exe is the game's own child. Any second
    /// copy used to be enough, which also caught a second instance of the game,
    /// or an unrelated program sharing a generic name like Game.exe - and their
    /// frames could end up in the session.
    /// </summary>
    private static string[] TargetArgs(int pid)
    {
        string byId = pid.ToString(CultureInfo.InvariantCulture);
        try
        {
            if (ChildWithSameExe(pid) is { } exeName)
                return new[] { "--process_name", exeName };
        }
        catch { /* the snapshot failed - followed by id, as before */ }

        return new[] { "--process_id", byId };
    }

    /// <summary>
    /// The game's exe name ("nw.exe") when one of its direct children runs the
    /// same exe, else null. From one Toolhelp snapshot of every process, which
    /// carries each one's parent and exe name without opening any of them.
    /// </summary>
    private static string? ChildWithSameExe(int pid)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return null;

        try
        {
            var entries = new List<(int Pid, int Parent, string Exe)>();
            var entry = new ProcessEntry { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                entries.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));

            string? exe = entries.FirstOrDefault(e => e.Pid == pid).Exe;
            if (string.IsNullOrEmpty(exe))
                return null;

            return entries.Any(e => e.Parent == pid && e.Pid != pid
                                    && string.Equals(e.Exe, exe, StringComparison.OrdinalIgnoreCase))
                ? exe
                : null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
                                                 CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // --- PresentMon stopping by itself ----------------------------------------

    /// <summary>Restarts for one game before giving up - a PresentMon that keeps dying must not loop.</summary>
    private const int MaxRestarts = 3;

    /// <summary>PresentMon ended while its game still ran; the widget restarts it on a later tick - see NeedsRestart.</summary>
    private volatile bool _diedEarly;
    private int _restarts;
    private int _restartsFor;
    private DateTime _retryAt;

    /// <summary>
    /// PresentMon stopped by itself while the game is still running - crashed,
    /// or its trace session was taken - and it is time to try again. Asked by
    /// the widget's tick, which harvests the frames and calls Start: nothing
    /// else would, as Start is only called when a game is first picked up,
    /// and FPS stayed n/a for the rest of the session with no reason given.
    /// </summary>
    public bool NeedsRestart
        => _diedEarly && TargetPid is not null && !_starting && _restarts < MaxRestarts && DateTime.UtcNow >= _retryAt;

    /// <summary>
    /// The current run's PresentMon exited. A stop on purpose has already let
    /// go of it (_process is another or null), and with the game gone
    /// --terminate_on_proc_exit is doing its job - neither is a failure.
    /// On a pool thread.
    /// </summary>
    private void OnPresentMonExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
            return;

        int? target;
        lock (_lock)
        {
            if (!ReferenceEquals(_process, process))
                return;
            target = TargetPid;
        }

        if (target is not int pid || !IsAlive(pid))
            return;

        int code;
        try { code = process.ExitCode; } catch { code = -1; }

        _retryAt = DateTime.UtcNow + TimeSpan.FromSeconds(2 * (_restarts + 1));
        Error = _restarts < MaxRestarts
            ? $"PresentMon stopped (exit code {code}) - starting it again"
            : $"PresentMon stopped (exit code {code}) {MaxRestarts} times - not started again for this game";
        _diedEarly = true;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        Process? process;
        string? exe;
        DataReceivedEventHandler? handler;
        lock (_lock)
        {
            // A start still queued or under way sees this and gives up - and
            // so does any line of the old run still on its way, see OnOutputLine.
            _generation++;

            process = _process;
            exe = _exePath;
            handler = _run?.Handler;
            _process = null;
            _run = null;
            _exePath = null;

            // Forgotten now rather than when the process is gone: the caller
            // has already taken SessionTotals, and Current must stop reporting
            // this run straight away.
            _chains.Clear();
        }

        TargetPid = null;
        _starting = false;

        if (process is null)
            return;

        // Unhooked here, not only when the queue gets to StopRun: that can
        // wait behind a start still checking PresentMon's signature, and the
        // lines the old run printed meanwhile landed in the chains just
        // cleared - the next run's, a game switched to or tabbed back into.
        if (handler is not null)
            process.OutputDataReceived -= handler;

        // Off the UI thread, on the same queue as the starts. A clean shutdown
        // starts a helper process and waits for two processes to exit - up to
        // five seconds - and this runs every time you tab out of an
        // exclusive-fullscreen game.
        _lifecycle = _lifecycle.ContinueWith(_ => StopRun(process, exe, handler),
                                             System.Threading.CancellationToken.None,
                                             System.Threading.Tasks.TaskContinuationOptions.None,
                                             System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>Ends one PresentMon run and lets go of it. On the lifecycle queue.</summary>
    private void StopRun(Process process, string? exe, DataReceivedEventHandler? handler)
    {
        // Unhooked first, so lines PresentMon prints while it shuts down can't
        // refill the chains - those frames would otherwise be counted into the
        // session after it was meant to have stopped. Stop has usually done
        // this already; for a run overtaken while it started, this is the one.
        if (handler is not null)
            process.OutputDataReceived -= handler;

        // Named for the freeze log - see Activity. Allowed its five seconds
        // of clean shutdown before it counts as slow.
        using var step = Activity.Begin("stopping PresentMon", TimeSpan.FromSeconds(10));
        try
        {
            // Usually already gone: --terminate_on_proc_exit makes PresentMon
            // close itself the moment the game does, which is the clean path.
            if (!process.HasExited)
                ShutDownCleanly(process, exe);
        }
        catch { /* already gone */ }
        finally
        {
            process.Dispose();
        }
    }

    private void WaitForPendingStop()
    {
        // Bounded: a start and a stop can both be queued, and ShutDownCleanly
        // gives up after five seconds by itself - so this only guards against
        // something stranger than a slow PresentMon.
        try { _lifecycle.Wait(TimeSpan.FromSeconds(10)); } catch { /* each step swallows its own errors */ }
    }

    /// <summary>
    /// Asks PresentMon to stop its own trace session instead of killing it.
    ///
    /// This matters: an ETW trace session outlives the process that created it,
    /// so killing PresentMon leaves a session running with no owner. Those
    /// orphans accumulate across runs and eventually starve new captures of
    /// data - which looks exactly like "FPS stopped working for no reason".
    /// </summary>
    private static void ShutDownCleanly(Process process, string? exe)
    {
        // Checked again, like the start: it runs as administrator too.
        using var held = exe is null ? null : HoldIfGenuine(exe);
        if (held is not null && exe is not null)
        {
            try
            {
                var stopper = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                stopper.ArgumentList.Add("--terminate_existing_session");
                stopper.ArgumentList.Add("--session_name");
                stopper.ArgumentList.Add(SessionName);

                using var helper = Process.Start(stopper);
                helper?.WaitForExit(2000);
            }
            catch { /* fall through to the kill below */ }
        }

        if (process.WaitForExit(2000))
            return;

        // Last resort. Leaves the session behind, but --stop_existing_session
        // on the next start will clear it.
        try { process.Kill(entireProcessTree: true); } catch { }
        process.WaitForExit(1000);
    }

    /// <summary>
    /// One CSV line per presented frame. Columns are located by name from the
    /// header row, because which columns appear depends on the tracking flags
    /// and on the PresentMon version.
    /// </summary>
    /// <param name="run">The run this line came from - see <see cref="OutputRun"/>.</param>
    private void OnOutputLine(OutputRun run, DataReceivedEventArgs e)
    {
        string? line = e.Data;
        if (string.IsNullOrEmpty(line))
            return;

        if (run.FrameTimeColumn < 0)
        {
            if (line.StartsWith("Application,", StringComparison.OrdinalIgnoreCase))
            {
                var columns = line.Split(',');
                run.SwapChainColumn = Array.FindIndex(columns, c => c.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase));
                run.FrameTimeColumn = Array.FindIndex(columns, c => c.Equals("FrameTime", StringComparison.OrdinalIgnoreCase));

                run.LatencyColumn = FindLatencyColumn(columns);

                // Only for the run still wanted: an old one's header must not
                // put its error on the new run's tooltip.
                if (run.FrameTimeColumn < 0 && IsCurrent(run.Generation))
                    Error = "PresentMon output has no FrameTime column";
            }
            return;
        }

        // Parsed by span, not Split: this runs once per presented frame, so at
        // 144 fps a string[] per line would be a few hundred KB of garbage a
        // second for two values.
        if (!TryGetField(line, run.FrameTimeColumn, out var frameTimeText) ||
            !double.TryParse(frameTimeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double frameTime) ||
            frameTime <= 0)
            return;

        // The last chain's key reused when the field is the same one, which it
        // nearly always is: made fresh, it was a new string for every frame.
        string swapChain = "0";
        if (run.SwapChainColumn >= 0 && TryGetField(line, run.SwapChainColumn, out var swapChainText))
            swapChain = run.LastChain is { } last && swapChainText.SequenceEqual(last)
                ? last
                : run.LastChain = swapChainText.ToString();

        lock (_lock)
        {
            // A line of a run that has been stopped or replaced, already on its
            // way when its handler was unhooked: not this run's frame.
            if (run.Generation != _generation)
                return;

            if (!_chains.TryGetValue(swapChain, out var chain))
            {
                // A launcher came and went, or the game recreated its chain on
                // a mode switch. Drop the one that has been quiet longest
                // rather than clearing the lot, which threw away the game's own
                // history along with the strays.
                if (_chains.Count >= MaxChains)
                    EvictQuietest();

                chain = new SwapChain(_loadingLockFps, _menuAboveFps);
                _chains[swapChain] = chain;
            }

            chain.Add(frameTime);

            // Only when this frame actually reached the screen. A dropped frame
            // leaves the column blank, which TryGetField reports as missing -
            // and a zero would drag the average toward a latency that never
            // happened.
            if (run.LatencyColumn >= 0 &&
                TryGetField(line, run.LatencyColumn, out var latencyText) &&
                double.TryParse(latencyText, NumberStyles.Float, CultureInfo.InvariantCulture, out double latency) &&
                latency > 0)
            {
                chain.AddLatency(latency);
            }

            var now = DateTime.UtcNow;
            chain.LastFrameAt = now;
            _lastFrameAt = now;
        }
    }

    /// <summary>
    /// Which column carries the display latency, or -1.
    ///
    /// The known names are tried first, then anything else ending in "Latency".
    /// That fallback exists because the column has been renamed once already -
    /// MsUntilDisplayed became DisplayLatency in v2 - and a PresentMon that
    /// renames it again should cost a guess, not the row.
    ///
    /// Input, instrumented and GPU latencies are skipped however they are
    /// spelled: they need --track_input, an instrumented game, or the GPU
    /// tracking this capture turns off, so they are always blank here - and
    /// GPULatency is a different measurement in any case, not a cheaper way to
    /// the same number.
    ///
    /// Missing is not an error the way FrameTime is - the row shows n/a and
    /// everything else carries on.
    /// </summary>
    private static int FindLatencyColumn(string[] columns)
    {
        foreach (string name in LatencyColumns)
        {
            int known = Array.FindIndex(columns, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (known >= 0)
                return known;
        }

        return Array.FindIndex(columns, c =>
            c.EndsWith("Latency", StringComparison.OrdinalIgnoreCase) &&
            !c.Contains("Input", StringComparison.OrdinalIgnoreCase) &&
            !c.Contains("Click", StringComparison.OrdinalIgnoreCase) &&
            !c.Contains("Instrumented", StringComparison.OrdinalIgnoreCase) &&
            !c.Contains("GPU", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Forgets the swap chain that has gone longest without a frame. Called
    /// under <see cref="_lock"/>.
    /// </summary>
    private void EvictQuietest()
    {
        string? quietest = null;
        DateTime oldest = DateTime.MaxValue;

        foreach (var (key, chain) in _chains)
        {
            if (chain.LastFrameAt < oldest)
            {
                oldest = chain.LastFrameAt;
                quietest = key;
            }
        }

        if (quietest is not null)
            _chains.Remove(quietest);
    }

    private static bool TryGetField(string line, int index, out ReadOnlySpan<char> field)
    {
        var span = line.AsSpan();
        int start = 0;
        for (int i = 0; i < index; i++)
        {
            int comma = span[start..].IndexOf(',');
            if (comma < 0)
            {
                field = default;
                return false;
            }
            start += comma + 1;
        }

        int end = span[start..].IndexOf(',');
        field = end < 0 ? span[start..] : span.Slice(start, end);
        return !field.IsEmpty;
    }

    /// <summary>
    /// Finds presentmon.exe: the configured path, then PATH, then where winget
    /// puts it (the alias shim, then the package folder).
    ///
    /// Internal so <see cref="Prerequisites"/> can ask the same question at
    /// startup - a second search of its own could disagree with this one and
    /// report PresentMon missing when FPS works, or the other way round.
    /// </summary>
    internal static string? ResolvePresentMon(string? overridePath)
    {
        // Every candidate has to be Intel's own, signed - see Genuine. DeskMon
        // starts this as administrator, and both the override (settings.json)
        // and the places searched (PATH, winget's folders) can be written by
        // anything running as the user.
        if (!string.IsNullOrWhiteSpace(overridePath) && Genuine(overridePath) is { } chosen)
            return chosen;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new List<string>
        {
            Path.Combine(localAppData, @"Microsoft\WinGet\Links\presentmon.exe"),
        };

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is not null)
            candidates.AddRange(pathVar.Split(Path.PathSeparator)
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(dir => Path.Combine(dir.Trim(), "presentmon.exe")));

        foreach (string candidate in candidates)
        {
            try
            {
                if (Genuine(candidate) is { } found)
                    return found;
            }
            catch { /* malformed PATH entry */ }
        }

        // Winget package folder, whose name carries a version-ish suffix.
        try
        {
            string packages = Path.Combine(localAppData, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(packages))
            {
                foreach (string dir in Directory.EnumerateDirectories(packages, "Intel.PresentMon*"))
                {
                    string? found = Directory.EnumerateFiles(dir, "presentmon.exe", SearchOption.AllDirectories)
                        .Select(Genuine)
                        .FirstOrDefault(path => path is not null);
                    if (found is not null)
                        return found;
                }
            }
        }
        catch { /* nothing there */ }

        return null;
    }

    /// <summary>Whose signature a PresentMon must carry - Intel's own certificate.</summary>
    private const string Publisher = "Intel Corporation";

    /// <summary>
    /// The real file behind <paramref name="candidate"/> - through a link like
    /// winget's, so what is checked is what runs - when it is signed by Intel;
    /// null for a missing file, or anything else wearing the name.
    /// </summary>
    private static string? Genuine(string candidate)
    {
        try
        {
            if (!File.Exists(candidate))
                return null;

            string real = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                          ?? Path.GetFullPath(candidate);
            return Authenticode.IsSignedBy(real, Publisher) ? real : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the program so it can be read and run but not written, moved or
    /// replaced, and checks its signature again while it is held - closing the
    /// gap between finding PresentMon and starting it, in which a planted
    /// file could otherwise be swapped in. Null when it can't be opened or the
    /// signature no longer holds. Dispose it once the process has started.
    /// </summary>
    private static FileStream? HoldIfGenuine(string exe)
    {
        FileStream? held = null;
        try
        {
            held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Authenticode.IsSignedBy(exe, Publisher))
                return held;
        }
        catch
        {
            // Not there, or held by something else: not started.
        }

        held?.Dispose();
        return null;
    }

    /// <summary>
    /// Stops and waits, unlike <see cref="Stop"/>: on exit there is no next
    /// tick to finish the job, and leaving the shutdown half done would leave
    /// PresentMon's trace session behind.
    /// </summary>
    public void Dispose()
    {
        Stop();
        WaitForPendingStop();
    }
}
