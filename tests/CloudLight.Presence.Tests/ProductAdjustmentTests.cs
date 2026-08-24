using CloudLight.Presence.App.ViewModels;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class ProductAdjustmentTests
{
    [Fact]
    public void DefaultRootUsesActualDocumentsDirectory()
    {
        var paths = new AppPaths();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CloudLight", "CloudLight XiaoMi"), paths.RootDirectory);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrationCopiesDatabaseSettingsAndAuthButKeepsLegacyData()
    {
        var legacy = TemporaryDirectory(); var target = Path.Combine(TemporaryDirectory(), "CloudLight XiaoMi");
        try
        {
            var legacyRepository = new SqlitePresenceRepository(new AppPaths(legacy)); await legacyRepository.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow; var router = await legacyRepository.UpsertRouterAsync(RouterAt(now), CancellationToken.None);
            _ = await legacyRepository.InsertDeviceAsync(DeviceAt(router.Id, now) with { CustomName = "保留名称", Note = "保留备注" }, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(legacy, "settings.json"), "{\"startMinimized\":false}");
            await File.WriteAllBytesAsync(Path.Combine(legacy, "auth.dat"), [1, 2, 3, 4]);

            var paths = new AppPaths(target, legacy); var result = await new AppDataMigrationService(paths).MigrateIfNeededAsync(CancellationToken.None);
            Assert.True(result.Migrated); Assert.True(File.Exists(paths.DatabasePath)); Assert.True(File.Exists(paths.SettingsPath)); Assert.True(File.Exists(paths.AuthPath));
            Assert.True(File.Exists(Path.Combine(legacy, "presence.db"))); Assert.True(File.Exists(Path.Combine(legacy, "settings.json"))); Assert.True(File.Exists(Path.Combine(legacy, "auth.dat")));
            var migrated = new SqlitePresenceRepository(paths); await migrated.InitializeAsync(CancellationToken.None);
            var migratedRouter = Assert.Single(await migrated.GetRoutersAsync(CancellationToken.None)); var device = Assert.Single(await migrated.GetDevicesAsync(migratedRouter.Id, CancellationToken.None));
            Assert.Equal("保留名称", device.CustomName); Assert.Equal("保留备注", device.Note);
        }
        finally { DeleteTree(legacy); DeleteTree(Directory.GetParent(target)!.FullName); }
    }

    [Fact]
    public async Task ExistingNewDataIsNeverOverwritten()
    {
        var legacy = TemporaryDirectory(); var target = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(legacy, "settings.json"), "old"); await File.WriteAllTextAsync(Path.Combine(target, "settings.json"), "new");
            var result = await new AppDataMigrationService(new AppPaths(target, legacy)).MigrateIfNeededAsync(CancellationToken.None);
            Assert.False(result.Migrated); Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "settings.json")));
        }
        finally { DeleteTree(legacy); DeleteTree(target); }
    }

    [Fact]
    public async Task ExistingSettingsWithoutIntervalKeepTenSecondDefault()
    {
        var root = TemporaryDirectory();
        try
        {
            var paths = new AppPaths(root);
            await File.WriteAllTextAsync(paths.SettingsPath, "{\"startMinimized\":false}");
            var settings = await new JsonSettingsStore(paths).LoadAsync(CancellationToken.None);
            Assert.Equal(10, settings.PollingIntervalSeconds);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task PollingIntervalIsPersistedAndAppliedToCurrentMonitor()
    {
        var root = TemporaryDirectory();
        try
        {
            var paths = new AppPaths(root); var repository = new SqlitePresenceRepository(paths); await repository.InitializeAsync(CancellationToken.None);
            var source = new ControlledSource(); var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            var viewModel = new MainViewModel(repository, source, monitor, new JsonSettingsStore(paths));

            await viewModel.SavePollingIntervalAsync(30);

            Assert.Equal(TimeSpan.FromSeconds(30), monitor.PollingInterval);
            Assert.Equal(30, (await new JsonSettingsStore(paths).LoadAsync(CancellationToken.None)).PollingIntervalSeconds);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task ManualRefreshIsSingleFlightAndAppliesSnapshot()
    {
        var root = TemporaryDirectory();
        try
        {
            var paths = new AppPaths(root); var repository = new SqlitePresenceRepository(paths); await repository.InitializeAsync(CancellationToken.None);
            var router = await repository.UpsertRouterAsync(RouterAt(DateTimeOffset.UtcNow), CancellationToken.None);
            var source = new ControlledSource(); var monitor = new PresenceMonitor(source, repository, new PresenceStateMachine(repository));
            var first = NextSnapshotAsync(monitor); await monitor.StartAsync(router, CancellationToken.None); await first.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PresenceState.Offline, Assert.Single(await repository.GetDevicesAsync(router.Id, CancellationToken.None)).CurrentState);

            source.Online = true;
            await Task.WhenAll(monitor.RefreshNowAsync(CancellationToken.None), monitor.RefreshNowAsync(CancellationToken.None), monitor.RefreshNowAsync(CancellationToken.None));
            Assert.Equal(PresenceState.Online, Assert.Single(await repository.GetDevicesAsync(router.Id, CancellationToken.None)).CurrentState);
            Assert.Equal(1, source.MaximumConcurrency);
            await monitor.StopAsync("test", CancellationToken.None);
        }
        finally { DeleteTree(root); }
    }

    private static Task NextSnapshotAsync(PresenceMonitor monitor)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null; handler = (_, _) => { monitor.SnapshotApplied -= handler; completion.TrySetResult(); };
        monitor.SnapshotApplied += handler; return completion.Task;
    }

    private static string TemporaryDirectory() { var path = Path.Combine(Path.GetTempPath(), "CloudLight-XiaoMi-Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void DeleteTree(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
    private static Router RouterAt(DateTimeOffset now) => new(0, "did-refresh", "xiaomi.router.rd03", "partner-refresh", "Router", null, null, now, now);
    private static NetworkDevice DeviceAt(long routerId, DateTimeOffset now) => new(0, routerId, "AA:BB:CC:DD:EE:FF", "Phone", "Phone", null, null, "192.168.1.2", "5 GHz", -55, PresenceState.Offline, now, now, null);

    private sealed class ControlledSource : IXiaomiPresenceSource
    {
        private int _concurrency; private int _maximumConcurrency;
        public bool Online { get; set; }
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool HasStoredLogin => true;
        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<XiaomiRouterDevice>>([]);
        public async Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            InterlockedExtensions.Max(ref _maximumConcurrency, concurrency);
            try { await Task.Delay(75, cancellationToken); return [new ObservedNetworkDevice("AA:BB:CC:DD:EE:FF", "Phone", "Phone", "192.168.1.2", Online, null, "5 GHz", -55)]; }
            finally { Interlocked.Decrement(ref _concurrency); }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
