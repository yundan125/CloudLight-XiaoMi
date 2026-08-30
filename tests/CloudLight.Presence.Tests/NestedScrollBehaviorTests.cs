using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Data;
using CloudLight.Presence.App.Behaviors;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CloudLight.Presence.Tests;

[CollectionDefinition("Wpf UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

[Collection("Wpf UI")]
public sealed class NestedScrollBehaviorTests
{
    [Fact]
    public async Task OneRecipientUsesNaturalHeightAndManyRecipientsScrollAtBoundary()
    {
        await RunOnStaAsync(() =>
        {
            var oneRecipient = CreateRecipientList(["已有接收人 · 私聊 · C47****FD8"], maxHeight: 220);
            var oneWindow = CreateScrollWindow(oneRecipient, topRows: 2, bottomRows: 2);
            LayoutScrollViewer(oneWindow);
            oneRecipient.ApplyTemplate();
            Assert.True(oneRecipient.ActualHeight > 0, $"one recipient list did not measure (items={oneRecipient.Items.Count}, visible={oneRecipient.Visibility}/{oneRecipient.IsVisible}, list={oneRecipient.ActualWidth}x{oneRecipient.ActualHeight}, desired={oneRecipient.DesiredSize.Width}x{oneRecipient.DesiredSize.Height})");
            Assert.InRange(oneRecipient.ActualHeight, 1d, 99d);

            var manyRecipients = CreateRecipientList(Enumerable.Range(1, 20).Select(value => $"联系人 {value}"), maxHeight: 120);
            var manyWindow = CreateScrollWindow(manyRecipients, topRows: 12, bottomRows: 4);
            LayoutScrollViewer(manyWindow);
            manyRecipients.ApplyTemplate();
            var inner = Assert.IsType<ScrollViewer>(FindVisualChild<ScrollViewer>(manyRecipients));
            Assert.True(inner.ScrollableHeight > 0, $"many recipients should have an inner scroll range (items={manyRecipients.Items.Count}, list={manyRecipients.ActualHeight}, viewport={inner.ViewportHeight}, extent={inner.ExtentHeight})");
            Assert.True(manyWindow.ScrollableHeight > 0, "the page should have an outer scroll range");

            inner.ScrollToVerticalOffset(0);
            var outerBefore = manyWindow.VerticalOffset;
            var innerWheel = RaiseWheel(manyRecipients, -120);
            Assert.False(innerWheel.Handled, "a list that is not at its bottom should keep the wheel for its inner ScrollViewer");
            Assert.Equal(outerBefore, manyWindow.VerticalOffset);

            inner.ScrollToBottom();
            inner.UpdateLayout();
            manyWindow.ScrollToVerticalOffset(0);
            outerBefore = manyWindow.VerticalOffset;
            RaiseWheel(manyRecipients, -120);
            Assert.True(manyWindow.VerticalOffset > outerBefore, "a list at its bottom should bubble down to the page");

            inner.ScrollToTop();
            inner.UpdateLayout();
            manyWindow.ScrollToVerticalOffset(manyWindow.ScrollableHeight);
            outerBefore = manyWindow.VerticalOffset;
            RaiseWheel(manyRecipients, 120);
            Assert.True(manyWindow.VerticalOffset < outerBefore, "a list at its top should bubble up to the page");
        });
    }

    [Fact]
    public async Task ClosedComboBoxUnfocusedSliderAndShortTextBoxDoNotConsumePageWheel()
    {
        await RunOnStaAsync(() =>
        {
            var page = new StackPanel();
            for (var index = 0; index < 10; index++) page.Children.Add(new Border { Height = 40 });

            var combo = new ComboBox { ItemsSource = new[] { "分钟", "小时" }, SelectedIndex = 0 };
            NestedScrollBehavior.SetBubbleMouseWheelWhenClosed(combo, true);
            page.Children.Add(combo);

            var slider = new Slider { Minimum = 0, Maximum = 10, Value = 4 };
            NestedScrollBehavior.SetBubbleMouseWheelWhenNotFocused(slider, true);
            page.Children.Add(slider);

            var shortText = new TextBox { Text = "一行模板", Height = 52, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            NestedScrollBehavior.SetBubbleMouseWheelAtBoundary(shortText, true);
            page.Children.Add(shortText);
            for (var index = 0; index < 6; index++) page.Children.Add(new Border { Height = 40 });

            var outer = new ScrollViewer { Width = 420, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
            LayoutScrollViewer(outer);
            Assert.True(outer.ScrollableHeight > 0);

            var comboBefore = combo.SelectedIndex;
            RaiseWheel(combo, -120);
            Assert.Equal(comboBefore, combo.SelectedIndex);
            Assert.True(outer.VerticalOffset > 0, "a closed ComboBox should bubble page scrolling");

            outer.ScrollToVerticalOffset(outer.ScrollableHeight);
            comboBefore = combo.SelectedIndex;
            RaiseWheel(combo, -120);
            Assert.Equal(comboBefore, combo.SelectedIndex);

            outer.ScrollToVerticalOffset(0);
            var sliderBefore = slider.Value;
            RaiseWheel(slider, -120);
            Assert.Equal(sliderBefore, slider.Value);
            Assert.True(outer.VerticalOffset > 0, "an unfocused Slider should bubble page scrolling");

            outer.ScrollToVerticalOffset(0);
            var textBefore = outer.VerticalOffset;
            RaiseWheel(shortText, -120);
            Assert.True(outer.VerticalOffset > textBefore, "a short multiline TextBox should bubble page scrolling");
        });
    }

    private static ListBox CreateRecipientList(IEnumerable<string> values, double maxHeight)
    {
        var list = new ListBox { ItemsSource = values.ToArray(), MaxHeight = maxHeight, Width = 360 };
        var itemTemplate = new DataTemplate(typeof(string));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(FrameworkElement.MinHeightProperty, 28d);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemTemplate.VisualTree = text;
        list.ItemTemplate = itemTemplate;
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        list.ItemContainerStyle = itemStyle;
        list.SetValue(ScrollViewer.CanContentScrollProperty, false);
        list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        NestedScrollBehavior.SetBubbleMouseWheelAtBoundary(list, true);
        return list;
    }

    private static ScrollViewer CreateScrollWindow(UIElement control, int topRows, int bottomRows)
    {
        var page = new StackPanel();
        for (var index = 0; index < topRows; index++) page.Children.Add(new Border { Height = 40 });
        page.Children.Add(control);
        for (var index = 0; index < bottomRows; index++) page.Children.Add(new Border { Height = 40 });
        return new ScrollViewer
        {
            Width = 380,
            Height = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = page
        };
    }

    private static void LayoutScrollViewer(ScrollViewer scrollViewer)
    {
        scrollViewer.Measure(new Size(scrollViewer.Width, scrollViewer.Height));
        scrollViewer.Arrange(new Rect(0, 0, scrollViewer.Width, scrollViewer.Height));
        scrollViewer.UpdateLayout();
        PumpDispatcher();
        scrollViewer.UpdateLayout();
    }

    private static MouseWheelEventArgs RaiseWheel(UIElement element, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = element
        };
        element.RaiseEvent(args);
        PumpDispatcher();
        return args;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindVisualChild<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        }

        return null;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
