using System.Windows;
using System.Windows.Controls;

namespace Commerce.Pos.Windows.Controls;

/// <summary>
/// Wraps its children in as many equal columns as fit at <see cref="MinItemWidth"/> and STRETCHES them to share the whole
/// width, so a grid of product cards never leaves an empty strip on the right: the leftover goes into the cards. Each row
/// is as tall as its tallest child. <see cref="Spacing"/> separates columns and rows.
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(UniformWrapPanel),
        new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(UniformWrapPanel),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a child may get; one more column is added only when every column keeps at least this.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The column count and item width for <paramref name="width"/>: at least one column, never wider than the panel.</summary>
    public static (int Columns, double ItemWidth) Layout(double width, double minItemWidth, double spacing)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
        {
            return (1, minItemWidth);
        }

        var columns = Math.Max(1, (int)Math.Floor((width + spacing) / (minItemWidth + spacing)));
        var itemWidth = Math.Max(0, (width - spacing * (columns - 1)) / columns);
        return (columns, itemWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Layout(availableSize.Width, MinItemWidth, Spacing);
        var height = 0d;
        var rowHeight = 0d;
        var count = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            count++;
            if (count % columns == 0)
            {
                height += rowHeight + Spacing;
                rowHeight = 0;
            }
        }

        if (count % columns != 0)
        {
            height += rowHeight;
        }
        else if (count > 0)
        {
            height -= Spacing;
        }

        var width = double.IsInfinity(availableSize.Width) ? columns * itemWidth + (columns - 1) * Spacing : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Layout(finalSize.Width, MinItemWidth, Spacing);
        var children = InternalChildren.Cast<UIElement>().ToList();
        var y = 0d;
        for (var start = 0; start < children.Count; start += columns)
        {
            var row = children.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(child => child.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
            {
                row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            }

            y += rowHeight + Spacing;
        }

        return finalSize;
    }
}
