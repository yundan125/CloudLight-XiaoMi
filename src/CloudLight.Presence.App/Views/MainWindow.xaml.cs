using System.ComponentModel;
using System.Windows;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Forms = System.Windows.Forms;

namespace CloudLight.Presence.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel; private readonly IPresenceRepository _repository; private readonly PresenceMonitor _monitor; private readonly PresenceDataTransferService _transfer; private readonly StartupRegistrationService _startup;
    private readonly long _runId;
    private readonly Forms.NotifyIcon _tray; private readonly Forms.ToolStripMenuItem _monitorItem; private readonly Forms.ToolStripMenuItem _startupItem; private readonly Forms.ToolStripMenuItem _minimizedItem; private bool _exiting; private SettingsWindow? _settingsWindow;
    public MainWindow(MainViewModel viewModel, IPresenceRepository repository, PresenceMonitor monitor, PresenceDataTransferService transfer, StartupRegistrationService startup, long runId)
    {
        InitializeComponent(); DataContext = viewModel; _viewModel = viewModel; _repository = repository; _monitor = monitor; _transfer = transfer; _startup = startup; _runId = runId;
        viewModel.OpenDeviceRequested += async (_, device) => await OpenDeviceAsync(device);
        _tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "CloudLight XiaoMi", Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开 CloudLight XiaoMi", null, (_, _) => ShowFromTray()); menu.Items.Add(new Forms.ToolStripSeparator());
        _monitorItem = new Forms.ToolStripMenuItem("暂停监控", null, async (_, _) => await ToggleMonitoringAsync()); menu.Items.Add(_monitorItem); menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = new Forms.ToolStripMenuItem("开机自启") { CheckOnClick = true }; _startupItem.Click += async (_, _) => await TraySettingChangedAsync(); menu.Items.Add(_startupItem);
        _minimizedItem = new Forms.ToolStripMenuItem("启动后最小化") { CheckOnClick = true }; _minimizedItem.Click += async (_, _) => await TraySettingChangedAsync(); menu.Items.Add(_minimizedItem); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync()); menu.Opening += (_, _) => RefreshTrayMenu(); _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    protected override void OnClosing(CancelEventArgs e) { if (!_exiting) { e.Cancel = true; Hide(); _tray.ShowBalloonTip(1200, "CloudLight XiaoMi", "监控仍在后台运行。", Forms.ToolTipIcon.Info); } base.OnClosing(e); }
    private void ShowFromTray() { if (!IsVisible) Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); Topmost = true; Topmost = false; Focus(); }
    private async Task OpenDeviceAsync(NetworkDevice device) { var vm = new DeviceDetailViewModel(_repository, new PresenceStatisticsService(_repository), device); await vm.LoadAsync(); var window = new DeviceDetailWindow(vm) { Owner = this }; window.Show(); }
    private void SettingsClicked(object sender, RoutedEventArgs e) { if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; } _settingsWindow = new SettingsWindow(_viewModel, _transfer, _startup) { Owner = this }; _settingsWindow.Show(); }
    private async Task ToggleMonitoringAsync() { if (_monitor.IsRunning) await _viewModel.PauseAsync(); else await _viewModel.ResumeAsync(); RefreshTrayMenu(); }
    private void RefreshTrayMenu() { _monitorItem.Text = _monitor.IsRunning ? "暂停监控" : "开始监控"; _startupItem.Checked = _viewModel.CurrentSettings.StartWithWindows; _minimizedItem.Checked = _viewModel.CurrentSettings.StartMinimized; }
    private async Task TraySettingChangedAsync() { try { _startup.Apply(_startupItem.Checked); await _viewModel.SaveGeneralSettingsAsync(_startupItem.Checked, _minimizedItem.Checked); } catch (Exception exception) { _tray.ShowBalloonTip(1800, "设置未保存", exception.Message, Forms.ToolTipIcon.Error); RefreshTrayMenu(); } }
    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CloudLightPresence.ico")) ?? throw new InvalidOperationException("应用图标资源缺失。 ");
        using var source = new System.Drawing.Icon(resource.Stream); return (System.Drawing.Icon)source.Clone();
    }
    private async Task ExitAsync() { if (_exiting) return; _exiting = true; try { if (_monitor.IsRunning) await _monitor.StopAsync("软件退出", CancellationToken.None); } catch { } try { await _repository.EndApplicationRunAsync(_runId, DateTimeOffset.UtcNow, CancellationToken.None); } catch { } _tray.Visible = false; _tray.Icon?.Dispose(); _tray.Dispose(); Close(); System.Windows.Application.Current.Shutdown(); }
}
