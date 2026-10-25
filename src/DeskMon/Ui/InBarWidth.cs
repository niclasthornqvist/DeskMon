using System;
using System.Globalization;
using System.Windows.Data;

namespace DeskMon.Ui;

/// <summary>
/// The least width of a row's middle with the value inside the bar: the bar
/// length setting, or wide enough for the number, whichever is more. The bar
/// is 90% of that middle, so a number wider than the setting - "4850 MHz" on
/// a short bar - used to reach past both ends of it. Here it is given its
/// width plus a little room each side, and that is made the bar's 90%.
///
/// Values: the setting's width (already as the 90%-scaled middle), then the
/// number's measured width.
/// </summary>
internal sealed class InBarWidth : IMultiValueConverter
{
    public static readonly InBarWidth Instance = new();

    /// <summary>Room between the number and each end of the bar, so it never touches the rounded ends.</summary>
    private const double Padding = 8;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double setting = values.Length > 0 && values[0] is double s ? s : 0;
        double text = values.Length > 1 && values[1] is double t ? t : 0;
        return Math.Max(setting, Math.Ceiling((text + 2 * Padding) / 0.9));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
