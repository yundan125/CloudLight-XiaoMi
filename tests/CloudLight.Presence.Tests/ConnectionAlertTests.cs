using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class ConnectionAlertTests
{
    [Fact]
    public async Task ConnectionEpisodeAlertsOnceRecoversOnceQueuesWhileQqIsOfflineAndClaimsConcurrently()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Connection-Alert-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var monitor = new PresenceMonitor(new FakeSource(), repository, new PresenceStateMachine(repository));
            var channel = new FakeChannel(connected: false);
            using var dispatcher = new NotificationDispatcher(repository, [channel]);
            using var alerts = new XiaomiConnectionAlertService(monitor, repository, dispatcher, _ => Task.FromResult(new ConnectionAlertConfiguration(new ConnectionAlertSettings(true, true, false, NotificationTargetType.Private, "target-openid"), NotificationTargetType.Private, "")), subscribe: false);
            var failedStatus = new MonitorStatus(CloudConnectionState.ConfirmedUnavailable, null, "cloud unavailable", DateTimeOffset.UtcNow.AddMinutes(-2), "测试路由器");

            await alerts.ProcessStatusAsync(new MonitorStatus(CloudConnectionState.Connected, failedStatus.LastUpdate, null, failedStatus.LastSuccessfulCloudUpdate, "测试路由器"), CancellationToken.None);
            Assert.Empty(await repository.GetRecentSystemNotificationDeliveriesAsync(10, CancellationToken.None));

            await alerts.ProcessStatusAsync(failedStatus, CancellationToken.None);
            await alerts.ProcessStatusAsync(failedStatus, CancellationToken.None);
            var failure = Assert.Single(await repository.GetRecentSystemNotificationDeliveriesAsync(10, CancellationToken.None));
            Assert.Equal(SystemNotificationKind.XiaomiConnectionFailure, failure.Kind);
            Assert.Equal(NotificationDeliveryStatus.Failed, failure.Status);
            Assert.Equal(1, channel.SendCount);

            channel.SetConnected(true);
            await dispatcher.RetryPendingAsync(DateTimeOffset.UtcNow.AddMinutes(2), CancellationToken.None);
            failure = Assert.Single(await repository.GetRecentSystemNotificationDeliveriesAsync(10, CancellationToken.None));
            Assert.Equal(NotificationDeliveryStatus.Delivered, failure.Status);
            Assert.Equal(2, channel.SendCount);

            await alerts.ProcessStatusAsync(new MonitorStatus(CloudConnectionState.Connected, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, "测试路由器"), CancellationToken.None);
            var deliveries = await repository.GetRecentSystemNotificationDeliveriesAsync(10, CancellationToken.None);
            var recovery = Assert.Single(deliveries, value => value.Kind == SystemNotificationKind.XiaomiConnectionRecovery);
            Assert.Equal(NotificationDeliveryStatus.Delivered, recovery.Status);
            Assert.Equal(3, channel.SendCount);
            await alerts.ProcessStatusAsync(new MonitorStatus(CloudConnectionState.Connected, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, "测试路由器"), CancellationToken.None);
            Assert.Equal(2, (await repository.GetRecentSystemNotificationDeliveriesAsync(10, CancellationToken.None)).Count);

            var newFailure = new MonitorStatus(CloudConnectionState.ConfirmedUnavailable, null, "cloud unavailable again", DateTimeOffset.UtcNow, "测试路由器");
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => alerts.ProcessStatusAsync(newFailure, CancellationToken.None)));
            deliveries = await repository.GetRecentSystemNotificationDeliveriesAsync(10, CancellationToken.None);
            Assert.Equal(3, deliveries.Count);
            Assert.Equal(2, deliveries.Count(value => value.Kind == SystemNotificationKind.XiaomiConnectionFailure));
            Assert.Equal(2, deliveries.Select(value => value.EpisodeId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, channel.SendCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeSource : IXiaomiPresenceSource
    {
        public bool HasStoredLogin => false;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
    }

    private sealed class FakeChannel(bool connected) : INotificationChannel
    {
        private NotificationChannelStatus _status = new(NotificationChannelType.QQ, true, connected, connected, connected ? NotificationConnectionState.Connected : NotificationConnectionState.Reconnecting);
        public NotificationChannelType ChannelType => NotificationChannelType.QQ;
        public NotificationChannelStatus Status => _status;
        public int SendCount { get; private set; }
        public event EventHandler<NotificationChannelStatus>? StatusChanged;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(_status.Connected
                ? new NotificationSendResult(true, 1, 1)
                : new NotificationSendResult(false, 0, 0, "QQ 当前未连接。"));
        }
        public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) => Task.FromResult(new NotificationSendResult(_status.Connected, _status.Connected ? 1 : 0, 1));
        public void SetConnected(bool connected)
        {
            _status = _status with { Running = connected, Connected = connected, ConnectionState = connected ? NotificationConnectionState.Connected : NotificationConnectionState.Reconnecting };
            StatusChanged?.Invoke(this, _status);
        }
    }
}
