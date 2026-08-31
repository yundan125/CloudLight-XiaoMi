using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class PresenceRehydrationTests
{
    [Fact]
    public async Task ImportRestoresOfflineStateSinceFromHistoricalDeviceState()
    {
        var now = At(12); var stateSince = now.AddHours(-3); var sourceRoot = TemporaryRoot(); var targetRoot = TemporaryRoot(); var backup = TemporaryFile();
        try
        {
            var source = await CreateRepositoryAsync(sourceRoot); var router = await source.UpsertRouterAsync(RouterAt("import-offline", now), CancellationToken.None);
            var device = await source.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:01", PresenceState.Offline, now.AddHours(-6), now.AddMinutes(-1), stateSince), CancellationToken.None);
            var subject = await source.CreateSubjectAsync("离线设备", null, Guid.NewGuid(), now.AddHours(-6), CancellationToken.None); await source.SetSubjectDevicesAsync(subject.Id, [device.Id], now.AddHours(-6), CancellationToken.None);
            await source.AddEventAsync(new PresenceEvent(0, device.Id, PresenceEventType.Offline, stateSince, PresenceSource.Polling), CancellationToken.None);
            await new PresenceDataTransferService(new AppPaths(sourceRoot)).ExportAsync(backup, CancellationToken.None);

            var target = await CreateRepositoryAsync(targetRoot); await new PresenceDataTransferService(new AppPaths(targetRoot)).ImportAsync(backup, CancellationToken.None);
            var importedSubject = Assert.Single(await target.GetSubjectsAsync(CancellationToken.None), value => value.DisplayName == "离线设备"); var fact = await new SubjectPresenceService(target, new PresenceStatisticsService(target)).GetCurrentFactAsync(importedSubject.Id, now, CancellationToken.None);
            Assert.Equal(PresenceState.Offline, fact!.CurrentState); Assert.True(fact.StateSinceKnown); Assert.Equal(stateSince, fact.StateSince); Assert.Equal(TimeSpan.FromHours(3), fact.ConfirmedDuration);
        }
        finally { Delete(sourceRoot); Delete(targetRoot); Delete(backup); }
    }

    [Fact]
    public async Task ImportRestoresMultipleDevicesAndAggregateStateSince()
    {
        var now = At(12); var onlineSince = now.AddHours(-2); var sourceRoot = TemporaryRoot(); var targetRoot = TemporaryRoot(); var backup = TemporaryFile();
        try
        {
            var source = await CreateRepositoryAsync(sourceRoot); var router = await source.UpsertRouterAsync(RouterAt("import-multiple", now), CancellationToken.None);
            var online = await source.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:02", PresenceState.Online, now.AddHours(-8), now.AddMinutes(-1), onlineSince), CancellationToken.None);
            var offline = await source.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:03", PresenceState.Offline, now.AddHours(-8), now.AddMinutes(-2), now.AddHours(-3)), CancellationToken.None);
            var subject = await source.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), now.AddHours(-8), CancellationToken.None); await source.SetSubjectDevicesAsync(subject.Id, [online.Id, offline.Id], now.AddHours(-8), CancellationToken.None);
            await source.AddEventAsync(new PresenceEvent(0, online.Id, PresenceEventType.Online, onlineSince, PresenceSource.Polling), CancellationToken.None); await source.AddSessionAsync(new PresenceSession(0, online.Id, onlineSince, null, true, false), CancellationToken.None);
            await source.AddEventAsync(new PresenceEvent(0, offline.Id, PresenceEventType.Offline, now.AddHours(-3), PresenceSource.Polling), CancellationToken.None);
            await new PresenceDataTransferService(new AppPaths(sourceRoot)).ExportAsync(backup, CancellationToken.None);

            var target = await CreateRepositoryAsync(targetRoot); var targetRouter = await target.UpsertRouterAsync(RouterAt("import-multiple", now.AddHours(-10)), CancellationToken.None);
            await target.InsertDeviceAsync(DeviceAt(targetRouter.Id, online.MacAddress, PresenceState.Offline, now.AddHours(-9), now.AddHours(-6), now.AddHours(-7)), CancellationToken.None);
            await target.InsertDeviceAsync(DeviceAt(targetRouter.Id, offline.MacAddress, PresenceState.Online, now.AddHours(-9), now.AddHours(-6), now.AddHours(-7)), CancellationToken.None);
            await new PresenceDataTransferService(new AppPaths(targetRoot)).ImportAsync(backup, CancellationToken.None);

            var devices = await target.GetDevicesAsync(targetRouter.Id, CancellationToken.None); Assert.Equal(PresenceState.Online, Assert.Single(devices, value => value.MacAddress == online.MacAddress).CurrentState); Assert.Equal(PresenceState.Offline, Assert.Single(devices, value => value.MacAddress == offline.MacAddress).CurrentState);
            var importedSubject = Assert.Single(await target.GetSubjectsAsync(CancellationToken.None), value => value.DisplayName == "爸爸"); var fact = await new SubjectPresenceService(target, new PresenceStatisticsService(target)).GetCurrentFactAsync(importedSubject.Id, now, CancellationToken.None);
            Assert.Equal(PresenceState.Online, fact!.CurrentState); Assert.True(fact.StateSinceKnown); Assert.Equal(onlineSince, fact.StateSince); Assert.Equal(TimeSpan.FromHours(2), fact.ConfirmedDuration); Assert.Equal(2, fact.Members.Count);
        }
        finally { Delete(sourceRoot); Delete(targetRoot); Delete(backup); }
    }

    [Fact]
    public async Task ImportPreservesOnlineStateSinceAcrossMonitoringGap()
    {
        var now = At(12); var onlineSince = now.AddHours(-6); var gapStart = now.AddHours(-2); var gapEnd = now.AddHours(-1); var sourceRoot = TemporaryRoot(); var targetRoot = TemporaryRoot(); var backup = TemporaryFile();
        try
        {
            var source = await CreateRepositoryAsync(sourceRoot); var router = await source.UpsertRouterAsync(RouterAt("import-gap", now), CancellationToken.None); var device = await source.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:04", PresenceState.Online, now.AddHours(-8), now.AddMinutes(-1), onlineSince), CancellationToken.None);
            var subject = await source.CreateSubjectAsync("缺口设备", null, Guid.NewGuid(), now.AddHours(-8), CancellationToken.None); await source.SetSubjectDevicesAsync(subject.Id, [device.Id], now.AddHours(-8), CancellationToken.None); await source.AddEventAsync(new PresenceEvent(0, device.Id, PresenceEventType.Online, onlineSince, PresenceSource.Polling), CancellationToken.None); await source.AddSessionAsync(new PresenceSession(0, device.Id, onlineSince, null, true, false), CancellationToken.None);
            var gap = await source.StartMonitoringGapAsync(gapStart, "导入测试", CancellationToken.None); await source.EndMonitoringGapAsync(gap, gapEnd, CancellationToken.None); await new PresenceDataTransferService(new AppPaths(sourceRoot)).ExportAsync(backup, CancellationToken.None);

            var target = await CreateRepositoryAsync(targetRoot); await new PresenceDataTransferService(new AppPaths(targetRoot)).ImportAsync(backup, CancellationToken.None); var importedSubject = Assert.Single(await target.GetSubjectsAsync(CancellationToken.None)); var service = new SubjectPresenceService(target, new PresenceStatisticsService(target)); var fact = await service.GetCurrentFactAsync(importedSubject.Id, now, CancellationToken.None); var timeline = await service.GetTimelineAsync(importedSubject.Id, onlineSince, now, CancellationToken.None);
            Assert.Equal(PresenceState.Online, fact!.CurrentState); Assert.True(fact.StateSinceKnown); Assert.Equal(onlineSince, fact.StateSince); Assert.Equal(TimeSpan.FromHours(6), fact.ConfirmedDuration); Assert.Contains(timeline, value => value.State == PresenceState.Unknown && value.Start == gapStart && value.End == gapEnd);
        }
        finally { Delete(sourceRoot); Delete(targetRoot); Delete(backup); }
    }

    [Fact]
    public async Task RestartAndFirstOnlinePollPreserveKnownStateSince()
    {
        var root = TemporaryRoot(); var stateSince = At(8); var now = At(12);
        try
        {
            var fixture = await CreateRuntimeFixtureAsync(root, PresenceState.Online, stateSince); var reopened = await CreateRepositoryAsync(root); var beforeEvents = await reopened.GetEventsAsync(fixture.Device.Id, CancellationToken.None); await new PresenceStateMachine(reopened).ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, online: true)], now, CancellationToken.None);
            var restored = await reopened.GetDeviceAsync(fixture.Device.Id, CancellationToken.None); var fact = await new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened)).GetCurrentFactAsync(fixture.Subject.Id, now, CancellationToken.None);
            Assert.Equal(stateSince, restored!.LastStateChangedAt); Assert.Equal(beforeEvents.Count, (await reopened.GetEventsAsync(fixture.Device.Id, CancellationToken.None)).Count); Assert.Equal(PresenceState.Online, fact!.CurrentState); Assert.Equal(stateSince, fact.StateSince); Assert.True(fact.StateSinceKnown); Assert.Equal(TimeSpan.FromHours(4), fact.ConfirmedDuration);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RestartAndFirstOfflinePollPreserveKnownStateSince()
    {
        var root = TemporaryRoot(); var stateSince = At(8); var now = At(12);
        try
        {
            var fixture = await CreateRuntimeFixtureAsync(root, PresenceState.Offline, stateSince); var reopened = await CreateRepositoryAsync(root); var beforeEvents = await reopened.GetEventsAsync(fixture.Device.Id, CancellationToken.None); await new PresenceStateMachine(reopened).ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, online: false)], now, CancellationToken.None);
            var restored = await reopened.GetDeviceAsync(fixture.Device.Id, CancellationToken.None); var fact = await new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened)).GetCurrentFactAsync(fixture.Subject.Id, now, CancellationToken.None);
            Assert.Equal(stateSince, restored!.LastStateChangedAt); Assert.Equal(beforeEvents.Count, (await reopened.GetEventsAsync(fixture.Device.Id, CancellationToken.None)).Count); Assert.Equal(PresenceState.Offline, fact!.CurrentState); Assert.Equal(stateSince, fact.StateSince); Assert.True(fact.StateSinceKnown); Assert.Equal(TimeSpan.FromHours(4), fact.ConfirmedDuration);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task RestartPreservesEarliestOnlineStateSinceForMultipleMacs()
    {
        var root = TemporaryRoot(); var firstSince = At(7); var secondSince = At(9); var now = At(12);
        try
        {
            var repository = await CreateRepositoryAsync(root); var router = await repository.UpsertRouterAsync(RouterAt("restart-multiple", now), CancellationToken.None); var first = await repository.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:05", PresenceState.Online, At(6), firstSince, firstSince), CancellationToken.None); var second = await repository.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:06", PresenceState.Online, At(6), secondSince, secondSince), CancellationToken.None); var subject = await repository.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), At(6), CancellationToken.None); await repository.SetSubjectDevicesAsync(subject.Id, [first.Id, second.Id], At(6), CancellationToken.None); await repository.AddSessionAsync(new PresenceSession(0, first.Id, firstSince, null, true, false), CancellationToken.None); await repository.AddSessionAsync(new PresenceSession(0, second.Id, secondSince, null, true, false), CancellationToken.None);
            var reopened = await CreateRepositoryAsync(root); await new PresenceStateMachine(reopened).ApplySnapshotAsync(router.Id, [Observation(first, true), Observation(second, true)], now, CancellationToken.None); var fact = await new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened)).GetCurrentFactAsync(subject.Id, now, CancellationToken.None);
            Assert.Equal(PresenceState.Online, fact!.CurrentState); Assert.True(fact.StateSinceKnown); Assert.Equal(firstSince, fact.StateSince); Assert.Equal(TimeSpan.FromHours(5), fact.ConfirmedDuration);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestartWithMonitoringGapAndSameStatePreservesStateSince(bool online)
    {
        var root = TemporaryRoot(); var stateSince = At(6); var gapStart = At(8); var now = At(12);
        try
        {
            var expected = online ? PresenceState.Online : PresenceState.Offline;
            var fixture = await CreateRuntimeFixtureAsync(root, expected, stateSince); var gap = await fixture.Repository.StartMonitoringGapAsync(gapStart, "restart", CancellationToken.None); var reopened = await CreateRepositoryAsync(root); await new PresenceStateMachine(reopened).ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, online)], now, CancellationToken.None); await reopened.EndMonitoringGapAsync(gap, now, CancellationToken.None); var service = new SubjectPresenceService(reopened, new PresenceStatisticsService(reopened)); var fact = await service.GetCurrentFactAsync(fixture.Subject.Id, now, CancellationToken.None); var timeline = await service.GetTimelineAsync(fixture.Subject.Id, stateSince, now, CancellationToken.None);
            var persisted = await reopened.GetSubjectCurrentStateAsync(fixture.Subject.Id, CancellationToken.None);
            Assert.Equal(expected, fact!.CurrentState); Assert.True(fact.StateSinceKnown); Assert.Equal(stateSince, fact.StateSince); Assert.Equal(TimeSpan.FromHours(6), fact.ConfirmedDuration); Assert.Equal(stateSince, persisted!.StateSince); Assert.Equal(now, persisted.LastObservedAt); Assert.DoesNotContain(await reopened.GetSubjectPresenceEventsAsync(fixture.Subject.Id, stateSince, now, CancellationToken.None), value => value.EventType is SubjectPresenceEventType.ConfirmedOnline or SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.DetectedOnlineAfterGap or SubjectPresenceEventType.DetectedOfflineAfterGap); Assert.Contains(timeline, value => value.State == PresenceState.Unknown && value.UnobservedReason == "restart");
        }
        finally { Delete(root); }
    }

    private static async Task<SqlitePresenceRepository> CreateRepositoryAsync(string root)
    {
        var repository = new SqlitePresenceRepository(new AppPaths(root)); await repository.InitializeAsync(CancellationToken.None); return repository;
    }

    private static async Task<RuntimeFixture> CreateRuntimeFixtureAsync(string root, PresenceState state, DateTimeOffset stateSince)
    {
        var repository = await CreateRepositoryAsync(root); var router = await repository.UpsertRouterAsync(RouterAt("restart-single", stateSince), CancellationToken.None); var device = await repository.InsertDeviceAsync(DeviceAt(router.Id, "AA:BB:CC:DD:EE:07", state, stateSince.AddHours(-2), stateSince, stateSince), CancellationToken.None); var subject = await repository.CreateSubjectAsync("测试设备", null, Guid.NewGuid(), stateSince.AddHours(-2), CancellationToken.None); await repository.SetSubjectDevicesAsync(subject.Id, [device.Id], stateSince.AddHours(-2), CancellationToken.None);
        await repository.AddEventAsync(new PresenceEvent(0, device.Id, state == PresenceState.Online ? PresenceEventType.Online : PresenceEventType.Offline, stateSince, PresenceSource.Polling), CancellationToken.None); if (state == PresenceState.Online) await repository.AddSessionAsync(new PresenceSession(0, device.Id, stateSince, null, true, false), CancellationToken.None);
        return new(repository, router, device, subject);
    }

    private static ObservedNetworkDevice Observation(NetworkDevice device, bool online) => new(device.MacAddress, device.OriginalName, device.OriginName, device.LastIp, online, null, device.ConnectionType, device.Signal);
    private static NetworkDevice DeviceAt(long routerId, string mac, PresenceState state, DateTimeOffset firstSeen, DateTimeOffset lastSeen, DateTimeOffset stateSince) => new(0, routerId, mac, "设备", "设备", null, null, "192.168.1.2", "5G", -50, state, firstSeen, lastSeen, stateSince);
    private static Router RouterAt(string did, DateTimeOffset at) => new(0, did, "xiaomi.router.rd03", did + "-partner", "测试路由器", null, null, at, at);
    private static DateTimeOffset At(int hour) => new(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);
    private static string TemporaryRoot() { var root = Path.Combine(Path.GetTempPath(), "CloudLight-Presence-Rehydration", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static string TemporaryFile() => Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.clpresence");
    private static void Delete(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
    private sealed record RuntimeFixture(SqlitePresenceRepository Repository, Router Router, NetworkDevice Device, PresenceSubject Subject);
}
