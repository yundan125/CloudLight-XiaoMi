using System.Windows;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Win32;

namespace CloudLight.Presence.App.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main; private readonly PresenceDataTransferService _transfer; private readonly StartupRegistrationService _startup; private bool _loaded;
    public SettingsWindow(MainViewModel main, PresenceDataTransferService transfer, StartupRegistrationService startup)
    {
        InitializeComponent(); DataContext = main; _main = main; _transfer = transfer; _startup = startup;
        StartWithWindowsBox.IsChecked = main.CurrentSettings.StartWithWindows; StartMinimizedBox.IsChecked = main.CurrentSettings.StartMinimized; _loaded = true;
    }

    private async void GeneralSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        try { var enabled = StartWithWindowsBox.IsChecked == true; _startup.Apply(enabled); await _main.SaveGeneralSettingsAsync(enabled, StartMinimizedBox.IsChecked == true); GeneralStatus.Text = enabled ? "已更新当前程序路径；设置立即生效。" : "已取消开机自启。"; }
        catch (Exception exception) { GeneralStatus.Text = $"设置失败：{exception.Message}"; }
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CloudLight Presence 数据 (*.clpresence)|*.clpresence", DefaultExt = ".clpresence", AddExtension = true, FileName = $"CloudLight-Presence-{DateTime.Now:yyyyMMdd-HHmm}.clpresence" };
        if (dialog.ShowDialog(this) != true) return;
        try { DataStatus.Text = "正在导出…"; await _transfer.ExportAsync(dialog.FileName, CancellationToken.None); DataStatus.Text = $"导出完成：{dialog.FileName}\n备份不包含 Xiaomi 认证信息。"; }
        catch (Exception exception) { DataStatus.Text = $"导出失败：{exception.Message}"; }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "CloudLight Presence 数据 (*.clpresence)|*.clpresence", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var wasRunning = _main.IsMonitoring;
        try
        {
            DataStatus.Text = "正在验证并合并…"; if (wasRunning) await _main.PauseAsync();
            var result = await _transfer.ImportAsync(dialog.FileName, CancellationToken.None); await _main.ReloadAfterImportAsync();
            DataStatus.Text = $"导入完成。新增设备：{result.AddedDevices}，更新设备：{result.UpdatedDevices}，新增事件：{result.AddedEvents}，跳过重复：{result.SkippedDuplicates}。";
        }
        catch (Exception exception) { DataStatus.Text = $"导入失败，数据库未提交：{exception.Message}"; }
        finally { if (wasRunning) await _main.ResumeAsync(); }
    }
}
