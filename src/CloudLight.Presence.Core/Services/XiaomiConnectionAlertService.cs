using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class XiaomiConnectionAlertService : IDisposable
{
    private readonly PresenceMonitor _monitor;
    private readonly IPresenceRepository _repository;
    private readonly INotificationDispatcher _dispatcher;
    private readonly Func<CancellationToken, Task<ConnectionAlertConfiguration>> _configurationProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public XiaomiConnectionAlertService(
        PresenceMonitor monitor,
        IPresenceRepository repository,
        INotificationDispatcher dispatcher,
        Func<CancellationToken, Task<ConnectionAlertConfiguration>> configurationProvider,
        bool subscribe = true)
    {
        _monitor = monitor; _repository = repository; _dispatcher = dispatcher; _configurationProvider = configurationProvider;
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

                SystemNotificationDelivery? delivery = null;
                var configuration = await _configurationProvider(cancellationToken);
                if (!state.FailureAlertSent && configuration.Settings.Enabled && TryResolveTarget(configuration, out var targetType, out var targetId))
                {
                    delivery = await _repository.CreateSystemNotificationDeliveryAsync(new SystemNotificationDelivery(
                        0, SystemNotificationKind.XiaomiConnectionFailure, episode, now, NotificationDeliveryStatus.Pending, null,
                        NotificationChannelType.QQ, targetType, targetId, FailureMessage(status.RouterName, state.LastSuccessfulCloudUpdateAt, now), null, 0, 0, null, now), cancellationToken);
                    state = state with { FailureAlertSent = true, UpdatedAt = now };
                }
                await _repository.UpsertConnectionAlertStateAsync(state with { UpdatedAt = now }, cancellationToken);
                if (delivery is not null) await _dispatcher.DispatchSystemAsync(delivery, cancellationToken);
                return;
            }

            if (state?.FailureEpisodeId is not { Length: > 0 } episodeId) return;
            var connectedAt = DateTimeOffset.UtcNow;
            SystemNotificationDelivery? recovery = null;
            var recoveryConfiguration = await _configurationProvider(cancellationToken);
            if (!state.RecoveryAlertSent && recoveryConfiguration.Settings.RecoveryEnabled && TryResolveTarget(recoveryConfiguration, out var recoveryTargetType, out var recoveryTargetId))
            {
                recovery = await _repository.CreateSystemNotificationDeliveryAsync(new SystemNotificationDelivery(
                    0, SystemNotificationKind.XiaomiConnectionRecovery, episodeId, connectedAt, NotificationDeliveryStatus.Pending, null,
                    NotificationChannelType.QQ, recoveryTargetType, recoveryTargetId, RecoveryMessage(status.RouterName, connectedAt), null, 0, 0, null, connectedAt), cancellationToken);
                state = state with { RecoveryAlertSent = true };
            }
            await _repository.UpsertConnectionAlertStateAsync(state with { FailureEpisodeId = null, FailureStartedAt = null, LastSuccessfulCloudUpdateAt = status.LastSuccessfulCloudUpdate ?? state.LastSuccessfulCloudUpdateAt, FailureAlertSent = false, RecoveryAlertSent = false, UpdatedAt = connectedAt }, cancellationToken);
            if (recovery is not null) await _dispatcher.DispatchSystemAsync(recovery, cancellationToken);
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
        try { await ProcessStatusAsync(status, CancellationToken.None); } catch { }
    }

    private static bool TryResolveTarget(ConnectionAlertConfiguration configuration, out NotificationTargetType targetType, out string targetId)
    {
        targetType = configuration.Settings.UseDefaultTarget ? configuration.DefaultTargetType : configuration.Settings.TargetType;
        targetId = (configuration.Settings.UseDefaultTarget ? configuration.DefaultTargetId : configuration.Settings.TargetId).Trim();
        return targetId.Length > 0 && targetId.Length <= 256 && !targetId.Any(char.IsWhiteSpace);
    }

    private static string FailureMessage(string? routerName, DateTimeOffset? lastSuccessfulUpdate, DateTimeOffset now) =>
        $"CloudLight XiaoMi 无法连接 Xiaomi 服务。\n\n路由器：{Value(routerName, "当前路由器")}\n最后成功更新：{NotificationTemplateRenderer.FormatTime(lastSuccessfulUpdate)}\n异常时间：{NotificationTemplateRenderer.FormatTime(now)}\n\n设备在线状态可能暂时无法更新。";

    private static string RecoveryMessage(string? routerName, DateTimeOffset now) =>
        $"CloudLight XiaoMi 已恢复连接 Xiaomi 服务。\n\n路由器：{Value(routerName, "当前路由器")}\n恢复时间：{NotificationTemplateRenderer.FormatTime(now)}";

    private static string Value(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
