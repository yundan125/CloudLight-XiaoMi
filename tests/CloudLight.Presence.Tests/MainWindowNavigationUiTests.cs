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
using CloudLight.Presence.Infrastructure.SecureStorage;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;
using Xunit.Abstractions;

namespace CloudLight.Presence.Tests;

[Collection("Wpf UI")]
public sealed class MainWindowNavigationUiTests
{
    private readonly ITestOutputHelper _output;

    public MainWindowNavigationUiTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MainWindowNavigatesItsRealButtonsWithoutBindingErrorsOrAdditionalWindows()
    {
        var diagnostics = new List<string>();

        await WpfTestHost.RunAsync(() => RunScenario(diagnostics));

        foreach (var line in diagnostics)
            _output.WriteLine(line);
    }

    private static void RunScenario(List<string> diagnostics)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-MainWindow-Ui-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainViewModel? viewModel = null;
        NotificationSettingsViewModel? notifications = null;
        NotificationRuntime? notificationRuntime = null;
        XiaomiConnectionAlertService? connectionAlerts = null;
        QQNotificationChannel? qqChannel = null;
        NotificationDispatcher? dispatcher = null;
        MainWindow? window = null;
        var bindingListener = new BindingErrorListener();
        var originalBindingLevel = PresentationTraceSources.DataBindingSource.Switch.Level;

        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            repository.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            var routerDevice = AccountDevice("router-did", "AX3000T", XiaomiAccountDeviceType.Router, isRouter: true, partnerId: "router-partner");
            var plug = AccountDevice("plug-did", "米家智能插座3", XiaomiAccountDeviceType.Plug, online: false);
            var band = AccountDevice("band-did", "小米手环10", XiaomiAccountDeviceType.Other);
            var phone = AccountDevice("phone-did", "Redmi K50", XiaomiAccountDeviceType.Other);
            var observedAt = DateTimeOffset.UtcNow;
            var router = repository.UpsertRouterAsync(new Router(0, routerDevice.Did, routerDevice.Model!, "router-partner", routerDevice.Name, null, null, observedAt, observedAt), CancellationToken.None).GetAwaiter().GetResult();
            var networkDevice = repository.InsertDeviceAsync(new NetworkDevice(0, router.Id, "AA:BB:CC:DD:EE:01", "测试手机", "测试手机", null, null, "192.168.1.2", "5G", -50, PresenceState.Offline, observedAt.AddHours(-3), observedAt, observedAt.AddHours(-2)), CancellationToken.None).GetAwaiter().GetResult();
            var subject = repository.CreateSubjectAsync("测试主体", null, Guid.NewGuid(), observedAt.AddHours(-3), CancellationToken.None).GetAwaiter().GetResult();
            repository.SetSubjectDevicesAsync(subject.Id, [networkDevice.Id], observedAt.AddHours(-3), CancellationToken.None).GetAwaiter().GetResult();
            var source = new UiSource([routerDevice, plug, band, phone]);
            var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            var subjects = new SubjectPresenceService(repository, new PresenceStatisticsService(repository));
            qqChannel = new QQNotificationChannel(paths.LogsDirectory);
            notifications = new NotificationSettingsViewModel(repository, new JsonSettingsStore(paths), new DpapiQqSecretStore(paths), qqChannel);
            notifications.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            dispatcher = new NotificationDispatcher(repository, [qqChannel]);
            notificationRuntime = new NotificationRuntime(monitor, new NotificationRuleService(repository, subjects), dispatcher);
            connectionAlerts = new XiaomiConnectionAlertService(
                monitor,
                repository,
                dispatcher,
                _ => Task.FromResult(new ConnectionAlertConfiguration(new ConnectionAlertSettings(), NotificationTargetType.Private, "")),
                subscribe: false);
            viewModel = new MainViewModel(repository, subjects, source, monitor, new JsonSettingsStore(paths), notifications, source);
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

            var deviceListPage = Assert.IsType<Grid>(window.FindName("DeviceListPage"));
            var detailView = Assert.IsType<XiaomiAccountDeviceDetailView>(window.FindName("XiaomiAccountDeviceDetailPage"));
            var presenceView = Assert.IsType<RouterPresenceView>(window.FindName("RouterPresencePage"));

            Assert.Equal(MainPage.XiaomiDeviceList, viewModel.CurrentPage);
            Assert.Null(viewModel.CurrentXiaomiAccountDeviceDetail);
            Assert.Null(viewModel.CurrentRouterPresence);
            Assert.Equal(Visibility.Visible, deviceListPage.Visibility);
            Assert.Equal(Visibility.Collapsed, detailView.Visibility);
            Assert.Equal(Visibility.Collapsed, presenceView.Visibility);
            Assert.Single(Application.Current!.Windows.Cast<Window>());
            AssertActiveSidebar(window, "OverviewNavButton");
            AssertSingleActiveFilter(window, viewModel, viewModel.ShowAllCommand);

