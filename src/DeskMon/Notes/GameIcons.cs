using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskMon.Notes;

/// <summary>
/// A small square icon per game, for the game pickers in the notes and
/// sessions windows - so a game is found by its look before its name is read.
///
///   - A game's own icon wherever one can be found - Steam's on disk, GOG
///     Galaxy's in the game's folder, or Steam's from its servers for a game
///     bought elsewhere; see <see cref="Lookup"/>.
///   - Anything else gets a drawn tile: the game's initials on a gradient whose
///     colours come from its name, so the same game is always the same colour.
///   - General and All games get drawn tiles of their own.
///
/// The drawn tile is shown at once and the Steam icon replaces it when found,
/// on a worker thread - finding the app id reads every Steam manifest, too
/// slow to do for a whole list while it opens.
/// </summary>
public static class GameIcons
{
    private const double Canvas = 64;

    private static AppSettings? _settings;

    /// <summary>For the app ids remembered from when each game last ran - see GamePlaytime.</summary>
    public static void Use(AppSettings settings) => _settings = settings;

    // Only touched on the UI thread: the pickers' converter is what asks.
    private static readonly Dictionary<string, GameIcon> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static GameIcon? _general, _allGames;

    /// <summary>The General notes' icon: a house - see Glyph.</summary>
    public static GameIcon General => _general ??= new GameIcon(Glyph(GlyphKind.Home));

    /// <summary>The sessions window's All games: a grid of tiles.</summary>
    public static GameIcon AllGames => _allGames ??= new GameIcon(Glyph(GlyphKind.Grid));

    /// <summary>The game's icon, made once per name and kept.</summary>
    public static GameIcon For(string game)
    {
        if (string.Equals(game, NotesStore.GeneralNotes, StringComparison.OrdinalIgnoreCase))
            return General;

        if (Cache.TryGetValue(game, out var known))
            return known;

        var icon = new GameIcon(Monogram(game));
        Cache[game] = icon;

        // Read here, on the UI thread, which is the only one that writes them.
        string? steamId = _settings is null ? null : GamePlaytime.RememberedSteamAppId(game, _settings);
        string? gogKey = _settings is null ? null : GamePlaytime.RememberedGogKey(game, _settings);
        bool online = _settings?.OnlineGameArt ?? false;

        // Its own thread, not the pool: an online icon can wait seconds on
        // Steam, and the notes picker asks for every game's at once - on pool
        // threads that held up the widget's sensor reads. See GameArt.OwnThread.
        GameArt.OwnThread(() => Lookup(game, steamId, gogKey, online))
            .ContinueWith(found =>
            {
                if (found.Status == TaskStatus.RanToCompletion && found.Result is { } steam)
                    icon.Image = steam;
            }, TaskScheduler.FromCurrentSynchronizationContext());

        return icon;
    }

    // --- Finding a real one --------------------------------------------------------

    /// <summary>
    /// The game's own icon, best first, or null to keep the drawn tile:
    ///   1. Steam's, on this PC - an installed Steam game.
    ///   2. GOG Galaxy's, in the game's folder - every GOG install has a
    ///      goggame-&lt;id&gt;.ico beside the game.
    ///   3. Steam's, from Steam - for a game bought elsewhere or not installed,
    ///      found by name the way the sessions window's banners are, when the
    ///      OnlineGameArt setting allows it. Kept in app\cache\gameart.
    /// </summary>
    private static ImageSource? Lookup(string game, string? steamId, string? gogKey, bool online)
    {
        try
        {
            steamId ??= SteamPlaytime.AppIdForName(game);
            if (LocalSteamIcon(steamId) is { } steam)
                return steam;

            if (GogIcon(gogKey ?? GogPlaytime.KeyForName(game)) is { } gog)
                return gog;

            if (online && GameArt.StoreIcon(game, steamId) is { } downloaded)
                return Load(downloaded);
        }
        catch
        {
            // Every source is someone else's files or servers; the drawn tile stays.
        }

        return null;
    }

    /// <summary>Steam's icon for the app as Steam keeps it on disk, or null.</summary>
    private static ImageSource? LocalSteamIcon(string? appId)
    {
        if (appId is null || SteamPlaytime.SteamFolder() is not { } steam)
            return null;

        string folder = Path.Combine(steam, "appcache", "librarycache", appId);
        if (!Directory.Exists(folder))
            return null;

        // Only the top level: the banners are in hash-named subfolders,
        // and only the icon is a hash-named file of its own.
        string? file = Directory.EnumerateFiles(folder, "*.jpg")
            .FirstOrDefault(f => IsHash(Path.GetFileNameWithoutExtension(f)));
        return file is null ? null : Load(file);
    }

    /// <summary>The goggame-&lt;id&gt;.ico GOG puts in every game it installs, largest size in it, or null.</summary>
    private static ImageSource? GogIcon(string? key)
    {
        if (key is null || GogPlaytime.InstallFolder(key) is not { } folder)
            return null;

        string id = key.StartsWith("gog_", StringComparison.OrdinalIgnoreCase) ? key[4..] : key;
        string file = Path.Combine(folder, $"goggame-{id}.ico");
        if (!File.Exists(file))
            return null;

        // Checked before decoding, as GameArt checks its pictures: the game's
        // folder is anyone's to write in, and this runs as administrator. An
        // icon file is small and starts 00 00 01 00; anything else is not
        // handed to a decoder.
        byte[] data = File.ReadAllBytes(file);
        if (data.Length > MaxIconBytes || data.Length < 6 || data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != 0)
            return null;

        // An .ico holds several sizes; the picker draws it at 18 px, sharp at
        // any DPI from the largest.
        using var stream = new MemoryStream(data);
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var largest = decoder.Frames.MaxBy(frame => frame.PixelWidth);
        largest?.Freeze();
        return largest;
    }

