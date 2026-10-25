using System;

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

    public void Add(double frameMs)
    {
        if (frameMs <= 0 || double.IsNaN(frameMs))
            return;

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
    }

    public FrameStats Copy()
    {
        var copy = new FrameStats();
        copy.Add(this);
        return copy;
    }

    /// <summary>The frame rate over every frame: frame times averaged, then inverted - not an average of rates.</summary>
    public double? AverageFps => Frames > 0 && TotalMs > 0 ? 1000.0 * Frames / TotalMs : null;

    /// <summary>The frame rate across the slowest 1% of the session's frames.</summary>
    public double? OnePercentLow => Low(0.01, OnePercentFloor);

    /// <summary>The frame rate across the slowest 0.1% - single long frames, the hitches.</summary>
    public double? PointOnePercentLow => Low(0.001, PointOnePercentFloor);

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

            // The median: the bin the middle frame falls in, by its frames'
            // own average - within a quarter millisecond either way.
            long half = (Frames + 1) / 2, seen = 0;
            double median = 0;
            for (int i = 0; i <= Bins; i++)
            {
                seen += _counts[i];
                if (seen >= half)
                {
                    median = _counts[i] > 0 ? _sums[i] / _counts[i] : (i + 0.5) * BinMs;
                    break;
                }
            }

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
    /// The slowest <paramref name="fraction"/> of frames, averaged as the
    /// widget's lows are (see FpsMonitor.LowAverage): their total time, taken
    /// from the slowest bins down, the last bin only in part.
    /// </summary>
    private double? Low(double fraction, int floor)
    {
        if (Frames < floor)
            return null;

        long wanted = Math.Max(1, (long)Math.Round(fraction * Frames));
        long taken = 0;
        double time = 0;

        for (int i = Bins; i >= 0 && taken < wanted; i--)
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
