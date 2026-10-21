using System;
using System.Globalization;
using System.Windows.Data;
using DeskMon.Notes;

namespace DeskMon.Ui;

/// <summary>
/// A game picker's item to its <see cref="GameIcon"/>, for the GamePickerItem
/// template. The items stay plain names, so nothing that reads the pickers'
/// selection has to change. A game's name gets its icon; anything else is the
/// sessions window's All games entry, the only item in either picker that
/// isn't a name.
/// </summary>
public sealed class GameIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string game ? GameIcons.For(game) : GameIcons.AllGames;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
