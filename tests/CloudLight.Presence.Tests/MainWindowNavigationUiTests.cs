using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.App.Views;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Notifications;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;
using Xunit.Abstractions;

namespace CloudLight.Presence.Tests;

public sealed class MainWindowNavigationUiTests
{
    private readonly ITestOutputHelper _output;

    public MainWindowNavigationUiTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MainWindowNavigatesItsRealButtonsWithoutBindingErrorsOrAdditionalWindows()
    {
        var diagnostics = new List<string>();

        await RunOnStaAsync(() => RunScenario(diagnostics));

        foreach (var line in diagnostics)
            _output.WriteLine(line);
    }

    private static void RunScenario(List<string> diagnostics)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-MainWindow-Ui-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? viewModel = null;
        NotificationRuntime? notificationRuntime = null;
        XiaomiConnectionAlertService? connectionAlerts = null;
        QQNotificationChannel? qqChannel = null;
        NotificationDispatcher? dispatcher = null;
        MainWindow? window = null;
        var ownsApplication = Application.Current is null;
        Application? application = null;
        var bindingListener = new BindingErrorListener();
        var originalBindingLevel = PresentationTraceSources.DataBindingSource.Switch.Level;

        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        try
        {
            if (ownsApplication)
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AddMainWindowResources(application);
            }

            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            repository.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var routerDevice = AccountDevice("router-did", "AX3000T", XiaomiAccountDeviceType.Router, isRouter: true, partnerId: "router-partner");
            var plug = AccountDevice("plug-did", "米家智能插座3", XiaomiAccountDeviceType.Plug, online: false);
            var band = AccountDevice("band-did", "小米手环10", XiaomiAccountDeviceType.Other);
            var observedAt = DateTimeOffset.UtcNow;
            var router = repository.UpsertRouterAsync(new Router(0, routerDevice.Did, routerDevice.Model!, "router-partner", routerDevice.Name, null, null, observedAt, observedAt), CancellationToken.None).GetAwaiter().GetResult();
            var networkDevice = repository.InsertDeviceAsync(new NetworkDevice(0, router.Id, "AA:BB:CC:DD:EE:01", "测试手机", "测试手机", null, null, "192.168.1.2", "5G", -50, PresenceState.Offline, observedAt.AddHours(-3), observedAt, observedAt.AddHours(-2)), CancellationToken.None).GetAwaiter().GetResult();
            var subject = repository.CreateSubjectAsync("测试主体", null, Guid.NewGuid(), observedAt.AddHours(-3), CancellationToken.None).GetAwaiter().GetResult();
            repository.SetSubjectDevicesAsync(subject.Id, [networkDevice.Id], observedAt.AddHours(-3), CancellationToken.None).GetAwaiter().GetResult();
            var source = new UiSource([routerDevice, plug, band]);
            var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            var subjects = new SubjectPresenceService(repository, new PresenceStatisticsService(repository));
            qqChannel = new QQNotificationChannel(paths.LogsDirectory);
            dispatcher = new NotificationDispatcher(repository, [qqChannel]);
            notificationRuntime = new NotificationRuntime(monitor, new NotificationRuleService(repository, subjects), dispatcher);
            connectionAlerts = new XiaomiConnectionAlertService(
                monitor,
                repository,
                dispatcher,
                _ => Task.FromResult(new ConnectionAlertConfiguration(new ConnectionAlertSettings(), NotificationTargetType.Private, "")),
                subscribe: false);
            viewModel = new MainViewModel(repository, subjects, source, monitor, new JsonSettingsStore(paths), accountDeviceSource: source);
            viewModel.Routers.Add(router);
            viewModel.SelectedRouter = router;
            window = new MainWindow(
                viewModel,
                repository,
                subjects,
                monitor,
                new PresenceDataTransferService(paths),
                new StartupRegistrationService(),
                paths,
                0,
                notificationRuntime,
                connectionAlerts,
                qqChannel,
                dispatcher);
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            var deviceListPage = Assert.IsType<Grid>(FindVisualChildByName(window, "DeviceListPage"));
            var detailView = Assert.IsType<XiaomiAccountDeviceDetailView>(FindVisualChild<XiaomiAccountDeviceDetailView>(window));
            var presenceView = Assert.IsType<RouterPresenceView>(FindVisualChild<RouterPresenceView>(window));

            Assert.Equal(MainPage.XiaomiDeviceList, viewModel.CurrentPage);
            Assert.Null(viewModel.CurrentXiaomiAccountDeviceDetail);
            Assert.Null(viewModel.CurrentRouterPresence);
            Assert.Equal(Visibility.Visible, deviceListPage.Visibility);
            Assert.Equal(Visibility.Collapsed, detailView.Visibility);
            Assert.Equal(Visibility.Collapsed, presenceView.Visibility);
            Assert.Single(Application.Current!.Windows.Cast<Window>());

