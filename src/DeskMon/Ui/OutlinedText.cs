using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DeskMon.Ui;

/// <summary>
/// One line of text with a solid outline round every letter, for a number
/// drawn on top of a bar (MetricRow's ValueInBar). A TextBlock can only be
/// given a shadow, and a blurred shadow fades out exactly where it is needed -
/// against the bright gradient fill the digits still melted into the bar.
/// A stroke is as strong at its edge as in its middle.
///
/// Drawn as geometry: the outline first, the fill over it, so the stroke only
/// ever shows outside the letters and never thins them. The font follows the
/// window's like a TextBlock's would.
/// </summary>
internal sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedText),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Changed));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender, Changed));

    /// <summary>The outline's whole width; half of it lies outside the letters, the other half under the fill.</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, Changed));

    // The font as a TextBlock inherits it, so the number matches the rest of the row.
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(OutlinedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, Changed));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(OutlinedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, Changed));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(OutlinedText), new FrameworkPropertyMetadata(FontWeights.Normal,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure, Changed));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    // Built once per change of text or font, not on every render: the
    // number changes once a tick, the widget may redraw far more often.
    private Geometry? _geometry;
    private Pen? _pen;
    private Size _size;

    /// <summary>Forgets the shape, so the next measure builds it again from the new text or font.</summary>
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = (OutlinedText)d;
        text._geometry = null;
        text._pen = null;
    }

    private void Build()
    {
        double half = StrokeThickness / 2;
        var formatted = new FormattedText(Text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                          new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
                                          FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Shifted by half the stroke, so the outline on the left and top
        // edges is inside the element's own bounds and not cut off.
        _geometry = formatted.BuildGeometry(new Point(half, half));
        _geometry.Freeze();
        _size = new Size(formatted.WidthIncludingTrailingWhitespace + StrokeThickness, formatted.Height + StrokeThickness);

        // Round joins: mitred ones put spikes on the sharp corners of a 4 or a 7.
        _pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
        _pen.Freeze();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_geometry is null)
            Build();
        return _size;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometry is null)
            Build();

        drawingContext.DrawGeometry(null, _pen, _geometry);
        drawingContext.DrawGeometry(Fill, null, _geometry);
    }
}
