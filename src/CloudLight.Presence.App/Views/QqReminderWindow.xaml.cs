using System.Windows;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.App.Views;

public partial class QqReminderWindow : Window
{
    private readonly NotificationSettingsViewModel _notifications;

    public QqReminderWindow(NotificationSettingsViewModel notifications)
    {
        InitializeComponent();
        _notifications = notifications;
        DataContext = notifications;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _notifications.LoadAsync(CancellationToken.None);
            ApplyConnectionAlertSettings();
        }
        catch (Exception exception)
        {
            _notifications.OperationStatus = $"通知设置读取失败：{exception.Message}";
        }
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
        var type = (ConnectionAlertTargetTypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "Group"
            ? NotificationTargetType.Group
            : NotificationTargetType.Private;
        try
        {
            await _notifications.SaveConnectionAlertSettingsAsync(
                new(ConnectionAlertEnabledBox.IsChecked == true, ConnectionAlertRecoveryBox.IsChecked == true,
                    ConnectionAlertUseDefaultBox.IsChecked == true, type, ConnectionAlertTargetIdBox.Text),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _notifications.OperationStatus = $"连接异常提醒设置未保存：{exception.Message}";
        }
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
}