            viewModel.RefreshAccountDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            viewModel.RefreshCardsAsync().GetAwaiter().GetResult();
            viewModel.ShowOfflineCommand.Execute(null);
            PumpDispatcher();

            Assert.Single(viewModel.AccountDevicesView.Cast<XiaomiAccountDeviceCardViewModel>(), card => card.Device.Did == plug.Did);
            Assert.Single(viewModel.AccountDevices, card => card.Device.Did == plug.Did).OpenCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();

            Assert.Equal(MainPage.XiaomiAccountDeviceDetail, viewModel.CurrentPage);
            Assert.NotNull(viewModel.CurrentXiaomiAccountDeviceDetail);
            Assert.Equal("米家智能插座3 · 设备详情 · CloudLight XiaoMi", window.Title);
            Assert.Equal(Visibility.Collapsed, deviceListPage.Visibility);
            Assert.Equal(Visibility.Visible, detailView.Visibility);
            Assert.Equal(Visibility.Collapsed, presenceView.Visibility);
            Assert.Single(Application.Current!.Windows.Cast<Window>());

            var detailBack = Assert.Single(FindVisualChildren<Button>(detailView), button => Equals(button.Content, "返回设备"));
            VerifyAndInvokeReturnButton(diagnostics, "Xiaomi plug", window, viewModel, detailBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);

