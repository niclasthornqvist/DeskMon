using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DeskMon.Notes;

/// <summary>
/// How long Steam says a game has been played - the "143.4 hours" on its
/// library page - read from Steam's own files, with no network and no login.
///
/// Steam keeps each account's playtime per app, in minutes, in
/// userdata\&lt;account&gt;\config\localconfig.vdf. Which app a running game is
/// comes from where its executable lives: every Steam game is installed under
/// &lt;library&gt;\steamapps\common\&lt;installdir&gt;, and that library's
/// appmanifest_&lt;appid&gt;.acf files say which installdir belongs to which app.
///
/// Null for anything that isn't a Steam game, or when any of those files is
/// missing or not as expected - the caller falls back to DeskMon's own logs.
/// Read once per session, not per tick: localconfig.vdf is ~100 KB.
/// </summary>
public static class SteamPlaytime
{
    // Every public call swallows its errors: Steam's files are its own
    // business and may change shape, and a playtime that can't be read is
    // just DeskMon's own figure instead.

    /// <summary>The Steam app the game running as <paramref name="pid"/> was installed as, or null.</summary>
    public static string? AppIdForProcess(int pid)
    {
        try { return ExePath(pid) is { } exe ? AppIdFor(exe) : null; }
        catch { return null; }
    }

