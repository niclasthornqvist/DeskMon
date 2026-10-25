using System.Collections.Generic;

namespace DeskMon.Metrics;

/// <summary>
/// ===========================================================================
///  THIS IS THE FILE TO EDIT WHEN YOU WANT TO ADD OR REMOVE A ROW.
/// ===========================================================================
/// The widget draws these sections in order, each with its title and one row
/// per metric. Add a line, rebuild (Build.ps1), done - no other file needs to
/// change. Add a whole new MetricSection the same way.
///
/// Values come from <see cref="Sensors.SensorSnapshot"/>, which already exposes
/// more than is shown by default. Uncomment the extras below to try them.
/// To read something that isn't in the snapshot yet, add a property to
/// SensorSnapshot and fill it in SensorHub.
/// </summary>
public static class MetricRegistry
{
    public static readonly List<MetricSection> Sections = new()
    {
        // Every row has a Tooltip: what it measures on the first line, how to
        // read it under that. The amber and red levels are added to it by
        // MetricRow from Warn and Crit, so they are not repeated here.
        new MetricSection("Usage",
            new Metric("CPU",  "%", s => s.CpuLoad,  Max: 100, Warn: 80, Crit: 95,
                       Tooltip: "How busy the processor is, averaged over all its cores.\n" +
                                "A game can be held back by one busy core while this still reads low.",
                       AlertName: "CPU load"),
            // Both memory rows unfold the programs behind them, so a red row
            // says what to close: under RAM what each holds in memory now,
            // under SWAP what each has been given in all, which is what spills
            // into the page file.
            new Metric("RAM",  "%", s => s.RamLoad,  Max: 100, Warn: 80, Crit: 92,
                       Tooltip: "How much of the memory is in use.\n" +
                                "When it runs out, Windows moves the overflow to the page file on disk - see SWAP.",
                       Details: s => s.TopRamUsers,
                       DetailsAbout: "the programs using the most memory",
                       DetailNote: "One of the programs holding the most memory right now, every process of it together.\nRight-click to end it.",
                       AlertName: "Memory use"),
            new Metric("SWAP", "%", s => s.SwapLoad, Max: 100, Warn: 50, Crit: 80,
                       Tooltip: "How much of the page file is in use - memory Windows has moved out to disk.\n" +
                                "High while RAM is full means the PC is short of memory, and games stutter reading it back.",
                       Details: s => s.TopCommitUsers,
                       DetailsAbout: "the programs that have asked for the most memory",
                       DetailNote: "One of the programs that have been given the most memory in all - what fills the page file once RAM is full. Closing it frees its share.\nRight-click to end it.",
                       AlertName: "Page file use")

            // ---- Extras, ready to use: just delete the "//" --------------
            // , new Metric("GPU",  "%",  s => s.GpuLoad,       Max: 100, Warn: 90)
            // , new Metric("RAM",  "GB", s => s.RamUsedGb,     Max: 32, Format: "0.0")
            // , new Metric("SWAP", "GB", s => s.SwapUsedGb,    Max: 16, Format: "0.0")
        )
        {
            // A pulse line: load is activity over time, and this is the shape
            // everyone already reads as "activity".
            Icon = "M0.5,6 H3 L4.5,2 L6.5,9.5 L8,5 H10.5",
            Tooltip = "How hard the PC is working right now.",
        },

        // Temperatures start the bar at 30 C so the bar actually moves in the
        // range that matters instead of sitting half-full all the time.
        //
        // The CPU row carries Details, which gives it an arrow that unfolds
        // every temperature the chip reports. The headline number is still
        // Core (Tctl/Tdie) - the one the firmware itself uses for boost and fan
        // decisions - but it runs well above the CCD and cache sensors, so
        // comparing against another tool used to mean guessing which of the six
        // it was showing. Now you can just look.
        new MetricSection("Temperature",
            new Metric("CPU", "°C", s => s.CpuTemp, Min: 30, Max: 95, Warn: 80, Crit: 90,
                       Details: s => s.CpuTemperatures,
                       Tooltip: "The processor's temperature - the sensor it uses itself to decide how fast to run.\n" +
                                "Too hot, it slows itself down to cool off, and the frame rate drops with it.",
                       AlertName: "CPU temperature",
                       // The slowing down itself, which the temperature can't
                       // show: 85 °C is fine on one chip and throttling on
                       // another. See ThrottleWatch for how it is noticed.
                       Flag: s => s.CpuThrottledFrom is double from && s.CpuClock is double now
                           ? $"Slowing down to cool off: the clock dropped from {from:0} to {now:0} MHz under load.\n" +
                             "The frame rate drops with it. Better cooling or a cleaner case is the fix."
                           : null),
            // The card's sensors the same way: core, hot spot and memory
            // junction, which on a 4070 SUPER sit about 12 K apart.
            new Metric("GPU", "°C", s => s.GpuTemp, Min: 30, Max: 90, Warn: 78, Crit: 85,
                       Details: s => s.GpuTemperatures,
                       Tooltip: "The graphics card's temperature.\n" +
                                "Too hot, it lowers its clocks to cool off, and the frame rate drops with it.",
                       AlertName: "GPU temperature")
        )
        {
            // A thermometer: rounded tube, bulb at the foot, and the column
            // inside it.
            Icon = "M4,6.4 V2 A1.5,1.5 0 0 1 7,2 V6.4 A2.6,2.6 0 1 1 4,6.4 Z M5.5,4 V8",
            Tooltip = "How hot the processor and graphics card are running.",
        },

        // Measured only while a game is running - n/a otherwise, or hidden with
        // HideFpsWithoutGame. FPS is inverted: the thresholds
        // mean "below 60 is amber, below 30 is red".
        //
        // LOW and WORST are the stutter figures. An average can read 140 fps
        // while the game feels broken, because it says nothing about the 50 ms
        // frame that lands every half second - these are those frames.
        //
        // Named in plain words rather than "1%" and "0.1%", which mean nothing
        // unless you already read frame-time benchmarks. The exact definition is
        // a sentence away on hover, which is the right place for it: the label
        // has to fit a narrow column, the explanation does not.
        //
        // Their thresholds sit lower than the average's, since a low is always
        // worse than the mean it came from.
        //
        // The three under FPS fold away behind its arrow, like the sensor
        // lists under CPU and GPU temperature: FPS is the number you glance at,
        // the rest is for when it looks wrong. FoldsUnder keeps them as real
        // rows - bar, colour, peak and session history all as before.
        new MetricSection("Game",
            new Metric("FPS", "", s => s.Fps, Max: 240, Warn: 60, Crit: 30, Invert: true,
                       Tooltip: "Frames per second: how many pictures the game puts on screen each second.\n" +
                                "Higher is smoother. n/a until a game is running and PresentMon has frames to count."),

            new Metric("LOW", "", s => s.FpsOnePercentLow, Max: 240, Warn: 50, Crit: 25, Invert: true,
                       Tooltip: "The frame rate during the slowest 1% of frames - the dips you feel.\n" +
                                "Close to the average above means a smooth game.",
                       FoldsUnder: "FPS"),

            new Metric("WORST", "", s => s.FpsPointOnePercentLow, Max: 240, Warn: 40, Crit: 20, Invert: true,
                       Tooltip: "The frame rate during the slowest 0.1% of frames - the worst stutters.\n" +
                                "Needs about 15 seconds of play before it means anything.",
                       FoldsUnder: "FPS"),

            // Not inverted, unlike the three above: here a high number is the
            // bad one. Thresholds are deliberately loose - 25 ms is around the
            // point a mouse starts to feel indirect, 50 ms is unmistakable -
            // and the bar fills at 60 ms, since beyond that the figure matters
            // more than where it sits on a bar.
            new Metric("LAT", "ms", s => s.Latency, Max: 60, Warn: 25, Crit: 50,
                       Tooltip: "Display latency: from the game starting work on a frame to that frame being on screen.\n" +
                                "Not the frame time - a game can hold a steady 10 ms frame time and still put it on screen 60 ms late.\n" +
                                "Shows n/a in a windowed game, which has no latency to report.",
                       FoldsUnder: "FPS"),

            // How busy the graphics card is - next to the frame rate because
            // together they say what is holding it back. No thresholds: a
            // card at 99% in a game is doing its job, not overheating, so
            // there is nothing to turn amber for. Read with the other card
            // sensors, so it refreshes with the temperatures (SensorIntervalMs).
            // n/a with no game running, like the rows above it.
            new Metric("GPU", "%", s => s.GameGpuLoad, Max: 100,
                       Tooltip: "How busy the graphics card is.\n" +
                                "Near 100%, the card is what limits the frame rate.\n" +
                                "Well below it with a low frame rate, the CPU or the game itself is holding it back."),

            // In GAME, not Usage: graphics memory runs out before RAM does on
            // high texture settings, and that is when it matters. Thresholds
            // sit high and there is no alert, because many games fill the card
            // on purpose as a cache - 90% alone is not a fault, only near 100%
            // together with stutter is. Read with the other card sensors.
            new Metric("VRAM", "%", s => s.GpuMemoryLoad, Max: 100, Warn: 90, Crit: 97,
                       Tooltip: "How much of the graphics card's own memory is in use.\n" +
                                "Many games fill it on purpose, so a high figure alone is fine.\n" +
                                "Near 100% with stutters, lower the texture setting - the card is moving textures in and out over PCIe.")
        )
        {
            OnlyWhileGaming = true,
            Tooltip = "How the running game is doing, measured while it plays - with its name and how long you have played it.",

            // A gamepad: body, a d-pad on the left, two buttons on the right.
            // The buttons are tiny circles, which the stroke fills in to dots -
            // not zero-length lines, which WPF may skip drawing altogether.
            Icon = "M2.5,3 H8.5 A2,2 0 0 1 10.5,5 V7 A2,2 0 0 1 8.5,9 H2.5 A2,2 0 0 1 0.5,7 V5 A2,2 0 0 1 2.5,3 Z " +
                   "M3,4.8 V7.2 M1.8,6 H4.2 " +
                   "M7,5.3 A0.4,0.4 0 1 1 7.8,5.3 A0.4,0.4 0 1 1 7,5.3 " +
                   "M8.2,6.8 A0.4,0.4 0 1 1 9,6.8 A0.4,0.4 0 1 1 8.2,6.8",
        },

        // Every drive together: how fast it is reading and writing, from the
        // counters Task Manager uses. No thresholds on the speeds - a drive
        // busy while a game loads is doing its job. FULL is the one that runs
        // out first, and unfolds every drive with its free space.
        new MetricSection("Disk",
            new Metric("READ", "MB/s", s => s.DiskRead, Max: 500,
                       Tooltip: "How fast the drives are reading, all of them together.\n" +
                                "High while a game loads or streams in a new area; a steady trickle is normal."),
            new Metric("WRITE", "MB/s", s => s.DiskWrite, Max: 500,
                       Tooltip: "How fast the drives are writing, all of them together.\n" +
                                "Downloads, game updates and saves show up here."),
            new Metric("FULL", "%", s => s.DiskFullest, Max: 100, Warn: 90, Crit: 95,
                       Tooltip: "How full the fullest drive is - the one that will run out first.\n" +
                                "A nearly full system drive slows Windows down and can stop games updating.",
                       Details: s => s.Drives,
                       DetailsAbout: "every drive and its free space",
                       DetailNote: "How full this drive is, with its free space in the name.",
                       AlertName: "Disk space")
        )
        {
            // A drive: a flat box with its activity light.
            Icon = "M0.5,3.5 H10.5 V8.5 H0.5 Z M8,6 H8.6",
            Tooltip = "How busy the drives are and how much room is left on them.",
        },

        // Download and upload across every connected adapter, in the megabits
        // internet plans are sold in. PING folds under DOWN: it only reads
        // while a game runs, and only with a server set in PingHost.
        new MetricSection("Network",
            new Metric("DOWN", "Mb/s", s => s.NetDown, Max: 100,
                       Tooltip: "How fast data is coming in, every network adapter together.\n" +
                                "Measured in megabits per second, the unit internet speeds are sold in."),
            new Metric("UP", "Mb/s", s => s.NetUp, Max: 20,
                       Tooltip: "How fast data is going out, every network adapter together.\n" +
                                "A game sends little; a large upload here can make an online game lag."),
            new Metric("PING", "ms", s => s.Ping, Max: 150, Warn: 60, Crit: 120,
                       Tooltip: "The round trip to the server in the PingHost setting - the delay an online game feels.\n" +
                                "Measured every two seconds while a game runs; n/a otherwise, or with no server set.",
                       FoldsUnder: "DOWN",
                       AlertName: "Ping")
        )
        {
            // Two arrows, down and up.
            Icon = "M3,1 V9.5 M0.8,7.3 L3,9.5 L5.2,7.3 M8,10 V1.5 M5.8,3.7 L8,1.5 L10.2,3.7",
            Tooltip = "How much the PC is downloading and uploading.",
        },

        // ---- Another section, ready to use: delete the "/*" and "*/" ------
        /*
        new MetricSection("Power",
            new Metric("CPU", "W",   s => s.CpuPower, Max: 120),
            new Metric("GPU", "W",   s => s.GpuPower, Max: 250),
            new Metric("CLK", "MHz", s => s.CpuClock, Max: 5500)
        ),
        */
    };
}
