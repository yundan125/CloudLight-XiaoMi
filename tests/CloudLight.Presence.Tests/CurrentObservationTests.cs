using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class CurrentObservationTests
{
    [Fact]
    public async Task StartupBeforeFirstPollIsUnknownAndFirstOfflinePollIsOffline()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        var source = new ControlledSource
        {
            Observations = fixture.Devices.Select(device => Observation(device, online: false)).ToArray(),
            WaitForRelease = true
        };
        var monitor = new PresenceMonitor(source, fixture.Repository, new PresenceStateMachine(fixture.Repository));
        var applied = SnapshotApplied(monitor);

        await monitor.StartAsync(fixture.Router, CancellationToken.None);
        await source.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var service = new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository));
        var beforePoll = await service.GetCurrentFactAsync(fixture.Subject.Id, At(11), CancellationToken.None);
        Assert.Equal(PresenceState.Unknown, beforePoll!.CurrentState);
        Assert.All(beforePoll.Members, value => Assert.Equal(PresenceState.Unknown, value.CurrentObservedState));
        Assert.All(beforePoll.Members, value => Assert.Equal(PresenceState.Online, value.LastKnownHistoricalState));
        var beforePollCard = PresenceCardViewModel.ForSubject((await service.GetSnapshotAsync(fixture.Subject.Id, At(11), CancellationToken.None))!, _ => { });
        Assert.Equal("正在检测在线设备", beforePollCard.CurrentConnection);

        source.Release.TrySetResult(true);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var afterPoll = await service.GetCurrentFactAsync(fixture.Subject.Id, At(12), CancellationToken.None);
        Assert.Equal(PresenceState.Offline, afterPoll!.CurrentState);
        Assert.All(afterPoll.Members, value => Assert.Equal(PresenceState.Offline, value.CurrentObservedState));
        await monitor.StopAsync("test", CancellationToken.None);
    }

    [Fact]
    public async Task FirstPollWithAllOfflineMembersCannotReuseHistoricalOnline()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await fixture.Repository.StartApplicationRunAsync(At(10), CancellationToken.None);
        var machine = new PresenceStateMachine(fixture.Repository);

        await machine.ApplySnapshotAsync(fixture.Router.Id,
            fixture.Devices.Select(device => Observation(device, online: false)).ToArray(), At(12), CancellationToken.None);

        var fact = await CurrentFactAsync(fixture);
        Assert.Equal(PresenceState.Offline, fact!.CurrentState);
        Assert.Null(fact.ActiveDevice);
    }

    [Fact]
    public async Task FirstPollWithAnyOnlineMemberMakesSubjectOnline()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Offline);
        await fixture.Repository.StartApplicationRunAsync(At(10), CancellationToken.None);
        var observations = fixture.Devices.Select((device, index) => Observation(device, online: index == 1)).ToArray();

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(fixture.Router.Id, observations, At(12), CancellationToken.None);

        var fact = await CurrentFactAsync(fixture);
        Assert.Equal(PresenceState.Online, fact!.CurrentState);
        Assert.Equal(fixture.Devices[1].MacAddress, fact.ActiveDevice!.MacAddress);
    }

    [Fact]
    public async Task FailedFirstPollLeavesSubjectUnknown()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        var source = new ControlledSource { ThrowOnPoll = true };
        var monitor = new PresenceMonitor(source, fixture.Repository, new PresenceStateMachine(fixture.Repository));

        try
        {
            await monitor.StartAsync(fixture.Router, CancellationToken.None);
            await source.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var fact = await CurrentFactAsync(fixture);
            Assert.Equal(PresenceState.Unknown, fact!.CurrentState);
            Assert.All(fact.Members, value => Assert.Equal(PresenceState.Unknown, value.CurrentObservedState));
        }
        finally
        {
            await monitor.StopAsync("test", CancellationToken.None);
        }
    }

    [Fact]
    public async Task MainCountsDoNotCountHistoricalOnlineAsCurrentOnline()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online, 6);
        await fixture.Repository.StartApplicationRunAsync(At(10), CancellationToken.None);
        await fixture.Repository.SetSubjectDevicesAsync(fixture.Subject.Id, [fixture.Devices[0].Id], At(10), CancellationToken.None);
        foreach (var device in fixture.Devices.Skip(1))
        {
            var subject = await fixture.Repository.CreateSubjectAsync(device.DisplayName, null, Guid.NewGuid(), At(10), CancellationToken.None);
            await fixture.Repository.SetSubjectDevicesAsync(subject.Id, [device.Id], At(10), CancellationToken.None);
        }
        foreach (var device in fixture.Devices.Take(5))
            await fixture.Repository.UpdateDeviceAsync(device with { CurrentState = PresenceState.Offline }, CancellationToken.None);

        var paths = new AppPaths(fixture.Root);
        var source = new ControlledSource();
        var monitor = new PresenceMonitor(source, fixture.Repository, new PresenceStateMachine(fixture.Repository));
        var viewModel = new MainViewModel(fixture.Repository, source, monitor, new JsonSettingsStore(paths));
        viewModel.SelectedRouter = fixture.Router;
        await viewModel.RefreshCardsAsync();

        Assert.Equal(6, viewModel.AllCount);
        Assert.Equal(0, viewModel.OnlineCount);
        Assert.Equal(5, viewModel.OfflineCount);
        Assert.Equal(1, viewModel.UnknownCount);
    }

    [Fact]
    public async Task MainSubjectDetailAndMembersUseTheSameCurrentState()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Offline);
        await fixture.Repository.StartApplicationRunAsync(At(10), CancellationToken.None);
        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(fixture.Router.Id,
            fixture.Devices.Select(device => Observation(device, online: false)).ToArray(), At(12), CancellationToken.None);
        var service = new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository));
        var snapshot = await service.GetSnapshotAsync(fixture.Subject.Id, At(12), CancellationToken.None);
        var card = PresenceCardViewModel.ForSubject(snapshot!, _ => { });
        var monitor = new PresenceMonitor(new ControlledSource(), fixture.Repository, new PresenceStateMachine(fixture.Repository));
        using var detail = new SubjectDetailViewModel(fixture.Repository, service, monitor, fixture.Subject);
        await detail.LoadAsync();

        Assert.Equal(PresenceState.Offline, card.CurrentState);
        Assert.Equal(PresenceState.Offline, detail.CurrentState);
        Assert.Equal("离线", detail.State);
        Assert.Equal("当前没有在线设备", detail.CurrentConnection);
        Assert.All(detail.Members, value => Assert.Equal("○", value.StateMark));
        Assert.Null(snapshot!.ActiveDevice);
    }

    [Fact]
    public async Task GapThenSameOnlinePollPreservesStateSinceWithoutInventingEvent()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        var gap = await fixture.Repository.StartMonitoringGapAsync(At(8), "restart", CancellationToken.None);
        await fixture.Repository.EndMonitoringGapAsync(gap, At(9), CancellationToken.None);
        await fixture.Repository.StartApplicationRunAsync(At(10), CancellationToken.None);
        var beforeEvents = await fixture.Repository.GetEventsAsync(fixture.Devices[0].Id, CancellationToken.None);

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(fixture.Router.Id,
            fixture.Devices.Select(device => Observation(device, online: true)).ToArray(), At(12), CancellationToken.None);
        var fact = await CurrentFactAsync(fixture);
        var afterEvents = await fixture.Repository.GetEventsAsync(fixture.Devices[0].Id, CancellationToken.None);

        Assert.Equal(PresenceState.Online, fact!.CurrentState);
        Assert.True(fact.StateSinceKnown);
        Assert.Equal(At(6), fact.StateSince);
        Assert.Equal(TimeSpan.FromHours(6), fact.ConfirmedDuration);
        Assert.Equal(beforeEvents.Count, afterEvents.Count);
    }

    [Fact]
    public async Task ContinuousSameOnlinePollPreservesHistoricalStateSince()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        await fixture.Repository.StartApplicationRunAsync(At(10), CancellationToken.None);
        var beforeEvents = await fixture.Repository.GetEventsAsync(fixture.Devices[0].Id, CancellationToken.None);

        await new PresenceStateMachine(fixture.Repository).ApplySnapshotAsync(fixture.Router.Id,
            fixture.Devices.Select(device => Observation(device, online: true)).ToArray(), At(12), CancellationToken.None);
        var fact = await CurrentFactAsync(fixture);
        var afterEvents = await fixture.Repository.GetEventsAsync(fixture.Devices[0].Id, CancellationToken.None);

        Assert.Equal(PresenceState.Online, fact!.CurrentState);
        Assert.True(fact.StateSinceKnown);
        Assert.Equal(At(6), fact.StateSince);
        Assert.Equal(beforeEvents.Count, afterEvents.Count);
    }

    [Fact]
    public async Task MultiMacBandSwitchKeepsAggregateOnlineStateSince()
    {
        await using var fixture = await CreateFixtureAsync(PresenceState.Online);
        var machine = new PresenceStateMachine(fixture.Repository);

        await machine.ApplySnapshotAsync(fixture.Router.Id,
            [Observation(fixture.Devices[0], online: false), Observation(fixture.Devices[1], online: true)], At(10), CancellationToken.None);
        await machine.ApplySnapshotAsync(fixture.Router.Id,
            [Observation(fixture.Devices[0], online: true), Observation(fixture.Devices[1], online: false)], At(11), CancellationToken.None);

        var fact = await CurrentFactAsync(fixture);
        var persisted = await fixture.Repository.GetSubjectCurrentStateAsync(fixture.Subject.Id, CancellationToken.None);

        Assert.Equal(PresenceState.Online, fact!.CurrentState);
        Assert.True(fact.StateSinceKnown);
        Assert.Equal(At(6), fact.StateSince);
        Assert.Equal(At(6), persisted!.StateSince);
        Assert.DoesNotContain(await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, At(0), At(12), CancellationToken.None), value => value.EventType is SubjectPresenceEventType.ConfirmedOnline or SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.DetectedOnlineAfterGap or SubjectPresenceEventType.DetectedOfflineAfterGap);
    }

    private static async Task<SubjectPresenceFact?> CurrentFactAsync(Fixture fixture) =>
        await new SubjectPresenceService(fixture.Repository, new PresenceStatisticsService(fixture.Repository))
            .GetCurrentFactAsync(fixture.Subject.Id, At(12), CancellationToken.None);

    private static TaskCompletionSource<bool> SnapshotApplied(PresenceMonitor monitor)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) => { monitor.SnapshotApplied -= handler; completion.TrySetResult(true); };
        monitor.SnapshotApplied += handler;
        return completion;
    }

    private static async Task<Fixture> CreateFixtureAsync(PresenceState state, int count = 2)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Current-Observation-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = new SqlitePresenceRepository(new AppPaths(root));
        await repository.InitializeAsync(CancellationToken.None);
        var router = await repository.UpsertRouterAsync(new Router(0, $"did-current-{Guid.NewGuid():N}", "xiaomi.router.rd03", $"partner-current-{Guid.NewGuid():N}", "测试路由器", null, null, At(5), At(5)), CancellationToken.None);
        var devices = new List<NetworkDevice>();
        for (var index = 0; index < count; index++)
        {
            var device = await repository.InsertDeviceAsync(new NetworkDevice(0, router.Id, Mac(index), $"设备 {index}", $"设备 {index}", null, null, "192.168.1.2", "5G", -50, state, At(5), At(6), At(6)), CancellationToken.None);
            devices.Add(device);
            await repository.AddEventAsync(new PresenceEvent(0, device.Id, state == PresenceState.Online ? PresenceEventType.Online : PresenceEventType.Offline, At(6), PresenceSource.Polling), CancellationToken.None);
            if (state == PresenceState.Online)
                await repository.AddSessionAsync(new PresenceSession(0, device.Id, At(6), null, true, false), CancellationToken.None);
        }
        var subject = await repository.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), At(5), CancellationToken.None);
        await repository.SetSubjectDevicesAsync(subject.Id, devices.Select(value => value.Id).ToArray(), At(5), CancellationToken.None);
        return new Fixture(root, repository, router, subject, devices);
    }

    private static ObservedNetworkDevice Observation(NetworkDevice device, bool online) =>
        new(device.MacAddress, device.OriginalName, device.OriginName, device.LastIp, online, null, device.ConnectionType, device.Signal);

    private static string Mac(int index) => $"AA:BB:CC:DD:EE:{index + 1:00}";
    private static DateTimeOffset At(int hour) => new(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(string Root, SqlitePresenceRepository Repository, Router Router, PresenceSubject Subject, IReadOnlyList<NetworkDevice> Devices) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledSource : IXiaomiPresenceSource
    {
        public IReadOnlyList<ObservedNetworkDevice> Observations { get; init; } = [];
        public bool WaitForRelease { get; init; }
        public bool ThrowOnPoll { get; init; }
        public TaskCompletionSource<bool> PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);

        public async Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken)
        {
            PollStarted.TrySetResult(true);
            if (WaitForRelease) await Release.Task.WaitAsync(cancellationToken);
            if (ThrowOnPoll) throw new InvalidOperationException("poll failed");
            return Observations;
        }
    }
}