    /// <summary>
    /// The installed Steam app called <paramref name="game"/>, for when there
    /// is no process to go by - the session history, about a game that isn't
    /// running. By name, ignoring case, spaces and punctuation, so "Last Epoch"
    /// finds "Last Epoch" but a game whose executable calls itself something
    /// else than its store page doesn't: that one is found through the app id
    /// remembered the last time it ran instead.
    /// </summary>
    public static string? AppIdForName(string game)
    {
        try
        {
            string wanted = Simplified(game);
            if (wanted.Length == 0)
                return null;

            return InstalledByName().TryGetValue(wanted, out string? appId) ? appId : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly object NamesLock = new();
    private static (string Stamp, Dictionary<string, string> Ids)? _names;

    /// <summary>
    /// Every installed app's id by its simplified name, read from the
    /// manifests once and kept until a library's folder changes - Steam
    /// adding or removing a manifest touches it. Was read afresh on every
    /// ask: the sessions window's All games asks once a game, on the UI
    /// thread, which with a few hundred installed games was a few hundred
    /// files parsed per game, every refresh.
    /// </summary>
    private static Dictionary<string, string> InstalledByName()
    {
        var libraries = Libraries().ToList();
        string stamp = string.Join("|", libraries.Select(folder =>
            Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder).Ticks.ToString(CultureInfo.InvariantCulture) : "-"));

        lock (NamesLock)
        {
            if (_names is { } known && known.Stamp == stamp)
                return known.Ids;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string steamapps in libraries)
            foreach (var state in Manifests(steamapps))
                if (Value(state, "name") is { } name && Value(state, "appid") is { } id)
                    ids.TryAdd(Simplified(name), id);   // the first, as the walk used to find

        lock (NamesLock)
            _names = (stamp, ids);
        return ids;
    }

    /// <summary>Steam's playtime for an app, or null.</summary>
    public static TimeSpan? ForApp(string appId)
    {
        try { return PlaytimeOf(appId); }
        catch { return null; }
    }

    /// <summary>A name with case, spaces and punctuation gone, for matching: "The Witcher 3: Wild Hunt" → "thewitcher3wildhunt". GOG's lookup uses it too.</summary>
    internal static string Simplified(string name)
        => new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Every library's steamapps folder, the main one first, as libraryfolders.vdf lists them.</summary>
    private static IEnumerable<string> Libraries()
    {
        if (SteamFolder() is not { } steam)
            yield break;

        string main = Path.Combine(steam, "steamapps");
        yield return main;

        string list = Path.Combine(main, "libraryfolders.vdf");
        if (!File.Exists(list) || Child(Vdf.Parse(File.ReadAllText(list)), "libraryfolders") is not { } folders)
            yield break;

        foreach (var folder in folders.Values.OfType<Dictionary<string, object>>())
            if (Value(folder, "path") is { } path)
            {
                string steamapps = Path.Combine(path, "steamapps");
                if (!string.Equals(Path.GetFullPath(steamapps), Path.GetFullPath(main), StringComparison.OrdinalIgnoreCase))
                    yield return steamapps;
            }
    }

    private static IEnumerable<Dictionary<string, object>> Manifests(string steamapps)
    {
        if (!Directory.Exists(steamapps))
            yield break;

        foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            if (Child(Vdf.Parse(File.ReadAllText(manifest)), "AppState") is { } state)
                yield return state;
    }

    // --- Which app -----------------------------------------------------------

    private const string CommonFolder = @"\steamapps\common\";

    /// <summary>
    /// The app whose installdir the executable is in. The library is found from
    /// the path itself, so a game in E:\SteamLibrary needs no lookup in
    /// libraryfolders.vdf.
    /// </summary>
    private static string? AppIdFor(string exe)
    {
        int at = exe.IndexOf(CommonFolder, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        string steamapps = exe[..(at + @"\steamapps".Length)];
        string rest = exe[(at + CommonFolder.Length)..];
        int slash = rest.IndexOf('\\');
        if (slash <= 0)
            return null;
        string installDir = rest[..slash];

        foreach (var state in Manifests(steamapps))
            if (string.Equals(Value(state, "installdir"), installDir, StringComparison.OrdinalIgnoreCase))
                return Value(state, "appid");

        return null;
    }

    // --- How long --------------------------------------------------------------

    /// <summary>
    /// Playtime in the active account's localconfig.vdf. The active account is
    /// the one whose file was written last: Steam writes it while you play,
    /// and a second account on the same PC can't be playing at the same time.
    /// </summary>
    private static TimeSpan? PlaytimeOf(string appId)
    {
        if (SteamFolder() is not { } steam)
            return null;

        string userData = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userData))
            return null;

        var config = Directory.EnumerateDirectories(userData)
            .Select(user => new FileInfo(Path.Combine(user, "config", "localconfig.vdf")))
            .Where(file => file.Exists)
            .MaxBy(file => file.LastWriteTimeUtc);
        if (config is null)
            return null;

        var root = ParsedConfig(config);

        // UserLocalConfigStore > Software > Valve > Steam > apps > <appid>.
        // The same app id turns up elsewhere in the file too (cloud, friends),
        // so the path is followed rather than searched for.
        var app = Child(Child(Child(Child(Child(Child(root,
                    "UserLocalConfigStore"), "Software"), "Valve"), "Steam"), "apps"), appId);

        return app is not null
               && Value(app, "Playtime") is { } minutes
               && long.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out long m)
            ? TimeSpan.FromMinutes(m)
            : null;
    }

    private static readonly object CacheLock = new();
    private static (string Path, DateTime Written, Dictionary<string, object> Root)? _config;

    /// <summary>
    /// localconfig.vdf parsed, kept until Steam writes it again. The history's
    /// All games view asks for every game in turn, and parsing the same 100 KB
    /// once per game would be the slow part of opening it.
    /// </summary>
    private static Dictionary<string, object> ParsedConfig(FileInfo file)
    {
        lock (CacheLock)
        {
            if (_config is { } cached && cached.Path == file.FullName && cached.Written == file.LastWriteTimeUtc)
                return cached.Root;

            var root = Vdf.Parse(File.ReadAllText(file.FullName, Encoding.UTF8));
            _config = (file.FullName, file.LastWriteTimeUtc, root);
            return root;
        }
    }

    /// <summary>Where Steam is installed, as Steam itself records it.</summary>
    internal static string? SteamFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");

        // Written with forward slashes, as "c:/program files (x86)/steam".
        return key?.GetValue("SteamPath") is string path && path.Length > 0
            ? path.Replace('/', '\\')
            : null;
    }

    // --- The executable --------------------------------------------------------

    /// <summary>
    /// The game's full path. Through QueryFullProcessImageName rather than
    /// Process.MainModule: that one reads the process's memory and is refused
    /// by games with anti-cheat, while this only needs the limited query right
    /// those still grant. GOG's lookup finds its games the same way.
    /// </summary>
    internal static string? ExePath(int pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // --- VDF -------------------------------------------------------------------

    // Case-insensitive throughout: Steam itself has written "Software" and
    // "software", "apps" and "Apps", in different versions.
    private static Dictionary<string, object>? Child(Dictionary<string, object>? node, string key)
        => node is not null && node.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

    private static string? Value(Dictionary<string, object> node, string key)
        => node.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>
    /// Valve's KeyValues text format: "key" "value", or "key" { ... } for a
    /// section. Just enough of it for these two files - no #include or
    /// conditionals, which neither uses.
    /// </summary>
    private static class Vdf
    {
        public static Dictionary<string, object> Parse(string text)
        {
            var root = New();
            var stack = new Stack<Dictionary<string, object>>();
            stack.Push(root);
            string? key = null;
            int i = 0;

            while (Next(text, ref i) is { } token)
            {
                if (token.Kind == '{')
                {
                    var section = New();
                    if (key is not null)
                        stack.Peek()[key] = section;
                    stack.Push(section);
                    key = null;
                }
                else if (token.Kind == '}')
                {
                    if (stack.Count > 1)
                        stack.Pop();
                    key = null;
                }
                else if (key is null)
                {
                    key = token.Text;
                }
                else
                {
                    stack.Peek()[key] = token.Text;
                    key = null;
                }
            }

            return root;
        }

        private static Dictionary<string, object> New() => new(StringComparer.OrdinalIgnoreCase);

        private readonly record struct Token(char Kind, string Text);

        private static Token? Next(string text, ref int i)
        {
            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;   // a comment, to the end of the line
                }
                else if (c is '{' or '}')
                {
                    i++;
                    return new Token(c, string.Empty);
                }
                else if (c == '"')
                {
                    var value = new StringBuilder();
                    for (i++; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            i++;
                            value.Append(text[i] switch { 'n' => '\n', 't' => '\t', var other => other });
                        }
                        else
                        {
                            value.Append(text[i]);
                        }
                    }
                    i++;   // the closing quote
                    return new Token('"', value.ToString());
                }
                else
                {
                    // An unquoted word, which the format allows but these files
                    // don't use - read so it can't stall the loop.
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                        i++;
                    return new Token('"', text[start..i]);
                }
            }

            return null;
        }
    }
}
