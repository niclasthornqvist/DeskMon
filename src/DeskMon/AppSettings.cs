using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeskMon;

/// <summary>
/// Everything tweakable without recompiling. Lives in settings.json next to
/// DeskMon.exe and is rewritten when the widget is moved or closed.
/// </summary>
public sealed class AppSettings
{
    // --- Position (saved automatically after a drag) ---
    public double? Left { get; set; }
    public double? Top { get; set; }

    // --- Polling ---
    /// <summary>Refresh interval while the widget is visible.</summary>
    public int IntervalMs { get; set; } = 1000;
    /// <summary>
    /// Heartbeat while hidden behind a fullscreen app. Only checks whether the
    /// fullscreen app is gone; no sensors are touched.
    /// </summary>
    public int HiddenCheckMs { get; set; } = 2000;
    /// <summary>
    /// Hide and stop polling entirely while a game / fullscreen app is in front.
    /// Off by default so the widget can act as an overlay and show FPS; turn it
    /// on to get the old zero-cost behaviour back, at the price of seeing
    /// nothing while you play.
    /// </summary>
    public bool PauseWhenFullscreen { get; set; }

    /// <summary>
    /// Refresh interval while a game is running. Slower than the desktop
    /// interval, because during a game every millisecond belongs to the game.
    /// </summary>
    public int GameIntervalMs { get; set; } = 2000;

    /// <summary>
    /// How often the temperatures and other hardware-library values are
    /// actually re-read, independently of how often the panel refreshes.
    ///
    /// This one setting matters more than all the others for staying out of a
    /// game's way. Load, memory and swap come from Win32 and cost microseconds,
    /// so they are read every tick regardless - but asking the sensor library
    /// for temperatures takes around 78 ms on this machine, three quarters of
    /// it waiting inside the graphics driver. Temperatures move over seconds,
    /// so reading them four times less often costs nothing you can see and
    /// leaves the driver alone four times as much.
    ///
    /// Lower it if you want temperatures to track a stress test more closely;
    /// 0 reads them on every tick, which is what DeskMon did before 1.12.
    /// </summary>
    public int SensorIntervalMs { get; set; } = 4000;

    /// <summary>
    /// Measure FPS with PresentMon while a game runs. The PresentMon child
    /// process only exists during that time.
    /// </summary>
    public bool MeasureFps { get; set; } = true;

    /// <summary>
    /// Full path to presentmon.exe. Empty means "find it": PATH first, then
    /// where winget installed it. Set in settings.json only - the settings
    /// window has no box for it (see SettingsCatalogue).
    /// </summary>
    public string PresentMonPath { get; set; } = string.Empty;

    /// <summary>
    /// Processes never treated as a game, by executable name - with or without
    /// ".exe", case insensitive.
    ///
    /// Browsers are why this exists. A fullscreen video is a borderless window
    /// with no title bar covering the monitor, which is precisely what a
    /// borderless-fullscreen game is, so no amount of looking at the window can
    /// tell the two apart. Empty the list to detect everything again, or remove
    /// a browser from it to measure cloud gaming in that browser.
    /// </summary>
    public List<string> NotGames { get; set; } = new()
    {
        "Arc", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
        "vlc", "mpv", "mpc-hc64", "PotPlayerMini64",
    };

    /// <summary>
    /// Processes always treated as a game while one of their windows is in
    /// front, fullscreen or not - by executable name, like <see cref="NotGames"/>.
    ///
    /// The other half of the same problem. A game is otherwise only noticed
    /// when it covers its monitor with no title bar, so one played in a window
    /// - common for strategy and building games - was never measured or
    /// logged. A name on both lists counts as a game: this one is the more
    /// specific request.
    /// </summary>
    public List<string> AlwaysGames { get; set; } = new();

    // --- Appearance ---
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>Ignore mouse clicks entirely, so the widget can't be grabbed or hit.</summary>
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 0.88;

    /// <summary>
    /// Opacity while a game is being tracked. Lower than <see cref="Opacity"/>
    /// on purpose: on the desktop the panel is what you are looking at, over a
    /// game it is something you glance at and should otherwise disappear into
    /// the picture. Set it to the same value as Opacity to turn the change off.
    /// </summary>
    public double GameOpacity { get; set; } = 0.6;
    public double FontSize { get; set; } = 13;
    public string FontFamily { get; set; } = "Consolas";
    public bool ShowBars { get; set; } = true;

    /// <summary>
    /// "vertical": the sections one under another, a tall narrow panel - the
    /// widget as it has always been. "horizontal": the sections side by side,
    /// each its own column, for the top or bottom edge of a screen where a
    /// tall panel would cover too much. Anything else reads as vertical.
    /// </summary>
    public string WidgetLayout { get; set; } = "vertical";

