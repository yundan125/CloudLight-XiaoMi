using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class PresenceStateMachineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CloudLight-Presence-Tests", Guid.NewGuid().ToString("N"));
    private SqlitePresenceRepository _repository = null!;
    private PresenceStateMachine _machine = null!;
    private Router _router = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqlitePresenceRepository(new AppPaths(_root)); await _repository.InitializeAsync(CancellationToken.None);
        _machine = new PresenceStateMachine(_repository); var now = DateTimeOffset.UtcNow;
        _router = await _repository.UpsertRouterAsync(new Router(0, "did-test", "xiaomi.router.rd03", "partner-test", "router", "home", "room", now, now), CancellationToken.None);
    }

    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); return Task.CompletedTask; }

    [Fact]
    public async Task OfflineToOnlineCreatesOneOnlineEventAndSession()
    {
        var start = DateTimeOffset.UtcNow;
        await Apply(false, start); await Apply(true, start.AddSeconds(10));
        var device = (await _repository.GetDevicesAsync(_router.Id, CancellationToken.None)).Single();
        var events = await _repository.GetEventsAsync(device.Id, CancellationToken.None); var sessions = await _repository.GetSessionsAsync(device.Id, CancellationToken.None);
        Assert.Single(events, value => value.EventType == PresenceEventType.Online); Assert.Single(sessions); Assert.True(sessions[0].StartKnown);
    }

    [Fact]
    public async Task DuplicateOnlineStateCreatesNoDuplicateEvents()
    {
        var start = DateTimeOffset.UtcNow;
        await Apply(true, start); await Apply(true, start.AddSeconds(10)); await Apply(true, start.AddSeconds(20));
        var device = (await _repository.GetDevicesAsync(_router.Id, CancellationToken.None)).Single();
        var events = await _repository.GetEventsAsync(device.Id, CancellationToken.None); var sessions = await _repository.GetSessionsAsync(device.Id, CancellationToken.None);
        Assert.Single(events); Assert.Equal(PresenceEventType.InitialObservation, events[0].EventType); Assert.Single(sessions); Assert.False(sessions[0].StartKnown);
    }

    [Fact]
    public async Task OnlineToOfflineClosesSessionAndCreatesOfflineEvent()
    {
        var start = DateTimeOffset.UtcNow; await Apply(true, start); await Apply(false, start.AddSeconds(10));
        var device = (await _repository.GetDevicesAsync(_router.Id, CancellationToken.None)).Single();
        var events = await _repository.GetEventsAsync(device.Id, CancellationToken.None); var session = Assert.Single(await _repository.GetSessionsAsync(device.Id, CancellationToken.None));
        Assert.Single(events, value => value.EventType == PresenceEventType.Offline); Assert.NotNull(session.EndedAt); Assert.True(session.EndKnown);
    }

    [Fact]
    public async Task SqliteRestoresMetadataHistorySessionAndState()
    {
        var start = DateTimeOffset.UtcNow; await Apply(true, start); await Apply(false, start.AddSeconds(10));
        var device = (await _repository.GetDevicesAsync(_router.Id, CancellationToken.None)).Single();
        await _repository.UpdateDeviceMetadataAsync(device.Id, "我的手机", "主力手机", CancellationToken.None);
        var reopened = new SqlitePresenceRepository(new AppPaths(_root)); await reopened.InitializeAsync(CancellationToken.None);
        var restored = Assert.Single(await reopened.GetDevicesAsync(_router.Id, CancellationToken.None));
        Assert.Equal("我的手机", restored.CustomName); Assert.Equal("主力手机", restored.Note); Assert.Equal(PresenceState.Offline, restored.CurrentState);
        Assert.Equal(2, (await reopened.GetEventsAsync(restored.Id, CancellationToken.None)).Count); Assert.Single(await reopened.GetSessionsAsync(restored.Id, CancellationToken.None));
    }

    private Task Apply(bool online, DateTimeOffset at) => _machine.ApplySnapshotAsync(_router.Id,
        [new ObservedNetworkDevice("AA:BB:CC:DD:EE:FF", "Phone", "Phone", "192.168.1.2", online, null, "5G", -55)], at, CancellationToken.None);
}
