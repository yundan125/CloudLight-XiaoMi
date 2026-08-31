using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Diagnostics;
using CloudLight.Presence.Infrastructure.Notifications;
using CloudLight.Presence.Infrastructure.Settings;
using CloudLight.Presence.Infrastructure.Updates;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;

namespace CloudLight.Presence.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IPresenceRepository _repository;
    private readonly ISubjectPresenceService _subjectPresence;
    private readonly PresenceMonitor _monitor;
    private readonly PresenceDataTransferService _transfer;
    private readonly StartupRegistrationService _startup;
    private readonly long _runId;
    private readonly NotificationRuntime _notificationRuntime;
    private readonly XiaomiConnectionAlertService _connectionAlerts;
    private readonly QQNotificationChannel _qqChannel;
    private readonly NotificationDispatcher _notificationDispatcher;
    private readonly GitHubReleaseUpdateService? _updateService;
    private readonly DiagnosticsExportService? _diagnosticsExport;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _xiaomiStatusItem;
    private readonly Forms.ToolStripMenuItem _qqStatusItem;
    private readonly Forms.ToolStripMenuItem _presenceStatusItem;
    private readonly Forms.ToolStripMenuItem _refreshItem;
    private readonly Forms.ToolStripMenuItem _pauseMenuItem;
    private readonly Forms.ToolStripMenuItem _resumeItem;
    private readonly Forms.ToolStripMenuItem _monitorItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly Forms.ToolStripMenuItem _minimizedItem;
    private readonly AppPaths _paths;
    private bool _exiting;
    private int _xiaomiDetailNavigationRequest;
    private System.Windows.Controls.UserControl? _auxiliaryPage;

    public MainWindow(
        MainViewModel viewModel,
        IPresenceRepository repository,
        ISubjectPresenceService subjectPresence,
        PresenceMonitor monitor,
        PresenceDataTransferService transfer,
        StartupRegistrationService startup,
        AppPaths paths,
        long runId,
        NotificationRuntime notificationRuntime,
        XiaomiConnectionAlertService connectionAlerts,
        QQNotificationChannel qqChannel,
        NotificationDispatcher notificationDispatcher,
        GitHubReleaseUpdateService? updateService = null,
        DiagnosticsExportService? diagnosticsExport = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _repository = repository;
        _subjectPresence = subjectPresence;
        _monitor = monitor;
        _transfer = transfer;
        _startup = startup;
        _paths = paths;
        _runId = runId;
        _notificationRuntime = notificationRuntime;
        _connectionAlerts = connectionAlerts;
        _qqChannel = qqChannel;
        _notificationDispatcher = notificationDispatcher;
        _updateService = updateService;
        _diagnosticsExport = diagnosticsExport;

        viewModel.PropertyChanged += ViewModelPropertyChanged;
        viewModel.OpenSubjectRequested += async (_, subject) => await OpenSubjectAsync(subject);
        viewModel.OpenRouterPresenceRequested += async (_, router) =>
        {
            try { await _viewModel.ShowRouterPresenceAsync(router); }
            catch (Exception exception) { CloudLightDialogs.Info(this, "切换路由器失败", exception.Message, warning: true); }
        };
        viewModel.OpenXiaomiAccountDeviceRequested += async (_, device) => await OpenXiaomiAccountDeviceAsync(device);

        _tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "CloudLight XiaoMi", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 CloudLight XiaoMi", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _xiaomiStatusItem = new Forms.ToolStripMenuItem("Xiaomi：正在初始化") { Enabled = false };
        _qqStatusItem = new Forms.ToolStripMenuItem("QQ：未配置") { Enabled = false };
        _presenceStatusItem = new Forms.ToolStripMenuItem("Presence：未启动") { Enabled = false };
        menu.Items.Add(_xiaomiStatusItem);
        menu.Items.Add(_qqStatusItem);
        menu.Items.Add(_presenceStatusItem);
        _refreshItem = new Forms.ToolStripMenuItem("立即刷新", null, async (_, _) => await RefreshFromTrayAsync());
        menu.Items.Add(_refreshItem);
        _pauseMenuItem = new Forms.ToolStripMenuItem("暂停监控");
        AddPauseMenuItem(_pauseMenuItem, "15 分钟", TimeSpan.FromMinutes(15));
        AddPauseMenuItem(_pauseMenuItem, "30 分钟", TimeSpan.FromMinutes(30));
        AddPauseMenuItem(_pauseMenuItem, "1 小时", TimeSpan.FromHours(1));
        AddPauseMenuItem(_pauseMenuItem, "2 小时", TimeSpan.FromHours(2));
        AddPauseMenuItem(_pauseMenuItem, "手动恢复前", null);
        menu.Items.Add(_pauseMenuItem);
        _resumeItem = new Forms.ToolStripMenuItem("立即恢复监控", null, async (_, _) => await ResumeFromTrayAsync());
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _monitorItem = new Forms.ToolStripMenuItem("暂停监控", null, async (_, _) => await ToggleMonitoringAsync());
        menu.Items.Add(_monitorItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = new Forms.ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _startupItem.Click += async (_, _) => await TraySettingChangedAsync();
        menu.Items.Add(_startupItem);
        _minimizedItem = new Forms.ToolStripMenuItem("启动后最小化") { CheckOnClick = true };
        _minimizedItem.Click += async (_, _) => await TraySettingChangedAsync();
        menu.Items.Add(_minimizedItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => RefreshTrayMenu();
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            if (_viewModel.CurrentSettings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1200, "CloudLight XiaoMi", "监控仍在后台运行。", Forms.ToolTipIcon.Info);
            }
            else
            {
                e.Cancel = true;
                _ = ExitAsync();
            }
        }
        base.OnClosing(e);
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPage)
            or nameof(MainViewModel.CurrentNavigationTarget)
            or nameof(MainViewModel.CurrentSubjectDetail)
            or nameof(MainViewModel.CurrentNetworkDeviceDetail)
            or nameof(MainViewModel.CurrentXiaomiAccountDeviceDetail)
            or nameof(MainViewModel.CurrentRouterPresence)
            or nameof(MainViewModel.CloudStatus)
            or nameof(MainViewModel.IsMonitoring)
            or nameof(MainViewModel.IsPaused)
            or nameof(MainViewModel.PauseStatusText)
            or nameof(MainViewModel.CurrentSettings))
        {
            RefreshTrayMenu();
            if (Dispatcher.CheckAccess()) UpdateAuxiliaryPage();
            else Dispatcher.BeginInvoke(UpdateAuxiliaryPage);
        }
    }

    private void UpdateAuxiliaryPage()
    {
        System.Windows.Controls.UserControl? next = null;
        switch (_viewModel.CurrentPage)
        {
            case MainPage.QqReminder when _viewModel.Notifications is { } notifications:
                next = _auxiliaryPage as QqReminderWindow ?? new QqReminderWindow(notifications);
                break;
            case MainPage.Settings when _viewModel.Notifications is { } notifications:
                next = _auxiliaryPage as SettingsWindow ?? new SettingsWindow(_viewModel, notifications, _transfer, _startup, _paths, _diagnosticsExport, _notificationRuntime);
                break;
            case MainPage.About:
                next = _auxiliaryPage as AboutView ?? new AboutView(_paths, _updateService);
                break;
            case MainPage.SubjectDetail when _viewModel.CurrentSubjectDetail is { } subjectDetail:
                if (_auxiliaryPage is not SubjectDetailWindow existingSubject || !ReferenceEquals(existingSubject.DataContext, subjectDetail))
                {
                    var subjectView = new SubjectDetailWindow(subjectDetail, _repository, _viewModel.SelectedRouter?.Id ?? 0, _viewModel.RefreshCardsAsync);
                    subjectView.Deleted += (_, _) => _viewModel.ShowDeviceList();
                    subjectDetail.OpenDeviceRequested += async (_, device) => await OpenDeviceAsync(device);
                    next = subjectView;
                }
                else next = existingSubject;
                break;
            case MainPage.NetworkDeviceDetail when _viewModel.CurrentNetworkDeviceDetail is { } networkDetail:
                if (_auxiliaryPage is not DeviceDetailWindow existingDevice || !ReferenceEquals(existingDevice.DataContext, networkDetail))
                    next = new DeviceDetailWindow(networkDetail);
                else next = existingDevice;
                break;
        }

        if (!ReferenceEquals(_auxiliaryPage, next))
        {
            AuxiliaryPageHost.Content = next;
            _auxiliaryPage = next;
        }
    }

    private void AccountDeviceCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null) return;
        if (sender is Border { DataContext: XiaomiAccountDeviceCardViewModel card })
            card.OpenCommand.Execute(null);
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async Task OpenDeviceAsync(NetworkDevice device)
    {
        var vm = new DeviceDetailViewModel(_repository, new PresenceStatisticsService(_repository), _monitor, device);
        try
        {
            await vm.LoadAsync();
            _viewModel.ShowNetworkDeviceDetail(vm);
        }
        catch
        {
            vm.Dispose();
            throw;
        }
    }

    private async Task OpenSubjectAsync(PresenceSubject subject)
    {
        if (_viewModel.SelectedRouter is null) return;
        var vm = new SubjectDetailViewModel(_repository, _subjectPresence, _monitor, subject);
        try
        {
            await vm.LoadAsync();
            _viewModel.ShowSubjectDetail(vm);
        }
        catch
        {
            vm.Dispose();
            throw;
        }
    }

    private async Task OpenXiaomiAccountDeviceAsync(XiaomiAccountDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.Did))
        {
            _viewModel.ShowDeviceList();
            CloudLightDialogs.Info(this, "无法打开设备", "未找到可打开的 Xiaomi 设备。", warning: true);
            return;
        }

        var source = _viewModel.DeviceControlSource;
        if (source is null)
        {
            _viewModel.ShowDeviceList();
            CloudLightDialogs.Info(this, "设备详情暂不可用", "当前 Xiaomi 会话暂时无法用于读取设备能力。");
            return;
        }

        var request = ++_xiaomiDetailNavigationRequest;
        var detail = new XiaomiAccountDeviceDetailViewModel(device, source, new MiotChineseLocalizationService());
        detail.ActionRequestHandler = HandleXiaomiActionRequestAsync;
        try { await detail.LoadAsync(); }
        catch (OperationCanceledException) { detail.Dispose(); return; }
        catch (Exception exception)
        {
            detail.Dispose();
            if (request != _xiaomiDetailNavigationRequest) return;
            _viewModel.ShowDeviceList();
            CloudLightDialogs.Info(this, "设备详情加载失败", $"无法加载“{device.DisplayName}”的设备详情。\n\n{exception.Message}", warning: true);
            return;
        }

        if (request != _xiaomiDetailNavigationRequest) { detail.Dispose(); return; }
        _viewModel.ShowXiaomiAccountDeviceDetail(detail);
    }

    private Task<XiaomiActionRequestResult?> HandleXiaomiActionRequestAsync(XiaomiActionViewModel action)
    {
        if (action.RequiresConfirmation)
        {
            if (!CloudLightDialogs.Confirm(this, "确认高风险操作", $"确定要执行“{action.DisplayName}”吗？\n\n{action.RiskWarning}", danger: true, accept: "继续执行"))
                return Task.FromResult<XiaomiActionRequestResult?>(null);
        }
        if (!action.HasInputArguments) return Task.FromResult<XiaomiActionRequestResult?>(new XiaomiActionRequestResult([], true));
        var dialog = new XiaomiActionDialog(action) { Owner = this };
        return Task.FromResult<XiaomiActionRequestResult?>(dialog.ShowDialog() == true ? new XiaomiActionRequestResult(dialog.Values, true) : null);
    }

    private async Task ToggleMonitoringAsync()
    {
        if (_monitor.IsPaused || !_monitor.IsRunning) await _viewModel.ResumeAsync();
        else await _viewModel.PauseAsync();
        RefreshTrayMenu();
    }

    private void RefreshTrayMenu()
    {
        var qq = _qqChannel.Status;
        _xiaomiStatusItem.Text = $"Xiaomi：{_viewModel.CloudStatus}";
        _qqStatusItem.Text = $"QQ：{FormatQqStatus(qq)}";
        _presenceStatusItem.Text = $"Presence：{(_viewModel.IsPaused ? _viewModel.PauseStatusText.Replace("Presence 监控", "监控", StringComparison.Ordinal) : _monitor.IsRunning ? "监控中" : "未启动")}";
        _refreshItem.Enabled = _viewModel.RefreshCommand.CanExecute(null);
        _monitorItem.Text = _monitor.IsPaused ? "继续监控" : _monitor.IsRunning ? "暂停监控" : "开始监控";
        _monitorItem.Enabled = _viewModel.SelectedRouter is not null || _monitor.IsPaused;
        _pauseMenuItem.Enabled = !_monitor.IsPaused && _viewModel.SelectedRouter is not null;
        _resumeItem.Enabled = _monitor.IsPaused;
        _startupItem.Checked = _viewModel.CurrentSettings.StartWithWindows;
        _minimizedItem.Checked = _viewModel.CurrentSettings.StartMinimized;
    }

    private async Task RefreshFromTrayAsync()
    {
        try { await _viewModel.RefreshAsync(); }
        catch (Exception exception) { _tray.ShowBalloonTip(1800, "刷新失败", exception.Message, Forms.ToolTipIcon.Error); }
        finally { RefreshTrayMenu(); }
    }

    private async Task ResumeFromTrayAsync()
    {
        try { await _viewModel.ResumeAsync(); }
        catch (Exception exception) { _tray.ShowBalloonTip(1800, "恢复失败", exception.Message, Forms.ToolTipIcon.Error); }
        finally { RefreshTrayMenu(); }
    }

    private async Task PauseFromTrayAsync(TimeSpan? duration)
    {
        try { await _viewModel.PauseAsync(duration); }
        catch (Exception exception) { _tray.ShowBalloonTip(1800, "暂停失败", exception.Message, Forms.ToolTipIcon.Error); }
        finally { RefreshTrayMenu(); }
    }

    private void AddPauseMenuItem(Forms.ToolStripMenuItem parent, string text, TimeSpan? duration) =>
        parent.DropDownItems.Add(text, null, async (_, _) => await PauseFromTrayAsync(duration));

    private static string FormatQqStatus(NotificationChannelStatus status) => status.Connected
        ? "已连接"
        : status.Configured
            ? status.ConnectionState switch
            {
                NotificationConnectionState.Authenticating => "认证中",
                NotificationConnectionState.Connecting => "连接中",
                NotificationConnectionState.Reconnecting => "重连中",
                NotificationConnectionState.AuthenticationFailed => "认证失败",
                NotificationConnectionState.GatewayFailed => "网关失败",
                _ => "未连接"
            }
            : "未配置";

    private async Task TraySettingChangedAsync()
    {
        try
        {
            _startup.Apply(_startupItem.Checked);
            await _viewModel.SaveGeneralSettingsAsync(_startupItem.Checked, _minimizedItem.Checked);
        }
        catch (Exception exception)
        {
            _tray.ShowBalloonTip(1800, "设置未保存", exception.Message, Forms.ToolTipIcon.Error);
            RefreshTrayMenu();
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/CloudLight.XiaoMi;component/Assets/CloudLightPresence.ico")) ?? throw new InvalidOperationException("应用图标资源缺失。");
        using var source = new System.Drawing.Icon(resource.Stream);
        return (System.Drawing.Icon)source.Clone();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        try { await _notificationRuntime.DisposeAsync(); } catch { }
        try { _connectionAlerts.Dispose(); } catch { }
        try { await _qqChannel.DisposeAsync(); } catch { }
        _notificationDispatcher.Dispose();
        _viewModel.Notifications?.Dispose();
        _viewModel.Dispose();
        try { _updateService?.Dispose(); } catch { }
        try { if (_monitor.IsRunning) await _monitor.StopAsync("软件退出", CancellationToken.None); } catch { }
        try { await _repository.EndApplicationRunAsync(_runId, DateTimeOffset.UtcNow, CancellationToken.None); } catch { }
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
