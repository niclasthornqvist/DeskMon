using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// Vendor marks for the hardware window.
///
/// Vector paths rather than image files: they stay sharp at whatever font size
/// the panel is set to, and they live inside the single-file exe with nothing
/// beside it that could go missing. The shapes are in <see cref="BrandLogoPaths"/>.
///
/// One list per kind of row, not one for everything, because names cross
/// over: a motherboard string can name its chipset ("AMD B650") without AMD
/// having made the board, and HP shows up both as a PC maker and on HyperX.
///
/// Brands with no logo in Simple Icons - Gigabyte, ASRock, G.Skill, Crucial,
/// SK hynix, Micron, ADATA, TeamGroup, Patriot, Realtek - simply get none.
/// </summary>
internal static class BrandLogos
{
    /// <summary>
    /// One brand's mark. The shape is parsed the first time it is asked for,
    /// not when the list is built: a machine shows five or six of these, and
    /// parsing all 25 up front - MSI's and Razer's are 8-9 KB of path each -
    /// was work for logos that never appear, done on the UI thread as the
    /// hardware window opened. Only ever touched from the UI thread, so no locking.
    /// </summary>
    public sealed class Logo
    {
        private readonly string _path;
        private Geometry? _shape;

        public Logo(string path, Brush? fill)
        {
            _path = path;
            Fill = fill;
        }

        /// <summary>Frozen, because the rows are bound once and never change it.</summary>
        public Geometry Shape
        {
            get
            {
                if (_shape is null)
                {
                    _shape = Geometry.Parse(_path);
                    _shape.Freeze();
                }

                return _shape;
            }
        }

        /// <summary>Null for brands whose mark is plain black: drawn in the
        /// panel's text colour instead, or it would vanish on the dark panel.</summary>
        public Brush? Fill { get; }
    }

    // The ones used by more than one list, made once.
    private static readonly Logo Amd = Make(BrandLogoPaths.Amd, "#ED1C24");
    private static readonly Logo Intel = Make(BrandLogoPaths.Intel, "#0071C5");
    private static readonly Logo Nvidia = Make(BrandLogoPaths.Nvidia, "#76B900");
    private static readonly Logo Kingston = Make(BrandLogoPaths.Kingston, null);
    private static readonly Logo Corsair = Make(BrandLogoPaths.Corsair, null);
    private static readonly Logo Samsung = Make(BrandLogoPaths.Samsung, "#1428A0");

    // Board and PC makers that make monitors too, so shared with Displays.
    private static readonly Logo Asus = Make(BrandLogoPaths.Asus, null);
    private static readonly Logo Msi = Make(BrandLogoPaths.Msi, "#FF0000");
    private static readonly Logo Alienware = Make(BrandLogoPaths.Alienware, "#541BAE");
    private static readonly Logo Dell = Make(BrandLogoPaths.Dell, "#007DB8");
    private static readonly Logo Hp = Make(BrandLogoPaths.Hp, "#0096D6");
    private static readonly Logo Lenovo = Make(BrandLogoPaths.Lenovo, "#E2231A");
    private static readonly Logo Acer = Make(BrandLogoPaths.Acer, "#83B81A");
    private static readonly Logo Razer = Make(BrandLogoPaths.Razer, "#00FF00");

    /// <summary>The Windows 11 mark, for the operating system row.</summary>
    public static readonly Logo Windows = Make(BrandLogoPaths.Windows, "#0078D4");

    /// <summary>A gamepad, for the gaming checks - plain, in the text colour, since it stands for no brand.</summary>
    public static readonly Logo Gaming = Make(BrandLogoPaths.Gamepad, null);

    // The fallbacks when no maker's logo matches - see ForDisplay and ForNetwork.
    private static readonly Logo AnyMonitor = Make(BrandLogoPaths.Monitor, null);
    private static readonly Logo Wired = Make(BrandLogoPaths.Network, null);
    private static readonly Logo Wireless = Make(BrandLogoPaths.Wifi, null);

