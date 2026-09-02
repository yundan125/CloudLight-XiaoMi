using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class QqRecipientBotBindingTests
{
    [Fact]
    public async Task LegacyGlobalRecipientOpenIdUniqueIndexIsRemovedWithoutChangingRecipientId()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var created = await repository.CreateNotificationRecipientAsync(
                new NotificationRecipient(0, "旧联系人", "OLD_RAW_OPENID_SYNTHETIC", NotificationTargetType.Private, now, now),
                CancellationToken.None);

            await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE UNIQUE INDEX UX_NotificationRecipient_LegacyGlobalOpenId ON NotificationRecipient(TargetType,OpenId); PRAGMA user_version=14;";
                await command.ExecuteNonQueryAsync();
            }

            await repository.InitializeAsync(CancellationToken.None);

            var recipient = await repository.GetNotificationRecipientAsync(created.Id, CancellationToken.None);
            Assert.NotNull(recipient);
            Assert.Equal(created.Id, recipient!.Id);
            Assert.Equal("OLD_RAW_OPENID_SYNTHETIC", recipient.LegacyOpenId);
            var unknown = Assert.Single(await repository.GetQqBotProfilesAsync(CancellationToken.None), value => value.IsLegacyUnknown);
            var binding = Assert.Single(await repository.GetNotificationRecipientBotBindingsAsync(created.Id, CancellationToken.None));
            Assert.Equal(unknown.Id, binding.BotProfileId);
            Assert.Equal("OLD_RAW_OPENID_SYNTHETIC", binding.OpenId);

            await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
            await verify.OpenAsync();
            await using var indexes = verify.CreateCommand();
            indexes.CommandText = "SELECT name FROM pragma_index_list('NotificationRecipient') WHERE [unique]=1";
            await using var reader = await indexes.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            foreach (var name in names)
            {
                await using var info = verify.CreateCommand();
                info.CommandText = $"SELECT group_concat(name, ',') FROM pragma_index_info('{name.Replace("'", "''", StringComparison.Ordinal)}')";
                var columns = Convert.ToString(await info.ExecuteScalarAsync());
                Assert.NotEqual("TargetType,OpenId", columns, StringComparer.OrdinalIgnoreCase);
            }
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task LegacyRecipientMigrationCreatesUnknownBindingAndIsIdempotent()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var legacy = "LEGACY_OPENID_SYNTHETIC_001";
            await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO NotificationRecipient(Id,Note,OpenId,TargetType,CreatedAt,UpdatedAt) VALUES(1,'联系人', $openid, 1, $now, $now); PRAGMA user_version=14;";
                command.Parameters.AddWithValue("$openid", legacy);
                command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            await repository.InitializeAsync(CancellationToken.None);
            await repository.InitializeAsync(CancellationToken.None);
            await repository.InitializeAsync(CancellationToken.None);

            var recipient = Assert.Single(await repository.GetNotificationRecipientsAsync(CancellationToken.None));
            Assert.Equal(1, recipient.Id);
            Assert.Equal("联系人", recipient.Note);
            Assert.Equal(legacy, recipient.LegacyOpenId);
            var profile = Assert.Single(await repository.GetQqBotProfilesAsync(CancellationToken.None), value => value.IsLegacyUnknown);
            var binding = Assert.Single(await repository.GetNotificationRecipientBotBindingsAsync(recipient.Id, CancellationToken.None));
            Assert.Equal(profile.Id, binding.BotProfileId);
            Assert.Equal(legacy, binding.OpenId);

            await repository.InitializeAsync(CancellationToken.None);
            Assert.Single(await repository.GetNotificationRecipientBotBindingsAsync(recipient.Id, CancellationToken.None));
            Assert.Single(await repository.GetQqBotProfilesAsync(CancellationToken.None), value => value.IsLegacyUnknown);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task SameRecipientResolvesDifferentRawOpenIdsPerBotAndNeverUsesAnotherScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var recipient = await fixture.Repository.CreateNotificationRecipientAsync(
            new NotificationRecipient(0, "联系人", NotificationTargetType.Private, fixture.Start, fixture.Start), CancellationToken.None);
        var botA = await fixture.Repository.EnsureQqBotProfileAsync("bot-a", "Bot A", fixture.Start, CancellationToken.None);
        var botB = await fixture.Repository.EnsureQqBotProfileAsync("bot-b", "Bot B", fixture.Start, CancellationToken.None);
        var botC = await fixture.Repository.EnsureQqBotProfileAsync("bot-c", "Bot C", fixture.Start, CancellationToken.None);
        await fixture.Repository.UpsertNotificationRecipientBotBindingAsync(
            new NotificationRecipientBotBinding(0, recipient.Id, botA.Id, NotificationTargetType.Private, "OPENID_FOR_BOT_A", fixture.Start, fixture.Start), CancellationToken.None);
        await fixture.Repository.UpsertNotificationRecipientBotBindingAsync(
            new NotificationRecipientBotBinding(0, recipient.Id, botB.Id, NotificationTargetType.Private, "OPENID_FOR_BOT_B", fixture.Start, fixture.Start), CancellationToken.None);

        var rule = await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60, NotificationChannelType.QQ,
            NotificationTargetType.Private, string.Empty, "online", fixture.Start, fixture.Start)
        { RecipientIds = [recipient.Id] }, CancellationToken.None);
        await fixture.ApplyAsync(fixture.Start.AddMinutes(1), true);

        var activeAppId = "bot-a";
        var service = new NotificationRuleService(fixture.Repository, fixture.Presence, currentBotAppIdProvider: () => activeAppId);
        var requestA = Assert.Single(await service.EvaluateAsync(fixture.Start.AddMinutes(2), CancellationToken.None));
        Assert.Equal("OPENID_FOR_BOT_A", requestA.TargetId);

        // A new rule is used for each scope so the business Episode+Recipient
        // de-duplication remains unchanged while resolution is tested directly.
        var secondRule = await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60, NotificationChannelType.QQ,
            NotificationTargetType.Private, string.Empty, "online", fixture.Start, fixture.Start)
        { RecipientIds = [recipient.Id] }, CancellationToken.None);
        activeAppId = "bot-b";
        var requestB = Assert.Single(await service.EvaluateAsync(fixture.Start.AddMinutes(2), CancellationToken.None), value => value.RuleId == secondRule.Id);
        Assert.Equal("OPENID_FOR_BOT_B", requestB.TargetId);

        var thirdRule = await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60, NotificationChannelType.QQ,
            NotificationTargetType.Private, string.Empty, "online", fixture.Start, fixture.Start)
        { RecipientIds = [recipient.Id] }, CancellationToken.None);
        activeAppId = "bot-c";
        var noBinding = await service.EvaluateAsync(fixture.Start.AddMinutes(2), CancellationToken.None);
        Assert.DoesNotContain(noBinding, value => value.RuleId == thirdRule.Id);
        var missing = Assert.Single(await fixture.Repository.GetNotificationDeliveriesForRuleAsync(thirdRule.Id, CancellationToken.None));
        Assert.Equal(NotificationDeliveryStatus.BindingRequired, missing.Status);
        Assert.Equal(botC.Id, missing.BotProfileId);
        Assert.Equal(string.Empty, missing.TargetId);
        var diagnostic = await service.EvaluateDiagnosticAsync(thirdRule.Id, fixture.Start.AddMinutes(2), CancellationToken.None);
        Assert.Equal(RuleEvaluationDiagnosticStatus.RecipientBindingMissing, diagnostic.Status);
        Assert.Contains("尚未绑定当前 QQ Bot", diagnostic.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENID_FOR_BOT_A", diagnostic.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENID_FOR_BOT_B", diagnostic.Explanation, StringComparison.Ordinal);
        var pending = await fixture.Repository.GetPendingNotificationDeliveriesAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        Assert.DoesNotContain(pending, value => value.RecipientId == recipient.Id && value.RuleId == thirdRule.Id);

        activeAppId = "bot-a";
        var restoredRule = await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60, NotificationChannelType.QQ,
            NotificationTargetType.Private, string.Empty, "online", fixture.Start, fixture.Start)
        { RecipientIds = [recipient.Id] }, CancellationToken.None);
        Assert.Equal("OPENID_FOR_BOT_A", Assert.Single(await service.EvaluateAsync(fixture.Start.AddMinutes(2), CancellationToken.None), value => value.RuleId == restoredRule.Id).TargetId);
    }

    [Fact]
    public async Task MissingBindingDoesNotPreventAnotherRecipientAndDoesNotRetryOldEpisode()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Repository.CreateNotificationRecipientAsync(
            new NotificationRecipient(0, "已绑定", NotificationTargetType.Private, fixture.Start, fixture.Start), CancellationToken.None);
        var second = await fixture.Repository.CreateNotificationRecipientAsync(
            new NotificationRecipient(0, "待绑定", NotificationTargetType.Private, fixture.Start, fixture.Start), CancellationToken.None);
        var bot = await fixture.Repository.EnsureQqBotProfileAsync("bot-main", "当前 Bot", fixture.Start, CancellationToken.None);
        await fixture.Repository.UpsertNotificationRecipientBotBindingAsync(
            new NotificationRecipientBotBinding(0, first.Id, bot.Id, NotificationTargetType.Private, "BOUND_OPENID", fixture.Start, fixture.Start), CancellationToken.None);
        var rule = await fixture.Repository.CreateNotificationRuleAsync(new NotificationRule(
            0, fixture.Subject.Id, true, NotificationCondition.OnlineFor, 60, NotificationChannelType.QQ,
            NotificationTargetType.Private, string.Empty, "online", fixture.Start, fixture.Start)
        { RecipientIds = [first.Id, second.Id] }, CancellationToken.None);
        await fixture.ApplyAsync(fixture.Start.AddMinutes(1), true);

        var service = new NotificationRuleService(fixture.Repository, fixture.Presence, currentBotAppIdProvider: () => "bot-main");
        var requests = await service.EvaluateAsync(fixture.Start.AddMinutes(2), CancellationToken.None);
        var firstRequest = Assert.Single(requests);
        Assert.Equal("BOUND_OPENID", firstRequest.TargetId);
        var channel = new CountingChannel();
        using var dispatcher = new NotificationDispatcher(fixture.Repository, [channel]);
        await dispatcher.DispatchAsync(firstRequest, CancellationToken.None);
        Assert.Equal(1, channel.Count);

        var deliveries = await fixture.Repository.GetNotificationDeliveriesForEpisodeAsync(rule.Id, firstRequest.EpisodeId, CancellationToken.None);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(NotificationDeliveryStatus.Delivered, Assert.Single(deliveries, value => value.RecipientId == first.Id).Status);
        var missing = Assert.Single(deliveries, value => value.RecipientId == second.Id);
        Assert.Equal(NotificationDeliveryStatus.BindingRequired, missing.Status);
        Assert.Null(missing.NextAttemptAt);

        await fixture.Repository.UpsertNotificationRecipientBotBindingAsync(
            new NotificationRecipientBotBinding(0, second.Id, bot.Id, NotificationTargetType.Private, "NOW_BOUND_OPENID", fixture.Start, fixture.Start), CancellationToken.None);
        Assert.Empty(await service.EvaluateAsync(fixture.Start.AddMinutes(3), CancellationToken.None));
        Assert.Equal(1, channel.Count);
    }

    [Fact]
    public async Task BindingConstraintsRejectMaskedValuesAndPreserveBindingsWhenEditingNote()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.CreateNotificationRecipientAsync(
            new NotificationRecipient(0, "bad", "MASKED****ID", NotificationTargetType.Private, fixture.Start, fixture.Start), CancellationToken.None));
        var recipient = await fixture.Repository.CreateNotificationRecipientAsync(
            new NotificationRecipient(0, "before", NotificationTargetType.Private, fixture.Start, fixture.Start), CancellationToken.None);
        var bot = await fixture.Repository.EnsureQqBotProfileAsync("bot-edit", "Bot Edit", fixture.Start, CancellationToken.None);
        var binding = await fixture.Repository.UpsertNotificationRecipientBotBindingAsync(
            new NotificationRecipientBotBinding(0, recipient.Id, bot.Id, NotificationTargetType.Private, "RAW_BINDING_VALUE", fixture.Start, fixture.Start), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.UpsertNotificationRecipientBotBindingAsync(
            binding with { OpenId = "MASKED****VALUE" }, CancellationToken.None));
        await fixture.Repository.UpdateNotificationRecipientAsync(
            recipient with { Note = "after", UpdatedAt = fixture.Start.AddMinutes(1) }, CancellationToken.None);
        var updated = Assert.Single(await fixture.Repository.GetNotificationRecipientBotBindingsAsync(recipient.Id, CancellationToken.None));
        Assert.Equal("RAW_BINDING_VALUE", updated.OpenId);
        Assert.Equal("after", (await fixture.Repository.GetNotificationRecipientAsync(recipient.Id, CancellationToken.None))!.Note);
    }

    [Fact]
    public async Task DataTransferRoundTripsLogicalRecipientAndAllBotBindingsIdempotently()
    {
        var sourceRoot = TemporaryRoot();
        var targetRoot = TemporaryRoot();
        var exportPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.clpresence");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var sourcePaths = new AppPaths(sourceRoot);
            var source = new SqlitePresenceRepository(sourcePaths);
            await source.InitializeAsync(CancellationToken.None);
            var recipient = await source.CreateNotificationRecipientAsync(
                new NotificationRecipient(0, "多 Bot 联系人", NotificationTargetType.Private, now, now), CancellationToken.None);
            var botA = await source.EnsureQqBotProfileAsync("transfer-bot-a", "Transfer A", now, CancellationToken.None);
            var botB = await source.EnsureQqBotProfileAsync("transfer-bot-b", "Transfer B", now, CancellationToken.None);
            await source.UpsertNotificationRecipientBotBindingAsync(
                new NotificationRecipientBotBinding(0, recipient.Id, botA.Id, NotificationTargetType.Private, "TRANSFER_OPENID_A", now, now), CancellationToken.None);
            await source.UpsertNotificationRecipientBotBindingAsync(
                new NotificationRecipientBotBinding(0, recipient.Id, botB.Id, NotificationTargetType.Private, "TRANSFER_OPENID_B", now, now), CancellationToken.None);

            await new PresenceDataTransferService(sourcePaths).ExportAsync(exportPath, CancellationToken.None);

            var targetPaths = new AppPaths(targetRoot);
            var target = new SqlitePresenceRepository(targetPaths);
            await target.InitializeAsync(CancellationToken.None);
            await new PresenceDataTransferService(targetPaths).ImportAsync(exportPath, CancellationToken.None);
            await new PresenceDataTransferService(targetPaths).ImportAsync(exportPath, CancellationToken.None);

            var importedRecipient = Assert.Single(await target.GetNotificationRecipientsAsync(CancellationToken.None));
            var importedBindings = await target.GetNotificationRecipientBotBindingsAsync(importedRecipient.Id, CancellationToken.None);
            Assert.Equal(2, importedBindings.Count);
            Assert.Contains(importedBindings, value => value.OpenId == "TRANSFER_OPENID_A");
            Assert.Contains(importedBindings, value => value.OpenId == "TRANSFER_OPENID_B");
            Assert.Equal(2, (await target.GetQqBotProfilesAsync(CancellationToken.None)).Count);
        }
        finally
        {
            Delete(sourceRoot);
            Delete(targetRoot);
            if (File.Exists(exportPath)) File.Delete(exportPath);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Qq-Binding-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class CountingChannel : INotificationChannel
    {
        public int Count { get; private set; }
        public NotificationChannelType ChannelType => NotificationChannelType.QQ;
        public NotificationChannelStatus Status => new(NotificationChannelType.QQ, true, true, true, NotificationConnectionState.Connected);
        public event EventHandler<NotificationChannelStatus>? StatusChanged;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken) => Task.FromResult(new NotificationSendResult(true, 1, 1));
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new NotificationSendResult(true, 1, 1));
        }
        public void RaiseStatus(NotificationChannelStatus status) => StatusChanged?.Invoke(this, status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly PresenceStateMachine _machine;
        private readonly NetworkDevice _device;
        private Fixture(string root, SqlitePresenceRepository repository, PresenceStateMachine machine, SubjectPresenceService presence, Router router, PresenceSubject subject, NetworkDevice device, DateTimeOffset start)
        {
            _root = root; Repository = repository; _machine = machine; Presence = presence; Router = router; Subject = subject; _device = device; Start = start;
        }
        public SqlitePresenceRepository Repository { get; }
        public SubjectPresenceService Presence { get; }
        public Router Router { get; }
        public PresenceSubject Subject { get; }
        public DateTimeOffset Start { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = TemporaryRoot();
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var router = await repository.UpsertRouterAsync(new Router(0, "binding-did", "router", "binding-partner", "测试路由器", null, null, start, start), CancellationToken.None);
            var device = await repository.InsertDeviceAsync(new NetworkDevice(0, router.Id, "AA:BB:CC:DD:EE:19", "测试设备", "测试设备", null, null, "192.168.1.2", "5G", -45, PresenceState.Offline, start, start, start), CancellationToken.None);
            var subjectId = (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id];
            var subject = (await repository.GetSubjectAsync(subjectId, CancellationToken.None))!;
            return new Fixture(root, repository, new PresenceStateMachine(repository), new SubjectPresenceService(repository, new PresenceStatisticsService(repository)), router, subject, device, start);
        }

        public Task ApplyAsync(DateTimeOffset at, bool online) =>
            _machine.ApplySnapshotAsync(Router.Id, [new ObservedNetworkDevice(_device.MacAddress, _device.OriginalName, _device.OriginName, _device.LastIp, online, null, _device.ConnectionType, _device.Signal)], at, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            Delete(_root);
            return ValueTask.CompletedTask;
        }
    }
}
