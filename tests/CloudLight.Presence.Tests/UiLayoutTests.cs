using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLight.Presence.App.Controls;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.App.Views;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

[Collection("Wpf UI")]
public sealed class UiLayoutTests
{
    [Fact]
    public async Task RouterSidebarAndMiotLayoutsStayWithinTheirMeasuredBounds()
    {
        var bindingErrors = new BindingErrorListener();
        await WpfTestHost.RunAsync(() =>
        {
            var originalBindingLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            try
            {
                VerifyRouterCompatibilityLayout();
                VerifySidebarActiveTemplate();
                VerifyMiotPropertyLayout();
                Trace.Flush();
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                PresentationTraceSources.DataBindingSource.Switch.Level = originalBindingLevel;
            }
        });
        Assert.Empty(bindingErrors.Messages);
    }

    private static void VerifyRouterCompatibilityLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Router-Compatibility-Ui-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? main = null;
        RouterPresenceViewModel? page = null;
        Window? window = null;

        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            repository.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow;
            const string model = "xiaomi.router.rd03.super-long-compatible-model";
            var router = repository.UpsertRouterAsync(
                new Router(0, "865000000000247", model, "2f1000000000007b2", "zhenfeng", null, null, now, now),
                CancellationToken.None).GetAwaiter().GetResult();
            var fields = Enumerable.Range(1, 43).Select(index => $"field_{index:00}_with_long_name").ToArray();
            var diagnostic = new RouterCapabilityDiagnostic(
                router.Id,
                router.MiotDid,
                router.MiotModel,
                true,
                "https://api.io.mi.com/app/appgateway/third/miwifi/app/s/api/device_list",
                0,
                true,
                fields,
                now,
                true,
                null,
                now);
            var source = new DiagnosticSource(diagnostic);
            var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            main = new MainViewModel(
                repository,
                new SubjectPresenceService(repository, new PresenceStatisticsService(repository)),
                source,
                monitor,
                new JsonSettingsStore(paths));
            main.Routers.Add(router);
            main.SelectedRouter = router;
            monitor.SelectRouter(router);
            monitor.RefreshNowAsync(CancellationToken.None).GetAwaiter().GetResult();

            page = new RouterPresenceViewModel(main, router);
            var view = new RouterPresenceView { DataContext = page };
            window = TestWindow(view, 780, 900);
            window.Show();
            LayoutContent(view, 780, 900);
            PumpDispatcher();
            window.UpdateLayout();

            var card = Assert.IsType<Border>(view.FindName("RouterCompatibilityCard"));
            var endpointText = Assert.IsType<TextBlock>(view.FindName("EndpointText"));
            var fieldsViewer = Assert.IsType<ScrollViewer>(view.FindName("ExposedFieldsViewer"));
            var fieldsSummary = Assert.IsType<TextBlock>(view.FindName("ExposedFieldsSummaryText"));

            Assert.False(page.IsExposedFieldsExpanded);
            Assert.Equal(43, page.ExposedFieldCount);
            Assert.Equal("检测到 43 个字段", page.ExposedFieldsSummaryText);
            Assert.Equal(Visibility.Collapsed, fieldsViewer.Visibility);
            Assert.Equal("/app/appgateway/third/miwifi/app/s/api/device_list", endpointText.Text);
            Assert.Equal("检测到 43 个字段", fieldsSummary.Text);
            Assert.InRange(card.DesiredSize.Height, 1, 800);
            AssertNoInvalidSizes(card);
            AssertNoVisibleTextOverlap(card);

            page.ToggleExposedFieldsCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();

