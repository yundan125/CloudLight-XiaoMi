using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace CloudLight.Presence.Infrastructure.Database;

public sealed class SqlitePresenceRepository(AppPaths paths) : IPresenceRepository
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS Router (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, MiotDid TEXT NOT NULL UNIQUE,
              MiotModel TEXT NOT NULL, PartnerId TEXT NOT NULL, Name TEXT NOT NULL,
              HomeId TEXT NULL, RoomId TEXT NULL, CreatedAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS NetworkDevice (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, RouterId INTEGER NOT NULL,
              MacAddress TEXT NOT NULL, OriginalName TEXT NULL, OriginName TEXT NULL,
              CustomName TEXT NULL, Note TEXT NULL, LastIp TEXT NULL,
              ConnectionType TEXT NULL, Signal INTEGER NULL, CurrentState INTEGER NOT NULL,
              FirstSeenAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL, LastStateChangedAt TEXT NULL,
              UNIQUE(RouterId, MacAddress), FOREIGN KEY(RouterId) REFERENCES Router(Id));
            CREATE TABLE IF NOT EXISTS PresenceEvent (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId INTEGER NOT NULL,
              EventType INTEGER NOT NULL, ObservedAt TEXT NOT NULL, Source INTEGER NOT NULL,
              FOREIGN KEY(DeviceId) REFERENCES NetworkDevice(Id));
            CREATE INDEX IF NOT EXISTS IX_PresenceEvent_Device_Observed ON PresenceEvent(DeviceId, ObservedAt DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PresenceEvent_Stable ON PresenceEvent(DeviceId, EventType, ObservedAt, Source);
            CREATE TABLE IF NOT EXISTS PresenceSession (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, DeviceId INTEGER NOT NULL,
              StartedAt TEXT NOT NULL, EndedAt TEXT NULL, StartKnown INTEGER NOT NULL,
              EndKnown INTEGER NOT NULL, FOREIGN KEY(DeviceId) REFERENCES NetworkDevice(Id));
            CREATE TABLE IF NOT EXISTS MonitoringGap (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StartedAt TEXT NOT NULL,
              EndedAt TEXT NULL, Reason TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PresenceSession_Stable ON PresenceSession(DeviceId, StartedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_MonitoringGap_Stable ON MonitoringGap(StartedAt, Reason);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Router> UpsertRouterAsync(Router router, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Router(MiotDid,MiotModel,PartnerId,Name,HomeId,RoomId,CreatedAt,LastSeenAt)
            VALUES($did,$model,$partner,$name,$home,$room,$created,$seen)
            ON CONFLICT(MiotDid) DO UPDATE SET MiotModel=$model,PartnerId=$partner,Name=$name,
              HomeId=$home,RoomId=$room,LastSeenAt=$seen RETURNING *;
            """;
        Add(command, "$did", router.MiotDid); Add(command, "$model", router.MiotModel);
        Add(command, "$partner", router.PartnerId); Add(command, "$name", router.Name);
        Add(command, "$home", router.HomeId); Add(command, "$room", router.RoomId);
        Add(command, "$created", Time(router.CreatedAt)); Add(command, "$seen", Time(router.LastSeenAt));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadRouter(reader);
    }

    public async Task<IReadOnlyList<Router>> GetRoutersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM Router ORDER BY Name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Router>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadRouter(reader));
        return result;
    }

    public async Task<NetworkDevice?> FindDeviceAsync(long routerId, string macAddress, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NetworkDevice WHERE RouterId=$router AND MacAddress=$mac";
        Add(command, "$router", routerId); Add(command, "$mac", macAddress);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
    }

    public async Task<NetworkDevice?> GetDeviceAsync(long deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NetworkDevice WHERE Id=$id"; Add(command, "$id", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
    }

    public async Task<NetworkDevice> InsertDeviceAsync(NetworkDevice device, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO NetworkDevice(RouterId,MacAddress,OriginalName,OriginName,CustomName,Note,LastIp,ConnectionType,Signal,CurrentState,FirstSeenAt,LastSeenAt,LastStateChangedAt)
            VALUES($router,$mac,$original,$origin,$custom,$note,$ip,$connection,$signal,$state,$first,$last,$changed) RETURNING *;
            """;
        AddDeviceParameters(command, device);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken);
        return ReadDevice(reader);
    }

    public async Task UpdateDeviceAsync(NetworkDevice device, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE NetworkDevice SET OriginalName=$original,OriginName=$origin,LastIp=$ip,
              ConnectionType=$connection,Signal=$signal,CurrentState=$state,LastSeenAt=$last,
              LastStateChangedAt=$changed WHERE Id=$id;
            """;
        AddDeviceParameters(command, device); Add(command, "$id", device.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NetworkDevice>> GetDevicesAsync(long routerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NetworkDevice WHERE RouterId=$router ORDER BY CurrentState DESC, COALESCE(CustomName,OriginalName,OriginName,MacAddress)";
        Add(command, "$router", routerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NetworkDevice>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDevice(reader));
        return result;
    }

    public async Task UpdateDeviceMetadataAsync(long deviceId, string? customName, string? note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkDevice SET CustomName=$name,Note=$note WHERE Id=$id";
        Add(command, "$name", NullIfWhiteSpace(customName)); Add(command, "$note", NullIfWhiteSpace(note)); Add(command, "$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEventAsync(PresenceEvent value, CancellationToken cancellationToken)
    {
        await ExecuteAsync("INSERT INTO PresenceEvent(DeviceId,EventType,ObservedAt,Source) VALUES($device,$type,$at,$source)",
            [("$device", value.DeviceId), ("$type", (int)value.EventType), ("$at", Time(value.ObservedAt)), ("$source", (int)value.Source)], cancellationToken);
    }

    public async Task<IReadOnlyList<PresenceEvent>> GetEventsAsync(long deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PresenceEvent WHERE DeviceId=$device ORDER BY ObservedAt DESC"; Add(command, "$device", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<PresenceEvent>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new PresenceEvent(reader.GetInt64(0), reader.GetInt64(1), (PresenceEventType)reader.GetInt32(2), ParseTime(reader.GetString(3)), (PresenceSource)reader.GetInt32(4)));
        return result;
    }

    public async Task AddSessionAsync(PresenceSession value, CancellationToken cancellationToken) =>
        await ExecuteAsync("INSERT INTO PresenceSession(DeviceId,StartedAt,EndedAt,StartKnown,EndKnown) VALUES($device,$start,$end,$sk,$ek)",
            [("$device", value.DeviceId), ("$start", Time(value.StartedAt)), ("$end", value.EndedAt is null ? null : Time(value.EndedAt.Value)), ("$sk", value.StartKnown ? 1 : 0), ("$ek", value.EndKnown ? 1 : 0)], cancellationToken);

    public async Task CloseOpenSessionAsync(long deviceId, DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE PresenceSession SET EndedAt=$end,EndKnown=1 WHERE Id=(SELECT Id FROM PresenceSession WHERE DeviceId=$device AND EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1)",
            [("$device", deviceId), ("$end", Time(endedAt))], cancellationToken);

    public async Task<IReadOnlyList<PresenceSession>> GetSessionsAsync(long deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PresenceSession WHERE DeviceId=$device ORDER BY StartedAt DESC"; Add(command, "$device", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<PresenceSession>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new PresenceSession(reader.GetInt64(0), reader.GetInt64(1), ParseTime(reader.GetString(2)), reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0));
        return result;
    }

    public async Task<IReadOnlyList<MonitoringGap>> GetMonitoringGapsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM MonitoringGap WHERE StartedAt < $to AND (EndedAt IS NULL OR EndedAt > $from) ORDER BY StartedAt";
        Add(command, "$from", Time(from)); Add(command, "$to", Time(to));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<MonitoringGap>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new MonitoringGap(reader.GetInt64(0), ParseTime(reader.GetString(1)), reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)), reader.GetString(3)));
        return result;
    }

    public async Task<long> StartMonitoringGapAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO MonitoringGap(StartedAt,Reason) VALUES($at,$reason); SELECT last_insert_rowid();";
        Add(command, "$at", Time(startedAt)); Add(command, "$reason", reason);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task EndMonitoringGapAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE MonitoringGap SET EndedAt=$end WHERE Id=$id", [("$id", gapId), ("$end", Time(endedAt))], cancellationToken);

    public async Task CloseOpenMonitoringGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE MonitoringGap SET EndedAt=$end WHERE EndedAt IS NULL", [("$end", Time(endedAt))], cancellationToken);

    private async Task ExecuteAsync(string sql, (string Name, object? Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON;"; await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static void AddDeviceParameters(SqliteCommand command, NetworkDevice value)
    {
        Add(command, "$router", value.RouterId); Add(command, "$mac", value.MacAddress); Add(command, "$original", value.OriginalName);
        Add(command, "$origin", value.OriginName); Add(command, "$custom", value.CustomName); Add(command, "$note", value.Note);
        Add(command, "$ip", value.LastIp); Add(command, "$connection", value.ConnectionType); Add(command, "$signal", value.Signal);
        Add(command, "$state", (int)value.CurrentState); Add(command, "$first", Time(value.FirstSeenAt)); Add(command, "$last", Time(value.LastSeenAt));
        Add(command, "$changed", value.LastStateChangedAt is null ? null : Time(value.LastStateChangedAt.Value));
    }

    private static Router ReadRouter(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetString(reader.GetOrdinal("MiotDid")), reader.GetString(reader.GetOrdinal("MiotModel")), reader.GetString(reader.GetOrdinal("PartnerId")), reader.GetString(reader.GetOrdinal("Name")), Text(reader, "HomeId"), Text(reader, "RoomId"), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("LastSeenAt"))));
    private static NetworkDevice ReadDevice(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetInt64(reader.GetOrdinal("RouterId")), reader.GetString(reader.GetOrdinal("MacAddress")), Text(reader, "OriginalName"), Text(reader, "OriginName"), Text(reader, "CustomName"), Text(reader, "Note"), Text(reader, "LastIp"), Text(reader, "ConnectionType"), Integer(reader, "Signal"), (PresenceState)reader.GetInt32(reader.GetOrdinal("CurrentState")), ParseTime(reader.GetString(reader.GetOrdinal("FirstSeenAt"))), ParseTime(reader.GetString(reader.GetOrdinal("LastSeenAt"))), Text(reader, "LastStateChangedAt") is { } changed ? ParseTime(changed) : null);
    private static string? Text(SqliteDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetString(i); }
    private static int? Integer(SqliteDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetInt32(i); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
