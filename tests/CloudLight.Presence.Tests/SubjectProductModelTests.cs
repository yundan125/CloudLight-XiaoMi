using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class SubjectProductModelTests
{
    [Fact]
    public async Task NewDeviceImmediatelyGetsStandaloneSubject()
    {
        await WithRepository(async (repository, paths, router) =>
        {
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:01", "Phone"), CancellationToken.None);
            var subject = Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None));
            Assert.Equal("Phone", subject.DisplayName);
            Assert.Equal(subject.Id, (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id]);
        });
    }

    [Fact]
    public async Task LegacyOrphanMigrationCreatesStandaloneSubject()
    {
        await WithRepository(async (repository, paths, router) =>
        {
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:02", "Legacy Phone"), CancellationToken.None);
            await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM PresenceSubject"; await command.ExecuteNonQueryAsync();
            }
            Assert.Empty(await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None));
            await repository.EnsureEveryDeviceHasSubjectAsync(CancellationToken.None);
            var subject = Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None));
            Assert.Equal(device.Id, Assert.Single(await repository.GetSubjectDevicesAsync(subject.Id, CancellationToken.None)).Id);
        });
    }

    [Fact]
    public async Task DetachingDeviceCreatesNewStandaloneSubjectAndDeletingGroupSplitsAllMembers()
    {
        await WithRepository(async (repository, _, router) =>
        {
            var now = DateTimeOffset.UtcNow;
            var a = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:03", "爸爸 5G"), CancellationToken.None);
            var b = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:04", "爸爸 2.4G"), CancellationToken.None);
            var target = await repository.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), now, CancellationToken.None);
            await repository.SetSubjectDevicesAsync(target.Id, [a.Id, b.Id], now, CancellationToken.None);
            Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None));

            await repository.SetSubjectDevicesAsync(target.Id, [a.Id], now, CancellationToken.None);
            var afterDetach = await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None);
            Assert.NotEqual(afterDetach[a.Id], afterDetach[b.Id]); Assert.Equal(2, (await repository.GetSubjectsAsync(CancellationToken.None)).Count);

            await repository.SetSubjectDevicesAsync(target.Id, [a.Id, b.Id], now, CancellationToken.None);
            await repository.DeleteSubjectAsync(target.Id, CancellationToken.None);
            var afterDelete = await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None);
            Assert.NotEqual(afterDelete[a.Id], afterDelete[b.Id]); Assert.Equal(2, (await repository.GetSubjectsAsync(CancellationToken.None)).Count);
        });
    }

    [Fact]
    public async Task InlineNameAndNoteEditSupportSaveAndCancelIndependently()
    {
        await WithRepository(async (repository, _, router) =>
        {
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:05", "Phone"), CancellationToken.None);
            var subject = Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None));
            var monitor = new PresenceMonitor(new EmptySource(), repository, new PresenceStateMachine(repository));
            using var viewModel = new SubjectDetailViewModel(repository, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), monitor, subject);

            viewModel.BeginNameEdit(); viewModel.NameDraft = "临时名称"; viewModel.CancelNameEdit();
            Assert.False(viewModel.IsNameEditing); Assert.Equal("Phone", viewModel.NameDraft);
            viewModel.BeginNameEdit(); viewModel.NameDraft = "我的手机"; await viewModel.SaveNameAsync();
            Assert.Equal("我的手机", (await repository.GetSubjectAsync(subject.Id, CancellationToken.None))!.DisplayName);

            viewModel.BeginNoteEdit(); viewModel.NoteDraft = "取消备注"; viewModel.CancelNoteEdit(); Assert.Null(viewModel.NoteDraft);
            viewModel.BeginNoteEdit(); viewModel.NoteDraft = "主力设备"; await viewModel.SaveNoteAsync();
            Assert.Equal("主力设备", (await repository.GetSubjectAsync(subject.Id, CancellationToken.None))!.Note);
            Assert.Equal(device.Id, Assert.Single(await repository.GetSubjectDevicesAsync(subject.Id, CancellationToken.None)).Id);
        });
    }

    [Fact]
    public async Task ActivityHidesUnknownByDefaultAndRestoresItWithSwitch()
    {
        await WithRepository(async (repository, _, router) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:06", "Phone") with { CurrentState = PresenceState.Online, FirstSeenAt = now.AddHours(-3), LastSeenAt = now }, CancellationToken.None);
            await repository.AddSessionAsync(new(0, device.Id, now.AddHours(-3), null, true, false), CancellationToken.None);
            var gap = await repository.StartMonitoringGapAsync(now.AddHours(-1), "restart", CancellationToken.None); await repository.EndMonitoringGapAsync(gap, now.AddMinutes(-50), CancellationToken.None);
            var subject = Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None));
            var monitor = new PresenceMonitor(new EmptySource(), repository, new PresenceStateMachine(repository));
            using var viewModel = new SubjectDetailViewModel(repository, new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), monitor, subject);
            await viewModel.LoadAsync();

            Assert.False(viewModel.ShowUnrecordedPeriods); Assert.DoesNotContain(viewModel.History, value => value.Event == "暂无监控数据"); Assert.Single(viewModel.History); Assert.Equal("已上线", viewModel.History[0].Event);
            viewModel.ShowUnrecordedPeriods = true; Assert.Contains(viewModel.History, value => value.Event == "暂无监控数据");
            viewModel.ShowUnrecordedPeriods = false; Assert.DoesNotContain(viewModel.History, value => value.Event == "暂无监控数据");
        });
    }

    [Fact]
    public async Task StartupRebindsAnUnambiguousOrphanRuleToItsRenamedDeviceSubject()
    {
        await WithRepository(async (repository, paths, router) =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:07", "DESKTOP-BOLQ07G"), CancellationToken.None);
            var target = await repository.CreateSubjectAsync("我的电脑", null, Guid.NewGuid(), now, CancellationToken.None);
            await repository.SetSubjectDevicesAsync(target.Id, [device.Id], now, CancellationToken.None);
            var orphan = await repository.CreateSubjectAsync("DESKTOP-BOLQ07G", null, Guid.NewGuid(), now, CancellationToken.None);
            var rule = await repository.CreateNotificationRuleAsync(new NotificationRule(
                0, orphan.Id, true, NotificationCondition.OnlineFor, 60,
                NotificationChannelType.QQ, NotificationTargetType.Private, "test-openid", "{name}", now, now), CancellationToken.None);
            await repository.UpsertNotificationRuleStateAsync(new NotificationRuleState(rule.Id, "old", now, true, now, false, null, null, now), CancellationToken.None);

            var reopened = new SqlitePresenceRepository(paths);
            await reopened.InitializeAsync(CancellationToken.None);

            var repaired = await reopened.GetNotificationRuleAsync(rule.Id, CancellationToken.None);
            Assert.NotNull(repaired);
            Assert.Equal(target.Id, repaired!.SubjectId);
            var state = await reopened.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);
            Assert.NotNull(state);
            Assert.Equal(0, state!.LastProcessedSubjectEventId);
            Assert.Null(await reopened.GetSubjectAsync(orphan.Id, CancellationToken.None));
        });
    }

    private static async Task WithRepository(Func<SqlitePresenceRepository, AppPaths, Router, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Subject-Product-Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root); var repository = new SqlitePresenceRepository(paths); await repository.InitializeAsync(CancellationToken.None); var now = DateTimeOffset.UtcNow;
            var router = await repository.UpsertRouterAsync(new(0, "did-subject-product", "router", "partner", "Router", null, null, now, now), CancellationToken.None);
            await test(repository, paths, router);
        }
        finally { Directory.Delete(root, true); }
    }

    private static NetworkDevice Device(long routerId, string mac, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new(0, routerId, mac, name, name, null, null, "192.168.1.2", "5G", -50, PresenceState.Offline, now, now, null);
    }

    private sealed class EmptySource : IXiaomiPresenceSource
    {
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
    }
}
