using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;
using Microsoft.Win32;
using System.Text.Json;

namespace CloudLight.Presence.Tests;

public sealed class StatisticsAndTransferTests
{
    [Fact]
    public void StartupRegistrationQuotesExecutableAndCanBeRemoved()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run"; const string valueName = "CloudLight XiaoMi"; const string legacyValueName = "CloudLight Presence";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64); using var key = root.OpenSubKey(runKey, true) ?? root.CreateSubKey(runKey, true);
        var previous = key.GetValue(valueName); var legacyPrevious = key.GetValue(legacyValueName); var service = new StartupRegistrationService(Environment.ProcessPath);
        try
        {
            key.SetValue(legacyValueName, "legacy.exe", RegistryValueKind.String); service.Apply(true); Assert.Equal($"\"{Environment.ProcessPath}\" --startup", key.GetValue(valueName)); Assert.Null(key.GetValue(legacyValueName));
            service.Apply(false); Assert.Null(key.GetValue(valueName));
        }
        finally { if (previous is null) key.DeleteValue(valueName, false); else key.SetValue(valueName, previous, RegistryValueKind.String); if (legacyPrevious is null) key.DeleteValue(legacyValueName, false); else key.SetValue(legacyValueName, legacyPrevious, RegistryValueKind.String); }
    }

    [Fact]
    public async Task StatisticsClipsSessionsAndKeepsMonitoringGapUnknown()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root)); await repository.InitializeAsync(CancellationToken.None);
            var from = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero); var to = from.AddDays(1);
            var router = await repository.UpsertRouterAsync(RouterAt(from.AddDays(-2)), CancellationToken.None);
            var device = await repository.InsertDeviceAsync(DeviceAt(router.Id, from.AddDays(-1)), CancellationToken.None);
            await repository.AddSessionAsync(new PresenceSession(0, device.Id, from.AddHours(-2), from.AddHours(2), true, true), CancellationToken.None);
            await repository.AddSessionAsync(new PresenceSession(0, device.Id, from.AddHours(10), from.AddHours(12), false, true), CancellationToken.None);
            var gap = await repository.StartMonitoringGapAsync(from.AddHours(5), "test", CancellationToken.None); await repository.EndMonitoringGapAsync(gap, from.AddHours(7), CancellationToken.None);
            var value = await new PresenceStatisticsService(repository).GetStatisticsAsync(device.Id, from, to, CancellationToken.None);
            Assert.Equal(TimeSpan.FromHours(4), value.KnownOnlineDuration); Assert.Equal(TimeSpan.FromHours(18), value.KnownOfflineDuration); Assert.Equal(TimeSpan.FromHours(2), value.UnknownDuration); Assert.Equal(22d / 24d, value.Coverage, 6);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnexpectedApplicationRunUsesLastSuccessfulCloudUpdateForGap()
    {
        var root = TemporaryRoot();
        try
        {
            var repository = new SqlitePresenceRepository(new AppPaths(root)); await repository.InitializeAsync(CancellationToken.None);
            var started = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero); var lastUpdate = started.AddMinutes(5); var restarted = started.AddMinutes(12);
            var firstRun = await repository.StartApplicationRunAsync(started, CancellationToken.None); await repository.UpdateApplicationRunCloudUpdateAsync(firstRun, lastUpdate, CancellationToken.None);
            var secondRun = await repository.StartApplicationRunAsync(restarted, CancellationToken.None);
            var gap = Assert.Single(await repository.GetMonitoringGapsAsync(started, restarted.AddMinutes(1), CancellationToken.None));
            Assert.Equal(lastUpdate, gap.StartedAt); Assert.Null(gap.EndedAt); Assert.Equal("UnexpectedTermination", gap.Reason);
            await repository.CloseOpenMonitoringGapsAsync(restarted, CancellationToken.None);
            gap = Assert.Single(await repository.GetMonitoringGapsAsync(started, restarted.AddMinutes(1), CancellationToken.None));
            Assert.Equal(restarted, gap.EndedAt);
            await repository.EndApplicationRunAsync(secondRun, restarted.AddMinutes(1), CancellationToken.None);
            _ = await repository.StartApplicationRunAsync(restarted.AddMinutes(2), CancellationToken.None);
            Assert.Single(await repository.GetMonitoringGapsAsync(started, restarted.AddMinutes(3), CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImportIsIdempotentAndExportContainsNoAuthentication()
    {
        var sourceRoot = TemporaryRoot(); var targetRoot = TemporaryRoot(); var backup = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.clpresence");
        try
        {
            var source = new SqlitePresenceRepository(new AppPaths(sourceRoot)); await source.InitializeAsync(CancellationToken.None); var now = DateTimeOffset.UtcNow;
            var router = await source.UpsertRouterAsync(RouterAt(now), CancellationToken.None); var device = await source.InsertDeviceAsync(DeviceAt(router.Id, now) with { CustomName = "我的手机", Note = "主力" }, CancellationToken.None);
            var subject = await source.CreateSubjectAsync("爸爸", "两个网络身份", Guid.NewGuid(), now, CancellationToken.None); await source.SetSubjectDevicesAsync(subject.Id, [device.Id], now, CancellationToken.None);
            await source.CreateNotificationRuleAsync(new NotificationRule(0, subject.Id, true, NotificationCondition.OfflineFor, 14 * 60 * 60, NotificationChannelType.QQ, NotificationTargetType.Private, "user-openid", "{name} {duration}", now, now), CancellationToken.None);
            await source.CreateNotificationRuleAsync(new NotificationRule(0, subject.Id, true, NotificationCondition.DetectedOnline, 0, NotificationChannelType.QQ, NotificationTargetType.Private, "user-openid", "{name} {currentTime}", now, now), CancellationToken.None);
            await source.AddEventAsync(new PresenceEvent(0, device.Id, PresenceEventType.InitialObservation, now, PresenceSource.Polling), CancellationToken.None);
            await source.AddSessionAsync(new PresenceSession(0, device.Id, now, null, false, false), CancellationToken.None);
            var gap = await source.StartMonitoringGapAsync(now.AddMinutes(-1), "导出检测活动", CancellationToken.None); await source.EndMonitoringGapAsync(gap, now, CancellationToken.None);
            await source.AddSubjectPresenceEventAsync(new SubjectPresenceEvent(0, subject.Id, SubjectPresenceEventType.DetectedOfflineAfterGap, now, gap), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "auth.dat"), "passToken serviceToken ssecurity Xiaomi Cookie");
            await new PresenceDataTransferService(new AppPaths(sourceRoot)).ExportAsync(backup, CancellationToken.None);
            var exported = await File.ReadAllTextAsync(backup); Assert.DoesNotContain("passToken", exported, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("serviceToken", exported, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("ssecurity", exported, StringComparison.OrdinalIgnoreCase); Assert.Contains("\"containsAuthentication\": false", exported);

            var targetPaths = new AppPaths(targetRoot); var target = new SqlitePresenceRepository(targetPaths); await target.InitializeAsync(CancellationToken.None); var transfer = new PresenceDataTransferService(targetPaths);
            var first = await transfer.ImportAsync(backup, CancellationToken.None); var second = await transfer.ImportAsync(backup, CancellationToken.None);
            var importedRouter = Assert.Single(await target.GetRoutersAsync(CancellationToken.None)); var importedDevice = Assert.Single(await target.GetDevicesAsync(importedRouter.Id, CancellationToken.None)); var importedSubject = Assert.Single(await target.GetSubjectsAsync(CancellationToken.None));
            Assert.Equal(2, first.AddedEvents); Assert.Equal(0, second.AddedEvents); Assert.Single(await target.GetEventsAsync(importedDevice.Id, CancellationToken.None)); Assert.Single(await target.GetSessionsAsync(importedDevice.Id, CancellationToken.None)); Assert.Equal("我的手机", importedDevice.CustomName);
            Assert.Equal("爸爸", importedSubject.DisplayName); Assert.Equal(importedDevice.Id, Assert.Single(await target.GetSubjectDevicesAsync(importedSubject.Id, CancellationToken.None)).Id); var importedRules = await target.GetNotificationRulesAsync(false, CancellationToken.None); Assert.Equal(2, importedRules.Count); var importedRule = Assert.Single(importedRules, value => value.Condition == NotificationCondition.OfflineFor); var importedEventRule = Assert.Single(importedRules, value => value.Condition == NotificationCondition.DetectedOnline); Assert.Equal(importedSubject.Id, importedRule.SubjectId); Assert.Equal(importedSubject.Id, importedEventRule.SubjectId); Assert.Equal("user-openid", importedRule.TargetId); Assert.Contains("\"version\": 2", exported);
            var importedDetected = Assert.Single(await target.GetSubjectPresenceEventsAsync(importedSubject.Id, now.AddMinutes(-2), now.AddMinutes(1), CancellationToken.None)); Assert.Equal(SubjectPresenceEventType.DetectedOfflineAfterGap, importedDetected.EventType); Assert.Equal(now, importedDetected.ObservedAt);
        }
        finally { if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, true); if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true); if (File.Exists(backup)) File.Delete(backup); }
    }

    [Fact]
    public async Task VersionOneBackupImportsDevicesAsStandaloneSubjects()
    {
        var root = TemporaryRoot(); var backup = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.clpresence");
        try
        {
            var now = DateTimeOffset.UtcNow; var document = new PresenceDataTransferService.ExportDocument(
                new("CloudLight.Presence.Export", 1, now, "legacy", false),
                [new("legacy-did", "router", "legacy-partner", "Router", null, null, now, now)],
                [new("legacy-did", "AA:BB:CC:DD:EE:01", "Phone", "Phone", null, null, "192.168.1.3", "5G", -50, (int)PresenceState.Offline, now, now, now)], [], [], []);
            await File.WriteAllTextAsync(backup, JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var paths = new AppPaths(root); var repository = new SqlitePresenceRepository(paths); await repository.InitializeAsync(CancellationToken.None); await new PresenceDataTransferService(paths).ImportAsync(backup, CancellationToken.None);
            var router = Assert.Single(await repository.GetRoutersAsync(CancellationToken.None)); var device = Assert.Single(await repository.GetDevicesAsync(router.Id, CancellationToken.None)); var subject = Assert.Single(await repository.GetSubjectsAsync(CancellationToken.None)); Assert.Equal("Phone", subject.DisplayName); Assert.Equal(subject.Id, (await repository.GetDeviceSubjectMapAsync(router.Id, CancellationToken.None))[device.Id]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (File.Exists(backup)) File.Delete(backup); }
    }

    [Fact]
    public async Task ConfirmationHistoryAndPendingOfflineStateRoundTripWithoutAGapReference()
    {
        var sourceRoot = TemporaryRoot(); var targetRoot = TemporaryRoot(); var backup = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.clpresence");
        try
        {
            var at = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
            var sourcePaths = new AppPaths(sourceRoot); var source = new SqlitePresenceRepository(sourcePaths); await source.InitializeAsync(CancellationToken.None);
            var router = await source.UpsertRouterAsync(RouterAt(at), CancellationToken.None); var device = await source.InsertDeviceAsync(DeviceAt(router.Id, at), CancellationToken.None);
            var subject = await source.CreateSubjectAsync("爸爸", null, Guid.NewGuid(), at, CancellationToken.None); await source.SetSubjectDevicesAsync(subject.Id, [device.Id], at, CancellationToken.None);
            await source.UpsertSubjectCurrentStateAsync(new SubjectCurrentState(subject.Id, PresenceState.Online, at, at.AddMinutes(2), at.AddMinutes(1)), CancellationToken.None);
            await source.AddSubjectPresenceEventAsync(new SubjectPresenceEvent(0, subject.Id, SubjectPresenceEventType.InitialOnline, at, null, at), CancellationToken.None);
            await source.AddSubjectPresenceEventAsync(new SubjectPresenceEvent(0, subject.Id, SubjectPresenceEventType.ConfirmedOffline, at.AddMinutes(2), null, at.AddMinutes(1)), CancellationToken.None);
            await new PresenceDataTransferService(sourcePaths).ExportAsync(backup, CancellationToken.None);

            var targetPaths = new AppPaths(targetRoot); var target = new SqlitePresenceRepository(targetPaths); await target.InitializeAsync(CancellationToken.None);
            await new PresenceDataTransferService(targetPaths).ImportAsync(backup, CancellationToken.None);

            var imported = Assert.Single(await target.GetSubjectsAsync(CancellationToken.None));
            var state = await target.GetSubjectCurrentStateAsync(imported.Id, CancellationToken.None);
            var events = await target.GetSubjectPresenceEventsAsync(imported.Id, at.AddMinutes(-1), at.AddMinutes(3), CancellationToken.None);
            Assert.NotNull(state);
            Assert.Equal(at, state!.StateSince);
            Assert.Equal(at.AddMinutes(1), state.PendingOfflineSince);
            Assert.Equal(
                [SubjectPresenceEventType.ConfirmedOffline, SubjectPresenceEventType.InitialOnline],
                events.Select(value => value.EventType).ToArray());
            Assert.All(events, value => Assert.Null(value.MonitoringGapId));
        }
        finally { if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, true); if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true); if (File.Exists(backup)) File.Delete(backup); }
    }

    private static string TemporaryRoot() { var path = Path.Combine(Path.GetTempPath(), "CloudLight-Presence-Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Router RouterAt(DateTimeOffset at) => new(0, "did-transfer", "xiaomi.router.rd03", "partner-transfer", "Router", null, null, at, at);
    private static NetworkDevice DeviceAt(long routerId, DateTimeOffset at) => new(0, routerId, "AA:BB:CC:DD:EE:FF", "Phone", "Phone", null, null, "192.168.1.2", "5 GHz", -55, PresenceState.Offline, at, at, null);
}
