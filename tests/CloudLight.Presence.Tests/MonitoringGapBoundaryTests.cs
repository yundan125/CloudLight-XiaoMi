using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class MonitoringGapBoundaryTests
{
    [Fact]
    public async Task LegacyOpenOnlineSessionIsClippedAtMonitoringGap()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        var onlineAt = At(10);
        await AddOnlineEvidenceAsync(fixture, onlineAt);
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "legacy-gap");

        var statistics = await new PresenceStatisticsService(fixture.Repository)
            .GetStatisticsAsync(fixture.Device.Id, At(10), At(16), CancellationToken.None);
        var timeline = await new PresenceStatisticsService(fixture.Repository)
            .GetTimelineAsync(fixture.Device.Id, At(10), At(16), CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(2), statistics.KnownOnlineDuration);
        Assert.Equal(TimeSpan.FromHours(2), statistics.UnknownDuration);
        Assert.Equal(TimeSpan.FromHours(2), statistics.KnownOfflineDuration);
        Assert.Equal([PresenceState.Online, PresenceState.Unknown, PresenceState.Offline], timeline.Select(value => value.State).ToArray());
    }

    [Fact]
    public async Task FirstOfflineObservationAfterGapClosesBoundaryWithoutOfflineEvent()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        var onlineAt = At(10);
        await AddOnlineEvidenceAsync(fixture, onlineAt);
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "legacy-gap");
        var beforeEvents = await fixture.Repository.GetEventsAsync(fixture.Device.Id, CancellationToken.None);

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id, [Observation(fixture.Device, online: false)], At(14), CancellationToken.None);

        var afterEvents = await fixture.Repository.GetEventsAsync(fixture.Device.Id, CancellationToken.None);
        var session = Assert.Single(await fixture.Repository.GetSessionsAsync(fixture.Device.Id, CancellationToken.None));
        var timeline = await new PresenceStatisticsService(fixture.Repository)
            .GetTimelineAsync(fixture.Device.Id, At(10), At(16), CancellationToken.None);

        Assert.Equal(beforeEvents.Count, afterEvents.Count);
        Assert.Equal(At(12), session.EndedAt);
        Assert.False(session.EndKnown);
        Assert.Equal([PresenceState.Online, PresenceState.Unknown, PresenceState.Offline], timeline.Select(value => value.State).ToArray());
        var device = (await fixture.Repository.GetDeviceAsync(fixture.Device.Id, CancellationToken.None))!;
        Assert.Equal(PresenceState.Offline, device.CurrentState);
        Assert.Equal(PresenceState.Offline, device.LastKnownHistoricalState);
        Assert.Equal(At(14), device.LastSeenAt);
        Assert.Equal(At(14), device.LastStateChangedAt);
    }

    [Fact]
    public async Task TwentyFourHourStatisticsSeparateOnlineOfflineAndUnknown()
    {
        await using var fixture = await CreateFixtureAsync();
        var from = At(0); var to = from.AddDays(1);
        await fixture.Repository.AddSessionAsync(new(0, fixture.Device.Id, from, from.AddHours(4), true, true), CancellationToken.None);
        var gap = await fixture.Repository.StartMonitoringGapAsync(from.AddHours(4), "test", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, from.AddHours(10), CancellationToken.None);

        var value = await new PresenceStatisticsService(fixture.Repository)
            .GetStatisticsAsync(fixture.Device.Id, from, to, CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(4), value.KnownOnlineDuration);
        Assert.Equal(TimeSpan.FromHours(14), value.KnownOfflineDuration);
        Assert.Equal(TimeSpan.FromHours(6), value.UnknownDuration);
        Assert.Equal(TimeSpan.FromHours(18), value.KnownDuration);
        Assert.Equal(.75, value.Coverage, 6);
        Assert.Equal(4d / 18d, value.OnlinePercentageOfKnownTime, 6);
    }

    [Fact]
    public async Task ThreeSevenAndThirtyDayWindowsUseTheSameIntervalNormalization()
    {
        await using var fixture = await CreateFixtureAsync();
        var to = At(0);
        await fixture.Repository.AddSessionAsync(new(0, fixture.Device.Id, to.AddDays(-2), to.AddDays(-2).AddHours(4), true, true), CancellationToken.None);
        var gap = await fixture.Repository.StartMonitoringGapAsync(to.AddDays(-2).AddHours(4), "test", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, to.AddDays(-2).AddHours(10), CancellationToken.None);
        await fixture.Repository.AddSessionAsync(new(0, fixture.Device.Id, to.AddDays(-1), to.AddDays(-1).AddHours(2), true, true), CancellationToken.None);

        foreach (var days in new[] { 1, 3, 7, 30 })
        {
            var value = await new PresenceStatisticsService(fixture.Repository)
                .GetStatisticsAsync(fixture.Device.Id, to.AddDays(-days), to, CancellationToken.None);
            Assert.Equal(TimeSpan.FromDays(days), value.WindowDuration);
            Assert.Equal(value.WindowDuration, value.KnownDuration + value.UnknownDuration);
            Assert.Equal(value.Coverage, value.KnownDuration.TotalSeconds / value.WindowDuration.TotalSeconds, 6);
        }
    }

    [Fact]
    public async Task UnexpectedApplicationRunClosesSessionAtLastSuccessfulObservation()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await AddOnlineEvidenceAsync(fixture, At(6));
        var firstRun = await fixture.Repository.StartApplicationRunAsync(At(8), CancellationToken.None);
        await fixture.Repository.UpdateApplicationRunCloudUpdateAsync(firstRun, At(10), CancellationToken.None);

        _ = await fixture.Repository.StartApplicationRunAsync(At(14), CancellationToken.None);
        await fixture.Repository.CloseOpenMonitoringGapsAsync(At(14), CancellationToken.None);

        var session = Assert.Single(await fixture.Repository.GetSessionsAsync(fixture.Device.Id, CancellationToken.None));
        var gaps = await fixture.Repository.GetMonitoringGapsAsync(At(6), At(16), CancellationToken.None);
        var statistics = await new PresenceStatisticsService(fixture.Repository)
            .GetStatisticsAsync(fixture.Device.Id, At(6), At(16), CancellationToken.None);

        Assert.Equal(At(10), session.EndedAt);
        Assert.False(session.EndKnown);
        Assert.Contains(gaps, value => value.StartedAt == At(10) && value.EndedAt == At(14));
        Assert.Equal(TimeSpan.FromHours(4), statistics.KnownOnlineDuration);
        Assert.Equal(TimeSpan.FromHours(4), statistics.UnknownDuration);
    }

    [Fact]
    public async Task PollFailureStartsGapAndClosesOnlineSession()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await AddOnlineEvidenceAsync(fixture, At(0));
        var source = new FailingSource();
        var monitor = new PresenceMonitor(source, fixture.Repository, new PresenceStateMachine(fixture.Repository));
        await monitor.StartAsync(fixture.Router, CancellationToken.None);
        try
        {
            await source.Failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            PresenceSession? session = null;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                session = (await fixture.Repository.GetSessionsAsync(fixture.Device.Id, CancellationToken.None)).Single();
                if (session.EndedAt is not null) break;
                await Task.Delay(50);
            }

            var gaps = await fixture.Repository.GetMonitoringGapsAsync(At(0), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            var events = await fixture.Repository.GetEventsAsync(fixture.Device.Id, CancellationToken.None);
            Assert.NotNull(session!.EndedAt);
            Assert.False(session.EndKnown);
            Assert.Contains(gaps, value => value.Reason == "Xiaomi Cloud 暂时不可用");
            Assert.DoesNotContain(events, value => value.EventType == PresenceEventType.Offline);
        }
        finally
        {
            await monitor.StopAsync("test", CancellationToken.None);
        }
    }

    [Fact]
    public async Task MultipleMacSubjectDoesNotRemainOnlineAfterGap()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Offline, 2);
        await fixture.Repository.AddEventAsync(new(0, fixture.Devices[0].Id, PresenceEventType.Online, At(10), PresenceSource.Polling), CancellationToken.None);
        await fixture.Repository.AddSessionAsync(new(0, fixture.Devices[0].Id, At(10), null, true, false), CancellationToken.None);
        var gap = await fixture.Repository.StartMonitoringGapAsync(At(12), "test", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, At(14), CancellationToken.None);

        var service = new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository));
        var timeline = await service.GetTimelineAsync(fixture.Subject.Id, At(10), At(16), CancellationToken.None);
        var fact = await service.GetCurrentFactAsync(fixture.Subject.Id, At(16), CancellationToken.None);

        Assert.Equal([PresenceState.Online, PresenceState.Unknown, PresenceState.Offline], timeline.Select(value => value.State).ToArray());
        Assert.Equal(PresenceState.Offline, fact!.CurrentState);
        Assert.Null(fact.ActiveDevice);
    }

    [Fact]
    public void GapBoundaryDoesNotCreateOfflineActivity()
    {
        var timeline = new PresenceTimelineSegment[]
        {
            new(At(10), At(12), PresenceState.Online),
            new(At(12), At(14), PresenceState.Unknown),
            new(At(14), At(16), PresenceState.Offline)
        };

        Assert.DoesNotContain(SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false), value => value.Type == SubjectActivityType.Offline);
        Assert.DoesNotContain(SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: true), value => value.Type == SubjectActivityType.Offline);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitialUnknownPeriodKeepsTheFirstKnownObservation(bool online)
    {
        var expected = online ? SubjectActivityType.Online : SubjectActivityType.Offline;
        var state = online ? PresenceState.Online : PresenceState.Offline;
        var timeline = new PresenceTimelineSegment[]
        {
            new(At(10), At(12), PresenceState.Unknown),
            new(At(12), At(14), state)
        };

        var hidden = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false);
        var shown = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: true);

        var first = Assert.Single(hidden);
        Assert.Equal(expected, first.Type);
        Assert.Equal(At(12), first.OccurredAtUtc);
        Assert.Equal([expected, SubjectActivityType.UnknownPeriod], shown.Select(value => value.Type).ToArray());
    }

    [Fact]
    public void KnownBoundaryAfterGapUsesThePostGapObservationAsItsBaseline()
    {
        var timeline = new PresenceTimelineSegment[]
        {
            new(At(10), At(12), PresenceState.Online),
            new(At(12), At(14), PresenceState.Unknown),
            new(At(14), At(15), PresenceState.Offline),
            new(At(15), At(16), PresenceState.Online)
        };
        var detected = new SubjectPresenceEvent(1, 1, SubjectPresenceEventType.DetectedOfflineAfterGap, At(14), 1);

        var activities = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false, detectedAfterGap: [detected]);

        Assert.Equal(
            [SubjectActivityType.Online, SubjectActivityType.DetectedOfflineAfterGap, SubjectActivityType.Online],
            activities.Select(value => value.Type).ToArray());
        Assert.Equal([At(15), At(14), At(10)], activities.Select(value => value.OccurredAtUtc).ToArray());
    }

    [Fact]
    public void DetectedActivityIsShownWithoutTreatingTheGapBoundaryAsARealTransition()
    {
        var timeline = new PresenceTimelineSegment[]
        {
            new(At(10), At(12), PresenceState.Online),
            new(At(12), At(14), PresenceState.Unknown),
            new(At(14), At(16), PresenceState.Offline)
        };
        var detected = new SubjectPresenceEvent(1, 1, SubjectPresenceEventType.DetectedOfflineAfterGap, At(14), 1);

        var activities = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false, detectedAfterGap: [detected]);

        Assert.Contains(activities, value => value.Type == SubjectActivityType.DetectedOfflineAfterGap && value.OccurredAtUtc == At(14));
        Assert.DoesNotContain(activities, value => value.Type == SubjectActivityType.Offline && value.OccurredAtUtc == At(14));
    }

    [Fact]
    public async Task OnlineToGapToOfflineCreatesOneDetectedSubjectEventAtFirstObservation()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await AddOnlineEvidenceAsync(fixture, At(10));
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id, [Observation(fixture.Device, online: false)], At(14), CancellationToken.None);

        var events = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, At(0), At(16), CancellationToken.None);
        var fact = await new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository))
            .GetCurrentFactAsync(fixture.Subject.Id, At(15), CancellationToken.None);

        var detected = Assert.Single(events);
        Assert.Equal(SubjectPresenceEventType.DetectedOfflineAfterGap, detected.EventType);
        Assert.Equal(At(14), detected.ObservedAt);
        var gap = Assert.Single(await fixture.Repository.GetMonitoringGapsAsync(At(0), At(16), CancellationToken.None));
        var baseline = Assert.Single(await fixture.Repository.GetMonitoringGapSubjectBaselinesAsync(gap.Id, CancellationToken.None));
        Assert.Equal(PresenceState.Online, baseline.State);
        Assert.Equal(PresenceState.Offline, fact!.CurrentState);
        Assert.True(fact.StateSinceKnown);
        Assert.Equal(At(14), fact.StateSince);
        Assert.Equal(TimeSpan.FromHours(1), fact.ConfirmedDuration);
    }

    [Fact]
    public async Task OfflineToGapToOnlineCreatesDetectedSubjectEventAndStartsDurationAtFirstObservation()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Offline);
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id, [Observation(fixture.Device, online: true)], At(14), CancellationToken.None);

        var events = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, At(0), At(16), CancellationToken.None);
        var fact = await new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository))
            .GetCurrentFactAsync(fixture.Subject.Id, At(15), CancellationToken.None);

        var detected = Assert.Single(events);
        Assert.Equal(SubjectPresenceEventType.DetectedOnlineAfterGap, detected.EventType);
        Assert.Equal(At(14), detected.ObservedAt);
        Assert.True(fact!.StateSinceKnown);
        Assert.Equal(At(14), fact.StateSince);
        Assert.Equal(TimeSpan.FromHours(1), fact.ConfirmedDuration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameStateAcrossGapDoesNotCreateDetectedSubjectEvent(bool online)
    {
        await using var fixture = await CreateFixtureAsync(online ? PresenceState.Online : PresenceState.Offline);
        if (online) await AddOnlineEvidenceAsync(fixture, At(10));
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id, [Observation(fixture.Device, online)], At(14), CancellationToken.None);

        var fact = await new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository))
            .GetCurrentFactAsync(fixture.Subject.Id, At(15), CancellationToken.None);

        Assert.Empty(await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, At(0), At(16), CancellationToken.None));
        Assert.Equal(online ? PresenceState.Online : PresenceState.Offline, fact!.CurrentState);
        Assert.True(fact.StateSinceKnown);
        Assert.Equal(At(0), fact.StateSince);
    }

    [Fact]
    public async Task MultipleMacSubjectCreatesOnlyOneDetectedEventFromTheFinalAggregateState()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Offline, 2);
        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id,
            [Observation(fixture.Devices[0], online: false), Observation(fixture.Devices[1], online: true)],
            At(10),
            CancellationToken.None);
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id,
            fixture.Devices.Select(value => Observation(value, online: false)).ToArray(),
            At(14),
            CancellationToken.None);

        var events = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, At(0), At(16), CancellationToken.None);
        var detected = Assert.Single(events);
        Assert.Equal(SubjectPresenceEventType.DetectedOfflineAfterGap, detected.EventType);
    }

    [Fact]
    public async Task PersistedGapBaselineKeepsRetryDetectionCorrectAfterPartialSnapshot()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Offline, 2);
        var onlineMember = fixture.Devices[1] with
        {
            CurrentState = PresenceState.Online,
            LastKnownHistoricalState = PresenceState.Online,
            LastStateChangedAt = At(10),
            LastSeenAt = At(10)
        };
        await fixture.Repository.UpdateDeviceAsync(onlineMember, CancellationToken.None);
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");
        var gap = Assert.Single(await fixture.Repository.GetMonitoringGapsAsync(At(11), At(15), CancellationToken.None));

        // This is the durable pre-gap baseline written before a first
        // snapshot mutates members. The following update emulates a failure
        // after one member was written but before the snapshot could finish.
        await fixture.Repository.AddMonitoringGapSubjectBaselineAsync(
            new MonitoringGapSubjectBaseline(gap.Id, fixture.Subject.Id, PresenceState.Online), CancellationToken.None);
        await fixture.Repository.UpdateDeviceAsync(onlineMember with
        {
            CurrentState = PresenceState.Offline,
            LastKnownHistoricalState = PresenceState.Offline,
            LastSeenAt = At(14)
        }, CancellationToken.None);

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id,
            fixture.Devices.Select(value => Observation(value, online: false)).ToArray(),
            At(14),
            CancellationToken.None);

        var detected = Assert.Single(await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, At(0), At(16), CancellationToken.None));
        Assert.Equal(SubjectPresenceEventType.DetectedOfflineAfterGap, detected.EventType);
        Assert.Equal(At(14), detected.ObservedAt);
    }

    [Fact]
    public async Task DetectedOfflineAfterGapStartsQqEpisodeAtFirstObservation()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await AddOnlineEvidenceAsync(fixture, At(10));
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");
        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id, [Observation(fixture.Device, online: false)], At(14), CancellationToken.None);
        await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OfflineFor, 60,
            NotificationChannelType.QQ, NotificationTargetType.Private, "openid", "{name}", At(14), At(14)), CancellationToken.None);

        var service = new NotificationRuleService(
            fixture.Repository,
            new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository)));

        Assert.Empty(await service.EvaluateAsync(At(14).AddSeconds(59), CancellationToken.None));
        var request = Assert.Single(await service.EvaluateAsync(At(15), CancellationToken.None));
        Assert.Equal($"{(int)PresenceState.Offline}:{At(14).UtcTicks}", request.EpisodeId);
    }

    [Fact]
    public async Task SameStateAcrossGapDoesNotUseRecoveredVisualDurationForQqThreshold()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await AddOnlineEvidenceAsync(fixture, At(10));
        await InsertLegacyGapAsync(fixture.Paths, At(12), At(14), "test-gap");
        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(
            fixture.Router.Id, [Observation(fixture.Device, online: true)], At(14), CancellationToken.None);
        await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60 * 60,
            NotificationChannelType.QQ, NotificationTargetType.Private, "openid", "{name}", At(14), At(14)), CancellationToken.None);

        var presence = new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository));
        var fact = await presence.GetCurrentFactAsync(fixture.Subject.Id, At(14).AddMinutes(30), CancellationToken.None);
        var service = new NotificationRuleService(fixture.Repository, presence);

        Assert.Equal(At(0), fact!.StateSince);
        Assert.Equal(TimeSpan.FromHours(14.5), fact.ConfirmedDuration);
        Assert.Empty(await service.EvaluateAsync(At(14).AddMinutes(30), CancellationToken.None));
        var request = Assert.Single(await service.EvaluateAsync(At(15), CancellationToken.None));
        Assert.Equal($"{(int)PresenceState.Online}:{At(14).UtcTicks}", request.EpisodeId);
    }

    private static async Task AddOnlineEvidenceAsync(Fixture fixture, DateTimeOffset at)
    {
        await fixture.Repository.AddEventAsync(new(0, fixture.Device.Id, PresenceEventType.Online, at, PresenceSource.Polling), CancellationToken.None);
        await fixture.Repository.AddSessionAsync(new(0, fixture.Device.Id, at, null, true, false), CancellationToken.None);
    }

    private static async Task InsertLegacyGapAsync(AppPaths paths, DateTimeOffset start, DateTimeOffset end, string reason)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO MonitoringGap(StartedAt,EndedAt,Reason) VALUES($start,$end,$reason)";
        command.Parameters.AddWithValue("$start", start.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Fixture> CreateFixtureAsync(PresenceState state = PresenceState.Offline, int count = 1)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Monitoring-Gap-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        var repository = new SqlitePresenceRepository(paths);
        await repository.InitializeAsync(CancellationToken.None);
        var router = await repository.UpsertRouterAsync(new(0, "gap-test-did", "xiaomi.router.rd03", "gap-test-partner", "测试路由器", null, null, At(0), At(0)), CancellationToken.None);
        var devices = new List<NetworkDevice>();
        for (var index = 0; index < count; index++)
        {
            devices.Add(await repository.InsertDeviceAsync(new NetworkDevice(
                0, router.Id, $"AA:BB:CC:DD:EE:{index + 1:00}", $"设备 {index + 1}", "Phone", null, null,
                "192.168.1.2", "5G", -50, state, At(0).AddDays(-30), At(0), state == PresenceState.Online ? At(0) : null), CancellationToken.None));
        }
        var subject = await repository.CreateSubjectAsync("测试主体", null, Guid.NewGuid(), At(0), CancellationToken.None);
        await repository.SetSubjectDevicesAsync(subject.Id, devices.Select(value => value.Id).ToArray(), At(0), CancellationToken.None);
        return new(root, paths, repository, router, subject, devices);
    }

    private static ObservedNetworkDevice Observation(NetworkDevice device, bool online) =>
        new(device.MacAddress, device.OriginalName, device.OriginName, device.LastIp, online, null, device.ConnectionType, device.Signal);

    private static DateTimeOffset At(int hour) => new(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        string Root,
        AppPaths Paths,
        SqlitePresenceRepository Repository,
        Router Router,
        PresenceSubject Subject,
        IReadOnlyList<NetworkDevice> Devices) : IAsyncDisposable
    {
        public NetworkDevice Device => Devices[0];

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSource : IXiaomiPresenceSource
    {
        private int _calls;
        public TaskCompletionSource<bool> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);

        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Failed.TrySetResult(true);
                throw new InvalidOperationException("poll failed");
            }
            return Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
        }
    }
}
