using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
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
    private readonly INotificationRuleService? _ruleService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private NotificationChannelStatus _qqStatus;
    private QqNotificationSettings _qqSettings = new();
    private ConnectionAlertSettings _connectionAlerts = new();
    private IReadOnlyList<QqBotProfile> _qqBotProfiles = [];
    private QqBotProfile? _currentBotProfile;
    private bool _secretConfigured;
    private string _operationStatus = "";
    private bool _disposed;

    public NotificationSettingsViewModel(
        IPresenceRepository repository,
        JsonSettingsStore settings,
        DpapiQqSecretStore secretStore,
        QQNotificationChannel qq,
        INotificationRuleService? ruleService = null)
    {
        _repository = repository;
        _settings = settings;
        _secretStore = secretStore;
        _qq = qq;
        _ruleService = ruleService;
        _ruleAdministration = new NotificationRuleAdministrationService(repository);
        _qqStatus = qq.Status;
        _qq.StatusChanged += QqStatusChanged;
    }

    public ObservableCollection<NotificationRuleItemViewModel> Rules { get; } = [];
    public ObservableCollection<NotificationRecipientItemViewModel> Recipients { get; } = [];
    public ObservableCollection<NotificationDeliveryItemViewModel> RecentDeliveries { get; } = [];
    public ObservableCollection<SystemNotificationDeliveryItemViewModel> RecentSystemDeliveries { get; } = [];
    public IReadOnlyList<QqBotProfile> QqBotProfiles => _qqBotProfiles;
    public QqBotProfile? CurrentBotProfile => _currentBotProfile;
    public string CurrentBotText => _currentBotProfile is null
        ? "当前 Bot：未配置"
        : $"当前 Bot：{_currentBotProfile.DisplayName} · AppID {NotificationSettingsViewModelFormatting.MaskTarget(_currentBotProfile.AppId)}";
    public bool HasCurrentBot => _currentBotProfile is not null;
    public int UnboundRecipientCount => Recipients.Count(value => !value.HasCurrentBinding);
    public string BotSwitchText => UnboundRecipientCount == 0
        ? ""
        : $"QQ Bot 已更换或尚未绑定：共有 {UnboundRecipientCount} 个联系人需要当前 Bot 的 OpenID。";
    public bool HasRules => Rules.Count > 0;
    public bool HasRecipients => Recipients.Count > 0;
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
    public WpfBrush QqStatusBrush => _qqStatus.ConnectionState switch
    {
        NotificationConnectionState.Connected => WpfBrushes.SeaGreen,
        NotificationConnectionState.AuthenticationFailed or NotificationConnectionState.GatewayFailed => WpfBrushes.Firebrick,
        NotificationConnectionState.Authenticating or NotificationConnectionState.Connecting or NotificationConnectionState.Identifying or NotificationConnectionState.Reconnecting => WpfBrushes.RoyalBlue,
        _ => WpfBrushes.SlateGray
    };
    public WpfBrush QqStatusBackgroundBrush => _qqStatus.ConnectionState switch
    {
        NotificationConnectionState.Connected => WpfBrushes.Honeydew,
        NotificationConnectionState.AuthenticationFailed or NotificationConnectionState.GatewayFailed => WpfBrushes.MistyRose,
        _ => WpfBrushes.AliceBlue
    };
    public string OperationStatus { get => _operationStatus; set => Set(ref _operationStatus, value); }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await _settings.LoadAsync(cancellationToken);
            _qqSettings = settings.Qq ?? new QqNotificationSettings();
            _connectionAlerts = settings.ConnectionAlerts ?? new ConnectionAlertSettings();
            var migrated = await MigrateLegacyRecipientSettingsAsync(_qqSettings, _connectionAlerts, cancellationToken);
            _qqSettings = migrated.Qq;
            _connectionAlerts = migrated.ConnectionAlerts;
            await EnsureCurrentBotProfileAsync(cancellationToken);
            if (migrated.Changed)
                await _settings.SaveAsync(settings with { Qq = _qqSettings, ConnectionAlerts = _connectionAlerts }, cancellationToken);
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
            settings = await NormalizeDefaultRecipientsAsync(settings, cancellationToken);
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
            await EnsureCurrentBotProfileAsync(cancellationToken);
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
            var normalized = await NormalizeConnectionAlertRecipientsAsync(settings with { TargetId = settings.TargetId.Trim() }, cancellationToken);
            if (!normalized.UseDefaultTarget && normalized.RecipientIds.Count == 0 && (normalized.TargetId.Length == 0 || normalized.TargetId.Length > 256 || normalized.TargetId.Any(char.IsWhiteSpace)))
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

    public async Task SendTestMessageAsync(long recipientId, CancellationToken cancellationToken)
    {
        var recipient = await _repository.GetNotificationRecipientAsync(recipientId, cancellationToken)
            ?? throw new InvalidOperationException("所选接收人不存在，请刷新后重试。");
        await EnsureCurrentBotProfileAsync(cancellationToken);
        if (_currentBotProfile is null)
            throw new InvalidOperationException("当前 QQ Bot 尚未配置。 ");
        var binding = await _repository.GetNotificationRecipientBotBindingAsync(recipient.Id, _currentBotProfile.Id, cancellationToken);
        if (binding is null)
            throw new InvalidOperationException("当前 QQ Bot 尚未绑定此联系人。 ");
        var result = await _qq.SendTestAsync(binding.TargetType, binding.OpenId, cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "QQ 测试消息发送失败。 ");
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateNotificationRecipientBotBindingStatusAsync(binding.Id, now, now, "sent", null, cancellationToken);
        await _repository.TouchQqBotProfileLastUsedAsync(_currentBotProfile.Id, now, cancellationToken);
        OperationStatus = "测试消息已发送。";
    }

    public async Task<NotificationRecipientItemViewModel?> SaveRecipientAsync(NotificationRecipientDraft draft, long? recipientId, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (draft.OpenIdEdited && (string.IsNullOrWhiteSpace(draft.OpenId) || draft.OpenId.Any(char.IsWhiteSpace) || draft.OpenId.Contains('*', StringComparison.Ordinal)))
                throw new ArgumentException("请输入新的完整 OpenID，不能保存脱敏占位符。", nameof(draft));
            if (string.IsNullOrWhiteSpace(draft.Note)) throw new ArgumentException("请输入接收人备注。", nameof(draft));
            var recipient = new NotificationRecipient(recipientId ?? 0, draft.Note.Trim(), draft.TargetType, now, now);
            if (recipientId is null)
                recipient = await _repository.CreateNotificationRecipientAsync(recipient, cancellationToken);
            else
            {
                await _repository.UpdateNotificationRecipientAsync(recipient, cancellationToken);
                recipient = await _repository.GetNotificationRecipientAsync(recipientId.Value, cancellationToken) ?? recipient;
            }
            if (draft.OpenIdEdited)
            {
                if (string.IsNullOrWhiteSpace(draft.OpenId) || draft.OpenId.Any(char.IsWhiteSpace) || draft.OpenId.Contains('*', StringComparison.Ordinal))
                    throw new ArgumentException("请输入新的完整 OpenID，不能保存脱敏占位符。", nameof(draft));
                await EnsureCurrentBotProfileAsync(cancellationToken);
                if (_currentBotProfile is null) throw new InvalidOperationException("请先配置当前 QQ Bot，再添加 OpenID 绑定。 ");
                await _repository.UpsertNotificationRecipientBotBindingAsync(new NotificationRecipientBotBinding(
                    0, recipient.Id, _currentBotProfile.Id, recipient.TargetType, draft.OpenId.Trim(), now, now), cancellationToken);
            }
            await ReloadListsAsync(cancellationToken);
            OperationStatus = recipientId is null ? "QQ 接收人已添加。" : "QQ 接收人已更新。";
            return Recipients.FirstOrDefault(value => value.Recipient.Id == recipient.Id);
        }
        finally { _loadGate.Release(); }
    }

    public async Task<NotificationRecipientBotBinding?> SaveRecipientBindingAsync(
        long recipientId,
        NotificationRecipientBindingDraft draft,
        long? bindingId,
        CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var recipient = await _repository.GetNotificationRecipientAsync(recipientId, cancellationToken)
                ?? throw new InvalidOperationException("所选接收人不存在，请刷新后重试。 ");
            var profile = _qqBotProfiles.FirstOrDefault(value => value.Id == draft.BotProfileId)
                ?? throw new InvalidOperationException("所选 QQ Bot Profile 不存在，请刷新后重试。 ");
            if (profile.IsLegacyUnknown) throw new InvalidOperationException("旧 Bot（AppID 未记录）只能作为历史绑定，不能新增。 ");
            var now = DateTimeOffset.UtcNow;
            var existing = bindingId is { } id
                ? (await _repository.GetNotificationRecipientBotBindingsAsync(recipientId, cancellationToken)).FirstOrDefault(value => value.Id == id)
                : await _repository.GetNotificationRecipientBotBindingAsync(recipientId, profile.Id, cancellationToken);
            if (existing is null && !draft.OpenIdEdited)
                throw new InvalidOperationException("要添加绑定时必须输入完整 OpenID。 ");
            var openId = draft.OpenId.Trim();
            if (existing is not null && !draft.OpenIdEdited) openId = existing.OpenId;
            var binding = new NotificationRecipientBotBinding(
                existing?.Id ?? 0, recipientId, profile.Id, recipient.TargetType, openId,
                existing?.CreatedAt ?? now, now, existing?.LastVerifiedAt, existing?.LastSuccessfulSendAt,
                existing?.LastSendStatus, existing?.LastErrorCode);
            var saved = await _repository.UpsertNotificationRecipientBotBindingAsync(binding, cancellationToken);
            await ReloadListsAsync(cancellationToken);
            OperationStatus = "QQ Bot 绑定已保存。";
            return saved;
        }
        finally { _loadGate.Release(); }
    }

    public async Task DeleteRecipientBindingAsync(long bindingId, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            await _repository.DeleteNotificationRecipientBotBindingAsync(bindingId, cancellationToken);
            await ReloadListsAsync(cancellationToken);
            OperationStatus = "QQ Bot 历史绑定已删除。";
        }
        finally { _loadGate.Release(); }
    }

    public async Task DeleteRecipientAsync(long recipientId, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            await _repository.DeleteNotificationRecipientAsync(recipientId, cancellationToken);
            await ReloadListsAsync(cancellationToken);
            OperationStatus = "QQ 接收人已删除。";
        }
        finally { _loadGate.Release(); }
    }

    public async Task<int> GetRecipientUsageCountAsync(long recipientId, CancellationToken cancellationToken) =>
        await _repository.GetNotificationRecipientUsageCountAsync(recipientId, cancellationToken);

    public NotificationRecipientItemViewModel? SaveRecipientFromDialog(NotificationRecipientDraft draft)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(draft.OpenId) || draft.OpenId.Any(char.IsWhiteSpace) || draft.OpenId.Contains('*', StringComparison.Ordinal))
                throw new ArgumentException("请输入有效的 OpenID。", nameof(draft));
            if (string.IsNullOrWhiteSpace(draft.Note))
                throw new ArgumentException("请输入接收人备注。", nameof(draft));

            var now = DateTimeOffset.UtcNow;
            var appId = (_qq.CurrentAppId.Length > 0 ? _qq.CurrentAppId : _qqSettings.AppId).Trim();
            if (appId.Length == 0) throw new InvalidOperationException("请先配置当前 QQ Bot，再添加 OpenID 绑定。 ");
            var recipient = Task.Run(async () =>
            {
                var created = await _repository.CreateNotificationRecipientAsync(
                    new NotificationRecipient(0, draft.Note.Trim(), draft.TargetType, now, now), CancellationToken.None);
                var profile = await _repository.EnsureQqBotProfileAsync(appId, "当前 QQ Bot", now, CancellationToken.None);
                await _repository.UpsertNotificationRecipientBotBindingAsync(new NotificationRecipientBotBinding(
                    0, created.Id, profile.Id, created.TargetType, draft.OpenId.Trim(), now, now), CancellationToken.None);
                return created;
            }).GetAwaiter().GetResult();
            var profileForItem = Task.Run(() => _repository.GetQqBotProfileByAppIdAsync(appId, CancellationToken.None)).GetAwaiter().GetResult();
            var bindingsForItem = Task.Run(() => _repository.GetNotificationRecipientBotBindingsAsync(recipient.Id, CancellationToken.None)).GetAwaiter().GetResult();
            var item = new NotificationRecipientItemViewModel(recipient, bindingsForItem, profileForItem);
            void AddToList()
            {
                var existing = Recipients.FirstOrDefault(value => value.Id == item.Id);
                if (existing is null) Recipients.Insert(0, item);
                Raise(nameof(HasRecipients));
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) AddToList();
            else dispatcher.Invoke(AddToList);
            OperationStatus = "QQ 接收人已添加。";
            return item;
        }
        catch (Exception exception)
        {
            OperationStatus = $"接收人未保存：{exception.Message}";
            return null;
        }
    }

    public NotificationRecipientItemViewModel? FindRecipientItem(long recipientId) =>
        Recipients.FirstOrDefault(value => value.Id == recipientId);

    public async Task<IReadOnlyList<PresenceSubject>> GetSubjectsAsync(CancellationToken cancellationToken) =>
        await _repository.GetSubjectsAsync(cancellationToken);

    public async Task<IReadOnlyList<NotificationSubjectOption>> GetNotificationSubjectOptionsAsync(CancellationToken cancellationToken)
    {
        var subjects = await _repository.GetSubjectsAsync(cancellationToken);
        var routers = (await _repository.GetRoutersAsync(cancellationToken)).ToDictionary(value => value.Id, value => value.Name);
        var entries = new List<(PresenceSubject Subject, IReadOnlyList<NetworkDevice> Devices)>();
        foreach (var subject in subjects)
            entries.Add((subject, await _repository.GetSubjectDevicesAsync(subject.Id, cancellationToken)));

        var labels = new Dictionary<long, string>();
        foreach (var group in entries.GroupBy(value => value.Subject.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                var only = group.Single();
                labels[only.Subject.Id] = only.Subject.DisplayName;
                continue;
            }

            foreach (var entry in group)
            {
                var deviceCount = entry.Devices.Count;
                var routerNames = entry.Devices
                    .Select(value => routers.GetValueOrDefault(value.RouterId))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var label = deviceCount > 1
                    ? $"{entry.Subject.DisplayName} · {deviceCount}台设备"
                    : deviceCount == 1 && routerNames.Length == 1
                        ? $"{entry.Subject.DisplayName} · {routerNames[0]} · 1台设备"
                        : $"{entry.Subject.DisplayName} · {deviceCount}台设备 · 主体{entry.Subject.Id}";
                labels[entry.Subject.Id] = label;
            }
        }

        return entries
            .Select(value => new NotificationSubjectOption(value.Subject, labels[value.Subject.Id]))
            .ToArray();
    }

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

    public async Task RefreshRuleDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_ruleService is null || _disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            var operation = dispatcher.InvokeAsync(() => RefreshRuleDiagnosticsCoreAsync(cancellationToken));
            await operation.Task.Unwrap();
            return;
        }
        if (_disposed || Rules.Count == 0) return;
        await _loadGate.WaitAsync(cancellationToken);
        try { await RefreshRuleDiagnosticsCoreAsync(cancellationToken); }
        finally { _loadGate.Release(); }
    }

    public async Task CheckRuleAsync(NotificationRuleItemViewModel item, CancellationToken cancellationToken)
    {
        if (_disposed || _ruleService is null)
        {
            if (!_disposed) OperationStatus = "规则诊断服务尚未初始化。";
            return;
        }
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var diagnostic = await _ruleService.EvaluateDiagnosticAsync(item.Rule.Id, DateTimeOffset.UtcNow, cancellationToken);
            item.ApplyDiagnostic(diagnostic);
            OperationStatus = $"检查完成：{diagnostic.Title}";
        }
        finally { _loadGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _qq.StatusChanged -= QqStatusChanged;
        _loadGate.Dispose();
    }

    private async Task ReloadListsAsync(CancellationToken cancellationToken)
    {
        var subjects = await _repository.GetSubjectsAsync(cancellationToken);
        var names = subjects.ToDictionary(value => value.Id, value => value.DisplayName);
        var recipients = await _repository.GetNotificationRecipientsAsync(cancellationToken);
        await EnsureCurrentBotProfileAsync(cancellationToken);
        var profiles = _qqBotProfiles.ToDictionary(value => value.Id);
        var recipientNames = recipients.ToDictionary(value => value.Id, value => value.DisplayName);
        Recipients.Clear();
        foreach (var recipient in recipients)
        {
            var bindings = await _repository.GetNotificationRecipientBotBindingsAsync(recipient.Id, cancellationToken);
            Recipients.Add(new NotificationRecipientItemViewModel(recipient, bindings, _currentBotProfile, profiles));
        }
        var rules = await _repository.GetNotificationRulesAsync(enabledOnly: false, cancellationToken);
        Rules.Clear();
        foreach (var rule in rules)
        {
            var ruleRecipients = await _repository.GetNotificationRuleRecipientsAsync(rule.Id, cancellationToken);
            Rules.Add(new NotificationRuleItemViewModel(rule, names.GetValueOrDefault(rule.SubjectId, "未知主体"), ruleRecipients));
        }
        await RefreshRuleDiagnosticsCoreAsync(cancellationToken);
        var deliveries = await _repository.GetRecentNotificationDeliveriesAsync(30, cancellationToken);
        RecentDeliveries.Clear();
        foreach (var delivery in deliveries)
        {
            var recipientName = delivery.RecipientId is { } recipientId
                ? recipientNames.GetValueOrDefault(recipientId)
                : null;
            RecentDeliveries.Add(new NotificationDeliveryItemViewModel(delivery, delivery.SubjectId is { } subjectId ? names.GetValueOrDefault(subjectId, "未知主体") : "系统通知", recipientName, profiles));
        }
        RecentSystemDeliveries.Clear();
        foreach (var delivery in await _repository.GetRecentSystemNotificationDeliveriesAsync(30, cancellationToken))
        {
            var recipientName = delivery.RecipientId is { } recipientId
                ? recipientNames.GetValueOrDefault(recipientId)
                : null;
            RecentSystemDeliveries.Add(new SystemNotificationDeliveryItemViewModel(delivery, recipientName, profiles));
        }
        Raise(nameof(HasRules)); Raise(nameof(HasRecipients)); Raise(nameof(HasRecentDeliveries)); Raise(nameof(HasRecentSystemDeliveries)); Raise(nameof(HasAnyRecentDeliveries));
        Raise(nameof(QqBotProfiles)); Raise(nameof(CurrentBotProfile)); Raise(nameof(CurrentBotText)); Raise(nameof(HasCurrentBot)); Raise(nameof(UnboundRecipientCount)); Raise(nameof(BotSwitchText));
    }

    private async Task EnsureCurrentBotProfileAsync(CancellationToken cancellationToken)
    {
        var appId = (_qq.CurrentAppId.Length > 0 ? _qq.CurrentAppId : _qqSettings.AppId).Trim();
        if (appId.Length == 0)
        {
            _currentBotProfile = null;
            _qqBotProfiles = await _repository.GetQqBotProfilesAsync(cancellationToken);
            return;
        }

        _currentBotProfile = await _repository.EnsureQqBotProfileAsync(appId, "当前 QQ Bot", DateTimeOffset.UtcNow, cancellationToken);
        _qqBotProfiles = await _repository.GetQqBotProfilesAsync(cancellationToken);
    }

    private async Task RefreshRuleDiagnosticsCoreAsync(CancellationToken cancellationToken)
    {
        if (_ruleService is null || _disposed) return;
        foreach (var rule in Rules.ToArray())
        {
            try
            {
                var diagnostic = await _ruleService.EvaluateDiagnosticAsync(rule.Rule.Id, DateTimeOffset.UtcNow, cancellationToken);
                rule.ApplyDiagnostic(diagnostic);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                rule.ApplyDiagnosticError(exception.Message);
            }
        }
    }

    private async Task<QqNotificationSettings> NormalizeDefaultRecipientsAsync(QqNotificationSettings settings, CancellationToken cancellationToken)
    {
        var ids = settings.DefaultRecipientIds.Distinct().ToArray();
        if (ids.Length == 0 && !string.IsNullOrWhiteSpace(settings.DefaultTargetId))
        {
            var now = DateTimeOffset.UtcNow;
            var created = await _repository.CreateNotificationRecipientAsync(new NotificationRecipient(0, "默认接收人", settings.DefaultTargetId.Trim(), settings.DefaultTargetType, now, now), cancellationToken);
            ids = [created.Id];
            settings = settings with { DefaultTargetType = created.TargetType, DefaultTargetId = string.Empty };
        }
        var recipients = new List<NotificationRecipient>();
        foreach (var id in ids)
        {
            var recipient = await _repository.GetNotificationRecipientAsync(id, cancellationToken) ?? throw new ArgumentException("默认接收人不存在，请重新选择。", nameof(settings));
            recipients.Add(recipient);
        }
        if (recipients.Count > 0)
            settings = settings with { DefaultRecipientIds = recipients.Select(value => value.Id).ToArray(), DefaultTargetType = recipients[0].TargetType, DefaultTargetId = string.Empty };
        return settings;
    }

    private async Task<ConnectionAlertSettings> NormalizeConnectionAlertRecipientsAsync(ConnectionAlertSettings settings, CancellationToken cancellationToken)
    {
        var ids = settings.RecipientIds.Distinct().ToArray();
        if (ids.Length == 0 && !settings.UseDefaultTarget && !string.IsNullOrWhiteSpace(settings.TargetId))
        {
            var now = DateTimeOffset.UtcNow;
            var created = await _repository.CreateNotificationRecipientAsync(new NotificationRecipient(0, "连接提醒接收人", settings.TargetId.Trim(), settings.TargetType, now, now), cancellationToken);
            ids = [created.Id];
            settings = settings with { TargetType = created.TargetType, TargetId = string.Empty };
        }
        var recipients = new List<NotificationRecipient>();
        foreach (var id in ids)
        {
            var recipient = await _repository.GetNotificationRecipientAsync(id, cancellationToken) ?? throw new ArgumentException("连接提醒接收人不存在，请重新选择。", nameof(settings));
            recipients.Add(recipient);
        }
        return recipients.Count == 0
            ? settings with { RecipientIds = [] }
            : settings with { RecipientIds = recipients.Select(value => value.Id).ToArray(), TargetType = recipients[0].TargetType, TargetId = string.Empty };
    }

    private async Task<(QqNotificationSettings Qq, ConnectionAlertSettings ConnectionAlerts, bool Changed)> MigrateLegacyRecipientSettingsAsync(QqNotificationSettings qq, ConnectionAlertSettings alerts, CancellationToken cancellationToken)
    {
        var originalQqIds = qq.DefaultRecipientIds;
        var originalAlertIds = alerts.RecipientIds;
        qq = await NormalizeDefaultRecipientsAsync(qq, cancellationToken);
        alerts = await NormalizeConnectionAlertRecipientsAsync(alerts, cancellationToken);
        var changed = !originalQqIds.SequenceEqual(qq.DefaultRecipientIds)
            || !string.Equals(qq.DefaultTargetId, _qqSettings.DefaultTargetId, StringComparison.Ordinal)
            || !originalAlertIds.SequenceEqual(alerts.RecipientIds)
            || !string.Equals(alerts.TargetId, _connectionAlerts.TargetId, StringComparison.Ordinal);
        return (qq, alerts, changed);
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
        Raise(nameof(QqSecretText)); Raise(nameof(QqStatusText)); Raise(nameof(QqStatusDetail)); Raise(nameof(QqStatusBrush)); Raise(nameof(QqStatusBackgroundBrush));
    }
}

