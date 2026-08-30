using System.Windows;
using System.Windows.Controls;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Models;
using Button = System.Windows.Controls.Button;

namespace CloudLight.Presence.App.Views;

public partial class QqReminderWindow : System.Windows.Controls.UserControl
{
    private readonly NotificationSettingsViewModel _notifications;

    public QqReminderWindow(NotificationSettingsViewModel notifications)
    {
        InitializeComponent();
        _notifications = notifications;
        DataContext = notifications;
    }

    private Window? OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current?.MainWindow;

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
        ConnectionAlertRecipientsList.SelectedItems.Clear();
        foreach (var item in _notifications.Recipients.Where(value => settings.RecipientIds.Contains(value.Id)))
            ConnectionAlertRecipientsList.SelectedItems.Add(item);
        UpdateConnectionAlertTargetFields();
    }

    private void ConnectionAlertTargetModeChanged(object sender, RoutedEventArgs e) => UpdateConnectionAlertTargetFields();

    private void UpdateConnectionAlertTargetFields() =>
        ConnectionAlertRecipientsList.IsEnabled = ConnectionAlertUseDefaultBox.IsChecked != true;

    private async void SaveConnectionAlertClicked(object sender, RoutedEventArgs e)
    {
        var selected = ConnectionAlertRecipientsList.SelectedItems.OfType<NotificationRecipientItemViewModel>().ToArray();
        var first = selected.FirstOrDefault()?.Recipient;
        var current = _notifications.ConnectionAlerts;
        try
        {
            await _notifications.SaveConnectionAlertSettingsAsync(
                new ConnectionAlertSettings(
                    ConnectionAlertEnabledBox.IsChecked == true,
                    ConnectionAlertRecoveryBox.IsChecked == true,
                    ConnectionAlertUseDefaultBox.IsChecked == true,
                    first?.TargetType ?? current.TargetType,
                    first?.OpenId ?? current.TargetId)
                {
                    RecipientIds = selected.Select(value => value.Id).ToArray()
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _notifications.OperationStatus = $"连接异常提醒设置未保存：{exception.Message}";
        }
    }

    private async void ConfigureQqClicked(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) return;
        var draft = QqConfigurationDialog.Show(owner, _notifications.QqSettings, _notifications.QqSecretConfigured, _notifications.Recipients.ToArray());
        if (draft is null) return;
        try { await _notifications.SaveQqConfigurationAsync(draft.Settings, draft.AppSecret, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"QQ 设置未保存：{exception.Message}"; }
    }

    private async void SendTestMessageClicked(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) return;
        var draft = QqTestDialog.Show(owner, _notifications.Recipients.ToArray());
        if (draft is null) return;
        try
        {
            if (draft.RecipientId is { } recipientId)
                await _notifications.SendTestMessageAsync(recipientId, CancellationToken.None);
            else
                await _notifications.SendTestMessageAsync(draft.TargetType, draft.TargetId, CancellationToken.None);
        }
        catch (Exception exception) { _notifications.OperationStatus = $"测试消息发送失败：{exception.Message}"; }
    }

    private async void AddRecipientClicked(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) return;
        var draft = NotificationRecipientDialog.Show(owner);
        if (draft is null) return;
        try { await _notifications.SaveRecipientAsync(draft, null, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"接收人保存失败：{exception.Message}"; }
    }

    private async void EditRecipientClicked(object sender, RoutedEventArgs e)
    {
        if (GetRecipientItem(sender) is not { } item || OwnerWindow is not { } owner) return;
        var draft = NotificationRecipientDialog.Show(owner, item.Recipient);
        if (draft is null) return;
        try { await _notifications.SaveRecipientAsync(draft, item.Id, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"接收人保存失败：{exception.Message}"; }
    }

    private async void DeleteRecipientClicked(object sender, RoutedEventArgs e)
    {
        if (GetRecipientItem(sender) is not { } item || OwnerWindow is not { } owner) return;
        try
        {
            var usage = await _notifications.GetRecipientUsageCountAsync(item.Id, CancellationToken.None);
            var prompt = usage > 0
                ? $"“{item.Note}”正被 {usage} 条自动提醒使用，不能直接删除。\n\n请先在提醒编辑页解除关联。"
                : $"删除接收人“{item.Note}”？\n\n之后将不能从提醒和测试消息中选择它。";
            if (usage > 0)
            {
                CloudLightDialogs.Info(owner, "联系人正在使用", prompt);
                return;
            }
            if (!CloudLightDialogs.Confirm(owner, "删除 QQ 接收人", prompt, danger: true, accept: "删除")) return;
            await _notifications.DeleteRecipientAsync(item.Id, CancellationToken.None);
        }
        catch (Exception exception) { _notifications.OperationStatus = $"接收人删除失败：{exception.Message}"; }
    }

    private async void TestRecipientClicked(object sender, RoutedEventArgs e)
    {
        if (GetRecipientItem(sender) is not { } item) return;
        try { await _notifications.SendTestMessageAsync(item.Id, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"测试消息发送失败：{exception.Message}"; }
    }

    private async void AddRuleClicked(object sender, RoutedEventArgs e)
    {
        if (OwnerWindow is not { } owner) return;
        try
        {
            var subjects = await _notifications.GetNotificationSubjectOptionsAsync(CancellationToken.None);
            var rule = NotificationRuleDialog.Show(owner, subjects, _notifications.Recipients.ToArray(), null, CreateRecipientFromDialog);
            if (rule is not null) await _notifications.SaveRuleAsync(rule, CancellationToken.None);
        }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒保存失败：{exception.Message}"; }
    }

    private async void EditRuleClicked(object sender, RoutedEventArgs e)
    {
        if (GetRuleItem(sender) is not { } item || OwnerWindow is not { } owner) return;
        try
        {
            var subjects = await _notifications.GetNotificationSubjectOptionsAsync(CancellationToken.None);
            var rule = NotificationRuleDialog.Show(owner, subjects, _notifications.Recipients.ToArray(), item.Rule, CreateRecipientFromDialog);
            if (rule is not null) await _notifications.SaveRuleAsync(rule, CancellationToken.None);
        }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒保存失败：{exception.Message}"; }
    }

    private NotificationRecipientItemViewModel? CreateRecipientFromDialog(NotificationRecipientDraft _)
    {
        if (OwnerWindow is not { } owner) return null;
        var draft = NotificationRecipientDialog.Show(owner);
        return draft is null ? null : _notifications.SaveRecipientFromDialog(draft);
    }

    private async void ToggleRuleClicked(object sender, RoutedEventArgs e)
    {
        if (GetRuleItem(sender) is not { } item) return;
        try { await _notifications.ToggleRuleAsync(item, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒状态未更新：{exception.Message}"; }
    }

    private async void DeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (GetRuleItem(sender) is not { } item || OwnerWindow is not { } owner) return;
        if (!CloudLightDialogs.Confirm(owner, "删除自动提醒", "删除这条自动提醒？\n\n删除后不会影响设备、在线记录或其他提醒。", danger: true, accept: "删除")) return;
        try { await _notifications.DeleteRuleAsync(item.Rule.Id, CancellationToken.None); }
        catch (Exception exception) { _notifications.OperationStatus = $"提醒删除失败：{exception.Message}"; }
    }

    private static NotificationRuleItemViewModel? GetRuleItem(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        return (element as Button)?.CommandParameter as NotificationRuleItemViewModel
               ?? element.DataContext as NotificationRuleItemViewModel;
    }

    private static NotificationRecipientItemViewModel? GetRecipientItem(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        return (element as Button)?.CommandParameter as NotificationRecipientItemViewModel
               ?? element.DataContext as NotificationRecipientItemViewModel;
    }
}