    /// <summary>
    /// For working on DeskMon itself: a BUILD button on the widget's toolbar
    /// that rebuilds from the source and restarts, as Build.ps1 does. Only in
    /// settings.json, never in the settings window - it means nothing to
    /// anyone without the source - and the button only appears when Build.ps1
    /// is in the folder above the program's.
    /// </summary>
    public bool DeveloperRebuild { get; set; }

    /// <summary>
    /// Show the worst reading each row has seen beside the live one - the
    /// highest, or the lowest for an inverted metric like FPS. Reset whenever a
    /// game starts, and from "Reset peaks" on the right-click menu.
    /// </summary>
    public bool ShowPeaks { get; set; } = true;
    /// <summary>
    /// Show the detected CPU and graphics card names at the top of the widget.
    /// Off by default now that there is a hardware window; turn it on to have
    /// them in both places.
    /// </summary>
    public bool ShowHardwareNames { get; set; }

    /// <summary>
    /// Drop the game section - FPS, LOW, WORST, LAT - from the widget while no
    /// game is running.
    ///
    /// Off by default since 1.32: the section used to come and go with every
    /// game, which made the widget jump in height and left nowhere to look for
    /// the frame rate before a game was picked up. Shown all the time, it
    /// reads n/a until there is something to measure. Turn this on for the
    /// old, shorter panel on the desktop.
    /// </summary>
    public bool HideFpsWithoutGame { get; set; }

    // --- FPS overlay ---
    /// <summary>
    /// The frame rate in large figures in the top-right corner of the monitor
    /// the game is on, while a game is measured. Click-through, so it can sit
    /// over the game's HUD. <see cref="FpsOverlayHotkey"/> turns it on and off
    /// from inside the game, and that choice is remembered here.
    /// </summary>
    public bool ShowFpsOverlay { get; set; } = true;

    /// <summary>
    /// Shows and hides the FPS overlay. Written and held like
    /// <see cref="ScreenshotHotkey"/>, and next to it for the same reason: a
    /// combination almost nothing else uses. Empty turns it off.
    /// </summary>
    public string FpsOverlayHotkey { get; set; } = "Ctrl+Shift+F11";

    /// <summary>Text size of the overlay's figure; the "FPS" beside it follows.</summary>
    public double FpsOverlaySize { get; set; } = 36;

    /// <summary>How solid the overlay's figures are. Below 1, so the game shows through them a little.</summary>
    public double FpsOverlayOpacity { get; set; } = 0.7;

    /// <summary>
    /// The GAME heading's playtime from Steam, for a Steam game: Steam has
    /// counted every hour since you bought it, DeskMon only since it was
    /// installed. Read from Steam's own files on this PC - nothing is sent
    /// anywhere. Off, or for a game Steam doesn't know, it is DeskMon's logs.
    /// </summary>
    public bool SteamPlaytime { get; set; } = true;

    /// <summary>
    /// For the sessions window's banner, when neither Steam's files nor a
    /// screenshot have a picture of the game: look the game up on the Steam
    /// store by name and download its banner, kept in app\cache\gameart so it
    /// is fetched once. The game's name is the only thing sent.
    /// </summary>
    public bool OnlineGameArt { get; set; } = true;

    /// <summary>
    /// A notification when a reading stays in the red - at or past its Crit
    /// level in MetricRegistry - for <see cref="AlertAfterSeconds"/>: the CPU
    /// running hot, memory or a drive nearly full. In the notification area,
    /// and under the FPS overlay's figure while a game is in front, where the
    /// widget's colours can't be seen. Each reading once while it stays red,
    /// and again only after it has recovered - at most once in ten minutes.
    /// </summary>
    public bool Alerts { get; set; } = true;

    /// <summary>How long a reading has to stay in the red before it alerts - a spike is not a problem.</summary>
    public int AlertAfterSeconds { get; set; } = 60;

    /// <summary>
    /// A server to ping while a game runs, for the NETWORK section's PING row -
    /// a game server's address, or 1.1.1.1 for the internet in general. Empty
    /// pings nothing, and the row reads n/a.
    /// </summary>
    public string PingHost { get; set; } = string.Empty;

    /// <summary>
    /// Whether the widget keeps a second layout for games: which sections are
    /// folded while a game runs is remembered apart from the desktop's, and
    /// the widget switches between them by itself. Off, one layout for both.
    /// </summary>
    public bool GameLayout { get; set; } = true;

    /// <summary>
    /// <see cref="CollapsedSections"/> for while a game runs, when
    /// <see cref="GameLayout"/> is on. Starts with DISK and NETWORK folded, so
    /// a game gets the compact widget without setting anything up.
    /// </summary>
    public List<string> CollapsedSectionsInGame { get; set; } = new() { "Disk", "Network" };

