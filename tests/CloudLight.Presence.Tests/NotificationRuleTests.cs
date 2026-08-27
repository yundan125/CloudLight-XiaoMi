using System.Net;
using System.Net.Http;
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

public sealed class NotificationRuleTests
{
    [Fact]
    public async Task OfflineThresholdSendsOnceAndSurvivesRestart()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var offlineAt = fixture.Start.AddHours(2);
            await fixture.ApplyAsync(true, fixture.Start.AddHours(1));
            await fixture.ApplyAsync(false, offlineAt);
            var rule = await fixture.Repository.CreateNotificationRuleAsync(Rule(fixture.Subject.Id, NotificationCondition.OfflineFor, 14 * 60 * 60), CancellationToken.None);
            var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
            var channel = new FakeChannel(true); using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);

            Assert.Empty(await service.EvaluateAsync(offlineAt.AddHours(13).AddMinutes(59), CancellationToken.None));
            var first = await service.EvaluateAsync(offlineAt.AddHours(14), CancellationToken.None);
            Assert.Single(first); await dispatcher.DispatchAsync(first[0], CancellationToken.None);
            var delivery = Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None));
            Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
            Assert.Empty(await service.EvaluateAsync(offlineAt.AddHours(20), CancellationToken.None));

            var reopened = new SqlitePresenceRepository(new AppPaths(fixture.Root)); await reopened.InitializeAsync(CancellationToken.None);
            var reopenedPresence = new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened));
            Assert.Empty(await new NotificationRuleService(reopened, reopenedPresence).EvaluateAsync(offlineAt.AddHours(20), CancellationToken.None));
            Assert.Equal(rule.Id, (await reopened.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None))[0].RuleId);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task OnlineThresholdResetsAfterOfflineAndCanTriggerAgain()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var onlineAt = fixture.Start.AddHours(1); await fixture.ApplyAsync(true, onlineAt);
            var rule = await fixture.Repository.CreateNotificationRuleAsync(Rule(fixture.Subject.Id, NotificationCondition.OnlineFor, 2 * 60 * 60), CancellationToken.None);
            var service = new NotificationRuleService(fixture.Repository, fixture.Presence); var channel = new FakeChannel(true); using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
            var first = await service.EvaluateAsync(onlineAt.AddHours(2), CancellationToken.None); Assert.Single(first); await dispatcher.DispatchAsync(first[0], CancellationToken.None);
            await fixture.ApplyAsync(false, onlineAt.AddHours(3)); Assert.Empty(await service.EvaluateAsync(onlineAt.AddHours(3).AddMinutes(1), CancellationToken.None));
            await fixture.ApplyAsync(true, onlineAt.AddHours(4));
            var second = await service.EvaluateAsync(onlineAt.AddHours(6), CancellationToken.None); Assert.Single(second); Assert.NotEqual(first[0].EpisodeId, second[0].EpisodeId);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task MonitoringGapBreaksContinuousDuration()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var onlineAt = fixture.Start.AddHours(1); await fixture.ApplyAsync(true, onlineAt);
            var gapStart = onlineAt.AddHours(1); var gapEnd = gapStart.AddHours(8); var gap = await fixture.Repository.StartMonitoringGapAsync(gapStart, "test", CancellationToken.None); await fixture.Repository.EndMonitoringGapAsync(gap, gapEnd, CancellationToken.None);
            var rule = await fixture.Repository.CreateNotificationRuleAsync(Rule(fixture.Subject.Id, NotificationCondition.OnlineFor, 2 * 60 * 60), CancellationToken.None);
            var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
            var duringGap = await service.EvaluateAsync(gapStart.AddHours(1), CancellationToken.None); Assert.Empty(duringGap);
            Assert.Empty(await service.EvaluateAsync(gapEnd.AddHours(1), CancellationToken.None));
            Assert.Empty(await service.EvaluateAsync(gapEnd.AddHours(2), CancellationToken.None));
            await fixture.ApplyAsync(false, gapEnd.AddHours(2)); await fixture.ApplyAsync(true, gapEnd.AddHours(2).AddMinutes(1));
            var afterRecovery = await service.EvaluateAsync(gapEnd.AddHours(4).AddMinutes(1), CancellationToken.None); Assert.Single(afterRecovery); Assert.NotEqual($"{(int)PresenceState.Online}:{onlineAt.UtcTicks}", afterRecovery[0].EpisodeId);
            Assert.Equal(rule.Id, afterRecovery[0].RuleId);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task DisconnectedChannelKeepsPendingDeliveryAndSendsOnceAfterReconnect()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var offlineAt = fixture.Start.AddHours(1); await fixture.ApplyAsync(true, fixture.Start.AddMinutes(30)); await fixture.ApplyAsync(false, offlineAt);
            await fixture.Repository.CreateNotificationRuleAsync(Rule(fixture.Subject.Id, NotificationCondition.OfflineFor, 60), CancellationToken.None);
            var service = new NotificationRuleService(fixture.Repository, fixture.Presence); var channel = new FakeChannel(false); using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
            var requests = await service.EvaluateAsync(offlineAt.AddMinutes(1), CancellationToken.None); Assert.Single(requests); await dispatcher.DispatchAsync(requests[0], CancellationToken.None);
            var failed = Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None)); Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status); Assert.NotNull((await fixture.Repository.GetNotificationRuleStateAsync(requests[0].RuleId, CancellationToken.None))!.PendingDeliveryId);
            channel.SetConnected(true); await dispatcher.RetryPendingAsync(failed.NextAttemptAt!.Value.AddSeconds(1), CancellationToken.None);
            var delivered = Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None)); Assert.Equal(NotificationDeliveryStatus.Delivered, delivered.Status); Assert.Equal(2, channel.SendCount);
            await dispatcher.RetryPendingAsync(offlineAt.AddHours(1), CancellationToken.None); Assert.Equal(2, channel.SendCount);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task RuleAdministrationPersistsToggleSemanticResetAndDeleteKeepsHistory()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await fixture.ApplyAsync(true, fixture.Start.AddMinutes(1));
            await fixture.ApplyAsync(false, fixture.Start.AddMinutes(2));
            var rule = await fixture.Repository.CreateNotificationRuleAsync(Rule(fixture.Subject.Id, NotificationCondition.OfflineFor, 60), CancellationToken.None);
            var administration = new NotificationRuleAdministrationService(fixture.Repository);
            var ruleService = new NotificationRuleService(fixture.Repository, fixture.Presence);

            await administration.DisableRuleAsync(rule.Id, CancellationToken.None);
            Assert.False((await fixture.Repository.GetNotificationRuleAsync(rule.Id, CancellationToken.None))!.Enabled);
            Assert.Empty(await fixture.Repository.GetNotificationRulesAsync(true, CancellationToken.None));
            Assert.Empty(await ruleService.EvaluateAsync(fixture.Start.AddMinutes(10), CancellationToken.None));

            await administration.EnableRuleAsync(rule.Id, CancellationToken.None);
            Assert.True((await fixture.Repository.GetNotificationRuleAsync(rule.Id, CancellationToken.None))!.Enabled);
            var triggered = await ruleService.EvaluateAsync(fixture.Start.AddMinutes(10), CancellationToken.None);
            Assert.Single(triggered);

            var oldState = new NotificationRuleState(rule.Id, "old-episode", fixture.Start.AddMinutes(2), true, fixture.Start.AddMinutes(10), true, triggered[0].DeliveryId, "waiting", fixture.Start.AddMinutes(10));
            await fixture.Repository.UpsertNotificationRuleStateAsync(oldState, CancellationToken.None);
            var oldDelivery = await fixture.Repository.GetNotificationDeliveryAsync(triggered[0].DeliveryId, CancellationToken.None);
            Assert.NotNull(oldDelivery);
            await administration.UpdateRuleAsync(rule with { Condition = NotificationCondition.OnlineFor, ThresholdSeconds = 120 }, CancellationToken.None);
            var reset = await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);
            Assert.NotNull(reset);
            Assert.Null(reset!.CurrentEpisodeId);
            Assert.False(reset.TriggeredForCurrentEpisode);
            Assert.Equal(NotificationDeliveryStatus.Canceled, (await fixture.Repository.GetNotificationDeliveryAsync(triggered[0].DeliveryId, CancellationToken.None))!.Status);

            await fixture.Repository.UpsertNotificationRuleStateAsync(reset with { CurrentEpisodeId = "keep", StateSince = fixture.Start, TriggeredForCurrentEpisode = true, TriggeredAt = fixture.Start.AddMinutes(11) }, CancellationToken.None);
            await administration.UpdateRuleAsync((await fixture.Repository.GetNotificationRuleAsync(rule.Id, CancellationToken.None))! with { MessageTemplate = "changed {name}" }, CancellationToken.None);
            var kept = await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);
            Assert.Equal("keep", kept!.CurrentEpisodeId);
            Assert.True(kept.TriggeredForCurrentEpisode);
            Assert.Equal("changed {name}", (await fixture.Repository.GetNotificationRuleAsync(rule.Id, CancellationToken.None))!.MessageTemplate);

            await administration.DeleteRuleAsync(rule.Id, CancellationToken.None);
            Assert.Null(await fixture.Repository.GetNotificationRuleAsync(rule.Id, CancellationToken.None));
            Assert.Null(await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None));
            var history = Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None));
            Assert.Null(history.RuleId);
            Assert.Equal(fixture.Subject.Id, history.SubjectId);
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public void MessageSplitterUsesUnicodeScalarLimitAndBridgeStylePrefixes()
    {
        var parts = QQMessageSplitter.Split(string.Concat(Enumerable.Repeat("😀在线。", 2000)), 5000);
        Assert.True(parts.Count > 1);
        Assert.All(parts, value => Assert.True(value.EnumerateRunes().Count() <= 5000));
        Assert.StartsWith("[1/", parts[0], StringComparison.Ordinal);
        Assert.All(parts, value => Assert.False(HasUnpairedSurrogate(value)));
    }

    [Fact]
    public async Task MergeMovesRulesAndSplitDoesNotCopyRulesToNewSubject()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root)); await repository.InitializeAsync(CancellationToken.None); var now = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero); var router = await repository.UpsertRouterAsync(new(0, "notification-did", "router", "notification-partner", "客厅路由器", null, null, now, now), CancellationToken.None);
            var a = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:01", now), CancellationToken.None); var b = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:02", now), CancellationToken.None);
            var source = await repository.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), now, CancellationToken.None); await repository.SetSubjectDevicesAsync(source.Id, [a.Id], now, CancellationToken.None); var target = await repository.CreateSubjectAsync("家人", null, Guid.NewGuid(), now, CancellationToken.None); await repository.SetSubjectDevicesAsync(target.Id, [b.Id], now, CancellationToken.None);
            var sourceRule = await repository.CreateNotificationRuleAsync(Rule(source.Id, NotificationCondition.OfflineFor, 60, "source"), CancellationToken.None); await repository.CreateNotificationRuleAsync(Rule(target.Id, NotificationCondition.OfflineFor, 60, "target"), CancellationToken.None); var sourceDelivery = await repository.CreateNotificationDeliveryAsync(new NotificationDelivery(0, sourceRule.Id, source.Id, "source-episode", now, NotificationDeliveryStatus.Failed, null, NotificationChannelType.QQ, NotificationTargetType.Private, "source", "source message", "waiting", 0, 0, now, now.AddMinutes(1)), CancellationToken.None);
            await repository.MergeSubjectsAsync(source.Id, target.Id, now.AddMinutes(1), CancellationToken.None);
            Assert.Null(await repository.GetSubjectAsync(source.Id, CancellationToken.None)); Assert.Equal(2, (await repository.GetSubjectDevicesAsync(target.Id, CancellationToken.None)).Count); Assert.Equal(2, (await repository.GetNotificationRulesAsync(false, CancellationToken.None)).Count); Assert.All(await repository.GetNotificationRulesAsync(false, CancellationToken.None), value => Assert.Equal(target.Id, value.SubjectId)); Assert.Equal(target.Id, (await repository.GetNotificationDeliveryAsync(sourceDelivery.Id, CancellationToken.None))!.SubjectId);

            await repository.SetSubjectDevicesAsync(target.Id, [a.Id], now.AddMinutes(2), CancellationToken.None);
            var standalone = Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None), value => value.Id != target.Id); Assert.Single(await repository.GetSubjectDevicesAsync(standalone.Id, CancellationToken.None)); Assert.Contains(await repository.GetNotificationRulesAsync(false, CancellationToken.None), value => value.SubjectId == target.Id); Assert.DoesNotContain(await repository.GetNotificationRulesAsync(false, CancellationToken.None), value => value.SubjectId == standalone.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task QqAppSecretUsesDpapiAndIsNotWrittenToJsonSettings()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root); var store = new DpapiQqSecretStore(paths); const string secret = "qq-app-secret-test-value";
            await store.SaveAsync(secret, CancellationToken.None);
            Assert.Equal(secret, await store.LoadAsync(CancellationToken.None)); Assert.NotEqual(secret, await File.ReadAllTextAsync(store.SecretPath));
            var settings = new JsonSettingsStore(paths); var expected = new QqNotificationSettings(true, false, "123456789", false, "custom-http", "http://127.0.0.1:7897", NotificationTargetType.Group, "group-openid"); await settings.SaveAsync(new PresenceSettings { Qq = expected }, CancellationToken.None); var json = await File.ReadAllTextAsync(paths.SettingsPath);
            Assert.Equal(expected, (await settings.LoadAsync(CancellationToken.None)).Qq); Assert.DoesNotContain(secret, json, StringComparison.Ordinal); Assert.Contains("123456789", json, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task QqAutoconnectFailureKeepsEnabledSettingsAndWritesSafeLog()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root); var settings = new JsonSettingsStore(paths); var expected = new QqNotificationSettings(true, true, "123456789", false, "direct", "", NotificationTargetType.Private, "user-openid"); await settings.SaveAsync(new PresenceSettings { Qq = expected }, CancellationToken.None);
            var secret = "qq-secret-that-must-not-be-logged"; var store = new DpapiQqSecretStore(paths); await store.SaveAsync(secret, CancellationToken.None); using var handler = new FailingHttpHandler(); var channel = new QQNotificationChannel(paths.LogsDirectory, handler);
            try
            {
                var failed = new TaskCompletionSource<NotificationChannelStatus>(TaskCreationOptions.RunContinuationsAsynchronously); channel.StatusChanged += (_, status) => { if (status.ConnectionState == NotificationConnectionState.GatewayFailed) failed.TrySetResult(status); };
                await channel.ConfigureAsync(expected, await store.LoadAsync(CancellationToken.None), CancellationToken.None); await channel.StartAsync(CancellationToken.None); var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(status.Configured); Assert.False(status.Connected); Assert.Equal(NotificationConnectionState.GatewayFailed, status.ConnectionState); Assert.Equal(expected, (await settings.LoadAsync(CancellationToken.None)).Qq); Assert.Equal(secret, await store.LoadAsync(CancellationToken.None));
                var log = await File.ReadAllTextAsync(Path.Combine(paths.LogsDirectory, "qq-notification.log")); Assert.Contains("qq_api_error", log, StringComparison.Ordinal); Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
            }
            finally { await channel.DisposeAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static NotificationRule Rule(long subjectId, NotificationCondition condition, long seconds, string target = "openid") => new(0, subjectId, true, condition, seconds, NotificationChannelType.QQ, NotificationTargetType.Private, target, "{name} {duration}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static NetworkDevice Device(long routerId, string mac, DateTimeOffset at) => new(0, routerId, mac, "Phone", "Phone", null, null, "192.168.1.2", "5G", -45, PresenceState.Offline, at, at, at);
    private static bool HasUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index])) { if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return true; index++; }
            else if (char.IsLowSurrogate(value[index])) return true;
        }
        return false;
    }
    private static string TemporaryRoot() { var root = Path.Combine(Path.GetTempPath(), "CloudLight-Notification-Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var root = TemporaryRoot(); var repository = new SqlitePresenceRepository(new AppPaths(root)); await repository.InitializeAsync(CancellationToken.None); var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero); var router = await repository.UpsertRouterAsync(new(0, "notification-fixture", "router", "fixture-partner", "测试路由器", null, null, start, start), CancellationToken.None); var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:FF", start), CancellationToken.None); var subjectId = (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id]; var subject = (await repository.GetSubjectAsync(subjectId, CancellationToken.None))!; return new(root, repository, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), new PresenceStateMachine(repository), router, device, subject, start);
    }

    private sealed class Fixture(string root, SqlitePresenceRepository repository, SubjectPresenceService presence, PresenceStateMachine machine, Router router, NetworkDevice device, PresenceSubject subject, DateTimeOffset start) : IDisposable
    {
        public string Root { get; } = root; public SqlitePresenceRepository Repository { get; } = repository; public SubjectPresenceService Presence { get; } = presence; public PresenceStateMachine Machine { get; } = machine; public Router Router { get; } = router; public NetworkDevice Device { get; } = device; public PresenceSubject Subject { get; } = subject; public DateTimeOffset Start { get; } = start;
        public async Task ApplyAsync(bool online, DateTimeOffset at) => await Machine.ApplySnapshotAsync(Router.Id, [new ObservedNetworkDevice(Device.MacAddress, "Phone", "Phone", "192.168.1.2", online, null, "5G", -45)], at, CancellationToken.None);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeChannel(bool connected) : INotificationChannel
    {
        private NotificationChannelStatus _status = new(NotificationChannelType.QQ, true, connected, connected, connected ? NotificationConnectionState.Connected : NotificationConnectionState.Reconnecting);
        public NotificationChannelType ChannelType => NotificationChannelType.QQ; public NotificationChannelStatus Status => _status; public int SendCount { get; private set; } public event EventHandler<NotificationChannelStatus>? StatusChanged;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask; public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
        {
            SendCount++; if (!_status.Connected) return Task.FromResult(new NotificationSendResult(false, 0, 0, "QQ 当前未连接。")); var parts = QQMessageSplitter.Split(request.Message, 5000); return Task.FromResult(new NotificationSendResult(true, Math.Max(0, parts.Count - startPart), parts.Count));
        }
        public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) => Task.FromResult(new NotificationSendResult(_status.Connected, _status.Connected ? 1 : 0, 1, _status.Connected ? null : "QQ 当前未连接。"));
        public void SetConnected(bool connected, bool notify = false) { _status = _status with { Running = connected, Connected = connected, ConnectionState = connected ? NotificationConnectionState.Connected : NotificationConnectionState.Reconnecting }; if (notify) StatusChanged?.Invoke(this, _status); }
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
    }
}
