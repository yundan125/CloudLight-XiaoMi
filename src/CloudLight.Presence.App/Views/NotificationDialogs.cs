using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CloudLight.Presence.App.Behaviors;
using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Settings;

using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;

namespace CloudLight.Presence.App.Views;

public sealed record QqConfigurationDraft(QqNotificationSettings Settings, string? AppSecret);
public sealed record QqTestDraft(NotificationTargetType TargetType, string TargetId, long? RecipientId = null);

public static class QqConfigurationDialog
{
    public static QqConfigurationDraft? Show(Window owner, QqNotificationSettings settings, bool secretConfigured) =>
        Show(owner, settings, secretConfigured, []);

    public static QqConfigurationDraft? Show(
        Window owner,
        QqNotificationSettings settings,
        bool secretConfigured,
        IReadOnlyList<NotificationRecipientItemViewModel> recipients)
    {
        var window = DialogUi.CreateWindow(owner, "配置 QQ", 640);
        var panel = DialogUi.Panel();
        panel.Children.Add(DialogUi.Title("配置 QQ"));
        panel.Children.Add(DialogUi.Subtitle("连接 QQ Bot，管理自动提醒的默认接收人和网络方式。"));

        var enabled = new CheckBox { Content = "启用 QQ 自动提醒", IsChecked = settings.Enabled, Margin = new Thickness(0, 18, 0, 18) };
        DialogUi.UseStyle(enabled, "ToggleSwitchStyle");
        var appId = new TextBox { Text = settings.AppId, MaxLength = 32 };
        var appSecret = new PasswordBox { MaxLength = 256 };
        var secretHint = DialogUi.Hint(secretConfigured ? "留空表示继续使用已保存的密钥。" : "密钥只保存在当前 Windows 用户的 DPAPI 加密文件中。");

        var botPanel = new StackPanel();
        botPanel.Children.Add(enabled);
        botPanel.Children.Add(DialogUi.Field("AppID", appId, "来自 QQ 开放平台的数字应用 ID。"));
        botPanel.Children.Add(DialogUi.Field("AppSecret", new StackPanel { Children = { appSecret, secretHint } }));
        panel.Children.Add(DialogUi.Card(botPanel));

        var autoConnect = new CheckBox { Content = "应用启动后自动连接", IsChecked = settings.AutoConnect, Margin = new Thickness(0, 0, 0, 14) };
        var reconnect = new CheckBox { Content = "连接断开后自动重连", IsChecked = settings.GatewayReconnectEnabled };
        DialogUi.UseStyle(autoConnect, "ToggleSwitchStyle");
        DialogUi.UseStyle(reconnect, "ToggleSwitchStyle");
        var connectionPanel = new StackPanel();
        connectionPanel.Children.Add(autoConnect);
        connectionPanel.Children.Add(reconnect);
        panel.Children.Add(DialogUi.Card(connectionPanel));

        var recipientOptions = recipients.Select(value => new RecipientOption(value)).ToArray();
        var defaultRecipients = new ListBox
        {
            MaxHeight = 220,
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = recipientOptions,
            DisplayMemberPath = nameof(RecipientOption.DisplayText),
            ToolTip = "可多选默认接收人；OpenID 仅以脱敏形式显示。"
        };
        defaultRecipients.SetValue(ScrollViewer.CanContentScrollProperty, false);
        defaultRecipients.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        NestedScrollBehavior.SetBubbleMouseWheelAtBoundary(defaultRecipients, true);
        foreach (var option in recipientOptions.Where(value => settings.DefaultRecipientIds.Contains(value.Item.Id)))
            defaultRecipients.SelectedItems.Add(option);
        var noRecipients = DialogUi.EmptyState("还没有 QQ 接收人", "先在 QQ 提醒页面保存常用 OpenID，之后就能在这里设为默认接收人。");
        noRecipients.Visibility = recipientOptions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        defaultRecipients.Visibility = recipientOptions.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var defaultPanel = new StackPanel();
        defaultPanel.Children.Add(DialogUi.Section("默认接收人"));
        defaultPanel.Children.Add(DialogUi.Hint("连接异常提醒和需要默认目标的通知会使用这里选中的联系人。"));
        defaultPanel.Children.Add(defaultRecipients);
        defaultPanel.Children.Add(noRecipients);
        panel.Children.Add(DialogUi.Card(defaultPanel));

        var proxyMode = new ComboBox();
        AddItem(proxyMode, "跟随系统代理", "environment");
        AddItem(proxyMode, "直连", "direct");
        AddItem(proxyMode, "自定义 HTTP 代理", "custom-http");
        proxyMode.SelectedIndex = settings.ProxyMode == "direct" ? 1 : settings.ProxyMode == "custom-http" ? 2 : 0;
        var proxyUrl = new TextBox { Text = settings.ProxyUrl, ToolTip = "例如 http://127.0.0.1:7897" };
        var proxyUrlField = DialogUi.Field("代理地址", proxyUrl, "仅在选择自定义 HTTP 代理时使用。\n不会修改系统代理设置。");
        void UpdateProxyVisibility()
        {
            proxyUrlField.Visibility = (proxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom-http"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        proxyMode.SelectionChanged += (_, _) => UpdateProxyVisibility();
        UpdateProxyVisibility();
        var advancedPanel = new StackPanel();
        advancedPanel.Children.Add(DialogUi.Section("高级"));
        advancedPanel.Children.Add(DialogUi.Field("网络代理", proxyMode));
        advancedPanel.Children.Add(proxyUrlField);
        panel.Children.Add(DialogUi.Card(advancedPanel));

        var error = DialogUi.Error();
        panel.Children.Add(error);

        QqConfigurationDraft? result = null;
        panel.Children.Add(DialogUi.Actions(window, "保存", validate: () =>
        {
            var mode = (proxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "environment";
            if (enabled.IsChecked == true && (appId.Text.Trim().Length < 5 || appId.Text.Trim().Any(value => value is < '0' or > '9')))
            {
                error.Text = "启用 QQ 时请输入有效的数字 AppID。";
                return false;
            }
            if (mode == "custom-http" && (!Uri.TryCreate(proxyUrl.Text.Trim(), UriKind.Absolute, out var proxy) || proxy.Scheme != Uri.UriSchemeHttp))
            {
                error.Text = "自定义代理必须是 HTTP 地址，例如 http://127.0.0.1:7897。";
                return false;
            }

            var selected = defaultRecipients.SelectedItems.OfType<RecipientOption>().Select(value => value.Item).ToArray();
            var first = selected.FirstOrDefault()?.Recipient;
            var defaultType = first?.TargetType ?? settings.DefaultTargetType;
            var defaultId = first?.OpenId ?? settings.DefaultTargetId;
            if (defaultId.Any(char.IsWhiteSpace))
            {
                error.Text = "默认 OpenID 不能包含空格。";
                return false;
            }

            result = new(
                new QqNotificationSettings(
                    enabled.IsChecked == true,
                    autoConnect.IsChecked == true,
                    appId.Text.Trim(),
                    reconnect.IsChecked == true,
                    mode,
                    mode == "custom-http" ? proxyUrl.Text.Trim() : "",
                    defaultType,
                    defaultId)
                {
                    DefaultRecipientIds = selected.Select(value => value.Id).ToArray()
                },
                string.IsNullOrWhiteSpace(appSecret.Password) ? null : appSecret.Password);
            return true;
        }));

        window.Content = DialogUi.MainScroll(panel);
        return window.ShowDialog() == true ? result : null;
    }

    private static void AddItem(ComboBox box, string text, object tag) => box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
}

public static class QqTestDialog
{
    public static QqTestDraft? Show(Window owner) => Show(owner, [], null);

    public static QqTestDraft? Show(Window owner, IReadOnlyList<NotificationRecipientItemViewModel> recipients, long? selectedRecipientId = null)
    {
        var window = DialogUi.CreateWindow(owner, "发送测试消息", 520);
        var panel = DialogUi.Panel();
        panel.Children.Add(DialogUi.Title("发送测试消息"));
        panel.Children.Add(DialogUi.Subtitle("选择一个接收人，确认 QQ Bot 可以正常发送通知。"));

        var options = recipients.Select(value => new RecipientOption(value)).ToArray();
        var recipient = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(RecipientOption.DisplayText) };
        recipient.SelectedItem = options.FirstOrDefault(value => value.Item.Id == selectedRecipientId) ?? options.FirstOrDefault();
        var recipientCard = new StackPanel();
        recipientCard.Children.Add(DialogUi.Section("消息目标"));
        if (options.Length > 0)
            recipientCard.Children.Add(DialogUi.Field("接收人", recipient));

        var type = new ComboBox { IsEnabled = options.Length == 0 };
        AddItem(type, "私聊", NotificationTargetType.Private);
        AddItem(type, "群聊", NotificationTargetType.Group);
        type.SelectedIndex = 0;
        var target = new TextBox { IsEnabled = options.Length == 0, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        if (options.Length == 0)
        {
            recipientCard.Children.Add(DialogUi.Field("类型", type));
            recipientCard.Children.Add(DialogUi.Field("OpenID", target, "还没有保存联系人；可以直接填写 QQ 开放平台提供的 OpenID。"));
        }
        else
        {
            recipientCard.Children.Add(DialogUi.Hint("发送目标只显示已保存联系人的备注和脱敏 OpenID。"));
        }
        panel.Children.Add(DialogUi.Card(recipientCard));

        var error = DialogUi.Error();
        panel.Children.Add(error);
        QqTestDraft? result = null;
        panel.Children.Add(DialogUi.Actions(window, "发送", validate: () =>
        {
            if (recipient.SelectedItem is RecipientOption selected)
            {
                result = new(selected.Item.Recipient.TargetType, selected.Item.Recipient.OpenId, selected.Item.Id);
                return true;
            }
            if (string.IsNullOrWhiteSpace(target.Text) || target.Text.Any(char.IsWhiteSpace))
            {
                error.Text = "请输入有效的 OpenID。";
                return false;
            }
            result = new((type.SelectedItem as ComboBoxItem)?.Tag is NotificationTargetType value ? value : NotificationTargetType.Private, target.Text.Trim());
            return true;
        }));
        window.Content = DialogUi.MainScroll(panel);
        return window.ShowDialog() == true ? result : null;
    }

    private static void AddItem(ComboBox box, string text, object tag) => box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
}

public static class NotificationRecipientDialog
{
    public static NotificationRecipientDraft? Show(Window owner, NotificationRecipient? existing = null)
    {
        var isEditing = existing is not null;
        var window = DialogUi.CreateWindow(owner, isEditing ? "编辑 QQ 接收人" : "添加 QQ 接收人", 540);
        var panel = DialogUi.Panel();
        panel.Children.Add(DialogUi.Title(isEditing ? "编辑 QQ 接收人" : "添加 QQ 接收人"));
        panel.Children.Add(DialogUi.Subtitle("保存常用 QQ 用户或群聊，创建提醒时可以直接选择。"));

        var note = new TextBox { Text = existing?.Note ?? "", ToolTip = "例如：我的 QQ、家庭群" };
        var openId = new TextBox
        {
            Text = existing?.OpenId ?? "",
            MaxLength = 256,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "QQ 官方开放平台提供的 OpenID，不是普通 QQ 号"
        };
        var type = new ComboBox();
        type.Items.Add(new ComboBoxItem { Content = "私聊", Tag = NotificationTargetType.Private });
        type.Items.Add(new ComboBoxItem { Content = "群聊", Tag = NotificationTargetType.Group });
        type.SelectedIndex = existing?.TargetType == NotificationTargetType.Group ? 1 : 0;

        var info = new StackPanel();
        info.Children.Add(DialogUi.Section("接收人信息"));
        info.Children.Add(DialogUi.Field("备注", note, "用容易识别的名称，例如“我的 QQ”或“家庭群”。"));
        info.Children.Add(DialogUi.Field("OpenID", openId, "OpenID 由 QQ 开放平台提供；输入框支持横向滚动，不会撑大窗口。"));
        info.Children.Add(DialogUi.Field("类型", type));
        panel.Children.Add(DialogUi.Card(info));

        var error = DialogUi.Error();
        panel.Children.Add(error);
        NotificationRecipientDraft? result = null;
        panel.Children.Add(DialogUi.Actions(window, "保存", validate: () =>
        {
            if (string.IsNullOrWhiteSpace(note.Text)) { error.Text = "请输入备注。"; return false; }
            if (string.IsNullOrWhiteSpace(openId.Text) || openId.Text.Any(char.IsWhiteSpace)) { error.Text = "请输入不含空格的 OpenID。"; return false; }
            var selectedType = (type.SelectedItem as ComboBoxItem)?.Tag is NotificationTargetType value ? value : NotificationTargetType.Private;
            result = new(note.Text.Trim(), openId.Text.Trim(), selectedType);
            return true;
        }));
        window.Content = DialogUi.MainScroll(panel);
        return window.ShowDialog() == true ? result : null;
    }
}

public static class NotificationRuleDialog
{
    private const long MaxSeconds = 365L * 24 * 60 * 60;

    public static NotificationRule? Show(Window owner, IReadOnlyList<NotificationSubjectOption> subjects, NotificationRule? existing) =>
        Show(owner, subjects, [], existing, null);

    public static NotificationRule? Show(
        Window owner,
        IReadOnlyList<NotificationSubjectOption> subjects,
        IReadOnlyList<NotificationRecipientItemViewModel> recipients,
        NotificationRule? existing,
        Func<NotificationRecipientDraft, NotificationRecipientItemViewModel?>? createRecipient)
    {
        if (subjects.Count == 0) return null;
        var window = DialogUi.CreateWindow(owner, existing is null ? "添加自动提醒" : "编辑自动提醒", 700);
        var panel = DialogUi.Panel();
        panel.Children.Add(DialogUi.Title(existing is null ? "添加自动提醒" : "编辑自动提醒"));
        panel.Children.Add(DialogUi.Subtitle("按主体和条件触发通知，并为同一条提醒选择一个或多个 QQ 接收人。"));

        var subject = new ComboBox { ItemsSource = subjects, DisplayMemberPath = nameof(NotificationSubjectOption.Label) };
        subject.SelectedItem = subjects.FirstOrDefault(value => value.Id == existing?.SubjectId) ?? subjects[0];
        var subjectPanel = new StackPanel();
        subjectPanel.Children.Add(DialogUi.Section("提醒对象"));
        subjectPanel.Children.Add(DialogUi.Field("用户 / 设备", subject));
        panel.Children.Add(DialogUi.Card(subjectPanel));

        var condition = new ComboBox();
        AddItem(condition, "连续在线", NotificationCondition.OnlineFor);
        AddItem(condition, "连续离线", NotificationCondition.OfflineFor);
        AddItem(condition, "检测到上线", NotificationCondition.DetectedOnline);
        AddItem(condition, "检测到离线", NotificationCondition.DetectedOffline);
        condition.SelectedItem = condition.Items.OfType<ComboBoxItem>().First(value => Equals(value.Tag, existing?.Condition ?? NotificationCondition.OnlineFor));
        var amount = new TextBox { Width = 96, TextAlignment = TextAlignment.Right, Text = existing is null ? "1" : ToAmount(existing.ThresholdSeconds).ToString() };
        DialogUi.UseStyle(amount, "NumericInputStyle");
        amount.PreviewTextInput += DigitsOnly;
        var unit = new ComboBox { Width = 124, Margin = new Thickness(10, 0, 0, 0) };
        AddItem(unit, "分钟", 60L);
        AddItem(unit, "小时", 3600L);
        AddItem(unit, "天", 86400L);
        unit.SelectedIndex = existing is null ? 0 : ToUnitIndex(existing.ThresholdSeconds);
        var duration = new StackPanel { Orientation = Orientation.Horizontal };
        duration.Children.Add(amount);
        duration.Children.Add(unit);
        var durationField = DialogUi.Field("持续时间", duration, "事件型条件不需要持续时间。");
        var conditionPanel = new StackPanel();
        conditionPanel.Children.Add(DialogUi.Section("触发条件"));
        conditionPanel.Children.Add(DialogUi.Field("条件", condition));
        conditionPanel.Children.Add(durationField);
        panel.Children.Add(DialogUi.Card(conditionPanel));

        var recipientSource = new ObservableCollection<RecipientOption>(recipients.Select(value => new RecipientOption(value)));
        var recipientView = new ListCollectionView(recipientSource);
        var recipientSearch = new TextBox { Style = Style("SearchInputStyle"), Tag = "搜索接收人", ToolTip = "按备注或 OpenID 搜索" };
        recipientView.Filter = value => value is RecipientOption option &&
            (string.IsNullOrWhiteSpace(recipientSearch.Text)
             || option.Item.Note.Contains(recipientSearch.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)
             || option.Item.OpenId.Contains(recipientSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        recipientSearch.TextChanged += (_, _) => recipientView.Refresh();
        var recipientList = new ListBox
        {
            MaxHeight = 240,
            ItemsSource = recipientView,
            SelectionMode = SelectionMode.Multiple,
            DisplayMemberPath = nameof(RecipientOption.DisplayText),
            ToolTip = "可多选接收人"
        };
        recipientList.SetValue(ScrollViewer.CanContentScrollProperty, false);
        recipientList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        NestedScrollBehavior.SetBubbleMouseWheelAtBoundary(recipientList, true);
        var selectedIds = existing?.RecipientIds.ToHashSet() ?? [];
        if (selectedIds.Count == 0 && existing is not null)
            selectedIds = recipients.Where(value => value.Recipient.TargetType == existing.TargetType && value.OpenId == existing.TargetId).Select(value => value.Id).ToHashSet();
        foreach (var option in recipientSource.Where(value => selectedIds.Contains(value.Item.Id))) recipientList.SelectedItems.Add(option);
        var addRecipient = DialogUi.Button("＋ 添加接收人", "SecondaryButtonStyle");
        var noRecipients = DialogUi.EmptyState("还没有 QQ 接收人", "保存常用 OpenID 后，添加提醒时可以直接多选。");
        void UpdateRecipientVisibility()
        {
            var hasRecipients = recipientSource.Count > 0;
            recipientSearch.Visibility = hasRecipients ? Visibility.Visible : Visibility.Collapsed;
            recipientList.Visibility = hasRecipients ? Visibility.Visible : Visibility.Collapsed;
            noRecipients.Visibility = hasRecipients ? Visibility.Collapsed : Visibility.Visible;
        }
        addRecipient.Click += (_, _) =>
        {
            if (createRecipient?.Invoke(new NotificationRecipientDraft("新接收人", "", NotificationTargetType.Private)) is not { } item) return;
            var option = new RecipientOption(item);
            recipientSource.Add(option);
            recipientList.SelectedItems.Add(option);
            UpdateRecipientVisibility();
        };
        UpdateRecipientVisibility();
        var recipientPanel = new StackPanel();
        recipientPanel.Children.Add(DialogUi.Section("QQ 接收人"));
        recipientPanel.Children.Add(DialogUi.Hint("可搜索并多选联系人；每个接收人会独立记录发送状态。"));
        recipientPanel.Children.Add(recipientSearch);
        recipientPanel.Children.Add(recipientList);
        recipientPanel.Children.Add(noRecipients);
        recipientPanel.Children.Add(addRecipient);
        panel.Children.Add(DialogUi.Card(recipientPanel));

        var conditionDefault = DefaultTemplate(condition);
        var template = new TextBox { Text = existing?.MessageTemplate ?? conditionDefault, MinHeight = 112, MaxHeight = 220, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var templateEdited = existing is not null && !string.Equals(existing.MessageTemplate, conditionDefault, StringComparison.Ordinal);
        var lastDefault = conditionDefault;
        template.TextChanged += (_, _) => { if (template.IsKeyboardFocusWithin) templateEdited = true; };
        condition.SelectionChanged += (_, _) =>
        {
            var current = DefaultTemplate(condition);
            if (!templateEdited && (template.Text == lastDefault || string.IsNullOrWhiteSpace(template.Text))) template.Text = current;
            lastDefault = current;
            durationField.Visibility = IsDurationCondition(condition) ? Visibility.Visible : Visibility.Collapsed;
        };
        durationField.Visibility = IsDurationCondition(condition) ? Visibility.Visible : Visibility.Collapsed;
        var messagePanel = new StackPanel();
        messagePanel.Children.Add(DialogUi.Section("通知内容"));
        messagePanel.Children.Add(DialogUi.Field("消息模板", template, "留空会使用当前条件的默认模板。大括号中的变量会在发送时替换成真实值。"));
        messagePanel.Children.Add(ParameterHelp());
        panel.Children.Add(DialogUi.Card(messagePanel));

        var error = DialogUi.Error();
        panel.Children.Add(error);
        NotificationRule? result = null;
        panel.Children.Add(DialogUi.Actions(window, "保存提醒", validate: () =>
        {
            if (subject.SelectedItem is not NotificationSubjectOption selectedSubject) { error.Text = "请选择提醒主体。"; return false; }
            var selectedCondition = (condition.SelectedItem as ComboBoxItem)?.Tag is NotificationCondition conditionValue ? conditionValue : NotificationCondition.OnlineFor;
            var seconds = 0L;
            if (IsDurationCondition(selectedCondition))
            {
                if (!long.TryParse(amount.Text.Trim(), out var number) || number <= 0) { error.Text = "持续时间必须是正整数。"; return false; }
                if (unit.SelectedItem is not ComboBoxItem { Tag: long multiplier }) { error.Text = "请选择时间单位。"; return false; }
                try { seconds = checked(number * multiplier); } catch (OverflowException) { error.Text = "持续时间过大。"; return false; }
                if (seconds is < 60 or > MaxSeconds) { error.Text = "持续时间必须在 1 分钟到 365 天之间。"; return false; }
            }
            var selectedRecipients = recipientList.SelectedItems.OfType<RecipientOption>().Select(value => value.Item).ToArray();
            if (selectedRecipients.Length == 0) { error.Text = "请至少选择一个 QQ 接收人。"; return false; }
            var first = selectedRecipients[0].Recipient;
            var now = DateTimeOffset.UtcNow;
            result = new NotificationRule(existing?.Id ?? 0, selectedSubject.Subject.Id, existing?.Enabled ?? true, selectedCondition, seconds, NotificationChannelType.QQ, first.TargetType, first.OpenId, template.Text.Trim(), existing?.CreatedAt ?? now, now)
            {
                RecipientIds = selectedRecipients.Select(value => value.Id).ToArray()
            };
            return true;
        }));

        window.Content = DialogUi.MainScroll(panel);
        return window.ShowDialog() == true ? result : null;
    }

    private static string DefaultTemplate(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is NotificationCondition condition ? NotificationTemplateRenderer.DefaultTemplate(condition) : NotificationTemplateRenderer.DefaultTemplate(NotificationCondition.OnlineFor);
    private static bool IsDurationCondition(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is NotificationCondition condition && IsDurationCondition(condition);
    private static bool IsDurationCondition(NotificationCondition condition) => condition is NotificationCondition.OnlineFor or NotificationCondition.OfflineFor;
    private static long ToAmount(long seconds) => seconds % 86400 == 0 ? seconds / 86400 : seconds % 3600 == 0 ? seconds / 3600 : seconds / 60;
    private static int ToUnitIndex(long seconds) => seconds % 86400 == 0 ? 2 : seconds % 3600 == 0 ? 1 : 0;
    private static void DigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(value => !char.IsDigit(value));
    private static void AddItem(ComboBox box, string text, object tag) => box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });

    private static Border ParameterHelp()
    {
        var panel = new StackPanel();
        panel.Children.Add(DialogUi.Section("可用变量"));
        panel.Children.Add(DialogUi.Hint("可复制到消息模板中，发送时会替换为当前值。"));
        foreach (var item in new[]
        {
            ("{name}", "用户或设备名称"),
            ("{state}", "当前状态，例如在线 / 离线"),
            ("{duration}", "本次连续状态持续时间"),
            ("{stateSince}", "本次状态开始时间"),
            ("{lastOnlineTime}", "最近一次确认在线的时间"),
            ("{lastOfflineTime}", "最近一次确认离线的时间"),
            ("{currentTime}", "规则评估时间"),
            ("{detectedTime}", "主体事件被确认的时间"),
            ("{routerName}", "当前监控的路由器名称")
        })
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(142) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var name = new TextBlock { Text = item.Item1, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = Brush("TextBrush") };
            var meaning = DialogUi.Hint(item.Item2);
            meaning.Margin = new Thickness(0);
            Grid.SetColumn(meaning, 1);
            row.Children.Add(name);
            row.Children.Add(meaning);
            panel.Children.Add(row);
        }
        return DialogUi.Card(panel, new Thickness(0));
    }

    private static Style? Style(string key) => Application.Current?.TryFindResource(key) as Style;
    private static System.Windows.Media.Brush Brush(string key) => Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.SlateGray;
}

internal sealed record RecipientOption(NotificationRecipientItemViewModel Item)
{
    public string DisplayText => $"{Item.Note}  ·  {Item.Summary}";
}
