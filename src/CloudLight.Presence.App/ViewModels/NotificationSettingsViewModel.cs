using System.Collections.ObjectModel;
using System.Windows;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Notifications;
using CloudLight.Presence.Infrastructure.SecureStorage;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.App.ViewModels;

public sealed class NotificationSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPresenceRepository _repository;
    private readonly JsonSettingsStore _settings;
    private readonly DpapiQqSecretStore _secretStore;
    private readonly QQNotificationChannel _qq;
    private readonly NotificationRuleAdministrationService _ruleAdministration;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private NotificationChannelStatus _qqStatus;
    private QqNotificationSettings _qqSettings = new();
    private ConnectionAlertSettings _connectionAlerts = new();
    private bool _secretConfigured;
    private string _operationStatus = "";

    public NotificationSettingsViewModel(IPresenceRepository repository, JsonSettingsStore settings, DpapiQqSecretStore secretStore, QQNotificationChannel qq)
    {
        _repository = repository;
        _settings = settings;
        _secretStore = secretStore;
        _qq = qq;
        _ruleAdministration = new NotificationRuleAdministrationService(repository);
        _qqStatus = qq.Status;
        _qq.StatusChanged += QqStatusChanged;
    }

    public ObservableCollection<NotificationRuleItemViewModel> Rules { get; } = [];
    public ObservableCollection<NotificationDeliveryItemViewModel> RecentDeliveries { get; } = [];
    public ObservableCollection<SystemNotificationDeliveryItemViewModel> RecentSystemDeliveries { get; } = [];
    public bool HasRules => Rules.Count > 0;
    public bool HasRecentDeliveries => RecentDeliveries.Count > 0;
    public bool HasRecentSystemDeliveries => RecentSystemDeliveries.Count > 0;
    public bool HasAnyRecentDeliveries => HasRecentDeliveries || HasRecentSystemDeliveries;
    public QqNotificationSettings QqSettings => _qqSettings;
    public ConnectionAlertSettings ConnectionAlerts => _connectionAlerts;
    public NotificationChannelStatus QqStatus => _qqStatus;
    public bool QqConfigured => _qqStatus.Configured;
    public bool QqSecretConfigured => _secretConfigured;
    public string QqSecretText => _secretConfigured ? "已保存（Windows 用户专属加密）" : "尚未保存";
    public string QqStatusText => _qqStatus.ConnectionState switch
    {
        NotificationConnectionState.Connected => "已连接",
        NotificationConnectionState.Authenticating or NotificationConnectionState.Connecting or NotificationConnectionState.Identifying => "正在连接",
        NotificationConnectionState.Reconnecting => "正在重连",
        NotificationConnectionState.AuthenticationFailed => "认证失败",
        NotificationConnectionState.GatewayFailed => "连接失败",
        NotificationConnectionState.Stopping => "正在停止",
        NotificationConnectionState.Stopped => _qqStatus.Configured ? "未连接" : "未配置",
        _ => "未配置"
    };
    public string QqStatusDetail => string.IsNullOrWhiteSpace(_qqStatus.LastError)
        ? _qqStatus.ConnectionState == NotificationConnectionState.Connected ? "QQ 通知通道正在工作。" : "配置 QQ Bot 后即可发送通知。"
        : _qqStatus.LastError!;
    public string OperationStatus { get => _operationStatus; set => Set(ref _operationStatus, value); }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await _settings.LoadAsync(cancellationToken);
            _qqSettings = settings.Qq ?? new QqNotificationSettings();
            _connectionAlerts = settings.ConnectionAlerts ?? new ConnectionAlertSettings();
            _secretConfigured = _secretStore.Exists;
            RaiseQqProperties();
            await ReloadListsAsync(cancellationToken);
        }
        finally { _loadGate.Release(); }
    }

    public async Task SaveQqConfigurationAsync(QqNotificationSettings settings, string? secretDraft, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var secret = string.IsNullOrWhiteSpace(secretDraft)
                ? settings.Enabled ? await _secretStore.LoadAsync(cancellationToken) : null
                : secretDraft.Trim();
            if (settings.Enabled && string.IsNullOrWhiteSpace(settings.AppId)) throw new ArgumentException("请输入 QQ AppID。", nameof(settings));
            if (settings.Enabled && string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("请输入 QQ AppSecret。", nameof(settings));

            if (!string.IsNullOrWhiteSpace(secretDraft)) await _secretStore.SaveAsync(secret!, cancellationToken);
            await _qq.ConfigureAsync(settings, secret, cancellationToken);
            var current = await _settings.LoadAsync(cancellationToken);
            await _settings.SaveAsync(current with { Qq = settings }, cancellationToken);
            _qqSettings = settings;
            _secretConfigured = _secretStore.Exists;
            if (settings.Enabled && settings.AutoConnect) await _qq.StartAsync(cancellationToken);
            else await _qq.StopAsync(cancellationToken);
            OperationStatus = "QQ 设置已保存。";
            RaiseQqProperties();
        }
        finally { _loadGate.Release(); }
    }

    public async Task SaveConnectionAlertSettingsAsync(ConnectionAlertSettings settings, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var normalized = settings with { TargetId = settings.TargetId.Trim() };
            if (!normalized.UseDefaultTarget && (normalized.TargetId.Length == 0 || normalized.TargetId.Length > 256 || normalized.TargetId.Any(char.IsWhiteSpace)))
                throw new ArgumentException("请输入有效的 QQ 用户或群聊 OpenID。", nameof(settings));
            var current = await _settings.LoadAsync(cancellationToken);
            await _settings.SaveAsync(current with { ConnectionAlerts = normalized }, cancellationToken);
            _connectionAlerts = normalized;
            Raise(nameof(ConnectionAlerts));
            OperationStatus = "连接异常提醒设置已保存。";
        }
        finally { _loadGate.Release(); }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await _qq.TestConnectionAsync(cancellationToken);
        OperationStatus = "QQ 服务地址和应用密钥验证成功，正在等待 Gateway 连接。";
    }

    public async Task SendTestMessageAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken)
    {
        var result = await _qq.SendTestAsync(targetType, targetId.Trim(), cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "QQ 测试消息发送失败。");
        OperationStatus = "测试消息已发送。";
    }

    public async Task<IReadOnlyList<PresenceSubject>> GetSubjectsAsync(CancellationToken cancellationToken) =>
        await _repository.GetSubjectsAsync(cancellationToken);

    public async Task SaveRuleAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (rule.Id <= 0)
                await _repository.CreateNotificationRuleAsync(rule with { Id = 0, CreatedAt = now, UpdatedAt = now }, cancellationToken);
            else await _ruleAdministration.UpdateRuleAsync(rule, cancellationToken);
            await ReloadListsAsync(cancellationToken);
            OperationStatus = "自动提醒已保存。";
        }
        finally { _loadGate.Release(); }
    }

    public async Task DisableRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try { await _ruleAdministration.DisableRuleAsync(ruleId, cancellationToken); await ReloadListsAsync(cancellationToken); OperationStatus = "自动提醒已关闭。"; }
        finally { _loadGate.Release(); }
    }

    public async Task EnableRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try { await _ruleAdministration.EnableRuleAsync(ruleId, cancellationToken); await ReloadListsAsync(cancellationToken); OperationStatus = "自动提醒已启用。"; }
        finally { _loadGate.Release(); }
    }

    public async Task UpdateRuleAsync(NotificationRule rule, CancellationToken cancellationToken) => await SaveRuleAsync(rule, cancellationToken);

    public async Task ToggleRuleAsync(NotificationRuleItemViewModel item, CancellationToken cancellationToken)
    {
        if (item.Rule.Enabled) await DisableRuleAsync(item.Rule.Id, cancellationToken);
        else await EnableRuleAsync(item.Rule.Id, cancellationToken);
    }

    public Task ToggleRuleAsync(long ruleId, bool enabled, CancellationToken cancellationToken) => enabled ? EnableRuleAsync(ruleId, cancellationToken) : DisableRuleAsync(ruleId, cancellationToken);

    public async Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try { await _ruleAdministration.DeleteRuleAsync(ruleId, cancellationToken); await ReloadListsAsync(cancellationToken); OperationStatus = "自动提醒已删除。"; }
        finally { _loadGate.Release(); }
    }

    public Task DeleteRuleAsync(NotificationRuleItemViewModel item, CancellationToken cancellationToken) => DeleteRuleAsync(item.Rule.Id, cancellationToken);

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try { await ReloadListsAsync(cancellationToken); }
        finally { _loadGate.Release(); }
    }

    public void Dispose()
    {
        _qq.StatusChanged -= QqStatusChanged;
        _loadGate.Dispose();
    }

    private async Task ReloadListsAsync(CancellationToken cancellationToken)
    {
        var subjects = await _repository.GetSubjectsAsync(cancellationToken);
        var names = subjects.ToDictionary(value => value.Id, value => value.DisplayName);
        var rules = await _repository.GetNotificationRulesAsync(enabledOnly: false, cancellationToken);
        Rules.Clear();
        foreach (var rule in rules) Rules.Add(new NotificationRuleItemViewModel(rule, names.GetValueOrDefault(rule.SubjectId, "未知主体")));
        var deliveries = await _repository.GetRecentNotificationDeliveriesAsync(30, cancellationToken);
        RecentDeliveries.Clear();
        foreach (var delivery in deliveries)
            RecentDeliveries.Add(new NotificationDeliveryItemViewModel(delivery, delivery.SubjectId is { } subjectId ? names.GetValueOrDefault(subjectId, "未知主体") : "系统通知"));
        RecentSystemDeliveries.Clear();
        foreach (var delivery in await _repository.GetRecentSystemNotificationDeliveriesAsync(30, cancellationToken))
            RecentSystemDeliveries.Add(new SystemNotificationDeliveryItemViewModel(delivery));
        Raise(nameof(HasRules)); Raise(nameof(HasRecentDeliveries)); Raise(nameof(HasRecentSystemDeliveries)); Raise(nameof(HasAnyRecentDeliveries));
    }

    private void QqStatusChanged(object? sender, NotificationChannelStatus status)
    {
        void Update()
        {
            _qqStatus = status;
            RaiseQqProperties();
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Update();
        else _ = dispatcher.InvokeAsync(Update);
    }

    private void RaiseQqProperties()
    {
        Raise(nameof(QqSettings)); Raise(nameof(ConnectionAlerts)); Raise(nameof(QqStatus)); Raise(nameof(QqConfigured)); Raise(nameof(QqSecretConfigured));
        Raise(nameof(QqSecretText)); Raise(nameof(QqStatusText)); Raise(nameof(QqStatusDetail));
    }
}

