using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace CloudLight.Presence.Infrastructure.Database;

public sealed class SqlitePresenceRepository(IAppDataPaths paths) : IPresenceRepository
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
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
               LastKnownHistoricalState INTEGER NOT NULL DEFAULT 0,
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
            CREATE TABLE IF NOT EXISTS ApplicationRun (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StartedAt TEXT NOT NULL,
              EndedAt TEXT NULL, LastSuccessfulCloudUpdateAt TEXT NULL);
            CREATE TABLE IF NOT EXISTS PresenceSubject (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, ExportId TEXT NOT NULL UNIQUE,
              DisplayName TEXT NOT NULL, Note TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SubjectDeviceMembership (
              SubjectId INTEGER NOT NULL, NetworkDeviceId INTEGER NOT NULL UNIQUE, CreatedAt TEXT NOT NULL,
              PRIMARY KEY(SubjectId, NetworkDeviceId),
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE,
              FOREIGN KEY(NetworkDeviceId) REFERENCES NetworkDevice(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_SubjectDeviceMembership_Subject ON SubjectDeviceMembership(SubjectId);
            CREATE TABLE IF NOT EXISTS NotificationRule (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, SubjectId INTEGER NOT NULL,
              Enabled INTEGER NOT NULL, RuleCondition INTEGER NOT NULL,
              ThresholdSeconds INTEGER NOT NULL, Channel INTEGER NOT NULL,
              TargetType INTEGER NOT NULL, TargetId TEXT NOT NULL,
              MessageTemplate TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_NotificationRule_Subject_Enabled ON NotificationRule(SubjectId,Enabled);
            CREATE TABLE IF NOT EXISTS NotificationRuleState (
              RuleId INTEGER PRIMARY KEY, CurrentEpisodeId TEXT NULL, StateSince TEXT NULL,
              TriggeredForCurrentEpisode INTEGER NOT NULL, TriggeredAt TEXT NULL,
              PendingDelivery INTEGER NOT NULL, PendingDeliveryId INTEGER NULL,
              LastDeliveryError TEXT NULL, UpdatedAt TEXT NOT NULL,
              FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS NotificationDelivery (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, RuleId INTEGER NULL, SubjectId INTEGER NULL,
              EpisodeId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status INTEGER NOT NULL,
              DeliveredAt TEXT NULL, Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL,
              TargetId TEXT NOT NULL, Message TEXT NOT NULL, Error TEXT NULL,
              SentParts INTEGER NOT NULL DEFAULT 0, TotalParts INTEGER NOT NULL DEFAULT 0,
              LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL,
              FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE SET NULL,
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE SET NULL,
              UNIQUE(RuleId,EpisodeId));
            CREATE INDEX IF NOT EXISTS IX_NotificationDelivery_Pending ON NotificationDelivery(Status,NextAttemptAt);
            CREATE INDEX IF NOT EXISTS IX_NotificationDelivery_Created ON NotificationDelivery(CreatedAt DESC);
            CREATE TABLE IF NOT EXISTS ConnectionAlertState (
              Id INTEGER PRIMARY KEY CHECK(Id=1), FailureEpisodeId TEXT NULL,
              FailureStartedAt TEXT NULL, LastSuccessfulCloudUpdateAt TEXT NULL,
              FailureAlertSent INTEGER NOT NULL, RecoveryAlertSent INTEGER NOT NULL,
              UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SystemNotificationDelivery (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind INTEGER NOT NULL, EpisodeId TEXT NOT NULL,
              CreatedAt TEXT NOT NULL, Status INTEGER NOT NULL, DeliveredAt TEXT NULL,
              Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL, TargetId TEXT NOT NULL,
              Message TEXT NOT NULL, Error TEXT NULL, SentParts INTEGER NOT NULL DEFAULT 0,
              TotalParts INTEGER NOT NULL DEFAULT 0, LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL,
              UNIQUE(Kind,EpisodeId));
            CREATE INDEX IF NOT EXISTS IX_SystemNotificationDelivery_Pending ON SystemNotificationDelivery(Status,NextAttemptAt);
            CREATE INDEX IF NOT EXISTS IX_SystemNotificationDelivery_Created ON SystemNotificationDelivery(CreatedAt DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureNetworkDeviceHistorySchemaAsync(connection, cancellationToken);
        await MigrateNotificationDeliverySchemaAsync(connection, cancellationToken);
        await EnsureEveryDeviceHasSubjectAsync(cancellationToken);
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

    public async Task<Router?> GetRouterAsync(long routerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM Router WHERE Id=$id"; Add(command, "$id", routerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRouter(reader) : null;
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO NetworkDevice(RouterId,MacAddress,OriginalName,OriginName,CustomName,Note,LastIp,ConnectionType,Signal,CurrentState,FirstSeenAt,LastSeenAt,LastStateChangedAt,LastKnownHistoricalState)
            VALUES($router,$mac,$original,$origin,$custom,$note,$ip,$connection,$signal,$state,$first,$last,$changed,$historical) RETURNING *;
            """;
        AddDeviceParameters(command, device);
        NetworkDevice created;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            created = ReadDevice(reader);
        }
        await CreateStandaloneSubjectAsync(connection, (SqliteTransaction)transaction, created, device.FirstSeenAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task UpdateDeviceAsync(NetworkDevice device, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE NetworkDevice SET OriginalName=$original,OriginName=$origin,LastIp=$ip,
              ConnectionType=$connection,Signal=$signal,CurrentState=$state,LastSeenAt=$last,
              LastStateChangedAt=$changed,LastKnownHistoricalState=$historical WHERE Id=$id;
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

    public async Task ResetCurrentObservedStateAsync(long routerId, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE NetworkDevice SET CurrentState=$state WHERE RouterId=$router", [
            ("$state", (int)PresenceState.Unknown), ("$router", routerId)], cancellationToken);

    public async Task UpdateDeviceMetadataAsync(long deviceId, string? customName, string? note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NetworkDevice SET CustomName=$name,Note=$note WHERE Id=$id";
        Add(command, "$name", NullIfWhiteSpace(customName)); Add(command, "$note", NullIfWhiteSpace(note)); Add(command, "$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PresenceSubject> CreateSubjectAsync(string displayName, string? note, Guid exportId, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("主体名称不能为空。", nameof(displayName));
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO PresenceSubject(ExportId,DisplayName,Note,CreatedAt,UpdatedAt) VALUES($export,$name,$note,$created,$updated) RETURNING *";
        Add(command, "$export", exportId.ToString("D")); Add(command, "$name", displayName.Trim()); Add(command, "$note", NullIfWhiteSpace(note)); Add(command, "$created", Time(createdAt)); Add(command, "$updated", Time(createdAt));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken); return ReadSubject(reader);
    }

    public async Task UpdateSubjectAsync(long subjectId, string displayName, string? note, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("主体名称不能为空。", nameof(displayName));
        await ExecuteAsync("UPDATE PresenceSubject SET DisplayName=$name,Note=$note,UpdatedAt=$updated WHERE Id=$id",
            [("$id", subjectId), ("$name", displayName.Trim()), ("$note", NullIfWhiteSpace(note)), ("$updated", Time(updatedAt))], cancellationToken);
    }

    public async Task DeleteSubjectAsync(long subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM PresenceSubject WHERE Id=$id";
            Add(command, "$id", subjectId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await EnsureEveryDeviceHasSubjectAsync(connection, (SqliteTransaction)transaction, DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PresenceSubject?> GetSubjectAsync(long subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PresenceSubject WHERE Id=$id"; Add(command, "$id", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadSubject(reader) : null;
    }

    public async Task<IReadOnlyList<PresenceSubject>> GetSubjectsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM PresenceSubject ORDER BY DisplayName";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<PresenceSubject>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSubject(reader)); return result;
    }

    public async Task<IReadOnlyList<NetworkDevice>> GetSubjectDevicesAsync(long subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT d.* FROM NetworkDevice d JOIN SubjectDeviceMembership m ON m.NetworkDeviceId=d.Id WHERE m.SubjectId=$id ORDER BY d.CurrentState DESC,d.Signal DESC,d.LastSeenAt DESC"; Add(command, "$id", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<NetworkDevice>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDevice(reader)); return result;
    }

    public async Task<IReadOnlyDictionary<long, long>> GetDeviceSubjectMapAsync(long routerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.NetworkDeviceId,m.SubjectId FROM SubjectDeviceMembership m JOIN NetworkDevice d ON d.Id=m.NetworkDeviceId WHERE d.RouterId=$router"; Add(command, "$router", routerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new Dictionary<long, long>();
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt64(0)] = reader.GetInt64(1); return result;
    }

    public async Task EnsureEveryDeviceHasSubjectAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureEveryDeviceHasSubjectAsync(connection, (SqliteTransaction)transaction, DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetSubjectDevicesAsync(long subjectId, IReadOnlyCollection<long> deviceIds, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var remove = connection.CreateCommand()) { remove.Transaction = (SqliteTransaction)transaction; remove.CommandText = "DELETE FROM SubjectDeviceMembership WHERE SubjectId=$id"; Add(remove, "$id", subjectId); await remove.ExecuteNonQueryAsync(cancellationToken); }
        foreach (var deviceId in deviceIds.Distinct())
        {
            await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO SubjectDeviceMembership(SubjectId,NetworkDeviceId,CreatedAt) VALUES($subject,$device,$created) ON CONFLICT(NetworkDeviceId) DO UPDATE SET SubjectId=$subject,CreatedAt=$created";
            Add(command, "$subject", subjectId); Add(command, "$device", deviceId); Add(command, "$created", Time(createdAt)); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var removeEmpty = connection.CreateCommand())
        {
            removeEmpty.Transaction = (SqliteTransaction)transaction;
            removeEmpty.CommandText = "DELETE FROM PresenceSubject WHERE NOT EXISTS (SELECT 1 FROM SubjectDeviceMembership m WHERE m.SubjectId=PresenceSubject.Id)";
            await removeEmpty.ExecuteNonQueryAsync(cancellationToken);
        }
        await EnsureEveryDeviceHasSubjectAsync(connection, (SqliteTransaction)transaction, createdAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureEveryDeviceHasSubjectAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var orphans = new List<NetworkDevice>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT d.* FROM NetworkDevice d LEFT JOIN SubjectDeviceMembership m ON m.NetworkDeviceId=d.Id WHERE m.NetworkDeviceId IS NULL ORDER BY d.Id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) orphans.Add(ReadDevice(reader));
        }
        foreach (var device in orphans)
            await CreateStandaloneSubjectAsync(connection, transaction, device, createdAt, cancellationToken);
    }

    private static async Task CreateStandaloneSubjectAsync(SqliteConnection connection, SqliteTransaction transaction, NetworkDevice device, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var subject = connection.CreateCommand();
        subject.Transaction = transaction;
        subject.CommandText = "INSERT INTO PresenceSubject(ExportId,DisplayName,Note,CreatedAt,UpdatedAt) VALUES($export,$name,NULL,$created,$created); SELECT last_insert_rowid();";
        Add(subject, "$export", Guid.NewGuid().ToString("D")); Add(subject, "$name", device.DisplayName); Add(subject, "$created", Time(createdAt));
        var subjectId = (long)(await subject.ExecuteScalarAsync(cancellationToken) ?? 0L);
        await using var membership = connection.CreateCommand();
        membership.Transaction = transaction;
        membership.CommandText = "INSERT INTO SubjectDeviceMembership(SubjectId,NetworkDeviceId,CreatedAt) VALUES($subject,$device,$created)";
        Add(membership, "$subject", subjectId); Add(membership, "$device", device.Id); Add(membership, "$created", Time(createdAt));
        await membership.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<long> StartApplicationRunAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        DateTimeOffset? previousStarted = null; DateTimeOffset? previousUpdate = null; long? previousId = null;
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = (SqliteTransaction)transaction;
            previous.CommandText = "SELECT Id,StartedAt,LastSuccessfulCloudUpdateAt FROM ApplicationRun WHERE EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1";
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) { previousId = reader.GetInt64(0); previousStarted = ParseTime(reader.GetString(1)); previousUpdate = reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)); }
        }
        if (previousId is not null && previousStarted is not null)
        {
            var gapStart = previousUpdate ?? await GetLatestDeviceObservationAsync(connection, (SqliteTransaction)transaction, previousStarted.Value, cancellationToken) ?? previousStarted.Value;
            if (gapStart < startedAt)
            {
                await using var gap = connection.CreateCommand(); gap.Transaction = (SqliteTransaction)transaction;
                gap.CommandText = "INSERT OR IGNORE INTO MonitoringGap(StartedAt,EndedAt,Reason) VALUES($start,$end,'UnexpectedTermination')";
                Add(gap, "$start", Time(gapStart)); Add(gap, "$end", Time(startedAt)); await gap.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var close = connection.CreateCommand(); close.Transaction = (SqliteTransaction)transaction;
            close.CommandText = "UPDATE ApplicationRun SET EndedAt=$end WHERE Id=$id"; Add(close, "$end", Time(startedAt)); Add(close, "$id", previousId.Value); await close.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var reset = connection.CreateCommand())
        {
            reset.Transaction = (SqliteTransaction)transaction;
            reset.CommandText = "UPDATE NetworkDevice SET CurrentState=$state";
            Add(reset, "$state", (int)PresenceState.Unknown);
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var insert = connection.CreateCommand(); insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = "INSERT INTO ApplicationRun(StartedAt) VALUES($start); SELECT last_insert_rowid();"; Add(insert, "$start", Time(startedAt));
        var id = (long)(await insert.ExecuteScalarAsync(cancellationToken) ?? 0L); await transaction.CommitAsync(cancellationToken); return id;
    }

    public async Task UpdateApplicationRunCloudUpdateAsync(long runId, DateTimeOffset updatedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE ApplicationRun SET LastSuccessfulCloudUpdateAt=$at WHERE Id=$id AND EndedAt IS NULL", [("$id", runId), ("$at", Time(updatedAt))], cancellationToken);

    public async Task EndApplicationRunAsync(long runId, DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE ApplicationRun SET EndedAt=$end WHERE Id=$id AND EndedAt IS NULL", [("$id", runId), ("$end", Time(endedAt))], cancellationToken);

    public async Task<IReadOnlyList<NotificationRule>> GetNotificationRulesAsync(bool enabledOnly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = enabledOnly ? "SELECT * FROM NotificationRule WHERE Enabled=1 ORDER BY UpdatedAt DESC,Id" : "SELECT * FROM NotificationRule ORDER BY UpdatedAt DESC,Id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<NotificationRule>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationRule(reader));
        return result;
    }

    public async Task<NotificationRule?> GetNotificationRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NotificationRule WHERE Id=$id"; Add(command, "$id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotificationRule(reader) : null;
    }

    public async Task<NotificationRule> CreateNotificationRuleAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRule(rule);
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO NotificationRule(SubjectId,Enabled,RuleCondition,ThresholdSeconds,Channel,TargetType,TargetId,MessageTemplate,CreatedAt,UpdatedAt) VALUES($subject,$enabled,$condition,$threshold,$channel,$targetType,$target,$template,$created,$updated) RETURNING *";
        AddRuleParameters(command, normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken); return ReadNotificationRule(reader);
    }

    public async Task UpdateNotificationRuleAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        if (rule.Id <= 0) throw new ArgumentException("通知规则编号无效。", nameof(rule));
        var normalized = NormalizeRule(rule);
        await ExecuteAsync("UPDATE NotificationRule SET SubjectId=$subject,Enabled=$enabled,RuleCondition=$condition,ThresholdSeconds=$threshold,Channel=$channel,TargetType=$targetType,TargetId=$target,MessageTemplate=$template,UpdatedAt=$updated WHERE Id=$id",
            [("$subject", normalized.SubjectId), ("$enabled", normalized.Enabled ? 1 : 0), ("$condition", (int)normalized.Condition), ("$threshold", normalized.ThresholdSeconds), ("$channel", (int)normalized.Channel), ("$targetType", (int)normalized.TargetType), ("$target", normalized.TargetId), ("$template", normalized.MessageTemplate), ("$updated", Time(normalized.UpdatedAt)), ("$id", normalized.Id)], cancellationToken);
    }

    public Task DeleteNotificationRuleAsync(long ruleId, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM NotificationRule WHERE Id=$id", [("$id", ruleId)], cancellationToken);

    public async Task<NotificationRuleState?> GetNotificationRuleStateAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationRuleState WHERE RuleId=$id"; Add(command, "$id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadNotificationRuleState(reader) : null;
    }

    public async Task UpsertNotificationRuleStateAsync(NotificationRuleState state, CancellationToken cancellationToken)
    {
        await ExecuteAsync("INSERT INTO NotificationRuleState(RuleId,CurrentEpisodeId,StateSince,TriggeredForCurrentEpisode,TriggeredAt,PendingDelivery,PendingDeliveryId,LastDeliveryError,UpdatedAt) VALUES($rule,$episode,$since,$triggered,$triggeredAt,$pending,$delivery,$error,$updated) ON CONFLICT(RuleId) DO UPDATE SET CurrentEpisodeId=$episode,StateSince=$since,TriggeredForCurrentEpisode=$triggered,TriggeredAt=$triggeredAt,PendingDelivery=$pending,PendingDeliveryId=$delivery,LastDeliveryError=$error,UpdatedAt=$updated",
            [("$rule", state.RuleId), ("$episode", state.CurrentEpisodeId), ("$since", state.StateSince is null ? null : Time(state.StateSince.Value)), ("$triggered", state.TriggeredForCurrentEpisode ? 1 : 0), ("$triggeredAt", state.TriggeredAt is null ? null : Time(state.TriggeredAt.Value)), ("$pending", state.PendingDelivery ? 1 : 0), ("$delivery", state.PendingDeliveryId), ("$error", state.LastDeliveryError), ("$updated", Time(state.UpdatedAt))], cancellationToken);
    }

    public async Task<NotificationDelivery?> GetNotificationDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationDelivery WHERE Id=$id"; Add(command, "$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadNotificationDelivery(reader) : null;
    }

    public async Task<NotificationDelivery?> GetNotificationDeliveryForEpisodeAsync(long ruleId, string episodeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId=$rule AND EpisodeId=$episode"; Add(command, "$rule", ruleId); Add(command, "$episode", episodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadNotificationDelivery(reader) : null;
    }

    public async Task<NotificationDelivery> CreateNotificationDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.RuleId is not > 0 || delivery.SubjectId is not > 0 || string.IsNullOrWhiteSpace(delivery.EpisodeId)) throw new ArgumentException("通知投递信息不完整。", nameof(delivery));
        if (delivery.Channel != NotificationChannelType.QQ) throw new ArgumentOutOfRangeException(nameof(delivery), "当前只支持 QQ 通知。");
        if (delivery.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group) || string.IsNullOrWhiteSpace(delivery.TargetId)) throw new ArgumentException("QQ 通知目标无效。", nameof(delivery));
        await using var connection = await OpenAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO NotificationDelivery(RuleId,SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt) VALUES($rule,$subject,$episode,$created,$status,$delivered,$channel,$targetType,$target,$message,$error,$sent,$total,$last,$next)";
            AddDeliveryParameters(insert, delivery); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var select = connection.CreateCommand(); select.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId=$rule AND EpisodeId=$episode"; Add(select, "$rule", delivery.RuleId); Add(select, "$episode", delivery.EpisodeId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("通知投递记录保存失败。"); return ReadNotificationDelivery(reader);
    }

    public Task UpdateNotificationDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE NotificationDelivery SET Status=$status,DeliveredAt=$delivered,Error=$error,SentParts=$sent,TotalParts=$total,LastAttemptAt=$last,NextAttemptAt=$next WHERE Id=$id",
            [("$status", (int)delivery.Status), ("$delivered", delivery.DeliveredAt is null ? null : Time(delivery.DeliveredAt.Value)), ("$error", delivery.Error), ("$sent", delivery.SentParts), ("$total", delivery.TotalParts), ("$last", delivery.LastAttemptAt is null ? null : Time(delivery.LastAttemptAt.Value)), ("$next", delivery.NextAttemptAt is null ? null : Time(delivery.NextAttemptAt.Value)), ("$id", delivery.Id)], cancellationToken);

    public async Task<IReadOnlyList<NotificationDelivery>> GetPendingNotificationDeliveriesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId IS NOT NULL AND Status IN ($pending,$failed) AND (NextAttemptAt IS NULL OR NextAttemptAt<=$now) AND (TotalParts=0 OR SentParts<TotalParts) ORDER BY CreatedAt,Id"; Add(command, "$pending", (int)NotificationDeliveryStatus.Pending); Add(command, "$failed", (int)NotificationDeliveryStatus.Failed); Add(command, "$now", Time(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<NotificationDelivery>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationDelivery(reader)); return result;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> GetRecentNotificationDeliveriesAsync(int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationDelivery ORDER BY CreatedAt DESC,Id DESC LIMIT $limit"; Add(command, "$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<NotificationDelivery>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationDelivery(reader)); return result;
    }

    public async Task<ConnectionAlertState?> GetConnectionAlertStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM ConnectionAlertState WHERE Id=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(Text(reader, "FailureEpisodeId"), Text(reader, "FailureStartedAt") is { } started ? ParseTime(started) : null,
            Text(reader, "LastSuccessfulCloudUpdateAt") is { } updated ? ParseTime(updated) : null,
            reader.GetInt32(reader.GetOrdinal("FailureAlertSent")) != 0,
            reader.GetInt32(reader.GetOrdinal("RecoveryAlertSent")) != 0,
            ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    }

    public Task UpsertConnectionAlertStateAsync(ConnectionAlertState state, CancellationToken cancellationToken) =>
        ExecuteAsync("INSERT INTO ConnectionAlertState(Id,FailureEpisodeId,FailureStartedAt,LastSuccessfulCloudUpdateAt,FailureAlertSent,RecoveryAlertSent,UpdatedAt) VALUES(1,$episode,$started,$last,$failure,$recovery,$updated) ON CONFLICT(Id) DO UPDATE SET FailureEpisodeId=$episode,FailureStartedAt=$started,LastSuccessfulCloudUpdateAt=$last,FailureAlertSent=$failure,RecoveryAlertSent=$recovery,UpdatedAt=$updated",
            [("$episode", state.FailureEpisodeId), ("$started", state.FailureStartedAt is null ? null : Time(state.FailureStartedAt.Value)), ("$last", state.LastSuccessfulCloudUpdateAt is null ? null : Time(state.LastSuccessfulCloudUpdateAt.Value)), ("$failure", state.FailureAlertSent ? 1 : 0), ("$recovery", state.RecoveryAlertSent ? 1 : 0), ("$updated", Time(state.UpdatedAt))], cancellationToken);

    public async Task<SystemNotificationDelivery?> GetSystemNotificationDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM SystemNotificationDelivery WHERE Id=$id"; Add(command, "$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadSystemNotificationDelivery(reader) : null;
    }

    public async Task<SystemNotificationDelivery> CreateSystemNotificationDeliveryAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(delivery.EpisodeId) || string.IsNullOrWhiteSpace(delivery.TargetId)) throw new ArgumentException("系统通知投递信息不完整。", nameof(delivery));
        if (delivery.Kind is not (SystemNotificationKind.XiaomiConnectionFailure or SystemNotificationKind.XiaomiConnectionRecovery)) throw new ArgumentOutOfRangeException(nameof(delivery));
        if (delivery.Channel != NotificationChannelType.QQ || delivery.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group)) throw new ArgumentException("系统通知目标无效。", nameof(delivery));
        await using var connection = await OpenAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO SystemNotificationDelivery(Kind,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt) VALUES($kind,$episode,$created,$status,$delivered,$channel,$targetType,$target,$message,$error,$sent,$total,$last,$next)";
            AddSystemDeliveryParameters(insert, delivery); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var select = connection.CreateCommand(); select.CommandText = "SELECT * FROM SystemNotificationDelivery WHERE Kind=$kind AND EpisodeId=$episode"; Add(select, "$kind", (int)delivery.Kind); Add(select, "$episode", delivery.EpisodeId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("系统通知投递记录保存失败。"); return ReadSystemNotificationDelivery(reader);
    }

    public Task UpdateSystemNotificationDeliveryAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE SystemNotificationDelivery SET Status=$status,DeliveredAt=$delivered,Error=$error,SentParts=$sent,TotalParts=$total,LastAttemptAt=$last,NextAttemptAt=$next WHERE Id=$id",
            [("$status", (int)delivery.Status), ("$delivered", delivery.DeliveredAt is null ? null : Time(delivery.DeliveredAt.Value)), ("$error", delivery.Error), ("$sent", delivery.SentParts), ("$total", delivery.TotalParts), ("$last", delivery.LastAttemptAt is null ? null : Time(delivery.LastAttemptAt.Value)), ("$next", delivery.NextAttemptAt is null ? null : Time(delivery.NextAttemptAt.Value)), ("$id", delivery.Id)], cancellationToken);

    public async Task<IReadOnlyList<SystemNotificationDelivery>> GetPendingSystemNotificationDeliveriesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM SystemNotificationDelivery WHERE Status IN ($pending,$failed) AND (NextAttemptAt IS NULL OR NextAttemptAt<=$now) AND (TotalParts=0 OR SentParts<TotalParts) ORDER BY CreatedAt,Id"; Add(command, "$pending", (int)NotificationDeliveryStatus.Pending); Add(command, "$failed", (int)NotificationDeliveryStatus.Failed); Add(command, "$now", Time(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<SystemNotificationDelivery>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSystemNotificationDelivery(reader)); return result;
    }

    public async Task<IReadOnlyList<SystemNotificationDelivery>> GetRecentSystemNotificationDeliveriesAsync(int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200); await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM SystemNotificationDelivery ORDER BY CreatedAt DESC,Id DESC LIMIT $limit"; Add(command, "$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<SystemNotificationDelivery>(); while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSystemNotificationDelivery(reader)); return result;
    }

    public async Task MergeSubjectsAsync(long sourceSubjectId, long targetSubjectId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (sourceSubjectId <= 0 || targetSubjectId <= 0 || sourceSubjectId == targetSubjectId) throw new ArgumentException("需要选择两个不同的主体。", nameof(sourceSubjectId));
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken); var sqliteTransaction = (SqliteTransaction)transaction;
        await ExecuteInTransactionAsync(connection, sqliteTransaction, "INSERT INTO SubjectDeviceMembership(SubjectId,NetworkDeviceId,CreatedAt) SELECT $target,NetworkDeviceId,$created FROM SubjectDeviceMembership WHERE SubjectId=$source ON CONFLICT(NetworkDeviceId) DO UPDATE SET SubjectId=$target,CreatedAt=$created", [("$source", sourceSubjectId), ("$target", targetSubjectId), ("$created", Time(updatedAt))], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction, "DELETE FROM NotificationRule AS source WHERE source.SubjectId=$source AND EXISTS (SELECT 1 FROM NotificationRule t WHERE t.SubjectId=$target AND t.RuleCondition=source.RuleCondition AND t.ThresholdSeconds=source.ThresholdSeconds AND t.Channel=source.Channel AND t.TargetType=source.TargetType AND t.TargetId=source.TargetId AND t.MessageTemplate=source.MessageTemplate)", [("$source", sourceSubjectId), ("$target", targetSubjectId)], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction, "UPDATE NotificationRule SET SubjectId=$target,UpdatedAt=$updated WHERE SubjectId=$source", [("$source", sourceSubjectId), ("$target", targetSubjectId), ("$updated", Time(updatedAt))], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction, "UPDATE NotificationDelivery SET SubjectId=$target WHERE SubjectId=$source", [("$source", sourceSubjectId), ("$target", targetSubjectId)], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction, "DELETE FROM PresenceSubject WHERE Id=$source", [("$source", sourceSubjectId)], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, (string Name, object? Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNetworkDeviceHistorySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasHistoricalState = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(NetworkDevice)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                hasHistoricalState |= string.Equals(reader.GetString(1), "LastKnownHistoricalState", StringComparison.OrdinalIgnoreCase);
        }

        if (hasHistoricalState) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE NetworkDevice ADD COLUMN LastKnownHistoricalState INTEGER NOT NULL DEFAULT 0";
        await alter.ExecuteNonQueryAsync(cancellationToken);
        await using var backfill = connection.CreateCommand();
        backfill.CommandText = "UPDATE NetworkDevice SET LastKnownHistoricalState=CurrentState";
        await backfill.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateNotificationDeliverySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var needsMigration = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(NotificationDelivery)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                if (name is "RuleId" or "SubjectId") needsMigration |= reader.GetInt32(3) != 0;
            }
        }
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_list(NotificationDelivery)";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(2); var onDelete = reader.GetString(6);
                if (table is "NotificationRule" or "PresenceSubject") needsMigration |= !string.Equals(onDelete, "SET NULL", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (!needsMigration) return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await ExecuteInTransactionAsync(connection, sqliteTransaction, "DROP INDEX IF EXISTS IX_NotificationDelivery_Pending; DROP INDEX IF EXISTS IX_NotificationDelivery_Created; ALTER TABLE NotificationDelivery RENAME TO NotificationDelivery_Legacy; CREATE TABLE NotificationDelivery_New (Id INTEGER PRIMARY KEY AUTOINCREMENT, RuleId INTEGER NULL, SubjectId INTEGER NULL, EpisodeId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status INTEGER NOT NULL, DeliveredAt TEXT NULL, Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL, TargetId TEXT NOT NULL, Message TEXT NOT NULL, Error TEXT NULL, SentParts INTEGER NOT NULL DEFAULT 0, TotalParts INTEGER NOT NULL DEFAULT 0, LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL, FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE SET NULL, FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE SET NULL, UNIQUE(RuleId,EpisodeId)); INSERT INTO NotificationDelivery_New(Id,RuleId,SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt) SELECT Id,RuleId,SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt FROM NotificationDelivery_Legacy; DROP TABLE NotificationDelivery_Legacy; ALTER TABLE NotificationDelivery_New RENAME TO NotificationDelivery; CREATE INDEX IX_NotificationDelivery_Pending ON NotificationDelivery(Status,NextAttemptAt); CREATE INDEX IX_NotificationDelivery_Created ON NotificationDelivery(CreatedAt DESC);", [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON;"; await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<DateTimeOffset?> GetLatestDeviceObservationAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT MAX(LastSeenAt) FROM NetworkDevice WHERE LastSeenAt >= $start"; Add(command, "$start", Time(notBefore));
        var value = await command.ExecuteScalarAsync(cancellationToken); return value is string text ? ParseTime(text) : null;
    }

    private static async Task<int> ExecuteInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, (string Name, object? Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NotificationRule NormalizeRule(NotificationRule value)
    {
        if (value.SubjectId <= 0) throw new ArgumentException("通知规则必须绑定到主体。", nameof(value));
        if (value.Condition is not (NotificationCondition.OnlineFor or NotificationCondition.OfflineFor)) throw new ArgumentOutOfRangeException(nameof(value), "通知条件无效。");
        if (value.ThresholdSeconds is < 60 or > 365 * 24 * 60 * 60) throw new ArgumentOutOfRangeException(nameof(value), "通知时长必须在 1 分钟到 365 天之间。");
        if (value.Channel != NotificationChannelType.QQ) throw new ArgumentOutOfRangeException(nameof(value), "当前只支持 QQ 通知。");
        if (value.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group)) throw new ArgumentOutOfRangeException(nameof(value), "QQ 通知目标类型无效。");
        var target = value.TargetId.Trim();
        if (target.Length is 0 or > 256 || target.Any(char.IsWhiteSpace)) throw new ArgumentException("QQ 目标 OpenID 无效。", nameof(value));
        var template = value.MessageTemplate?.Trim() ?? string.Empty;
        if (template.Length > 10_000) throw new ArgumentException("通知内容过长。", nameof(value));
        return value with { TargetId = target, MessageTemplate = template };
    }

    private static void AddRuleParameters(SqliteCommand command, NotificationRule value)
    {
        Add(command, "$subject", value.SubjectId); Add(command, "$enabled", value.Enabled ? 1 : 0);
        Add(command, "$condition", (int)value.Condition); Add(command, "$threshold", value.ThresholdSeconds);
        Add(command, "$channel", (int)value.Channel); Add(command, "$targetType", (int)value.TargetType);
        Add(command, "$target", value.TargetId); Add(command, "$template", value.MessageTemplate);
        Add(command, "$created", Time(value.CreatedAt)); Add(command, "$updated", Time(value.UpdatedAt));
    }

    private static void AddDeliveryParameters(SqliteCommand command, NotificationDelivery value)
    {
        Add(command, "$rule", value.RuleId); Add(command, "$subject", value.SubjectId); Add(command, "$episode", value.EpisodeId);
        Add(command, "$created", Time(value.CreatedAt)); Add(command, "$status", (int)value.Status);
        Add(command, "$delivered", value.DeliveredAt is null ? null : Time(value.DeliveredAt.Value)); Add(command, "$channel", (int)value.Channel);
        Add(command, "$targetType", (int)value.TargetType); Add(command, "$target", value.TargetId); Add(command, "$message", value.Message);
        Add(command, "$error", value.Error); Add(command, "$sent", value.SentParts); Add(command, "$total", value.TotalParts);
        Add(command, "$last", value.LastAttemptAt is null ? null : Time(value.LastAttemptAt.Value)); Add(command, "$next", value.NextAttemptAt is null ? null : Time(value.NextAttemptAt.Value));
    }

    private static void AddSystemDeliveryParameters(SqliteCommand command, SystemNotificationDelivery value)
    {
        Add(command, "$kind", (int)value.Kind); Add(command, "$episode", value.EpisodeId); Add(command, "$created", Time(value.CreatedAt)); Add(command, "$status", (int)value.Status);
        Add(command, "$delivered", value.DeliveredAt is null ? null : Time(value.DeliveredAt.Value)); Add(command, "$channel", (int)value.Channel); Add(command, "$targetType", (int)value.TargetType); Add(command, "$target", value.TargetId); Add(command, "$message", value.Message); Add(command, "$error", value.Error); Add(command, "$sent", value.SentParts); Add(command, "$total", value.TotalParts);
        Add(command, "$last", value.LastAttemptAt is null ? null : Time(value.LastAttemptAt.Value)); Add(command, "$next", value.NextAttemptAt is null ? null : Time(value.NextAttemptAt.Value));
    }

    private static void AddDeviceParameters(SqliteCommand command, NetworkDevice value)
    {
        Add(command, "$router", value.RouterId); Add(command, "$mac", value.MacAddress); Add(command, "$original", value.OriginalName);
        Add(command, "$origin", value.OriginName); Add(command, "$custom", value.CustomName); Add(command, "$note", value.Note);
        Add(command, "$ip", value.LastIp); Add(command, "$connection", value.ConnectionType); Add(command, "$signal", value.Signal);
        Add(command, "$state", (int)value.CurrentState); Add(command, "$first", Time(value.FirstSeenAt)); Add(command, "$last", Time(value.LastSeenAt));
        Add(command, "$changed", value.LastStateChangedAt is null ? null : Time(value.LastStateChangedAt.Value));
        Add(command, "$historical", (int)(value.LastKnownHistoricalState ?? value.CurrentState));
    }

    private static Router ReadRouter(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetString(reader.GetOrdinal("MiotDid")), reader.GetString(reader.GetOrdinal("MiotModel")), reader.GetString(reader.GetOrdinal("PartnerId")), reader.GetString(reader.GetOrdinal("Name")), Text(reader, "HomeId"), Text(reader, "RoomId"), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("LastSeenAt"))));
    private static PresenceSubject ReadSubject(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), Guid.Parse(reader.GetString(reader.GetOrdinal("ExportId"))), reader.GetString(reader.GetOrdinal("DisplayName")), Text(reader, "Note"), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    private static NetworkDevice ReadDevice(SqliteDataReader reader)
    {
        var device = new NetworkDevice(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetInt64(reader.GetOrdinal("RouterId")), reader.GetString(reader.GetOrdinal("MacAddress")), Text(reader, "OriginalName"), Text(reader, "OriginName"), Text(reader, "CustomName"), Text(reader, "Note"), Text(reader, "LastIp"), Text(reader, "ConnectionType"), Integer(reader, "Signal"), (PresenceState)reader.GetInt32(reader.GetOrdinal("CurrentState")), ParseTime(reader.GetString(reader.GetOrdinal("FirstSeenAt"))), ParseTime(reader.GetString(reader.GetOrdinal("LastSeenAt"))), Text(reader, "LastStateChangedAt") is { } changed ? ParseTime(changed) : null);
        return device with { LastKnownHistoricalState = (PresenceState)reader.GetInt32(reader.GetOrdinal("LastKnownHistoricalState")) };
    }
    private static NotificationRule ReadNotificationRule(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetInt64(reader.GetOrdinal("SubjectId")), reader.GetInt32(reader.GetOrdinal("Enabled")) != 0, (NotificationCondition)reader.GetInt32(reader.GetOrdinal("RuleCondition")), reader.GetInt64(reader.GetOrdinal("ThresholdSeconds")), (NotificationChannelType)reader.GetInt32(reader.GetOrdinal("Channel")), (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")), reader.GetString(reader.GetOrdinal("TargetId")), reader.GetString(reader.GetOrdinal("MessageTemplate")), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    private static NotificationRuleState ReadNotificationRuleState(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("RuleId")), Text(reader, "CurrentEpisodeId"), Text(reader, "StateSince") is { } since ? ParseTime(since) : null, reader.GetInt32(reader.GetOrdinal("TriggeredForCurrentEpisode")) != 0, Text(reader, "TriggeredAt") is { } triggered ? ParseTime(triggered) : null, reader.GetInt32(reader.GetOrdinal("PendingDelivery")) != 0, reader.IsDBNull(reader.GetOrdinal("PendingDeliveryId")) ? null : reader.GetInt64(reader.GetOrdinal("PendingDeliveryId")), Text(reader, "LastDeliveryError"), ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    private static NotificationDelivery ReadNotificationDelivery(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.IsDBNull(reader.GetOrdinal("RuleId")) ? null : reader.GetInt64(reader.GetOrdinal("RuleId")), reader.IsDBNull(reader.GetOrdinal("SubjectId")) ? null : reader.GetInt64(reader.GetOrdinal("SubjectId")), reader.GetString(reader.GetOrdinal("EpisodeId")), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), (NotificationDeliveryStatus)reader.GetInt32(reader.GetOrdinal("Status")), Text(reader, "DeliveredAt") is { } delivered ? ParseTime(delivered) : null, (NotificationChannelType)reader.GetInt32(reader.GetOrdinal("Channel")), (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")), reader.GetString(reader.GetOrdinal("TargetId")), reader.GetString(reader.GetOrdinal("Message")), Text(reader, "Error"), reader.GetInt32(reader.GetOrdinal("SentParts")), reader.GetInt32(reader.GetOrdinal("TotalParts")), Text(reader, "LastAttemptAt") is { } attempted ? ParseTime(attempted) : null, Text(reader, "NextAttemptAt") is { } next ? ParseTime(next) : null);
    private static SystemNotificationDelivery ReadSystemNotificationDelivery(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), (SystemNotificationKind)reader.GetInt32(reader.GetOrdinal("Kind")), reader.GetString(reader.GetOrdinal("EpisodeId")), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), (NotificationDeliveryStatus)reader.GetInt32(reader.GetOrdinal("Status")), Text(reader, "DeliveredAt") is { } delivered ? ParseTime(delivered) : null, (NotificationChannelType)reader.GetInt32(reader.GetOrdinal("Channel")), (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")), reader.GetString(reader.GetOrdinal("TargetId")), reader.GetString(reader.GetOrdinal("Message")), Text(reader, "Error"), reader.GetInt32(reader.GetOrdinal("SentParts")), reader.GetInt32(reader.GetOrdinal("TotalParts")), Text(reader, "LastAttemptAt") is { } attempted ? ParseTime(attempted) : null, Text(reader, "NextAttemptAt") is { } next ? ParseTime(next) : null);
    private static string? Text(SqliteDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetString(i); }
    private static int? Integer(SqliteDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetInt32(i); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
