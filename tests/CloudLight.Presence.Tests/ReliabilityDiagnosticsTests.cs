using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Diagnostics;
using CloudLight.Presence.Infrastructure.Settings;
using CloudLight.Presence.Infrastructure.Updates;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class ReliabilityDiagnosticsTests
{
    [Fact]
    public async Task FreshDatabaseDoesNotCreateMigrationBackup()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            await new SqlitePresenceRepository(paths).InitializeAsync(CancellationToken.None);

            var files = Directory.Exists(paths.BackupsDirectory)
                ? Directory.EnumerateFiles(paths.BackupsDirectory, "presence-before-migration-*.db").ToArray()
                : [];
            Assert.Empty(files);
            Assert.Null((await new SqliteDatabaseBackupService(paths).GetStatusAsync(CancellationToken.None)).LastMigrationBackupPath);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task MigrationBackupUsesWalSafeSnapshotAndManualBackupIsIndependent()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            await new SqlitePresenceRepository(paths).InitializeAsync(CancellationToken.None);
            await using (var connection = await OpenDatabaseAsync(paths.DatabasePath))
            {
                await ExecuteAsync(connection, "PRAGMA user_version=12;");
            }

            await new SqlitePresenceRepository(paths).InitializeAsync(CancellationToken.None);
            var service = new SqliteDatabaseBackupService(paths);
            var migrationPath = Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "presence-before-migration-*.db"));
            Assert.Equal(migrationPath, (await service.GetStatusAsync(CancellationToken.None)).LastMigrationBackupPath);
            await AssertIndependentDatabaseAsync(migrationPath, expectedVersion: 12);
            Assert.False(File.Exists(migrationPath + "-wal"));
            Assert.False(File.Exists(migrationPath + "-shm"));

            // A second initialization is already at the current schema and
            // must not add another migration snapshot.
            await new SqlitePresenceRepository(paths).InitializeAsync(CancellationToken.None);
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "presence-before-migration-*.db"));

            var manualPath = await service.CreateManualBackupAsync(CancellationToken.None);
            await AssertIndependentDatabaseAsync(manualPath, expectedVersion: SqliteDatabaseBackupService.CurrentSchemaVersion);
            Assert.False(File.Exists(manualPath + "-wal"));
            Assert.False(File.Exists(manualPath + "-shm"));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task MigrationBackupsAreRotatedToTenAndFailuresAreRecorded()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            await new SqlitePresenceRepository(paths).InitializeAsync(CancellationToken.None);
            var service = new SqliteDatabaseBackupService(paths);
            await using (var source = await OpenDatabaseAsync(paths.DatabasePath, SqliteOpenMode.ReadOnly))
            {
                for (var index = 0; index < 11; index++)
                    await service.CreateMigrationBackupAsync(source, 12, CancellationToken.None);
            }

            var files = Directory.EnumerateFiles(paths.BackupsDirectory, "presence-before-migration-*.db").ToArray();
            Assert.Equal(10, files.Length);
            Assert.Contains(files, value => string.Equals(value, (service.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult()).LastMigrationBackupPath, StringComparison.OrdinalIgnoreCase));

            var missingRoot = TemporaryRoot();
            try
            {
                var missingPaths = new AppPaths(missingRoot);
                await Assert.ThrowsAsync<FileNotFoundException>(() => new SqliteDatabaseBackupService(missingPaths).CreateManualBackupAsync(CancellationToken.None));
                var status = await new SqliteDatabaseBackupService(missingPaths).GetStatusAsync(CancellationToken.None);
                Assert.Contains("数据库不存在", status.LastFailure);
            }
            finally { DeleteDirectory(missingRoot); }
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task MigrationFailureRollsBackStructuralChangesAndKeepsUsableOriginalDatabase()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            await using (var connection = await OpenDatabaseAsync(paths.DatabasePath, SqliteOpenMode.ReadWriteCreate))
            {
                await ExecuteAsync(connection, "CREATE TABLE NetworkDevice(Id INTEGER PRIMARY KEY); PRAGMA user_version=12;");
            }

            await Assert.ThrowsAnyAsync<Exception>(() => new SqlitePresenceRepository(paths).InitializeAsync(CancellationToken.None));
            var backup = Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "presence-before-migration-*.db"));
            await AssertIndependentDatabaseAsync(backup, expectedVersion: 12);
            var status = await new SqliteDatabaseBackupService(paths).GetStatusAsync(CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(status.LastFailure));
            Assert.NotNull(status.LastFailureAt);

            await using var verify = await OpenDatabaseAsync(paths.DatabasePath);
            Assert.Equal(12, await SqliteDatabaseBackupService.ReadUserVersionAsync(verify, CancellationToken.None));
            await using var command = verify.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Router';";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None)));
            await using var network = verify.CreateCommand();
            network.CommandText = "SELECT COUNT(*) FROM pragma_table_info('NetworkDevice') WHERE name='LastKnownHistoricalState';";
            Assert.Equal(0L, Convert.ToInt64(await network.ExecuteScalarAsync(CancellationToken.None)));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task RuleDiagnosticReportsProgressAndDryRunHasNoSideEffects()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var rule = await fixture.Repository.CreateNotificationRuleAsync(
            Rule(fixture.Subject.Id, NotificationCondition.OnlineFor, 4 * 60 * 60), CancellationToken.None);
        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);
        var beforeEventId = await fixture.Repository.GetLatestSubjectPresenceEventIdAsync(fixture.Subject.Id, CancellationToken.None);
        var beforeState = await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);

        var diagnostic = await service.EvaluateDiagnosticAsync(
            rule.Id, fixture.OnlineAt.AddHours(2), CancellationToken.None);

        Assert.Equal(RuleEvaluationDiagnosticStatus.AccumulatingDuration, diagnostic.Status);
        Assert.Equal(PresenceState.Online, diagnostic.CurrentState);
        Assert.Equal(fixture.OnlineAt, diagnostic.StateSince);
        Assert.Equal(TimeSpan.FromHours(2), diagnostic.CurrentDuration);
        Assert.Equal(TimeSpan.FromHours(2), diagnostic.RemainingDuration);
        Assert.Equal(.5, diagnostic.Progress, 6);
        Assert.Contains("预计还需要约", diagnostic.Explanation, StringComparison.Ordinal);
        Assert.Equal(beforeState, await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None));
        Assert.Empty(await fixture.Repository.GetNotificationDeliveriesForRuleAsync(rule.Id, CancellationToken.None));
        Assert.Equal(beforeEventId, await fixture.Repository.GetLatestSubjectPresenceEventIdAsync(fixture.Subject.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RuleDiagnosticExplainsMismatchedStateAndOfflineProgress()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var onlineRule = await fixture.Repository.CreateNotificationRuleAsync(
            Rule(fixture.Subject.Id, NotificationCondition.OnlineFor, 4 * 60 * 60), CancellationToken.None);
        var offlineRule = await fixture.Repository.CreateNotificationRuleAsync(
            Rule(fixture.Subject.Id, NotificationCondition.OfflineFor, 2 * 60 * 60), CancellationToken.None);
        var offlineAt = fixture.OnlineAt.AddHours(2);
        await fixture.Machine.ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, false)], offlineAt, CancellationToken.None);
        await fixture.Machine.ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, false)], offlineAt.AddSeconds(30), CancellationToken.None);
        var service = new NotificationRuleService(fixture.Repository, fixture.Presence);

        var mismatch = await service.EvaluateDiagnosticAsync(onlineRule.Id, offlineAt.AddMinutes(1), CancellationToken.None);
        var progress = await service.EvaluateDiagnosticAsync(offlineRule.Id, offlineAt.AddHours(1), CancellationToken.None);

        Assert.Equal(RuleEvaluationDiagnosticStatus.WaitingForState, mismatch.Status);
        Assert.Contains("当前主体处于离线状态", mismatch.Explanation, StringComparison.Ordinal);
        Assert.Equal(RuleEvaluationDiagnosticStatus.AccumulatingDuration, progress.Status);
        Assert.Equal(PresenceState.Offline, progress.CurrentState);
        Assert.Equal(TimeSpan.FromHours(1), progress.CurrentDuration);
        Assert.Equal(.5, progress.Progress, 6);
    }

    [Fact]
    public async Task EventRuleDiagnosticWaitsForNewEventWithoutAdvancingWatermark()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var rule = await fixture.Repository.CreateNotificationRuleAsync(
            Rule(fixture.Subject.Id, NotificationCondition.DetectedOnline, 0), CancellationToken.None);
        var beforeEventId = await fixture.Repository.GetLatestSubjectPresenceEventIdAsync(fixture.Subject.Id, CancellationToken.None);
        var beforeState = await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);
        var diagnostic = await new NotificationRuleService(fixture.Repository, fixture.Presence)
            .EvaluateDiagnosticAsync(rule.Id, fixture.OnlineAt.AddHours(1), CancellationToken.None);

        Assert.Equal(RuleEvaluationDiagnosticStatus.WaitingForNewEvent, diagnostic.Status);
        Assert.Contains("正在监听新的“在线”事件", diagnostic.Explanation, StringComparison.Ordinal);
        Assert.Equal(beforeEventId, await fixture.Repository.GetLatestSubjectPresenceEventIdAsync(fixture.Subject.Id, CancellationToken.None));
        Assert.Equal(beforeState, await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None));
        Assert.Empty(await fixture.Repository.GetNotificationDeliveriesForRuleAsync(rule.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UserPauseKeepsSameStateSessionButTimelineShowsUserPausedGap()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var pauseAt = fixture.OnlineAt.AddHours(1);
        var resumeAt = pauseAt.AddHours(2);
        var gapId = await fixture.Repository.StartMonitoringGapAsync(pauseAt, "UserPaused", CancellationToken.None);
        await fixture.Repository.ResetCurrentObservedStateAsync(fixture.Router.Id, CancellationToken.None);
        await fixture.Machine.ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, true)], resumeAt, CancellationToken.None);
        await fixture.Repository.CloseOpenMonitoringGapsAsync(resumeAt, CancellationToken.None);

        var session = Assert.Single(await fixture.Repository.GetSessionsAsync(fixture.Device.Id, CancellationToken.None));
        var state = await fixture.Repository.GetSubjectCurrentStateAsync(fixture.Subject.Id, CancellationToken.None);
        var deviceTimeline = await new PresenceStatisticsService(fixture.Repository)
            .GetTimelineAsync(fixture.Device.Id, fixture.OnlineAt, resumeAt.AddHours(1), CancellationToken.None);
        var subjectTimeline = await fixture.Presence
            .GetTimelineAsync(fixture.Subject.Id, fixture.OnlineAt, resumeAt.AddHours(1), CancellationToken.None);

        Assert.Null(session.EndedAt);
        Assert.Equal(fixture.OnlineAt, state!.StateSince);
        Assert.Equal([PresenceState.Online, PresenceState.Unknown, PresenceState.Online], deviceTimeline.Select(value => value.State).ToArray());
        Assert.Equal([PresenceState.Online, PresenceState.Unknown, PresenceState.Online], subjectTimeline.Select(value => value.State).ToArray());
        Assert.Equal("UserPaused", Assert.Single(subjectTimeline, value => value.State == PresenceState.Unknown).UnobservedReason);
        var gap = Assert.Single(await fixture.Repository.GetMonitoringGapsAsync(pauseAt, resumeAt.AddMinutes(1), CancellationToken.None));
        Assert.Equal(gapId, gap.Id);
        Assert.Equal(resumeAt, gap.EndedAt);
    }

    [Fact]
    public async Task UserPauseStateChangeCreatesDetectedEventAtResumeNotOrdinaryOffline()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var pauseAt = fixture.OnlineAt.AddHours(1);
        var resumeAt = pauseAt.AddHours(2);
        await fixture.Repository.StartMonitoringGapAsync(pauseAt, "UserPaused", CancellationToken.None);
        await fixture.Repository.ResetCurrentObservedStateAsync(fixture.Router.Id, CancellationToken.None);
        await fixture.Machine.ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, false)], resumeAt, CancellationToken.None);
        await fixture.Repository.CloseOpenMonitoringGapsAsync(resumeAt, CancellationToken.None);

        var sessions = await fixture.Repository.GetSessionsAsync(fixture.Device.Id, CancellationToken.None);
        var deviceEvents = await fixture.Repository.GetEventsAsync(fixture.Device.Id, CancellationToken.None);
        var subjectEvents = await fixture.Repository.GetSubjectPresenceEventsAsync(fixture.Subject.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.Equal(resumeAt, Assert.Single(sessions).EndedAt);
        Assert.DoesNotContain(deviceEvents, value => value.EventType == PresenceEventType.Offline && value.ObservedAt == resumeAt);
        var detected = Assert.Single(subjectEvents, value => value.EventType == SubjectPresenceEventType.DetectedOfflineAfterGap);
        Assert.Equal(resumeAt, detected.ObservedAt);
        Assert.Equal(PresenceState.Offline, (await fixture.Presence.GetCurrentFactAsync(fixture.Subject.Id, resumeAt.AddMinutes(1), CancellationToken.None))!.CurrentState);
    }

    [Fact]
    public async Task RestartDuringUserPauseDoesNotCreateUnexpectedGapOrBreakSession()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        await fixture.Repository.StartApplicationRunAsync(fixture.OnlineAt.AddMinutes(-30), CancellationToken.None);
        // StartApplicationRun resets the observed projection.  The next
        // successful snapshot rehydrates the same historical online episode.
        await fixture.Machine.ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, true)], fixture.OnlineAt, CancellationToken.None);

        var pauseAt = fixture.OnlineAt.AddHours(1);
        var resumeAt = pauseAt.AddHours(2);
        await fixture.Repository.StartMonitoringGapAsync(pauseAt, "UserPaused", CancellationToken.None);

        // Leave the application run open to model a crash/forced termination.
        await fixture.Repository.StartApplicationRunAsync(resumeAt, CancellationToken.None);
        var gapsBeforeResume = await fixture.Repository.GetMonitoringGapsAsync(pauseAt.AddMinutes(-1), resumeAt, CancellationToken.None);
        Assert.DoesNotContain(gapsBeforeResume, value => value.Reason == "UnexpectedTermination");

        await fixture.Machine.ApplySnapshotAsync(fixture.Router.Id, [Observation(fixture.Device, true)], resumeAt, CancellationToken.None);
        await fixture.Repository.CloseOpenMonitoringGapsAsync(resumeAt, CancellationToken.None);

        var session = Assert.Single(await fixture.Repository.GetSessionsAsync(fixture.Device.Id, CancellationToken.None));
        Assert.Equal(fixture.OnlineAt, session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.Equal(fixture.OnlineAt, (await fixture.Repository.GetSubjectCurrentStateAsync(fixture.Subject.Id, CancellationToken.None))!.StateSince);
    }

    [Fact]
    public async Task PausedPresenceRuntimeDoesNotEvaluateOrCreateQqDeliveries()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var rule = await fixture.Repository.CreateNotificationRuleAsync(
            Rule(fixture.Subject.Id, NotificationCondition.OnlineFor, 30 * 60), CancellationToken.None);
        fixture.Monitor.SelectRouter(fixture.Router);
        await fixture.Monitor.PauseAsync(fixture.OnlineAt.AddHours(1), CancellationToken.None);
        var beforeState = await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None);

        await using var runtime = new NotificationRuntime(fixture.Monitor, fixture.RuleService, new NoopDispatcher());
        await runtime.EvaluateAndDispatchAsync(CancellationToken.None);

        Assert.Equal(beforeState, await fixture.Repository.GetNotificationRuleStateAsync(rule.Id, CancellationToken.None));
        Assert.Empty(await fixture.Repository.GetNotificationDeliveriesForRuleAsync(rule.Id, CancellationToken.None));
        Assert.Null(runtime.LastEvaluationError);
    }

    [Fact]
    public async Task RouterDiagnosticsArePersistedPerRouterAndSameMacsStaySeparate()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root));
            await repository.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var routerA = await repository.UpsertRouterAsync(new(0, "did-a", "model-a", "partner-a", "路由器 A", null, null, now, now), CancellationToken.None);
            var routerB = await repository.UpsertRouterAsync(new(0, "did-b", "model-b", "partner-b", "路由器 B", null, null, now, now), CancellationToken.None);
            await repository.InsertDeviceAsync(Device(routerA.Id, "DA:92:47:11:22:32", now), CancellationToken.None);
            await repository.InsertDeviceAsync(Device(routerB.Id, "DA:92:47:11:22:32", now), CancellationToken.None);
            var diagnostic = new RouterCapabilityDiagnostic(routerB.Id, routerB.MiotDid, routerB.MiotModel, false,
                "https://example.invalid/device_list", 404, false, ["code"], null, false, "未返回 partner_id", now);
            await repository.UpsertRouterCapabilityDiagnosticAsync(diagnostic, CancellationToken.None);

            var mapA = await repository.GetDeviceSubjectMapAsync(routerA.Id, CancellationToken.None);
            var mapB = await repository.GetDeviceSubjectMapAsync(routerB.Id, CancellationToken.None);
            var saved = await repository.GetRouterCapabilityDiagnosticAsync(routerB.Id, CancellationToken.None);

            Assert.NotEqual(mapA.Values.Single(), mapB.Values.Single());
            Assert.NotNull(saved);
            Assert.Equal(diagnostic with { SuccessfulFields = saved!.SuccessfulFields }, saved);
            Assert.False(saved!.PresenceAvailable);
            Assert.Equal(404, saved.LastApiCode);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task MonitoringGapsAreScopedToTheirRouterAndGlobalGapsRemainVisible()
    {
        var root = TemporaryRoot();
        try
        {
            var paths = new AppPaths(root);
            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var routerA = await repository.UpsertRouterAsync(new(0, "gap-did-a", "model-a", "gap-partner-a", "路由器 A", null, null, now, now), CancellationToken.None);
            var routerB = await repository.UpsertRouterAsync(new(0, "gap-did-b", "model-b", "gap-partner-b", "路由器 B", null, null, now, now), CancellationToken.None);

            await repository.StartMonitoringGapAsync(now, "Router A unavailable", CancellationToken.None, routerA.Id);
            await repository.StartMonitoringGapAsync(now, "Router B unavailable", CancellationToken.None, routerB.Id);
            await repository.StartMonitoringGapAsync(now, "software gap", CancellationToken.None);

            var gapsForA = await repository.GetMonitoringGapsAsync(now.AddMinutes(-1), now.AddMinutes(1), CancellationToken.None, routerA.Id);
            var gapsForB = await repository.GetMonitoringGapsAsync(now.AddMinutes(-1), now.AddMinutes(1), CancellationToken.None, routerB.Id);

            Assert.Contains(gapsForA, value => value.RouterId == routerA.Id && value.Reason == "Router A unavailable");
            Assert.DoesNotContain(gapsForA, value => value.RouterId == routerB.Id);
            Assert.Contains(gapsForB, value => value.RouterId == routerB.Id && value.Reason == "Router B unavailable");
            Assert.DoesNotContain(gapsForB, value => value.RouterId == routerA.Id);
            Assert.Contains(gapsForA, value => value.RouterId is null && value.Reason == "software gap");
            Assert.Contains(gapsForB, value => value.RouterId is null && value.Reason == "software gap");
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void SemanticVersionAndDiagnosticRedactionAreConservative()
    {
        Assert.True(SemanticVersion.Parse("2.1.10").CompareTo(SemanticVersion.Parse("2.1.9")) > 0);
        Assert.Equal(new SemanticVersion(2, 1, 2), SemanticVersion.Parse("v2.1.2-beta.1+build"));
        Assert.Equal("C47****FD8", DiagnosticsRedaction.MaskOpenId("C47abcdefFD8"));
        Assert.Equal("DA:92:47:**:**:32", DiagnosticsRedaction.MaskMac("DA:92:47:11:22:32"));

        const string secret = "service-token-value";
        var sanitized = DiagnosticsRedaction.RedactText(
            $"token={secret}; serviceToken={secret}; accessToken=access-value; AppSecret=app-value; Authorization: Bearer abc123; openid=C47abcdefFD8; mac=DA:92:47:11:22:32");

        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("access-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("app-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("C47abcdefFD8", sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
        Assert.Contains("DA:92:47:**:**:32", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseCheckIgnoresDraftAndPrereleaseAndReportsNetworkFailure()
    {
        const string releases = """
            [
              {"tag_name":"v2.1.9","name":"old","html_url":"https://example.invalid/old","draft":false,"prerelease":false},
              {"tag_name":"v2.1.10","name":"new","html_url":"https://example.invalid/new","draft":false,"prerelease":false},
              {"tag_name":"v9.0.0-beta","name":"pre","html_url":"https://example.invalid/pre","draft":false,"prerelease":true},
              {"tag_name":"v10.0.0","name":"draft","html_url":"https://example.invalid/draft","draft":true,"prerelease":false}
            ]
            """;
        using (var http = new HttpClient(new StubHttpHandler(releases)))
        using (var service = new GitHubReleaseUpdateService(http, "2.1.9"))
        {
            var result = await service.CheckAsync(CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.True(result.HasUpdate);
            Assert.Equal("2.1.10", result.LatestRelease!.Version.ToString());
            Assert.Equal("https://example.invalid/new", result.LatestRelease.HtmlUrl);
        }

        using var failingHttp = new HttpClient(new ThrowingHttpHandler());
        using var failingService = new GitHubReleaseUpdateService(failingHttp, "2.1.10");
        var failure = await failingService.CheckAsync(CancellationToken.None);
        Assert.False(failure.Succeeded);
        Assert.False(failure.HasUpdate);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));
    }

    [Fact]
    public async Task DiagnosticZipContainsMetadataAndSanitizedLogsButNoDatabase()
    {
        await using var fixture = await CreateOnlineFixtureAsync();
        var paths = fixture.Paths;
        await new JsonSettingsStore(paths).SaveAsync(new PresenceSettings
        {
            PollingIntervalSeconds = 30,
            StartWithWindows = true,
            StartMinimized = true,
            Qq = new QqNotificationSettings(true, true, "app-id", DefaultTargetId: "C47abcdefFD8")
        }, CancellationToken.None);
        Directory.CreateDirectory(paths.LogsDirectory);
        const string token = "diagnostic-secret-token";
        await File.WriteAllTextAsync(Path.Combine(paths.LogsDirectory, "notification-runtime.log"),
            $"2026-08-31T10:00:00Z [Error] token={token}; Authorization: Bearer bearer-value; openid=C47abcdefFD8; mac=DA:92:47:11:22:32");
        var runtime = new NotificationRuntime(fixture.Monitor, fixture.RuleService, new NoopDispatcher());
        try
        {
            var zip = Path.Combine(fixture.Root, "CloudLight-XiaoMi-Diagnostics.zip");
            var exporter = new DiagnosticsExportService(paths, fixture.Repository, fixture.Monitor, runtime, null,
                new JsonSettingsStore(paths), new SqliteDatabaseBackupService(paths));
            await exporter.ExportAsync(zip, CancellationToken.None);

            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, value => value.FullName == "diagnostics.json");
            Assert.Contains(archive.Entries, value => value.FullName == "settings-redacted.json");
            Assert.Contains(archive.Entries, value => value.FullName == "database-info.txt");
            Assert.Contains(archive.Entries, value => value.FullName == "runtime-info.txt");
            Assert.DoesNotContain(archive.Entries, value => value.FullName.EndsWith("presence.db", StringComparison.OrdinalIgnoreCase));
            var log = await ReadEntryAsync(archive.GetEntry("logs/notification-runtime.log")!);
            var settings = await ReadEntryAsync(archive.GetEntry("settings-redacted.json")!);
            Assert.DoesNotContain(token, log, StringComparison.Ordinal);
            Assert.DoesNotContain("bearer-value", log, StringComparison.Ordinal);
            Assert.DoesNotContain("C47abcdefFD8", log, StringComparison.Ordinal);
            Assert.Contains("DA:92:47:**:**:32", log, StringComparison.Ordinal);
            Assert.DoesNotContain("AppSecret", settings, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C47abcdefFD8", settings, StringComparison.Ordinal);
        }
        finally { await runtime.DisposeAsync(); }
    }

    private static NotificationRule Rule(long subjectId, NotificationCondition condition, long seconds) =>
        new(0, subjectId, true, condition, seconds, NotificationChannelType.QQ, NotificationTargetType.Private,
            "openid", "{name} {duration}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static NetworkDevice Device(long routerId, string mac, DateTimeOffset at) =>
        new(0, routerId, mac, "Phone", "Phone", null, null, "192.168.1.2", "5G", -45,
            PresenceState.Offline, at, at, at);

    private static ObservedNetworkDevice Observation(NetworkDevice device, bool online) =>
        new(device.MacAddress, device.OriginalName, device.OriginName, device.LastIp, online, null, device.ConnectionType, device.Signal);

    private static async Task<OnlineFixture> CreateOnlineFixtureAsync()
    {
        var root = TemporaryRoot();
        var paths = new AppPaths(root);
        var repository = new SqlitePresenceRepository(paths);
        await repository.InitializeAsync(CancellationToken.None);
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var router = await repository.UpsertRouterAsync(new(0, "reliability-did", "xiaomi.router.rd03", "reliability-partner", "测试路由器", null, null, now, now), CancellationToken.None);
        var device = await repository.InsertDeviceAsync(Device(router.Id, "AA:BB:CC:DD:EE:FF", now), CancellationToken.None);
        var subjectId = (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id];
        var subject = (await repository.GetSubjectAsync(subjectId, CancellationToken.None))!;
        var presence = new SubjectPresenceService(repository, new PresenceStatisticsService(repository));
        var machine = new PresenceStateMachine(repository);
        var onlineAt = now.AddHours(1);
        await machine.ApplySnapshotAsync(router.Id, [Observation(device, true)], onlineAt, CancellationToken.None);
        var source = new EmptyPresenceSource();
        var monitor = new PresenceMonitor(source, repository, machine);
        var ruleService = new NotificationRuleService(repository, presence);
        return new(root, paths, repository, router, device, subject, presence, machine, monitor, ruleService, onlineAt);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task AssertIndependentDatabaseAsync(string path, int expectedVersion)
    {
        await using var connection = await OpenDatabaseAsync(path, SqliteOpenMode.ReadOnly);
        Assert.Equal(expectedVersion, await SqliteDatabaseBackupService.ReadUserVersionAsync(connection, CancellationToken.None));
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None)));
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Reliability-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectory(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record OnlineFixture(
        string Root,
        AppPaths Paths,
        SqlitePresenceRepository Repository,
        Router Router,
        NetworkDevice Device,
        PresenceSubject Subject,
        SubjectPresenceService Presence,
        PresenceStateMachine Machine,
        PresenceMonitor Monitor,
        NotificationRuleService RuleService,
        DateTimeOffset OnlineAt) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            DeleteDirectory(Root);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyPresenceSource : IXiaomiPresenceSource
    {
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ObservedNetworkDevice>>([]);
    }

    private sealed class NoopDispatcher : INotificationDispatcher
    {
        public Task DispatchAsync(NotificationRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DispatchSystemAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RetryPendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHttpHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("network unavailable"));
    }
}
