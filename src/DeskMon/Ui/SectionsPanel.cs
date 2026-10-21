using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskMon.Metrics;

namespace DeskMon.Ui;

/// <summary>
/// Lays out the widget's rows: one under another, or with each section in a
/// column of its own, side by side - the WidgetLayout setting.
///
/// A panel rather than a second list of columns, so the widget keeps the one
/// flat list it has always drawn - heading, rows, heading, rows. Folding,
/// dragging a section, the detail rows and every row template work on that
/// list and never need to know which way it is laid out. In columns, a new
/// column starts at every section heading; the hardware names, which belong to
/// no section, are a column of their own at the start.
/// </summary>
internal sealed class SectionsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(bool), typeof(SectionsPanel),
        new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Side by side, a column per section; one under another when false.</summary>
    public bool Columns
    {
        get => (bool)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty SeparatorBrushProperty = DependencyProperty.Register(
        nameof(SeparatorBrush), typeof(Brush), typeof(SectionsPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The line between two columns - the theme's separator, as the line
    /// above each heading is when the sections are stacked. The headings
    /// leave theirs off in columns; this draws the upright one instead.
    /// </summary>
    public Brush? SeparatorBrush
    {
        get => (Brush?)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    /// <summary>The space between two columns, with the line down its middle.</summary>
    public const double ColumnGap = 22;

    /// <summary>Where each column ended up, for the lines drawn between them.</summary>
    private readonly List<double> _gapCentres = new();

    /// <summary>The children, as the columns they fall into: a new one at every heading.</summary>
    private List<List<UIElement>> Grouped()
    {
        var columns = new List<List<UIElement>>();
        foreach (UIElement child in InternalChildren)
        {
            bool heading = child is FrameworkElement { DataContext: SectionRow };
            if (columns.Count == 0 || (heading && columns[^1].Count > 0))
                columns.Add(new List<UIElement>());
            columns[^1].Add(child);
        }
        return columns;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (!Columns)
        {
            // As a vertical StackPanel would: the width it is given, the
            // height everything asks for.
            double width = 0, height = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(available.Width, double.PositiveInfinity));
                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;
            }
            return new Size(width, height);
        }

        // Unbounded across: the window sizes itself to its content, and a
        // column must never be squeezed by the ones beside it.
        double total = 0, tallest = 0;
        var columns = Grouped();
        foreach (var column in columns)
        {
            double width = 0, height = 0;
            foreach (var child in column)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;
            }
            total += width;
            tallest = Math.Max(tallest, height);
        }

        total += ColumnGap * Math.Max(0, columns.Count - 1);
        return new Size(total, tallest);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _gapCentres.Clear();

        if (!Columns)
        {
            double y = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Arrange(new Rect(0, y, final.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }
            return final;
        }

        double x = 0;
        var columns = Grouped();
        for (int i = 0; i < columns.Count; i++)
        {
            double width = 0;
            foreach (var child in columns[i])
                width = Math.Max(width, child.DesiredSize.Width);

            // Every row the column's width, so the hover strips and click
            // areas reach across it as they reach across the stacked panel.
            double y = 0;
            foreach (var child in columns[i])
            {
                child.Arrange(new Rect(x, y, width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }

            x += width;
            if (i < columns.Count - 1)
            {
                _gapCentres.Add(x + ColumnGap / 2);
                x += ColumnGap;
            }
        }

        return final;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        if (!Columns || SeparatorBrush is not { } brush || _gapCentres.Count == 0)
            return;

        // On whole pixels plus a half, so a 1 px line stays 1 px rather than
        // blurring across two. A few pixels in from top and bottom, so the
        // lines read as dividers rather than a frame.
        var pen = new Pen(brush, 1);
        pen.Freeze();
        double top = 3, bottom = Math.Max(top, RenderSize.Height - 3);
        foreach (double centre in _gapCentres)
        {
            double x = Math.Round(centre) + 0.5;
            context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        }
    }
}