    /// <summary>Far above any icon; an .ico with every size in it is well under this.</summary>
    private const int MaxIconBytes = 1024 * 1024;

    /// <summary>
    /// A picture file read in full and closed - not held open, as Steam
    /// replaces these. Read into memory and checked the way GameArt checks its
    /// banners before anything decodes it: Steam's folders and the cache can
    /// be written by anyone signed in, this runs as administrator, and WPF
    /// picks its decoder by what the file holds, not by its name. Decoded
    /// small, as it is only ever drawn as an icon. Null for anything else.
    /// </summary>
    private static ImageSource? Load(string file)
    {
        byte[] data = File.ReadAllBytes(file);
        if (data.Length > GameArt.MaxDownload || !GameArt.IsPlainPicture(data))
            return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(data);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = 64;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool IsHash(string name)
        => name.Length == 40 && name.All(Uri.IsHexDigit);

    // --- Drawn -------------------------------------------------------------------

    /// <summary>
    /// The initials on a diagonal gradient. The hue comes from a hash of the
    /// name - FNV, not string.GetHashCode, which changes every run - so a game
    /// keeps its colour; the second stop is a little further round the wheel
    /// and darker, so the tile has depth without turning into another colour.
    /// </summary>
    private static ImageSource Monogram(string game)
    {
        uint hash = 2166136261;
        foreach (char c in game.ToLowerInvariant())
            hash = (hash ^ c) * 16777619;

        double hue = hash % 360;
        var tile = Tile(FromHsv(hue, 0.70, 0.95), FromHsv(hue + 40, 0.80, 0.55));

        var text = new FormattedText(Initials(game), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal,
                                                  FontWeights.Bold, FontStretches.Normal),
                                     30, Brushes.White, 1.0);
        var letters = text.BuildGeometry(new Point((Canvas - text.Width) / 2, (Canvas - text.Height) / 2));
        tile.Children.Add(new GeometryDrawing(Brushes.White, null, letters));

        return Freeze(tile);
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "of", "and" };

    /// <summary>
    /// Up to two characters: the first of each of the first two words that
    /// count, so "The Witcher 3" is W3 and "Baldur's Gate" is BG. One word
    /// gives one letter - "Factorio" is F, not Fa.
    /// </summary>
    internal static string Initials(string game)
    {
        var words = game.Split(new[] { ' ', ':', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();

        var meaningful = words.Where(w => !Skipped.Contains(w)).ToList();
        if (meaningful.Count == 0)
            meaningful = words;
        if (meaningful.Count == 0)
            return "?";

        return string.Concat(meaningful.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    private enum GlyphKind { Home, Grid }

    /// <summary>A white outline glyph on the accent blue-to-violet, so the two entries that aren't games look alike and unlike any game.</summary>
    private static ImageSource Glyph(GlyphKind kind)
    {
        var tile = Tile(Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x7C, 0x3A, 0xED));
        var line = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var faint = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        var shapes = new GeometryGroup();
        if (kind == GlyphKind.Home)
        {
            // A house: the roof, the walls and a door. It was a ruled page,
            // which said "notes" - true of every entry in the list - rather
            // than "general", the home for what belongs to no game.
            tile.Children.Add(new GeometryDrawing(faint, null, Geometry.Parse("M32,17 L46,29 V50 H18 V29 Z")));
            tile.Children.Add(new GeometryDrawing(null, line, Geometry.Parse(
                "M13,31 L32,15 L51,31 M18,27 V50 H46 V27 M28,50 V40 H36 V50")));
        }
        else
        {
            // Four tiles, two by two.
            foreach (var (x, y) in new[] { (15.0, 15.0), (35.0, 15.0), (15.0, 35.0), (35.0, 35.0) })
                shapes.Children.Add(new RectangleGeometry(new Rect(x, y, 14, 14), 3, 3));
            tile.Children.Add(new GeometryDrawing(faint, line, shapes));
        }

        return Freeze(tile);
    }

    /// <summary>A rounded square filled top-left to bottom-right. The picker clips it round as well, so Steam's square icons match.</summary>
    private static DrawingGroup Tile(Color from, Color to)
    {
        var group = new DrawingGroup();
        var fill = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        group.Children.Add(new GeometryDrawing(fill, null, new RectangleGeometry(new Rect(0, 0, Canvas, Canvas), 12, 12)));
        return group;
    }

    private static ImageSource Freeze(DrawingGroup group)
    {
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;

        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        byte Byte(double v) => (byte)Math.Round((v + m) * 255);
        return Color.FromRgb(Byte(r), Byte(g), Byte(b));
    }
}

/// <summary>One game's icon, which can change once: from the drawn tile to Steam's icon when that is found.</summary>
public sealed class GameIcon : INotifyPropertyChanged
{
    private ImageSource _image;

    public GameIcon(ImageSource image) => _image = image;

    public ImageSource Image
    {
        get => _image;
        internal set
        {
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