    /// <summary>
    /// Sections left off the widget altogether on the desktop - heading and
    /// all, unlike a folded one, which keeps its heading to be unfolded from.
    /// Keyed by the name in MetricRegistry.cs, like <see cref="CollapsedSections"/>.
    /// Set with the tick boxes under "Sections shown" in the settings window.
    /// </summary>
    public List<string> HiddenSections { get; set; } = new();

    /// <summary>The same for while a game runs - its own list, whatever <see cref="GameLayout"/> says.</summary>
    public List<string> HiddenSectionsInGame { get; set; } = new();

    /// <summary>
    /// Look for a newer DeskMon once a day, on GitHub releases of
    /// <see cref="UpdateRepository"/>, and say so in the notification area and
    /// the versions window. Nothing is downloaded or installed.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Where releases are published, as "owner/repository" on GitHub - the
    /// newest release's tag is the version, e.g. "v1.56.0". Empty checks
    /// nothing.
    /// </summary>
    public string UpdateRepository { get; set; } = string.Empty;

    /// <summary>
    /// The game the sessions window showed last, so it opens on it again -
    /// "*" for All games, empty before it has been opened. Written by DeskMon.
    /// </summary>
    public string SessionsGame { get; set; } = string.Empty;

    /// <summary>The newest version already announced, so each is announced once. Written by DeskMon.</summary>
    public string UpdateAnnounced { get; set; } = string.Empty;

    /// <summary>
    /// The Steam app each game was found to be when it last ran, keyed by the
    /// name the GAME heading shows. Written by DeskMon, for the session
    /// history: it has no running process to tell which Steam game a name is.
    /// </summary>
    public Dictionary<string, string> SteamAppIds { get; set; } = new();

    /// <summary>
    /// The GAME heading's playtime from GOG Galaxy, for a GOG game, as
    /// <see cref="SteamPlaytime"/> does for Steam. Read from Galaxy's database
    /// on this PC - nothing is sent anywhere.
    /// </summary>
    public bool GogPlaytime { get; set; } = true;

    /// <summary>
    /// The GOG Galaxy game ("gog_1238653230") each game was found to be when
    /// it last ran, keyed by the name the GAME heading shows - as
    /// <see cref="SteamAppIds"/> is for Steam, and for the same reason.
    /// </summary>
    public Dictionary<string, string> GogGames { get; set; } = new();

    /// <summary>
    /// Games whose save files record their playtime, keyed by the name the
    /// GAME heading shows: which files, which value, and in what unit. Used
    /// for a game Steam doesn't have, before DeskMon's own logs, since a save
    /// knows about hours played before DeskMon was installed. Dawnwalker is
    /// filled in; another game is added the same way, by hand.
    /// </summary>
    public Dictionary<string, Notes.SaveFileSource> PlaytimeFromSaves { get; set; } = DefaultPlaytimeFromSaves();

    public static Dictionary<string, Notes.SaveFileSource> DefaultPlaytimeFromSaves() => new()
    {
        // Each save has a small JSON .meta beside it with "PlayTime" in
        // seconds - the time the game's own save menu shows.
        ["Dawnwalker"] = new Notes.SaveFileSource
        {
            Files = @"%LOCALAPPDATA%\Dawnwalker\Saved\SaveGames\*.meta",
            Key = "PlayTime",
            Unit = "seconds",
        },
    };

    /// <summary>
    /// Renames the section headings. The key is the name the section has in
    /// MetricRegistry.cs, the value is what gets drawn. A section with no entry
    /// here is drawn in upper case, as before. The game section keeps its
    /// game's name after it, separated by a space only - put a colon in the
    /// heading here if you want one.
    /// </summary>
    public Dictionary<string, string> SectionTitles { get; set; } = new()
    {
        ["Usage"] = "USAGE",
        ["Temperature"] = "TEMPERATURE",
        ["Game"] = "GAME",
    };

    /// <summary>
    /// Sections whose rows are folded away. Click a heading on the widget to
    /// toggle it; this is where that choice is remembered. Keyed by the name in
    /// MetricRegistry.cs, like <see cref="SectionTitles"/>, so renaming a
    /// heading doesn't lose the setting.
    /// </summary>
    public List<string> CollapsedSections { get; set; } = new();

    /// <summary>
    /// The order the sections are drawn in, top to bottom - set by dragging a
    /// heading up or down on the widget. Keyed by the name in MetricRegistry.cs
    /// like <see cref="CollapsedSections"/>. Empty means MetricRegistry's own
    /// order; a section not named here goes after those that are, so one
    /// added to the registry later still turns up.
    /// </summary>
    public List<string> SectionOrder { get; set; } = new();

