using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// One figure on a card: its name in small capitals above, the figure in
/// larger bright type below - the row of cards at the top of the session
/// history and of the pictures window. One description here, as BinBadge is
/// for the bin, so the two windows' cards can't drift apart.
/// </summary>
internal static class StatCard
{
    /// <param name="caption">Drawn before the name - a playtime's source icon, say. Null for none.</param>
    /// <param name="valueBrush">The figure's colour when it should say good or bad; bright text otherwise.</param>
    public static Border Make(UiTheme theme, string name, string value, string? tooltip = null,
                              UIElement? caption = null, Brush? valueBrush = null)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        if (caption is not null)
            heading.Children.Add(caption);
        heading.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = theme.Header,
            FontSize = Math.Max(8, Math.Max(9, UiTheme.PanelFontSize - 2.5) - 1),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var card = new StackPanel();
        card.Children.Add(heading);
        card.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = valueBrush ?? theme.NotesText,
            FontSize = UiTheme.PanelFontSize + 2,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        });

        return new Border
        {
            // Frosted: the window colour, mostly solid, so a figure reads over
            // any part of the picture behind the header. A 5% tint let a bright
            // banner show straight through.
            Background = theme.WindowAt(0xC8),
            BorderBrush = theme.Shade(0x1A),   // its edge, now that it hides what is behind it
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 12, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Child = card,
            ToolTip = tooltip,
        };
    }
}
