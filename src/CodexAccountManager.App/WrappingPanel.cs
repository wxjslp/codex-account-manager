using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CodexAccountManager.App;

public sealed class WrappingPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(WrappingPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(WrappingPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width);

        double rowWidth = 0;
        double rowHeight = 0;
        double totalHeight = 0;
        double measuredWidth = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desiredSize = child.DesiredSize;

            if (rowWidth > 0 && rowWidth + HorizontalSpacing + desiredSize.Width > maxWidth)
            {
                totalHeight += rowHeight + VerticalSpacing;
                measuredWidth = Math.Max(measuredWidth, rowWidth);
                rowWidth = 0;
                rowHeight = 0;
            }

            if (rowWidth > 0)
            {
                rowWidth += HorizontalSpacing;
            }

            rowWidth += desiredSize.Width;
            rowHeight = Math.Max(rowHeight, desiredSize.Height);
        }

        if (rowWidth > 0)
        {
            totalHeight += rowHeight;
            measuredWidth = Math.Max(measuredWidth, rowWidth);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? measuredWidth : availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;

        foreach (var child in Children)
        {
            var desiredSize = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + desiredSize.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(new Point(x, y), desiredSize));
            x += desiredSize.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, desiredSize.Height);
        }

        return finalSize;
    }

    private static void OnLayoutPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is WrappingPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }
}
