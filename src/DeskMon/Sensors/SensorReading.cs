namespace DeskMon.Sensors;

/// <summary>
/// One sensor's own name and its current value - the unit of the lists an
/// expandable metric row shows.
///
/// This exists because "the CPU temperature" is not one number. A Ryzen 7800X3D
/// reports Tctl/Tdie, the CCD die, the IO die hotspot, both L3 caches and the
/// package, and they can sit 20 K apart at the same instant. The widget shows
/// one of them; expanding the row shows all of them, named as the chip names
/// them, so a figure from another tool can be matched up instead of guessed at.
///
/// The same lists also carry things that aren't sensors: the programs using
/// the most memory, the drives and how full they are. Those name things that
/// come and go, so a row's name is updated as well as its value, and their
/// figures are in a unit of their own - <paramref name="Text"/>.
/// </summary>
/// <param name="Text">Shown instead of <paramref name="Value"/> in its row's unit, e.g. "1.4 GB".</param>
public readonly record struct SensorReading(string Name, double? Value, string? Text = null);
