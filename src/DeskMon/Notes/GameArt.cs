using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskMon.Notes;

/// <summary>
/// A wide picture of a game, for the banner behind the top of the sessions
/// window - the way Steam's library page shows one behind a game's name.
///
/// Found best first:
///   1. Steam's own library art, which Steam keeps on disk for every game in
///      the library: appcache\librarycache\&lt;appid&gt;, as library_hero.jpg
///      (the wide banner), or the smaller header.jpg when that is all there is.
///   2. With online art on, the Steam store's banner - kept in the art cache
///      after the first fetch, so a GOG copy gets the real banner too.
///   3. The newest screenshot DeskMon took of the game - a game the store
///      hasn't got still has a picture of itself.
/// Null when none exists; the window then draws <see cref="Drawn"/>.
///
/// Everything returned is frozen, so it can be made on a worker thread: the
/// lookup reads Steam's manifests and decodes a large JPEG, which the window
/// doesn't wait on.
/// </summary>
public static class GameArt
{
    /// <summary>
    /// Wide enough for the windows the banner sits in, even made wide; decoding
    /// the full 3840 px hero would only cost memory. 1200 rather than 1600:
    /// the banner is shown faded behind text, where the difference can't be
    /// seen, and each one kept is about 40% smaller.
    /// </summary>
    private const int DecodeWidth = 1200;

    /// <summary>Tiles in the All games collage - three across, two down.</summary>
    public const int CollageGames = 6;

    /// <summary>
    /// A game's banner, and its logo - the game's name as its own artwork, a
    /// transparent PNG Steam keeps beside the banner, for the title in the
    /// banner's corner. Either can be null; the window then draws the
    /// picture or writes the name.
    /// </summary>
    /// <param name="SteamAppId">The game's Steam app id where the lookup learned it, for the logo's link to its store page.</param>
    public sealed record Art(ImageSource? Picture, ImageSource? Logo, string? SteamAppId = null);

    /// <param name="steamAppId">The app id remembered from the last time the game ran, or null to look it up by name.</param>
    /// <param name="online">Whether the Steam store may be asked when this PC has no picture (the OnlineGameArt setting).</param>
    public static Art For(string game, string? steamAppId, string screenshotFolder, bool online)
    {
        string? appId = steamAppId ?? LocalAppId(game);
        var picture = Load(Find(game, ref appId, screenshotFolder, online), DecodeWidth);

        // Searched for on its own when the banner came from the cache or a
        // screenshot and no app id is known: the search takes only a game of
        // the same name, and a miss is remembered like the banner's.
        string? logo = SteamArt(appId, "logo.png");
        if (logo is null && online)
            logo = StoreFile(game, ref appId, ".logo.png", LogoUrls);

        return new Art(picture, Trimmed(Load(logo, 600)), appId);
    }