public sealed record NotificationSubjectOption(PresenceSubject Subject, string Label)
{
    public long Id => Subject.Id;
}

public sealed record NotificationRecipientDraft(
    string Note,
    string OpenId,
    NotificationTargetType TargetType,
    bool OpenIdEdited = true);

public sealed record NotificationRecipientBindingDraft(long BotProfileId, string OpenId, bool OpenIdEdited = true);

public sealed class NotificationRecipientItemViewModel
{
    private readonly QqBotProfile? _currentBot;

    public NotificationRecipientItemViewModel(NotificationRecipient recipient)
        : this(recipient, [], null, new Dictionary<long, QqBotProfile>()) { }

    public NotificationRecipientItemViewModel(
        NotificationRecipient recipient,
        IReadOnlyList<NotificationRecipientBotBinding> bindings,
        QqBotProfile? currentBot,
        IReadOnlyDictionary<long, QqBotProfile>? profiles = null)
    {
        Recipient = recipient;
        Bindings = bindings;
        _currentBot = currentBot;
        Profiles = profiles ?? new Dictionary<long, QqBotProfile>();
        BindingItems = bindings.Select(value => new NotificationRecipientBindingItemViewModel(value, Profiles.GetValueOrDefault(value.BotProfileId))).ToArray();
    }

