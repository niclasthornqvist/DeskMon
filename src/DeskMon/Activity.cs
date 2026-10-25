using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DeskMon;

/// <summary>
/// What DeskMon is in the middle of right now, on every thread - for naming
/// the call behind a freeze.
///
/// The freezes so far stopped the whole process, not only the UI thread: a
/// memory dump couldn't even pause it to be written, and by the time it could,
/// whatever had been stuck had returned and the dump showed every thread idle.
/// That points at a call into a driver - the sensor library's, the graphics
/// card's, the network's - and this is how to see which. Each of those notes
/// when it starts and ends; the watchdog writes out what is still running the
/// moment the widget stops answering, which needs no pause of anything.
///
/// Anything that takes longer than its limit is also logged as it ends, so a
/// slow call shows up even when it was not long enough to freeze the widget.
/// </summary>
internal static class Activity
{
    private sealed record Entry(string Name, long StartedAt, string Thread, long SlowAfterMs);

    private static readonly Dictionary<long, Entry> Running = new();
    private static readonly object RunningLock = new();
    private static long _next;

    /// <summary>The UI thread's id - see <see cref="ThreadName"/>. Set once at startup.</summary>
    private static int _uiThread = -1;

    /// <summary>Marks the calling thread as the UI thread, for the log's sake.</summary>
    public static void SetUiThread() => _uiThread = Environment.CurrentManagedThreadId;

    /// <summary>
    /// How long a step may take before it is logged as slow. Above anything a
    /// healthy read takes - the GPU, the slowest, is under 100 ms - and below
    /// the watchdog's 10 s, so a slow step is on record even short of a freeze.
    /// </summary>
    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Notes that <paramref name="name"/> has started on this thread; dispose
    /// the result when it is done. <paramref name="slowAfter"/> for a step that
    /// is allowed longer than usual, such as PresentMon shutting down.
    /// </summary>
    public static Scope Begin(string name, TimeSpan? slowAfter = null)
    {
        var entry = new Entry(name, Environment.TickCount64, ThreadName(),
                              (long)(slowAfter ?? SlowAfter).TotalMilliseconds);
        long id;
        lock (RunningLock)
        {
            id = ++_next;
            Running[id] = entry;
        }
        return new Scope(id);
    }

    /// <summary>Ends a step - see <see cref="Begin"/>. A struct, so a step costs no allocation of its own.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly long _id;

        internal Scope(long id) => _id = id;

        public void Dispose()
        {
            if (_id == 0)
                return;

            Entry? entry;
            lock (RunningLock)
            {
                if (!Running.Remove(_id, out entry))
                    return;
            }

            long took = Environment.TickCount64 - entry.StartedAt;
            if (took > entry.SlowAfterMs)
                App.Log("slow", $"{entry.Name} took {Seconds(took)} on {entry.Thread}.");
        }
    }

    /// <summary>
    /// Everything still running, longest first - "GPU sensor update for 31.2 s
    /// on a pool thread; UI tick for 30.9 s on the UI thread" - or a plain
    /// "nothing" when no tracked step is under way.
    /// </summary>
    public static string Describe()
    {
        long now = Environment.TickCount64;
        List<Entry> running;
        lock (RunningLock)
            running = Running.Values.ToList();

        if (running.Count == 0)
            return "No tracked step was running.";

        return "Running: " + string.Join("; ", running
            .OrderBy(entry => entry.StartedAt)
            .Select(entry => $"{entry.Name} for {Seconds(now - entry.StartedAt)} on {entry.Thread}")) + ".";
    }

    private static string Seconds(long milliseconds)
        => (milliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>Which thread, in words: the UI thread, a named one, or a pool thread.</summary>
    private static string ThreadName()
    {
        var thread = Thread.CurrentThread;
        if (thread.ManagedThreadId == _uiThread)
            return "the UI thread";
        if (thread.Name is { Length: > 0 } name)
            return $"\"{name}\"";
        return thread.IsThreadPoolThread ? $"pool thread {thread.ManagedThreadId}" : $"thread {thread.ManagedThreadId}";
    }
}
