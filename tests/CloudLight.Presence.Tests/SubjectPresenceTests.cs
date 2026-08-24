using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class SubjectPresenceTests
{
    [Fact]
    public async Task AnyOnlineMemberMakesSubjectOnline()
    {
        await WithSubject(async (repository, service, subject, a, _) =>
        {
            var now = At(14); await repository.AddSessionAsync(new(0, a.Id, now.AddHours(-1), null, true, false), CancellationToken.None);
            var snapshot = await service.GetSnapshotAsync(subject.Id, now, CancellationToken.None); Assert.Equal(PresenceState.Online, snapshot!.CurrentState); Assert.Equal(a.Id, snapshot.ActiveDevice!.Id);
        }, aOnline: true);
    }

    [Fact]
    public async Task BandSwitchWithinGraceDoesNotCreateOfflineSegment()
    {
        await WithSubject(async (repository, service, subject, a, b) =>
        {
            var from = At(10); var to = At(12); await repository.AddSessionAsync(new(0, a.Id, from, At(11), true, true), CancellationToken.None); await repository.AddSessionAsync(new(0, b.Id, At(11).AddSeconds(10), null, true, false), CancellationToken.None);
            var timeline = await service.GetTimelineAsync(subject.Id, from, to, CancellationToken.None); Assert.DoesNotContain(timeline, value => value.State == PresenceState.Offline); Assert.Equal(TimeSpan.FromHours(2), timeline.Where(value => value.State == PresenceState.Online).Aggregate(TimeSpan.Zero, (sum, value) => sum + (value.End - value.Start)));
            var snapshot = await service.GetSnapshotAsync(subject.Id, to, CancellationToken.None); Assert.Equal(PresenceState.Online, snapshot!.CurrentState); Assert.Equal(from, snapshot.LastStateChangedAt);
            var activities = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false); Assert.Single(activities); Assert.Equal(SubjectActivityType.Online, activities[0].Type); Assert.Equal(from, activities[0].OccurredAtUtc);
        }, bOnline: true);
    }

    [Fact]
    public async Task RepeatedSnapshotsKeepAggregateStateSinceAcrossMonitoringGaps()
    {
        await WithSubject(async (repository, service, subject, a, _) =>
        {
            var started = At(10); await repository.AddSessionAsync(new(0, a.Id, started, null, true, false), CancellationToken.None);
            var gap = await repository.StartMonitoringGapAsync(At(11), "restart", CancellationToken.None); await repository.EndMonitoringGapAsync(gap, At(11).AddSeconds(10), CancellationToken.None);
            var first = await service.GetSnapshotAsync(subject.Id, At(12), CancellationToken.None); var second = await service.GetSnapshotAsync(subject.Id, At(12).AddMinutes(2), CancellationToken.None);
            Assert.Equal(started, first!.LastStateChangedAt); Assert.Equal(first.LastStateChangedAt, second!.LastStateChangedAt); Assert.Equal(PresenceState.Online, second.CurrentState);
            var timeline = await service.GetTimelineAsync(subject.Id, started, At(12), CancellationToken.None);
            var hidden = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false); Assert.Single(hidden); Assert.Equal(new SubjectActivityItem(started, SubjectActivityType.Online), hidden[0]);
            var shown = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: true); Assert.Equal([SubjectActivityType.Online, SubjectActivityType.UnknownPeriod, SubjectActivityType.Online], shown.Select(value => value.Type).ToArray());
        }, aOnline: true);
    }

    [Fact]
    public void ActivityProjectionUsesAggregateStateBoundariesAndAlternatesKnownStates()
    {
        var timeline = new PresenceTimelineSegment[]
        {
            new(At(18), At(18).AddMinutes(30), PresenceState.Online),
            new(At(18).AddMinutes(30), At(19), PresenceState.Unknown),
            new(At(19), At(20), PresenceState.Online),
            new(At(20), At(21), PresenceState.Offline),
            new(At(21), At(22), PresenceState.Unknown),
            new(At(22), At(23), PresenceState.Offline),
            new(At(23), At(23).AddHours(1), PresenceState.Online)
        };

        var activities = SubjectActivityBuilder.Build(timeline, includeUnknownPeriods: false);

        Assert.Equal([SubjectActivityType.Online, SubjectActivityType.Offline, SubjectActivityType.Online], activities.Select(value => value.Type).ToArray());
        Assert.Equal([At(23), At(20), At(18)], activities.Select(value => value.OccurredAtUtc).ToArray());
    }

    [Fact]
    public async Task AllOfflineBeyondGraceMakesSubjectOffline()
    {
        await WithSubject(async (repository, service, subject, a, _) =>
        {
            var from = At(10); var to = At(12); await repository.AddSessionAsync(new(0, a.Id, from, At(11), true, true), CancellationToken.None); var timeline = await service.GetTimelineAsync(subject.Id, from, to, CancellationToken.None); Assert.Equal(PresenceState.Offline, timeline[^1].State); Assert.Equal(At(11), timeline[^1].Start);
        });
    }

    [Fact]
    public async Task OverlappingMemberSessionsUseIntervalUnionAndGapStaysUnknown()
    {
        await WithSubject(async (repository, service, subject, a, b) =>
        {
            var from = At(10); var to = At(14); await repository.AddSessionAsync(new(0, a.Id, from, At(12), true, true), CancellationToken.None); await repository.AddSessionAsync(new(0, b.Id, At(11), At(13), true, true), CancellationToken.None);
            var gap = await repository.StartMonitoringGapAsync(At(11).AddMinutes(30), "test", CancellationToken.None); await repository.EndMonitoringGapAsync(gap, At(12), CancellationToken.None);
            var statistics = await service.GetSubjectStatisticsAsync(subject.Id, from, to, CancellationToken.None); Assert.Equal(TimeSpan.FromHours(2.5), statistics.KnownOnlineDuration); Assert.Equal(TimeSpan.FromMinutes(30), statistics.UnknownDuration);
        });
    }

    private static async Task WithSubject(Func<SqlitePresenceRepository, SubjectPresenceService, PresenceSubject, NetworkDevice, NetworkDevice, Task> test, bool aOnline = false, bool bOnline = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Subject-Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root)); await repository.InitializeAsync(CancellationToken.None); var first = At(10);
            var router = await repository.UpsertRouterAsync(new(0, "subject-did", "router", "partner", "Router", null, null, first, first), CancellationToken.None);
            var a = await repository.InsertDeviceAsync(Device(router.Id, "2A:97:25:AD:72:F3", first, aOnline), CancellationToken.None); var b = await repository.InsertDeviceAsync(Device(router.Id, "DA:92:47:1D:EE:32", first, bOnline), CancellationToken.None);
            var subject = await repository.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), first, CancellationToken.None); await repository.SetSubjectDevicesAsync(subject.Id, [a.Id, b.Id], first, CancellationToken.None);
            await test(repository, new(repository, new PresenceStatisticsService(repository)), subject, a, b);
        }
        finally { Directory.Delete(root, true); }
    }
    private static NetworkDevice Device(long router, string mac, DateTimeOffset at, bool online) => new(0, router, mac, "Phone", "Phone", null, null, "192.168.1.2", "5G", -45, online ? PresenceState.Online : PresenceState.Offline, at, at, at);
    private static DateTimeOffset At(int hour) => new(2026, 8, 24, hour, 0, 0, TimeSpan.Zero);
}
