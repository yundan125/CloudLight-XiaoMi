using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace CloudLight.Presence.App.Controls;

/// <summary>
/// Lays out cards in as many equal-width columns as can fit in the available
/// width.  It keeps a vertical page scroll as the only scroll surface while
/// still using the available width when the window grows.
/// </summary>
public sealed class ResponsiveWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinItemWidth),
            typeof(double),
            typeof(ResponsiveWrapPanel),
            new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemSpacingProperty =
        DependencyProperty.Register(
            nameof(ItemSpacing),
            typeof(double),
            typeof(ResponsiveWrapPanel),
            new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = CreateLayout(availableSize.Width);
        var rowHeights = new double[layout.RowCount];

        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var child = InternalChildren[index];
            var margin = GetMargin(child);
            child.Measure(new Size(
                Math.Max(0, layout.ItemWidth - margin.Left - margin.Right),
                double.PositiveInfinity));

            var row = index / layout.ColumnCount;
            rowHeights[row] = Math.Max(rowHeights[row], child.DesiredSize.Height);
        }

        var height = rowHeights.Sum() + Math.Max(0, rowHeights.Length - 1) * ItemSpacing;
        var width = double.IsInfinity(availableSize.Width) ? layout.ItemWidth * layout.ColumnCount : availableSize.Width;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = CreateLayout(finalSize.Width);
        var rowHeights = new double[layout.RowCount];

        for (var index = 0; index < InternalChildren.Count; index++)
        {
            rowHeights[index / layout.ColumnCount] = Math.Max(rowHeights[index / layout.ColumnCount], InternalChildren[index].DesiredSize.Height);
        }

        var y = 0d;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var child = InternalChildren[index];
            var margin = GetMargin(child);
            var row = index / layout.ColumnCount;
            var column = index % layout.ColumnCount;
            var x = column * (layout.ItemWidth + ItemSpacing);
            var rowHeight = rowHeights[row];

            child.Arrange(new Rect(
                x + margin.Left,
                y + margin.Top,
                Math.Max(0, layout.ItemWidth - margin.Left - margin.Right),
                Math.Max(0, rowHeight - margin.Top - margin.Bottom)));

            if (column == layout.ColumnCount - 1 || index == InternalChildren.Count - 1)
            {
                y += rowHeight + ItemSpacing;
            }
        }

        return finalSize;
    }

    private Layout CreateLayout(double availableWidth)
    {
        var spacing = Math.Max(0, ItemSpacing);
        var minimum = Math.Max(1, MinItemWidth);
        var usableWidth = double.IsInfinity(availableWidth) ? minimum : Math.Max(0, availableWidth);
        var columns = Math.Max(1, (int)Math.Floor((usableWidth + spacing) / (minimum + spacing)));
        var itemWidth = double.IsInfinity(availableWidth)
            ? minimum
            : Math.Max(0, (usableWidth - (columns - 1) * spacing) / columns);
        var rowCount = InternalChildren.Count == 0 ? 0 : (InternalChildren.Count + columns - 1) / columns;
        return new Layout(columns, itemWidth, rowCount);
    }

    private static Thickness GetMargin(UIElement child) => child is FrameworkElement element ? element.Margin : new Thickness();

    private readonly record struct Layout(int ColumnCount, double ItemWidth, int RowCount);
}
