using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;
using Microsoft.Win32;

namespace CloudLight.Presence.Tests;

public sealed class StatisticsAndTransferTests
{
    [Fact]
    public void StartupRegistrationQuotesExecutableAndCanBeRemoved()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run"; const string valueName = "CloudLight Presence";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64); using var key = root.OpenSubKey(runKey, true) ?? root.CreateSubKey(runKey, true);
        var previous = key.GetValue(valueName); var service = new StartupRegistrationService();
        try
        {
            service.Apply(true); Assert.Equal($"\"{Environment.ProcessPath}\" --startup", key.GetValue(valueName));
            service.Apply(false); Assert.Null(key.GetValue(valueName));
        }
        finally { if (previous is null) key.DeleteValue(valueName, false); else key.SetValue(valueName, previous, RegistryValueKind.String); }
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
            Assert.Equal(lastUpdate, gap.StartedAt); Assert.Equal(restarted, gap.EndedAt); Assert.Equal("UnexpectedTermination", gap.Reason);
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
            await source.AddEventAsync(new PresenceEvent(0, device.Id, PresenceEventType.InitialObservation, now, PresenceSource.Polling), CancellationToken.None);
            await source.AddSessionAsync(new PresenceSession(0, device.Id, now, null, false, false), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "auth.dat"), "passToken serviceToken ssecurity Xiaomi Cookie");
            await new PresenceDataTransferService(new AppPaths(sourceRoot)).ExportAsync(backup, CancellationToken.None);
            var exported = await File.ReadAllTextAsync(backup); Assert.DoesNotContain("passToken", exported, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("serviceToken", exported, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("ssecurity", exported, StringComparison.OrdinalIgnoreCase); Assert.Contains("\"containsAuthentication\": false", exported);

            var targetPaths = new AppPaths(targetRoot); var target = new SqlitePresenceRepository(targetPaths); await target.InitializeAsync(CancellationToken.None); var transfer = new PresenceDataTransferService(targetPaths);
            var first = await transfer.ImportAsync(backup, CancellationToken.None); var second = await transfer.ImportAsync(backup, CancellationToken.None);
            var importedRouter = Assert.Single(await target.GetRoutersAsync(CancellationToken.None)); var importedDevice = Assert.Single(await target.GetDevicesAsync(importedRouter.Id, CancellationToken.None));
            Assert.Equal(1, first.AddedEvents); Assert.Equal(0, second.AddedEvents); Assert.Single(await target.GetEventsAsync(importedDevice.Id, CancellationToken.None)); Assert.Single(await target.GetSessionsAsync(importedDevice.Id, CancellationToken.None)); Assert.Equal("我的手机", importedDevice.CustomName);
        }
        finally { if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, true); if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true); if (File.Exists(backup)) File.Delete(backup); }
    }

    private static string TemporaryRoot() { var path = Path.Combine(Path.GetTempPath(), "CloudLight-Presence-Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Router RouterAt(DateTimeOffset at) => new(0, "did-transfer", "xiaomi.router.rd03", "partner-transfer", "Router", null, null, at, at);
    private static NetworkDevice DeviceAt(long routerId, DateTimeOffset at) => new(0, routerId, "AA:BB:CC:DD:EE:FF", "Phone", "Phone", null, null, "192.168.1.2", "5 GHz", -55, PresenceState.Offline, at, at, null);
}
