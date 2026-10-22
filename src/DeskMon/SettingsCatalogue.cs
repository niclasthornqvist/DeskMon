namespace DeskMon;

/// <summary>
/// ===========================================================================
///  ADD A LINE HERE WHEN YOU ADD A SETTING.
/// ===========================================================================
/// What the settings window shows, group by group, in the order it shows them.
///
/// Only the key, what to call it, a line of help and a few layout hints.
/// Everything else - which control to draw, how to read the value, how to
/// write it back - comes from the property on <see cref="AppSettings"/> that
/// the key names: a bool gets a tick box, a number or a path gets a text box, a
/// key ending in "Color" gets a text box with a swatch, a list gets a
/// comma-separated box, and a number given a Slider range gets a slider beside
/// its box. So adding a setting is one property there and one line here.
///
/// SectionTitles is the one exception to that: it is a map from section name to
/// heading rather than a single value, so the window expands it into one box
/// per section, named from MetricRegistry.
///
/// What is deliberately not here: window positions, which sections are folded
/// and which rows are unfolded. Those are not settings, they are what the
/// program remembers about how you left it, and a form full of them would bury
/// the things you actually came to change.
///
/// The order puts what people change first - how the widget looks and
/// behaves - and the fine-tuning (column widths, refresh intervals) last,
/// where it is out of the way until someone goes looking for it.
/// </summary>
public static class SettingsCatalogue
{
    /// <param name="Key">
    /// The property on <see cref="AppSettings"/>, which is also the key in
    /// settings.json. Shown on hover, so the window still teaches you the file.
    /// </param>
    /// <param name="Label">
    /// What the window calls it. The key used to be the label, and keys like
    /// "AutoOpenNotesWithGame" were hard to read and said little about what
    /// they did - so the window now speaks plain words and the file keeps its
    /// keys.
    /// </param>
    /// <param name="Help">
    /// What it does, in a sentence, shown under the label. Visible rather than
    /// a tooltip, since a tooltip is help you have to know is there. Empty when
    /// the label says it all.
    /// </param>
    /// <param name="NeedsReload">
    /// True for a setting nothing re-reads while running - it is captured when
    /// something is built, so it only takes hold on the next start. The window
    /// tags these and offers the restart.
    /// </param>
    /// <param name="Choices">A fixed set of values, which gets a dropdown.</param>
    /// <param name="Unit">Shown after the box, so "1000" is not a guess between seconds and milliseconds.</param>
    /// <param name="Wide">
    /// The box goes under the text, full width - for paths and lists, which a
    /// narrow box beside the label would cut off.
    /// </param>
    /// <param name="Kind">
    /// A control the property's type can't imply: a font is a string like any
    /// other, but wants a list of installed fonts rather than a box to type a
    /// name into and hope.
    /// </param>
    /// <param name="Slider">
    /// For a number: a slider beside its box, over this range. The box stays
    /// and still takes any value, so the range only needs to cover what is
    /// sensible, not everything that works - typing 50000 ms is still allowed.
    /// The minimum should match whatever floor the code that reads the setting
    /// already enforces, or the low end of the slider would do nothing.
    /// </param>
    public sealed record Setting(
        string Key,
        string Label,
        string Help,
        bool NeedsReload = false,
        string[]? Choices = null,
        string? Unit = null,
        bool Wide = false,
        EditorKind Kind = EditorKind.Auto,
        SliderRange? Slider = null);

    /// <param name="Step">What one notch is worth, and what the value snaps to while dragging.</param>
    public sealed record SliderRange(double Min, double Max, double Step);

    // Shared by every opacity: Math.Clamp(…, 0.1, 1.0) is applied wherever one is used.
    private static readonly SliderRange OpacitySlider = new(0.1, 1, 0.01);

    /// <summary>Which control a setting gets, when its type alone doesn't say.</summary>
    public enum EditorKind
    {
        /// <summary>Decided by the property: tick box, number, colour, text.</summary>
        Auto,

        /// <summary>A dropdown of the fonts installed on this machine.</summary>
        Font,

        /// <summary>A path with a Browse button that opens Windows' folder dialog.</summary>
        Folder,

        /// <summary>A path to an .exe, with a Browse button that opens the file dialog.</summary>
        Program,

        /// <summary>Program names as removable tags, added from what is running.</summary>
        ProgramList,

        /// <summary>
        /// The logon task, kept by Windows rather than in settings.json - so
        /// the key names no AppSettings property; see Autostart.
        /// </summary>
        Startup,
    }