    public NotificationRecipient Recipient { get; }
    public IReadOnlyList<NotificationRecipientBotBinding> Bindings { get; }
    public IReadOnlyDictionary<long, QqBotProfile> Profiles { get; }
    public IReadOnlyList<NotificationRecipientBindingItemViewModel> BindingItems { get; }
    public long Id => Recipient.Id;
    public string Note => Recipient.DisplayName;
    public NotificationRecipientBotBinding? CurrentBinding => _currentBot is null ? null : Bindings.FirstOrDefault(value => value.BotProfileId == _currentBot.Id);
    public bool HasCurrentBinding => CurrentBinding is not null;
    public string CurrentBotName => _currentBot?.DisplayName ?? "当前 Bot 未配置";
    public long? CurrentBotProfileId => _currentBot?.Id;
    public string CurrentBotAppIdText => _currentBot is null ? "AppID 未配置" : NotificationSettingsViewModelFormatting.MaskTarget(_currentBot.AppId);
    public string CurrentOpenIdText => CurrentBinding is { } binding
        ? NotificationSettingsViewModelFormatting.MaskTarget(binding.OpenId)
        : "需要绑定";
    public string OpenId => CurrentBinding?.OpenId ?? string.Empty;
    public string MaskedOpenId => CurrentOpenIdText;
    public string TargetTypeText => Recipient.TargetTypeText;
    public int HistoricalBindingCount => Bindings.Count(value => _currentBot is null || value.BotProfileId != _currentBot.Id);
    public string BindingSummary => HasCurrentBinding ? $"当前 Bot · {CurrentOpenIdText}" : "当前 Bot · 需要绑定";
    public string Summary => $"{TargetTypeText} · {BindingSummary}";
}

