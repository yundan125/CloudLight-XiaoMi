using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.Views;

public partial class SettingsWindow : System.Windows.Controls.UserControl
{
    private readonly MainViewModel _main;
    private readonly NotificationSettingsViewModel _notifications;
    private readonly PresenceDataTransferService _transfer;
    private readonly StartupRegistrationService _startup;
    private readonly AppPaths _paths;
    private bool _loaded;

    public SettingsWindow(MainViewModel main, NotificationSettingsViewModel notifications, PresenceDataTransferService transfer, StartupRegistrationService startup, AppPaths paths)
    {
        InitializeComponent();
        DataContext = main;
        _main = main;
        _notifications = notifications;
        _transfer = transfer;
        _startup = startup;
        _paths = paths;
        DataPathText.Text = paths.RootDirectory;
        StartWithWindowsBox.IsChecked = main.CurrentSettings.StartWithWindows;
        StartMinimizedBox.IsChecked = main.CurrentSettings.StartMinimized;
        PollingIntervalBox.Text = main.PollingIntervalSeconds.ToString();
        _loaded = true;
    }

    private Window? OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current?.MainWindow;

    private async void GeneralSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        try
        {
            var enabled = StartWithWindowsBox.IsChecked == true;
            _startup.Apply(enabled);
            await _main.SaveGeneralSettingsAsync(enabled, StartMinimizedBox.IsChecked == true);
            GeneralStatus.Text = enabled ? "开机自动启动已开启。" : "开机自动启动已关闭。";
        }
        catch (Exception exception) { GeneralStatus.Text = $"设置失败：{exception.Message}"; }
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.ExportsDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出数据 - CloudLight XiaoMi",
            Filter = "CloudLight XiaoMi 数据 (*.clpresence)|*.clpresence",
            DefaultExt = ".clpresence",
            AddExtension = true,
            InitialDirectory = _paths.ExportsDirectory,
            FileName = $"CloudLight-XiaoMi-{DateTime.Now:yyyyMMdd-HHmm}.clpresence"
        };
        if (OwnerWindow is not { } owner || dialog.ShowDialog(owner) != true) return;
        try
        {
            DataStatus.Text = "正在导出…";
            await _transfer.ExportAsync(dialog.FileName, CancellationToken.None);
            DataStatus.Text = $"导出完成：{dialog.FileName}\n导出文件不包含 Xiaomi 登录信息或通知密钥；QQ 接收人和提醒规则会一并导出。";
        }
        catch (Exception exception) { DataStatus.Text = $"导出失败：{exception.Message}"; }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入数据 - CloudLight XiaoMi",
            Filter = "CloudLight XiaoMi 数据 (*.clpresence)|*.clpresence",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_paths.ExportsDirectory) ? _paths.ExportsDirectory : _paths.RootDirectory
        };
        if (OwnerWindow is not { } owner || dialog.ShowDialog(owner) != true) return;
        if (!CloudLightDialogs.Confirm(
                owner,
                "导入数据",
                $"将验证并合并“{Path.GetFileName(dialog.FileName)}”。\n\n现有设备、Presence、历史记录、QQ 接收人和提醒规则不会被整体删除；重复记录会自动跳过。",
                accept: "继续导入")) return;
        var wasRunning = _main.IsMonitoring;
        try
        {
            DataStatus.Text = "正在验证并合并…";
            if (wasRunning) await _main.PauseAsync();
            var result = await _transfer.ImportAsync(dialog.FileName, CancellationToken.None);
            await _main.ReloadAfterImportAsync();
            await _notifications.RefreshHistoryAsync(CancellationToken.None);
            DataStatus.Text = $"导入完成。新增设备：{result.AddedDevices}，更新设备：{result.UpdatedDevices}，新增事件：{result.AddedEvents}，跳过重复：{result.SkippedDuplicates}。";
        }
        catch (Exception exception) { DataStatus.Text = $"导入失败，原有数据没有变化：{exception.Message}"; }
        finally { if (wasRunning) await _main.ResumeAsync(); }
    }

    private void OpenDataDirectoryClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.RootDirectory) { UseShellExecute = true });
    }

    private void PollingIntervalPreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(value => !char.IsDigit(value));

    private async void ApplyPollingIntervalClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollingIntervalBox.Text, out var seconds) || seconds is < 5 or > 300)
        {
            PollingStatus.Foreground = FindBrush("DangerBrush", System.Windows.Media.Brushes.Firebrick);
            PollingStatus.Text = "请输入 5 到 300 之间的秒数。";
            return;
        }
        try
        {
            await _main.SavePollingIntervalAsync(seconds);
            PollingStatus.Foreground = FindBrush("SuccessBrush", System.Windows.Media.Brushes.ForestGreen);
            PollingStatus.Text = $"已更新为 {seconds} 秒，设置已生效。";
        }
        catch (Exception exception)
        {
            PollingStatus.Foreground = FindBrush("DangerBrush", System.Windows.Media.Brushes.Firebrick);
            PollingStatus.Text = $"设置未保存：{exception.Message}";
        }
    }

    private static System.Windows.Media.Brush FindBrush(string key, System.Windows.Media.Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
}
