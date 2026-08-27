using System.Windows;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace CloudLight.Presence.App.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main; private readonly NotificationSettingsViewModel _notifications; private readonly PresenceDataTransferService _transfer; private readonly StartupRegistrationService _startup; private readonly AppPaths _paths; private bool _loaded;
    public SettingsWindow(MainViewModel main, NotificationSettingsViewModel notifications, PresenceDataTransferService transfer, StartupRegistrationService startup, AppPaths paths)
    {
        InitializeComponent(); DataContext = main; _main = main; _notifications = notifications; _transfer = transfer; _startup = startup; _paths = paths; DataPathText.Text = paths.RootDirectory;
        StartWithWindowsBox.IsChecked = main.CurrentSettings.StartWithWindows; StartMinimizedBox.IsChecked = main.CurrentSettings.StartMinimized; PollingIntervalBox.Text = main.PollingIntervalSeconds.ToString(); _loaded = true;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try { await _notifications.LoadAsync(CancellationToken.None); ApplyConnectionAlertSettings(); }
        catch (Exception exception) { _notifications.OperationStatus = $"通知设置读取失败：{exception.Message}"; }
    }

    private void ApplyConnectionAlertSettings()
    {
        var settings = _notifications.ConnectionAlerts;
        ConnectionAlertEnabledBox.IsChecked = settings.Enabled;
        ConnectionAlertRecoveryBox.IsChecked = settings.RecoveryEnabled;
        ConnectionAlertUseDefaultBox.IsChecked = settings.UseDefaultTarget;
        ConnectionAlertTargetTypeBox.SelectedIndex = settings.TargetType == NotificationTargetType.Group ? 1 : 0;
        ConnectionAlertTargetIdBox.Text = settings.TargetId;
        UpdateConnectionAlertTargetFields();
    }

    private void ConnectionAlertTargetModeChanged(object sender, RoutedEventArgs e) => UpdateConnectionAlertTargetFields();

    private void ConnectionAlertTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateConnectionAlertTargetFields();

    private void UpdateConnectionAlertTargetFields()
    {
        var useDefault = ConnectionAlertUseDefaultBox.IsChecked == true;
        ConnectionAlertTargetGrid.Visibility = useDefault ? Visibility.Collapsed : Visibility.Visible;
        var group = (ConnectionAlertTargetTypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "Group";
        ConnectionAlertTargetLabel.Text = group ? "群聊 OpenID" : "用户 OpenID";
        ConnectionAlertTargetHint.Text = useDefault
            ? "将跟随“配置 QQ”中的默认接收目标；未设置默认 OpenID 时不会发送系统提醒。"
            : "这里填写 QQ 官方开放平台提供的用户或群聊 OpenID，不是普通 QQ 号。";
    }

    private async void SaveConnectionAlertClicked(object sender, RoutedEventArgs e)
    {
        var type = (ConnectionAlertTargetTypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "Group" ? NotificationTargetType.Group : NotificationTargetType.Private;
        try { await _notifications.SaveConnectionAlertSettingsAsync(new(ConnectionAlertEnabledBox.IsChecked == true, ConnectionAlertRecoveryBox.IsChecked == true, ConnectionAlertUseDefaultBox.IsChecked == true, type, ConnectionAlertTargetIdBox.Text), CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"连接异常提醒设置未保存：{exception.Message}"; }
    }

    private async void ConfigureQqClicked(object sender, RoutedEventArgs e)
    {
        var draft = QqConfigurationDialog.Show(this, _notifications.QqSettings, _notifications.QqSecretConfigured);
        if (draft is null) return;
        try { await _notifications.SaveQqConfigurationAsync(draft.Settings, draft.AppSecret, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"QQ 设置未保存：{exception.Message}"; }
    }

    private async void SendTestMessageClicked(object sender, RoutedEventArgs e)
    {
        var draft = QqTestDialog.Show(this);
        if (draft is null) return;
        try { await _notifications.SendTestMessageAsync(draft.TargetType, draft.TargetId, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"测试消息发送失败：{exception.Message}"; }
    }

    private async void AddRuleClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var subjects = await _notifications.GetSubjectsAsync(CancellationToken.None);
            var rule = NotificationRuleDialog.Show(this, subjects, null);
            if (rule is not null) await _notifications.SaveRuleAsync(rule, CancellationToken.None);
        }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒保存失败：{exception.Message}"; }
    }

    private async void EditRuleClicked(object sender, RoutedEventArgs e)
    {
        if (GetRuleItem(sender) is not { } item) return;
        try
        {
            var subjects = await _notifications.GetSubjectsAsync(CancellationToken.None);
            var rule = NotificationRuleDialog.Show(this, subjects, item.Rule);
            if (rule is not null) await _notifications.SaveRuleAsync(rule, CancellationToken.None);
        }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒保存失败：{exception.Message}"; }
    }

    private async void ToggleRuleClicked(object sender, RoutedEventArgs e)
    {
        if (GetRuleItem(sender) is not { } item) return;
        try { if (item.Rule.Enabled) await _notifications.DisableRuleAsync(item.Rule.Id, CancellationToken.None); else await _notifications.EnableRuleAsync(item.Rule.Id, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒状态未更新：{exception.Message}"; }
    }

    private async void DeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (GetRuleItem(sender) is not { } item) return;
        if (System.Windows.MessageBox.Show(this, "删除这条自动提醒？\n\n删除后不会影响设备、在线记录或其他提醒。", "删除自动提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _notifications.DeleteRuleAsync(item.Rule.Id, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒删除失败：{exception.Message}"; }
    }

    private static NotificationRuleItemViewModel? GetRuleItem(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        return (element as System.Windows.Controls.Button)?.CommandParameter as NotificationRuleItemViewModel
            ?? element.DataContext as NotificationRuleItemViewModel;
    }

    private async void GeneralSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        try { var enabled = StartWithWindowsBox.IsChecked == true; _startup.Apply(enabled); await _main.SaveGeneralSettingsAsync(enabled, StartMinimizedBox.IsChecked == true); GeneralStatus.Text = enabled ? "开机自动启动已开启。" : "开机自动启动已关闭。"; }
        catch (Exception exception) { GeneralStatus.Text = $"设置失败：{exception.Message}"; }
    }

    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.ExportsDirectory);
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出数据 - CloudLight XiaoMi", Filter = "CloudLight XiaoMi 数据 (*.clpresence)|*.clpresence", DefaultExt = ".clpresence", AddExtension = true, InitialDirectory = _paths.ExportsDirectory, FileName = $"CloudLight-XiaoMi-{DateTime.Now:yyyyMMdd-HHmm}.clpresence" };
        if (dialog.ShowDialog(this) != true) return;
        try { DataStatus.Text = "正在导出…"; await _transfer.ExportAsync(dialog.FileName, CancellationToken.None); DataStatus.Text = $"导出完成：{dialog.FileName}\n导出文件不包含 Xiaomi 登录信息或 QQ AppSecret；QQ 目标 OpenID 属于提醒规则内容。"; }
        catch (Exception exception) { DataStatus.Text = $"导出失败：{exception.Message}"; }
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入数据 - CloudLight XiaoMi", Filter = "CloudLight XiaoMi 数据 (*.clpresence)|*.clpresence", CheckFileExists = true, InitialDirectory = Directory.Exists(_paths.ExportsDirectory) ? _paths.ExportsDirectory : _paths.RootDirectory };
        if (dialog.ShowDialog(this) != true) return;
        var wasRunning = _main.IsMonitoring;
        try
        {
            DataStatus.Text = "正在验证并合并…"; if (wasRunning) await _main.PauseAsync();
            var result = await _transfer.ImportAsync(dialog.FileName, CancellationToken.None); await _main.ReloadAfterImportAsync(); await _notifications.RefreshHistoryAsync(CancellationToken.None);
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
            PollingStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            PollingStatus.Text = "请输入 5 到 300 之间的秒数。";
            return;
        }
        try
        {
            await _main.SavePollingIntervalAsync(seconds);
            PollingStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            PollingStatus.Text = $"已更新为 {seconds} 秒，设置已生效。";
        }
        catch (Exception exception)
        {
            PollingStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            PollingStatus.Text = $"设置未保存：{exception.Message}";
        }
    }
}