    // How a wireless adapter names itself; anything else is taken as wired.
    private static readonly Regex WirelessName = Pattern(@"\b(wireless|wi-?fi|wlan|802\.11)");
    private static readonly Regex BluetoothName = Pattern(@"\bbluetooth\b");

    // Matched as names, with product lines too, because the strings are not
    // uniform: the registry fallback for the CPU can lack the vendor, and a
    // driver may report "GeForce RTX ..." without "NVIDIA" in front. Word
    // boundaries so a short name cannot hide inside a longer word; "Intel(R)"
    // still matches since "(" ends the word. Most specific first in each list:
    // no real name carries two vendors, but if one ever did, the first wins.
    private static readonly (Regex Name, Logo Logo)[] Chips =
    {
        (Pattern(@"\b(nvidia|geforce|quadro)\b"), Nvidia),
        (Pattern(@"\b(amd|ryzen|radeon|threadripper|epyc|athlon)\b"), Amd),
        (Pattern(@"\b(intel|xeon|celeron|pentium)\b"), Intel),
    };

    // Board makers, and the PC makers whose own name is on the board of a
    // prebuilt or a laptop. Alienware before Dell, since Dell owns it and an
    // Alienware board could mention both.
    private static readonly (Regex Name, Logo Logo)[] Boards =
    {
        (Pattern(@"\basus"), Asus),   // the registry says "ASUSTeK", so no end boundary
        (Pattern(@"\bmsi\b|micro-star"), Msi),
        (Pattern(@"\bnzxt\b"), Make(BrandLogoPaths.Nzxt, null)),
        (Pattern(@"\balienware\b"), Alienware),
        (Pattern(@"\bdell\b"), Dell),
        (Pattern(@"\bhp\b|hewlett"), Hp),
        (Pattern(@"\blenovo\b"), Lenovo),
        (Pattern(@"\bacer\b"), Acer),
        (Pattern(@"\brazer\b"), Razer),
        (Pattern(@"\bframework\b"), Make(BrandLogoPaths.Framework, null)),
    };

    // Screens, by the maker SystemInfo puts in front of the model from its
    // EDID code. Alienware before Dell: their screens report Dell's code, so
    // the "AW" model ("AW2723DF") is what tells them apart. BenQ, AOC,
    // Philips, ViewSonic, Gigabyte and iiyama have no Simple Icons logo and
    // get the plain screen instead.
    private static readonly (Regex Name, Logo Logo)[] Displays =
    {
        (Pattern(@"\balienware\b|\baw\d{4}"), Alienware),
        (Pattern(@"\bdell\b"), Dell),
        (Pattern(@"\bacer\b"), Acer),
        (Pattern(@"\basus\b"), Asus),
        (Pattern(@"\bsamsung\b"), Samsung),
        (Pattern(@"\blg\b"), Make(BrandLogoPaths.Lg, "#A50034")),
        (Pattern(@"\bmsi\b"), Msi),
        (Pattern(@"\bhp\b"), Hp),
        (Pattern(@"\blenovo\b"), Lenovo),
        (Pattern(@"\brazer\b"), Razer),
        (Pattern(@"\bsony\b"), Make(BrandLogoPaths.Sony, null)),   // white in Simple Icons: the text colour instead, or it vanishes on a pale theme
        (Pattern(@"\bxiaomi\b"), Make(BrandLogoPaths.Xiaomi, "#FF6900")),
        (Pattern(@"\bhuawei\b"), Make(BrandLogoPaths.Huawei, "#FF0000")),
    };

    // HyperX before Kingston: HyperX sticks were made by Kingston and report
    // it as their maker, so the KHX part number is what tells them apart.
    private static readonly (Regex Name, Logo Logo)[] Memory =
    {
        (Pattern(@"\b(hyperx|khx)"), Make(BrandLogoPaths.HyperX, "#E21836")),
        (Pattern(@"\bkingston\b"), Kingston),
        (Pattern(@"\bcorsair\b"), Corsair),
        (Pattern(@"\bsamsung\b"), Samsung),
    };

