using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using DeskMon.Ui;

namespace DeskMon.Notes;

/// <summary>
/// Personal achievements: badges for how you play, not for what you did in a
/// game - each with levels, Bronze to Platinum, and grouped by what they are
/// about.
///
/// Nothing new is recorded for them. Each is worked out from what DeskMon
/// keeps anyway - the session logs, the notes, the screenshots and the
/// playtime the widget shows - so the history from before they existed counts
/// too. What is kept, in achievements.json, is only when each level was
/// unlocked and whether it has been seen: an unlock is celebrated once, and
/// keeps its date even if the sessions that earned it are deleted later.
/// </summary>
internal static class Achievements
{
    /// <summary>The levels, lowest first. An achievement with fewer levels uses the first ones.</summary>
    public static readonly string[] LevelNames = { "Bronze", "Silver", "Gold", "Platinum" };

    /// <summary>The categories, in the window's order.</summary>
    public static readonly string[] Categories = { "Time played", "When you play", "Games", "Smooth play", "Notes and pictures" };

    /// <summary>
    /// How far an achievement has got: the figure now, and the way it got
    /// there - each step with when it happened - so every level can be dated
    /// by when it was really reached.
    /// </summary>
    /// <param name="Value">The figure now: hours, sessions, days. Can be more than the path reaches - Steam's hours have no dates.</param>
    /// <param name="Path">The figure as it grew, oldest first, never going down.</param>
    public sealed record Measure(double Value, IReadOnlyList<(DateTime When, double Value)> Path)
    {
        /// <summary>When the figure first reached <paramref name="target"/>, if the path says.</summary>
        public DateTime? ReachedAt(double target)
        {
            foreach (var (when, value) in Path)
                if (value >= target)
                    return when;
            return null;
        }

        public static readonly Measure None = new(0, Array.Empty<(DateTime, double)>());

        /// <summary>One for each of these moments - a count of things, each dated.</summary>
        public static Measure Count(IEnumerable<DateTime> moments)
        {
            var path = moments.OrderBy(t => t).Select((t, i) => (t, (double)(i + 1))).ToList();
            return new(path.Count, path);
        }
    }

