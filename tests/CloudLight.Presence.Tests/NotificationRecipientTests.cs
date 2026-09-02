using CloudLight.Presence.App.ViewModels;
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

public sealed class NotificationRecipientTests
{
    [Fact]
    public async Task RuleCreatesOneDeliveryPerRecipientAndRepeatedEvaluationDoesNotDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        var offlineObservedAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(onlineAt, true);
        await fixture.ApplyAsync(offlineObservedAt, false);
        await fixture.ApplyAsync(offlineObservedAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);

        var first = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("我的 QQ", "openid-a", NotificationTargetType.Private), CancellationToken.None);
        var second = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("家庭群", "openid-b", NotificationTargetType.Group), CancellationToken.None);
        var rule = await fixture.Repository.CreateNotificationRuleAsync(
            new NotificationRule(0, fixture.Subject.Id, true, NotificationCondition.OfflineFor, 60, NotificationChannelType.QQ,
                first.TargetType, first.OpenId, "{name} {duration}", fixture.Start, fixture.Start)
            {
                RecipientIds = [first.Id, second.Id]
            },
            CancellationToken.None);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var evaluated = await service.EvaluateAsync(offlineObservedAt.AddMinutes(2), CancellationToken.None);
        Assert.Equal(2, evaluated.Count);
        Assert.Equal([first.Id, second.Id], evaluated.Select(value => fixture.Repository.GetNotificationDeliveryAsync(value.DeliveryId, CancellationToken.None).GetAwaiter().GetResult()!.RecipientId).OrderBy(value => value));

        var again = await service.EvaluateAsync(offlineObservedAt.AddMinutes(3), CancellationToken.None);
        Assert.Equal(2, (await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None)).Count);
        Assert.Equal(evaluated.Select(value => value.DeliveryId).OrderBy(value => value), again.Select(value => value.DeliveryId).OrderBy(value => value));
        Assert.Equal([first, second], await fixture.Repository.GetNotificationRuleRecipientsAsync(rule.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FailedRecipientRetriesIndependentlyAfterAnotherRecipientSucceeds()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        var offlineObservedAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(onlineAt, true);
        await fixture.ApplyAsync(offlineObservedAt, false);
        await fixture.ApplyAsync(offlineObservedAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);

        var first = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("我的 QQ", "openid-a", NotificationTargetType.Private), CancellationToken.None);
        var second = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("家庭群", "openid-b", NotificationTargetType.Group), CancellationToken.None);
        await fixture.Repository.CreateNotificationRuleAsync(
            new NotificationRule(0, fixture.Subject.Id, true, NotificationCondition.OfflineFor, 60, NotificationChannelType.QQ,
                first.TargetType, first.OpenId, "{name}", fixture.Start, fixture.Start)
            {
                RecipientIds = [first.Id, second.Id]
            },
            CancellationToken.None);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var requests = await service.EvaluateAsync(offlineObservedAt.AddMinutes(2), CancellationToken.None);
        var channel = new SelectiveChannel("openid-b");
        using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
        foreach (var request in requests) await dispatcher.DispatchAsync(request, CancellationToken.None);

        var afterFirstSend = await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None);
        Assert.Equal(NotificationDeliveryStatus.Delivered, Assert.Single(afterFirstSend, value => value.TargetId == "openid-a").Status);
        var failed = Assert.Single(afterFirstSend, value => value.TargetId == "openid-b");
        Assert.Equal(NotificationDeliveryStatus.Failed, failed.Status);

        channel.FailTarget = null;
        await dispatcher.RetryPendingAsync(DateTimeOffset.UtcNow.AddMinutes(2), CancellationToken.None);
        var final = await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None);
        Assert.All(final, value => Assert.Equal(NotificationDeliveryStatus.Delivered, value.Status));
        Assert.Equal(1, channel.SendCount("openid-a"));
        Assert.Equal(2, channel.SendCount("openid-b"));
    }

    [Fact]
    public async Task TerminalRecipientFailureIsNotRetriedAndDoesNotBlockAnotherRecipient()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onlineAt = fixture.Start.AddMinutes(1);
        var offlineObservedAt = onlineAt.AddMinutes(2);
        await fixture.ApplyAsync(onlineAt, true);
        await fixture.ApplyAsync(offlineObservedAt, false);
        await fixture.ApplyAsync(offlineObservedAt + SubjectPresenceService.DefaultOfflineGracePeriod, false);

        var first = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("我的 QQ", "openid-a", NotificationTargetType.Private), CancellationToken.None);
        var second = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("家庭群", "openid-b", NotificationTargetType.Group), CancellationToken.None);
        await fixture.Repository.CreateNotificationRuleAsync(
            new NotificationRule(0, fixture.Subject.Id, true, NotificationCondition.OfflineFor, 60, NotificationChannelType.QQ,
                first.TargetType, first.OpenId, "{name}", fixture.Start, fixture.Start)
            {
                RecipientIds = [first.Id, second.Id]
            },
            CancellationToken.None);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var requests = await service.EvaluateAsync(offlineObservedAt.AddMinutes(2), CancellationToken.None);
        var channel = new SelectiveChannel("openid-b") { TerminalFailure = true };
        using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
        foreach (var request in requests) await dispatcher.DispatchAsync(request, CancellationToken.None);

        var deliveries = await fixture.Repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None);
        Assert.Equal(NotificationDeliveryStatus.Delivered, Assert.Single(deliveries, value => value.TargetId == "openid-a").Status);
        var failed = Assert.Single(deliveries, value => value.TargetId == "openid-b");
        Assert.Equal(NotificationDeliveryStatus.PermanentFailed, failed.Status);
        Assert.Null(failed.NextAttemptAt);
        Assert.Empty(await fixture.Repository.GetPendingNotificationDeliveriesAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None));
        var state = await fixture.Repository.GetNotificationRuleStateAsync(requests[0].RuleId, CancellationToken.None);
        Assert.NotNull(state);
        Assert.False(state!.PendingDelivery);
        Assert.Null(state.PendingDeliveryId);
        Assert.Contains("模拟接收人失败", state.LastDeliveryError, StringComparison.Ordinal);

        await dispatcher.RetryPendingAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.Equal(1, channel.SendCount("openid-b"));
    }

    [Fact]
    public async Task EditingNoteWithMaskedOpenIdKeepsTheStoredRawOpenId()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Recipient-Edit-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var rawOpenId = "C47xxxxxxxxxxxxxxxxxxxxxxxxFD8";
            var created = await repository.CreateNotificationRecipientAsync(Recipient("旧备注", rawOpenId, NotificationTargetType.Private), CancellationToken.None);
            await using var channel = new QQNotificationChannel(paths.LogsDirectory);
            using var viewModel = new NotificationSettingsViewModel(repository, new JsonSettingsStore(paths), new DpapiQqSecretStore(paths), channel);
            await viewModel.LoadAsync(CancellationToken.None);

            await viewModel.SaveRecipientAsync(
                new NotificationRecipientDraft("新备注", "C47****FD8", NotificationTargetType.Private, OpenIdEdited: false),
                created.Id,
                CancellationToken.None);

            var updated = await repository.GetNotificationRecipientAsync(created.Id, CancellationToken.None);
            Assert.NotNull(updated);
            Assert.Equal("新备注", updated!.Note);
            Assert.Equal(rawOpenId, updated.OpenId);
            Assert.DoesNotContain('*', updated.OpenId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MaskedOpenIdCannotBeSavedAsANewRecipient()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.CreateNotificationRecipientAsync(
            Recipient("错误脱敏值", "C47****FD8", NotificationTargetType.Private), CancellationToken.None));
    }

    [Fact]
    public async Task RecipientUniquenessDeleteProtectionAndLegacyMigrationAreIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("旧备注", "same-openid", NotificationTargetType.Private), CancellationToken.None);
        var same = await fixture.Repository.CreateNotificationRecipientAsync(Recipient("另一个备注", "same-openid", NotificationTargetType.Private), CancellationToken.None);
        Assert.Equal(created.Id, same.Id);

        var legacy = await fixture.Repository.CreateNotificationRuleAsync(
            new NotificationRule(0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60, NotificationChannelType.QQ,
                NotificationTargetType.Private, "legacy-openid", "{name}", fixture.Start, fixture.Start), CancellationToken.None);
        await fixture.Repository.InitializeAsync(CancellationToken.None);
        await fixture.Repository.InitializeAsync(CancellationToken.None);
        var migrated = await fixture.Repository.GetNotificationRecipientsAsync(CancellationToken.None);
        var legacyRecipient = Assert.Single(migrated, value => value.OpenId == "legacy-openid");
        Assert.Single(await fixture.Repository.GetNotificationRuleRecipientsAsync(legacy.Id, CancellationToken.None));
        Assert.Equal(legacyRecipient.Id, (await fixture.Repository.GetNotificationRuleRecipientsAsync(legacy.Id, CancellationToken.None))[0].Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.DeleteNotificationRecipientAsync(legacyRecipient.Id, CancellationToken.None));
        await fixture.Repository.DeleteNotificationRuleAsync(legacy.Id, CancellationToken.None);
        await fixture.Repository.DeleteNotificationRecipientAsync(legacyRecipient.Id, CancellationToken.None);
        Assert.DoesNotContain(await fixture.Repository.GetNotificationRecipientsAsync(CancellationToken.None), value => value.Id == legacyRecipient.Id);
    }

    private static NotificationRecipient Recipient(string note, string openId, NotificationTargetType type) =>
        new(0, note, openId, type, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly PresenceStateMachine _machine;
        private readonly NetworkDevice _device;

        private Fixture(string root, SqlitePresenceRepository repository, PresenceStateMachine machine, SubjectPresenceService presence, Router router, PresenceSubject subject, NetworkDevice device, DateTimeOffset start)
        {
            _root = root;
            Repository = repository;
            _machine = machine;
            Presence = presence;
            Router = router;
            Subject = subject;
            _device = device;
            Start = start;
        }

        public SqlitePresenceRepository Repository { get; }
        public SubjectPresenceService Presence { get; }
        public Router Router { get; }
        public PresenceSubject Subject { get; }
        public DateTimeOffset Start { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CloudLight-Recipient-Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var start = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
            var router = await repository.UpsertRouterAsync(new Router(0, "recipient-did", "router", "recipient-partner", "测试路由器", null, null, start, start), CancellationToken.None);
            var device = await repository.InsertDeviceAsync(new NetworkDevice(0, router.Id, "AA:BB:CC:DD:EE:01", "测试设备", "测试设备", null, null, "192.168.1.2", "5G", -45, PresenceState.Offline, start.AddHours(-1), start, start), CancellationToken.None);
            var subject = await repository.CreateSubjectAsync("测试主体", null, Guid.NewGuid(), start.AddHours(-1), CancellationToken.None);
            await repository.SetSubjectDevicesAsync(subject.Id, [device.Id], start.AddHours(-1), CancellationToken.None);
            return new Fixture(root, repository, new PresenceStateMachine(repository), new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), router, subject, device, start);
        }

        public async Task ApplyAsync(DateTimeOffset observedAt, bool online)
        {
            await _machine.ApplySnapshotAsync(Router.Id, [new ObservedNetworkDevice(_device.MacAddress, _device.OriginalName, _device.OriginName, _device.LastIp, online, null, _device.ConnectionType, _device.Signal)], observedAt, CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SelectiveChannel(string? failTarget) : INotificationChannel
    {
        private readonly List<string> _sentTargets = [];
        public string? FailTarget { get; set; } = failTarget;
        public bool TerminalFailure { get; set; }
        public NotificationChannelType ChannelType => NotificationChannelType.QQ;
        public NotificationChannelStatus Status => new(NotificationChannelType.QQ, true, true, true, NotificationConnectionState.Connected);
        public event EventHandler<NotificationChannelStatus>? StatusChanged = delegate { };
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) => Task.FromResult(new NotificationSendResult(true, 1, 1));
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
        {
            _sentTargets.Add(request.TargetId);
            return Task.FromResult(request.TargetId == FailTarget
                ? new NotificationSendResult(false, 0, 0, "模拟接收人失败", FailureKind: TerminalFailure ? NotificationFailureKind.PermanentTarget : NotificationFailureKind.Unknown)
                : new NotificationSendResult(true, 1, 1));
        }
        public int SendCount(string target) => _sentTargets.Count(value => value == target);
    }
}
