using System;
using System.Linq;

namespace DeskMon.Notes;

/// <summary>Where a playtime figure came from - said beside it, since they can differ a lot.</summary>
public enum PlaytimeSource { Steam, Gog, Saves, Logged }

public readonly record struct Playtime(TimeSpan Time, PlaytimeSource Source);

/// <summary>
/// How long a game has been played in all, from the best source there is:
/// Steam's own count, else GOG Galaxy's, else the game's save files, else
/// DeskMon's logs.
///
/// One place for the choice, so the widget's GAME heading and the session
/// history always show the same figure. The sources replace each other and
/// are never added together: Steam, Galaxy and the saves have counted every
/// session DeskMon logged as well, so a sum would count those hours twice.
/// </summary>
public static class GamePlaytime
{
    /// <param name="pid">
    /// The game's process, when it is running: the surest way to tell which
    /// Steam app it is. Without one - the history - the app id remembered from
    /// the last time it ran is used, then a match on the name.
    /// </param>
    /// <param name="logged">
    /// DeskMon's own total, when the caller has just read the log anyway - the
    /// history has - so it isn't read a second time for the fallback.
    /// </param>
    public static Playtime For(string game, AppSettings settings, SessionHistory history,
                               int? pid = null, TimeSpan? logged = null)
    {
        var found = Look(game, settings, history, pid, logged);
        Remember(game, settings, found.SteamFromProcess, found.GogFromProcess);
        return found.Playtime;
    }

    /// <summary>
    /// What <see cref="For"/> answers, plus the ids found from the running
    /// process - without writing them to the settings.
    ///
    /// Split out so a game's start can look this up on a worker thread: with
    /// a process to go by it parses every Steam manifest in the game's
    /// library, queries Galaxy's database and reads save files, and on the UI
    /// thread that froze the widget and every DeskMon window just as the game
    /// launched. The settings are the UI thread's, so the ids go back there,
    /// to <see cref="Remember"/>, rather than being written from the worker.
    /// </summary>
    public static (Playtime Playtime, string? SteamFromProcess, string? GogFromProcess) Look(
        string game, AppSettings settings, SessionHistory history, int? pid = null, TimeSpan? logged = null)
    {
        // A game running out of a Galaxy install is a GOG game, whatever
        // Steam might also have under the same name - checked before Steam
        // for that reason. Otherwise Steam first, as it has always been.
        bool gogRunning = false;
        string? gogKey = settings.GogPlaytime ? GogKey(game, settings, pid, out gogRunning) : null;
        string? gogFromProcess = gogRunning ? gogKey : null;
        if (gogKey is not null && gogRunning && GogPlaytime.ForKey(gogKey) is { } runningGog)
            return (new Playtime(runningGog, PlaytimeSource.Gog), null, gogFromProcess);

        string? steamFromProcess = null;
        string? appId = settings.SteamPlaytime ? SteamAppId(game, settings, pid, out steamFromProcess) : null;
        if (appId is not null && SteamPlaytime.ForApp(appId) is { } steam)
            return (new Playtime(steam, PlaytimeSource.Steam), steamFromProcess, gogFromProcess);

        if (gogKey is not null && GogPlaytime.ForKey(gogKey) is { } gog)
            return (new Playtime(gog, PlaytimeSource.Gog), steamFromProcess, gogFromProcess);

        if (SavePlaytime.For(game, settings.PlaytimeFromSaves) is { } saves)
            return (new Playtime(saves, PlaytimeSource.Saves), steamFromProcess, gogFromProcess);

        return (new Playtime(logged ?? TimeSpan.FromTicks(history.Sessions(game).Sum(s => s.Length.Ticks)),
                             PlaytimeSource.Logged),
                steamFromProcess, gogFromProcess);
    }

    /// <summary>
    /// Keeps the ids found from a running game for the history, which has no
    /// process to go by and can't always find the game by name: an executable
    /// may call itself "The Witcher 3" while Steam lists "The Witcher 3: Wild
    /// Hunt". On the UI thread - it writes the settings. One save for both,
    /// and none when nothing new was found.
    /// </summary>
    public static void Remember(string game, AppSettings settings, string? steamAppId, string? gogKey)
    {
        bool changed = false;

        if (steamAppId is not null
            && (!settings.SteamAppIds.TryGetValue(game, out string? knownSteam) || knownSteam != steamAppId))
        {
            settings.SteamAppIds[game] = steamAppId;
            changed = true;
        }

        if (gogKey is not null
            && (!settings.GogGames.TryGetValue(game, out string? knownGog) || knownGog != gogKey))
        {
            settings.GogGames[game] = gogKey;
            changed = true;
        }

        if (changed)
            settings.Save();
    }

    /// <param name="fromProcess">The id when it was found from the running process - for <see cref="Remember"/>.</param>
    private static string? SteamAppId(string game, AppSettings settings, int? pid, out string? fromProcess)
    {
        fromProcess = pid is int running ? SteamPlaytime.AppIdForProcess(running) : null;
        if (fromProcess is not null)
            return fromProcess;

        return RememberedSteamAppId(game, settings) ?? SteamPlaytime.AppIdForName(game);
    }

    /// <summary>The app id saved the last time the game ran from Steam, or null.</summary>
    internal static string? RememberedSteamAppId(string game, AppSettings settings)
        => settings.SteamAppIds
            .FirstOrDefault(entry => string.Equals(entry.Key, game, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// The GOG Galaxy game this is, found the way <see cref="SteamAppId"/>
    /// finds a Steam one: from the running process's folder - remembered for
    /// the history, by <see cref="Remember"/> - else what was remembered, else
    /// the name.
    /// </summary>
    /// <param name="running">Whether it was found from the running process, which makes it surely the GOG copy.</param>
    private static string? GogKey(string game, AppSettings settings, int? pid, out bool running)
    {
        running = false;

        if (pid is int process && GogPlaytime.KeyForProcess(process) is { } found)
        {
            running = true;
            return found;
        }

        var remembered = settings.GogGames
            .FirstOrDefault(entry => string.Equals(entry.Key, game, StringComparison.OrdinalIgnoreCase)).Value;

        return remembered ?? GogPlaytime.KeyForName(game);
    }

    /// <summary>
    /// Hours to one decimal, as in "13.3 hours" - and minutes below an hour,
    /// where "0.2 hours" would make you do the sum. Full stop as the decimal
    /// mark whatever the Windows region, like every other number on the widget.
    /// </summary>
    public static string Describe(TimeSpan played)
        => played.TotalHours >= 1
            ? played.TotalHours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " hours"
            : $"{Math.Max(0, (int)played.TotalMinutes)} min";

    /// <summary>"on Steam", for "143.4 hours on Steam".</summary>
    public static string Where(PlaytimeSource source) => source switch
    {
        PlaytimeSource.Steam => "on Steam",
        PlaytimeSource.Gog => "on GOG Galaxy",
        PlaytimeSource.Saves => "from the game's saves",
        _ => "logged by DeskMon",
    };
}