public sealed class NotificationRuleItemViewModel(NotificationRule rule, string subjectName)
{
    public NotificationRule Rule { get; } = rule;
    public string SubjectName { get; } = subjectName;
    public string EnabledText => Rule.Enabled ? "已开启" : "已关闭";
    public string ToggleText => Rule.Enabled ? "关闭" : "开启";
    public string ConditionText => Rule.Condition == NotificationCondition.OnlineFor ? "连续在线" : "连续离线";
    public string DurationText => NotificationSettingsViewModelFormatting.FormatThreshold(Rule.ThresholdSeconds);
    public string TargetText => $"QQ {Rule.TargetType switch { NotificationTargetType.Private => "私聊", _ => "群聊" }} {NotificationSettingsViewModelFormatting.MaskTarget(Rule.TargetId)}";
    public string Summary => $"{SubjectName} · {ConditionText} {DurationText} · 发送到 {TargetText}";
    public string MessagePreview => string.IsNullOrWhiteSpace(Rule.MessageTemplate) ? "使用默认通知内容" : Rule.MessageTemplate.Replace('\n', ' ');
}

public sealed class NotificationDeliveryItemViewModel(NotificationDelivery delivery, string subjectName)
{
    public string CreatedText => delivery.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string SubjectName { get; } = subjectName;
    public string TargetText => $"QQ {delivery.TargetType switch { NotificationTargetType.Private => "私聊", _ => "群聊" }} {NotificationSettingsViewModelFormatting.MaskTarget(delivery.TargetId)}";
    public string MessageText => delivery.Message.Replace('\n', ' ');
    public string StatusText => delivery.Status switch
    {
        NotificationDeliveryStatus.Delivered => "已发送",
        NotificationDeliveryStatus.Failed => "发送失败，等待重试",
        NotificationDeliveryStatus.Canceled => "已取消",
        _ => "等待发送"
    };
    public string StatusColor => delivery.Status switch
    {
        NotificationDeliveryStatus.Delivered => "#16803A",
        NotificationDeliveryStatus.Failed => "#B45309",
        _ => "#64748B"
    };
    public string ErrorText => string.IsNullOrWhiteSpace(delivery.Error) ? "" : delivery.Error!;
}

