using System;
using System.Linq;

namespace DeskMon.Sensors;

/// <summary>
/// A whole session's frame times, kept small enough to hold for hours: a
/// count and a sum for the average, a sum of squares for how much they vary,
/// and a histogram for the lows.
///
/// The widget's lows come from the last 3600 frames - right for "how is it
/// running now", but the session log kept the lowest they ever dipped to, and
/// a loading screen takes that to 1 fps in almost every game. These cover
/// every frame of the session instead, so a 30-second load is 30 frames out
/// of a few hundred thousand and weighs what it should.
///
/// The histogram has a bin per quarter millisecond up to 100 ms, and each bin
/// keeps the sum of its own frames as well as their number - so the slowest
/// frames' total, which the lows are made of, comes out exact for every whole
/// bin and very nearly so for the one it is cut in. 6.4 KB however long the
/// game runs.
///
/// Not thread-safe: FpsMonitor fills it under its own lock and hands out copies.
/// </summary>
public sealed class FrameStats
{
    private const double BinMs = 0.25;
    private const int Bins = 400;   // 0 to 100 ms; slower frames go in the last bin

    /// <summary>Fewer frames than this gives no low: a 1% low of 50 frames is just the worst one.</summary>
    private const int OnePercentFloor = 200, PointOnePercentFloor = 1000;

    private readonly long[] _counts = new long[Bins + 1];
    private readonly double[] _sums = new double[Bins + 1];

    public long Frames { get; private set; }
    public double TotalMs { get; private set; }

    /// <summary>The frames the jitter is taken over - those under 100 ms; see Add.</summary>
    private long _paced;
    private double _pacedMs, _squares;

    // --- Loading left out ------------------------------------------------------
    //
    // Leaving out only frames of 100 ms or more wasn't enough. Factorio reading
    // its mods draws its loading screen at 10-20 fps - frames of 50-100 ms - for
    // half a minute or more, and that alone took a game holding 60 in play to
    // an average of 49-57, a 1% low of 5-11, hundreds of "stutters" and a Poor
    // score. So a run's frames only count once the game is playing: ten whole
    // seconds in a row with no frame of 50 ms or more. Everything before that
    // is the loading screen and is left out of every figure.
    //
    // A game that never gets there - one that hitches every few seconds, or
    // simply runs at under 20 fps - would lose its whole session that way, so
    // after three minutes without settling everything held back counts after
    // all, as it did before. A session that ends before settling counts in
    // full too, see Add(FrameStats).

    /// <summary>Slower than this is a loading frame while the run hasn't settled: 20 fps.</summary>
    private const double LoadingFrameMs = 50;

    /// <summary>Whole clean seconds in a row that say the game is playing.</summary>
    private const int SettleSeconds = 10;

    /// <summary>How long to wait for that before counting everything anyway.</summary>
    private const double GiveUpMs = 180_000;

    /// <summary>Whether this one waits out the loading screen - only a live run's; see the constructor.</summary>
    private readonly bool _skipLoading;
    private bool _settled;

    /// <summary>Held back while unsettled: the loading frames, the clean streak so far, and the second in progress.</summary>
    private FrameStats? _loading, _streak, _second;
    private int _cleanSeconds;
    private bool _secondClean = true;

    // --- A loading screen locked at a frame rate -----------------------------
    //
    // The Witcher 3 draws its loading screens at a steady 30 fps - frames of
    // 33-37 ms, under the 50 ms above - for 10-20 s, at the start and on every
    // fast travel. Its main menu runs at 1200 fps first, which settled the run
    // before the loading screen even began, so 20 s at 27 fps counted as play
    // and took a game averaging 210 to a 1% low of 27. Such a lock can't be
    // told from a game simply running at 30, so it is a per-game setting
    // (AppSettings.GameLoadingFps): with one, a whole second held at that rate
    // is loading wherever it falls - left out and added to LoadingMs.

    /// <summary>The per-game lock, or null: see the note above.</summary>
    private readonly double? _lockFps;

    /// <summary>
    /// How close to the lock a frame must be: 75% to 110% of it - 22 to 33
    /// fps for 30. Wide below, since the lock measured 27; tight above, since
    /// a capped screen never runs faster than its cap.
    /// </summary>
    private bool AtLock(double frameMs)
        => _lockFps is double fps && frameMs >= 1000 / (fps * 1.1) && frameMs <= 1000 / (fps * 0.75);

    /// <summary>
    /// A second held at the lock: nine frames in ten in its band. Not all of
    /// them, so one odd frame doesn't let a loading second through; a real
    /// game at a varied 30-ish breaks out of the band far more often than that.
    /// </summary>
    private bool Locked(FrameStats second, int atLock)
        => _lockFps is not null && second.Frames > 0 && atLock >= second.Frames * 0.9;

