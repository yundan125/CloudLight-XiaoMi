using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

/// <summary>
/// Regression coverage for the single confirmed-subject state used by cards,
/// timelines and QQ rules. These tests intentionally use SQLite rather than
/// an in-memory repository so episode/event deduplication survives reopening.
/// </summary>
public sealed class ConfirmedPresenceNotificationTests
{
    [Fact]
    public async Task ContinuousOnlineKeepsOneConfirmedBoundaryAndTimelineCurrentSegment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineSince = fixture.Start.AddMinutes(1);
        await fixture.ApplyAsync(onlineSince, true);
        await fixture.ApplyAsync(onlineSince.AddHours(3), true);

        var fact = await fixture.FactAsync(onlineSince.AddHours(4));
        var timeline = await fixture.Presence.GetTimelineAsync(fixture.Subject.Id, onlineSince, onlineSince.AddHours(4), CancellationToken.None);

        Assert.Equal(PresenceState.Online, fact.CurrentState);
        Assert.Equal(onlineSince, fact.StateSince);
        Assert.Equal(onlineSince, Assert.Single(timeline).Start);
        Assert.Equal(onlineSince.AddHours(4), Assert.Single(timeline).End);
    }

    [Fact]
    public async Task ContinuousOfflineKeepsFirstObservedOfflineBoundaryAfterConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineSince = fixture.Start.AddMinutes(1);
        var offlineObservedAt = onlineSince.AddHours(1);
        await fixture.ApplyAsync(onlineSince, true);
        await fixture.ApplyAsync(offlineObservedAt, false);
        await fixture.ApplyAsync(offlineObservedAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);
        await fixture.ApplyAsync(offlineObservedAt.AddHours(2), false);

        var state = await fixture.Repository.GetSubjectCurrentStateAsync(fixture.Subject.Id, CancellationToken.None);
        var confirmed = Assert.Single(await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, fixture.Start, offlineObservedAt.AddHours(3), CancellationToken.None), value => value.EventType == SubjectPresenceEventType.ConfirmedOffline);

        Assert.NotNull(state);
        Assert.Equal(PresenceState.Offline, state!.CurrentState);
        Assert.Equal(offlineObservedAt, state.StateSince);
        Assert.Equal(offlineObservedAt + SubjectPresenceService.DefaultOfflineGracePeriod, confirmed.ObservedAt);
        Assert.Equal(offlineObservedAt, confirmed.EffectiveAt);
    }

    [Fact]
    public async Task ShortOfflineInsideGraceKeepsOnlineEpisodeAndCreatesNoConfirmedEvent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineSince = fixture.Start.AddMinutes(1);
        await fixture.ApplyAsync(onlineSince, true);
        await fixture.ApplyAsync(onlineSince.AddMinutes(10), false);
        await fixture.ApplyAsync(onlineSince.AddMinutes(10).AddSeconds(20), true);

        var state = await fixture.Repository.GetSubjectCurrentStateAsync(fixture.Subject.Id, CancellationToken.None);
        var events = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, fixture.Start, onlineSince.AddMinutes(11), CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(PresenceState.Online, state!.CurrentState);
        Assert.Equal(onlineSince, state.StateSince);
        Assert.Null(state.PendingOfflineSince);
        Assert.DoesNotContain(events, value => value.EventType is SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.ConfirmedOnline);
    }

    [Fact]
    public async Task MultiMacBandSwitchKeepsOnlineEpisodeAndDoesNotEmitSubjectTransition()
    {
        await using var fixture = await Fixture.CreateAsync(memberCount: 2);
        var onlineSince = fixture.Start.AddMinutes(1);
        await fixture.ApplyAsync(onlineSince, true, false);
        await fixture.ApplyAsync(onlineSince.AddSeconds(10), false, true);
        await fixture.ApplyAsync(onlineSince.AddSeconds(20), true, false);

        var fact = await fixture.FactAsync(onlineSince.AddMinutes(1));
        var events = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, fixture.Start, onlineSince.AddMinutes(1), CancellationToken.None);

        Assert.Equal(PresenceState.Online, fact.CurrentState);
        Assert.Equal(onlineSince, fact.StateSince);
        Assert.DoesNotContain(events, value => value.EventType is SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.ConfirmedOnline or SubjectPresenceEventType.DetectedOnlineAfterGap or SubjectPresenceEventType.DetectedOfflineAfterGap);
    }

    [Fact]
    public async Task SameStateAcrossGapKeepsBoundaryAndDoesNotCreateDetectedEvent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineSince = fixture.Start.AddMinutes(1);
        var gapStart = onlineSince.AddHours(1);
        var gapEnd = gapStart.AddHours(2);
        await fixture.ApplyAsync(onlineSince, true);
        var gap = await fixture.Repository.StartMonitoringGapAsync(gapStart, "test", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, gapEnd, CancellationToken.None);
        await fixture.ApplyAsync(gapEnd, true);

        var fact = await fixture.FactAsync(gapEnd.AddMinutes(1));
        var timeline = await fixture.Presence.GetTimelineAsync(fixture.Subject.Id, onlineSince, gapEnd.AddMinutes(1), CancellationToken.None);
        var events = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, fixture.Start, gapEnd.AddMinutes(1), CancellationToken.None);

        Assert.Equal(onlineSince, fact.StateSince);
        Assert.DoesNotContain(timeline, value => value.State == PresenceState.Unknown);
        Assert.DoesNotContain(events, value => value.EventType is SubjectPresenceEventType.DetectedOnlineAfterGap or SubjectPresenceEventType.DetectedOfflineAfterGap);
    }

    [Fact]
    public async Task DifferentStateAcrossGapStartsAtDetectionAndPersistsDetectedEvent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineSince = fixture.Start.AddMinutes(1);
        var gapStart = onlineSince.AddHours(1);
        var detectedAt = gapStart.AddHours(2);
        await fixture.ApplyAsync(onlineSince, true);
        var gap = await fixture.Repository.StartMonitoringGapAsync(gapStart, "test", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, detectedAt, CancellationToken.None);
        await fixture.ApplyAsync(detectedAt, false);

        var fact = await fixture.FactAsync(detectedAt.AddMinutes(1));
        var detected = Assert.Single(await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, fixture.Start, detectedAt.AddMinutes(1), CancellationToken.None), value => value.EventType == SubjectPresenceEventType.DetectedOfflineAfterGap);

        Assert.Equal(PresenceState.Offline, fact.CurrentState);
        Assert.Equal(detectedAt, fact.StateSince);
        Assert.Equal(detectedAt, detected.ObservedAt);
        Assert.Equal(detectedAt, detected.EffectiveAt);
        Assert.NotNull(detected.MonitoringGapId);
    }

    [Fact]
    public async Task InitialObservationCreatesOnlyBaselineAndNeverAnEventReminder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var createdAt = fixture.Start.AddMinutes(-1);
        await fixture.Repository.CreateNotificationRuleAsync(EventRule(fixture.Subject.Id, NotificationCondition.DetectedOffline, createdAt), CancellationToken.None);
        await fixture.ApplyAsync(fixture.Start, false);

        var requests = await new NotificationRuleService(fixture.Repository, fixture.Presence).EvaluateAsync(fixture.Start.AddMinutes(1), CancellationToken.None);
        var eventRecord = Assert.Single(await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, fixture.Start, fixture.Start.AddMinutes(1), CancellationToken.None));

        Assert.Equal(SubjectPresenceEventType.InitialOffline, eventRecord.EventType);
        Assert.Empty(requests);
        Assert.Empty(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task SubjectCardUsesTheSameStateSinceAsCurrentTimelineSegment()
    {
        var current = DateTimeOffset.UtcNow;
        await using var fixture = await Fixture.CreateAsync(current.AddHours(-3));
        var stateSince = current.AddHours(-2);
        await fixture.ApplyAsync(stateSince, true);

        var snapshot = (await fixture.Presence.GetSnapshotAsync(fixture.Subject.Id, current, CancellationToken.None))!;
        var timeline = await fixture.Presence.GetTimelineAsync(fixture.Subject.Id, stateSince, current, CancellationToken.None);
        var card = PresenceCardViewModel.ForSubject(snapshot, _ => { });

        Assert.Equal(stateSince, snapshot.ConfirmedStateSince);
        Assert.Equal(stateSince, timeline[^1].Start);
        Assert.StartsWith("在线 2小时", card.Duration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlineAndOfflineDurationRulesUseConfirmedEpisodesOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        var offlineAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(onlineAt, false);
        var onlineRule = await fixture.Repository.CreateNotificationRuleAsync(DurationRule(fixture.Subject.Id, NotificationCondition.OnlineFor, 60, fixture.Start), CancellationToken.None);
        await fixture.ApplyAsync(onlineAt, true);
        var rules = new NotificationRuleService(fixture.Repository, fixture.Presence);
        Assert.Empty(await rules.EvaluateAsync(onlineAt.AddSeconds(59), CancellationToken.None));
        var onlineRequest = Assert.Single(await rules.EvaluateAsync(onlineAt.AddMinutes(1), CancellationToken.None));

        var channel = new FakeChannel(true);
        using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
        await dispatcher.DispatchAsync(onlineRequest, CancellationToken.None);
        Assert.Empty(await rules.EvaluateAsync(onlineAt.AddHours(1), CancellationToken.None));

        await fixture.ApplyAsync(offlineAt, false);
        await fixture.ApplyAsync(offlineAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);
        var offlineRule = await fixture.Repository.CreateNotificationRuleAsync(DurationRule(fixture.Subject.Id, NotificationCondition.OfflineFor, 60, fixture.Start), CancellationToken.None);
        Assert.Empty(await rules.EvaluateAsync(offlineAt.AddSeconds(59), CancellationToken.None));
        var offlineRequest = Assert.Single(await rules.EvaluateAsync(offlineAt.AddMinutes(1), CancellationToken.None));
        await dispatcher.DispatchAsync(offlineRequest, CancellationToken.None);
        Assert.Empty(await rules.EvaluateAsync(offlineAt.AddHours(1), CancellationToken.None));

        Assert.Equal(2, channel.SendCount);
        Assert.Equal(onlineRule.Id, onlineRequest.RuleId);
        Assert.Equal(offlineRule.Id, offlineRequest.RuleId);
    }

    [Fact]
    public async Task LegacyDurationDeliveryIsNotDuplicatedAfterEpisodeIdMigration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        await fixture.ApplyAsync(onlineAt, true);
        var rule = await fixture.Repository.CreateNotificationRuleAsync(DurationRule(fixture.Subject.Id, NotificationCondition.OnlineFor, 60, fixture.Start), CancellationToken.None);
        var legacyEpisode = $"{(int)PresenceState.Online}:{onlineAt.AddHours(-1).UtcTicks}";
        await fixture.Repository.CreateNotificationDeliveryAsync(new NotificationDelivery(
            0, rule.Id, fixture.Subject.Id, legacyEpisode, onlineAt.AddMinutes(1), NotificationDeliveryStatus.Delivered,
            onlineAt.AddMinutes(1), NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "legacy", null, 1, 1,
            onlineAt.AddMinutes(1), null), CancellationToken.None);

        var requests = await new NotificationRuleService(fixture.Repository, fixture.Presence).EvaluateAsync(onlineAt.AddMinutes(2), CancellationToken.None);

        Assert.Empty(requests);
        Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmedAndGapEventsCreatePersistentOnePerEventDeliveries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var createdAt = fixture.Start.AddMinutes(-1);
        var onlineRule = await fixture.Repository.CreateNotificationRuleAsync(EventRule(fixture.Subject.Id, NotificationCondition.DetectedOnline, createdAt), CancellationToken.None);
        var offlineRule = await fixture.Repository.CreateNotificationRuleAsync(EventRule(fixture.Subject.Id, NotificationCondition.DetectedOffline, createdAt), CancellationToken.None);
        await fixture.ApplyAsync(fixture.Start, false);
        var onlineAt = fixture.Start.AddMinutes(1);
        await fixture.ApplyAsync(onlineAt, true);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var channel = new FakeChannel(true);
        using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
        var first = Assert.Single(await service.EvaluateAsync(onlineAt, CancellationToken.None));
        Assert.Equal(onlineRule.Id, first.RuleId);
        await dispatcher.DispatchAsync(first, CancellationToken.None);
        Assert.Empty(await service.EvaluateAsync(onlineAt.AddMinutes(1), CancellationToken.None));

        var offlineAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(offlineAt, false);
        await fixture.ApplyAsync(offlineAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);
        var second = Assert.Single(await service.EvaluateAsync(offlineAt.AddMinutes(1), CancellationToken.None));
        Assert.Equal(offlineRule.Id, second.RuleId);
        await dispatcher.DispatchAsync(second, CancellationToken.None);

        var deliveries = await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, value => Assert.Equal(NotificationDeliveryStatus.Delivered, value.Status));
        Assert.All(deliveries, value => Assert.StartsWith("event:", value.EpisodeId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventRulesIgnoreGraceAndBandSwitchButTriggerForChangedGap()
    {
        await using var fixture = await Fixture.CreateAsync(memberCount: 2);
        var createdAt = fixture.Start.AddMinutes(-1);
        await fixture.Repository.CreateNotificationRuleAsync(EventRule(fixture.Subject.Id, NotificationCondition.DetectedOffline, createdAt), CancellationToken.None);
        var onlineAt = fixture.Start.AddMinutes(1);
        await fixture.ApplyAsync(onlineAt, true, false);
        await fixture.ApplyAsync(onlineAt.AddSeconds(10), false, true);
        await fixture.ApplyAsync(onlineAt.AddSeconds(20), true, false);
        await fixture.ApplyAsync(onlineAt.AddMinutes(1), false, false);
        await fixture.ApplyAsync(onlineAt.AddMinutes(1).AddSeconds(20), true, false);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        Assert.Empty(await service.EvaluateAsync(onlineAt.AddMinutes(2), CancellationToken.None));

        var gapStart = onlineAt.AddMinutes(3);
        var detectedAt = gapStart.AddMinutes(1);
        var gap = await fixture.Repository.StartMonitoringGapAsync(gapStart, "test", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, detectedAt, CancellationToken.None);
        await fixture.ApplyAsync(detectedAt, false, false);

        var request = Assert.Single(await service.EvaluateAsync(detectedAt, CancellationToken.None));
        Assert.StartsWith("event:", request.EpisodeId, StringComparison.Ordinal);
        Assert.Contains("已离线", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentEvaluationsCreateAndSendOnlyOneDeliveryForOneEpisode()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        var offlineAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(onlineAt, true);
        await fixture.ApplyAsync(offlineAt, false);
        await fixture.ApplyAsync(offlineAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);
        await fixture.Repository.CreateNotificationRuleAsync(DurationRule(fixture.Subject.Id, NotificationCondition.OfflineFor, 60, fixture.Start), CancellationToken.None);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var evaluated = await Task.WhenAll(
            service.EvaluateAsync(offlineAt.AddMinutes(1), CancellationToken.None),
            service.EvaluateAsync(offlineAt.AddMinutes(1), CancellationToken.None));
        var requests = evaluated.SelectMany(value => value).ToArray();
        Assert.NotEmpty(requests);
        var request = requests[0];
        Assert.All(requests, value => Assert.Equal(request.DeliveryId, value.DeliveryId));
        Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None));

        var channel = new FakeChannel(true);
        using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
        await Task.WhenAll(dispatcher.DispatchAsync(request, CancellationToken.None), dispatcher.DispatchAsync(request, CancellationToken.None));

        Assert.Equal(1, channel.SendCount);
        Assert.Equal(NotificationDeliveryStatus.Delivered, Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task FailedDeliverySurvivesReopenAndDisabledRuleCancelsBeforeSend()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        var offlineAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(onlineAt, true);
        await fixture.ApplyAsync(offlineAt, false);
        await fixture.ApplyAsync(offlineAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);
        var rule = await fixture.Repository.CreateNotificationRuleAsync(DurationRule(fixture.Subject.Id, NotificationCondition.OfflineFor, 60, fixture.Start), CancellationToken.None);
        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var request = Assert.Single(await service.EvaluateAsync(offlineAt.AddMinutes(1), CancellationToken.None));

        var disconnected = new FakeChannel(false);
        using (var dispatcher = new NotificationDispatcher(fixture.Repository, [disconnected]))
            await dispatcher.DispatchAsync(request, CancellationToken.None);
        var failed = Assert.Single(await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None));
        Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status);
        Assert.NotNull((await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None))!.PendingDeliveryId);

        // A later state change must not erase the durable failed-delivery
        // pointer before the dispatcher has had a chance to retry it.
        await fixture.ApplyAsync(offlineAt.AddMinutes(1).AddSeconds(10), true);
        Assert.Empty(await service.EvaluateAsync(offlineAt.AddMinutes(1).AddSeconds(10), CancellationToken.None));
        var retained = await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);
        Assert.True(retained!.PendingDelivery);
        Assert.Equal(failed.Id, retained.PendingDeliveryId);

        var reopened = new SqlitePresenceRepository(new AppPaths(fixture.Root));
        await reopened.InitializeAsync(CancellationToken.None);
        var connected = new FakeChannel(true);
        using (var dispatcher = new NotificationDispatcher(reopened, [connected]))
            await dispatcher.RetryPendingAsync(failed.NextAttemptAt!.Value.AddSeconds(1), CancellationToken.None);
        Assert.Equal(NotificationDeliveryStatus.Delivered, Assert.Single(await reopened.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None)).Status);
        Assert.Equal(1, connected.SendCount);

        var nextOfflineAt = offlineAt.AddMinutes(2);
        await fixture.ApplyAsync(nextOfflineAt, false);
        await fixture.ApplyAsync(nextOfflineAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);
        var nextRule = await reopened.CreateNotificationRuleAsync(DurationRule(fixture.Subject.Id, NotificationCondition.OfflineFor, 60, fixture.Start), CancellationToken.None);
        var secondRequest = Assert.Single(await new NotificationRuleService(reopened, new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened))).EvaluateAsync(nextOfflineAt.AddMinutes(1), CancellationToken.None), value => value.RuleId == nextRule.Id);
        await reopened.UpdateNotificationRuleAsync(nextRule with { Enabled = false, UpdatedAt = nextOfflineAt.AddMinutes(1) }, CancellationToken.None);
        var noSend = new FakeChannel(true);
        using (var dispatcher = new NotificationDispatcher(reopened, [noSend]))
            await dispatcher.DispatchAsync(secondRequest, CancellationToken.None);
        Assert.Equal(0, noSend.SendCount);
        Assert.Equal(NotificationDeliveryStatus.Canceled, (await reopened.GetNotificationDeliveryAsync(secondRequest.DeliveryId, CancellationToken.None))!.Status);
    }

    private static NotificationRule DurationRule(long subjectId, NotificationCondition condition, long thresholdSeconds, DateTimeOffset createdAt) =>
        new(0, subjectId, true, condition, thresholdSeconds, NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "{name} {duration}", createdAt, createdAt);

    private static NotificationRule EventRule(long subjectId, NotificationCondition condition, DateTimeOffset createdAt) =>
        new(0, subjectId, true, condition, 0, NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "", createdAt, createdAt);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly PresenceStateMachine _machine;
        private readonly IReadOnlyList<NetworkDevice> _devices;

        private Fixture(
            string root,
            SqlitePresenceRepository repository,
            PresenceStateMachine machine,
            SubjectPresenceService presence,
            Router router,
            PresenceSubject subject,
            IReadOnlyList<NetworkDevice> devices,
            DateTimeOffset start)
        {
            _root = root;
            Repository = repository;
            _machine = machine;
            Presence = presence;
            Router = router;
            Subject = subject;
            _devices = devices;
            Start = start;
        }

        public string Root => _root;
        public SqlitePresenceRepository Repository { get; }
        public SubjectPresenceService Presence { get; }
        public Router Router { get; }
        public PresenceSubject Subject { get; }
        public DateTimeOffset Start { get; }

        public static async Task<Fixture> CreateAsync(DateTimeOffset? start = null, int memberCount = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "CloudLight-Confirmed-Presence-Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var at = start ?? new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
            var router = await repository.UpsertRouterAsync(new Router(0, $"confirmed-{Guid.NewGuid():N}", "router", $"partner-{Guid.NewGuid():N}", "测试路由器", null, null, at, at), CancellationToken.None);
            var devices = new List<NetworkDevice>();
            for (var index = 0; index < memberCount; index++)
            {
                devices.Add(await repository.InsertDeviceAsync(new NetworkDevice(
                    0, router.Id, $"AA:BB:CC:DD:EF:{index + 1:00}", $"设备 {index + 1}", $"设备 {index + 1}", null, null,
                    "192.168.1.2", "5G", -45, PresenceState.Offline, at.AddHours(-1), at, at), CancellationToken.None));
            }
            var subject = await repository.CreateSubjectAsync("测试主体", null, Guid.NewGuid(), at.AddHours(-1), CancellationToken.None);
            await repository.SetSubjectDevicesAsync(subject.Id, devices.Select(value => value.Id).ToArray(), at.AddHours(-1), CancellationToken.None);
            var machine = new PresenceStateMachine(repository);
            return new Fixture(root, repository, machine, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), router, subject, devices, at);
        }

        public async Task ApplyAsync(DateTimeOffset observedAt, params bool[] online)
        {
            Assert.Equal(_devices.Count, online.Length);
            var observations = _devices.Select((device, index) => new ObservedNetworkDevice(
                device.MacAddress, device.OriginalName, device.OriginName, device.LastIp, online[index], null, device.ConnectionType, device.Signal)).ToArray();
            await _machine.ApplySnapshotAsync(Router.Id, observations, observedAt, CancellationToken.None);
        }

        public async Task<SubjectPresenceFact> FactAsync(DateTimeOffset now) =>
            (await Presence.GetCurrentFactAsync(Subject.Id, now, CancellationToken.None))!;

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeChannel(bool connected) : INotificationChannel
    {
        private NotificationChannelStatus _status = new(NotificationChannelType.QQ, true, connected, connected, connected ? NotificationConnectionState.Connected : NotificationConnectionState.Reconnecting);

        public NotificationChannelType ChannelType => NotificationChannelType.QQ;
        public NotificationChannelStatus Status => _status;
        public int SendCount { get; private set; }
        public event EventHandler<NotificationChannelStatus>? StatusChanged = delegate { };

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) =>
            Task.FromResult(new NotificationSendResult(_status.Connected, _status.Connected ? 1 : 0, 1, _status.Connected ? null : "QQ 当前未连接。"));

        public Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(_status.Connected
                ? new NotificationSendResult(true, 1, 1)
                : new NotificationSendResult(false, 0, 0, "QQ 当前未连接。"));
        }
    }
}