public sealed class SystemNotificationDeliveryItemViewModel(SystemNotificationDelivery delivery)
{
    public string CreatedText => delivery.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string SubjectName => delivery.Kind == SystemNotificationKind.XiaomiConnectionFailure ? "Xiaomi 连接异常" : "Xiaomi 连接恢复";
    public string MessageText => delivery.Message.Replace('\n', ' ');
    public string TargetText => $"QQ {delivery.TargetType switch { NotificationTargetType.Private => "私聊", _ => "群聊" }} {NotificationSettingsViewModelFormatting.MaskTarget(delivery.TargetId)}";
    public string StatusText => delivery.Status switch { NotificationDeliveryStatus.Delivered => "已发送", NotificationDeliveryStatus.Failed => "发送失败，等待重试", NotificationDeliveryStatus.Canceled => "已取消", _ => "等待发送" };
    public string StatusColor => delivery.Status switch { NotificationDeliveryStatus.Delivered => "#16803A", NotificationDeliveryStatus.Failed => "#B45309", _ => "#64748B" };
    public string ErrorText => string.IsNullOrWhiteSpace(delivery.Error) ? "" : delivery.Error!;
}

internal static class NotificationSettingsViewModelFormatting
{
    public static string FormatThreshold(long seconds)
    {
        if (seconds % (24 * 60 * 60) == 0) return $"{seconds / (24 * 60 * 60)}天";
        if (seconds % (60 * 60) == 0) return $"{seconds / (60 * 60)}小时";
        return $"{seconds / 60}分钟";
    }

    public static string MaskTarget(string target)
    {
        if (target.Length <= 4) return target;
        if (target.Length <= 7) return $"{target[..2]}****{target[^2..]}";
        return $"{target[..3]}****{target[^3..]}";
    }
}
