using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskMon.Notes;

/// <summary>
/// How long GOG Galaxy says a game has been played - the figure on its
/// library page - read from Galaxy's own database on this PC, with no network
/// and no login. The GOG counterpart of <see cref="SteamPlaytime"/>.
///
/// Galaxy keeps everything in one SQLite file,
/// %ProgramData%\GOG.com\Galaxy\storage\galaxy-2.0.db:
///   GameTimes             minutes played, per game ("gog_1238653230")
///   GamePieces            the game's title, as JSON: {"title":"Factorio"}
///   InstalledBaseProducts where each installed game is (E:\Games\Factorio)
/// A running game is told by the folder its executable is in; a game that
/// isn't running, by the id remembered from when it last ran, else its title.
///
/// Null for anything Galaxy doesn't know, or when the database is missing or
/// not as expected - the caller falls back to the next source. Galaxy's
/// layout is its own business and could change, so every public call
/// swallows its errors.
/// </summary>
public static class GogPlaytime
{
    private static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

    /// <summary>The Galaxy game the program running as <paramref name="pid"/> was installed as, or null.</summary>
    public static string? KeyForProcess(int pid)
    {
        try
        {
            if (Load() is not { } galaxy || SteamPlaytime.ExePath(pid) is not { } exe)
                return null;

            // The deepest folder that holds the executable, should one game
            // ever be installed inside another's folder.
            return galaxy.Installs
                .Where(install => exe.StartsWith(install.Folder, StringComparison.OrdinalIgnoreCase))
                .MaxBy(install => install.Folder.Length)
                .Key;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The Galaxy game called <paramref name="game"/>, for the session history,
    /// which has no process to go by. By title or install folder, ignoring
    /// case, spaces and punctuation - "Factorio" finds Factorio. A game whose
    /// executable calls itself something other than its store page is found
    /// through the id remembered the last time it ran instead.
    /// </summary>
    public static string? KeyForName(string game)
    {
        try
        {
            string wanted = SteamPlaytime.Simplified(game);
            if (wanted.Length == 0 || Load() is not { } galaxy)
                return null;

            foreach (var (key, played) in galaxy.Games)
                if (played.Title is { } title && SteamPlaytime.Simplified(title) == wanted)
                    return key;

            foreach (var install in galaxy.Installs)
                if (SteamPlaytime.Simplified(Path.GetFileName(install.Folder.TrimEnd('\\'))) == wanted)
                    return install.Key;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Galaxy's playtime for a game, or null when it has none.</summary>
    public static TimeSpan? ForKey(string key)
    {
        try
        {
            return Load() is { } galaxy && galaxy.Games.TryGetValue(key, out var played) && played.Minutes > 0
                ? TimeSpan.FromMinutes(played.Minutes)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Where Galaxy installed the game ("gog_1238653230"), or null when it isn't installed.</summary>
    public static string? InstallFolder(string key)
    {
        try
        {
            return Load()?.Installs.FirstOrDefault(install => install.Key == key).Folder;
        }
        catch
        {
            return null;
        }
    }

    // --- Reading the database --------------------------------------------------

    private sealed record Galaxy(
        Dictionary<string, (long Minutes, string? Title)> Games,
        List<(string Folder, string Key)> Installs);

    private static readonly object CacheLock = new();
    private static (DateTime Db, DateTime Wal, long WalLength, Galaxy? Data)? _cache;

    /// <summary>
    /// The database read once and kept until Galaxy writes it again: the
    /// session history's All games view asks about every game in turn. Galaxy
    /// writes to the -wal file beside the database first, so both are watched.
    /// Null when there is no Galaxy, or it can't be read.
    /// </summary>
    private static Galaxy? Load()
    {
        var db = new FileInfo(DatabasePath);
        if (!db.Exists)
            return null;

        var wal = new FileInfo(db.FullName + "-wal");
        var stamp = (db.LastWriteTimeUtc, wal.Exists ? wal.LastWriteTimeUtc : DateTime.MinValue, wal.Exists ? wal.Length : 0);

        lock (CacheLock)
        {
            if (_cache is { } cached && (cached.Db, cached.Wal, cached.WalLength) == stamp)
                return cached.Data;

            Galaxy? data;
            try
            {
                data = Read(db.FullName, readOnly: true);
            }
            catch
            {
                // Galaxy holding it in a way a reader can't get past: read a
                // copy instead. Null if even that fails - and remembered, so
                // a broken database isn't copied again for every game.
                data = ReadCopy(db.FullName);
            }

            _cache = (stamp.Item1, stamp.Item2, stamp.Item3, data);
            return data;
        }
    }

    private static Galaxy? ReadCopy(string database)
    {
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "DeskMon", "gog");
            Directory.CreateDirectory(folder);
            string copy = Path.Combine(folder, Path.GetFileName(database));

            // The -wal holds whatever Galaxy wrote lately, not yet folded into
            // the database: without it the copy would be days behind.
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                string from = database + suffix, to = copy + suffix;
                if (File.Exists(from))
                    File.Copy(from, to, overwrite: true);
                else if (File.Exists(to))
                    File.Delete(to);
            }

            return Read(copy, readOnly: false);
        }
        catch
        {
            return null;
        }
    }

    private static Galaxy Read(string database, bool readOnly)
    {
        // Each game's minutes - the most any account on this PC has, should
        // there be more than one - and its title: the "title" piece if there
        // is one, else "originalTitle". Games never played are left out.
        const string Played = """
            select gt.releaseKey, max(gt.minutesInGame),
                   (select p.value from GamePieces p
                      join GamePieceTypes t on t.id = p.gamePieceTypeId
                     where p.releaseKey = gt.releaseKey and t.type in ('title', 'originalTitle')
                     order by t.type = 'title' desc
                     limit 1)
              from GameTimes gt
             where gt.minutesInGame > 0
             group by gt.releaseKey
            """;

        var games = new Dictionary<string, (long, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, minutes, title) in WinSqlite.Query(database, Played,
                     row => (row.Text(0), row.Long(1), TitleFrom(row.Text(2))), readOnly))
            if (key is not null)
                games[key] = (minutes, title);

        // Installed games' folders, ending in a backslash so E:\Games\Factorio
        // doesn't also claim E:\Games\Factorio Mods. Release keys are "gog_"
        // and the product id, which is how GameTimes names them too.
        var installs = WinSqlite.Query(database, "select productId, installationPath from InstalledBaseProducts",
                           row => (Folder: row.Text(1), Key: "gog_" + row.Long(0)), readOnly)
            .Where(install => !string.IsNullOrWhiteSpace(install.Folder))
            .Select(install => (Folder: install.Folder!.TrimEnd('\\') + "\\", install.Key))
            .ToList();

        return new Galaxy(games, installs);
    }

    /// <summary>"Factorio", from Galaxy's {"title":"Factorio"}.</summary>
    private static string? TitleFrom(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