            var devicesExpanded = viewModel.IsDevicesExpanded;
            InvokeButton(Assert.IsType<Button>(window.FindName("ToggleDevicesButton")));
            PumpDispatcher();
            Assert.NotEqual(devicesExpanded, viewModel.IsDevicesExpanded);
            InvokeButton(Assert.IsType<Button>(window.FindName("ToggleDevicesButton")));
            PumpDispatcher();
            Assert.Equal(devicesExpanded, viewModel.IsDevicesExpanded);

            var auxiliaryHost = Assert.IsType<ContentControl>(window.FindName("AuxiliaryPageHost"));
            InvokeButton(Assert.IsType<Button>(window.FindName("QqNavButton")));
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.QqReminder, viewModel.CurrentPage);
            Assert.IsType<QqReminderWindow>(auxiliaryHost.Content);
            Assert.Single(Application.Current!.Windows.Cast<Window>());
            AssertActiveSidebar(window, "QqNavButton");

            InvokeButton(Assert.IsType<Button>(window.FindName("SettingsNavButton")));
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.Settings, viewModel.CurrentPage);
            Assert.IsType<SettingsWindow>(auxiliaryHost.Content);
            Assert.Single(Application.Current!.Windows.Cast<Window>());
            AssertActiveSidebar(window, "SettingsNavButton");

            InvokeButton(Assert.IsType<Button>(window.FindName("AboutNavButton")));
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.About, viewModel.CurrentPage);
            Assert.IsType<AboutView>(auxiliaryHost.Content);
            Assert.Single(Application.Current!.Windows.Cast<Window>());
            AssertActiveSidebar(window, "AboutNavButton");

            InvokeButton(Assert.IsType<Button>(window.FindName("DevicesNavButton")));
            PumpDispatcher();
            window.UpdateLayout();
            AssertActiveSidebar(window, "OverviewNavButton");

            viewModel.RefreshAccountDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            viewModel.RefreshCardsAsync().GetAwaiter().GetResult();
            Assert.DoesNotContain(viewModel.SidebarDevices, item => item.Kind == SidebarDeviceKind.PresenceSubject);
            Assert.DoesNotContain(viewModel.SidebarGroups, group => string.Equals(group.Title, "Presence", StringComparison.Ordinal));
            Assert.Contains(viewModel.SidebarDevices, item => item.Kind == SidebarDeviceKind.Router && item.Name == router.Name);
            Assert.Contains(viewModel.SidebarDevices, item => item.Kind == SidebarDeviceKind.XiaomiAccountDevice && item.Name == plug.Name);
            Assert.Contains(viewModel.SidebarDevices, item => item.Kind == SidebarDeviceKind.XiaomiAccountDevice && item.Name == phone.Name);
            viewModel.ShowOfflineCommand.Execute(null);
            PumpDispatcher();
            AssertSingleActiveFilter(window, viewModel, viewModel.ShowOfflineCommand);

            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);

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
            AssertActiveSidebar(window, identity: plug.Did);

            viewModel.RefreshAccountDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            PumpDispatcher();
            window.UpdateLayout();
            AssertActiveSidebar(window, identity: plug.Did);

            var detailBack = Assert.Single(FindVisualChildren<Button>(detailView), button => Equals(button.Content, "返回设备"));
            VerifyAndInvokeReturnButton(diagnostics, "Xiaomi plug", window, viewModel, detailBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);
            AssertActiveSidebar(window, "OverviewNavButton");

            Assert.Single(viewModel.AccountDevices, card => card.Device.Did == phone.Did).OpenCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.XiaomiAccountDeviceDetail, viewModel.CurrentPage);
            Assert.Equal("Redmi K50 · 设备详情 · CloudLight XiaoMi", window.Title);
            Assert.Equal(phone.Did, viewModel.CurrentXiaomiAccountDeviceDetail!.Device.Did);
            Assert.Equal(Visibility.Visible, detailView.Visibility);
            AssertActiveSidebar(window, identity: phone.Did);

            var phoneBack = Assert.Single(FindVisualChildren<Button>(detailView), button => Equals(button.Content, "返回设备"));
            VerifyAndInvokeReturnButton(diagnostics, "Redmi K50", window, viewModel, phoneBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);
            AssertActiveSidebar(window, "OverviewNavButton");

            Assert.Single(viewModel.AccountDevices, card => card.Device.Did == band.Did).OpenCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.XiaomiAccountDeviceDetail, viewModel.CurrentPage);
            Assert.Equal("小米手环10 · 设备详情 · CloudLight XiaoMi", window.Title);
            Assert.Equal(band.Did, viewModel.CurrentXiaomiAccountDeviceDetail!.Device.Did);
            Assert.Equal(Visibility.Visible, detailView.Visibility);
            AssertActiveSidebar(window, identity: band.Did);

            source.RemoveDevice(band.Did);
            viewModel.RefreshAccountDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);
            AssertActiveSidebar(window, "OverviewNavButton");

            var bandBack = Assert.Single(FindVisualChildren<Button>(detailView), button => Equals(button.Content, "返回设备"));
            VerifyAndInvokeReturnButton(diagnostics, "Xiaomi band", window, viewModel, bandBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);
            AssertActiveSidebar(window, "OverviewNavButton");

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
            AssertActiveSidebar(window, identity: router.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var presenceCard = Assert.Single(viewModel.Cards);
            Assert.StartsWith("离线 ", presenceCard.Duration);
            Assert.DoesNotContain("持续时间未知", presenceCard.Duration);
            viewModel.ShowOnlineCommand.Execute(null);
            AssertSingleActiveFilter(window, viewModel, viewModel.ShowOnlineCommand);
            Assert.Single(Application.Current!.Windows.Cast<Window>());

            var presenceBack = Assert.Single(FindVisualChildren<Button>(presenceView), button => Equals(button.Content, "← 设备总览"));
            VerifyAndInvokeReturnButton(diagnostics, "Router Presence", window, viewModel, presenceBack);
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);
            AssertActiveSidebar(window, "OverviewNavButton");

            var subjectDetail = new SubjectDetailViewModel(repository, subjects, monitor, subject);
            subjectDetail.LoadAsync().GetAwaiter().GetResult();
            viewModel.ShowSubjectDetail(subjectDetail);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.SubjectDetail, viewModel.CurrentPage);
            AssertActiveSidebar(window, identity: router.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var networkDetail = new DeviceDetailViewModel(repository, new PresenceStatisticsService(repository), monitor, networkDevice);
            networkDetail.LoadAsync().GetAwaiter().GetResult();
            viewModel.ShowNetworkDeviceDetail(networkDetail);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(MainPage.NetworkDeviceDetail, viewModel.CurrentPage);
            AssertActiveSidebar(window, identity: router.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

            viewModel.ShowDeviceList();
            PumpDispatcher();
            window.UpdateLayout();
            AssertActiveSidebar(window, "OverviewNavButton");

            viewModel.ShowRouterPresence(router);
            PumpDispatcher();
            window.UpdateLayout();
            AssertActiveSidebar(window, identity: router.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            viewModel.Routers.Clear();
            viewModel.RefreshSidebarAsync(CancellationToken.None).GetAwaiter().GetResult();
            PumpDispatcher();
            window.UpdateLayout();
            AssertDeviceListVisible(viewModel, deviceListPage, detailView, presenceView, plug.Did);
            Assert.Null(viewModel.SelectedRouter);
            AssertActiveSidebar(window, "OverviewNavButton");

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
            notifications?.Dispose();
            viewModel?.Dispose();
            connectionAlerts?.Dispose();
            notificationRuntime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            dispatcher?.Dispose();
            qqChannel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    private static void AssertActiveSidebar(MainWindow window, string? buttonName = null, string? identity = null)
    {
        var active = FindVisualChildren<Button>(window)
            .Where(button => button.Name is "OverviewNavButton" or "QqNavButton" or "SettingsNavButton" or "AboutNavButton"
                || button.DataContext is SidebarDeviceItemViewModel)
            .Where(button => button.Tag is true)
            .ToArray();
        var selected = Assert.Single(active);
        if (buttonName is not null) Assert.Equal(buttonName, selected.Name);
        if (identity is not null) Assert.Equal(identity, Assert.IsType<SidebarDeviceItemViewModel>(selected.DataContext).Identity);
    }

    private static void AssertSingleActiveFilter(MainWindow window, MainViewModel viewModel, RelayCommand expected)
    {
        var commands = new[] { viewModel.ShowAllCommand, viewModel.ShowOnlineCommand, viewModel.ShowOfflineCommand, viewModel.ShowUnknownCommand };
        var filters = FindVisualChildren<Button>(window)
            .Where(button => button.IsVisible && commands.Contains(button.Command))
            .ToArray();
        Assert.Equal(4, filters.Length);
        Assert.Single(filters, button => ReferenceEquals(button.Command, expected) && button.Tag is true);
        Assert.All(filters.Where(button => !ReferenceEquals(button.Command, expected)), button => Assert.NotEqual(true, button.Tag));
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

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
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
        private readonly List<XiaomiAccountDevice> _devices = devices.ToList();
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
        public Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiAccountDevice>>(_devices.ToArray());
        public void RemoveDevice(string did) => _devices.RemoveAll(value => value.Did == did);
        public Task<XiaomiPowerStateResult> ReadPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(true, true));
        public Task<XiaomiPowerStateResult> SetPowerStateAsync(XiaomiAccountDevice device, XiaomiPowerCapability capability, bool value, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPowerStateResult(true, value));
        public Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(XiaomiAccountDevice device, CancellationToken cancellationToken) => Task.FromResult(device.Definition);
        public Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(XiaomiAccountDevice device, IReadOnlyList<XiaomiPropertyDefinition> properties, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiPropertyReadResult>>([]);
        public Task<XiaomiPropertyOperationResult> SetPropertyAsync(XiaomiAccountDevice device, XiaomiPropertyDefinition property, object? value, CancellationToken cancellationToken) => Task.FromResult(new XiaomiPropertyOperationResult(true));
        public Task<XiaomiActionInvocationResult> InvokeActionAsync(XiaomiAccountDevice device, XiaomiActionDefinition action, IReadOnlyList<object?> inputArguments, CancellationToken cancellationToken) => Task.FromResult(new XiaomiActionInvocationResult(true, []));
    }
}