    /// <summary>
    /// The game's Steam app id from the store's search by name - for a game
    /// whose art came from the cache, so no lookup learned its id this run.
    /// Null when the store has no game of that name, or can't be reached.
    /// Blocks, so for a worker thread.
    /// </summary>
    public static string? StoreAppId(string game)
    {
        try
        {
            return SearchStore(game);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The games whose art is kept, most recently asked for first out last.
    /// Kept for good, every game's banner looked at in a session stayed in
    /// memory until REFRESH - several MB each, in a program meant to sit
    /// lightly over games. Eight covers going back and forth between a few.
    /// </summary>
    private const int ArtKept = 8;

    private static readonly object FoundLock = new();
    private static readonly Dictionary<string, (System.Threading.Tasks.Task<Art> Art, long Used)> Found =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _used;

    /// <summary>
    /// <see cref="For"/> on a worker thread, once per game for every window
    /// that shows it - the notes, its pictures and the sessions all open on
    /// the same game, and would otherwise each read Steam's folders and decode
    /// the same banner. Never faults: nothing found is an Art of nulls.
    ///
    /// On a thread of its own (LongRunning), not the shared pool: a lookup can
    /// wait seconds on the Steam store, and on a pool thread several at once
    /// held up the widget's sensor reads, which queue on the same pool.
    /// </summary>
    public static System.Threading.Tasks.Task<Art> ForAsync(string game, string? steamAppId, string screenshotFolder, bool online)
    {
        lock (FoundLock)
        {
            if (Found.TryGetValue(game, out var known))
            {
                Found[game] = (known.Art, ++_used);
                return known.Art;
            }

            var art = OwnThread(() =>
            {
                try { return For(game, steamAppId, screenshotFolder, online); }
                catch { return new Art(null, null); }
            });

            Found[game] = (art, ++_used);
            if (Found.Count > ArtKept)
                Found.Remove(Found.MinBy(entry => entry.Value.Used).Key);

            return art;
        }
    }

    /// <summary>
    /// Runs blocking work - downloads, Steam's app info - on a thread of its
    /// own rather than the shared pool. See <see cref="ForAsync"/>.
    /// </summary>
    internal static System.Threading.Tasks.Task<T> OwnThread<T>(Func<T> work)
        => System.Threading.Tasks.Task.Factory.StartNew(work, System.Threading.CancellationToken.None,
                                                         System.Threading.Tasks.TaskCreationOptions.LongRunning,
                                                         System.Threading.Tasks.TaskScheduler.Default);

    /// <summary>Looks every game up again next time - for REFRESH, after a screenshot or a change in Steam.</summary>
    public static void Forget()
    {
        lock (FoundLock)
            Found.Clear();
    }

    /// <summary>
    /// The games' pictures side by side, for All games - most played first,
    /// up to <see cref="CollageGames"/>. Games with no picture are skipped
    /// rather than shown as an empty tile. Null when none of them has one.
    /// </summary>
    public static ImageSource? Collage(IReadOnlyList<(string Game, string? SteamAppId, string ScreenshotFolder)> games,
                                       bool online)
    {
        var pictures = new List<ImageSource>();
        foreach (var (game, appId, folder) in games)
        {
            if (pictures.Count == CollageGames)
                break;

            // The wide hero first, as for one game: header.jpg has the game's
            // name written across it, and six names in a row would be a wall
            // of text behind the window's own.
            string? id = appId ?? LocalAppId(game);
            if (Load(Find(game, ref id, folder, online), 800) is { } picture)
                pictures.Add(picture);
        }

        return pictures.Count == 0 ? null : Tiled(pictures);
    }

    /// <summary>
    /// The best picture there is: Steam's on this PC, then the Steam store's,
    /// then the game's own newest screenshot.
    /// <paramref name="appId"/> comes back filled in when the store's search
    /// found the game, so its logo can be fetched without searching again.
    /// </summary>
    /// The store before the screenshots: a screenshot is whatever was on the
    /// screen when the key was pressed - an editor, the desktop - and a GOG
    /// copy of Factorio wore one of Visual Studio as its banner while the
    /// store's real one sat in the cache. A miss is cached for a week, so a
    /// game the store hasn't got still falls through to its screenshot cheaply.
    private static string? Find(string game, ref string? appId, string screenshotFolder, bool online)
        => SteamArt(appId, "library_hero.jpg", "library_header.jpg", "header.jpg")
           ?? (online ? StoreFile(game, ref appId, ".jpg", BannerUrls) : null)
           ?? NewestScreenshot(screenshotFolder);

    /// <summary>The store's own image server, by the old fixed address every app had.</summary>
    private static string Cdn(string appId, string file) => $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/{file}";

    /// <summary>The wide banner where the game has one, else the header every app has.</summary>
    private static IEnumerable<string> BannerUrls(string appId)
    {
        yield return Cdn(appId, "library_hero.jpg");
        yield return Cdn(appId, "header.jpg");
    }

    /// <summary>
    /// The old fixed address first, which older games still answer; newer ones
    /// keep it under a hashed folder that only Steam's app info gives out -
    /// asked only when the first is missing, since it means a connection to Steam.
    /// </summary>
    private static IEnumerable<string> LogoUrls(string appId)
    {
        yield return Cdn(appId, "logo.png");
        if (SteamAppInfo.LogoPath(appId) is { } path)
            yield return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{path}";
    }

    /// <summary>
    /// The game's small square icon from Steam, for a game this PC's Steam has
    /// no icon for - bought elsewhere, or not installed. Found as the banners
    /// are: by its app id, else the store's search by name, and kept in
    /// app\cache\gameart as Factorio.icon.jpg, a miss for a week as
    /// Factorio.icon.none. A path, or null. Blocks, so for a worker thread.
    /// </summary>
    public static string? StoreIcon(string game, string? appId)
        => StoreFile(game, ref appId, ".icon.jpg", IconUrls);

    /// <summary>Its hash comes only from Steam's app info - one short anonymous connection.</summary>
    private static IEnumerable<string> IconUrls(string appId)
    {
        if (SteamAppInfo.IconHash(appId) is { } hash)
            yield return $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{hash}.jpg";
    }

    // --- Where from ------------------------------------------------------------

    /// <summary>The installed Steam game of that name, or null - never throws.</summary>
    private static string? LocalAppId(string game)
    {
        try { return SteamPlaytime.AppIdForName(game); }
        catch { return null; }
    }

    /// <summary>
    /// The first of <paramref name="names"/> Steam has on disk for the app,
    /// or null. Newer Steam clients keep each file in a subfolder named by
    /// its hash, older ones loose in the app's folder - so the folder is
    /// searched, not a fixed path.
    /// </summary>
    private static string? SteamArt(string? appId, params string[] names)
    {
        try
        {
            if (appId is null || SteamPlaytime.SteamFolder() is not { } steam)
                return null;

            string folder = Path.Combine(steam, "appcache", "librarycache", appId);
            if (!Directory.Exists(folder))
                return null;

            foreach (string name in names)
            {
                // The biggest copy: an update can leave an old, smaller one behind.
                var file = new DirectoryInfo(folder)
                    .EnumerateFiles(name, SearchOption.AllDirectories)
                    .Where(f => f.Length > 0)
                    .MaxBy(f => f.Length);
                if (file is not null)
                    return file.FullName;
            }
        }
        catch
        {
            // Steam's folders are its own; no art is no banner, not an error.
        }

        return null;
    }

    // --- From the Steam store ----------------------------------------------------

    /// <summary>
    /// Downloaded banners and logos, one each per game: %LOCALAPPDATA%\DeskMon\cache\gameart\Factorio.jpg,
    /// Factorio.logo.png. Asked each time rather than kept, since DataFolder
    /// can fall back to app\ for a run; a path join is nothing beside a download.
    /// </summary>
    private static string CacheFolder => Path.Combine(DataFolder.Cache, "gameart");

    /// <summary>
    /// How long "the store has no such game" is believed before asking again
    /// - a game can come out on Steam later, but not so often that every
    /// opening of the window should ask.
    /// </summary>
    private static readonly TimeSpan MissKeptFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Written into each .none file. A miss only stands for the lookup that
    /// made it: when the lookup learns a new place to look - as it did for
    /// newer games' logos, from Steam's app info - this goes up, and every
    /// older "not found" is asked again rather than believed for a week.
    /// </summary>
    private const string LookupVersion = "2";

    private static readonly System.Net.Http.HttpClient Http = MakeClient();

    private static System.Net.Http.HttpClient MakeClient()
    {
        var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskMon");
        return client;
    }

    /// <summary>
    /// The first of <paramref name="urls"/> the Steam store has for the game
    /// - its banner, or its logo - downloaded once and kept in
    /// <see cref="CacheFolder"/> as the game's name plus <paramref name="suffix"/>.
    /// Found by its remembered app id when there is
    /// one - a game Steam knows but that isn't installed here - else by the
    /// store's own search, taking only a result with the same name: the
    /// search's best guess for a GOG game Steam doesn't sell would be some
    /// other game's picture.
    ///
    /// The store's search and image servers are public, with no key or login;
    /// the game's name is all that is sent. Null when offline, or when the
    /// store has no such game or file - which is remembered for a week as a
    /// .none file (Factorio.none, Factorio.logo.none), so what isn't
    /// there isn't asked about every time.
    /// </summary>
    private static string? StoreFile(string game, ref string? appId, string suffix, Func<string, IEnumerable<string>> urls)
    {
        try
        {
            string name = SafeName(game);
            string cached = Path.Combine(CacheFolder, name + suffix);
            string miss = Path.Combine(CacheFolder, name + Path.ChangeExtension(suffix, ".none"));

            if (File.Exists(cached))
                return cached;
            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissKeptFor
                && File.ReadAllText(miss) == LookupVersion)
                return null;

            appId ??= SearchStore(game);

            byte[]? image = null;
            if (appId is not null)
                // In order, and lazily: a later address may cost more to find.
                foreach (string url in urls(appId))
                    if ((image = Download(url)) is not null)
                        break;

            Directory.CreateDirectory(CacheFolder);
            if (image is null)
            {
                // Only a definite "not on the store" gets here: a dropped
                // connection throws instead, and tries again next time.
                File.WriteAllText(miss, LookupVersion);
                return null;
            }

            AtomicFile.WriteAllBytes(cached, image);
            File.Delete(miss);
            return cached;
        }
        catch
        {
            return null;   // offline, or the store changed shape: the drawn picture instead
        }
    }

    /// <summary>The app whose store name is the game's, ignoring case and punctuation; null if none is.</summary>
    private static string? SearchStore(string game)
    {
        string url = "https://store.steampowered.com/api/storesearch/?cc=us&l=english&term=" + Uri.EscapeDataString(game);
        string json = Http.GetStringAsync(url).GetAwaiter().GetResult();

        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items))
            return null;

