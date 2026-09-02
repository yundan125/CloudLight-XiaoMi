using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class XiaomiConnectionAlertService : IDisposable
{
    private readonly PresenceMonitor _monitor;
    private readonly IPresenceRepository _repository;
    private readonly INotificationDispatcher _dispatcher;
    private readonly Func<CancellationToken, Task<ConnectionAlertConfiguration>> _configurationProvider;
    private readonly INotificationDiagnostics _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public XiaomiConnectionAlertService(
        PresenceMonitor monitor,
        IPresenceRepository repository,
        INotificationDispatcher dispatcher,
        Func<CancellationToken, Task<ConnectionAlertConfiguration>> configurationProvider,
        bool subscribe = true,
        INotificationDiagnostics? diagnostics = null)
    {
        _monitor = monitor; _repository = repository; _dispatcher = dispatcher; _configurationProvider = configurationProvider; _diagnostics = diagnostics ?? NullNotificationDiagnostics.Instance;
        if (subscribe) _monitor.StatusChanged += MonitorStatusChanged;
    }

    public async Task ProcessStatusAsync(MonitorStatus status, CancellationToken cancellationToken)
    {
        if (_disposed || status.State is not (CloudConnectionState.ConfirmedUnavailable or CloudConnectionState.Connected)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await _repository.GetConnectionAlertStateAsync(cancellationToken);
            if (status.State == CloudConnectionState.ConfirmedUnavailable)
            {
                var now = DateTimeOffset.UtcNow;
                var episode = state?.FailureEpisodeId;
                if (string.IsNullOrWhiteSpace(episode))
                {
                    episode = Guid.NewGuid().ToString("N");
                    state = new ConnectionAlertState(episode, now, status.LastSuccessfulCloudUpdate, false, false, now);
                }
                else
                {
                    state ??= new ConnectionAlertState(episode, now, status.LastSuccessfulCloudUpdate, false, false, now);
                    state = state with { LastSuccessfulCloudUpdateAt = status.LastSuccessfulCloudUpdate ?? state.LastSuccessfulCloudUpdateAt, UpdatedAt = now };
                }

                var configuration = await _configurationProvider(cancellationToken);
                var deliveries = new List<SystemNotificationDelivery>();
                if (!state.FailureAlertSent && configuration.Settings.Enabled)
                {
                    foreach (var target in await ResolveTargetsAsync(configuration, cancellationToken))
                    {
                        deliveries.Add(await _repository.CreateSystemNotificationDeliveryAsync(new SystemNotificationDelivery(
                            0, SystemNotificationKind.XiaomiConnectionFailure, episode, now,
                            target.BindingMissing ? NotificationDeliveryStatus.BindingRequired : NotificationDeliveryStatus.Pending,
                            null, NotificationChannelType.QQ, target.TargetType, target.TargetId,
                            FailureMessage(status.RouterName, state.LastSuccessfulCloudUpdateAt, now),
                            target.BindingMissing ? "当前 QQ Bot 尚未绑定此联系人。" : null, 0, 0, null,
                            target.BindingMissing ? null : now, target.RecipientId, target.BotProfileId, target.BindingId), cancellationToken));
                    }
                    if (deliveries.Count > 0) state = state with { FailureAlertSent = true, UpdatedAt = now };
                }
                await _repository.UpsertConnectionAlertStateAsync(state with { UpdatedAt = now }, cancellationToken);
                foreach (var delivery in deliveries) await _dispatcher.DispatchSystemAsync(delivery, cancellationToken);
                return;
            }

            if (state?.FailureEpisodeId is not { Length: > 0 } episodeId) return;
            var connectedAt = DateTimeOffset.UtcNow;
            var recoveries = new List<SystemNotificationDelivery>();
            var recoveryConfiguration = await _configurationProvider(cancellationToken);
            if (!state.RecoveryAlertSent && recoveryConfiguration.Settings.RecoveryEnabled)
            {
                foreach (var target in await ResolveTargetsAsync(recoveryConfiguration, cancellationToken))
                {
                    recoveries.Add(await _repository.CreateSystemNotificationDeliveryAsync(new SystemNotificationDelivery(
                        0, SystemNotificationKind.XiaomiConnectionRecovery, episodeId, connectedAt,
                        target.BindingMissing ? NotificationDeliveryStatus.BindingRequired : NotificationDeliveryStatus.Pending,
                        null, NotificationChannelType.QQ, target.TargetType, target.TargetId,
                        RecoveryMessage(status.RouterName, connectedAt),
                        target.BindingMissing ? "当前 QQ Bot 尚未绑定此联系人。" : null, 0, 0, null,
                        target.BindingMissing ? null : connectedAt, target.RecipientId, target.BotProfileId, target.BindingId), cancellationToken));
                }
                if (recoveries.Count > 0) state = state with { RecoveryAlertSent = true };
            }
            await _repository.UpsertConnectionAlertStateAsync(state with { FailureEpisodeId = null, FailureStartedAt = null, LastSuccessfulCloudUpdateAt = status.LastSuccessfulCloudUpdate ?? state.LastSuccessfulCloudUpdateAt, FailureAlertSent = false, RecoveryAlertSent = false, UpdatedAt = connectedAt }, cancellationToken);
            foreach (var recovery in recoveries) await _dispatcher.DispatchSystemAsync(recovery, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.StatusChanged -= MonitorStatusChanged;
        _gate.Dispose();
    }

    private async void MonitorStatusChanged(object? sender, MonitorStatus status)
    {
        try
        {
            await ProcessStatusAsync(status, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await _diagnostics.RecordAsync("system_alert", exception, null, null, CancellationToken.None);
        }
    }

    private async Task<IReadOnlyList<NotificationRecipientTarget>> ResolveTargetsAsync(ConnectionAlertConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.Settings.UseDefaultTarget)
        {
            if (configuration.DefaultTargets is { Count: > 0 }) return configuration.DefaultTargets;
            if (configuration.CurrentBotAppId is not null) return [];
            return IsValidTarget(configuration.DefaultTargetId)
                ? [new(null, configuration.DefaultTargetType, configuration.DefaultTargetId.Trim())]
                : [];
        }

        if (configuration.Settings.RecipientIds.Count > 0)
        {
            var result = new List<NotificationRecipientTarget>();
            foreach (var recipientId in configuration.Settings.RecipientIds.Distinct())
                if (await _repository.GetNotificationRecipientAsync(recipientId, cancellationToken) is { } recipient)
                {
                    var binding = configuration.CurrentBotProfileId is { } profileId
                        ? await _repository.GetNotificationRecipientBotBindingAsync(recipient.Id, profileId, cancellationToken)
                        : null;
                    result.Add(binding is null
                        ? new(recipient.Id, recipient.TargetType, string.Empty, recipient.DisplayName,
                            configuration.CurrentBotProfileId, BindingMissing: configuration.CurrentBotAppId is not null)
                        : new(recipient.Id, binding.TargetType, binding.OpenId, recipient.DisplayName,
                            configuration.CurrentBotProfileId, binding.Id, MaskedTargetId: MaskTarget(binding.OpenId)));
                }
            if (result.Count > 0) return result;
        }

        if (configuration.CurrentBotAppId is not null) return [];
        return IsValidTarget(configuration.Settings.TargetId)
            ? [new(null, configuration.Settings.TargetType, configuration.Settings.TargetId.Trim())]
            : [];
    }

    private static bool IsValidTarget(string value) => value.Trim() is { Length: > 0 and <= 256 } target && !target.Any(char.IsWhiteSpace);

    private static string MaskTarget(string value) => value.Length <= 6 ? value : $"{value[..3]}****{value[^3..]}";

    private static string FailureMessage(string? routerName, DateTimeOffset? lastSuccessfulUpdate, DateTimeOffset now) =>
        $"CloudLight XiaoMi 无法连接 Xiaomi 服务。\n\n路由器：{Value(routerName, "当前路由器")}\n最后成功更新：{NotificationTemplateRenderer.FormatTime(lastSuccessfulUpdate)}\n异常时间：{NotificationTemplateRenderer.FormatTime(now)}\n\n设备在线状态可能暂时无法更新。";

    private static string RecoveryMessage(string? routerName, DateTimeOffset now) =>
        $"CloudLight XiaoMi 已恢复连接 Xiaomi 服务。\n\n路由器：{Value(routerName, "当前路由器")}\n恢复时间：{NotificationTemplateRenderer.FormatTime(now)}";

    private static string Value(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
