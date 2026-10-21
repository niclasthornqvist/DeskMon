using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// Any colour, rather than the handful in settings.json: the saturation/value
/// square and hue strip every graphics program uses, plus the hex box, which is
/// the way a colour gets copied back out into settings.json or in from
/// somewhere else.
///
/// Hue, saturation and value are kept as the state rather than a Color, because
/// RGB cannot hold the question the square is asking. Black is R=G=B=0 whatever
/// its hue, so dragging to the bottom of the square and back up again would
/// lose which hue you were on - the strip would jump to red. Keeping HSV and
/// converting outwards means the square and the strip stay where you left
/// them.
/// </summary>
public partial class ColorPicker : UserControl
{
    private double _hue;          // 0-360
    private double _saturation;   // 0-1
    private double _value;        // 0-1

    /// <summary>Stops the hex box and the square from re-entering each other.</summary>
    private bool _syncing;

    /// <summary>The host's ordinary text colour; see <see cref="SetNormal"/>.</summary>
    private Color _normal = Colors.White;

    public ColorPicker()
    {
        InitializeComponent();
        SetColor(Colors.White);
    }

    /// <summary>Raised by Apply, with the colour as it stands.</summary>
    public event Action<Color>? Picked;

    /// <summary>The colour the controls are showing.</summary>
    public Color Current => FromHsv(_hue, _saturation, _value);

    /// <summary>
    /// Points the picker at an existing colour - the colour of the text the
    /// caret is in, so opening the picker continues from what is there rather
    /// than from an arbitrary default.
    /// </summary>
    public void SetColor(Color colour)
    {
        (_hue, _saturation, _value) = ToHsv(colour);
        Sync();
    }

    /// <summary>Theme: the picker is dropped into windows of several colours.</summary>
    public void ApplyTheme(Brush text, Brush border, Style textBoxStyle, Style buttonStyle)
    {
        HexBox.Style = textBoxStyle;
        HexBox.Foreground = text;
        ApplyButton.Style = buttonStyle;
        NormalButton.Style = buttonStyle;
        Preview.BorderBrush = border;
    }

    /// <summary>
    /// What Apply does, on hover. The default speaks for the notepad, which
    /// colours text; the settings window colours the widget instead.
    /// </summary>
    public void SetApplyTooltip(string tooltip) => ApplyButton.ToolTip = tooltip;

    /// <summary>
    /// Offers a one-click way back to the document's ordinary text colour.
    ///
    /// It applies that colour like any other, which in the notes amounts to
    /// clearing the colour: the file is saved with the window's own text colour
    /// stripped out of it, so text in that colour is text with no colour of its
    /// own. See Notes/NotesRtf.cs.
    ///
    /// Null hides the button, for a host that has no such colour.
    /// </summary>
    public void SetNormal(Color? normal, string tooltip)
    {
        if (normal is not Color colour)
        {
            NormalButton.Visibility = Visibility.Collapsed;
            return;
        }

        _normal = colour;
        NormalButton.ToolTip = tooltip;
        NormalButton.Visibility = Visibility.Visible;
    }

    // --- The square -------------------------------------------------------

    private void Square_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Square.CaptureMouse();   // so a drag off the edge still tracks
        PickFromSquare(e.GetPosition(Square));
    }

    private void Square_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Square.IsMouseCaptured)
            PickFromSquare(e.GetPosition(Square));
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueStrip.CaptureMouse();
        PickFromHue(e.GetPosition(HueStrip));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && HueStrip.IsMouseCaptured)
            PickFromHue(e.GetPosition(HueStrip));
    }

    private void Pointer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Square.ReleaseMouseCapture();
        HueStrip.ReleaseMouseCapture();
    }

    private void PickFromSquare(Point at)
    {
        _saturation = Math.Clamp(at.X / Square.ActualWidth, 0, 1);

        // Downwards is darker, which is the way round every other picker does
        // it - hence 1 minus.
        _value = Math.Clamp(1 - at.Y / Square.ActualHeight, 0, 1);

        Sync();
    }

    private void PickFromHue(Point at)
    {
        _hue = Math.Clamp(at.Y / HueStrip.ActualHeight, 0, 1) * 360;
        Sync();
    }

    // --- Keeping the controls in step -------------------------------------

    /// <summary>
    /// Redraws everything from the current HSV: the square's hue layer, the two
    /// markers, the preview and the hex box.
    /// </summary>
    private void Sync()
    {
        var colour = Current;

        // Only the bottom layer changes with the hue; the white and black
        // gradients over it are fixed.
        HueLayer.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
        Preview.Background = new SolidColorBrush(colour);

        Canvas.SetLeft(Crosshair, _saturation * Square.Width - Crosshair.Width / 2);
        Canvas.SetTop(Crosshair, (1 - _value) * Square.Height - Crosshair.Height / 2);
        Canvas.SetTop(HueMarker, _hue / 360 * HueStrip.Height - HueMarker.Height / 2);

        _syncing = true;
        try
        {
            HexBox.Text = $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
            return;

        if (!TryParseHex(HexBox.Text, out var colour))
            return;   // half-typed; the controls keep what they had

        (_hue, _saturation, _value) = ToHsv(colour);

        // Not Sync(): that would rewrite the box under the cursor while it is
        // being typed in. Everything else is redrawn.
        var full = Current;
        HueLayer.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
        Preview.Background = new SolidColorBrush(full);
        Canvas.SetLeft(Crosshair, _saturation * Square.Width - Crosshair.Width / 2);
        Canvas.SetTop(Crosshair, (1 - _value) * Square.Height - Crosshair.Height / 2);
        Canvas.SetTop(HueMarker, _hue / 360 * HueStrip.Height - HueMarker.Height / 2);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Picked?.Invoke(Current);

    private void Normal_Click(object sender, RoutedEventArgs e) => Picked?.Invoke(_normal);

    /// <summary>
    /// "#RRGGBB", with or without the hash. Alpha is not accepted: this picks
    /// the colour of text, and half-transparent text in the notes would read as
    /// a bug rather than a choice.
    /// </summary>
    private static bool TryParseHex(string text, out Color colour)
    {
        colour = Colors.Black;

        string value = text.Trim().TrimStart('#');
        if (value.Length != 6 ||
            !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed))
            return false;

        colour = Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    // --- HSV ---------------------------------------------------------------

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;

        double chroma = value * saturation;
        double second = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double match = value - chroma;

        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, second, 0d),
            < 120 => (second, chroma, 0d),
            < 180 => (0d, chroma, second),
            < 240 => (0d, second, chroma),
            < 300 => (second, 0d, chroma),
            _ => (chroma, 0d, second),
        };

        return Color.FromRgb(Byte(r + match), Byte(g + match), Byte(b + match));
    }

    private static (double Hue, double Saturation, double Value) ToHsv(Color colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double chroma = max - min;

        double hue = 0;
        if (chroma > 0)
        {
            if (max == r) hue = 60 * (((g - b) / chroma % 6 + 6) % 6);
            else if (max == g) hue = 60 * ((b - r) / chroma + 2);
            else hue = 60 * ((r - g) / chroma + 4);
        }

        return (hue, max > 0 ? chroma / max : 0, max);
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