    // Drives are named by model alone, and some models never say the maker:
    // Western Digital's start "WDC", "WD_BLACK" or "WDS...", Seagate's "ST"
    // and digits. Those two are anchored to the start, where the model is,
    // so "ST" or "WD" further along can't match.
    private static readonly (Regex Name, Logo Logo)[] Storage =
    {
        (Pattern(@"\bsamsung\b"), Samsung),
        (Pattern(@"\bkingston\b"), Kingston),
        (Pattern(@"\bcorsair\b"), Corsair),
        (Pattern(@"^wd|\bwestern\s?digital\b"), Make(BrandLogoPaths.WesternDigital, "#995DFF")),
        (Pattern(@"^st\d|\bseagate\b"), Make(BrandLogoPaths.Seagate, "#6EBE49")),
        (Pattern(@"\bsandisk\b"), Make(BrandLogoPaths.SanDisk, "#ED1C24")),
        (Pattern(@"\btoshiba\b"), Make(BrandLogoPaths.Toshiba, "#FF0000")),
        (Pattern(@"\bintel\b"), Intel),
    };

    // Realtek, the most common maker of all, has no Simple Icons logo, and
    // Killer cards are left out because the name says nothing about whose
    // chip is inside - older ones are Qualcomm, newer ones Intel. Both get
    // the plain wired or Wi-Fi symbol instead - see ForNetwork.
    private static readonly (Regex Name, Logo Logo)[] Network =
    {
        (Pattern(@"\bintel\b"), Intel),
        (Pattern(@"\bmediatek\b"), Make(BrandLogoPaths.MediaTek, "#EC9430")),
        (Pattern(@"\b(qualcomm|atheros)\b"), Make(BrandLogoPaths.Qualcomm, "#3253DC")),
        (Pattern(@"\bbroadcom\b"), Make(BrandLogoPaths.Broadcom, "#E31837")),
    };

    /// <summary>The logo for a CPU or graphics card name, or null if the vendor isn't one we draw.</summary>
    public static Logo? ForChip(string? name) => Find(Chips, name);

    /// <summary>The logo for a motherboard's maker.</summary>
    public static Logo? ForBoard(string? name) => Find(Boards, name);

    /// <summary>The logo for one memory module's maker.</summary>
    public static Logo? ForMemory(string? name) => Find(Memory, name);

    /// <summary>The logo for a drive, from its model name.</summary>
    public static Logo? ForStorage(string? name) => Find(Storage, name);

    /// <summary>
    /// The logo for a screen's maker, or the plain screen when it has none -
    /// so every screen row has a mark and the column doesn't look half-filled
    /// on a desk with one known brand and one unknown.
    /// </summary>
    public static Logo? ForDisplay(string? name) => Find(Displays, name) ?? AnyMonitor;

    /// <summary>
    /// The logo for a network adapter's chip maker, or else the plain wired or
    /// Wi-Fi symbol, which says the one thing worth knowing about an adapter
    /// whose maker has no logo (Realtek). Not for Bluetooth: a network symbol
    /// would pass it off as an internet connection.
    /// </summary>
    public static Logo? ForNetwork(string? name)
        => Find(Network, name)
           ?? (string.IsNullOrEmpty(name) || BluetoothName.IsMatch(name) ? null
               : WirelessName.IsMatch(name) ? Wireless
               : Wired);

    private static Logo? Find((Regex Name, Logo Logo)[] brands, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        foreach (var brand in brands)
            if (brand.Name.IsMatch(name))
                return brand.Logo;

        return null;
    }

    // Not RegexOptions.Compiled: that turns each pattern into IL on the spot,
    // which only pays back over thousands of matches. These match about a
    // dozen strings, once per run - and the compiling happened on the UI
    // thread as the hardware window opened, for 30-odd patterns. Measured,
    // the matching itself was also about 10x faster without it.
    private static Regex Pattern(string pattern)
        => new(pattern, RegexOptions.IgnoreCase);

    /// <summary>The fill is frozen, because the rows are bound once and never change it.</summary>
    private static Logo Make(string path, string? color)
    {
        Brush? fill = null;
        if (color is not null)
        {
            fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            fill.Freeze();
        }

        return new Logo(path, fill);
    }
}