        string wanted = SteamPlaytime.Simplified(game);
        var results = items.EnumerateArray()
            .Where(i => i.TryGetProperty("name", out _) && i.TryGetProperty("id", out _))
            .Select(i => (Name: SteamPlaytime.Simplified(i.GetProperty("name").GetString() ?? string.Empty),
                          Id: i.GetProperty("id").ToString()))
            .ToList();

        // The same name; or else the shortest that contains it, for a game
        // that calls itself by part of its store name - "Dawnwalker" is
        // "The Blood of Dawnwalker" - and shortest so its DLC and soundtrack,
        // which carry the name and more, lose to it. Not for a name of three
        // letters or fewer, which half the store contains.
        return results.FirstOrDefault(r => r.Name == wanted).Id
               ?? (wanted.Length >= 4
                   ? results.Where(r => r.Name.Contains(wanted, StringComparison.Ordinal))
                            .OrderBy(r => r.Name.Length)
                            .FirstOrDefault().Id
                   : null);
    }

    /// <summary>
    /// The file, or null when it isn't there (404) - anything else throws.
    ///
    /// Kept only if it is a plain PNG or JPEG of sensible size, fetched over
    /// https to the end. The picture is decoded inside a process running as
    /// administrator, and Windows decodes many formats - some through codecs
    /// that have had holes - so whatever comes back is checked before it is
    /// cached and ever handed to a decoder. Steam's art is always one of the
    /// two, a few hundred KB.
    /// </summary>
    private static byte[]? Download(string url)
    {
        using var response = Http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        // Redirects are followed; one that ended up on plain http is refused.
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Redirected off https: " + url);

        if (response.Content.Headers.ContentLength > MaxDownload)
            throw new InvalidDataException("Too large: " + url);

        using var body = response.Content.ReadAsStream();
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (copy.Length > MaxDownload)
                throw new InvalidDataException("Too large: " + url);   // no length given, or a false one
        }

        byte[] data = copy.ToArray();
        if (!IsPlainPicture(data))
            throw new InvalidDataException("Not a PNG or JPEG: " + url);
        return data;
    }

    /// <summary>Far above any banner or logo Steam serves, far below what could hurt.</summary>
    internal const int MaxDownload = 8 * 1024 * 1024;

    /// <summary>
    /// Larger than any Steam art, and than a screenshot of an 8K screen - the
    /// banner can be the newest screenshot. A bigger PNG header is a
    /// decompression bomb, not a picture.
    /// </summary>
    private const int MaxSide = 8192;

    /// <summary>
    /// Starts like a PNG with sane dimensions, or like a JPEG. Checked by the
    /// bytes, not the file name or the server's say-so: WPF picks the decoder
    /// by what the file holds, so a ".jpg" that is really a TIFF or an icon
    /// would go to those decoders instead.
    /// </summary>
    internal static bool IsPlainPicture(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length >= 24 && data[..8].SequenceEqual(png))
        {
            // IHDR always comes first: width and height, big-endian, at 16 and 20.
            uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
            uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            return width is > 0 and <= MaxSide && height is > 0 and <= MaxSide;
        }

        // JPEG: start-of-image, then a marker. Its size is held further in;
        // Load decodes it at a fixed width, which bounds the memory anyway.
        return data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
    }

    /// <summary>
    /// Bigger than any screenshot DeskMon takes - an 8K PNG of a busy scene
    /// is well under it - and small enough that a planted file can't make an
    /// elevated process read gigabytes before the check below turns it away.
    /// </summary>
    internal const long MaxPictureFile = 128L * 1024 * 1024;

    /// <summary>
    /// A screenshot's bytes, read whole and checked the way the downloaded art
    /// is - see <see cref="IsPlainPicture"/> - or null for anything else. For
    /// every place a screenshot is decoded: the folders are the signed-in
    /// user's to write, a link in pasted notes can point anywhere on the PC,
    /// and DeskMon decodes as administrator.
    /// </summary>
    internal static byte[]? ReadPlainPicture(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxPictureFile)
            return null;

        byte[] data = File.ReadAllBytes(path);
        return IsPlainPicture(data) ? data : null;
    }

    private static string SafeName(string game)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(game.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return safe.Length > 0 ? safe : "game";
    }

    /// <summary>The game's latest screenshot in the notes' folder - by its name, which is the time it was taken.</summary>
    private static string? NewestScreenshot(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;

            return Directory.EnumerateFiles(folder)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .MaxBy(f => File.GetLastWriteTimeUtc(f));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decoded smaller than it is, and read into memory at once so the file
    /// isn't held open - Steam replaces these when a game's art changes, and
    /// the pictures window may delete a screenshot.
    /// </summary>
    private static ImageSource? Load(string? path, int width)
    {
        if (path is null)
            return null;

        try
        {
            // Checked again here, not only on download: the cache folder and
            // the screenshots are files anyone signed in can replace, and this
            // runs as administrator. Read whole, then decoded from memory, so
            // what is checked is what is decoded.
            byte[] data = File.ReadAllBytes(path);
            if (data.Length > MaxDownload * 4 || !IsPlainPicture(data))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(data);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The logo cut to what is actually drawn in it. Steam's logo.png files
    /// carry their own transparent margins, more on some than others, so two
    /// logos set to the same bottom-right corner stood at different heights
    /// and distances from the edge - and a padded one came out smaller than
    /// its box allowed. Trimmed, every logo's ink meets the same corner.
    /// </summary>
    private static ImageSource? Trimmed(ImageSource? source)
    {
        if (source is not BitmapSource bitmap)
            return source;

        try
        {
            var pixels32 = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int w = pixels32.PixelWidth, h = pixels32.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            pixels32.CopyPixels(pixels, stride, 0);

            // Nearly clear counts as clear: soft shadows fade out over many
            // pixels at alpha 1-2, which would leave most of the margin.
            const byte Clear = 8;
            int left = w, right = -1, top = h, bottom = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[row + x * 4 + 3] <= Clear)
                        continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }

            // Wholly clear, or nothing to cut: as it was.
            if (right < 0 || (left == 0 && top == 0 && right == w - 1 && bottom == h - 1))
                return source;

            // Only the inked rectangle copied into a bitmap of its own. A
            // CroppedBitmap kept the converted full-size logo - and the decoded
            // one behind it - alive for as long as the art cache held it.
            int width = right - left + 1, height = bottom - top + 1, cropStride = width * 4;
            var cropped = new byte[cropStride * height];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, (top + y) * stride + left * 4, cropped, y * cropStride, cropStride);

            var trimmed = BitmapSource.Create(width, height, bitmap.DpiX, bitmap.DpiY, PixelFormats.Bgra32, null, cropped, cropStride);
            trimmed.Freeze();
            return trimmed;
        }
        catch
        {
            // An odd format it can't convert: the logo untrimmed beats none.
            return source;
        }
    }

    // --- Collage -----------------------------------------------------------------

    private const double CanvasWidth = 1200, CanvasHeight = 400, TileGap = 4;

    /// <summary>
    /// One row for up to three pictures, two rows for more, each picture
    /// cropped to fill its tile as a banner would rather than squeezed into it.
    /// The gaps are the panel's own dark, so the tiles read as separate cards.
    /// </summary>
    private static ImageSource Tiled(IReadOnlyList<ImageSource> pictures)
    {
        int rows = pictures.Count > 3 ? 2 : 1;
        int across = (int)Math.Ceiling(pictures.Count / (double)rows);

        double width = (CanvasWidth - TileGap * (across - 1)) / across;
        double height = (CanvasHeight - TileGap * (rows - 1)) / rows;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null,
                                               new RectangleGeometry(new Rect(0, 0, CanvasWidth, CanvasHeight))));

        for (int i = 0; i < pictures.Count; i++)
        {
            // The last row centred when it has fewer: five pictures are three
            // over two, not three over two and a hole.
            int row = i / across;
            int inRow = row == rows - 1 ? pictures.Count - row * across : across;
            double rowWidth = inRow * width + (inRow - 1) * TileGap;
            double left = (CanvasWidth - rowWidth) / 2 + (i % across) * (width + TileGap);
            var tile = new Rect(left, row * (height + TileGap), width, height);

            var cell = new DrawingGroup { ClipGeometry = new RectangleGeometry(tile, 6, 6) };
            cell.Children.Add(new ImageDrawing(pictures[i], Cover(pictures[i], tile)));
            group.Children.Add(cell);
        }

        var collage = new DrawingImage(group);
        collage.Freeze();
        return collage;
    }

    /// <summary>The picture's rectangle, at its own shape, just large enough to cover the tile - centred, so the crop takes both sides evenly.</summary>
    private static Rect Cover(ImageSource picture, Rect tile)
    {
        double scale = Math.Max(tile.Width / picture.Width, tile.Height / picture.Height);
        double width = picture.Width * scale, height = picture.Height * scale;
        return new Rect(tile.X + (tile.Width - width) / 2, tile.Y + (tile.Height - height) / 2, width, height);
    }

    // --- Drawn -------------------------------------------------------------------

    /// <summary>What a drawn banner shows: something that stands for its window.</summary>
    public enum Motif
    {
        /// <summary>A game with no picture anywhere.</summary>
        Gamepad,
        /// <summary>All games: a grid of cards, standing for the collage.</summary>
        Cards,
        /// <summary>The General notes, which belong to no game.</summary>
        Notepad,
        /// <summary>The pictures window: instant photos, one of mountains under a sun.</summary>
        Photos,
        /// <summary>Settings.</summary>
        Gear,
        /// <summary>The hardware window.</summary>
        Chip,
        /// <summary>The versions window: releases along a line.</summary>
        Timeline,
        /// <summary>
        /// The widget: DeskMon's own mark - the tray icon's tile with its three
        /// rising bars - and its name. On a tall canvas, as the widget is.
        /// </summary>
        DeskMon,
    }

    private static readonly Dictionary<Motif, ImageSource> DrawnCache = new();

    /// <summary>
    /// Glows of the window's chart colours - blue, orange, pink - with a large
    /// outline of the motif right of centre. Right of centre rather than at the
    /// edge: the banner is narrower than the drawing in a default-sized window,
    /// and its sides are cropped. Drawn, so it is sharp at any width; made once
    /// per motif, so showing it again is the same picture and doesn't fade.
    /// </summary>
    public static ImageSource Drawn(Motif motif)
    {
        lock (DrawnCache)
        {
            if (!DrawnCache.TryGetValue(motif, out var drawn))
                DrawnCache[motif] = drawn = Draw(motif);
            return drawn;
        }
    }

    private static ImageSource Draw(Motif motif)
    {
        var group = new DrawingGroup();
        var bounds = motif == Motif.DeskMon
            ? new Rect(0, 0, 600, 800)
            : new Rect(0, 0, CanvasWidth, CanvasHeight);

        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(bounds)));
        if (motif == Motif.DeskMon)
        {
            // The meters' green, with the toolbar's blue: the widget's own colours.
            group.Children.Add(Glow(bounds, new Point(0.75, 0.15), 0.6, Color.FromArgb(0x90, 0x4A, 0xDE, 0x80)));
            group.Children.Add(Glow(bounds, new Point(0.1, 0.6), 0.55, Color.FromArgb(0x70, 0x60, 0xA5, 0xFA)));
        }
        else
        {
            group.Children.Add(Glow(bounds, new Point(0.78, 0.25), 0.55, Color.FromArgb(0xB0, 0x60, 0xA5, 0xFA)));
            group.Children.Add(Glow(bounds, new Point(0.12, 0.95), 0.45, Color.FromArgb(0x60, 0xF4, 0x72, 0xB6)));
            group.Children.Add(Glow(bounds, new Point(0.45, -0.1), 0.4, Color.FromArgb(0x50, 0xFB, 0x92, 0x3C)));
        }

        var line = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var fill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        void Add(Geometry shape, bool filled = true)
            => group.Children.Add(new GeometryDrawing(filled ? fill : null, line, shape));

        switch (motif)
        {
            case Motif.Cards:
            {
                // Six cards, tilted a little.
                var cards = new GeometryGroup { Transform = new RotateTransform(-6, 790, 190) };
                for (int row = 0; row < 2; row++)
                    for (int column = 0; column < 3; column++)
                        cards.Children.Add(new RectangleGeometry(
                            new Rect(610 + column * 120, 90 + row * 110, 104, 94), 12, 12));
                Add(cards);
                break;
            }

            case Motif.Notepad:
            {
                // A page with rings along the top and ruled lines, tilted,
                // and a pencil lying across its corner.
                var tilt = new RotateTransform(-7, 790, 200);
                Add(new RectangleGeometry(new Rect(690, 70, 200, 260), 14, 14) { Transform = tilt });

                var rings = new GeometryGroup { Transform = tilt };
                for (int i = 0; i < 5; i++)
                    rings.Children.Add(new EllipseGeometry(new Point(722 + i * 34, 70), 8, 8));
                var ruled = new GeometryGroup { Transform = tilt };
                for (int i = 0; i < 5; i++)
                    ruled.Children.Add(new LineGeometry(new Point(720, 130 + i * 38), new Point(i == 4 ? 800 : 860, 130 + i * 38)));
                Add(rings, filled: false);
                Add(ruled, filled: false);

                Add(Shape("M 0,0 H 190 L 225,14 L 190,28 H 0 Z M 190,0 V 28 M 30,0 V 28",
                          new TransformGroup { Children = { new RotateTransform(-38), new TranslateTransform(840, 330) } }));
                break;
            }

            case Motif.Photos:
            {
                // Two instant photos dropped on a table, the way the notepad
                // is a page and a pencil: one behind, tilted back, and one in
                // front, tilted the other way, with a picture in it - mountains
                // under a sun - and the wide white strip along its bottom.
                var back = new RotateTransform(-10, 735, 185);
                Add(new RectangleGeometry(new Rect(630, 85, 210, 200), 10, 10) { Transform = back });
                Add(new RectangleGeometry(new Rect(646, 101, 178, 140), 4, 4) { Transform = back }, filled: false);

                var front = new RotateTransform(7, 850, 215);
                Add(new RectangleGeometry(new Rect(735, 105, 230, 220), 10, 10) { Transform = front });
                Add(new RectangleGeometry(new Rect(752, 122, 196, 152), 4, 4) { Transform = front }, filled: false);

                // The picture inside: a range of peaks, and the sun above them.
                Add(Shape("M 752,274 L 805,204 L 838,240 L 884,180 L 948,256 V 274 Z", front));
                Add(new EllipseGeometry(new Point(915, 152), 14, 14) { Transform = front });
                break;
            }

            case Motif.Gear:
            {
                // Two gears in mesh, a big one and a small one up to its right.
                Add(Gear(new Point(750, 215), 120, 96, 12, 34));
                Add(Gear(new Point(915, 120), 70, 54, 8, 20));
                break;
            }

            case Motif.Chip:
            {
                // A processor: the package, its die, and pins on every side.
                Add(new RectangleGeometry(new Rect(690, 100, 200, 200), 18, 18));
                Add(new RectangleGeometry(new Rect(745, 155, 90, 90), 8, 8), filled: false);

                var pins = new GeometryGroup();
                for (int i = 0; i < 6; i++)
                {
                    double at = 718 + i * 29, down = 128 + i * 29;
                    pins.Children.Add(new LineGeometry(new Point(at, 100), new Point(at, 70)));
                    pins.Children.Add(new LineGeometry(new Point(at, 300), new Point(at, 330)));
                    pins.Children.Add(new LineGeometry(new Point(690, down), new Point(660, down)));
                    pins.Children.Add(new LineGeometry(new Point(890, down), new Point(920, down)));
                }
                Add(pins, filled: false);
                break;
            }

            case Motif.DeskMon:
            {
                // The tray icon, large: a rounded tile, three bars short to
                // tall in the meters' green. Tilted a touch, so it reads as a
                // backdrop rather than a button.
                var tilt = new RotateTransform(-8, 330, 300);
                Add(new RectangleGeometry(new Rect(200, 170, 260, 260), 56, 56) { Transform = tilt });

                var green = new SolidColorBrush(Color.FromArgb(0x70, 0x4A, 0xDE, 0x80));
                var bars = new GeometryGroup { Transform = tilt };
                bars.Children.Add(new RectangleGeometry(new Rect(250, 330, 40, 60), 8, 8));
                bars.Children.Add(new RectangleGeometry(new Rect(310, 270, 40, 120), 8, 8));
                bars.Children.Add(new RectangleGeometry(new Rect(370, 210, 40, 180), 8, 8));
                group.Children.Add(new GeometryDrawing(green, null, bars));

                // The name under it, as outlines so it needs no font at draw time.
                var name = new FormattedText("DeskMon", System.Globalization.CultureInfo.InvariantCulture,
                                             FlowDirection.LeftToRight,
                                             new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal,
                                                          FontWeights.SemiBold, FontStretches.Normal),
                                             92, Brushes.White, 1.0);
                var letters = name.BuildGeometry(new Point(330 - name.Width / 2, 470));
                letters.Transform = tilt;
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), null, letters));
                break;
            }

            case Motif.Timeline:
            {
                // Releases along a line: a dot each, with a card above or below.
                Add(new LineGeometry(new Point(560, 200), new Point(1040, 200)), filled: false);
                for (int i = 0; i < 4; i++)
                {
                    double x = 610 + i * 130;
                    bool above = i % 2 == 0;
                    Add(new EllipseGeometry(new Point(x, 200), 13, 13));
                    Add(new LineGeometry(new Point(x, above ? 187 : 213), new Point(x, above ? 160 : 240)), filled: false);
                    Add(new RectangleGeometry(new Rect(x - 50, above ? 96 : 240, 100, 64), 10, 10));
                }
                break;
            }

            default:
            {
                // A gamepad: body with two grips, a d-pad, four buttons.
                var shift = new TranslateTransform(-170, 0);
                Add(Shape("M 860,110 H 1040 C 1100,110 1130,180 1130,250 C 1130,295 1100,310 1075,295 L 1025,250 H 875 " +
                          "L 825,295 C 800,310 770,295 770,250 C 770,180 800,110 860,110 Z " +
                          "M 845,185 H 885 M 865,165 V 205", shift));

                var buttons = new GeometryGroup { Transform = shift };
                foreach (var (x, y) in new[] { (1035.0, 165.0), (1060.0, 190.0), (1010.0, 190.0), (1035.0, 215.0) })
                    buttons.Children.Add(new EllipseGeometry(new Point(x, y), 10, 10));
                Add(buttons, filled: false);
                break;
            }
        }

        var drawn = new DrawingImage(group);
        drawn.Freeze();
        return drawn;
    }

    /// <summary>Path markup with a transform; parsed geometry comes back frozen, so it is copied first.</summary>
    private static Geometry Shape(string data, Transform transform)
    {
        var shape = Geometry.Parse(data).CloneCurrentValue();
        shape.Transform = transform;
        return shape;
    }

    /// <summary>A gear outline: <paramref name="teeth"/> flat-topped teeth between two radii, with a round hole.</summary>
    private static Geometry Gear(Point centre, double outer, double inner, int teeth, double hole)
    {
        var figure = new PathFigure { IsClosed = true };
        double step = 2 * Math.PI / teeth;
        for (int i = 0; i < teeth; i++)
        {
            // Root, flank up, tooth top, flank down: a quarter of the step each.
            double a = i * step;
            Point At(double angle, double radius) => new(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));

            var root = At(a, inner);
            if (i == 0)
                figure.StartPoint = root;
            else
                figure.Segments.Add(new LineSegment(root, true));
            figure.Segments.Add(new LineSegment(At(a + step * 0.25, inner), true));
            figure.Segments.Add(new LineSegment(At(a + step * 0.35, outer), true));
            figure.Segments.Add(new LineSegment(At(a + step * 0.65, outer), true));
            figure.Segments.Add(new LineSegment(At(a + step * 0.75, inner), true));
        }

        var gear = new PathGeometry(new[] { figure }) { FillRule = FillRule.EvenOdd };
        gear.Figures.Add(new EllipseGeometry(centre, hole, hole).GetFlattenedPathGeometry().Figures[0]);
        return gear;
    }

    private static GeometryDrawing Glow(Rect bounds, Point centre, double radius, Color colour)
    {
        var brush = new RadialGradientBrush(colour, Color.FromArgb(0, colour.R, colour.G, colour.B))
        {
            Center = centre,
            GradientOrigin = centre,
            RadiusX = radius,
            RadiusY = radius * bounds.Width / bounds.Height,   // relative to each side: round on screen
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        return new GeometryDrawing(brush, null, new RectangleGeometry(bounds));
    }
}
