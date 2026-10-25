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
    /// <param name="Min">
    /// For a number: the lowest the box accepts - below it the box turns red
    /// and nothing is applied. Only where a lower value breaks something
    /// rather than merely doing little: a text size of 0 made WPF throw, and
    /// saved like that DeskMon could no longer start. Separate from the
    /// slider's range, whose low end is a suggestion that typing may go past.
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
        SliderRange? Slider = null,
        double? Min = null);

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

    // The groups follow what you came to change rather than which file reads
    // it: the widget's own look, then games, then the windows, and the
    // fine-tuning (refresh intervals) last. Each help line is one short
    // sentence - longer ones wrapped to three or four lines in the window and
    // stopped being read. Opacities have no unit: their slider already shows
    // the range, and "0.1 – 1" squeezed the box beside it.
    public static readonly Group[] Groups =
    {
        // First: it changes everything after it. The dropdown is drawn by the
        // window itself (ThemeRow), with the picked theme described under it.
        // The windows' colours are here, not under Notes: they colour every
        // window, as the rest of this group does.
        new("Theme", "Colours, fonts and corners in one go. Changing any of them below makes it Custom.",
            new Setting("Theme", "Theme", "", Choices: ThemeCatalogue.Names),
            new Setting("AccentColor", "Accent colour", "Ticked boxes, sliders, selected rows, links and the sessions chart."),
            new Setting("WindowFontFamily", "Window font", "Every window but the widget. The notes keep their own.", Kind: EditorKind.Font),
            // No reload any more: every window repaints on UiTheme.Changed.
            new Setting("NotesBackgroundColor", "Window background", "The notepad and the other windows."),
            new Setting("NotesTextColor", "Window text", "Normal text in the notepad and the other windows."),
            new Setting("CornerRadius", "Corner roundness", "0 is square.", Unit: "px", Slider: new(0, 24, 1)),
            new Setting("Glow", "Glow", "A soft light around the widget and the windows."),
            new Setting("GlowColor", "Glow colour", "Empty glows in the accent colour.")),

        new("Widget", null,
            new Setting("StartWithWindows", "Start with Windows", "Starts DeskMon when you log in, without a UAC prompt.", Kind: EditorKind.Startup),
            new Setting("AlwaysOnTop", "Always on top", "Keep the widget above other windows."),
            new Setting("Locked", "Lock position", "Stops it being dragged by accident. Its buttons still work."),
            new Setting("Opacity", "Opacity", "1 is fully solid.", Slider: OpacitySlider),
            new Setting("GameOpacity", "Opacity during games", "Lower, so the widget doesn't distract from the game.", Slider: OpacitySlider),
            new Setting("WidgetBackdrop", "DeskMon picture", "The logo behind the readings. 0 hides it.", Slider: new(0, 1, 0.01)),
            new Setting("WidgetLayout", "Layout", "Horizontal puts the sections side by side.",
                        Choices: new[] { "vertical", "horizontal" }),
            new Setting("FontFamily", "Font", "Fixed-width, like Consolas, keeps the numbers from jumping.", Kind: EditorKind.Font),
            new Setting("FontSize", "Text size", "Everything on the widget scales with it.", Slider: new(8, 24, 0.5), Min: 6),
            new Setting("SectionAccent", "Heading accent", "A short bar before each section heading."),
            new Setting("ShowHardwareNames", "Show hardware names", "Your CPU and graphics card at the top.", NeedsReload: true)),

        // The bars and the columns they sit in, together: the columns are
        // mostly the bar's length and the space around it. Minimums are the
        // floors MetricRow already applies: 16 px for the peak column, 1 px
        // for the bar's thickness.
        new("Bars and columns", "Wider columns make a wider widget.",
            new Setting("ShowBars", "Show bars", "A bar beside each reading."),
            new Setting("BarStyle", "Bar style", "Pill, square, blocks, dots, stripes, 10% ticks, outline, outline with stripes, or a line with a knob.",
                        Choices: new[] { "rounded", "flat", "segmented", "dots", "striped", "ticks", "outlined", "outlined-striped", "line" }),
            new Setting("ValueInBar", "Value inside the bar", "The number on the bar, which then fills the row between label and peak."),
            new Setting("BarTrackWidth", "Bar length", "", Unit: "px", Slider: new(10, 250, 1), Min: 1),
            new Setting("BarTrackHeight", "Bar thickness", "Thin as a line at the low end, a thick block at the high end.", Unit: "px", Slider: new(1, 20, 1)),
            new Setting("ShowPeaks", "Show peaks", "The highest reading so far (lowest for FPS), after the bar."),
            new Setting("ShowPeakMarker", "Peak marker on the bar", "A thin tick where the peak reached."),
            new Setting("LabelWidth", "Label column", "Widen it if labels are cut off.", Unit: "px", Slider: new(20, 200, 1), Min: 0),
            new Setting("ValueWidth", "Value column", "", Unit: "px", Slider: new(20, 200, 1), Min: 0),
            new Setting("PeakWidth", "Peak column", "Also the gap between the bar and the peak.", Unit: "px", Slider: new(16, 120, 1))),

        new("Widget colours", "Click a swatch to pick a colour. Empty uses the built-in one.",
            new Setting("BackgroundColor", "Background", ""),
            new Setting("BorderColor", "Border", "The outline around the widget."),
            new Setting("LabelColor", "Labels", "Names like CPU and RAM."),
            new Setting("HeaderColor", "Hardware names and peaks", ""),
            new Setting("SectionColor", "Headings", "USAGE, TEMPERATURE, GAME and the rest."),
            new Setting("SeparatorColor", "Divider lines", "The line between sections."),
            new Setting("BarGradient", "Gradient bars", "Bars in the gradient below. Their end still turns amber and red."),
            new Setting("BarGradientStartColor", "Gradient start", "The empty end of a bar."),
            new Setting("BarGradientEndColor", "Gradient end", "The colour a full bar reaches."),
            new Setting("BarColor", "Bar", "Without the gradient. Empty colours each bar by its reading."),
            new Setting("BarTrackColor", "Bar background", "The empty part of the bar."),
            new Setting("NormalColor", "Normal reading", "Below the warning level."),
            new Setting("WarnColor", "Warning", "At or above the warning level."),
            new Setting("CritColor", "Critical", "At or above the critical level."),
            new Setting("UnavailableColor", "Unavailable", "Readings shown as n/a.")),

        // Expanded by the window into a tick-box grid, a row per section and
        // a column per layout - like SectionTitles, one setting standing for
        // two lists: HiddenSections and HiddenSectionsInGame.
        new("Sections shown", "Untick a section to hide it - separately on the desktop and in games.",
            new Setting("HiddenSections", "", "")),

        new("Section headings", "Rename the widget's headings. Empty keeps the default name.",
            new Setting("SectionTitles", "", "")),

        // What counts as a game and how the widget behaves around one. The
        // frame-rate settings have a group of their own below.
        new("Games", null,
            new Setting("PauseWhenFullscreen", "Hide during games", "Hide the widget completely while a game is in front."),
            new Setting("GameLayout", "Separate layout for games", "Sections folded in a game are remembered apart from the desktop's."),
            new Setting("GameStartCheck", "Check what runs beside a game", "After 20 s, warn about a heavy browser, Windows Update or a virus scan."),
            new Setting("PingHost", "Ping server", "Pinged every 2 s in games for the PING row. Empty pings nothing."),
            new Setting("NotGames", "Never treat as a game", "Fullscreen browsers and video players, for example.", Wide: true, Kind: EditorKind.ProgramList),
            new Setting("AlwaysGames", "Always treat as a game", "Games played in a window, which aren't noticed otherwise.", Wide: true, Kind: EditorKind.ProgramList)),
            // PresentMonPath is left out on purpose: settings.json only. The
            // automatic search finds every normal install, and a box here made
            // it too easy to point an administrator process at any program.

        new("FPS", null,
            new Setting("MeasureFps", "Measure FPS", "Count frames with PresentMon while a game runs."),
            new Setting("HideFpsWithoutGame", "Hide FPS without a game", "Off shows the FPS section as n/a until a game starts."),
            // No reload: PerformanceScore reads it every time a score is shown.
            new Setting("TargetFps", "Target frame rate", "Full marks in the performance score. Your monitor's refresh rate suits.", Unit: "fps", Slider: new(30, 360, 1)),
            new Setting("ShowFpsOverlay", "FPS overlay", "The frame rate in large figures, top right of the game."),
            new Setting("FpsOverlayHotkey", "FPS overlay hotkey", "Shows and hides the overlay in the game. Empty turns it off."),
            new Setting("FpsOverlaySize", "FPS overlay size", "", Unit: "px", Slider: new(16, 96, 1)),
            new Setting("FpsOverlayOpacity", "FPS overlay opacity", "", Slider: OpacitySlider)),

        new("Notes", null,
            new Setting("AutoOpenNotesWithGame", "Open notes with a game", "Show the notepad when a game starts."),
            new Setting("ScreenshotHotkey", "Screenshot hotkey", "Puts a screenshot of the game into its notes. Empty turns it off."),
            // Floor 10: Screenshot.Save clamps there, below it is only smear.
            new Setting("ScreenshotQuality", "Screenshot quality", "100 is a lossless PNG. Lower is a smaller JPEG - 90 looks the same.", Slider: new(10, 100, 5)),
            new Setting("NotesFolder", "Notes folder", "Empty uses %APPDATA%\\DeskMon\\notes.", NeedsReload: true, Wide: true, Kind: EditorKind.Folder)),

        // Playtime and pictures are here because the sessions window is where
        // they show most - the widget's playtime follows the same switches.
        new("Sessions", null,
            new Setting("LogSessionToFile", "Log sessions", "Save each play session to the history when the game quits."),
            // No reload: read when each game starts.
            new Setting("SteamPlaytime", "Playtime from Steam", "Steam's own total for Steam games, instead of DeskMon's."),
            new Setting("GogPlaytime", "Playtime from GOG Galaxy", "Galaxy's own total for GOG games, instead of DeskMon's."),
            // No reload: read each time the sessions window looks for a picture.
            new Setting("OnlineGameArt", "Game pictures from Steam", "Look up missing pictures on the Steam store. Off sends nothing."),
            // Min 0: a negative price would only turn the column into nonsense.
            new Setting("EnergyPrice", "Energy price", "Per kWh, for the Cost column. 0 hides it.", Unit: "/kWh", Min: 0),
            new Setting("EnergyCurrency", "Currency", "After the cost: kr, €, $. Empty shows just the number."),
            new Setting("SessionFolder", "Session history folder", "Empty uses %APPDATA%\\DeskMon\\sessions.", NeedsReload: true, Wide: true, Kind: EditorKind.Folder)),

        new("Alerts and updates", null,
            new Setting("Alerts", "Alerts", "Notify when a reading stays red - too hot, or nearly full."),
            new Setting("AlertAfterSeconds", "Alert after", "How long it has to stay red first, so a spike doesn't alert.", Unit: "s", Slider: new(10, 600, 10)),
            new Setting("CheckForUpdates", "Check for updates", "Once a day. Nothing is downloaded or installed."),
            new Setting("UpdateRepository", "Release page", "owner/repository on GitHub. Empty checks nothing.", Wide: true)),

        // No NeedsReload on the check: the hardware window re-reads every time
        // it opens, so a change already shows on the next open.
        new("Hardware", null,
            new Setting("PreferredGpu", "Graphics card", "Which card to report. Auto prefers a dedicated one.",
                        NeedsReload: true, Choices: new[] { "auto", "nvidia", "amd", "intel" }),
            new Setting("CheckDriverOnline", "Look online for updates", "Newer driver, BIOS and Windows, when the hardware window opens. Off still flags old ones.")),

        // The rule in the note is PanelWindow.DimDuringGames: an unfocused
        // window during a game takes the lower of its own value and GameOpacity.
        // Dragging Settings fades the very window the slider is in, so near 0.1
        // the slider itself gets hard to see. Same floor anyway: PanelWindow
        // never goes below it, and the box still reads clearly.
        new("Windows", "1 is fully solid. In games, a window not in use is no more solid than the widget.",
            new Setting("NotesAlwaysOnTop", "Windows on top", "Keep the notepad, hardware, versions and settings windows above the game."),
            new Setting("NotesOpacity", "Notepad opacity", "", Slider: OpacitySlider),
            new Setting("HardwareOpacity", "Hardware opacity", "", Slider: OpacitySlider),
            new Setting("VersionsOpacity", "Versions opacity", "", Slider: OpacitySlider),
            new Setting("SessionsOpacity", "Sessions opacity", "", Slider: OpacitySlider),
            new Setting("SettingsOpacity", "Settings opacity", "This window.", Slider: OpacitySlider)),

        // Minimums are the floors MainWindow already applies to each timer
        // (100, 250 and 500 ms), so the low end of a slider always does something.
        new("Refresh rate", "Slower means less work for your PC.",
            new Setting("IntervalMs", "Refresh", "How often the widget updates on the desktop.", Unit: "ms", Slider: new(100, 5000, 50)),
            new Setting("GameIntervalMs", "Refresh during games", "Slower by default, so the game gets the PC.", Unit: "ms", Slider: new(250, 10000, 250)),
            new Setting("SensorIntervalMs", "Temperature reads", "Reading temperatures is slow, so this matters most in games.", NeedsReload: true, Unit: "ms", Slider: new(1000, 30000, 500)),
            new Setting("HiddenCheckMs", "Check while hidden", "How often to check whether a fullscreen app has gone.", Unit: "ms", Slider: new(500, 10000, 250))),
    };
}
