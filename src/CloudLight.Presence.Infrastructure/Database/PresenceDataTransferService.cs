using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace CloudLight.Presence.Infrastructure.Database;

public sealed record ImportResult(int AddedDevices, int UpdatedDevices, int AddedEvents, int SkippedDuplicates);

public sealed class PresenceDataTransferService(AppPaths paths)
{
    private const string Format = "CloudLight.Presence.Export";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();

    public async Task ExportAsync(string targetPath, CancellationToken cancellationToken)
    {
        var model = new ExportDocument(
            new ExportManifest(Format, 1, DateTimeOffset.UtcNow, Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "development", false),
            [], [], [], [], []);
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ReadAsync(connection, "SELECT MiotDid,MiotModel,PartnerId,Name,HomeId,RoomId,CreatedAt,LastSeenAt FROM Router", reader =>
            model.Routers.Add(new ExportRouter(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4), Text(reader, 5), Time(reader, 6), Time(reader, 7))), cancellationToken);
        await ReadAsync(connection, "SELECT r.MiotDid,d.MacAddress,d.OriginalName,d.OriginName,d.CustomName,d.Note,d.LastIp,d.ConnectionType,d.Signal,d.CurrentState,d.FirstSeenAt,d.LastSeenAt,d.LastStateChangedAt FROM NetworkDevice d JOIN Router r ON r.Id=d.RouterId", reader =>
            model.Devices.Add(new ExportDevice(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.GetInt32(9), Time(reader, 10), Time(reader, 11), NullableTime(reader, 12))), cancellationToken);
        await ReadAsync(connection, "SELECT r.MiotDid,d.MacAddress,e.EventType,e.ObservedAt,e.Source FROM PresenceEvent e JOIN NetworkDevice d ON d.Id=e.DeviceId JOIN Router r ON r.Id=d.RouterId", reader =>
            model.Events.Add(new ExportEvent(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), Time(reader, 3), reader.GetInt32(4))), cancellationToken);
        await ReadAsync(connection, "SELECT r.MiotDid,d.MacAddress,s.StartedAt,s.EndedAt,s.StartKnown,s.EndKnown FROM PresenceSession s JOIN NetworkDevice d ON d.Id=s.DeviceId JOIN Router r ON r.Id=d.RouterId", reader =>
            model.Sessions.Add(new ExportSession(reader.GetString(0), reader.GetString(1), Time(reader, 2), NullableTime(reader, 3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0)), cancellationToken);
        await ReadAsync(connection, "SELECT StartedAt,EndedAt,Reason FROM MonitoringGap", reader =>
            model.MonitoringGaps.Add(new ExportGap(Time(reader, 0), NullableTime(reader, 1), reader.GetString(2))), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var temporary = targetPath + ".new";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, model, JsonOptions, cancellationToken);
        File.Move(temporary, targetPath, true);
    }

    public async Task<ImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ExportDocument document;
        await using (var stream = File.OpenRead(sourcePath))
            document = await JsonSerializer.DeserializeAsync<ExportDocument>(stream, JsonOptions, cancellationToken) ?? throw new InvalidDataException("备份文件为空。 ");
        Validate(document);

        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var routers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var router in document.Routers)
        {
            var id = await ScalarLongAsync(connection, "SELECT Id FROM Router WHERE MiotDid=$did OR PartnerId=$partner LIMIT 1", [("$did", router.MiotDid), ("$partner", router.PartnerId)], cancellationToken);
            if (id == 0) id = await InsertIdAsync(connection, "INSERT INTO Router(MiotDid,MiotModel,PartnerId,Name,HomeId,RoomId,CreatedAt,LastSeenAt) VALUES($did,$model,$partner,$name,$home,$room,$created,$seen)", [("$did", router.MiotDid), ("$model", router.MiotModel), ("$partner", router.PartnerId), ("$name", router.Name), ("$home", router.HomeId), ("$room", router.RoomId), ("$created", Iso(router.CreatedAt)), ("$seen", Iso(router.LastSeenAt))], cancellationToken);
            routers[router.MiotDid] = id;
        }

        var devices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); var addedDevices = 0; var updatedDevices = 0;
        foreach (var device in document.Devices)
        {
            var routerId = routers[device.RouterMiotDid]; var mac = PresenceStateMachine.NormalizeMac(device.MacAddress);
            var id = await ScalarLongAsync(connection, "SELECT Id FROM NetworkDevice WHERE RouterId=$router AND MacAddress=$mac", [("$router", routerId), ("$mac", mac)], cancellationToken);
            if (id == 0)
            {
                id = await InsertIdAsync(connection, "INSERT INTO NetworkDevice(RouterId,MacAddress,OriginalName,OriginName,CustomName,Note,LastIp,ConnectionType,Signal,CurrentState,FirstSeenAt,LastSeenAt,LastStateChangedAt) VALUES($router,$mac,$original,$origin,$custom,$note,$ip,$connection,$signal,$state,$first,$last,$changed)", DeviceParameters(device, routerId, mac), cancellationToken); addedDevices++;
            }
            else
            {
                await ExecuteAsync(connection, "UPDATE NetworkDevice SET CustomName=CASE WHEN CustomName IS NULL OR trim(CustomName)='' THEN $custom ELSE CustomName END, Note=CASE WHEN Note IS NULL OR trim(Note)='' THEN $note ELSE Note END WHERE Id=$id", [("$custom", device.CustomName), ("$note", device.Note), ("$id", id)], cancellationToken); updatedDevices++;
            }
            devices[Key(device.RouterMiotDid, mac)] = id;
        }

        var addedEvents = 0; var skipped = 0;
        foreach (var value in document.Events)
        {
            var id = devices[Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))];
            addedEvents += await InsertOrIgnoreAsync(connection, "INSERT OR IGNORE INTO PresenceEvent(DeviceId,EventType,ObservedAt,Source) VALUES($device,$type,$at,$source)", [("$device", id), ("$type", value.EventType), ("$at", Iso(value.ObservedAt)), ("$source", value.Source)], cancellationToken); if (addedEvents == 0) { }
        }
        foreach (var value in document.Sessions)
        {
            var id = devices[Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))];
            var changed = await InsertOrIgnoreAsync(connection, "INSERT OR IGNORE INTO PresenceSession(DeviceId,StartedAt,EndedAt,StartKnown,EndKnown) VALUES($device,$start,$end,$sk,$ek)", [("$device", id), ("$start", Iso(value.StartedAt)), ("$end", value.EndedAt is null ? null : Iso(value.EndedAt.Value)), ("$sk", value.StartKnown ? 1 : 0), ("$ek", value.EndKnown ? 1 : 0)], cancellationToken); skipped += changed == 0 ? 1 : 0;
        }
        foreach (var value in document.MonitoringGaps)
        {
            var changed = await InsertOrIgnoreAsync(connection, "INSERT OR IGNORE INTO MonitoringGap(StartedAt,EndedAt,Reason) VALUES($start,$end,$reason)", [("$start", Iso(value.StartedAt)), ("$end", value.EndedAt is null ? null : Iso(value.EndedAt.Value)), ("$reason", value.Reason)], cancellationToken); skipped += changed == 0 ? 1 : 0;
        }
        var totalEventDuplicates = document.Events.Count - addedEvents; skipped += totalEventDuplicates;
        await transaction.CommitAsync(cancellationToken);
        return new ImportResult(addedDevices, updatedDevices, addedEvents, skipped);
    }

    private static void Validate(ExportDocument document)
    {
        if (document.Manifest.Format != Format || document.Manifest.Version != 1 || document.Manifest.ContainsAuthentication) throw new InvalidDataException("不支持或不安全的 CloudLight Presence 备份格式。 ");
        if (document.Routers.Any(value => string.IsNullOrWhiteSpace(value.MiotDid) || string.IsNullOrWhiteSpace(value.PartnerId))) throw new InvalidDataException("路由器数据不完整。 ");
        var routerIds = document.Routers.Select(value => value.MiotDid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.Devices.Any(value => !routerIds.Contains(value.RouterMiotDid))) throw new InvalidDataException("设备引用了不存在的路由器。 ");
        var deviceKeys = document.Devices.Select(value => Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.Events.Any(value => !deviceKeys.Contains(Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress)))) || document.Sessions.Any(value => !deviceKeys.Contains(Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))))) throw new InvalidDataException("历史记录引用了不存在的设备。 ");
    }

    private static List<(string, object?)> DeviceParameters(ExportDevice value, long routerId, string mac) => [("$router", routerId), ("$mac", mac), ("$original", value.OriginalName), ("$origin", value.OriginName), ("$custom", value.CustomName), ("$note", value.Note), ("$ip", value.LastIp), ("$connection", value.ConnectionType), ("$signal", value.Signal), ("$state", value.CurrentState), ("$first", Iso(value.FirstSeenAt)), ("$last", Iso(value.LastSeenAt)), ("$changed", value.LastStateChangedAt is null ? null : Iso(value.LastStateChangedAt.Value))];
    private static async Task ReadAsync(SqliteConnection connection, string sql, Action<SqliteDataReader> read, CancellationToken token) { await using var command = connection.CreateCommand(); command.CommandText = sql; await using var reader = await command.ExecuteReaderAsync(token); while (await reader.ReadAsync(token)) read(reader); }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, List<(string, object?)> values, CancellationToken token) { await using var command = Command(connection, sql, values); var value = await command.ExecuteScalarAsync(token); return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
    private static async Task<long> InsertIdAsync(SqliteConnection connection, string sql, List<(string, object?)> values, CancellationToken token) { await ExecuteAsync(connection, sql, values, token); return await ScalarLongAsync(connection, "SELECT last_insert_rowid()", [], token); }
    private static async Task<int> InsertOrIgnoreAsync(SqliteConnection connection, string sql, List<(string, object?)> values, CancellationToken token) => await ExecuteAsync(connection, sql, values, token);
    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, List<(string, object?)> values, CancellationToken token) { await using var command = Command(connection, sql, values); return await command.ExecuteNonQueryAsync(token); }
    private static SqliteCommand Command(SqliteConnection connection, string sql, List<(string, object?)> values) { var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value); return command; }
    private static string Key(string router, string mac) => $"{router}|{mac}";
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset Time(SqliteDataReader reader, int ordinal) => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    private static DateTimeOffset? NullableTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Time(reader, ordinal);
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public sealed record ExportDocument(ExportManifest Manifest, List<ExportRouter> Routers, List<ExportDevice> Devices, List<ExportEvent> Events, List<ExportSession> Sessions, List<ExportGap> MonitoringGaps);
    public sealed record ExportManifest(string Format, int Version, DateTimeOffset CreatedAtUtc, string AppVersion, bool ContainsAuthentication);
    public sealed record ExportRouter(string MiotDid, string MiotModel, string PartnerId, string Name, string? HomeId, string? RoomId, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);
    public sealed record ExportDevice(string RouterMiotDid, string MacAddress, string? OriginalName, string? OriginName, string? CustomName, string? Note, string? LastIp, string? ConnectionType, int? Signal, int CurrentState, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? LastStateChangedAt);
    public sealed record ExportEvent(string RouterMiotDid, string MacAddress, int EventType, DateTimeOffset ObservedAt, int Source);
    public sealed record ExportSession(string RouterMiotDid, string MacAddress, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool StartKnown, bool EndKnown);
    public sealed record ExportGap(DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Reason);
}