public sealed class NotificationRecipientBindingItemViewModel(
    NotificationRecipientBotBinding binding,
    QqBotProfile? profile)
{
    public NotificationRecipientBotBinding Binding { get; } = binding;
    public QqBotProfile? Profile { get; } = profile;
    public long Id => Binding.Id;
    public string BotName => Profile?.DisplayName ?? "历史 Bot";
    public string AppIdText => Profile is { IsLegacyUnknown: true } ? "AppID 未记录" : Profile is null ? "Bot 未知" : NotificationSettingsViewModelFormatting.MaskTarget(Profile.AppId);
    public string OpenIdText => NotificationSettingsViewModelFormatting.MaskTarget(Binding.OpenId);
    public string HistoryText => Binding.LastSuccessfulSendAt is { } sent
        ? $"最近成功：{sent.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : "历史绑定";
    public string StatusText => string.IsNullOrWhiteSpace(Binding.LastSendStatus) ? "未验证" : Binding.LastSendStatus switch
    {
        "sent" => "可用",
        "permanent-failed" => "需处理",
        _ => "发送失败"
    };
}

public sealed class NotificationRuleItemViewModel(
    NotificationRule rule,
    string subjectName,
    IReadOnlyList<NotificationRecipient> recipients) : ObservableObject
{
    private RuleEvaluationDiagnostic? _diagnostic;

    public NotificationRule Rule { get; } = rule;
    public string SubjectName { get; } = subjectName;
    public IReadOnlyList<NotificationRecipient> Recipients { get; } = recipients;
    public string EnabledText => Rule.Enabled ? "已开启" : "已关闭";
    public string ToggleText => Rule.Enabled ? "关闭" : "开启";
    public string ConditionText => Rule.Condition switch
    {
        NotificationCondition.OnlineFor => "连续在线",
        NotificationCondition.OfflineFor => "连续离线",
        NotificationCondition.DetectedOnline => "检测到上线",
        NotificationCondition.DetectedOffline => "检测到离线",
        _ => "未知条件"
    };
    public string DurationText => NotificationSettingsViewModelFormatting.FormatThreshold(Rule.ThresholdSeconds);
    public string TargetText => Recipients.Count > 0
        ? string.Join("、", Recipients.Select(value => value.DisplayName))
        : $"QQ {Rule.TargetType switch { NotificationTargetType.Private => "私聊", _ => "群聊" }} {NotificationSettingsViewModelFormatting.MaskTarget(Rule.TargetId)}";
    public string Summary => Rule.Condition is NotificationCondition.OnlineFor or NotificationCondition.OfflineFor
        ? $"{SubjectName} · {ConditionText} {DurationText} · 发送到 {TargetText}"
        : $"{SubjectName} · {ConditionText} · 发送到 {TargetText}";
    public string MessagePreview => string.IsNullOrWhiteSpace(Rule.MessageTemplate) ? "使用默认通知内容" : Rule.MessageTemplate.Replace('\n', ' ');
    public RuleEvaluationDiagnostic? Diagnostic => _diagnostic;
    public bool HasDiagnostic => _diagnostic is not null;
    public string DiagnosticTitle => _diagnostic?.Title ?? "尚未评估";
    public string DiagnosticText => _diagnostic?.Explanation ?? "等待首次规则评估。";
    public string CurrentStateText => _diagnostic is null ? "当前状态：未知" : $"当前状态：{PresenceStateText(_diagnostic.CurrentState)}";
    public string CurrentDurationText => _diagnostic is null || !_diagnostic.HasProgress
        ? ""
        : $"当前连续：{FormatDuration(_diagnostic.CurrentDuration)}\n触发阈值：{DurationText}";
    public bool HasProgress => _diagnostic?.HasProgress == true;
    public double ProgressValue => (_diagnostic?.Progress ?? 0) * 100;
    public string ProgressText => HasProgress ? $"进度：{_diagnostic!.ProgressPercentage}%" : "";
    public string LastEvaluationText => FormatTimestamp(_diagnostic?.LastEvaluationAt, "最近评估");
    public string LastTriggeredText => FormatTimestamp(_diagnostic?.LastTriggeredAt, "最近触发");
    public string LastSentText => FormatTimestamp(_diagnostic?.LastSentAt, "最近发送");
    public string LastErrorText => string.IsNullOrWhiteSpace(_diagnostic?.LastError) ? "" : $"最近错误：{_diagnostic.LastError}";
    public string DiagnosticColor => _diagnostic?.Status switch
    {
        RuleEvaluationDiagnosticStatus.DeliveryFailed or RuleEvaluationDiagnosticStatus.RecipientBindingMissing => "#B45309",
        RuleEvaluationDiagnosticStatus.SubjectUnavailable or RuleEvaluationDiagnosticStatus.RecipientUnavailable => "#B91C1C",
        RuleEvaluationDiagnosticStatus.ThresholdReached or RuleEvaluationDiagnosticStatus.Delivered => "#16803A",
        RuleEvaluationDiagnosticStatus.AccumulatingDuration => "#2563EB",
        _ => "#64748B"
    };

    public void ApplyDiagnostic(RuleEvaluationDiagnostic diagnostic)
    {
        _diagnostic = diagnostic;
        Raise(nameof(Diagnostic));
        Raise(nameof(HasDiagnostic));
        Raise(nameof(DiagnosticTitle));
        Raise(nameof(DiagnosticText));
        Raise(nameof(CurrentStateText));
        Raise(nameof(CurrentDurationText));
        Raise(nameof(HasProgress));
        Raise(nameof(ProgressValue));
        Raise(nameof(ProgressText));
        Raise(nameof(LastEvaluationText));
        Raise(nameof(LastTriggeredText));
        Raise(nameof(LastSentText));
        Raise(nameof(LastErrorText));
        Raise(nameof(DiagnosticColor));
    }

    public void ApplyDiagnosticError(string error)
    {
        var now = DateTimeOffset.UtcNow;
        ApplyDiagnostic(new RuleEvaluationDiagnostic(
            Rule.Id, Rule.SubjectId, Rule.Condition, RuleEvaluationDiagnosticStatus.SubjectUnavailable,
            now, "诊断失败", $"规则诊断失败：{error}", LastError: error, LastEvaluationAt: now));
    }

    private static string PresenceStateText(PresenceState state) => state switch
    {
        PresenceState.Online => "在线",
        PresenceState.Offline => "离线",
        _ => "未知"
    };

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return "0分钟";
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}天{value.Hours}小时";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}小时{value.Minutes}分钟";
        return $"{Math.Max(0, (int)value.TotalMinutes)}分钟";
    }

    private static string FormatTimestamp(DateTimeOffset? value, string label) =>
        value is null ? $"{label}：暂无" : $"{label}：{value.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}

