using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.SecureStorage;
using CloudLight.Presence.Infrastructure.Settings;
using CloudLight.Presence.Xiaomi;

if (args.Contains("--verify-transfer", StringComparer.OrdinalIgnoreCase))
{
    await VerifyTransferAsync();
    return;
}

var paths = new AppPaths();
var repository = new SqlitePresenceRepository(paths);
var source = new XiaomiPresenceSource(new DpapiSessionStore(paths), paths.MigatePython);
await repository.InitializeAsync(CancellationToken.None);
await source.RestoreAsync(CancellationToken.None);
var discovered = await source.DiscoverRoutersAsync(CancellationToken.None);
Console.WriteLine($"routers={discovered.Count}");
foreach (var item in discovered) Console.WriteLine($"router={item.Name};model={item.MiotModel};has_partner_id={!string.IsNullOrWhiteSpace(item.PartnerId)}");
var selected = discovered.Count == 1 ? discovered[0] : discovered.SingleOrDefault(item => item.MiotModel == "xiaomi.router.rd03")
    ?? throw new InvalidOperationException("Smoke check requires an unambiguous RD03 router.");
var now = DateTimeOffset.UtcNow;
var router = await repository.UpsertRouterAsync(new Router(0, selected.MiotDid, selected.MiotModel, selected.PartnerId, selected.Name, selected.HomeId, selected.RoomId, now, now), CancellationToken.None);
var observations = await source.GetDevicesAsync(selected.PartnerId, CancellationToken.None);
await new PresenceStateMachine(repository).ApplySnapshotAsync(router.Id, observations, now, CancellationToken.None);
var stored = await repository.GetDevicesAsync(router.Id, CancellationToken.None);
Console.WriteLine($"clients={observations.Count};online={observations.Count(value => value.Online)};offline={observations.Count(value => !value.Online)};persisted={stored.Count}");
var eventsBefore = 0;
var onlineEvents = 0; var offlineEvents = 0;
foreach (var device in stored)
{
    var events = await repository.GetEventsAsync(device.Id, CancellationToken.None); eventsBefore += events.Count;
    onlineEvents += events.Count(value => value.EventType == PresenceEventType.Online);
    offlineEvents += events.Count(value => value.EventType == PresenceEventType.Offline);
}
Console.WriteLine($"custom_names={stored.Count(value => !string.IsNullOrWhiteSpace(value.CustomName))};notes={stored.Count(value => !string.IsNullOrWhiteSpace(value.Note))};events={eventsBefore};online_events={onlineEvents};offline_events={offlineEvents}");
if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"database={paths.Database}");
    return;
}
var waitStarted = DateTimeOffset.UtcNow;
await Task.Delay(TimeSpan.FromSeconds(10));
var second = await source.GetDevicesAsync(selected.PartnerId, CancellationToken.None);
await new PresenceStateMachine(repository).ApplySnapshotAsync(router.Id, second, DateTimeOffset.UtcNow, CancellationToken.None);
var eventsAfter = 0;
foreach (var device in await repository.GetDevicesAsync(router.Id, CancellationToken.None)) eventsAfter += (await repository.GetEventsAsync(device.Id, CancellationToken.None)).Count;
Console.WriteLine($"second_poll_seconds={(DateTimeOffset.UtcNow - waitStarted).TotalSeconds:F1};event_delta={eventsAfter - eventsBefore}");
Console.WriteLine($"database={paths.Database}");

static async Task VerifyTransferAsync()
{
    var sourcePaths = new AppPaths(); var source = new SqlitePresenceRepository(sourcePaths); await source.InitializeAsync(CancellationToken.None);
    var validationRoot = Path.Combine(Path.GetTempPath(), "CloudLight-Presence-ReleaseValidation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(validationRoot);
    var backup = Path.Combine(validationRoot, "backup.clpresence"); var targetPaths = new AppPaths(Path.Combine(validationRoot, "database"));
    try
    {
        await new PresenceDataTransferService(sourcePaths).ExportAsync(backup, CancellationToken.None);
        var contents = await File.ReadAllTextAsync(backup); var forbidden = new[] { "passToken", "serviceToken", "ssecurity", "auth.dat", "Cookie" };
        foreach (var value in forbidden) if (contents.Contains(value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Export contains forbidden authentication marker: {value}");

        var target = new SqlitePresenceRepository(targetPaths); await target.InitializeAsync(CancellationToken.None); var transfer = new PresenceDataTransferService(targetPaths);
        var first = await transfer.ImportAsync(backup, CancellationToken.None); var firstCounts = await CountHistoryAsync(target);
        var second = await transfer.ImportAsync(backup, CancellationToken.None); var secondCounts = await CountHistoryAsync(target);
        if (firstCounts != secondCounts) throw new InvalidDataException("Repeated import changed Event or Session totals.");
        var routers = await target.GetRoutersAsync(CancellationToken.None); var devices = new List<NetworkDevice>(); foreach (var router in routers) devices.AddRange(await target.GetDevicesAsync(router.Id, CancellationToken.None));
        Console.WriteLine($"backup_created={File.Exists(backup)};bytes={new FileInfo(backup).Length};authentication_markers=0");
        Console.WriteLine($"routers={routers.Count};devices={devices.Count};custom_names={devices.Count(value => !string.IsNullOrWhiteSpace(value.CustomName))};notes={devices.Count(value => !string.IsNullOrWhiteSpace(value.Note))}");
        Console.WriteLine($"first_added_events={first.AddedEvents};events={firstCounts.Events};sessions={firstCounts.Sessions}");
        Console.WriteLine($"second_added_events={second.AddedEvents};second_events={secondCounts.Events};second_sessions={secondCounts.Sessions};skipped_duplicates={second.SkippedDuplicates}");
    }
    finally { if (Directory.Exists(validationRoot)) Directory.Delete(validationRoot, true); }
}

static async Task<(int Events, int Sessions)> CountHistoryAsync(SqlitePresenceRepository repository)
{
    var events = 0; var sessions = 0;
    foreach (var router in await repository.GetRoutersAsync(CancellationToken.None))
    foreach (var device in await repository.GetDevicesAsync(router.Id, CancellationToken.None))
    {
        events += (await repository.GetEventsAsync(device.Id, CancellationToken.None)).Count;
        sessions += (await repository.GetSessionsAsync(device.Id, CancellationToken.None)).Count;
    }
    return (events, sessions);
}
