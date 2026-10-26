using System;
using System.Globalization;
using System.Windows.Data;

namespace DeskMon.Ui;

/// <summary>
/// Whether a scrollbar is at one end, so its arrow for that end can fade out:
/// there is nothing more that way, and a full-strength arrow would promise
/// content that isn't there. XAML triggers can only compare against a fixed
/// value, and the far end - Maximum - changes with every list's length, hence
/// a converter.
///
/// Values: Value, Minimum, Maximum. Parameter: "start" or "end".
/// </summary>
internal sealed class ScrollAtEnd : IMultiValueConverter
{
    public static readonly ScrollAtEnd Instance = new();

    /// <summary>
    /// Slack for the comparison: on a scaled display the offset can stop a
    /// fraction of a pixel short of Maximum, which would leave the arrow lit
    /// at the bottom. Logical (per-item) scrolling moves in whole steps, so
    /// half a unit never hides a real one.
    /// </summary>
    private const double Slack = 0.5;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not double value
            || values[1] is not double min || values[2] is not double max)
            return false;

        return parameter as string == "start" ? value <= min + Slack : value >= max - Slack;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