public sealed class NotificationDeliveryItemViewModel(
    NotificationDelivery delivery,
    string subjectName,
    string? recipientName = null,
    IReadOnlyDictionary<long, QqBotProfile>? profiles = null)
{
    public string CreatedText => delivery.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string SubjectName { get; } = subjectName;
    public string TargetText => string.IsNullOrWhiteSpace(recipientName)
        ? $"QQ {delivery.TargetType switch { NotificationTargetType.Private => "私聊", _ => "群聊" }} {NotificationSettingsViewModelFormatting.MaskTarget(delivery.TargetId)}"
        : recipientName!;
    public string BotText => delivery.BotProfileId is { } profileId && profiles?.GetValueOrDefault(profileId) is { } profile
        ? $"Bot：{(profile.IsLegacyUnknown ? "旧 Bot（AppID 未记录）" : NotificationSettingsViewModelFormatting.MaskTarget(profile.AppId))}"
        : "Bot：历史版本未记录";
    public string MessageText => delivery.Message.Replace('\n', ' ');
    public string StatusText => delivery.Status switch
    {
        NotificationDeliveryStatus.Delivered => "已发送",
        NotificationDeliveryStatus.Failed => "发送失败，等待重试",
        NotificationDeliveryStatus.PermanentFailed => "发送失败，需要处理",
        NotificationDeliveryStatus.Canceled => "已取消",
        NotificationDeliveryStatus.BindingRequired => "需要绑定",
        _ => "等待发送"
    };
    public string StatusColor => delivery.Status switch
    {
        NotificationDeliveryStatus.Delivered => "#16803A",
        NotificationDeliveryStatus.Failed => "#B45309",
        NotificationDeliveryStatus.PermanentFailed => "#B91C1C",
        _ => "#64748B"
    };
    public string ErrorText => string.IsNullOrWhiteSpace(delivery.Error) ? "" : delivery.Error!;
}

