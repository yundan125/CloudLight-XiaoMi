using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class DuplicateSubjectAndEventReplayTests
{
    [Fact]
    public async Task ActualShapeMergesStaleDuplicateKeepsOfflineBoundaryAndSendsOnlyAfterRealTransition()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var at = new DateTimeOffset(2026, 8, 30, 13, 54, 45, TimeSpan.FromHours(8));
            var router = await repository.UpsertRouterAsync(new(0, "duplicate-router", "router", "duplicate-partner", "zhenfeng", null, null, at, at), CancellationToken.None);
            var first = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:01", "爸爸", "爸爸1", "2.4G", at), CancellationToken.None);
            var second = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:02", "王振峰", "爸爸2", "5G", at), CancellationToken.None);
            var canonical = await repository.CreateSubjectAsync("爸爸", "真实主体", Guid.NewGuid(), at, CancellationToken.None);
            await repository.SetSubjectDevicesAsync(canonical.Id, [first.Id, second.Id], at, CancellationToken.None);
            await repository.UpsertSubjectCurrentStateAsync(new SubjectCurrentState(canonical.Id, PresenceState.Offline, at, at.AddHours(2)), CancellationToken.None);
            await repository.AddSubjectPresenceEventAsync(new SubjectPresenceEvent(0, canonical.Id, SubjectPresenceEventType.InitialOffline, at, null, at), CancellationToken.None);

            var duplicate = await repository.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), at.AddHours(1), CancellationToken.None);
            var rule = await repository.CreateNotificationRuleAsync(new NotificationRule(
                0, duplicate.Id, true, NotificationCondition.DetectedOnline, 0,
                NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "{name} 已经上线。", at.AddHours(2), at.AddHours(2)), CancellationToken.None);
            var staleEvent = new SubjectPresenceEvent(0, duplicate.Id, SubjectPresenceEventType.DetectedOnlineAfterGap, at.AddHours(2), null, at.AddHours(2));
            await repository.AddSubjectPresenceEventAsync(staleEvent, CancellationToken.None);
            var persistedStaleEvent = Assert.Single(await repository.GetSubjectPresenceEventsAsync(duplicate.Id, at, at.AddHours(3), CancellationToken.None));
            var oldDelivery = await repository.CreateNotificationDeliveryAsync(new NotificationDelivery(
                0, rule.Id, duplicate.Id, $"event:{persistedStaleEvent.Id}", at.AddHours(2), NotificationDeliveryStatus.Delivered,
                at.AddHours(2), NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "爸爸 已经上线。", null, 1, 1,
                at.AddHours(2), null), CancellationToken.None);
            await repository.UpsertNotificationRuleStateAsync(new NotificationRuleState(
                rule.Id, $"event:{persistedStaleEvent.Id}", at.AddHours(2), true, at.AddHours(2), false, null, null, at.AddHours(2), persistedStaleEvent.Id), CancellationToken.None);

            var reopened = new SqlitePresenceRepository(paths);
            await reopened.InitializeAsync(CancellationToken.None);

            var subjects = await reopened.GetSubjectsAsync(CancellationToken.None);
            var repaired = Assert.Single(subjects, value => value.DisplayName == "爸爸");
            Assert.Equal(1, subjects.Count(value => value.DisplayName == "爸爸"));
            Assert.Equal(2, (await reopened.GetSubjectDevicesAsync(repaired.Id, CancellationToken.None)).Count);
            Assert.Null(await reopened.GetSubjectAsync(duplicate.Id, CancellationToken.None));

            var state = await reopened.GetSubjectCurrentStateAsync(repaired.Id, CancellationToken.None);
            Assert.NotNull(state);
            Assert.Equal(PresenceState.Offline, state!.CurrentState);
            Assert.Equal(at, state.StateSince);
            Assert.Null(state.PendingOfflineSince);
            var events = await reopened.GetSubjectPresenceEventsAsync(repaired.Id, at.AddHours(-1), at.AddHours(3), CancellationToken.None);
            Assert.Equal(2, events.Count);
            Assert.Contains(events, value => value.EventType == SubjectPresenceEventType.DetectedOnlineAfterGap);
            Assert.Equal(repaired.Id, (await reopened.GetNotificationRuleAsync(rule.Id, CancellationToken.None))!.SubjectId);
            var history = await reopened.GetNotificationDeliveryAsync(oldDelivery.Id, CancellationToken.None);
            Assert.NotNull(history);
            Assert.Equal(repaired.Id, history!.SubjectId);
            Assert.Equal(rule.Id, history.RuleId);
            Assert.Equal($"event:{persistedStaleEvent.Id}", history.EpisodeId);
            var ruleState = await reopened.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);
            Assert.Equal(events.Max(value => value.Id), ruleState!.LastProcessedSubjectEventId);

            var presence = new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened));
            var service = new NotificationRuleService(reopened, presence);
            Assert.Empty(await service.EvaluateAsync(at.AddHours(3), CancellationToken.None));
            Assert.Single(await reopened.GetNotificationDeliveriesForRuleAsync(rule.Id, CancellationToken.None));

            var machine = new PresenceStateMachine(reopened);
            await machine.ApplySnapshotAsync(router.Id,
                [
                    Observed(first, true),
                    Observed(second, true)
                ], at.AddHours(4), CancellationToken.None);
            var requests = Assert.Single(await service.EvaluateAsync(at.AddHours(4), CancellationToken.None));
            Assert.Equal(NotificationCondition.DetectedOnline, (await reopened.GetNotificationRuleAsync(rule.Id, CancellationToken.None))!.Condition);
            Assert.Contains("爸爸 已经上线。", requests.Message, StringComparison.Ordinal);

            var channel = new CountingChannel();
            using (var dispatcher = new NotificationDispatcher(reopened, [channel]))
                await dispatcher.DispatchAsync(requests, CancellationToken.None);
            Assert.Equal(1, channel.SendCount);
            for (var index = 0; index < 10; index++)
                Assert.Empty(await service.EvaluateAsync(at.AddHours(4).AddSeconds(index + 1), CancellationToken.None));
            Assert.Equal(2, (await reopened.GetNotificationDeliveriesForRuleAsync(rule.Id, CancellationToken.None)).Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoPopulatedSameNameSubjectsRemainDistinct()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var at = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
            var router = await repository.UpsertRouterAsync(new(0, "same-name-router", "router", "same-name-partner", "router", null, null, at, at), CancellationToken.None);
            var first = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:11", "设备一", "设备一", "5G", at), CancellationToken.None);
            var second = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:12", "设备二", "设备二", "2.4G", at), CancellationToken.None);
            var left = await repository.CreateSubjectAsync("同名", "左侧", Guid.NewGuid(), at, CancellationToken.None);
            var right = await repository.CreateSubjectAsync("同名", "右侧", Guid.NewGuid(), at, CancellationToken.None);
            await repository.SetSubjectDevicesAsync(left.Id, [first.Id], at, CancellationToken.None);
            await repository.SetSubjectDevicesAsync(right.Id, [second.Id], at, CancellationToken.None);

            await repository.ReconcileSubjectIdentityAsync(CancellationToken.None);

            var subjects = await repository.GetSubjectsAsync(CancellationToken.None);
            Assert.Equal(2, subjects.Count(value => value.DisplayName == "同名"));
            Assert.Single(await repository.GetSubjectDevicesAsync(left.Id, CancellationToken.None));
            Assert.Single(await repository.GetSubjectDevicesAsync(right.Id, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DetectedRuleDoesNotReplayHistoryAndDuplicateDeliveryUsesDatabaseUniqueKey()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var at = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
            var router = await repository.UpsertRouterAsync(new(0, "watermark-router", "router", "watermark-partner", "router", null, null, at, at), CancellationToken.None);
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:21", "设备", "设备", "5G", at), CancellationToken.None);
            var subjectId = (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id];
            await repository.UpsertSubjectCurrentStateAsync(new SubjectCurrentState(subjectId, PresenceState.Offline, at, at), CancellationToken.None);
            await repository.AddSubjectPresenceEventAsync(new SubjectPresenceEvent(0, subjectId, SubjectPresenceEventType.InitialOffline, at, null, at), CancellationToken.None);
            var rule = await repository.CreateNotificationRuleAsync(new NotificationRule(
                0, subjectId, true, NotificationCondition.DetectedOffline, 0, NotificationChannelType.QQ,
                NotificationTargetType.Private, "test-openid", "{name}", at.AddMinutes(1), at.AddMinutes(1)), CancellationToken.None);
            var service = new NotificationRuleService(repository, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)));
            Assert.Empty(await service.EvaluateAsync(at.AddMinutes(2), CancellationToken.None));
            Assert.Equal(1, (await repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None))!.LastProcessedSubjectEventId);

            var delivery = new NotificationDelivery(0, rule.Id, subjectId, "event:999", at.AddMinutes(3), NotificationDeliveryStatus.Pending, null,
                NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "history", null, 0, 0, null, at.AddMinutes(3));
            var first = await repository.CreateNotificationDeliveryAsync(delivery, CancellationToken.None);
            var second = await repository.CreateNotificationDeliveryAsync(delivery with { Id = 0, Message = "different" }, CancellationToken.None);
            Assert.Equal(first.Id, second.Id);
            Assert.Single(await repository.GetNotificationDeliveriesForRuleAsync(rule.Id, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DetectedRuleRejectsLegacyStateEpisodeBeforeSending()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var at = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
            var router = await repository.UpsertRouterAsync(new(0, "legacy-delivery-router", "router", "legacy-delivery-partner", "router", null, null, at, at), CancellationToken.None);
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:31", "设备", "设备", "5G", at), CancellationToken.None);
            var subjectId = (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id];
            await repository.UpsertSubjectCurrentStateAsync(new SubjectCurrentState(subjectId, PresenceState.Offline, at, at), CancellationToken.None);
            var rule = await repository.CreateNotificationRuleAsync(new NotificationRule(
                0, subjectId, true, NotificationCondition.DetectedOnline, 0, NotificationChannelType.QQ,
                NotificationTargetType.Private, "test-openid", "{name} 已经上线。", at, at), CancellationToken.None);
            var invalid = await repository.CreateNotificationDeliveryAsync(new NotificationDelivery(
                0, rule.Id, subjectId, $"2:{at.UtcTicks}", at.AddMinutes(1), NotificationDeliveryStatus.Pending, null,
                NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "错误上线", null, 0, 0, null, at.AddMinutes(1)), CancellationToken.None);
            var channel = new CountingChannel();
            using (var dispatcher = new NotificationDispatcher(repository, [channel]))
                await dispatcher.DispatchAsync(new NotificationRequest(invalid.Id, rule.Id, subjectId, invalid.EpisodeId, invalid.Channel, invalid.TargetType, invalid.TargetId, invalid.Message, invalid.CreatedAt), CancellationToken.None);
            Assert.Equal(0, channel.SendCount);
            Assert.Equal(NotificationDeliveryStatus.Canceled, (await repository.GetNotificationDeliveryAsync(invalid.Id, CancellationToken.None))!.Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static NetworkDevice Device(long routerId, string mac, string originalName, string customName, string connectionType, DateTimeOffset at) =>
        new(0, routerId, mac, originalName, "iPhone", customName, null, "192.168.1.2", connectionType, -45, PresenceState.Offline, at.AddHours(-1), at, at);

    private static ObservedNetworkDevice Observed(NetworkDevice device, bool online) =>
        new(device.MacAddress, device.OriginalName, device.OriginName, device.LastIp, online, null, device.ConnectionType, device.Signal);

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Duplicate-Subject-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CountingChannel : INotificationChannel
    {
        private readonly NotificationChannelStatus _status = new(NotificationChannelType.QQ, true, true, true, NotificationConnectionState.Connected);

        public NotificationChannelType ChannelType => NotificationChannelType.QQ;
        public NotificationChannelStatus Status => _status;
        public int SendCount { get; private set; }
        public event EventHandler<NotificationChannelStatus>? StatusChanged = delegate { };
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) =>
            Task.FromResult(new NotificationSendResult(true, 1, 1));
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new NotificationSendResult(true, 1, 1));
        }
    }
}
