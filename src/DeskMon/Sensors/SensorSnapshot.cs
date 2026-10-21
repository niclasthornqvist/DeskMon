using System;
using System.Collections.Generic;

namespace DeskMon.Sensors;

/// <summary>
/// One complete reading of the machine. Every value is nullable: null means
/// "this sensor isn't available" (no driver, no such hardware) and the widget
/// renders "n/a" rather than a misleading zero.
/// </summary>
public sealed class SensorSnapshot
{
    // --- Cheap Win32 counters, no driver needed ---
    public double? CpuLoad { get; set; }     // %
    public double? RamLoad { get; set; }     // %
    public double? SwapLoad { get; set; }    // % of pagefile in use
    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }
    public double? SwapUsedGb { get; set; }
    public double? SwapTotalGb { get; set; }

    // --- LibreHardwareMonitor (CPU temp needs administrator) ---
    public double? CpuTemp { get; set; }     // degrees C
    public double? CpuPower { get; set; }    // W
    public double? CpuClock { get; set; }    // MHz, highest active core
    public double? GpuTemp { get; set; }     // degrees C
    public double? GpuLoad { get; set; }     // %
    public double? GpuMemoryLoad { get; set; } // %
    public double? GpuPower { get; set; }    // W

    // --- PresentMon, only while a game is running (see FpsMonitor) ---
    public double? Fps { get; set; }

    /// <summary>
    /// The frame rate only 1% (and 0.1%) of frames were worse than. These are
    /// the stutter figures: an average of 140 fps says nothing about the 50 ms
    /// frame that arrives every half second, and these do. Null until enough
    /// frames have been seen to mean anything.
    /// </summary>
    public double? FpsOnePercentLow { get; set; }
    public double? FpsPointOnePercentLow { get; set; }

    /// <summary>
    /// Display latency in milliseconds: from the game starting work on a frame
    /// to that frame being on screen.
    ///
    /// Not the same thing as the frame time, and the difference is the point. A
    /// game can hold a steady 10 ms frame time while every frame reaches the
    /// screen 60 ms after the work on it began, and that gap is what the mouse
    /// feels like. Null when PresentMon reports no latency for the frames seen
    /// so far - a windowed game, typically.
    /// </summary>
    public double? Latency { get; set; }

    /// <summary>
    /// <see cref="GpuLoad"/>, but only while a game is running - null, so n/a,
    /// otherwise. For the GAME section's GPU row, which would read a desktop
    /// figure beside an FPS of n/a and look as though it belonged to a game.
    /// Filled in by MainWindow alongside the frame rates.
    /// </summary>
    public double? GameGpuLoad { get; set; }

    // --- Full sensor lists, for the metric rows that can expand ---

    /// <summary>
    /// Every temperature the CPU reports, in the order the library lists them.
    /// <see cref="CpuTemp"/> is one of these; this is the lot.
    /// </summary>
    public IReadOnlyList<SensorReading> CpuTemperatures { get; set; } = Array.Empty<SensorReading>();

    /// <summary>Every temperature the graphics card reports.</summary>
    public IReadOnlyList<SensorReading> GpuTemperatures { get; set; } = Array.Empty<SensorReading>();

    /// <summary>The programs holding the most RAM, for the list under RAM. See MemoryUsers.</summary>
    public IReadOnlyList<SensorReading> TopRamUsers { get; set; } = Array.Empty<SensorReading>();

    /// <summary>The programs that have been given the most memory in all - what fills the page file.</summary>
    public IReadOnlyList<SensorReading> TopCommitUsers { get; set; } = Array.Empty<SensorReading>();

    // --- Disk and network, from Windows' own counters ---

    public double? DiskRead { get; set; }    // MB/s, every drive together
    public double? DiskWrite { get; set; }   // MB/s

    /// <summary>How full the fullest drive is, in percent - the one that runs out first.</summary>
    public double? DiskFullest { get; set; }

    /// <summary>Every fixed drive, named with its free space, valued by how full it is.</summary>
    public IReadOnlyList<SensorReading> Drives { get; set; } = Array.Empty<SensorReading>();

    public double? NetDown { get; set; }     // Mbit/s, every adapter together
    public double? NetUp { get; set; }       // Mbit/s

    /// <summary>
    /// Round trip to PingHost in milliseconds - only while a game runs and a
    /// host is set; null otherwise. Filled in by MainWindow, like the frame rates.
    /// </summary>
    public double? Ping { get; set; }
}