    /// <summary>
    /// Metric rows whose sensor list is unfolded, keyed "Section/Label" - so
    /// the CPU row under Temperature is told apart from the one under Usage.
    /// Click a row with a <c>▸</c> on it to change this.
    /// </summary>
    public List<string> ExpandedMetrics { get; set; } = new();
    /// <summary>
    /// Width of the whole bar track, in pixels - the groove, not the filled
    /// part, which is the reading's share of it.
    ///
    /// Called BarWidth until 1.19, which read as though it sized the coloured
    /// bar within a fixed track. It sizes the track, and since the panel is
    /// exactly as wide as its content, widening the track widens the panel.
    /// </summary>
    public double BarTrackWidth { get; set; } = 70;

    /// <summary>
    /// How thick the bar is. The rounded ends follow it, so it stays a pill at
    /// any height - and at 1 or 2 it becomes a plain rule under the numbers.
    /// </summary>
    public double BarTrackHeight { get; set; } = 6;

    /// <summary>
    /// "rounded" is one smooth pill; "segmented" cuts the track into blocks
    /// like an LED meter, which reads at a glance over a busy game and fills a
    /// block at a time. A string rather than a bool so another style can join.
    /// </summary>
    public string BarStyle { get; set; } = "rounded";

    /// <summary>
    /// A thin tick on the bar where the peak reached, so how high it got is
    /// seen against the live bar rather than read from the peak figure.
    /// </summary>
    public bool ShowPeakMarker { get; set; } = true;

    /// <summary>
    /// A short bar in the bar gradient's colours before each section heading,
    /// so the sections are told apart at a glance.
    /// </summary>
    public bool SectionAccent { get; set; } = true;

    public double LabelWidth { get; set; } = 56;
    public double ValueWidth { get; set; } = 64;

    /// <summary>
    /// Width of the peak column - the dim figure after the bar.
    ///
    /// This is what sets the distance between the bar and that figure, which
    /// widening the bar cannot: the peak is laid out after the bar, so a longer
    /// bar pushes the peak further right and leaves the gap exactly as it was.
    /// The figure is right-aligned in this column so the peaks line up down the
    /// panel, which means any width beyond what the digits need becomes space
    /// to their left.
    ///
    /// It was ValueWidth - 16 until 1.27, which at the default 64 left about
    /// twice the room four characters need.
    /// </summary>
    public double PeakWidth { get; set; } = 34;

    public string BackgroundColor { get; set; } = "#D9121216";
    public string BorderColor { get; set; } = "#33FFFFFF";
    public string LabelColor { get; set; } = "#FF9AA4B2";
    /// <summary>The detected hardware names at the top.</summary>
    public string HeaderColor { get; set; } = "#FF7F8A9B";
    /// <summary>The "USAGE" / "TEMPERATURE" titles.</summary>
    public string SectionColor { get; set; } = "#FFE2E8F0";
    /// <summary>The line between sections.</summary>
    public string SeparatorColor { get; set; } = "#2EFFFFFF";
    /// <summary>
    /// The filled part of the bar. Empty - the default - means it follows the
    /// value's own colour, so the bar goes amber and red with the number it
    /// belongs to. Set a colour here to pin it to that one instead, which is
    /// quieter but no longer tells you anything on its own.
    /// </summary>
    public string BarColor { get; set; } = string.Empty;

    /// <summary>
    /// Fills the widget's bars and the sessions chart with a gradient from
    /// BarGradientStartColor to BarGradientEndColor. Takes over from BarColor
    /// and the value's colour while on - but the gradient's end turns amber
    /// and red with the number, so a hot reading is never a calm-looking bar.
    /// </summary>
    public bool BarGradient { get; set; } = true;

    /// <summary>The empty end of the bar: the left on the widget, the foot of a chart bar.</summary>
    public string BarGradientStartColor { get; set; } = "#FF22D3EE";

    /// <summary>The colour a full bar reaches.</summary>
    public string BarGradientEndColor { get; set; } = "#FFA855F7";

    /// <summary>The unfilled groove behind it.</summary>
    public string BarTrackColor { get; set; } = "#26FFFFFF";

    /// <summary>
    /// The look of the whole program, by name - one of Ui/Themes.cs's presets,
    /// or "Custom" for the colours as they are set below. Picking a preset
    /// writes its colours, fonts and corners into the settings here; editing
    /// any of them afterwards makes it Custom again. See ThemeCatalogue.
    /// </summary>
    public string Theme { get; set; } = ThemeCatalogue.Custom;

    /// <summary>
    /// Your own look, saved when you first pick a preset, so picking Custom
    /// brings it back. Keyed by setting name, values as settings.json writes
    /// them. Empty until then. Written by DeskMon.
    /// </summary>
    public Dictionary<string, string> CustomTheme { get; set; } = new();

    /// <summary>
    /// What lights up: the toolbar's open windows, ticked boxes, sliders,
    /// selected rows, links, the sessions chart. Blue by default.
    /// </summary>
    public string AccentColor { get; set; } = "#FF60A5FA";