public sealed class SystemNotificationDeliveryItemViewModel(
    SystemNotificationDelivery delivery,
    string? recipientName = null,
    IReadOnlyDictionary<long, QqBotProfile>? profiles = null)
{
    public string CreatedText => delivery.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string SubjectName => delivery.Kind == SystemNotificationKind.XiaomiConnectionFailure ? "Xiaomi 连接异常" : "Xiaomi 连接恢复";
    public string MessageText => delivery.Message.Replace('\n', ' ');
    public string TargetText => string.IsNullOrWhiteSpace(recipientName)
        ? $"QQ {delivery.TargetType switch { NotificationTargetType.Private => "私聊", _ => "群聊" }} {NotificationSettingsViewModelFormatting.MaskTarget(delivery.TargetId)}"
        : recipientName!;
    public string BotText => delivery.BotProfileId is { } profileId && profiles?.GetValueOrDefault(profileId) is { } profile
        ? $"Bot：{(profile.IsLegacyUnknown ? "旧 Bot（AppID 未记录）" : NotificationSettingsViewModelFormatting.MaskTarget(profile.AppId))}"
        : "Bot：历史版本未记录";
    public string StatusText => delivery.Status switch { NotificationDeliveryStatus.Delivered => "已发送", NotificationDeliveryStatus.Failed => "发送失败，等待重试", NotificationDeliveryStatus.PermanentFailed => "发送失败，需要处理", NotificationDeliveryStatus.Canceled => "已取消", NotificationDeliveryStatus.BindingRequired => "需要绑定", _ => "等待发送" };
    public string StatusColor => delivery.Status switch { NotificationDeliveryStatus.Delivered => "#16803A", NotificationDeliveryStatus.Failed => "#B45309", NotificationDeliveryStatus.PermanentFailed => "#B91C1C", _ => "#64748B" };
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
