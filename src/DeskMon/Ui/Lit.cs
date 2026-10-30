using System.Windows;

namespace DeskMon.Ui;

/// <summary>
/// Marks a flat PanelButton as on, which the style lights in the accent: the
/// notes' bold, italic, underline and strikethrough buttons while the text at
/// the cursor has that formatting. An attached flag rather than ToggleButtons,
/// which flip themselves on every click - here it is the text that says
/// whether a button is on, not the button.
/// </summary>
public static class Lit
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.RegisterAttached("IsOn", typeof(bool), typeof(Lit), new PropertyMetadata(false));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