    /// <summary>Frames in the second in progress that sat at the lock - see Locked.</summary>
    private int _secondAtLock;

    /// <summary>
    /// Once settled, with a lock: the second in progress, held until it is
    /// whole and can be judged. Without a lock frames count straight away.
    /// </summary>
    private FrameStats? _playSecond;

    /// <summary>The last whole second was left out - at the lock or in a menu - for the timeline's shading.</summary>
    private bool _atLockNow;

    // --- Menus left out --------------------------------------------------------
    //
    // The Witcher 3's menus - the main menu, the map, the pause screen - run
    // uncapped at 800-1200 fps. A minute in one makes as many frames as nine
    // minutes of play at 130, so they were over half of a session's frames:
    // its median frame came out at 1 ms, every play frame slower than 9 ms
    // counted as a "stutter" (20,000 in nine minutes of smooth play), and the
    // average and the frame-time spread were the menu's. So a whole second
    // running faster than three times the game's FPS target is a menu, and
    // left out like a loading screen. Three times, so a game simply played
    // fast stays in: 222 fps play against a 144 target counts, as would CS2
    // at 600 against 240.

    /// <summary>Faster than this, a second is a menu - see the note above. Null to count every second.</summary>
    private readonly double? _menuAboveFps;

    /// <summary>A whole second whose frames came faster than the menu cut-off.</summary>
    private bool InMenu(FrameStats second)
        => _menuAboveFps is double above && second.TotalMs > 0 && second.Frames * 1000 / second.TotalMs > above;

    /// <summary>The time left out as menus, across every run added in. Kept apart from LoadingMs, which the log names "loading".</summary>
    public double MenuMs { get; private set; }

    /// <param name="skipLoading">
    /// Wait for the game to settle before counting - for a swap chain's live
    /// frames. Off for totals that are only added together from runs, which
    /// were each settled already.
    /// </param>
    /// <param name="loadingLockFps">The game's loading screen lock, if it has one - see the note above.</param>
    /// <param name="menuAboveFps">Seconds faster than this are menus, left out - see "Menus left out".</param>
    public FrameStats(bool skipLoading = false, double? loadingLockFps = null, double? menuAboveFps = null)
    {
        _skipLoading = skipLoading;
        _settled = !skipLoading;
        _lockFps = loadingLockFps is double fps && double.IsFinite(fps) && fps > 0 ? fps : null;
        _menuAboveFps = menuAboveFps is double menu && double.IsFinite(menu) && menu > 0 ? menu : null;
    }

    public void Add(double frameMs)
    {
        if (frameMs <= 0 || double.IsNaN(frameMs))
            return;

        if (!_settled)
        {
            Hold(frameMs);
            return;
        }

        if (_lockFps is null && _menuAboveFps is null)
        {
            Count(frameMs);
            return;
        }

        // Settled, with a lock or a menu cut-off: judged a second at a time,
        // as while unsettled.
        _playSecond ??= new FrameStats();
        _playSecond.Count(frameMs);
        if (AtLock(frameMs))
            _secondAtLock++;

        if (_playSecond.TotalMs < 1000)
            return;

        bool locked = Locked(_playSecond, _secondAtLock);
        bool menu = !locked && InMenu(_playSecond);
        _atLockNow = locked || menu;
        if (locked)
            LoadingMs += _playSecond.TotalMs;
        else if (menu)
            MenuMs += _playSecond.TotalMs;
        else
            Add(_playSecond);
        _playSecond = new FrameStats();
        _secondAtLock = 0;
    }

    /// <summary>A frame while unsettled: into the second in progress, and each whole second judged on its own.</summary>
    private void Hold(double frameMs)
    {
        _loading ??= new FrameStats();
        _streak ??= new FrameStats();
        _second ??= new FrameStats();

        _second.Count(frameMs);
        if (frameMs >= LoadingFrameMs)
            _secondClean = false;
        if (AtLock(frameMs))
            _secondAtLock++;

        if (_second.TotalMs < 1000)
            return;

        // A second at the game's loading lock is loading however smooth -
        // see "A loading screen locked at a frame rate".
        bool locked = Locked(_second, _secondAtLock);
        _secondAtLock = 0;

        // A menu second is set aside: neither play towards settling - the
        // 1200 fps main menu settled the run before its loading screen had
        // even begun - nor a slow second that breaks the streak.
        if (!locked && InMenu(_second))
        {
            MenuMs += _second.TotalMs;
            _second = new FrameStats();
            _secondClean = true;
            return;
        }

        if (_secondClean && !locked)
        {
            _streak.Add(_second);
            _cleanSeconds++;
        }
        else
        {
            // A slow frame breaks the streak: it and the streak before it were
            // still the loading screen.
            _loading.Add(_streak);
            _loading.Add(_second);
            _streak = new FrameStats();
            _cleanSeconds = 0;
        }
        _second = new FrameStats();
        _secondClean = true;

        if (_cleanSeconds >= SettleSeconds)
        {
            // Playing: the clean streak is play and counts, the loading is
            // dropped - but its length kept, so the log can say how much was
            // left out and a wrong guess shows.
            Add(_streak);
            LoadingMs += _loading.TotalMs;
            Settle();
        }
        else if (_loading.TotalMs + _streak.TotalMs >= GiveUpMs)
        {
            // Never settled: count it all rather than lose the session.
            Add(_loading);
            Add(_streak);
            Settle();
        }
    }