    /// <param name="How">What a level takes, with {0} for its target: "Spend {0} hours in one game."</param>
    /// <param name="Unit">After the figures of the progress to the next level: "12 / 50 nights".</param>
    /// <param name="Levels">Each level's target, Bronze first; one for an achievement with a single level.</param>
    /// <param name="Once">What a target of one takes, said in the singular - "in 1 sessions" read as a slip.</param>
    public sealed record Achievement(string Id, string Name, string Category, string How, BadgeLook Badge,
                                     string Unit, double[] Levels, Func<Facts, Measure> Measure, string? Once = null)
    {
        public string HowFor(int level)
        {
            double target = Levels[Math.Clamp(level, 1, Levels.Length) - 1];
            return target == 1 && Once is not null
                ? Once
                : string.Format(CultureInfo.InvariantCulture, How, Figure(target));
        }

        /// <summary>
        /// The figure on a badge that has one - Century's - for the level
        /// shown: its target, "50" to "1K". It was a fixed "100" whatever the
        /// level, so Bronze's 50 and Platinum's 1000 both said 100. Locked
        /// (level 0), the first level's target.
        /// </summary>
        public string? BadgeLabel(int level)
        {
            if (Badge.Label is null)
                return null;
            double target = Levels[Math.Clamp(level, 1, Levels.Length) - 1];
            return target >= 1000
                ? (target / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "K"
                : Figure(target);
        }

        /// <summary>"Night Owl · Silver" - just the name for one with a single level.</summary>
        public string NameFor(int level) => Levels.Length == 1 ? Name : $"{Name} · {LevelNames[level - 1]}";
    }

    /// <summary>One achievement as a check found it: its measure, and the highest level reached (0 for none).</summary>
    public sealed record Result(Achievement Achievement, Measure Measure, int Level)
    {
        public bool Maxed => Level >= Achievement.Levels.Length;

        /// <summary>The target of the level after the one reached - the one being worked towards.</summary>
        public double NextTarget => Achievement.Levels[Math.Min(Level, Achievement.Levels.Length - 1)];
    }

    /// <summary>A level unlocked: for its card, and for the keys in achievements.json.</summary>
    public readonly record struct Unlock(Achievement Achievement, int Level)
    {
        public string Key => KeyFor(Achievement.Id, Level);
    }

    public static string KeyFor(string id, int level) => $"{id}:{level}";

    /// <summary>
    /// Everything the checks read, gathered once on a worker - reading every
    /// log and folder is disk work - and handed to all of them.
    /// </summary>
    /// <param name="Sessions">Every game's sessions, oldest first, each with its Game set.</param>
    /// <param name="Playtime">Each game's total, from the best source - what the widget's GAME heading shows.</param>
    /// <param name="NotesMade">When each game's notes were first made.</param>
    /// <param name="Screenshots">Each screenshot's game folder and when it was taken, oldest first.</param>
    public sealed record Facts(IReadOnlyList<SessionRecord> Sessions, IReadOnlyDictionary<string, TimeSpan> Playtime,
                               IReadOnlyList<DateTime> NotesMade, IReadOnlyList<(string Folder, DateTime When)> Screenshots,
                               int SessionNotes);

    // --- The achievements --------------------------------------------------------
    //
    // In the window's order, category by category. Each badge has a shape, a
    // colour pair and a symbol of its own, so no two look alike - see
    // Ui/Badges; the level shows as the badge's rim.

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private const string Time = "Time played", When = "When you play", Games = "Games",
                         Smooth = "Smooth play", Notes = "Notes and pictures";

    public static readonly IReadOnlyList<Achievement> All = new Achievement[]
    {
        // Time played
        new("hours-in", "Hours In", Time, "Spend {0} hours in one game.",
            new(BadgeShape.Hexagon, C("#FFF5C542"), C("#FFB7791F"), Glyphs.Hourglass), "h",
            new double[] { 100, 250, 500, 1000 }, HoursInOneGame),
        new("lifetime", "Lifetime", Time, "Spend {0} hours playing, across every game.",
            new(BadgeShape.Hexagon, C("#FFFDE68A"), C("#FFCA8A04"), Glyphs.Crown), "h",
            new double[] { 250, 1000, 2500, 5000 }, HoursInAll),
        new("marathon", "Marathon", Time, "Play one session of {0} hours or more.",
            new(BadgeShape.Shield, C("#FFFB923C"), C("#FFC2410C"), Glyphs.Stopwatch), "h",
            new double[] { 3, 4, 6, 8 }, Longest),
        new("century", "Century", Time, "Play {0} sessions in all.",
            new(BadgeShape.Octagon, C("#FFE2E8F0"), C("#FF64748B"), Glyphs.Laurel, Label: "100"), "sessions",
            new double[] { 50, 100, 500, 1000 }, f => Measure.Count(f.Sessions.Select(s => s.End))),

        // When you play
        new("night-owl", "Night Owl", When, "Play past midnight in {0} sessions.",
            new(BadgeShape.Circle, C("#FF4F5BD5"), C("#FF1E1B4B"), Glyphs.Moon), "nights",
            new double[] { 1, 10, 50, 100 }, f => Sessions(f, s => Touches(s, 0, 4)), "Play past midnight."),
        new("early-bird", "Early Bird", When, "Start {0} sessions between 5 and 8 in the morning.",
            new(BadgeShape.Octagon, C("#FFFDE047"), C("#FFEA8A0C"), Glyphs.Sunrise), "mornings",
            new double[] { 1, 10, 25, 50 }, f => Sessions(f, s => s.Start.Hour is >= 5 and < 8), "Start a session between 5 and 8 in the morning."),
        new("weekend-warrior", "Weekend Warrior", When, "Play on both the Saturday and the Sunday of {0} weekends.",
            new(BadgeShape.Shield, C("#FFF87171"), C("#FF991B1B"), Glyphs.Swords), "weekends",
            new double[] { 1, 5, 20, 52 }, Weekends, "Play on both the Saturday and the Sunday of one weekend."),
        new("on-a-roll", "On a Roll", When, "Play {0} days in a row.",
            new(BadgeShape.Diamond, C("#FFFF8A4C"), C("#FFDC2626"), Glyphs.Flame), "days",
            new double[] { 3, 7, 14, 30 }, Streak),
        new("regular", "Regular", When, "Play on {0} different days.",
            new(BadgeShape.Square, C("#FF93C5FD"), C("#FF1D4ED8"), Glyphs.Calendar), "days",
            new double[] { 30, 100, 250, 365 }, f => Measure.Count(f.Sessions.GroupBy(s => s.Start.Date).Select(d => d.Min(s => s.End)))),
        new("comeback", "Comeback", When, "Go back to a game after 90 days or more away, {0} times.",
            new(BadgeShape.Circle, C("#FFBEF264"), C("#FF4D7C0F"), Glyphs.Return), "returns",
            new double[] { 1, 3, 5, 10 }, f => Comebacks(f, TimeSpan.FromDays(90)), "Go back to a game after 90 days or more away."),

        // Games
        new("explorer", "Explorer", Games, "Play {0} different games.",
            new(BadgeShape.Circle, C("#FF22D3EE"), C("#FF0E7490"), Glyphs.Compass), "games",
            new double[] { 5, 10, 25, 50 }, Explorer),
        new("loyal", "Loyal", Games, "Play {0} sessions of one game.",
            new(BadgeShape.Hexagon, C("#FFF472B6"), C("#FFBE185D"), Glyphs.Heart), "sessions",
            new double[] { 10, 25, 50, 100 }, Loyal),
        new("variety-night", "Variety Night", Games, "Play {0} different games in one day.",
            new(BadgeShape.Square, C("#FFF0ABFC"), C("#FFA21CAF"), Glyphs.Dice), "games",
            new double[] { 3, 4, 5, 6 }, Variety),

        // Smooth play
        new("silky-smooth", "Silky Smooth", Smooth, "Score 95 or more in {0} sessions of 30 minutes or longer.",
            new(BadgeShape.Square, C("#FFA78BFA"), C("#FF6D28D9"), Glyphs.Wave), "sessions",
            new double[] { 1, 10, 50, 100 },
            f => Sessions(f, s => s.Length >= TimeSpan.FromMinutes(30) && PerformanceScore.For(s)?.Score >= 95), "Score 95 or more in a session of 30 minutes or longer."),
        new("cool-head", "Cool Head", Smooth, "Keep CPU and GPU at 70 °C or under through {0} sessions of an hour or more.",
            new(BadgeShape.Octagon, C("#FFBAE6FD"), C("#FF0284C7"), Glyphs.Snowflake), "sessions",
            new double[] { 1, 10, 25, 50 },
            f => Sessions(f, s => s.Length >= Hour && s.Find("CPU", "°C")?.Number is <= 70 && s.Find("GPU", "°C")?.Number is <= 70), "Keep CPU and GPU at 70 °C or under through a session of an hour or more."),
        new("clean-line", "Clean Line", Smooth, "Play {0} sessions of an hour or more with the ping under 20 ms and no pings lost.",
            new(BadgeShape.Diamond, C("#FF86EFAC"), C("#FF16A34A"), Glyphs.Signal), "sessions",
            new double[] { 1, 10, 25, 50 },
            f => Sessions(f, s => s.Length >= Hour && s.PingMs < 20 && s.PingLossPercent == 0), "Play an hour or more with the ping under 20 ms and no pings lost."),
        new("eco-mode", "Eco Mode", Smooth, "Play {0} sessions of an hour or more with CPU and GPU together under 150 W.",
            new(BadgeShape.Diamond, C("#FF6EE7B7"), C("#FF047857"), Glyphs.Leaf), "sessions",
            new double[] { 1, 10, 25, 50 },
            f => Sessions(f, s => s.Length >= Hour && s.EnergyWh is double wh && wh / s.Length.TotalHours < 150), "Play an hour or more with CPU and GPU together under 150 W."),
        new("stutter-free", "Stutter Free", Smooth, "Play {0} sessions of an hour or more without a single stutter.",
            new(BadgeShape.Circle, C("#FF67E8F9"), C("#FF0891B2"), Glyphs.Pulse), "sessions",
            new double[] { 1, 5, 20, 50 },
            f => Sessions(f, s => s.Length >= Hour && s.Stutters == 0), "Play an hour or more without a single stutter."),
        new("high-refresh", "High Refresh", Smooth, "Average 144 fps or more in {0} sessions of an hour or longer.",
            new(BadgeShape.Shield, C("#FFFDA4AF"), C("#FFE11D48"), Glyphs.Bolt), "sessions",
            new double[] { 1, 10, 25, 50 },
            f => Sessions(f, s => s.Length >= Hour && s.AverageFps >= 144), "Average 144 fps or more through a session of an hour or longer."),

        // Notes and pictures
        new("first-steps", "First Steps", Notes, "Finish your first recorded session.",
            new(BadgeShape.Circle, C("#FF4ADE80"), C("#FF15803D"), Glyphs.Flag), "",
            new double[] { 1 }, f => Measure.Count(f.Sessions.Take(1).Select(s => s.End))),
        new("collector", "Collector", Notes, "Write notes for {0} games.",
            new(BadgeShape.Square, C("#FF2DD4BF"), C("#FF0F766E"), Glyphs.Pages), "notes",
            new double[] { 10, 25, 50, 100 }, f => Measure.Count(f.NotesMade)),
        new("shutterbug", "Shutterbug", Notes, "Take {0} screenshots.",
            new(BadgeShape.Circle, C("#FFFBBF24"), C("#FFB45309"), Glyphs.Camera), "pictures",
            new double[] { 25, 100, 250, 1000 }, f => Measure.Count(f.Screenshots.Select(p => p.When))),
        new("storyteller", "Storyteller", Notes, "Write a note after {0} sessions, in the card that asks as a game ends.",
            new(BadgeShape.Shield, C("#FFD6A26B"), C("#FF7C4A1E"), Glyphs.Quill), "notes",
            new double[] { 5, 10, 25, 50 }, f => new(f.SessionNotes, Array.Empty<(DateTime, double)>())),
        new("photo-album", "Photo Album", Notes, "Take screenshots in {0} different games.",
            new(BadgeShape.Octagon, C("#FFC4B5FD"), C("#FF7C3AED"), Glyphs.Album), "games",
            new double[] { 3, 5, 10, 25 }, PhotoAlbum),
    };

    /// <summary>Every level of every achievement - the count over the window's badges.</summary>
    public static int LevelCount => All.Sum(a => a.Levels.Length);

    // --- Player level --------------------------------------------------------------
    //
    // One level for the player, worked out from the tiers unlocked, shown big
    // in the window's corner. Points rather than a count of tiers: a Platinum
    // takes far longer than a Bronze, and counting them the same made the
    // last tiers worth no more than the first.

    /// <summary>What each tier is worth, Bronze first; an achievement with a single level is worth one.</summary>
    private static readonly int[] TierPoints = { 1, 2, 3, 5 };

    /// <summary>The points one achievement gives at this level - its tiers so far added up.</summary>
    public static int PointsFor(Achievement achievement, int level)
        => achievement.Levels.Length == 1
            ? (level > 0 ? 1 : 0)
            : TierPoints.Take(Math.Clamp(level, 0, TierPoints.Length)).Sum();

    /// <summary>Every tier of every achievement - 254 with the twenty-four.</summary>
    public static int MaxPoints => All.Sum(a => PointsFor(a, a.Levels.Length));

    /// <summary>The highest player level: reached only with every tier unlocked.</summary>
    public const int TopLevel = 20;

    /// <summary>
    /// The points player level <paramref name="level"/> starts at. Each level
    /// takes a little more than the one before - level 2 at 5 points - and
    /// the curve is fitted so <see cref="TopLevel"/> lands exactly on
    /// <see cref="MaxPoints"/>: levels come quickly at first, when a single
    /// evening unlocks several Bronzes, and slowly at the end.
    /// </summary>
    public static int PointsForLevel(int level)
    {
        double steps = Math.Clamp(level, 1, TopLevel) - 1;
        double last = TopLevel - 1;
        double curve = (MaxPoints - 5 * last) / (last * last - last);   // the growth that ends on MaxPoints
        return (int)Math.Round(curve * steps * steps + (5 - curve) * steps);
    }

    /// <summary>The player level these points make, 1 to <see cref="TopLevel"/>.</summary>
    public static int PlayerLevel(int points)
    {
        int level = 1;
        while (level < TopLevel && points >= PointsForLevel(level + 1))
            level++;
        return level;
    }

    private static readonly string[] RankNames =
        { "Rookie", "Regular", "Enthusiast", "Veteran", "Expert", "Elite", "Master", "Champion", "Legend", "Mythic" };

    /// <summary>The rank a player level carries - a new one every two levels, Rookie to Mythic.</summary>
    public static string RankName(int level) => RankNames[Math.Clamp((level - 1) / 2, 0, RankNames.Length - 1)];

    /// <summary>The metal the rank emblem takes: bronze for levels 1-5, silver to 10, gold to 15, platinum after.</summary>
    public static int RankTier(int level) => Math.Clamp((level - 1) / 5 + 1, 1, TierPoints.Length);

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Whole numbers as they are, others to one decimal: "63", "2.5".</summary>
    public static string Figure(double value)
        => value.ToString(Math.Abs(value - Math.Round(value)) < 0.05 ? "0" : "0.0", CultureInfo.InvariantCulture);

    // --- The measures ------------------------------------------------------------

    /// <summary>The sessions that do it, each counted when it ended.</summary>
    private static Measure Sessions(Facts facts, Func<SessionRecord, bool> does)
        => Measure.Count(facts.Sessions.Where(does).Select(s => s.End));

    /// <summary>
    /// A figure that is the best of something so far - the longest session,
    /// the longest streak - as a path that only steps up when it is beaten.
    /// </summary>
    private sealed class Best
    {
        private readonly List<(DateTime, double)> _path = new();
        public double Value { get; private set; }

        public void Offer(DateTime when, double value)
        {
            if (value <= Value)
                return;
            Value = value;
            _path.Add((when, value));
        }

        public Measure ToMeasure(double? now = null) => new(Math.Max(now ?? 0, Value), _path);
    }

    /// <summary>
    /// Whether any minute of the session fell between these hours - "past
    /// midnight" is a session still going at 00:30, not only one started then.
    /// </summary>
    private static bool Touches(SessionRecord session, int fromHour, int toHour)
    {
        for (var day = session.Start.Date; day <= session.End.Date; day = day.AddDays(1))
        {
            var from = day.AddHours(fromHour);
            var to = day.AddHours(toHour);
            if (session.Start < to && session.End > from)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The most hours in one game. The figure is the playtime the widget
    /// shows, which may be Steam's or GOG's own count; the dates come from
    /// DeskMon's logs, so a level reached on Steam's hours alone is dated when
    /// DeskMon first saw it.
    /// </summary>
    private static Measure HoursInOneGame(Facts facts)
    {
        var best = new Best();
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in facts.Sessions)
        {
            string game = s.Game ?? string.Empty;
            totals[game] = totals.GetValueOrDefault(game) + s.Length.TotalHours;
            best.Offer(s.End, totals[game]);
        }
        return best.ToMeasure(facts.Playtime.Count == 0 ? 0 : facts.Playtime.Values.Max(t => t.TotalHours));
    }

    /// <summary>All the hours, every game - Steam's and GOG's counts where they are known, as for one game.</summary>
    private static Measure HoursInAll(Facts facts)
    {
        var best = new Best();
        double total = 0;
        foreach (var s in facts.Sessions)
        {
            total += s.Length.TotalHours;
            best.Offer(s.End, total);
        }
        return best.ToMeasure(facts.Playtime.Values.Sum(t => t.TotalHours));
    }

    private static Measure Longest(Facts facts)
    {
        var best = new Best();
        foreach (var s in facts.Sessions)
            best.Offer(s.End, s.Length.TotalHours);
        return best.ToMeasure();
    }

    /// <summary>Weekends with a session on the Saturday and one on the Sunday right after it, each dated by the Sunday.</summary>
    private static Measure Weekends(Facts facts)
    {
        var days = facts.Sessions.Select(s => s.Start.Date).ToHashSet();
        return Measure.Count(facts.Sessions
            .Where(s => s.Start.DayOfWeek == DayOfWeek.Sunday && days.Contains(s.Start.Date.AddDays(-1)))
            .GroupBy(s => s.Start.Date)
            .Select(sunday => sunday.Min(s => s.End)));
    }

    /// <summary>The longest run of days in a row with a session started on each.</summary>
    private static Measure Streak(Facts facts)
    {
        var best = new Best();
        int run = 0;
        DateTime? previous = null;
        foreach (var day in facts.Sessions.GroupBy(s => s.Start.Date).OrderBy(d => d.Key))
        {
            run = previous is { } p && day.Key == p.AddDays(1) ? run + 1 : 1;
            previous = day.Key;
            best.Offer(day.Min(s => s.End), run);
        }
        return best.ToMeasure();
    }

    /// <summary>Sessions of a game whose last one had ended at least this long before.</summary>
    private static Measure Comebacks(Facts facts, TimeSpan away)
    {
        var last = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var returns = new List<DateTime>();
        foreach (var s in facts.Sessions)
        {
            string game = s.Game ?? string.Empty;
            if (last.TryGetValue(game, out var ended) && s.Start - ended >= away)
                returns.Add(s.End);
            last[game] = s.End;
        }
        return Measure.Count(returns);
    }

    private static Measure Explorer(Facts facts)
        => Measure.Count(facts.Sessions.GroupBy(s => s.Game ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                                       .Select(g => g.First().End));

    private static Measure Loyal(Facts facts)
    {
        var best = new Best();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in facts.Sessions)
        {
            string game = s.Game ?? string.Empty;
            counts[game] = counts.GetValueOrDefault(game) + 1;
            best.Offer(s.End, counts[game]);
        }
        return best.ToMeasure();
    }

    /// <summary>The most different games in one day, dated by the session that made the number.</summary>
    private static Measure Variety(Facts facts)
    {
        var best = new Best();
        foreach (var day in facts.Sessions.GroupBy(s => s.Start.Date))
        {
            var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in day)
                if (games.Add(s.Game ?? string.Empty))
                    best.Offer(s.End, games.Count);
        }
        return best.ToMeasure();
    }

    /// <summary>Games with screenshots, each counted from its first picture.</summary>
    private static Measure PhotoAlbum(Facts facts)
        => Measure.Count(facts.Screenshots.GroupBy(p => p.Folder, StringComparer.OrdinalIgnoreCase)
                                          .Select(g => g.Min(p => p.When)));

    // --- Gathering ---------------------------------------------------------------

    /// <summary>
    /// Reads what the checks need. On a worker: every game's log, the notes
    /// folder and the screenshots - and the playtime, which may read Steam's
    /// and GOG's own files.
    /// </summary>
    /// <param name="from">
    /// Only what happened since - set by CLEAR ALL, which starts the
    /// achievements over (see Book.CountFrom). The hours are then DeskMon's
    /// own logs from then on: Steam's and GOG's totals can't be split by date.
    /// </param>
    public static Facts Gather(SessionHistory history, NotesStore notes, AppSettings settings, int sessionNotes,
                               DateTime? from = null)
    {
        var sessions = new List<SessionRecord>();
        var playtime = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (string game in history.Games())
        {
            var own = from is { } since
                ? history.Sessions(game).Where(s => s.Start >= since).ToList()
                : history.Sessions(game);
            sessions.AddRange(own);
            try
            {
                var logged = TimeSpan.FromTicks(own.Sum(s => s.Length.Ticks));
                playtime[game] = from is null
                    ? GamePlaytime.Look(game, settings, history, logged: logged).Playtime.Time
                    : logged;
            }
            catch (Exception ex)
            {
                // One game's playtime source failing mustn't cost the rest.
                App.Log($"reading {game}'s playtime for the achievements", ex);
            }
        }
        sessions.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Notes for games only: a game with a session log, or one Steam or GOG
        // ran - by its file name, as the notes are listed. Not General, which
        // is everyone's, nor a note of your own from NEW NOTE: Collector says
        // "notes for games", and those were counted as games. Since a clear,
        // only notes whose file was made after it.
        var games = history.Games().Concat(GamePlaytime.RememberedGames(settings))
            .Select(NotesStore.SafeFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notesMade = notes.ListGames()
            .Where(name => games.Contains(name)
                           && !string.Equals(name, NotesStore.GeneralNotes, StringComparison.OrdinalIgnoreCase))
            .Select(name => MadeAt(notes, name))
            .Where(made => from is not { } since || made >= since)
            .ToList();

        var screenshots = Screenshots(notes);
        if (from is { } after)
            screenshots.RemoveAll(p => p.When < after);

        return new(sessions, playtime, notesMade, screenshots, sessionNotes);
    }

    /// <summary>When a game's notes file was first made - the .rtf, or an old .txt not yet carried over.</summary>
    private static DateTime MadeAt(NotesStore notes, string game)
    {
        try
        {
            string rtf = notes.PathFor(game);
            return File.GetCreationTime(File.Exists(rtf) ? rtf : notes.LegacyPathFor(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;   // unreadable counts as old: it was there before a clear, as far as can be told
        }
    }

    /// <summary>
    /// Every screenshot under notes\screenshots, with its game's folder and
    /// when it was taken: from its name (2026-10-17_210412.png), or the file's
    /// own time for one named otherwise. Pictures deleted since don't count -
    /// only what is there.
    /// </summary>
    private static List<(string Folder, DateTime When)> Screenshots(NotesStore notes)
    {
        var pictures = new List<(string, DateTime)>();
        string root = Path.GetDirectoryName(notes.ScreenshotFolderFor("x"))!;
        if (!Directory.Exists(root))
            return pictures;

        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg"))
                    continue;
                var taken = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd_HHmmss",
                                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var named)
                    ? named
                    : File.GetLastWriteTime(file);
                // The game is the folder right under screenshots - whatever
                // lies deeper still belongs to that game.
                string relative = Path.GetRelativePath(root, file);
                int cut = relative.IndexOfAny(new[] { '\\', '/' });
                pictures.Add((cut > 0 ? relative[..cut] : string.Empty, taken));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log("counting screenshots for the achievements", ex);
        }

        pictures.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return pictures;
    }

    // --- Checking ----------------------------------------------------------------

    /// <summary>Every achievement's measure and level; one failing leaves it at no level rather than stopping the rest.</summary>
    public static List<Result> Check(Facts facts)
    {
        var results = new List<Result>();
        foreach (var achievement in All)
        {
            Measure measure;
            try
            {
                measure = achievement.Measure(facts);
            }
            catch (Exception ex)
            {
                App.Log($"checking the {achievement.Name} achievement", ex);
                measure = Measure.None;
            }
            int level = achievement.Levels.Count(target => measure.Value >= target);
            results.Add(new(achievement, measure, level));
        }
        return results;
    }

    // --- What is kept ------------------------------------------------------------

    /// <summary>
    /// achievements.json: when each level was unlocked ("night-owl:2"), which
    /// have been seen, and the one count no other file keeps - session notes
    /// written in the card. UI thread only; written on AtomicFile's writer, as
    /// settings are.
    /// </summary>
    public sealed class Book
    {
        public Dictionary<string, DateTime> Unlocked { get; set; } = new();
        public HashSet<string> Seen { get; set; } = new();

        /// <summary>The first check has run: from then on an unlock is new, before it was history.</summary>
        public bool Seeded { get; set; }

        /// <summary>Session notes saved from the card since achievements came in - the old ones are inside the notes, not counted.</summary>
        public int SessionNotes { get; set; }

        /// <summary>
        /// Set by CLEAR ALL: only what happened after this counts, so the
        /// achievements can be earned again from nothing. Null - the default -
        /// counts the whole history.
        /// </summary>
        public DateTime? CountFrom { get; set; }

        /// <summary>The categories folded in the window, by name - kept here, with the rest of the window's own state, so they stay folded.</summary>
        public HashSet<string> Folded { get; set; } = new();

        /// <summary>
        /// The player level the window last showed: one above it plays the
        /// emblem's level-up the next time the window is on screen, however
        /// long after the level was reached.
        /// </summary>
        public int LevelSeen { get; set; }

        /// <summary>
        /// The points every tier unlocked is worth - see PointsFor. A method,
        /// not a property: a property would be written into the file too.
        /// </summary>
        public int PointsNow() => All.Sum(a => PointsFor(a, LevelOf(a)));

        public static string FilePath => Path.Combine(DataFolder.Root, "achievements.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        /// <summary>
        /// The book, or null when the file is there but can't be read right now
        /// - held by antivirus or a backup. Every failure used to come back as
        /// a fresh book, which the next check saved over the real file: a
        /// CLEAR ALL undone, the Storyteller count and the dates gone. Null
        /// tells the caller to leave it and try again later.
        ///
        /// Only a file that reads but isn't a book - broken, not busy - starts
        /// afresh; it is kept beside it as .broken first, so nothing is lost
        /// without a trace.
        /// </summary>
        public static Book? Load()
        {
            string text;
            try
            {
                if (!File.Exists(FilePath))
                    return new Book();
                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log("reading achievements.json - trying again later", ex);
                return null;
            }

            try
            {
                if (JsonSerializer.Deserialize<Book>(text, Options) is { } book)
                {
                    book.Unlocked ??= new();
                    book.Seen ??= new();
                    book.Folded ??= new();
                    return book;
                }
            }
            catch (JsonException ex)
            {
                App.Log("reading achievements.json - it is broken, so it is kept as .broken and started afresh", ex);
                try
                {
                    File.Copy(FilePath, FilePath + ".broken", overwrite: true);
                }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
                {
                    App.Log("keeping the broken achievements.json", copy);
                }
            }
            return new Book();
        }

        public void SaveLater()
        {
            string json = JsonSerializer.Serialize(this, Options);
            AtomicFile.Later(() =>
            {
                try
                {
                    AtomicFile.WriteAllText(FilePath, json);
                }
                catch (Exception ex)
                {
                    App.Log("writing achievements.json", ex);
                }
            });
        }

        /// <summary>The highest level unlocked of this achievement, 0 for none.</summary>
        public int LevelOf(Achievement achievement)
        {
            for (int level = achievement.Levels.Length; level > 0; level--)
                if (Unlocked.ContainsKey(KeyFor(achievement.Id, level)))
                    return level;
            return 0;
        }

        /// <summary>
        /// Takes in a check's results and returns the levels unlocked by it
        /// that are new, lowest first. Each level is dated by when its target
        /// was really reached, where the data says. The very first time,
        /// everything already earned is taken in quietly - marked seen - so a
        /// long history doesn't arrive as a burst of cards.
        /// </summary>
        public List<Unlock> Take(IReadOnlyList<Result> results)
        {
            var fresh = new List<Unlock>();
            bool added = false;
            foreach (var result in results)
            {
                for (int level = 1; level <= result.Level; level++)
                {
                    var unlock = new Unlock(result.Achievement, level);
                    if (Unlocked.ContainsKey(unlock.Key))
                        continue;

                    added = true;
                    Unlocked[unlock.Key] = result.Measure.ReachedAt(result.Achievement.Levels[level - 1]) ?? DateTime.Now;
                    if (Seeded)
                        fresh.Add(unlock);
                    else
                        Seen.Add(unlock.Key);
                }
            }

            // Written only when something changed: this runs after every
            // session, and the same file again each time would be churn.
            bool changed = added || !Seeded;
            Seeded = true;
            if (changed)
                SaveLater();
            return fresh;
        }

        /// <summary>
        /// Starts over: every level locked, the session-note count back to
        /// nothing, and counting from now. Still seeded, so what is earned
        /// from here on is new and gets its card. The sessions, notes and
        /// pictures themselves are not touched.
        /// </summary>
        public void Clear()
        {
            Unlocked.Clear();
            Seen.Clear();
            SessionNotes = 0;
            CountFrom = DateTime.Now;
            Seeded = true;
            LevelSeen = 1;   // back at the bottom - no level-up for falling to it; the folds stay as they were
            SaveLater();
        }
    }
}