            Assert.Single(viewModel.AccountDevices, card => card.Device.Did == band.Did).OpenCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.XiaomiAccountDeviceDetail, viewModel.CurrentPage);
            Assert.Equal("小米手环10 · 设备详情 · CloudLight XiaoMi", window.Title);
            Assert.Equal(band.Did, viewModel.CurrentXiaomiAccountDeviceDetail!.Device.Did);
            Assert.Equal(Visibility.Visible, detailView.Visibility);

            var bandBack = Assert.Single(FindVisualChildren<Button>(detailView), button => Equals(button.Content, "返回设备"));
            VerifyAndInvokeReturnButton(diagnostics, "Xiaomi band", window, viewModel, bandBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);

            Assert.Single(viewModel.AccountDevices, card => card.Device.Did == routerDevice.Did).RouterPresenceCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.RouterPresence, viewModel.CurrentPage);
            Assert.NotNull(viewModel.CurrentRouterPresence);
            Assert.Same(router, viewModel.CurrentRouterPresence!.Router);
            Assert.IsType<RouterPresenceViewModel>(presenceView.DataContext);
            Assert.Equal("AX3000T · 路由器 Presence · CloudLight XiaoMi", window.Title);
            Assert.Equal(Visibility.Collapsed, deviceListPage.Visibility);
            Assert.Equal(Visibility.Collapsed, detailView.Visibility);
            Assert.Equal(Visibility.Visible, presenceView.Visibility);
            var presenceCard = Assert.Single(viewModel.Cards);
            Assert.StartsWith("离线 ", presenceCard.Duration);
            Assert.DoesNotContain("持续时间未知", presenceCard.Duration);
            viewModel.ShowOnlineCommand.Execute(null);
            Assert.Single(Application.Current!.Windows.Cast<Window>());

            var presenceBack = Assert.Single(FindVisualChildren<Button>(presenceView), button => Equals(button.Content, "返回设备"));
            VerifyAndInvokeReturnButton(diagnostics, "Router Presence", window, viewModel, presenceBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);

            Trace.Flush();
            diagnostics.Add($"WPF Binding Error count: {bindingListener.Messages.Count}");
            Assert.Empty(bindingListener.Messages);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
            PresentationTraceSources.DataBindingSource.Switch.Level = originalBindingLevel;
            CloseWindowForTest(window);
            DisposeTrayIcon(window);
            viewModel?.Dispose();
            connectionAlerts?.Dispose();
            notificationRuntime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            dispatcher?.Dispose();
            qqChannel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (ownsApplication) application?.Shutdown();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AssertDeviceListVisible(
        MainViewModel viewModel,
        Grid deviceListPage,
        XiaomiAccountDeviceDetailView detailView,
        RouterPresenceView presenceView,
        string expectedFilteredDeviceDid)
    {
        Assert.Equal(MainPage.XiaomiDeviceList, viewModel.CurrentPage);
        Assert.Equal(Visibility.Visible, deviceListPage.Visibility);
        Assert.Equal(Visibility.Collapsed, detailView.Visibility);
        Assert.Equal(Visibility.Collapsed, presenceView.Visibility);
        Assert.Equal(expectedFilteredDeviceDid, Assert.Single(viewModel.AccountDevicesView.Cast<XiaomiAccountDeviceCardViewModel>()).Device.Did);
        Assert.Single(Application.Current!.Windows.Cast<Window>());
    }

    private static void AddMainWindowResources(Application application)
    {
        application.Resources["BooleanToVisibility"] = new BooleanToVisibilityConverter();
        application.Resources["SecondaryButton"] = new Style(typeof(Button));
        application.Resources["FilterButton"] = new Style(typeof(Button));
        application.Resources["SurfaceCard"] = new Style(typeof(Border));
        application.Resources["SectionTitle"] = new Style(typeof(TextBlock));
    }

    private static void VerifyAndInvokeReturnButton(
        List<string> diagnostics,
        string page,
        MainWindow window,
        MainViewModel viewModel,
        Button returnButton)
    {
        Assert.Same(viewModel.ReturnToDevicesCommand, returnButton.Command);
        Assert.NotNull(returnButton.Command);
        Assert.True(returnButton.IsEnabled);
        Assert.True(returnButton.Command.CanExecute(returnButton.CommandParameter));
        diagnostics.Add($"{page} return button DataContext type: {returnButton.DataContext?.GetType().FullName ?? "<null>"}");
        diagnostics.Add($"{page} return button Command type: {returnButton.Command.GetType().FullName}");
        diagnostics.Add($"{page} Command CanExecute: {returnButton.Command.CanExecute(returnButton.CommandParameter)}");
        diagnostics.Add($"{page} MainWindow DataContext type: {window.DataContext?.GetType().FullName ?? "<null>"}");
        diagnostics.Add($"{page} CurrentPage before: {viewModel.CurrentPage}");

        var peer = new ButtonAutomationPeer(returnButton);
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
        PumpDispatcher();

        diagnostics.Add($"{page} CurrentPage after: {viewModel.CurrentPage}");
    }

    private static XiaomiAccountDevice AccountDevice(
        string did,
        string name,
        XiaomiAccountDeviceType type,
        bool isRouter = false,
        string? partnerId = null,
        bool? online = true) =>
        new(did, isRouter ? "xiaomi.router.rd03" : "xiaomi.test.device", name, null, type, online, null,
            "home", "room", "测试家庭", "测试房间", partnerId, null, null, false,
            new XiaomiDeviceCapabilities(isRouter: isRouter));

    private static FrameworkElement? FindVisualChildByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement { Name: var currentName } element && currentName == name) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = FindVisualChildByName(VisualTreeHelper.GetChild(root, index), name);
            if (child is not null) return child;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = FindVisualChild<T>(VisualTreeHelper.GetChild(root, index));
            if (child is not null) return child;
        }
        return null;
    }

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

    private static void DisposeTrayIcon(MainWindow? window)
    {
        if (window is null) return;
        var tray = typeof(MainWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(window) as System.Windows.Forms.NotifyIcon;
        tray?.Dispose();
    }

    private static void CloseWindowForTest(MainWindow? window)
    {
        if (window is null) return;
        typeof(MainWindow).GetField("_exiting", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(window, true);
        if (window.IsLoaded)
        {
            window.Close();
            PumpDispatcher();
        }
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

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message);
        }

        public override void WriteLine(string? message) => Write(message);
    }

    private sealed class UiSource(IReadOnlyList<XiaomiAccountDevice> devices) : IXiaomiPresenceSource, IXiaomiAccountDeviceSource, IXiaomiDeviceControlSource
    {
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
        public Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(devices);
        public Task<XiaomiPowerStateResult> ReadPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(true, true));
        public Task<XiaomiPowerStateResult> SetPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, bool value, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(true, value));
        public Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(XiaomiAccountDevice device, CancellationToken cancellationToken) => Task.FromResult(device.Definition);
        public Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(XiaomiAccountDevice device, IReadOnlyList<XiaomiPropertyDefinition> properties, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiPropertyReadResult>>([]);
        public Task<XiaomiPropertyOperationResult> SetPropertyAsync(XiaomiAccountDevice device, XiaomiPropertyDefinition property, object? value, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPropertyOperationResult(true));
        public Task<XiaomiActionInvocationResult> InvokeActionAsync(XiaomiAccountDevice device, XiaomiActionDefinition action, IReadOnlyList<object?> inputArguments, CancellationToken cancellationToken) => Task.FromResult(new XiaomiActionInvocationResult(true, []));
    }
}