    /// <summary>
    /// The loading screen time left out of every figure, across every run
    /// added in - for the log's "loading 38 s". Zero when nothing was cut,
    /// or when a run never settled and so counted in full.
    /// </summary>
    public double LoadingMs { get; private set; }

    private void Settle()
    {
        _settled = true;
        _loading = _streak = _second = null;
    }

    private void Count(double frameMs)
    {
        int bin = Math.Min(Bins, (int)(frameMs / BinMs));
        _counts[bin]++;
        _sums[bin] += frameMs;

        Frames++;
        TotalMs += frameMs;

        // Not frames past the histogram's last bin: a frame of a second or
        // more is a loading screen, not frame pacing, and squared it swamps
        // everything else - 30 of them in 100,000 frames took the jitter from
        // under 2 ms to 17. The lows still count them in full.
        if (frameMs < Bins * BinMs)
        {
            _paced++;
            _pacedMs += frameMs;
            _squares += frameMs * frameMs;
        }
    }

    /// <summary>Adds another run's frames - a session interrupted by Alt+Tab is measured in several.</summary>
    public void Add(FrameStats other)
    {
        for (int i = 0; i <= Bins; i++)
        {
            _counts[i] += other._counts[i];
            _sums[i] += other._sums[i];
        }

        Frames += other.Frames;
        TotalMs += other.TotalMs;
        _paced += other._paced;
        _pacedMs += other._pacedMs;
        _squares += other._squares;
        LoadingMs += other.LoadingMs;
        MenuMs += other.MenuMs;

        // A run that ended - or is read - before it settled hands over what
        // it held back as well: a short session is all loading or all play,
        // and either way dropping it would leave nothing to show.
        if (!other._settled)
            foreach (var held in new[] { other._loading, other._streak, other._second })
                if (held is not null)
                    Add(held);

        // Likewise the part-second a locked run is still judging: a session
        // read or ended mid-second would otherwise lose up to a second of play.
        if (other._settled && other._playSecond is { } pending)
            Add(pending);
    }

    /// <summary>
    /// Whether frames count now - past the loading screen, and not at the
    /// game's loading lock or in a menu - for the timeline, which shades the rest.
    /// </summary>
    public bool Settled => _settled && !_atLockNow;

    /// <summary>Every frame added, counted or held back - for telling which chain is the game's.</summary>
    public long Seen => Frames + (_loading?.Frames ?? 0) + (_streak?.Frames ?? 0) + (_second?.Frames ?? 0) + (_playSecond?.Frames ?? 0);

    public FrameStats Copy()
    {
        var copy = new FrameStats();
        copy.Add(this);
        return copy;
    }

    /// <summary>The frame rate over every frame: frame times averaged, then inverted - not an average of rates.</summary>
    public double? AverageFps => Frames > 0 && TotalMs > 0 ? 1000.0 * Frames / TotalMs : null;

    /// <summary>The frame rate across the slowest 1% of the session's frames.</summary>
    /// <remarks>
    /// Over frames under 100 ms only, as the jitter is: a frame that long is a
    /// loading screen or a save, not a dip in play. Counted in full, 30 s of
    /// loading in a 10-minute Factorio session made up most of its slowest 1%
    /// and took a steady 60 fps to a "1% low" of 9 - and its score with it.
    /// The long frames still show in the 0.1% low and the stutter count.
    /// </remarks>
    public double? OnePercentLow => Low(0.01, OnePercentFloor, playOnly: true);

    /// <summary>The frame rate across the slowest 0.1% - single long frames, the hitches.</summary>
    public double? PointOnePercentLow => Low(0.001, PointOnePercentFloor, playOnly: false);

