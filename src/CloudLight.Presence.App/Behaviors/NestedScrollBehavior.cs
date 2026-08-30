using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;

namespace CloudLight.Presence.App.Behaviors;

/// <summary>
/// Keeps a nested vertical scroll surface from trapping the mouse at either
/// end.  The attached handler only takes over at a boundary; while the inner
/// surface can still move, WPF's normal ScrollViewer handling is preserved.
/// </summary>
public static class NestedScrollBehavior
{
    public static readonly DependencyProperty BubbleMouseWheelAtBoundaryProperty =
        DependencyProperty.RegisterAttached(
            "BubbleMouseWheelAtBoundary",
            typeof(bool),
            typeof(NestedScrollBehavior),
            new PropertyMetadata(false, OnBehaviorPropertyChanged));

    public static readonly DependencyProperty BubbleMouseWheelWhenClosedProperty =
        DependencyProperty.RegisterAttached(
            "BubbleMouseWheelWhenClosed",
            typeof(bool),
            typeof(NestedScrollBehavior),
            new PropertyMetadata(false, OnBehaviorPropertyChanged));

    public static readonly DependencyProperty BubbleMouseWheelWhenNotFocusedProperty =
        DependencyProperty.RegisterAttached(
            "BubbleMouseWheelWhenNotFocused",
            typeof(bool),
            typeof(NestedScrollBehavior),
            new PropertyMetadata(false, OnBehaviorPropertyChanged));

    public static void SetBubbleMouseWheelAtBoundary(DependencyObject element, bool value) =>
        element.SetValue(BubbleMouseWheelAtBoundaryProperty, value);

    public static bool GetBubbleMouseWheelAtBoundary(DependencyObject element) =>
        (bool)element.GetValue(BubbleMouseWheelAtBoundaryProperty);

    public static void SetBubbleMouseWheelWhenClosed(DependencyObject element, bool value) =>
        element.SetValue(BubbleMouseWheelWhenClosedProperty, value);

    public static bool GetBubbleMouseWheelWhenClosed(DependencyObject element) =>
        (bool)element.GetValue(BubbleMouseWheelWhenClosedProperty);

    public static void SetBubbleMouseWheelWhenNotFocused(DependencyObject element, bool value) =>
        element.SetValue(BubbleMouseWheelWhenNotFocusedProperty, value);

    public static bool GetBubbleMouseWheelWhenNotFocused(DependencyObject element) =>
        (bool)element.GetValue(BubbleMouseWheelWhenNotFocusedProperty);

    private static void OnBehaviorPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element) return;

        element.RemoveHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        if (HasBehavior(element))
            element.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
    }

    private static bool HasBehavior(DependencyObject element) =>
        GetBubbleMouseWheelAtBoundary(element) ||
        GetBubbleMouseWheelWhenClosed(element) ||
        GetBubbleMouseWheelWhenNotFocused(element);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || sender is not UIElement element || args.Delta == 0) return;

        if (element is ComboBox comboBox && GetBubbleMouseWheelWhenClosed(element))
        {
            if (!comboBox.IsDropDownOpen)
            {
                BubbleToNearestScrollableParent(element, args.Delta);
                // A closed ComboBox is not a numeric/list scrolling surface;
                // consume the event even when its page is already at an end
                // so a casual page scroll cannot change the selected value.
                args.Handled = true;
            }
            return;
        }

        if (element is Slider slider && GetBubbleMouseWheelWhenNotFocused(element))
        {
            if (!slider.IsKeyboardFocusWithin)
            {
                BubbleToNearestScrollableParent(element, args.Delta);
                // Keep an unfocused Slider from turning page scrolling into a
                // value edit when there is no remaining page range.
                args.Handled = true;
            }
            return;
        }

        if (!GetBubbleMouseWheelAtBoundary(element)) return;

        var innerScrollViewer = element is ScrollViewer scrollViewer
            ? scrollViewer
            : FindDescendantScrollViewer(element);

        if ((innerScrollViewer is null || !CanScroll(innerScrollViewer, args.Delta)) &&
            BubbleToNearestScrollableParent(element, args.Delta))
        {
            args.Handled = true;
        }
    }

    private static bool BubbleToNearestScrollableParent(UIElement element, int delta)
    {
        var parent = FindParentScrollViewer(element);
        while (parent is not null)
        {
            if (CanScroll(parent, delta))
            {
                var amount = Math.Max(1d, Math.Abs(delta) / 120d) * 48d;
                var nextOffset = parent.VerticalOffset - Math.Sign(delta) * amount;
                parent.ScrollToVerticalOffset(Math.Clamp(nextOffset, 0d, parent.ScrollableHeight));
                return true;
            }

            parent = FindParentScrollViewer(parent);
        }

        return false;
    }

    private static bool CanScroll(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.ScrollableHeight <= 0.5d) return false;
        return delta > 0
            ? scrollViewer.VerticalOffset > 0.5d
            : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 0.5d;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindDescendantScrollViewer(child) is { } nested) return nested;
        }

        return null;
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            current = GetParent(current);
            if (current is ScrollViewer scrollViewer) return scrollViewer;
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual)
        {
            var visualParent = VisualTreeHelper.GetParent(element);
            if (visualParent is not null) return visualParent;
        }

        return element is FrameworkElement frameworkElement ? frameworkElement.Parent : null;
    }
}