    /// <summary>The font of every window but the widget - menus and tooltips too. The notes keep their own.</summary>
    public string WindowFontFamily { get; set; } = "Calibri";

    /// <summary>How round the corners of the widget and the windows are, in pixels; 0 is square.</summary>
    public double CornerRadius { get; set; } = 10;

    /// <summary>A soft light in the accent colour around the widget and the windows, for the neon themes.</summary>
    public bool Glow { get; set; }

    /// <summary>The glow's colour. Empty - the default - is the accent colour.</summary>
    public string GlowColor { get; set; } = string.Empty;

    /// <summary>
    /// How strongly DeskMon's own picture - the logo tile and name - shows
    /// behind the widget's readings, from 0 (not at all) to 1. Was a fixed
    /// 0.14 until 1.58, which was hard to make out.
    /// </summary>
    public double WidgetBackdrop { get; set; } = 0.3;

    public string NormalColor { get; set; } = "#FF4ADE80";
    public string WarnColor { get; set; } = "#FFFBBF24";
    public string CritColor { get; set; } = "#FFF87171";
    public string UnavailableColor { get; set; } = "#FF6B7280";

    /// <summary>"auto", "nvidia", "amd" or "intel" - which card to report.</summary>
    public string PreferredGpu { get; set; } = "auto";

    // --- Per-game notes ---
    /// <summary>Open the notes window by itself whenever a game is detected.</summary>
    public bool AutoOpenNotesWithGame { get; set; } = true;
    /// <summary>Keep notes above the game, so a borderless game doesn't bury them.</summary>
    public bool NotesAlwaysOnTop { get; set; } = true;

    /// <summary>
    /// How see-through the notepad is while you are using it.
    ///
    /// This multiplies the alpha already in NotesBackgroundColor, so 1 is not
    /// necessarily opaque - it means "as the background colour says". There is
    /// one of these per window because they are used quite differently: the
    /// notepad is read and typed in and wants to be solid, while the hardware
    /// and versions windows are glanced at and can afford to float.
    ///
    /// While a game runs and the window is not the one in front, the lower of
    /// this and GameOpacity wins - see PanelWindow.DimDuringGames.
    /// </summary>
    public double NotesOpacity { get; set; } = 1;

    /// <summary>Where the notes live. Empty means %APPDATA%\DeskMon\notes - see DataFolder.</summary>
    public string NotesFolder { get; set; } = string.Empty;

    /// <summary>
    /// Puts a screenshot of the game into its notes, from inside the game.
    /// Written the way WPF writes shortcuts, e.g. "Ctrl+Shift+F12"; empty
    /// turns it off.
    ///
    /// Ctrl+Shift+F12 by default because a global hotkey is taken away from
    /// every other program while DeskMon holds it - a common combination
    /// would stop working in games and editors - and almost nothing uses this
    /// one.
    /// </summary>
    public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+F12";

    /// <summary>
    /// The full-size screenshot file: 100 saves a lossless PNG, anything lower
    /// a JPEG at that quality - a fraction of the size. 100 by default so
    /// nothing changed for anyone who never touches it. Read at each capture,
    /// so a change applies to the next screenshot. The picture shown inside
    /// the notes is unaffected; see Notes/Screenshot.cs.
    /// </summary>
    public int ScreenshotQuality { get; set; } = 100;

    // NotesFontSize and NotesFontFamily used to live here. Font and size are
    // now picked on the notepad's toolbar and saved with the notes, so the
    // settings could only ever disagree with what the notes say. The other
    // windows take UiTheme.PanelFontFamily/PanelFontSize instead. An old
    // entry left in settings.json is ignored.

    /// <summary>
    /// Matches the widget's panel but less see-through, since this one is for
    /// reading and typing. Set it to the widget's #D9121216 to make them
    /// identical, or #FF121216 for fully opaque.
    /// </summary>
    public string NotesBackgroundColor { get; set; } = "#F2121216";
    public string NotesTextColor { get; set; } = "#FFE6EAF0";

    // NotesColors used to live here: five hex strings that became five swatches
    // on the notes toolbar. The colour picker in 1.24 made them a shortcut to
    // five of the sixteen million it offers, and a shortcut nobody needs is
    // just another key to explain - so both are gone. An old entry left in
    // settings.json is ignored.

    // Remembered window placement.
    public double? NotesLeft { get; set; }
    public double? NotesTop { get; set; }
    public double? NotesWidth { get; set; }
    public double? NotesHeight { get; set; }

    // --- Session history ---
    /// <summary>
    /// When a game is quit, add one line to its session history saying how long
    /// it ran, its average frame rate and the worst each reading reached.
    /// Sessions under a minute are never logged.
    ///
    /// This replaces LogSessionToNotes, which put the same line into the game's
    /// notes - in among what you had written there yourself, and growing by a
    /// line every session. An old key of that name in settings.json is ignored.
    /// </summary>
    public bool LogSessionToFile { get; set; } = true;