            Assert.True(page.IsExposedFieldsExpanded);
            Assert.Equal(Visibility.Visible, fieldsViewer.Visibility);
            Assert.InRange(fieldsViewer.ActualHeight, 0, fieldsViewer.MaxHeight + 1);
            Assert.Equal(43, FindVisualChildren<TextBlock>(fieldsViewer).Count(value => fields.Contains(value.Text)));
            AssertNoInvalidSizes(card);
            AssertNoVisibleTextOverlap(card);
        }
        finally
        {
            if (window is { IsLoaded: true }) window.Close();
            page?.Dispose();
            main?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void VerifySidebarActiveTemplate()
    {
        var host = new Grid { Width = 260, Height = 120 };
        var normal = new Button
        {
            Content = "普通设备",
            Tag = false,
            Style = (Style)Application.Current!.Resources["SidebarDeviceItemStyle"]
        };
        var active = new Button
        {
            Content = "米家智能插座3",
            Tag = true,
            Style = (Style)Application.Current.Resources["SidebarDeviceItemStyle"]
        };
        host.RowDefinitions.Add(new RowDefinition());
        host.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(normal, 0);
        Grid.SetRow(active, 1);
        host.Children.Add(normal);
        host.Children.Add(active);
        var window = TestWindow(host, 300, 180);
        try
        {
            window.Show();
            LayoutContent(host, 300, 180);
            PumpDispatcher();
            window.UpdateLayout();
            normal.ApplyTemplate();
            active.ApplyTemplate();
            PumpDispatcher();

            var normalRoot = Assert.IsType<Border>(normal.Template.FindName("Root", normal));
            var activeRoot = Assert.IsType<Border>(active.Template.FindName("Root", active));
            var indicator = Assert.IsType<Border>(active.Template.FindName("ActiveIndicator", active));
            Assert.Equal(Brushes.Transparent, normalRoot.Background);
            Assert.NotEqual(Brushes.Transparent, activeRoot.Background);
            Assert.Equal(Visibility.Visible, indicator.Visibility);
            Assert.True(active.ActualWidth > 0);
            Assert.InRange(activeRoot.ActualWidth, active.ActualWidth * 0.95, active.ActualWidth + 1);
            Assert.Equal(Color.FromRgb(244, 247, 253), ((SolidColorBrush)Application.Current.Resources["SidebarHoverBrush"]).Color);
            Assert.NotEqual(((SolidColorBrush)Application.Current.Resources["SidebarHoverBrush"]).Color, ((SolidColorBrush)Application.Current.Resources["AccentSoftBrush"]).Color);
        }
        finally
        {
            if (window.IsLoaded) window.Close();
        }
    }

    private static void VerifyMiotPropertyLayout()
    {
        var properties = new[]
        {
            Property(1, "power", "功率", "W", 109m),
            Property(2, "temperature", "温度", "℃", 26m),
            Property(3, "serial-number", "", null, "SN-PRIMARY"),
            Property(4, "serial-no", "", null, null)
        };
        var definition = new XiaomiDeviceDefinition(
            "urn:miot-spec-v2:device:outlet:0000A002:1",
            [new XiaomiServiceDefinition(
                2,
                "urn:miot-spec-v2:service:device-information:00007801:1",
                "device-information",
                "设备信息",
                properties,
                [],
                [])]);
        var device = new XiaomiAccountDevice(
            "did-plug",
            "cuco.plug.v3",
            "米家智能插座3",
            null,
            XiaomiAccountDeviceType.Plug,
            true,
            null,
            "home",
            "room",
            "我的家",
            "客厅",
            null,
            null,
            null,
            false,
            new XiaomiDeviceCapabilities(isPlug: true),
            definition.SpecType,
            definition);
        using var page = new XiaomiAccountDeviceDetailViewModel(device, new PropertySource(definition), new MiotChineseLocalizationService());
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new XiaomiAccountDeviceDetailView { DataContext = page };
        var window = TestWindow(view, 860, 900);
        try
        {
            window.Show();
            LayoutContent(view, 860, 900);
            PumpDispatcher();
            window.UpdateLayout();

            var power = Assert.Single(page.ReadableProperties, value => value.Definition.Name == "power");
            var temperature = Assert.Single(page.ReadableProperties, value => value.Definition.Name == "temperature");
            Assert.Equal("109", power.CurrentValueText);
            Assert.Equal("W", power.UnitText);
            Assert.Equal("26", temperature.CurrentValueText);
            Assert.Equal("℃", temperature.UnitText);
            Assert.Contains(page.ReadableProperties, value => value.DisplayName == "序列号");
            Assert.Contains(page.ReadableProperties, value => value.DisplayName == "设备序列号");

            var powerValue = Assert.Single(FindVisualChildren<TextBlock>(view), value => value.Text == "109");
            var powerUnit = Assert.Single(FindVisualChildren<TextBlock>(view), value => value.Text == "W");
            var parent = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(powerValue));
            Assert.Same(parent, VisualTreeHelper.GetParent(powerUnit));
            var valueBounds = BoundsInAncestor(powerValue, parent);
            var unitBounds = BoundsInAncestor(powerUnit, parent);
            Assert.True(unitBounds.Left >= valueBounds.Right);
            Assert.InRange(unitBounds.Left - valueBounds.Right, 0, 8);

            var valuePositions = new[] { powerValue, Assert.Single(FindVisualChildren<TextBlock>(view), value => value.Text == "26") }
                .Select(value => BoundsInAncestor(value, view).Left)
                .ToArray();
            Assert.True(Math.Abs(valuePositions[0] - valuePositions[1]) > 100);
            Assert.NotEmpty(FindVisualChildren<ResponsiveWrapPanel>(view));
        }
        finally
        {
            if (window.IsLoaded) window.Close();
        }
    }

    private static XiaomiPropertyDefinition Property(int piid, string name, string chineseName, string? unit, object? value) =>
        new(2, piid, $"urn:miot-spec-v2:property:{name}:00000000:1", name, chineseName, true, false, true,
            name is "power" or "temperature" ? XiaomiMiotValueType.Number : XiaomiMiotValueType.String,
            null, [], unit, CurrentValue: value);

    private static Window TestWindow(FrameworkElement content, double width, double height) => new()
    {
        Content = content,
        Width = width,
        Height = height,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -32000,
        Top = -32000
    };

    private static void LayoutContent(FrameworkElement content, double width, double height)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
    }

    private static void AssertNoInvalidSizes(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            Assert.False(double.IsNaN(element.ActualWidth));
            Assert.False(double.IsNaN(element.ActualHeight));
            Assert.False(double.IsInfinity(element.ActualWidth));
            Assert.False(double.IsInfinity(element.ActualHeight));
            Assert.True(element.ActualWidth >= 0);
            Assert.True(element.ActualHeight >= 0);
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            AssertNoInvalidSizes(VisualTreeHelper.GetChild(root, index));
    }

    private static void AssertNoVisibleTextOverlap(FrameworkElement root)
    {
        var textBlocks = FindVisualChildren<TextBlock>(root)
            .Where(value => value.Visibility == Visibility.Visible && value.ActualWidth > 0 && value.ActualHeight > 0)
            .ToArray();
        for (var leftIndex = 0; leftIndex < textBlocks.Length; leftIndex++)
        {
            var left = BoundsInAncestor(textBlocks[leftIndex], root);
            for (var rightIndex = leftIndex + 1; rightIndex < textBlocks.Length; rightIndex++)
            {
                var right = BoundsInAncestor(textBlocks[rightIndex], root);
                Assert.False(HasPositiveAreaIntersection(left, right),
                    $"Visible text overlaps: '{textBlocks[leftIndex].Text}' and '{textBlocks[rightIndex].Text}'.");
            }
        }
    }

    private static Rect BoundsInAncestor(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static bool HasPositiveAreaIntersection(Rect left, Rect right) =>
        Math.Min(left.Right, right.Right) > Math.Max(left.Left, right.Left) &&
        Math.Min(left.Bottom, right.Bottom) > Math.Max(left.Top, right.Top);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T result) yield return result;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class DiagnosticSource(RouterCapabilityDiagnostic diagnostic) : IXiaomiPresenceSource, IXiaomiPresenceDiagnosticsSource
    {
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
        public Task<RouterPresenceProbeResult> GetDevicesWithDiagnosticsAsync(XiaomiRouterDevice router, CancellationToken cancellationToken) =>
            Task.FromResult(new RouterPresenceProbeResult([], diagnostic));
    }

    private sealed class PropertySource(XiaomiDeviceDefinition definition) : IXiaomiDeviceControlSource
    {
        public Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(XiaomiAccountDevice device, CancellationToken cancellationToken) => Task.FromResult<XiaomiDeviceDefinition?>(definition);

        public Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(
            XiaomiAccountDevice device,
            IReadOnlyList<XiaomiPropertyDefinition> properties,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<XiaomiPropertyReadResult>>(properties
                .Select(value => new XiaomiPropertyReadResult(value.Siid, value.Piid, true, value.CurrentValue))
                .ToArray());

        public Task<XiaomiPropertyOperationResult> SetPropertyAsync(XiaomiAccountDevice device, XiaomiPropertyDefinition property, object? value, CancellationToken cancellationToken) =>
            Task.FromResult(new XiaomiPropertyOperationResult(false));

        public Task<XiaomiActionInvocationResult> InvokeActionAsync(XiaomiAccountDevice device, XiaomiActionDefinition action, IReadOnlyList<object?> inputArguments, CancellationToken cancellationToken) =>
            Task.FromResult(new XiaomiActionInvocationResult(false, []));
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message);
        }

        public override void WriteLine(string? message) => Write(message);
    }
}