    /// <param name="Note">One line under the heading, for what every setting in the group shares.</param>
    public sealed record Group(string Title, string? Note, params Setting[] Settings);

    private const string OpacityRange = "0.1 – 1";

    public static readonly Group[] Groups =
    {
        // First: it changes everything after it. The dropdown is drawn by the
        // window itself (ThemeRow), with the picked theme described under it.
        new("Theme", "The look of the whole program at once: colours, fonts and corners. Anything a theme sets can still be changed below - doing so makes it Custom.",
            new Setting("Theme", "Theme", "", Choices: ThemeCatalogue.Names),
            new Setting("AccentColor", "Accent colour", "What lights up: open windows on the toolbar, ticked boxes, sliders, selected rows, links and the sessions chart."),
            new Setting("WindowFontFamily", "Window font", "Every window but the widget, with its menus and tooltips. The notes keep their own.", Kind: EditorKind.Font),
            new Setting("CornerRadius", "Corner roundness", "0 is square.", Unit: "px", Slider: new(0, 24, 1)),
            new Setting("Glow", "Glow", "A soft light around the widget and the windows."),
            new Setting("GlowColor", "Glow colour", "Leave empty to glow in the accent colour.")),

        new("Widget", null,
            new Setting("StartWithWindows", "Start with Windows", "Starts DeskMon when you log in, without a UAC prompt.", Kind: EditorKind.Startup),
            new Setting("Opacity", "Opacity", "How solid the widget is on the desktop. 1 is fully solid.", Unit: OpacityRange, Slider: OpacitySlider),
            new Setting("GameOpacity", "Opacity during games", "Lower, so the widget doesn't distract from the game.", Unit: OpacityRange, Slider: OpacitySlider),
            new Setting("WidgetBackdrop", "DeskMon picture", "How strongly DeskMon's logo shows behind the readings. 0 hides it.", Unit: "0 – 1", Slider: new(0, 1, 0.01)),
            new Setting("AlwaysOnTop", "Always on top", "Keep the widget above other windows."),
            new Setting("Locked", "Lock position", "Stops the widget from being dragged by accident. Its buttons still work."),
            new Setting("WidgetLayout", "Layout", "vertical stacks the sections one under another. horizontal puts them side by side, for the top or bottom of a screen.",
                        Choices: new[] { "vertical", "horizontal" }),
            new Setting("FontFamily", "Font", "A fixed-width one, like Consolas, stops the numbers jumping as they change.", Kind: EditorKind.Font),
            new Setting("FontSize", "Text size", "Everything else on the widget scales with it.", Slider: new(8, 24, 0.5)),
            new Setting("ShowBars", "Show bars", "A bar beside each reading."),
            new Setting("ShowPeaks", "Show peaks", "The highest reading so far (lowest for FPS), after the bar."),
            new Setting("ShowHardwareNames", "Show hardware names", "Your CPU and graphics card at the top of the widget.", NeedsReload: true)),

        new("Section headings", "Rename the headings on the widget. Leave a box empty for the default name.",
            new Setting("SectionTitles", "", "")),

        // Expanded by the window into a tick-box grid, a row per section and
        // a column per layout - like SectionTitles, one setting standing for
        // two lists: HiddenSections and HiddenSectionsInGame.
        new("Sections shown", "Untick a section to leave it off the widget, heading and all - separately for the desktop and while a game runs.",
            new Setting("HiddenSections", "", "")),

        new("Widget colours", "Click a swatch to pick a colour and its opacity. Default puts the built-in colour back.",
            new Setting("BackgroundColor", "Background", ""),
            new Setting("BorderColor", "Border", "The outline around the widget."),
            new Setting("LabelColor", "Labels", "Names like CPU and RAM."),
            new Setting("HeaderColor", "Hardware names and peaks", ""),
            new Setting("SectionColor", "Headings", "USAGE, TEMPERATURE and GAME."),
            new Setting("SeparatorColor", "Divider lines", "The line between sections."),
            new Setting("BarGradient", "Gradient bars", "Fill the bars on the widget and in the sessions chart with the gradient below, instead of one colour."),
            new Setting("BarGradientStartColor", "Gradient start", "The colour at the empty end of a bar."),
            new Setting("BarGradientEndColor", "Gradient end", "The colour a full bar reaches."),
            new Setting("BarColor", "Bar", "Without the gradient. Leave empty to colour each bar like its reading: green, amber, red."),
            new Setting("BarTrackColor", "Bar background", "The empty part of the bar."),
            new Setting("NormalColor", "Normal reading", "Below the warning level."),
            new Setting("WarnColor", "Warning", "At or above the warning level."),
            new Setting("CritColor", "Critical", "At or above the critical level."),
            new Setting("UnavailableColor", "Unavailable", "Readings shown as n/a.")),

        new("Games and FPS", null,
            new Setting("MeasureFps", "Measure FPS", "Count frames with PresentMon while a game runs."),
            new Setting("HideFpsWithoutGame", "Hide FPS without a game", "Hide the FPS section on the widget while no game is running. Off shows it all the time, as n/a until a game starts."),
            new Setting("ShowFpsOverlay", "FPS overlay", "The frame rate in large figures in the top-right corner of the game's screen. Clicks go through it to the game."),
            new Setting("FpsOverlayHotkey", "FPS overlay hotkey", "Shows and hides the FPS overlay from inside the game. Empty turns it off."),
            new Setting("FpsOverlaySize", "FPS overlay size", "", Slider: new(16, 96, 1)),
            new Setting("FpsOverlayOpacity", "FPS overlay opacity", "", Unit: OpacityRange, Slider: OpacitySlider),
            // No reload: read when each game starts.
            new Setting("SteamPlaytime", "Playtime from Steam", "For Steam games, the playtime after the game's name is Steam's total, read from Steam's files on this PC. Off, or for other games, it is the time DeskMon has logged."),
            new Setting("GogPlaytime", "Playtime from GOG Galaxy", "For GOG games, the playtime is GOG Galaxy's total, read from Galaxy's database on this PC. Off, or without Galaxy, it is the time DeskMon has logged."),
            // No reload: read each time the sessions window looks for a picture.
            new Setting("OnlineGameArt", "Game pictures from the Steam store", "When a game has no picture or logo on this PC, the sessions window looks it up on the Steam store by name, and asks Steam's servers anonymously for a newer game's logo. Copies are kept in app\\cache. Off sends nothing and shows a drawn picture instead."),
            new Setting("PauseWhenFullscreen", "Hide during games", "Hide the widget completely while a game is in front, instead of showing it over the game."),
            new Setting("GameLayout", "Separate layout for games", "Sections you fold while a game runs are remembered apart from the desktop's, and the widget switches by itself. Starts with DISK and NETWORK folded in games."),
            // No reload: PerformanceScore reads it every time a score is shown.
            new Setting("TargetFps", "Target frame rate", "The frame rate a game needs for full marks in its performance score in the sessions window - your monitor's refresh rate is a good choice. Every session is scored again when it changes.", Unit: "fps", Slider: new(30, 360, 1)),
            new Setting("PingHost", "Ping server", "Pinged every two seconds while a game runs, for the PING row under NETWORK: a game server's address, or 1.1.1.1. Empty pings nothing."),
            new Setting("PreferredGpu", "Graphics card", "Which card to report. auto picks a dedicated card over the one built into the CPU.",
                        NeedsReload: true, Choices: new[] { "auto", "nvidia", "amd", "intel" }),
            new Setting("NotGames", "Never treat as a game", "Keeps fullscreen browsers and video players from counting as games. Add from what is running, or type a name.", Wide: true, Kind: EditorKind.ProgramList),
            new Setting("AlwaysGames", "Always treat as a game", "For games played in a window, which otherwise aren't noticed: measured and logged whenever they are in front, fullscreen or not.", Wide: true, Kind: EditorKind.ProgramList)),
            // PresentMonPath is left out on purpose: settings.json only. The
            // automatic search finds every normal install, and a box here made
            // it too easy to point an administrator process at any program.

        new("Notes and sessions", null,
            new Setting("AutoOpenNotesWithGame", "Open notes with a game", "Show the notepad when a game starts."),
            new Setting("NotesAlwaysOnTop", "Windows on top", "Keep the notepad, hardware, versions and settings windows above the game."),
            new Setting("LogSessionToFile", "Log sessions", "Write a line about each session to the session history when you quit a game."),
            new Setting("ScreenshotHotkey", "Screenshot hotkey", "Puts a screenshot of the game into its notes, from inside the game. Empty turns it off."),
            // Floor 10: Screenshot.Save clamps there, below it is only smear.
            new Setting("ScreenshotQuality", "Screenshot quality", "The full-size file. 100 saves a lossless PNG; lower saves a much smaller JPEG - 90 is hard to tell from the original.", Slider: new(10, 100, 5)),
            // No reload any more: every window repaints on UiTheme.Changed.
            new Setting("NotesBackgroundColor", "Window background", "Background of the notepad and the other windows."),
            new Setting("NotesTextColor", "Notes text colour", "Normal text colour in the notepad and the other windows."),
            new Setting("NotesFolder", "Notes folder", "Where notes are saved. Empty means app\\notes.", NeedsReload: true, Wide: true, Kind: EditorKind.Folder),
            new Setting("SessionFolder", "Session history folder", "Empty means app\\sessions.", NeedsReload: true, Wide: true, Kind: EditorKind.Folder)),

        new("Alerts and updates", null,
            new Setting("Alerts", "Alerts", "A notification when a reading stays red - CPU or GPU too hot, memory or a drive nearly full. During a game it also shows under the FPS overlay."),
            new Setting("AlertAfterSeconds", "Alert after", "How long a reading has to stay red first, so a spike doesn't alert.", Unit: "s", Slider: new(10, 600, 10)),
            new Setting("CheckForUpdates", "Check for updates", "Once a day, look for a newer DeskMon and say so. Nothing is downloaded or installed."),
            new Setting("UpdateRepository", "Release page", "Where DeskMon is published, as owner/repository on GitHub. Empty checks nothing.", Wide: true)),

        // No NeedsReload: the hardware window re-reads every time it opens,
        // so a change already shows on the next open without a restart.
        new("Hardware window", null,
            new Setting("CheckDriverOnline", "Check online for a newer driver, BIOS and Windows", "Asks nvidia.com or amd.com about the graphics card, asus.com about an ASUS board, and Windows Update about Windows, and runs a short ping test (to PingHost, or 1.1.1.1), each time the hardware window opens. Off still warns when the driver or BIOS is over 3 months old (yellow) or a year old (red).")),

        // The rule in the note is PanelWindow.DimDuringGames: an unfocused
        // window during a game takes the lower of its own value and GameOpacity.
        new("Window opacity", "1 is fully solid. During a game, a window you aren't using is never more solid than Opacity during games.",
            new Setting("NotesOpacity", "Notepad", "", Unit: OpacityRange, Slider: OpacitySlider),
            new Setting("HardwareOpacity", "Hardware", "", Unit: OpacityRange, Slider: OpacitySlider),
            new Setting("VersionsOpacity", "Versions", "", Unit: OpacityRange, Slider: OpacitySlider),
            new Setting("SessionsOpacity", "Sessions", "", Unit: OpacityRange, Slider: OpacitySlider),
            // Dragging this one fades the very window the slider is in, so
            // near 0.1 the slider itself gets hard to see. Same floor anyway:
            // PanelWindow never goes below it, and the box still reads clearly.
            new Setting("SettingsOpacity", "Settings", "This window.", Unit: OpacityRange, Slider: OpacitySlider)),

        // Minimums are the floors MetricRow already applies: 16 px for the
        // peak column, 1 px for the bar's thickness.
        new("Widget layout", "The widget is as wide as what is in it, so wider columns make a wider widget.",
            new Setting("LabelWidth", "Label column", "Widen it if labels are cut off.", Unit: "px", Slider: new(20, 200, 1)),
            new Setting("ValueWidth", "Value column", "", Unit: "px", Slider: new(20, 200, 1)),
            new Setting("BarTrackWidth", "Bar length", "", Unit: "px", Slider: new(10, 250, 1)),
            new Setting("BarTrackHeight", "Bar thickness", "1 or 2 makes it a thin line.", Unit: "px", Slider: new(1, 20, 1)),
            new Setting("PeakWidth", "Peak column", "Also sets the gap between the bar and the peak figure.", Unit: "px", Slider: new(16, 120, 1))),

        // Minimums are the floors MainWindow already applies to each timer
        // (100, 250 and 500 ms), so the low end of a slider always does something.
        new("Refresh rate", "Slower means less work for your PC while it runs.",
            new Setting("IntervalMs", "Refresh", "How often the widget updates on the desktop.", Unit: "ms", Slider: new(100, 5000, 50)),
            new Setting("GameIntervalMs", "Refresh during games", "Slower by default, so the game gets the PC.", Unit: "ms", Slider: new(250, 10000, 250)),
            new Setting("SensorIntervalMs", "Temperature reads", "How often temperatures are really read. Reading them is slow, so this matters most for game performance.", NeedsReload: true, Unit: "ms", Slider: new(1000, 30000, 500)),
            new Setting("HiddenCheckMs", "Check while hidden", "While hidden behind a fullscreen app, how often to check whether it has gone.", Unit: "ms", Slider: new(500, 10000, 250))),
    };
}