    /// <summary>
    /// Where the history files go. Empty means %APPDATA%\DeskMon\sessions - see DataFolder.
    ///
    /// Separate from <see cref="NotesFolder"/> on purpose: the notes are worth
    /// syncing between machines, while one machine's frame rates filed under
    /// the same game as another's are just confusing.
    /// </summary>
    public string SessionFolder { get; set; } = string.Empty;

    /// <summary>
    /// A while into each game, one notification if something heavy is running
    /// beside it - a browser holding gigabytes, Windows Update or a Defender
    /// scan busy on the CPU. Silent when nothing stands out. See GameStartCheck.
    /// </summary>
    public bool GameStartCheck { get; set; } = true;

    /// <summary>
    /// What a kWh costs, for the LOG window's Cost column. 0 hides the column.
    /// Worked out when shown, not stored with the session, so a new price
    /// applies to every session at once.
    /// </summary>
    public double EnergyPrice { get; set; }

    /// <summary>Written after the cost, e.g. "kr" or "€". Empty for a bare number.</summary>
    public string EnergyCurrency { get; set; } = string.Empty;

    // --- Hardware window (shares the notes window's colours and font) ---
    /// <summary>Width of the label column, e.g. "Motherboard".</summary>
    public double HardwareLabelWidth { get; set; } = 110;

    /// <summary>
    /// Ask NVIDIA or AMD for the newest driver, ASUS for the newest BIOS and
    /// Windows Update for waiting updates, each
    /// time the hardware window is opened. The name keeps "Driver" so existing
    /// settings.json files keep their choice. It can be turned off; the
    /// offline age check on the driver and BIOS stays either way.
    /// </summary>
    public bool CheckDriverOnline { get; set; } = true;

    /// <summary>As <see cref="NotesOpacity"/>, for the hardware window.</summary>
    public double HardwareOpacity { get; set; } = 1;
    public double? HardwareLeft { get; set; }
    public double? HardwareTop { get; set; }
    public double? HardwareWidth { get; set; }
    public double? HardwareHeight { get; set; }

    // --- Versions window ---
    /// <summary>As <see cref="NotesOpacity"/>, for the versions window.</summary>
    public double VersionsOpacity { get; set; } = 1;

    public double? VersionsLeft { get; set; }
    public double? VersionsTop { get; set; }
    public double? VersionsWidth { get; set; }
    public double? VersionsHeight { get; set; }

    // --- Sessions window ---
    /// <summary>As <see cref="NotesOpacity"/>, for the session history window.</summary>
    public double SessionsOpacity { get; set; } = 1;

    public double? SessionsLeft { get; set; }
    public double? SessionsTop { get; set; }
    public double? SessionsWidth { get; set; }
    public double? SessionsHeight { get; set; }

    /// <summary>
    /// The table's columns, picked with its Columns button. Peaks are keyed
    /// "Label|Unit", the way MetricRegistry names a row, since CPU alone is
    /// both a load and a temperature. Date is always shown and not listed.
    /// </summary>
    public List<string> SessionColumns { get; set; } = DefaultSessionColumns();

    /// <summary>
    /// Where the hardware window starts counting crashes and unexpected
    /// shutdowns from, if later than 30 days ago - set by right-clicking that
    /// line. For shutdowns that were known and harmless (the power button
    /// held instead of shutting down), which Windows logs the same as a
    /// freeze. Null counts the last 30 days.
    /// </summary>
    public DateTime? StabilityCountedFrom { get; set; }

    public static List<string> DefaultSessionColumns()
        => new() { "time", "length", "avg", "LOW|", "CPU|°C", "GPU|°C" };

    /// <summary>
    /// The frame rate a game has to hold for full marks in its performance
    /// score - your monitor's refresh rate, or whatever you aim for. Changing
    /// it re-scores every session, old ones included; see PerformanceScore.
    /// </summary>
    public double TargetFps { get; set; } = 60;

    /// <summary>
    /// A target of its own for a game that can't reach <see cref="TargetFps"/>
    /// by design - Factorio runs at 60 whatever the monitor, and scored
    /// "Needs optimising" against 240 while running perfectly. By game name;
    /// set by right-clicking the SCORE card in the sessions window.
    /// </summary>
    public Dictionary<string, double> GameTargetFps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the chart's bars measure: "avg", "length", "score" or a peak's "Label|Unit".</summary>
    public string SessionsChart { get; set; } = "avg";

    // --- Pictures window ---
    // No opacity of its own: it belongs to the notes and follows NotesOpacity.