    /// <summary>
    /// How far a frame time typically is from the average, in milliseconds -
    /// the standard deviation. Low means every frame takes about as long as
    /// the next, which is what smooth feels like; the average alone can't say.
    /// Over frames under 100 ms only; see Add.
    /// </summary>
    public double? JitterMs
    {
        get
        {
            if (_paced < OnePercentFloor)
                return null;
            double mean = _pacedMs / _paced;
            return Math.Sqrt(Math.Max(0, _squares / _paced - mean * mean));
        }
    }

    /// <summary>
    /// How many frames were stutters: at least 2.5 times the session's median
    /// frame time, and at least 8 ms more than it - so at 120 fps a frame of
    /// 21 ms or more, at 60 fps one of 42 ms or more. The average and the lows
    /// can look fine while a game hitches every few seconds; this counts the
    /// hitches. The 8 ms floor is for high frame rates, where 2.5 times a
    /// 4 ms frame is a dip nobody feels.
    ///
    /// Frames past the histogram's last bin (100 ms and up) are left out:
    /// those are loading screens and hangs, which the 0.1% low already shows.
    /// From the histogram alone, so it is exact across merged runs and costs
    /// nothing per frame. Null below the 1% low's floor, like the lows.
    /// </summary>
    public long? Stutters
    {
        get
        {
            if (Frames < OnePercentFloor)
                return null;

            double median = Median();
            double threshold = Math.Max(2.5 * median, median + 8);

            // Whole bins from the one the threshold starts in, short of the
            // last; the bin it cuts through counts if its frames average above it.
            long stutters = 0;
            for (int i = (int)(threshold / BinMs); i < Bins; i++)
                if (_counts[i] > 0 && _sums[i] / _counts[i] >= threshold)
                    stutters += _counts[i];
            return stutters;
        }
    }

    /// <summary>
    /// The median frame time: the bin the middle frame falls in, by its
    /// frames' own average - within a quarter millisecond either way.
    /// </summary>
    private double Median()
    {
        long half = (Frames + 1) / 2, seen = 0;
        for (int i = 0; i <= Bins; i++)
        {
            seen += _counts[i];
            if (seen >= half)
                return _counts[i] > 0 ? _sums[i] / _counts[i] : (i + 0.5) * BinMs;
        }
        return 0;
    }

    /// <summary>The usual frame caps and refresh rates a cap is snapped to - 59.6 is a 60 cap.</summary>
    private static readonly int[] CommonCaps = { 30, 40, 45, 48, 50, 60, 72, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 280, 300, 360 };

    /// <summary>
    /// The frame rate the game was held at, when nearly every frame sat at
    /// one rate: 80% or more within 4% of the median frame time. That is a
    /// frame cap or V-Sync - Factorio's 60 - and a target above it can never
    /// be met however well the game runs, so the session history offers it
    /// as the game's own target. Snapped to a usual cap when within 3% of
    /// one. Null when the frame rate moved about, as an uncapped game's does.
    /// </summary>
    public int? CapFps
    {
        get
        {
            if (Frames < OnePercentFloor)
                return null;

            double median = Median();
            if (median <= 0)
                return null;

            long near = 0;
            for (int i = 0; i < Bins; i++)
                if (_counts[i] > 0 && Math.Abs(_sums[i] / _counts[i] - median) <= median * 0.04)
                    near += _counts[i];
            if (near < Frames * 0.8)
                return null;

            double fps = 1000 / median;
            int nearest = CommonCaps.MinBy(c => Math.Abs(c - fps));
            return Math.Abs(nearest - fps) <= nearest * 0.03 ? nearest : (int)Math.Round(fps);
        }
    }

    /// <summary>
    /// The slowest <paramref name="fraction"/> of frames, averaged as the
    /// widget's lows are (see FpsMonitor.LowAverage): their total time, taken
    /// from the slowest bins down, the last bin only in part.
    /// </summary>
    /// <param name="playOnly">Leave out the last bin - frames of 100 ms or more, see OnePercentLow.</param>
    private double? Low(double fraction, int floor, bool playOnly)
    {
        // The share is of the frames counted, so leaving the long ones out
        // doesn't make the slowest 1% reach further into normal play.
        long frames = playOnly ? Frames - _counts[Bins] : Frames;
        if (frames < floor)
            return null;

        long wanted = Math.Max(1, (long)Math.Round(fraction * frames));
        long taken = 0;
        double time = 0;

        for (int i = playOnly ? Bins - 1 : Bins; i >= 0 && taken < wanted; i--)
        {
            if (_counts[i] == 0)
                continue;

            long take = Math.Min(_counts[i], wanted - taken);
            time += _sums[i] * take / _counts[i];
            taken += take;
        }

        return time > 0 ? 1000.0 * taken / time : null;
    }
}
