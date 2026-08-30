using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Notifications;
using CloudLight.Presence.Infrastructure.SecureStorage;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

/// <summary>
/// A deliberately opt-in live verification. It never changes the user's
/// database or rules: all generated subjects/rules/deliveries live under an
/// isolated temporary AppPaths root and are removed in finally.
/// </summary>
public sealed class LiveQqNotificationE2ETests
{
    private const string OptInVariable = "CLOUDLIGHT_RUN_QQ_E2E";

    [Fact]
    public async Task OptionalLiveQqRuntimeDeliversContinuousAndConfirmedEventRules()
    {
        // Keeping the external service call opt-in lets the normal test suite
        // remain offline and prevents accidental messages in CI.
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal)) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var userPaths = new AppPaths();
        var qq = (await new JsonSettingsStore(userPaths).LoadAsync(token)).Qq;
        var secret = await new DpapiQqSecretStore(userPaths).LoadAsync(token);
        if (!qq.Enabled || string.IsNullOrWhiteSpace(qq.DefaultTargetId) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("真实 QQ E2E 需要已启用的 QQ 配置、默认接收目标和已保存的 AppSecret。 ");

        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Live-QQ-E2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        QQNotificationChannel? channel = null;
        NotificationRuntime? runtime = null;
        NotificationDispatcher? dispatcher = null;
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(token);
            var now = DateTimeOffset.UtcNow;
            var router = await repository.UpsertRouterAsync(new Router(
                0, $"qq-e2e-{Guid.NewGuid():N}", "test.router", $"qq-e2e-partner-{Guid.NewGuid():N}",
                "QQ E2E 隔离路由器", null, null, now, now), token);
            var continuousDevice = await InsertDeviceAsync(repository, router, "AA:BB:CC:DD:EE:01", "QQ E2E 连续在线", now, token);
            var eventDevice = await InsertDeviceAsync(repository, router, "AA:BB:CC:DD:EE:02", "QQ E2E 事件主体", now, token);
            var continuous = await repository.CreateSubjectAsync("QQ E2E 连续在线", null, Guid.NewGuid(), now, token);
            await repository.SetSubjectDevicesAsync(continuous.Id, [continuousDevice.Id], now, token);
            var events = await repository.CreateSubjectAsync("QQ E2E 事件主体", null, Guid.NewGuid(), now, token);
            await repository.SetSubjectDevicesAsync(events.Id, [eventDevice.Id], now, token);

            var createdAt = now.AddMinutes(-6);
            await repository.CreateNotificationRuleAsync(Rule(continuous.Id, NotificationCondition.OnlineFor, 60,
                "[CloudLight 自动提醒 E2E] 连续在线：{name}，{duration}", qq, createdAt), token);
            await repository.CreateNotificationRuleAsync(Rule(events.Id, NotificationCondition.DetectedOnline, 0,
                "[CloudLight 自动提醒 E2E] 检测到上线：{name}，{currentTime}", qq, createdAt), token);
            await repository.CreateNotificationRuleAsync(Rule(events.Id, NotificationCondition.DetectedOffline, 0,
                "[CloudLight 自动提醒 E2E] 检测到离线：{name}，{currentTime}", qq, createdAt), token);

            var machine = new PresenceStateMachine(repository);
            // Evaluate while the continuous subject is still online.  The
            // final offline snapshot belongs only to the event subject; if
            // this were evaluated only after all snapshots, a continuous
            // online rule would correctly have nothing to send.
            await ApplyAsync(machine, router.Id, continuousDevice, true, eventDevice, false, now.AddMinutes(-5), token);

            channel = new QQNotificationChannel(paths.LogsDirectory);
            await channel.ConfigureAsync(qq, secret, token);
            await channel.StartAsync(token);
            await WaitForConnectionAsync(channel, token);

            dispatcher = new NotificationDispatcher(repository, [channel]);
            var presence = new SubjectPresenceService(repository, new PresenceStatisticsService(repository));
            var monitor = new PresenceMonitor(new EmptySource(), repository, machine);
            runtime = new NotificationRuntime(monitor, new NotificationRuleService(repository, presence), dispatcher);
            await runtime.StartAsync(token);
            await runtime.EvaluateAndDispatchAsync(token);

            await ApplyAsync(machine, router.Id, continuousDevice, true, eventDevice, true, now.AddMinutes(-4), token);
            await runtime.EvaluateAndDispatchAsync(token);
            await ApplyAsync(machine, router.Id, continuousDevice, true, eventDevice, false, now.AddMinutes(-3), token);
            await ApplyAsync(machine, router.Id, continuousDevice, true, eventDevice, false,
                now.AddMinutes(-3).Add(SubjectPresenceService.DefaultOfflineGracePeriod), token);
            await runtime.EvaluateAndDispatchAsync(token);

            var deliveries = await repository.GetRecentNotificationDeliveriesAsync(10, token);
            Assert.Equal(3, deliveries.Count);
            Assert.All(deliveries, value => Assert.Equal(NotificationDeliveryStatus.Delivered, value.Status));
            Assert.Contains(deliveries, value => value.Message.Contains("连续在线", StringComparison.Ordinal));
            Assert.Contains(deliveries, value => value.Message.Contains("检测到上线", StringComparison.Ordinal));
            Assert.Contains(deliveries, value => value.Message.Contains("检测到离线", StringComparison.Ordinal));
        }
        finally
        {
            if (runtime is not null) await runtime.DisposeAsync();
            dispatcher?.Dispose();
            if (channel is not null) await channel.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static NotificationRule Rule(long subjectId, NotificationCondition condition, long thresholdSeconds, string template, QqNotificationSettings qq, DateTimeOffset createdAt) =>
        new(0, subjectId, true, condition, thresholdSeconds, NotificationChannelType.QQ, qq.DefaultTargetType,
            qq.DefaultTargetId.Trim(), template, createdAt, createdAt);

    private static async Task<NetworkDevice> InsertDeviceAsync(SqlitePresenceRepository repository, Router router, string mac, string name, DateTimeOffset now, CancellationToken token) =>
        await repository.InsertDeviceAsync(new NetworkDevice(0, router.Id, mac, name, name, null, null, "192.168.1.2", "5G", -45,
            PresenceState.Offline, now.AddMinutes(-10), now, now), token);

    private static Task ApplyAsync(
        PresenceStateMachine machine,
        long routerId,
        NetworkDevice continuous,
        bool continuousOnline,
        NetworkDevice events,
        bool eventsOnline,
        DateTimeOffset observedAt,
        CancellationToken token) =>
        machine.ApplySnapshotAsync(routerId,
        [
            new ObservedNetworkDevice(continuous.MacAddress, continuous.OriginalName, continuous.OriginName, continuous.LastIp, continuousOnline, null, continuous.ConnectionType, continuous.Signal),
            new ObservedNetworkDevice(events.MacAddress, events.OriginalName, events.OriginName, events.LastIp, eventsOnline, null, events.ConnectionType, events.Signal)
        ], observedAt, token);

    private static async Task WaitForConnectionAsync(QQNotificationChannel channel, CancellationToken token)
    {
        while (!channel.Status.Connected)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    private sealed class EmptySource : IXiaomiPresenceSource
    {
        public bool HasStoredLogin => false;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
    }
}