    /// <summary>
    /// The order of the pictures, picked above them: "newest", "oldest",
    /// "largest", "smallest" or "unused" (those not in the notes first). Newest
    /// first, as it always was, for anything else.
    /// </summary>
    public string PicturesSort { get; set; } = "newest";

    public double? PicturesLeft { get; set; }
    public double? PicturesTop { get; set; }
    public double? PicturesWidth { get; set; }
    public double? PicturesHeight { get; set; }

    // --- Settings window ---
    /// <summary>As <see cref="NotesOpacity"/>, for the settings window.</summary>
    public double SettingsOpacity { get; set; } = 1;

    public double? SettingsLeft { get; set; }
    public double? SettingsTop { get; set; }
    public double? SettingsWidth { get; set; }
    public double? SettingsHeight { get; set; }

    // ---------------------------------------------------------------

    /// <summary>In the user's own data folder, not beside the exe - see DataFolder.</summary>
    [JsonIgnore]
    public static string FilePath => Path.Combine(DataFolder.Root, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                        ?? new AppSettings()).Repaired();
        }
        catch (Exception ex)
        {
            // A hand-edited file with a typo shouldn't stop the widget from
            // starting; fall back to defaults and rewrite on the next save.
            // A copy is kept first: that next save - a drag of the widget is
            // enough - writes the defaults over the file, and one stray comma
            // used to cost every setting in it.
            KeepUnreadableCopy();
            App.Log("reading settings.json", ex);
        }
        return new AppSettings();
    }

    /// <summary>
    /// Copies a settings.json that won't parse to settings.unreadable-&lt;time&gt;.json
    /// beside it, so the edits in it can be put back by hand. Never throws:
    /// this only runs when something is already wrong.
    /// </summary>
    private static void KeepUnreadableCopy()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            string copy = Path.Combine(Path.GetDirectoryName(FilePath)!,
                                       $"settings.unreadable-{DateTime.Now:yyyy-MM-dd_HHmmss}.json");
            File.Copy(FilePath, copy, overwrite: true);
        }
        catch
        {
            // Nothing more to try; the error is in error.log.
        }
    }

    /// <summary>
    /// Puts right what a hand-edited file can get wrong without being invalid.
    ///
    /// <c>"SectionTitles": null</c> is perfectly good JSON, so Deserialize
    /// accepts it and quietly replaces the default with nothing - and then the
    /// first thing to read it threw, at startup, with no widget to show for it.
    /// The catch in <see cref="Load"/> is no help there, because nothing threw
    /// where it could see. Editing this file by hand is a supported way to use
    /// DeskMon, so an empty value has to mean "the default".
    ///
    /// Colours are left alone on purpose: UiTheme already falls back when a
    /// value won't parse, and null is just another value that won't parse.
    /// </summary>
    private AppSettings Repaired()
    {
        SectionTitles ??= new Dictionary<string, string>();
        CollapsedSections ??= new List<string>();
        CollapsedSectionsInGame ??= new List<string>();
        HiddenSections ??= new List<string>();
        HiddenSectionsInGame ??= new List<string>();
        SectionOrder ??= new List<string>();
        ExpandedMetrics ??= new List<string>();
        NotGames ??= new List<string>();
        AlwaysGames ??= new List<string>();
        SessionColumns ??= DefaultSessionColumns();
        // Rebuilt rather than only null-checked: the deserialiser makes a
        // case-sensitive one, and the game names in the logs vary in case.
        GameTargetFps = new Dictionary<string, double>(GameTargetFps ?? new(), StringComparer.OrdinalIgnoreCase);
        PlaytimeFromSaves ??= DefaultPlaytimeFromSaves();
        SteamAppIds ??= new Dictionary<string, string>();
        GogGames ??= new Dictionary<string, string>();
        SessionsChart ??= "avg";
        PicturesSort ??= "newest";

        FontFamily ??= "Consolas";
        WidgetLayout ??= "vertical";
        BarStyle ??= "rounded";
        PreferredGpu ??= "auto";
        PresentMonPath ??= string.Empty;
        BarColor ??= string.Empty;
        BarGradientStartColor ??= "#FF22D3EE";
        BarGradientEndColor ??= "#FFA855F7";
        BarTrackColor ??= "#26FFFFFF";
        Theme ??= ThemeCatalogue.Custom;
        CustomTheme ??= new Dictionary<string, string>();
        AccentColor ??= "#FF60A5FA";
        WindowFontFamily ??= "Calibri";
        GlowColor ??= string.Empty;
        NotesFolder ??= string.Empty;
        SessionFolder ??= string.Empty;
        ScreenshotHotkey ??= string.Empty;
        FpsOverlayHotkey ??= string.Empty;
        PingHost ??= string.Empty;
        UpdateRepository ??= string.Empty;
        UpdateAnnounced ??= string.Empty;
        SessionsGame ??= string.Empty;

        // Numbers WPF refuses outright, rather than merely draws oddly: a text
        // size of 0 threw at startup and kept DeskMon from starting at all.
        // The same limits the widget applies where it uses them.
        if (!double.IsFinite(FontSize) || FontSize < 6)
            FontSize = 13;
        if (!double.IsFinite(LabelWidth) || LabelWidth < 0)
            LabelWidth = 56;
        if (!double.IsFinite(ValueWidth) || ValueWidth < 0)
            ValueWidth = 64;

        return this;
    }

    /// <summary>
    /// Keys DeskMon changes by itself while running. Everything else in the
    /// file belongs to whoever edited it, and a save must leave it alone.
    /// </summary>
    private static readonly string[] OwnedAtRuntime =
    {
        nameof(Left), nameof(Top),
        nameof(AlwaysOnTop), nameof(Locked),
        nameof(ShowFpsOverlay),   // flipped by its hotkey
        nameof(CollapsedSections), nameof(SectionOrder), nameof(ExpandedMetrics),
        nameof(CollapsedSectionsInGame), nameof(UpdateAnnounced), nameof(SessionsGame),
        nameof(NotesLeft), nameof(NotesTop), nameof(NotesWidth), nameof(NotesHeight),
        nameof(HardwareLeft), nameof(HardwareTop), nameof(HardwareWidth), nameof(HardwareHeight),
        nameof(VersionsLeft), nameof(VersionsTop), nameof(VersionsWidth), nameof(VersionsHeight),
        nameof(SessionsLeft), nameof(SessionsTop), nameof(SessionsWidth), nameof(SessionsHeight),
        nameof(SessionColumns), nameof(SessionsChart), nameof(SteamAppIds), nameof(GogGames),
        nameof(PicturesLeft), nameof(PicturesTop), nameof(PicturesWidth), nameof(PicturesHeight),
        nameof(PicturesSort),
        nameof(GameTargetFps),   // the SCORE card's right-click menu
        // Right-click on the hardware window's stability line. Off this list
        // it was only ever written once, as null, and every later click was
        // lost on the next start.
        nameof(StabilityCountedFrom),
        nameof(SettingsLeft), nameof(SettingsTop), nameof(SettingsWidth), nameof(SettingsHeight),
    };

    /// <summary>
    /// Keys changed in the settings window this run.
    ///
    /// <see cref="Save"/> deliberately leaves alone every key it does not own,
    /// which is what keeps a hand-edited file hand-edited. But a value just
    /// changed in the window has to reach the file, and those keys are not ones
    /// DeskMon owns - the window is the user editing the file, through a
    /// different door. So it says what it touched, and only that is written.
    /// </summary>
    private readonly HashSet<string> _edited = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that <paramref name="key"/> was changed deliberately and must be
    /// written out. The name is a property on this class.
    /// </summary>
    public void MarkEdited(string key) => _edited.Add(key);

    /// <summary>
    /// Writes the settings DeskMon owns without disturbing the rest of the file.
    ///
    /// Serializing the whole object over the file used to undo hand edits
    /// without a word: the copy in memory is the one loaded at startup, so
    /// editing settings.json and then dragging the widget - or just exiting -
    /// wrote the startup values straight back over the new ones. Only the keys
    /// this program changes at runtime are merged in now.
    /// </summary>
    public void Save()
    {
        try
        {
            var target = ReadFileAsJson();

            if (target is null)
            {
                // Nothing readable there: write the file in full. This is also
                // what makes "delete settings.json to get the defaults back"
                // produce a complete file again. A file that is there but
                // won't parse - broken by a hand edit while DeskMon runs - is
                // copied aside first, rather than written over and lost.
                KeepUnreadableCopy();
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
                return;
            }

            var mine = JsonSerializer.SerializeToNode(this, Options)?.AsObject();
            if (mine is null)
                return;

            // A key the file doesn't have yet is added with its default, so a
            // file written by an older build gains new settings rather than
            // hiding them. A key already present is never overwritten.
            foreach (var (key, value) in mine)
                if (!target.ContainsKey(key))
                    target[key] = value?.DeepClone();

            foreach (string key in OwnedAtRuntime)
                target[key] = mine[key]?.DeepClone();

            foreach (string key in _edited)
                target[key] = mine[key]?.DeepClone();

            AtomicFile.WriteAllText(FilePath, target.ToJsonString(Options));
        }
        catch
        {
            // Read-only folder or a locked file - not worth bothering the user.
        }
    }

    /// <summary>
    /// settings.json as it is on disk this moment, or null when there is no
    /// file or it cannot be parsed.
    /// </summary>
    private static JsonObject? ReadFileAsJson()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            return JsonNode.Parse(File.ReadAllText(FilePath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch
        {
            return null;
        }
    }
}
