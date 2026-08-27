using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using GroupBox = System.Windows.Controls.GroupBox;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.Views;

public sealed record QqConfigurationDraft(QqNotificationSettings Settings, string? AppSecret);
public sealed record QqTestDraft(NotificationTargetType TargetType, string TargetId);

public static class QqConfigurationDialog
{
    public static QqConfigurationDraft? Show(Window owner, QqNotificationSettings settings, bool secretConfigured)
    {
        var window = Dialog(owner, "配置 QQ 通知", 540, 620);
        var panel = Panel();
        var enabled = new CheckBox { Content = "启用 QQ 自动提醒", IsChecked = settings.Enabled };
        var appId = new TextBox { Text = settings.AppId, MaxLength = 32 };
        var appSecret = new PasswordBox { MaxLength = 256 };
        var secretHint = new TextBlock { Text = secretConfigured ? "留空表示继续使用已保存的密钥。" : "密钥只保存在当前 Windows 用户的 DPAPI 加密文件中。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
        var autoConnect = new CheckBox { Content = "应用启动后自动连接", IsChecked = settings.AutoConnect, Margin = new Thickness(0, 2, 0, 0) };
        var reconnect = new CheckBox { Content = "连接断开后自动重连", IsChecked = settings.GatewayReconnectEnabled, Margin = new Thickness(0, 10, 0, 0) };
        var defaultTargetType = new ComboBox { MinHeight = 34 }; AddItem(defaultTargetType, "QQ 私聊", "Private"); AddItem(defaultTargetType, "QQ群聊", "Group"); defaultTargetType.SelectedIndex = settings.DefaultTargetType == NotificationTargetType.Group ? 1 : 0;
        var defaultTargetId = new TextBox { Text = settings.DefaultTargetId, MinHeight = 34 };
        var proxyMode = new ComboBox { Width = 180 };
        AddItem(proxyMode, "跟随系统代理", "environment"); AddItem(proxyMode, "直连", "direct"); AddItem(proxyMode, "自定义 HTTP 代理", "custom-http");
        proxyMode.SelectedIndex = settings.ProxyMode == "direct" ? 1 : settings.ProxyMode == "custom-http" ? 2 : 0;
        var proxyUrl = new TextBox { Text = settings.ProxyUrl, Width = 250, ToolTip = "例如 http://127.0.0.1:7897" };
        var error = ErrorText();
        panel.Children.Add(new TextBlock { Text = "QQ 官方 Bot 配置", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "AppID 和 AppSecret 来自 QQ 开放平台。私聊或群聊目标请在发送测试消息和提醒规则中填写 OpenID。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        panel.Children.Add(enabled);
        AddRow(panel, "AppID", appId);
        AddRow(panel, "AppSecret", new StackPanel { Children = { appSecret, secretHint } });
        panel.Children.Add(autoConnect); panel.Children.Add(reconnect); AddRow(panel, "默认接收", defaultTargetType); AddRow(panel, "默认 OpenID", defaultTargetId); panel.Children.Add(new TextBlock { Text = "系统连接异常提醒可以使用此默认目标；OpenID 不是普通 QQ 号。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(100, 5, 0, 0) });
        var advanced = new GroupBox { Header = "高级设置", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(12) };
        var advancedPanel = new StackPanel(); AddRow(advancedPanel, "网络代理", proxyMode); AddRow(advancedPanel, "代理地址", proxyUrl); advanced.Content = advancedPanel; panel.Children.Add(advanced);
        panel.Children.Add(error); var result = (QqConfigurationDraft?)null; var buttons = Buttons(window, "保存", () =>
        {
            var mode = (proxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "environment";
            if (enabled.IsChecked == true && (appId.Text.Trim().Length < 5 || appId.Text.Trim().Any(value => value is < '0' or > '9'))) { error.Text = "启用 QQ 时请输入有效的数字 AppID。"; return false; }
            if (mode == "custom-http")
            {
                if (!Uri.TryCreate(proxyUrl.Text.Trim(), UriKind.Absolute, out var proxy) || proxy.Scheme != Uri.UriSchemeHttp) { error.Text = "自定义代理必须是 HTTP 地址，例如 http://127.0.0.1:7897。"; return false; }
            }
            var defaultType = (defaultTargetType.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Group" ? NotificationTargetType.Group : NotificationTargetType.Private;
            var defaultId = defaultTargetId.Text.Trim();
            if (defaultId.Any(char.IsWhiteSpace)) { error.Text = "默认 OpenID 不能包含空格。"; return false; }
            result = new(new QqNotificationSettings(enabled.IsChecked == true, autoConnect.IsChecked == true, appId.Text.Trim(), reconnect.IsChecked == true, mode, mode == "custom-http" ? proxyUrl.Text.Trim() : "", defaultType, defaultId), string.IsNullOrWhiteSpace(appSecret.Password) ? null : appSecret.Password);
            return true;
        });
        panel.Children.Add(buttons); window.Content = panel;
        return window.ShowDialog() == true ? result : null;
    }

    private static void AddItem(ComboBox box, string text, string tag) => box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
    private static StackPanel Panel() => new() { Margin = new Thickness(24) };
    private static Window Dialog(Window owner, string title, double width, double height) => new() { Owner = owner, Title = title, Width = width, Height = height, MinHeight = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White, ResizeMode = ResizeMode.NoResize };
    private static void AddRow(Panel panel, string label, UIElement control) { var row = new Grid { Margin = new Thickness(0, 8, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) }); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row); }
    private static StackPanel Buttons(Window window, string accept, Func<bool> validate) { var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => window.DialogResult = false; var ok = new Button { Content = accept, MinWidth = 80, IsDefault = true }; ok.Click += (_, _) => { if (validate()) window.DialogResult = true; }; row.Children.Add(cancel); row.Children.Add(ok); return row; }
    private static TextBlock ErrorText() => new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private static System.Windows.Media.Brush MutedBrush => System.Windows.Media.Brushes.SlateGray;
}

public static class QqTestDialog
{
    public static QqTestDraft? Show(Window owner)
    {
        var window = new Window { Owner = owner, Title = "发送 QQ 测试消息", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) }; var type = new ComboBox(); type.Items.Add(new ComboBoxItem { Content = "私聊", Tag = NotificationTargetType.Private }); type.Items.Add(new ComboBoxItem { Content = "群聊", Tag = NotificationTargetType.Group }); type.SelectedIndex = 0;
        var target = new TextBox(); var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "选择发送目标", FontSize = 17, FontWeight = FontWeights.SemiBold }); AddRow(panel, "发送到", type); AddRow(panel, "OpenID", target); panel.Children.Add(new TextBlock { Text = "这里填写 QQ 官方开放平台返回的用户或群 OpenID，不是好友昵称。", Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }); panel.Children.Add(error);
        QqTestDraft? result = null; var buttons = Buttons(window, "发送", () => { if (string.IsNullOrWhiteSpace(target.Text) || target.Text.Any(char.IsWhiteSpace)) { error.Text = "请输入有效的 OpenID。"; return false; } result = new((type.SelectedItem as ComboBoxItem)?.Tag is NotificationTargetType value ? value : NotificationTargetType.Private, target.Text.Trim()); return true; }); panel.Children.Add(buttons); window.Content = panel;
        return window.ShowDialog() == true ? result : null;
    }

    private static void AddRow(Panel panel, string label, UIElement control) { var row = new Grid { Margin = new Thickness(0, 10, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.Children.Add(new TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row); }
    private static StackPanel Buttons(Window window, string accept, Func<bool> validate) { var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => window.DialogResult = false; var ok = new Button { Content = accept, MinWidth = 80, IsDefault = true }; ok.Click += (_, _) => { if (validate()) window.DialogResult = true; }; row.Children.Add(cancel); row.Children.Add(ok); return row; }
}

public static class NotificationRuleDialog
{
    private const long MaxSeconds = 365L * 24 * 60 * 60;

    public static NotificationRule? Show(Window owner, IReadOnlyList<PresenceSubject> subjects, NotificationRule? existing)
    {
        if (subjects.Count == 0) return null;
        var window = new Window { Owner = owner, Title = existing is null ? "添加自动提醒" : "编辑自动提醒", Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height * .86, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White, ResizeMode = ResizeMode.NoResize };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * .76 };
        var panel = new StackPanel { Margin = new Thickness(28) }; scroll.Content = panel;
        panel.Children.Add(new TextBlock { Text = existing is null ? "添加自动提醒" : "编辑自动提醒", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.DarkSlateGray });
        panel.Children.Add(new TextBlock { Text = "当用户或设备持续处于指定状态时，通过 QQ 发送一次提醒。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16) });
        var subject = new ComboBox { ItemsSource = subjects, DisplayMemberPath = nameof(PresenceSubject.DisplayName), MinHeight = 34 }; subject.SelectedItem = subjects.FirstOrDefault(value => value.Id == existing?.SubjectId) ?? subjects[0];
        var condition = new ComboBox { MinHeight = 34 }; condition.Items.Add(new ComboBoxItem { Content = "连续在线", Tag = NotificationCondition.OnlineFor }); condition.Items.Add(new ComboBoxItem { Content = "连续离线", Tag = NotificationCondition.OfflineFor }); condition.SelectedIndex = existing?.Condition == NotificationCondition.OfflineFor ? 1 : 0;
        var amount = new TextBox { Width = 112, TextAlignment = TextAlignment.Right, Text = existing is null ? "1" : ToAmount(existing.ThresholdSeconds).ToString() }; amount.PreviewTextInput += DigitsOnly;
        var unit = new ComboBox { Width = 120, MinHeight = 34 }; unit.Items.Add(new ComboBoxItem { Content = "分钟", Tag = 60L }); unit.Items.Add(new ComboBoxItem { Content = "小时", Tag = 3600L }); unit.Items.Add(new ComboBoxItem { Content = "天", Tag = 86400L }); unit.SelectedIndex = existing is null ? 0 : ToUnitIndex(existing.ThresholdSeconds);
        var targetType = new ComboBox { MinHeight = 34 }; targetType.Items.Add(new ComboBoxItem { Content = "QQ 私聊", Tag = NotificationTargetType.Private }); targetType.Items.Add(new ComboBoxItem { Content = "QQ群聊", Tag = NotificationTargetType.Group }); targetType.SelectedIndex = existing?.TargetType == NotificationTargetType.Group ? 1 : 0;
        var target = new TextBox { Text = existing?.TargetId ?? "", MinHeight = 34 }; var targetLabel = new TextBlock { Foreground = MutedBrush, Margin = new Thickness(0, 5, 0, 0) };
        var conditionDefault = DefaultTemplate(condition); var template = new TextBox { Text = existing?.MessageTemplate ?? conditionDefault, MinHeight = 112, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; var templateEdited = existing is not null && !string.Equals(existing.MessageTemplate, conditionDefault, StringComparison.Ordinal); var lastDefault = conditionDefault;
        template.TextChanged += (_, _) => { if (template.IsKeyboardFocusWithin) templateEdited = true; };
        condition.SelectionChanged += (_, _) => { var current = DefaultTemplate(condition); if (!templateEdited && (template.Text == lastDefault || string.IsNullOrWhiteSpace(template.Text))) template.Text = current; lastDefault = current; };
        targetType.SelectionChanged += (_, _) => UpdateTargetHelp(targetType, targetLabel);
        UpdateTargetHelp(targetType, targetLabel);
        AddSectionLabel(panel, "用户 / 设备"); AddField(panel, "提醒主体", subject);
        AddSectionLabel(panel, "触发条件"); AddField(panel, "条件", condition);
        var duration = new StackPanel { Orientation = Orientation.Horizontal }; duration.Children.Add(amount); duration.Children.Add(new Border { Width = 10, Background = System.Windows.Media.Brushes.Transparent }); duration.Children.Add(unit); AddField(panel, "持续时间", duration);
        AddSectionLabel(panel, "QQ 接收目标"); AddField(panel, "发送到", targetType); AddField(panel, targetLabel, target); panel.Children.Add(new TextBlock { Text = "这里填写 QQ 官方开放平台提供的用户或群聊 OpenID，不是普通 QQ 号。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(128, 6, 0, 0) });
        AddSectionLabel(panel, "通知内容"); AddField(panel, "消息模板", template); panel.Children.Add(new TextBlock { Text = "留空会使用当前条件的默认模板。发送时会把大括号中的参数替换成真实值。", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(128, 6, 0, 0) });
        panel.Children.Add(ParameterHelp());
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(128, 12, 0, 0) }; panel.Children.Add(error);
        NotificationRule? result = null; var buttons = Buttons(window, "保存", () =>
        {
            if (subject.SelectedItem is not PresenceSubject selectedSubject) { error.Text = "请选择提醒主体。"; return false; }
            if (!long.TryParse(amount.Text.Trim(), out var number) || number <= 0) { error.Text = "持续时间必须是正整数。"; return false; }
            if (unit.SelectedItem is not ComboBoxItem { Tag: long multiplier }) { error.Text = "请选择时间单位。"; return false; }
            long seconds; try { seconds = checked(number * multiplier); } catch (OverflowException) { error.Text = "持续时间过大。"; return false; }
            if (seconds is < 60 or > MaxSeconds) { error.Text = "持续时间必须在 1 分钟到 365 天之间。"; return false; }
            if (string.IsNullOrWhiteSpace(target.Text) || target.Text.Any(char.IsWhiteSpace)) { error.Text = "请输入 QQ 用户或群聊 OpenID，不要填写普通 QQ 号。"; return false; }
            var now = DateTimeOffset.UtcNow; var selectedCondition = (condition.SelectedItem as ComboBoxItem)?.Tag is NotificationCondition conditionValue ? conditionValue : NotificationCondition.OnlineFor; var selectedTarget = (targetType.SelectedItem as ComboBoxItem)?.Tag is NotificationTargetType targetValue ? targetValue : NotificationTargetType.Private;
            result = new(existing?.Id ?? 0, selectedSubject.Id, existing?.Enabled ?? true, selectedCondition, seconds, NotificationChannelType.QQ, selectedTarget, target.Text.Trim(), template.Text.Trim(), existing?.CreatedAt ?? now, now); return true;
        });
        panel.Children.Add(buttons); window.Content = scroll; return window.ShowDialog() == true ? result : null;
    }

    private static string DefaultTemplate(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is NotificationCondition condition ? NotificationTemplateRenderer.DefaultTemplate(condition) : NotificationTemplateRenderer.DefaultTemplate(NotificationCondition.OnlineFor);
    private static long ToAmount(long seconds) => seconds % 86400 == 0 ? seconds / 86400 : seconds % 3600 == 0 ? seconds / 3600 : seconds / 60;
    private static int ToUnitIndex(long seconds) => seconds % 86400 == 0 ? 2 : seconds % 3600 == 0 ? 1 : 0;
    private static void DigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(value => !char.IsDigit(value));
    private static void AddSectionLabel(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.DarkSlateGray, Margin = new Thickness(0, 12, 0, 2) });
    private static void AddField(Panel panel, string label, UIElement control) => AddField(panel, new TextBlock { Text = label, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center }, control);
    private static void AddField(Panel panel, TextBlock label, UIElement control) { var row = new Grid { Margin = new Thickness(0, 7, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); row.ColumnDefinitions.Add(new ColumnDefinition()); label.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(label); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row); }
    private static void UpdateTargetHelp(ComboBox type, TextBlock label) => label.Text = (type.SelectedItem as ComboBoxItem)?.Tag is NotificationTargetType.Group ? "群聊 OpenID" : "用户 OpenID";
    private static Border ParameterHelp()
    {
        var border = new Border { Background = System.Windows.Media.Brushes.White, BorderBrush = System.Windows.Media.Brushes.LightSteelBlue, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 16, 0, 0) }; var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "可用参数", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.DarkSlateGray }); panel.Children.Add(new TextBlock { Text = "参数              含义 / 示例", Foreground = MutedBrush, Margin = new Thickness(0, 9, 0, 4) });
        AddParameter(panel, "{name}", "用户或设备名称，例如：爸爸"); AddParameter(panel, "{state}", "当前状态，例如：在线 / 离线"); AddParameter(panel, "{duration}", "本次连续状态持续时间，例如：14小时23分钟"); AddParameter(panel, "{stateSince}", "本次在线或离线状态开始时间，例如：2026-08-26 08:15"); AddParameter(panel, "{lastOnlineTime}", "最近一次确认在线的时间"); AddParameter(panel, "{lastOfflineTime}", "最近一次确认离线的时间"); AddParameter(panel, "{currentTime}", "QQ 消息实际发送时间"); AddParameter(panel, "{routerName}", "当前监控的路由器名称"); border.Child = panel; return border;
    }
    private static void AddParameter(Panel panel, string name, string meaning) { var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.Children.Add(new TextBlock { Text = name, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = System.Windows.Media.Brushes.DarkSlateGray }); var text = new TextBlock { Text = meaning, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(text, 1); grid.Children.Add(text); panel.Children.Add(grid); }
    private static StackPanel Buttons(Window window, string accept, Func<bool> validate) { var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; var cancel = new Button { Content = "取消", Style = (System.Windows.Style?)System.Windows.Application.Current?.FindResource("SecondaryButton"), MinWidth = 84, Margin = new Thickness(0, 0, 10, 0), IsCancel = true }; cancel.Click += (_, _) => window.DialogResult = false; var ok = new Button { Content = accept, MinWidth = 84, IsDefault = true }; ok.Click += (_, _) => { if (validate()) window.DialogResult = true; }; row.Children.Add(cancel); row.Children.Add(ok); return row; }
    private static System.Windows.Media.Brush MutedBrush => System.Windows.Media.Brushes.SlateGray;
}
