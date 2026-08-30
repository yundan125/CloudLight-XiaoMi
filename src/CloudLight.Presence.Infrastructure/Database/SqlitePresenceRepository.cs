using System.Globalization;
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
            CREATE TABLE IF NOT EXISTS MonitoringGapSubjectBaseline (
              MonitoringGapId INTEGER NOT NULL, SubjectId INTEGER NOT NULL,
              State INTEGER NOT NULL,
              PRIMARY KEY(MonitoringGapId, SubjectId),
              FOREIGN KEY(MonitoringGapId) REFERENCES MonitoringGap(Id) ON DELETE CASCADE,
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_MonitoringGapSubjectBaseline_Subject ON MonitoringGapSubjectBaseline(SubjectId);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PresenceSession_Stable ON PresenceSession(DeviceId, StartedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_MonitoringGap_Stable ON MonitoringGap(StartedAt, Reason);
            CREATE TABLE IF NOT EXISTS ApplicationRun (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, StartedAt TEXT NOT NULL,
              EndedAt TEXT NULL, LastSuccessfulCloudUpdateAt TEXT NULL);
            CREATE TABLE IF NOT EXISTS PresenceSubject (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, ExportId TEXT NOT NULL UNIQUE,
              DisplayName TEXT NOT NULL, Note TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SubjectCurrentState (
              SubjectId INTEGER PRIMARY KEY, CurrentState INTEGER NOT NULL,
              StateSince TEXT NOT NULL, LastObservedAt TEXT NOT NULL, PendingOfflineSince TEXT NULL,
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_SubjectCurrentState_Observed ON SubjectCurrentState(LastObservedAt DESC);
            CREATE TABLE IF NOT EXISTS SubjectPresenceEvent (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, SubjectId INTEGER NOT NULL,
              EventType INTEGER NOT NULL, ObservedAt TEXT NOT NULL, MonitoringGapId INTEGER NULL,
              StateSince TEXT NULL,
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE,
              FOREIGN KEY(MonitoringGapId) REFERENCES MonitoringGap(Id) ON DELETE SET NULL);
            CREATE INDEX IF NOT EXISTS IX_SubjectPresenceEvent_Subject_Observed ON SubjectPresenceEvent(SubjectId, ObservedAt DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SubjectPresenceEvent_Stable ON SubjectPresenceEvent(SubjectId, EventType, ObservedAt);
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
            CREATE TABLE IF NOT EXISTS NotificationRecipient (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Note TEXT NOT NULL DEFAULT '',
              OpenId TEXT NOT NULL, TargetType INTEGER NOT NULL,
              CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
              UNIQUE(TargetType,OpenId));
            CREATE INDEX IF NOT EXISTS IX_NotificationRecipient_Updated ON NotificationRecipient(UpdatedAt DESC,Id DESC);
            CREATE TABLE IF NOT EXISTS NotificationRuleRecipient (
              RuleId INTEGER NOT NULL, RecipientId INTEGER NOT NULL, CreatedAt TEXT NOT NULL,
              PRIMARY KEY(RuleId,RecipientId),
              FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE CASCADE,
              FOREIGN KEY(RecipientId) REFERENCES NotificationRecipient(Id) ON DELETE RESTRICT);
            CREATE INDEX IF NOT EXISTS IX_NotificationRuleRecipient_Recipient ON NotificationRuleRecipient(RecipientId);
            CREATE TABLE IF NOT EXISTS NotificationRuleState (
              RuleId INTEGER PRIMARY KEY, CurrentEpisodeId TEXT NULL, StateSince TEXT NULL,
              TriggeredForCurrentEpisode INTEGER NOT NULL, TriggeredAt TEXT NULL,
              PendingDelivery INTEGER NOT NULL, PendingDeliveryId INTEGER NULL,
              LastDeliveryError TEXT NULL, UpdatedAt TEXT NOT NULL,
              LastProcessedSubjectEventId INTEGER NULL,
              FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS NotificationDelivery (
              Id INTEGER PRIMARY KEY AUTOINCREMENT, RuleId INTEGER NULL, SubjectId INTEGER NULL,
              EpisodeId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status INTEGER NOT NULL,
              DeliveredAt TEXT NULL, Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL,
              TargetId TEXT NOT NULL, RecipientId INTEGER NULL, Message TEXT NOT NULL, Error TEXT NULL,
              SentParts INTEGER NOT NULL DEFAULT 0, TotalParts INTEGER NOT NULL DEFAULT 0,
              LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL,
              FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE SET NULL,
              FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE SET NULL,
              FOREIGN KEY(RecipientId) REFERENCES NotificationRecipient(Id) ON DELETE SET NULL,
              UNIQUE(RuleId,EpisodeId,RecipientId));
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
              RecipientId INTEGER NULL, Message TEXT NOT NULL, Error TEXT NULL, SentParts INTEGER NOT NULL DEFAULT 0,
              TotalParts INTEGER NOT NULL DEFAULT 0, LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL,
              FOREIGN KEY(RecipientId) REFERENCES NotificationRecipient(Id) ON DELETE SET NULL,
              UNIQUE(Kind,EpisodeId,RecipientId));
            CREATE INDEX IF NOT EXISTS IX_SystemNotificationDelivery_Pending ON SystemNotificationDelivery(Status,NextAttemptAt);
            CREATE INDEX IF NOT EXISTS IX_SystemNotificationDelivery_Created ON SystemNotificationDelivery(CreatedAt DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureNetworkDeviceHistorySchemaAsync(connection, cancellationToken);
        await EnsureSubjectCurrentStateSchemaAsync(connection, cancellationToken);
        await MigrateSubjectPresenceEventSchemaAsync(connection, cancellationToken);
        await EnsureNotificationRuleStateSchemaAsync(connection, cancellationToken);
        await MigrateNotificationRecipientsAsync(connection, cancellationToken);
        await MigrateNotificationDeliverySchemaAsync(connection, cancellationToken);
        await MigrateSystemNotificationDeliverySchemaAsync(connection, cancellationToken);
        await EnsureLegacyNotificationDeliveryUniqueIndexAsync(connection, cancellationToken);
        await BackfillNotificationDeliveryRecipientsAsync(connection, cancellationToken);
        await EnsureEveryDeviceHasSubjectAsync(cancellationToken);
        await EnsureSubjectCurrentStatesAsync(cancellationToken);
        await ReconcileSubjectIdentityAsync(cancellationToken);
        await EnsureNotificationRuleEventWatermarksAsync(cancellationToken);
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
        await BackfillMissingSubjectCurrentStatesAsync(connection, (SqliteTransaction)transaction, cancellationToken);
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
        await BackfillMissingSubjectCurrentStatesAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureSubjectCurrentStatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await BackfillMissingSubjectCurrentStatesAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReconcileSubjectIdentityAsync(CancellationToken cancellationToken)
    {
        // A subject is an identity, not a display-name bucket.  Automatic
        // reconciliation is therefore deliberately narrow: only an empty
        // subject whose name matches exactly one subject's current device
        // metadata may be merged.  Two populated subjects with the same name
        // remain distinct and are rendered distinctly by the UI.
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var emptySubjectIds = await ReadLongsInTransactionAsync(connection, sqliteTransaction,
            "SELECT s.Id FROM PresenceSubject s WHERE NOT EXISTS (SELECT 1 FROM SubjectDeviceMembership m WHERE m.SubjectId=s.Id) ORDER BY s.Id",
            [], cancellationToken);

        foreach (var sourceSubjectId in emptySubjectIds)
        {
            var source = await ReadSubjectInTransactionAsync(connection, sqliteTransaction, sourceSubjectId, cancellationToken);
            if (source is null) continue;
            var hasData = await SubjectHasDependentDataAsync(connection, sqliteTransaction, sourceSubjectId, cancellationToken);
            if (!hasData)
            {
                await ExecuteInTransactionAsync(connection, sqliteTransaction, "DELETE FROM PresenceSubject WHERE Id=$subject", [("$subject", sourceSubjectId)], cancellationToken);
                continue;
            }
            var targets = await ReadLongsInTransactionAsync(connection, sqliteTransaction,
                "SELECT DISTINCT m.SubjectId FROM NetworkDevice d JOIN SubjectDeviceMembership m ON m.NetworkDeviceId=d.Id WHERE m.SubjectId<>$source AND (lower(trim(COALESCE(d.OriginalName,'')))=lower(trim($name)) OR lower(trim(COALESCE(d.OriginName,'')))=lower(trim($name)) OR lower(trim(COALESCE(d.CustomName,'')))=lower(trim($name))) ORDER BY m.SubjectId",
                [("$source", sourceSubjectId), ("$name", source.DisplayName)], cancellationToken);
            if (targets.Count == 1 && !await HasAmbiguousEmptyIdentityAsync(connection, sqliteTransaction, sourceSubjectId, source.DisplayName, cancellationToken))
            {
                await MergeSubjectsInTransactionAsync(connection, sqliteTransaction, sourceSubjectId, targets[0], DateTimeOffset.UtcNow, cancellationToken);
                continue;
            }

            // Empty shells with no historical or notification data cannot
            // represent a real subject.  Do not delete an ambiguous shell
            // carrying history: it remains auditable and the UI will show
            // enough identity context for a user decision.
        }

        await BackfillMissingSubjectCurrentStatesAsync(connection, sqliteTransaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetSubjectDevicesAsync(long subjectId, IReadOnlyCollection<long> deviceIds, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var affectedSubjectIds = new HashSet<long> { subjectId };
        foreach (var deviceId in deviceIds.Distinct())
        {
            await using var existing = connection.CreateCommand();
            existing.Transaction = sqliteTransaction;
            existing.CommandText = "SELECT SubjectId FROM SubjectDeviceMembership WHERE NetworkDeviceId=$device";
            Add(existing, "$device", deviceId);
            var value = await existing.ExecuteScalarAsync(cancellationToken);
            if (value is not null and not DBNull) affectedSubjectIds.Add(Convert.ToInt64(value));
        }
        await using (var remove = connection.CreateCommand()) { remove.Transaction = sqliteTransaction; remove.CommandText = "DELETE FROM SubjectDeviceMembership WHERE SubjectId=$id"; Add(remove, "$id", subjectId); await remove.ExecuteNonQueryAsync(cancellationToken); }
        foreach (var deviceId in deviceIds.Distinct())
        {
            await using var command = connection.CreateCommand(); command.Transaction = sqliteTransaction;
            command.CommandText = "INSERT INTO SubjectDeviceMembership(SubjectId,NetworkDeviceId,CreatedAt) VALUES($subject,$device,$created) ON CONFLICT(NetworkDeviceId) DO UPDATE SET SubjectId=$subject,CreatedAt=$created";
            Add(command, "$subject", subjectId); Add(command, "$device", deviceId); Add(command, "$created", Time(createdAt)); await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (deviceIds.Count == 0)
        {
            await ExecuteInTransactionAsync(connection, sqliteTransaction,
                "DELETE FROM PresenceSubject WHERE Id=$subject",
                [("$subject", subjectId)], cancellationToken);
        }

        await EnsureEveryDeviceHasSubjectAsync(connection, sqliteTransaction, createdAt, cancellationToken);
        if (deviceIds.Count > 0)
        {
            foreach (var affectedSubjectId in affectedSubjectIds.Where(value => value != subjectId).ToArray())
            {
                if (await IsEmptySubjectAsync(connection, sqliteTransaction, affectedSubjectId, cancellationToken))
                    await MergeSubjectsInTransactionAsync(connection, sqliteTransaction, affectedSubjectId, subjectId, createdAt, cancellationToken);
            }
            await RebuildSubjectCurrentStateInTransactionAsync(connection, sqliteTransaction, subjectId, createdAt, cancellationToken);
        }
        await BackfillMissingSubjectCurrentStatesAsync(connection, sqliteTransaction, cancellationToken);
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

    private static async Task BackfillMissingSubjectCurrentStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var subjectIds = new List<long>();
        await using (var subjects = connection.CreateCommand())
        {
            subjects.Transaction = transaction;
            subjects.CommandText = "SELECT s.Id FROM PresenceSubject s WHERE NOT EXISTS (SELECT 1 FROM SubjectCurrentState c WHERE c.SubjectId=s.Id) ORDER BY s.Id";
            await using var reader = await subjects.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) subjectIds.Add(reader.GetInt64(0));
        }

        foreach (var subjectId in subjectIds)
        {
            var members = new List<NetworkDevice>();
            await using (var devices = connection.CreateCommand())
            {
                devices.Transaction = transaction;
                devices.CommandText = "SELECT d.* FROM NetworkDevice d JOIN SubjectDeviceMembership m ON m.NetworkDeviceId=d.Id WHERE m.SubjectId=$subject";
                Add(devices, "$subject", subjectId);
                await using var reader = await devices.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) members.Add(ReadDevice(reader));
            }

            if (members.Count == 0) continue;
            var state = AggregateHistoricalState(members);
            if (state is not (PresenceState.Online or PresenceState.Offline)) continue;
            var relevant = members.Where(value => (value.LastKnownHistoricalState ?? value.CurrentObservedState) == state).ToArray();
            if (relevant.Length == 0) continue;
            var candidates = relevant.Select(value => value.LastStateChangedAt ?? value.LastSeenAt).ToArray();
            var stateSince = state == PresenceState.Online ? candidates.Min() : candidates.Max();
            var lastObservedAt = members.Max(value => value.LastSeenAt);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO SubjectCurrentState(SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince) VALUES($subject,$state,$since,$observed,NULL)";
            Add(insert, "$subject", subjectId);
            Add(insert, "$state", (int)state);
            Add(insert, "$since", Time(stateSince));
            Add(insert, "$observed", Time(lastObservedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CreateStandaloneSubjectAsync(SqliteConnection connection, SqliteTransaction transaction, NetworkDevice device, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT 1 FROM SubjectDeviceMembership WHERE NetworkDeviceId=$device LIMIT 1";
            Add(existing, "$device", device.Id);
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
                return;
        }

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

    public async Task<SubjectCurrentState?> GetSubjectCurrentStateAsync(long subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince FROM SubjectCurrentState WHERE SubjectId=$subject";
        Add(command, "$subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubjectCurrentState(reader) : null;
    }

    public async Task<IReadOnlyList<SubjectCurrentState>> GetSubjectCurrentStatesAsync(
        IReadOnlyCollection<long> subjectIds,
        CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var placeholders = subjectIds.Distinct().Select((_, index) => $"$subject{index}").ToArray();
        command.CommandText = $"SELECT SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince FROM SubjectCurrentState WHERE SubjectId IN ({string.Join(',', placeholders)})";
        foreach (var (subjectId, index) in subjectIds.Distinct().Select((value, index) => (value, index)))
            Add(command, placeholders[index], subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SubjectCurrentState>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSubjectCurrentState(reader));
        return result;
    }

    public Task UpsertSubjectCurrentStateAsync(SubjectCurrentState state, CancellationToken cancellationToken)
    {
        if (state.CurrentState is not (PresenceState.Online or PresenceState.Offline))
            throw new ArgumentOutOfRangeException(nameof(state), "主体当前状态必须是在线或离线。");
        return ExecuteAsync(
            "INSERT INTO SubjectCurrentState(SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince) VALUES($subject,$state,$since,$observed,$pending) ON CONFLICT(SubjectId) DO UPDATE SET CurrentState=$state,StateSince=$since,LastObservedAt=$observed,PendingOfflineSince=$pending",
            [("$subject", state.SubjectId), ("$state", (int)state.CurrentState), ("$since", Time(state.StateSince)), ("$observed", Time(state.LastObservedAt)), ("$pending", state.PendingOfflineSince is null ? null : Time(state.PendingOfflineSince.Value))],
            cancellationToken);
    }

    public async Task RecordSubjectStateAndEventAsync(SubjectCurrentState state, SubjectPresenceEvent presenceEvent, CancellationToken cancellationToken)
    {
        if (state.CurrentState is not (PresenceState.Online or PresenceState.Offline))
            throw new ArgumentOutOfRangeException(nameof(state), "主体当前状态必须是在线或离线。");
        if (state.SubjectId != presenceEvent.SubjectId)
            throw new ArgumentException("主体状态和主体事件必须属于同一主体。", nameof(presenceEvent));
        if (EventState(presenceEvent.EventType) != state.CurrentState)
            throw new InvalidOperationException("主体事件状态与主体当前确认状态不一致，已拒绝写入。");

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await UpsertSubjectCurrentStateInTransactionAsync(connection, sqliteTransaction, state, cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "INSERT OR IGNORE INTO SubjectPresenceEvent(SubjectId,EventType,ObservedAt,MonitoringGapId,StateSince) VALUES($subject,$type,$at,$gap,$since)",
            [("$subject", presenceEvent.SubjectId), ("$type", (int)presenceEvent.EventType), ("$at", Time(presenceEvent.ObservedAt)), ("$gap", presenceEvent.MonitoringGapId), ("$since", presenceEvent.StateSince is null ? null : Time(presenceEvent.StateSince.Value))],
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task AddSubjectPresenceEventAsync(SubjectPresenceEvent value, CancellationToken cancellationToken) =>
        ExecuteAsync("INSERT OR IGNORE INTO SubjectPresenceEvent(SubjectId,EventType,ObservedAt,MonitoringGapId,StateSince) VALUES($subject,$type,$at,$gap,$since)",
            [("$subject", value.SubjectId), ("$type", (int)value.EventType), ("$at", Time(value.ObservedAt)), ("$gap", value.MonitoringGapId), ("$since", value.StateSince is null ? null : Time(value.StateSince.Value))], cancellationToken);

    public async Task<SubjectPresenceEvent?> GetSubjectPresenceEventAsync(long eventId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SubjectPresenceEvent WHERE Id=$id";
        Add(command, "$id", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubjectPresenceEvent(reader) : null;
    }

    public async Task<IReadOnlyList<SubjectPresenceEvent>> GetSubjectPresenceEventsAfterIdAsync(long subjectId, long afterEventId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SubjectPresenceEvent WHERE SubjectId=$subject AND Id>$after ORDER BY Id";
        Add(command, "$subject", subjectId); Add(command, "$after", afterEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SubjectPresenceEvent>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSubjectPresenceEvent(reader));
        return result;
    }

    public async Task<long?> GetLatestSubjectPresenceEventIdAsync(long subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Id) FROM SubjectPresenceEvent WHERE SubjectId=$subject";
        Add(command, "$subject", subjectId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<SubjectPresenceEvent>> GetSubjectPresenceEventsAsync(
        long subjectId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SubjectPresenceEvent WHERE SubjectId=$subject AND ObservedAt >= $from AND ObservedAt <= $to ORDER BY ObservedAt DESC,Id DESC";
        Add(command, "$subject", subjectId); Add(command, "$from", Time(from)); Add(command, "$to", Time(to));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SubjectPresenceEvent>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadSubjectPresenceEvent(reader));
        return result;
    }

    public async Task AddSessionAsync(PresenceSession value, CancellationToken cancellationToken) =>
        await ExecuteAsync("INSERT INTO PresenceSession(DeviceId,StartedAt,EndedAt,StartKnown,EndKnown) VALUES($device,$start,$end,$sk,$ek)",
            [("$device", value.DeviceId), ("$start", Time(value.StartedAt)), ("$end", value.EndedAt is null ? null : Time(value.EndedAt.Value)), ("$sk", value.StartKnown ? 1 : 0), ("$ek", value.EndKnown ? 1 : 0)], cancellationToken);

    public async Task CloseOpenSessionAsync(long deviceId, DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE PresenceSession SET EndedAt=$end,EndKnown=1 WHERE Id=(SELECT Id FROM PresenceSession WHERE DeviceId=$device AND EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1)",
            [("$device", deviceId), ("$end", Time(endedAt))], cancellationToken);

    public async Task CloseOpenSessionAtBoundaryAsync(long deviceId, DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync("UPDATE PresenceSession SET EndedAt=$end,EndKnown=0 WHERE DeviceId=$device AND EndedAt IS NULL AND StartedAt <= $end",
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

    public async Task<IReadOnlyList<MonitoringGapSubjectBaseline>> GetMonitoringGapSubjectBaselinesAsync(
        long monitoringGapId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MonitoringGapId,SubjectId,State FROM MonitoringGapSubjectBaseline WHERE MonitoringGapId=$gap";
        Add(command, "$gap", monitoringGapId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MonitoringGapSubjectBaseline>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MonitoringGapSubjectBaseline(reader.GetInt64(0), reader.GetInt64(1), (PresenceState)reader.GetInt32(2)));
        return result;
    }

    public Task AddMonitoringGapSubjectBaselineAsync(MonitoringGapSubjectBaseline baseline, CancellationToken cancellationToken) =>
        ExecuteAsync("INSERT OR IGNORE INTO MonitoringGapSubjectBaseline(MonitoringGapId,SubjectId,State) VALUES($gap,$subject,$state)",
            [("$gap", baseline.MonitoringGapId), ("$subject", baseline.SubjectId), ("$state", (int)baseline.State)], cancellationToken);

    public async Task<long> StartMonitoringGapAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await using var command = connection.CreateCommand(); command.Transaction = sqliteTransaction;
        command.CommandText = "INSERT INTO MonitoringGap(StartedAt,Reason) VALUES($at,$reason); SELECT last_insert_rowid();";
        Add(command, "$at", Time(startedAt)); Add(command, "$reason", reason);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        await CloseOpenSessionsAtAsync(connection, sqliteTransaction, startedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task EndMonitoringGapAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "UPDATE PresenceSession SET EndedAt=(SELECT StartedAt FROM MonitoringGap WHERE Id=$id),EndKnown=0 WHERE EndedAt IS NULL AND StartedAt <= (SELECT StartedAt FROM MonitoringGap WHERE Id=$id)",
            [("$id", gapId)], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "UPDATE MonitoringGap SET EndedAt=$end WHERE Id=$id",
            [("$id", gapId), ("$end", Time(endedAt))], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "DELETE FROM MonitoringGapSubjectBaseline WHERE MonitoringGapId=$id",
            [("$id", gapId)], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CloseOpenMonitoringGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var starts = new List<DateTimeOffset>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = sqliteTransaction;
            command.CommandText = "SELECT StartedAt FROM MonitoringGap WHERE EndedAt IS NULL AND StartedAt < $end ORDER BY StartedAt";
            Add(command, "$end", Time(endedAt));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) starts.Add(ParseTime(reader.GetString(0)));
        }
        foreach (var start in starts)
            await CloseOpenSessionsAtAsync(connection, sqliteTransaction, start, cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "DELETE FROM MonitoringGapSubjectBaseline WHERE MonitoringGapId IN (SELECT Id FROM MonitoringGap WHERE EndedAt IS NULL AND StartedAt < $end)",
            [("$end", Time(endedAt))], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "UPDATE MonitoringGap SET EndedAt=$end WHERE EndedAt IS NULL AND StartedAt < $end",
            [("$end", Time(endedAt))], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

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
                gap.CommandText = "INSERT OR IGNORE INTO MonitoringGap(StartedAt,EndedAt,Reason) VALUES($start,NULL,'UnexpectedTermination')";
                Add(gap, "$start", Time(gapStart)); await gap.ExecuteNonQueryAsync(cancellationToken);
                await CloseOpenSessionsAtAsync(connection, (SqliteTransaction)transaction, gapStart, cancellationToken);
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
        return await AttachNotificationRecipientIdsAsync(result, cancellationToken);
    }

    public async Task<NotificationRule?> GetNotificationRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NotificationRule WHERE Id=$id"; Add(command, "$id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var rule = ReadNotificationRule(reader);
        return rule with { RecipientIds = await GetNotificationRuleRecipientIdsAsync(rule.Id, cancellationToken) };
    }

    public async Task<NotificationRule> CreateNotificationRuleAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRule(rule);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        NotificationRule created;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = sqliteTransaction;
            command.CommandText = "INSERT INTO NotificationRule(SubjectId,Enabled,RuleCondition,ThresholdSeconds,Channel,TargetType,TargetId,MessageTemplate,CreatedAt,UpdatedAt) VALUES($subject,$enabled,$condition,$threshold,$channel,$targetType,$target,$template,$created,$updated) RETURNING *";
            AddRuleParameters(command, normalized);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            created = ReadNotificationRule(reader);
        }
        await InsertNotificationRuleRecipientsInTransactionAsync(connection, sqliteTransaction, created.Id, normalized.RecipientIds, cancellationToken);
        await ResetNotificationRuleStateInTransactionAsync(connection, sqliteTransaction, created.Id, created.SubjectId, created.UpdatedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task UpdateNotificationRuleAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        if (rule.Id <= 0) throw new ArgumentException("通知规则编号无效。", nameof(rule));
        var normalized = NormalizeRule(rule);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "UPDATE NotificationRule SET SubjectId=$subject,Enabled=$enabled,RuleCondition=$condition,ThresholdSeconds=$threshold,Channel=$channel,TargetType=$targetType,TargetId=$target,MessageTemplate=$template,UpdatedAt=$updated WHERE Id=$id",
            [("$subject", normalized.SubjectId), ("$enabled", normalized.Enabled ? 1 : 0), ("$condition", (int)normalized.Condition), ("$threshold", normalized.ThresholdSeconds), ("$channel", (int)normalized.Channel), ("$targetType", (int)normalized.TargetType), ("$target", normalized.TargetId), ("$template", normalized.MessageTemplate), ("$updated", Time(normalized.UpdatedAt)), ("$id", normalized.Id)], cancellationToken);
        await InsertNotificationRuleRecipientsInTransactionAsync(connection, sqliteTransaction, normalized.Id, normalized.RecipientIds, cancellationToken, replace: true);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task DeleteNotificationRuleAsync(long ruleId, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM NotificationRule WHERE Id=$id", [("$id", ruleId)], cancellationToken);

    public async Task<IReadOnlyList<NotificationRecipient>> GetNotificationRecipientsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NotificationRecipient ORDER BY UpdatedAt DESC,Id DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationRecipient>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationRecipient(reader));
        return result;
    }

    public async Task<NotificationRecipient?> GetNotificationRecipientAsync(long recipientId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NotificationRecipient WHERE Id=$id";
        Add(command, "$id", recipientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotificationRecipient(reader) : null;
    }

    public async Task<NotificationRecipient> CreateNotificationRecipientAsync(NotificationRecipient recipient, CancellationToken cancellationToken)
    {
        var normalized = NormalizeRecipient(recipient);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO NotificationRecipient(Note,OpenId,TargetType,CreatedAt,UpdatedAt) VALUES($note,$openid,$type,$created,$updated)";
        Add(command, "$note", normalized.Note);
        Add(command, "$openid", normalized.OpenId);
        Add(command, "$type", (int)normalized.TargetType);
        Add(command, "$created", Time(normalized.CreatedAt));
        Add(command, "$updated", Time(normalized.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT * FROM NotificationRecipient WHERE TargetType=$type AND OpenId=$openid";
        Add(select, "$type", (int)normalized.TargetType);
        Add(select, "$openid", normalized.OpenId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("QQ 接收人保存失败。");
        return ReadNotificationRecipient(reader);
    }

    public async Task UpdateNotificationRecipientAsync(NotificationRecipient recipient, CancellationToken cancellationToken)
    {
        if (recipient.Id <= 0) throw new ArgumentException("QQ 接收人编号无效。", nameof(recipient));
        var normalized = NormalizeRecipient(recipient);
        await ExecuteAsync("UPDATE NotificationRecipient SET Note=$note,OpenId=$openid,TargetType=$type,UpdatedAt=$updated WHERE Id=$id",
            [("$note", normalized.Note), ("$openid", normalized.OpenId), ("$type", (int)normalized.TargetType), ("$updated", Time(normalized.UpdatedAt)), ("$id", normalized.Id)], cancellationToken);
    }

    public async Task DeleteNotificationRecipientAsync(long recipientId, CancellationToken cancellationToken)
    {
        var usage = await GetNotificationRecipientUsageCountAsync(recipientId, cancellationToken);
        if (usage > 0) throw new InvalidOperationException($"该接收人正被 {usage} 条自动提醒使用，请先解除关联。");
        await ExecuteAsync("DELETE FROM NotificationRecipient WHERE Id=$id", [("$id", recipientId)], cancellationToken);
    }

    public async Task<int> GetNotificationRecipientUsageCountAsync(long recipientId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NotificationRuleRecipient WHERE RecipientId=$id";
        Add(command, "$id", recipientId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<NotificationRecipient>> GetNotificationRuleRecipientsAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.* FROM NotificationRecipient r JOIN NotificationRuleRecipient rr ON rr.RecipientId=r.Id WHERE rr.RuleId=$rule ORDER BY rr.CreatedAt,r.Id";
        Add(command, "$rule", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationRecipient>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationRecipient(reader));
        return result;
    }

    public async Task SetNotificationRuleRecipientsAsync(long ruleId, IReadOnlyCollection<long> recipientIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertNotificationRuleRecipientsInTransactionAsync(connection, (SqliteTransaction)transaction, ruleId, recipientIds, cancellationToken, replace: true);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<NotificationRuleState?> GetNotificationRuleStateAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationRuleState WHERE RuleId=$id"; Add(command, "$id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadNotificationRuleState(reader) : null;
    }

    public async Task UpsertNotificationRuleStateAsync(NotificationRuleState state, CancellationToken cancellationToken)
    {
        await ExecuteAsync("INSERT INTO NotificationRuleState(RuleId,CurrentEpisodeId,StateSince,TriggeredForCurrentEpisode,TriggeredAt,PendingDelivery,PendingDeliveryId,LastDeliveryError,UpdatedAt,LastProcessedSubjectEventId) VALUES($rule,$episode,$since,$triggered,$triggeredAt,$pending,$delivery,$error,$updated,$watermark) ON CONFLICT(RuleId) DO UPDATE SET CurrentEpisodeId=$episode,StateSince=$since,TriggeredForCurrentEpisode=$triggered,TriggeredAt=$triggeredAt,PendingDelivery=$pending,PendingDeliveryId=$delivery,LastDeliveryError=$error,UpdatedAt=$updated,LastProcessedSubjectEventId=$watermark",
            [("$rule", state.RuleId), ("$episode", state.CurrentEpisodeId), ("$since", state.StateSince is null ? null : Time(state.StateSince.Value)), ("$triggered", state.TriggeredForCurrentEpisode ? 1 : 0), ("$triggeredAt", state.TriggeredAt is null ? null : Time(state.TriggeredAt.Value)), ("$pending", state.PendingDelivery ? 1 : 0), ("$delivery", state.PendingDeliveryId), ("$error", state.LastDeliveryError), ("$updated", Time(state.UpdatedAt)), ("$watermark", state.LastProcessedSubjectEventId)], cancellationToken);
    }

    public async Task ResetNotificationRuleStateAsync(long ruleId, long subjectId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ResetNotificationRuleStateInTransactionAsync(connection, (SqliteTransaction)transaction, ruleId, subjectId, updatedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureNotificationRuleEventWatermarksAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "INSERT INTO NotificationRuleState(RuleId,CurrentEpisodeId,StateSince,TriggeredForCurrentEpisode,TriggeredAt,PendingDelivery,PendingDeliveryId,LastDeliveryError,UpdatedAt,LastProcessedSubjectEventId) SELECT r.Id,NULL,NULL,0,NULL,0,NULL,NULL,r.UpdatedAt,COALESCE((SELECT MAX(e.Id) FROM SubjectPresenceEvent e WHERE e.SubjectId=r.SubjectId),0) FROM NotificationRule r WHERE NOT EXISTS (SELECT 1 FROM NotificationRuleState s WHERE s.RuleId=r.Id)",
            [], cancellationToken);
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "UPDATE NotificationRuleState SET LastProcessedSubjectEventId=COALESCE((SELECT MAX(e.Id) FROM SubjectPresenceEvent e JOIN NotificationRule r ON r.Id=NotificationRuleState.RuleId WHERE e.SubjectId=r.SubjectId),0),UpdatedAt=UpdatedAt WHERE LastProcessedSubjectEventId IS NULL",
            [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<NotificationDelivery?> GetNotificationDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationDelivery WHERE Id=$id"; Add(command, "$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? ReadNotificationDelivery(reader) : null;
    }

    public async Task<NotificationDelivery?> GetNotificationDeliveryForEpisodeAsync(long ruleId, string episodeId, CancellationToken cancellationToken)
    {
        var deliveries = await GetNotificationDeliveriesForEpisodeAsync(ruleId, episodeId, cancellationToken);
        return deliveries.FirstOrDefault();
    }

    public async Task<IReadOnlyList<NotificationDelivery>> GetNotificationDeliveriesForEpisodeAsync(long ruleId, string episodeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId=$rule AND EpisodeId=$episode ORDER BY Id"; Add(command, "$rule", ruleId); Add(command, "$episode", episodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<NotificationDelivery>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationDelivery(reader));
        return result;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> GetNotificationDeliveriesForRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId=$rule ORDER BY CreatedAt DESC,Id DESC"; Add(command, "$rule", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<NotificationDelivery>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationDelivery(reader));
        return result;
    }

    public async Task<NotificationDelivery> CreateNotificationDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.RuleId is not > 0 || delivery.SubjectId is not > 0 || string.IsNullOrWhiteSpace(delivery.EpisodeId)) throw new ArgumentException("通知投递信息不完整。", nameof(delivery));
        if (delivery.Channel != NotificationChannelType.QQ) throw new ArgumentOutOfRangeException(nameof(delivery), "当前只支持 QQ 通知。");
        if (delivery.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group) || string.IsNullOrWhiteSpace(delivery.TargetId)) throw new ArgumentException("QQ 通知目标无效。", nameof(delivery));
        await using var connection = await OpenAsync(cancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO NotificationDelivery(RuleId,SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,RecipientId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt) VALUES($rule,$subject,$episode,$created,$status,$delivered,$channel,$targetType,$target,$recipient,$message,$error,$sent,$total,$last,$next)";
            AddDeliveryParameters(insert, delivery); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var select = connection.CreateCommand(); select.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId=$rule AND EpisodeId=$episode AND ((RecipientId=$recipient) OR ($recipient IS NULL AND RecipientId IS NULL)) ORDER BY Id"; Add(select, "$rule", delivery.RuleId); Add(select, "$episode", delivery.EpisodeId); Add(select, "$recipient", delivery.RecipientId);
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
            insert.CommandText = "INSERT OR IGNORE INTO SystemNotificationDelivery(Kind,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,RecipientId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt) VALUES($kind,$episode,$created,$status,$delivered,$channel,$targetType,$target,$recipient,$message,$error,$sent,$total,$last,$next)";
            AddSystemDeliveryParameters(insert, delivery); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var select = connection.CreateCommand(); select.CommandText = "SELECT * FROM SystemNotificationDelivery WHERE Kind=$kind AND EpisodeId=$episode AND ((RecipientId=$recipient) OR ($recipient IS NULL AND RecipientId IS NULL)) ORDER BY Id"; Add(select, "$kind", (int)delivery.Kind); Add(select, "$episode", delivery.EpisodeId); Add(select, "$recipient", delivery.RecipientId);
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
        if (sourceSubjectId <= 0 || targetSubjectId <= 0 || sourceSubjectId == targetSubjectId)
            throw new ArgumentException("需要选择两个不同的主体。", nameof(sourceSubjectId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        if (await ReadSubjectInTransactionAsync(connection, sqliteTransaction, sourceSubjectId, cancellationToken) is not null)
            await MergeSubjectsInTransactionAsync(connection, sqliteTransaction, sourceSubjectId, targetSubjectId, updatedAt, cancellationToken);
        else if (await ReadSubjectInTransactionAsync(connection, sqliteTransaction, targetSubjectId, cancellationToken) is null)
            throw new InvalidOperationException("目标主体不存在。 ");
        await BackfillMissingSubjectCurrentStatesAsync(connection, sqliteTransaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MergeSubjectsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceSubjectId,
        long targetSubjectId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        if (sourceSubjectId == targetSubjectId) return;
        var source = await ReadSubjectInTransactionAsync(connection, transaction, sourceSubjectId, cancellationToken)
            ?? throw new InvalidOperationException("源主体不存在。 ");
        var target = await ReadSubjectInTransactionAsync(connection, transaction, targetSubjectId, cancellationToken)
            ?? throw new InvalidOperationException("目标主体不存在。 ");
        var sourceState = await ReadSubjectCurrentStateInTransactionAsync(connection, transaction, sourceSubjectId, cancellationToken);
        var targetState = await ReadSubjectCurrentStateInTransactionAsync(connection, transaction, targetSubjectId, cancellationToken);
        var sourceRules = await ReadNotificationRulesForSubjectInTransactionAsync(connection, transaction, sourceSubjectId, cancellationToken);
        var targetRules = await ReadNotificationRulesForSubjectInTransactionAsync(connection, transaction, targetSubjectId, cancellationToken);

        // Preserve event identity whenever possible.  If the two subjects
        // contain the same stable event, keep the lower existing row and
        // retarget delivery episode references before removing the duplicate.
        foreach (var sourceEvent in await ReadSubjectPresenceEventsForSubjectInTransactionAsync(connection, transaction, sourceSubjectId, cancellationToken))
        {
            var targetEventId = await ScalarLongInTransactionAsync(connection, transaction,
                "SELECT Id FROM SubjectPresenceEvent WHERE SubjectId=$subject AND EventType=$type AND ObservedAt=$observed LIMIT 1",
                [("$subject", targetSubjectId), ("$type", (int)sourceEvent.EventType), ("$observed", Time(sourceEvent.ObservedAt))], cancellationToken);
            if (targetEventId == 0)
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE SubjectPresenceEvent SET SubjectId=$target WHERE Id=$event",
                    [("$target", targetSubjectId), ("$event", sourceEvent.Id)], cancellationToken);
                continue;
            }

            var keepEventId = Math.Min(sourceEvent.Id, targetEventId);
            var removeEventId = Math.Max(sourceEvent.Id, targetEventId);
            await RemapEventEpisodeInTransactionAsync(connection, transaction, removeEventId, keepEventId, sourceSubjectId, targetSubjectId, cancellationToken);
            await ExecuteInTransactionAsync(connection, transaction,
                "DELETE FROM SubjectPresenceEvent WHERE Id=$event",
                [("$event", removeEventId)], cancellationToken);
            if (keepEventId == sourceEvent.Id)
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE SubjectPresenceEvent SET SubjectId=$target WHERE Id=$event",
                    [("$target", targetSubjectId), ("$event", keepEventId)], cancellationToken);
        }

        await ExecuteInTransactionAsync(connection, transaction,
            "INSERT OR IGNORE INTO MonitoringGapSubjectBaseline(MonitoringGapId,SubjectId,State) SELECT MonitoringGapId,$target,State FROM MonitoringGapSubjectBaseline WHERE SubjectId=$source; DELETE FROM MonitoringGapSubjectBaseline WHERE SubjectId=$source",
            [("$source", sourceSubjectId), ("$target", targetSubjectId)], cancellationToken);
        await ExecuteInTransactionAsync(connection, transaction,
            "UPDATE SubjectDeviceMembership SET SubjectId=$target WHERE SubjectId=$source",
            [("$source", sourceSubjectId), ("$target", targetSubjectId)], cancellationToken);

        var members = await ReadDevicesForSubjectInTransactionAsync(connection, transaction, targetSubjectId, cancellationToken);
        await RebuildCanonicalSubjectStateInTransactionAsync(
            connection, transaction, targetSubjectId, members, targetState, sourceState, updatedAt, cancellationToken);
        await ExecuteInTransactionAsync(connection, transaction,
            "UPDATE PresenceSubject SET Note=CASE WHEN (Note IS NULL OR trim(Note)='') THEN $sourceNote ELSE Note END,UpdatedAt=$updated WHERE Id=$target",
            [("$sourceNote", source.Note), ("$updated", Time(Max(target.UpdatedAt, Max(source.UpdatedAt, updatedAt)))), ("$target", targetSubjectId)], cancellationToken);

        foreach (var sourceRule in sourceRules)
        {
            var targetRule = targetRules.FirstOrDefault(value => RulesAreEquivalent(value, sourceRule));
            var sourceRuleState = await ReadNotificationRuleStateInTransactionAsync(connection, transaction, sourceRule.Id, cancellationToken);
            if (targetRule is null)
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE NotificationRule SET SubjectId=$target,UpdatedAt=CASE WHEN UpdatedAt>$updated THEN UpdatedAt ELSE $updated END WHERE Id=$rule AND SubjectId=$source",
                    [("$target", targetSubjectId), ("$source", sourceSubjectId), ("$rule", sourceRule.Id), ("$updated", Time(updatedAt))], cancellationToken);
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE NotificationDelivery SET SubjectId=$target WHERE RuleId=$rule",
                    [("$target", targetSubjectId), ("$rule", sourceRule.Id)], cancellationToken);
                await ResetRuleStateAfterRebindInTransactionAsync(connection, transaction, sourceRule.Id, targetSubjectId, sourceRuleState, updatedAt, cancellationToken);
                continue;
            }

            var targetRuleState = await ReadNotificationRuleStateInTransactionAsync(connection, transaction, targetRule.Id, cancellationToken);
            await MoveRuleDeliveriesInTransactionAsync(connection, transaction, sourceRule.Id, targetRule.Id, targetSubjectId, cancellationToken);
            await MergeRuleStatesInTransactionAsync(connection, transaction, targetRule.Id, targetSubjectId, targetRuleState, sourceRuleState, updatedAt, cancellationToken);
            await ExecuteInTransactionAsync(connection, transaction,
                "DELETE FROM NotificationRule WHERE Id=$rule AND SubjectId=$source",
                [("$rule", sourceRule.Id), ("$source", sourceSubjectId)], cancellationToken);
        }

        // This also repairs old rows whose SubjectId did not agree with the
        // rule's subject (the real 16:05/16:06 history contains such rows).
        await ExecuteInTransactionAsync(connection, transaction,
            "UPDATE NotificationDelivery SET SubjectId=$target WHERE SubjectId=$source",
            [("$source", sourceSubjectId), ("$target", targetSubjectId)], cancellationToken);
        await ExecuteInTransactionAsync(connection, transaction,
            "DELETE FROM SubjectCurrentState WHERE SubjectId=$source; DELETE FROM PresenceSubject WHERE Id=$source",
            [("$source", sourceSubjectId)], cancellationToken);
    }

    private static async Task MoveRuleDeliveriesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceRuleId,
        long targetRuleId,
        long targetSubjectId,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in await ReadNotificationDeliveriesForRuleInTransactionAsync(connection, transaction, sourceRuleId, cancellationToken))
        {
            var collision = await ScalarLongInTransactionAsync(connection, transaction,
                "SELECT Id FROM NotificationDelivery WHERE RuleId=$rule AND EpisodeId=$episode AND Id<>$delivery LIMIT 1",
                [("$rule", targetRuleId), ("$episode", delivery.EpisodeId), ("$delivery", delivery.Id)], cancellationToken);
            if (collision != 0)
            {
                // Keep both audit rows, but do not attach two rows to the same
                // logical rule/episode.  The canonical delivery remains the
                // one selected by the database unique constraint.
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE NotificationDelivery SET RuleId=NULL,SubjectId=$subject WHERE Id=$delivery",
                    [("$subject", targetSubjectId), ("$delivery", delivery.Id)], cancellationToken);
            }
            else
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE NotificationDelivery SET RuleId=$rule,SubjectId=$subject WHERE Id=$delivery",
                    [("$rule", targetRuleId), ("$subject", targetSubjectId), ("$delivery", delivery.Id)], cancellationToken);
            }
        }
    }

    private static async Task RemapEventEpisodeInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long removedEventId,
        long keptEventId,
        long sourceSubjectId,
        long targetSubjectId,
        CancellationToken cancellationToken)
    {
        var oldEpisode = $"event:{removedEventId}";
        var newEpisode = $"event:{keptEventId}";
        foreach (var delivery in await ReadNotificationDeliveriesForEpisodeInTransactionAsync(connection, transaction, oldEpisode, cancellationToken))
        {
            var collision = delivery.RuleId is { } ruleId
                ? await ScalarLongInTransactionAsync(connection, transaction,
                    "SELECT Id FROM NotificationDelivery WHERE RuleId=$rule AND EpisodeId=$episode AND Id<>$delivery LIMIT 1",
                    [("$rule", ruleId), ("$episode", newEpisode), ("$delivery", delivery.Id)], cancellationToken)
                : 0;
            if (collision != 0)
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE NotificationDelivery SET RuleId=NULL,SubjectId=$subject WHERE Id=$delivery",
                    [("$subject", targetSubjectId), ("$delivery", delivery.Id)], cancellationToken);
            }
            else
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE NotificationDelivery SET EpisodeId=$episode,SubjectId=CASE WHEN SubjectId IS NULL OR SubjectId=$oldSubject THEN $subject ELSE SubjectId END WHERE Id=$delivery",
                    [("$episode", newEpisode), ("$oldSubject", sourceSubjectId), ("$subject", targetSubjectId), ("$delivery", delivery.Id)], cancellationToken);
            }
        }
    }

    private async Task ResetRuleStateAfterRebindInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ruleId,
        long subjectId,
        NotificationRuleState? previous,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var pending = await GetValidPendingDeliveryInTransactionAsync(connection, transaction, previous?.PendingDeliveryId, ruleId, cancellationToken);
        var watermark = await GetLatestSubjectPresenceEventIdInTransactionAsync(connection, transaction, subjectId, cancellationToken) ?? 0;
        await UpsertNotificationRuleStateInTransactionAsync(connection, transaction, new NotificationRuleState(
            ruleId, null, null, false, null, pending is not null, pending?.Id,
            pending?.Error, updatedAt, watermark), cancellationToken);
    }

    private async Task MergeRuleStatesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ruleId,
        long subjectId,
        NotificationRuleState? target,
        NotificationRuleState? source,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var targetPending = await GetValidPendingDeliveryInTransactionAsync(connection, transaction, target?.PendingDeliveryId, ruleId, cancellationToken);
        var sourcePending = await GetValidPendingDeliveryInTransactionAsync(connection, transaction, source?.PendingDeliveryId, ruleId, cancellationToken);
        var pending = targetPending ?? sourcePending;
        var latest = await GetLatestSubjectPresenceEventIdInTransactionAsync(connection, transaction, subjectId, cancellationToken);
        var watermark = Max(latest, Max(target?.LastProcessedSubjectEventId, source?.LastProcessedSubjectEventId)) ?? 0;
        await UpsertNotificationRuleStateInTransactionAsync(connection, transaction, new NotificationRuleState(
            ruleId, null, null, false, null, pending is not null, pending?.Id,
            pending?.Error, updatedAt, watermark), cancellationToken);
    }

    private static async Task<NotificationDelivery?> GetValidPendingDeliveryInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? deliveryId,
        long ruleId,
        CancellationToken cancellationToken)
    {
        if (deliveryId is not { } id) return null;
        var delivery = await ReadNotificationDeliveryInTransactionAsync(connection, transaction, id, cancellationToken);
        return delivery is { RuleId: var currentRule } && currentRule == ruleId && delivery.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed
            ? delivery
            : null;
    }

    private async Task RebuildSubjectCurrentStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var current = await ReadSubjectCurrentStateInTransactionAsync(connection, transaction, subjectId, cancellationToken);
        var members = await ReadDevicesForSubjectInTransactionAsync(connection, transaction, subjectId, cancellationToken);
        if (members.Count == 0) return;
        await RebuildCanonicalSubjectStateInTransactionAsync(connection, transaction, subjectId, members, current, null, updatedAt, cancellationToken);
    }

    private static async Task RebuildCanonicalSubjectStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        IReadOnlyList<NetworkDevice> members,
        SubjectCurrentState? targetState,
        SubjectCurrentState? sourceState,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var aggregate = AggregateCurrentMemberState(members);
        var preserved = targetState ?? sourceState;
        if (aggregate == PresenceState.Unknown)
        {
            if (preserved is null) return;
            aggregate = preserved.CurrentState;
        }

        var stateSince = targetState?.CurrentState == aggregate
            ? targetState.StateSince
            : sourceState?.CurrentState == aggregate
                ? sourceState.StateSince
                : await FindLatestEventBoundaryInTransactionAsync(connection, transaction, subjectId, aggregate, cancellationToken)
                    ?? FindMemberStateBoundary(members, aggregate)
                    ?? updatedAt;
        var lastObservedAt = members.Count == 0 ? updatedAt : members.Max(value => value.LastSeenAt);
        if (targetState is { } target) lastObservedAt = Max(lastObservedAt, target.LastObservedAt);
        if (sourceState is { } source) lastObservedAt = Max(lastObservedAt, source.LastObservedAt);
        var pendingOfflineSince = aggregate == PresenceState.Online
            ? targetState?.CurrentState == PresenceState.Online
                ? targetState.PendingOfflineSince
                : sourceState?.CurrentState == PresenceState.Online ? sourceState.PendingOfflineSince : null
            : null;
        await UpsertSubjectCurrentStateInTransactionAsync(connection, transaction,
            new SubjectCurrentState(subjectId, aggregate, stateSince, lastObservedAt, pendingOfflineSince), cancellationToken);
    }

    private static async Task<DateTimeOffset?> FindLatestEventBoundaryInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        PresenceState state,
        CancellationToken cancellationToken)
    {
        var events = await ReadSubjectPresenceEventsForSubjectInTransactionAsync(connection, transaction, subjectId, cancellationToken);
        return events.Where(value => EventState(value.EventType) == state)
            .OrderBy(value => value.EffectiveAt)
            .ThenBy(value => value.Id)
            .LastOrDefault()?.EffectiveAt;
    }

    private static DateTimeOffset? FindMemberStateBoundary(IReadOnlyList<NetworkDevice> members, PresenceState state)
    {
        var values = members
            .Where(value => (value.CurrentState != PresenceState.Unknown ? value.CurrentState : value.LastKnownHistoricalState ?? PresenceState.Unknown) == state)
            .Select(value => value.LastStateChangedAt ?? value.LastSeenAt)
            .ToArray();
        if (values.Length == 0) return null;
        return state == PresenceState.Online ? values.Min() : values.Max();
    }

    private static PresenceState AggregateCurrentMemberState(IReadOnlyList<NetworkDevice> members)
    {
        var observed = members.Select(value => value.CurrentState).ToArray();
        if (observed.Contains(PresenceState.Online)) return PresenceState.Online;
        if (observed.All(value => value is PresenceState.Online or PresenceState.Offline))
            return observed.Length == 0 || observed.Contains(PresenceState.Unknown) ? PresenceState.Unknown : PresenceState.Offline;
        var historical = members.Select(value => value.LastKnownHistoricalState ?? PresenceState.Unknown).ToArray();
        if (historical.Contains(PresenceState.Online)) return PresenceState.Online;
        if (historical.Length > 0 && historical.All(value => value == PresenceState.Offline)) return PresenceState.Offline;
        return PresenceState.Unknown;
    }

    private static bool RulesAreEquivalent(NotificationRule left, NotificationRule right) =>
        left.Enabled == right.Enabled
        && left.Condition == right.Condition
        && left.ThresholdSeconds == right.ThresholdSeconds
        && left.Channel == right.Channel
        && left.TargetType == right.TargetType
        && string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
        && string.Equals(left.MessageTemplate, right.MessageTemplate, StringComparison.Ordinal);

    private static PresenceState EventState(SubjectPresenceEventType type) => type switch
    {
        SubjectPresenceEventType.DetectedOnlineAfterGap or SubjectPresenceEventType.InitialOnline or SubjectPresenceEventType.ConfirmedOnline => PresenceState.Online,
        SubjectPresenceEventType.DetectedOfflineAfterGap or SubjectPresenceEventType.InitialOffline or SubjectPresenceEventType.ConfirmedOffline => PresenceState.Offline,
        _ => PresenceState.Unknown
    };

    private static async Task<IReadOnlyList<long>> ReadLongsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<long>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static async Task<long> ScalarLongInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<PresenceSubject?> ReadSubjectInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM PresenceSubject WHERE Id=$id";
        Add(command, "$id", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubject(reader) : null;
    }

    private static async Task<SubjectCurrentState?> ReadSubjectCurrentStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince FROM SubjectCurrentState WHERE SubjectId=$subject";
        Add(command, "$subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubjectCurrentState(reader) : null;
    }

    private static async Task<IReadOnlyList<NetworkDevice>> ReadDevicesForSubjectInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT d.* FROM NetworkDevice d JOIN SubjectDeviceMembership m ON m.NetworkDeviceId=d.Id WHERE m.SubjectId=$subject ORDER BY d.Id";
        Add(command, "$subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NetworkDevice>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDevice(reader));
        return result;
    }

    private static async Task<IReadOnlyList<SubjectPresenceEvent>> ReadSubjectPresenceEventsForSubjectInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM SubjectPresenceEvent WHERE SubjectId=$subject ORDER BY Id";
        Add(command, "$subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SubjectPresenceEvent>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSubjectPresenceEvent(reader));
        return result;
    }

    private static async Task<IReadOnlyList<NotificationRule>> ReadNotificationRulesForSubjectInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM NotificationRule WHERE SubjectId=$subject ORDER BY Id";
        Add(command, "$subject", subjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationRule>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationRule(reader));
        return result;
    }

    private static async Task<NotificationRuleState?> ReadNotificationRuleStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ruleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM NotificationRuleState WHERE RuleId=$rule";
        Add(command, "$rule", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotificationRuleState(reader) : null;
    }

    private static async Task UpsertSubjectCurrentStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubjectCurrentState state,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(connection, transaction,
            "INSERT INTO SubjectCurrentState(SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince) VALUES($subject,$state,$since,$observed,$pending) ON CONFLICT(SubjectId) DO UPDATE SET CurrentState=$state,StateSince=$since,LastObservedAt=$observed,PendingOfflineSince=$pending",
            [("$subject", state.SubjectId), ("$state", (int)state.CurrentState), ("$since", Time(state.StateSince)), ("$observed", Time(state.LastObservedAt)), ("$pending", state.PendingOfflineSince is null ? null : Time(state.PendingOfflineSince.Value))], cancellationToken);
    }

    private static async Task ResetNotificationRuleStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ruleId,
        long subjectId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var watermark = await GetLatestSubjectPresenceEventIdInTransactionAsync(connection, transaction, subjectId, cancellationToken) ?? 0;
        await UpsertNotificationRuleStateInTransactionAsync(connection, transaction,
            new NotificationRuleState(ruleId, null, null, false, null, false, null, null, updatedAt, watermark), cancellationToken);
    }

    private static async Task<IReadOnlyList<NotificationDelivery>> ReadNotificationDeliveriesForRuleInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ruleId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM NotificationDelivery WHERE RuleId=$rule ORDER BY Id";
        Add(command, "$rule", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationDelivery>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationDelivery(reader));
        return result;
    }

    private static async Task<IReadOnlyList<NotificationDelivery>> ReadNotificationDeliveriesForEpisodeInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string episodeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM NotificationDelivery WHERE EpisodeId=$episode ORDER BY Id";
        Add(command, "$episode", episodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NotificationDelivery>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNotificationDelivery(reader));
        return result;
    }

    private static async Task<NotificationDelivery?> ReadNotificationDeliveryInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long deliveryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM NotificationDelivery WHERE Id=$id";
        Add(command, "$id", deliveryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotificationDelivery(reader) : null;
    }

    private static async Task<long?> GetLatestSubjectPresenceEventIdInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        var value = await ScalarLongInTransactionAsync(connection, transaction,
            "SELECT MAX(Id) FROM SubjectPresenceEvent WHERE SubjectId=$subject",
            [("$subject", subjectId)], cancellationToken);
        return value == 0 ? null : value;
    }

    private static async Task UpsertNotificationRuleStateInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NotificationRuleState state,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(connection, transaction,
            "INSERT INTO NotificationRuleState(RuleId,CurrentEpisodeId,StateSince,TriggeredForCurrentEpisode,TriggeredAt,PendingDelivery,PendingDeliveryId,LastDeliveryError,UpdatedAt,LastProcessedSubjectEventId) VALUES($rule,$episode,$since,$triggered,$triggeredAt,$pending,$delivery,$error,$updated,$watermark) ON CONFLICT(RuleId) DO UPDATE SET CurrentEpisodeId=$episode,StateSince=$since,TriggeredForCurrentEpisode=$triggered,TriggeredAt=$triggeredAt,PendingDelivery=$pending,PendingDeliveryId=$delivery,LastDeliveryError=$error,UpdatedAt=$updated,LastProcessedSubjectEventId=$watermark",
            [("$rule", state.RuleId), ("$episode", state.CurrentEpisodeId), ("$since", state.StateSince is null ? null : Time(state.StateSince.Value)), ("$triggered", state.TriggeredForCurrentEpisode ? 1 : 0), ("$triggeredAt", state.TriggeredAt is null ? null : Time(state.TriggeredAt.Value)), ("$pending", state.PendingDelivery ? 1 : 0), ("$delivery", state.PendingDeliveryId), ("$error", state.LastDeliveryError), ("$updated", Time(state.UpdatedAt)), ("$watermark", state.LastProcessedSubjectEventId)], cancellationToken);
    }

    private static async Task<bool> IsEmptySubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        var value = await ScalarLongInTransactionAsync(connection, transaction,
            "SELECT NOT EXISTS (SELECT 1 FROM SubjectDeviceMembership WHERE SubjectId=$subject)",
            [("$subject", subjectId)], cancellationToken);
        return value != 0;
    }

    private static async Task<bool> SubjectHasDependentDataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long subjectId,
        CancellationToken cancellationToken)
    {
        var value = await ScalarLongInTransactionAsync(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM PresenceSubject WHERE Id=$subject AND Note IS NOT NULL AND trim(Note)<>'') OR EXISTS (SELECT 1 FROM SubjectCurrentState WHERE SubjectId=$subject) OR EXISTS (SELECT 1 FROM SubjectPresenceEvent WHERE SubjectId=$subject) OR EXISTS (SELECT 1 FROM MonitoringGapSubjectBaseline WHERE SubjectId=$subject) OR EXISTS (SELECT 1 FROM NotificationRule WHERE SubjectId=$subject) OR EXISTS (SELECT 1 FROM NotificationDelivery WHERE SubjectId=$subject) OR EXISTS (SELECT 1 FROM NotificationDelivery d JOIN NotificationRule r ON r.Id=d.RuleId WHERE r.SubjectId=$subject)",
            [("$subject", subjectId)], cancellationToken);
        return value != 0;
    }

    private static async Task<bool> HasAmbiguousEmptyIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceSubjectId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var value = await ScalarLongInTransactionAsync(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM PresenceSubject other WHERE other.Id<>$source AND lower(trim(other.DisplayName))=lower(trim($name)) AND NOT EXISTS (SELECT 1 FROM SubjectDeviceMembership m WHERE m.SubjectId=other.Id) AND ((other.Note IS NOT NULL AND trim(other.Note)<>'') OR EXISTS (SELECT 1 FROM SubjectCurrentState c WHERE c.SubjectId=other.Id) OR EXISTS (SELECT 1 FROM SubjectPresenceEvent e WHERE e.SubjectId=other.Id) OR EXISTS (SELECT 1 FROM MonitoringGapSubjectBaseline b WHERE b.SubjectId=other.Id) OR EXISTS (SELECT 1 FROM NotificationRule r WHERE r.SubjectId=other.Id) OR EXISTS (SELECT 1 FROM NotificationDelivery d WHERE d.SubjectId=other.Id) OR EXISTS (SELECT 1 FROM NotificationDelivery d JOIN NotificationRule r ON r.Id=d.RuleId WHERE r.SubjectId=other.Id)))",
            [("$source", sourceSubjectId), ("$name", displayName)], cancellationToken);
        return value != 0;
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

    private static async Task EnsureSubjectCurrentStateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasPendingOfflineSince = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(SubjectCurrentState)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                hasPendingOfflineSince |= string.Equals(reader.GetString(1), "PendingOfflineSince", StringComparison.OrdinalIgnoreCase);
        }

        if (hasPendingOfflineSince) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE SubjectCurrentState ADD COLUMN PendingOfflineSince TEXT NULL";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureNotificationRuleStateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasWatermark = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(NotificationRuleState)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                hasWatermark |= string.Equals(reader.GetString(1), "LastProcessedSubjectEventId", StringComparison.OrdinalIgnoreCase);
        }

        if (hasWatermark) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE NotificationRuleState ADD COLUMN LastProcessedSubjectEventId INTEGER NULL";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateNotificationRecipientsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var legacyRules = new List<(long RuleId, int TargetType, string TargetId)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = sqliteTransaction;
            command.CommandText = "SELECT Id,TargetType,TargetId FROM NotificationRule";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                legacyRules.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)));
        }

        foreach (var (ruleId, targetType, targetId) in legacyRules)
        {
            await ExecuteInTransactionAsync(connection, sqliteTransaction,
                "INSERT OR IGNORE INTO NotificationRecipient(Note,OpenId,TargetType,CreatedAt,UpdatedAt) VALUES($note,$openid,$type,$now,$now)",
                [("$note", "已有接收人"), ("$openid", targetId.Trim()), ("$type", targetType), ("$now", Time(DateTimeOffset.UtcNow))], cancellationToken);
            var recipientId = await ScalarLongInTransactionAsync(connection, sqliteTransaction,
                "SELECT Id FROM NotificationRecipient WHERE TargetType=$type AND OpenId=$openid LIMIT 1",
                [("$type", targetType), ("$openid", targetId.Trim())], cancellationToken);
            if (recipientId == 0) continue;
            await ExecuteInTransactionAsync(connection, sqliteTransaction,
                "INSERT OR IGNORE INTO NotificationRuleRecipient(RuleId,RecipientId,CreatedAt) VALUES($rule,$recipient,$now)",
                [("$rule", ruleId), ("$recipient", recipientId), ("$now", Time(DateTimeOffset.UtcNow))], cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task BackfillNotificationDeliveryRecipientsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            "UPDATE NotificationDelivery SET RecipientId=(SELECT r.Id FROM NotificationRecipient r WHERE r.TargetType=NotificationDelivery.TargetType AND r.OpenId=NotificationDelivery.TargetId) WHERE RecipientId IS NULL AND RuleId IS NOT NULL",
            [], cancellationToken);
        await ExecuteAsync(
            "UPDATE SystemNotificationDelivery SET RecipientId=(SELECT r.Id FROM NotificationRecipient r WHERE r.TargetType=SystemNotificationDelivery.TargetType AND r.OpenId=SystemNotificationDelivery.TargetId) WHERE RecipientId IS NULL",
            [], cancellationToken);
    }

    private static async Task MigrateSystemNotificationDeliverySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasRecipientId = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(SystemNotificationDelivery)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                hasRecipientId |= string.Equals(reader.GetString(1), "RecipientId", StringComparison.OrdinalIgnoreCase);
        }

        var hasUniqueKindEpisodeRecipient = false;
        var uniqueIndexes = new List<string>();
        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = "PRAGMA index_list(SystemNotificationDelivery)";
            await using var reader = await indexes.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(2) != 0) uniqueIndexes.Add(reader.GetString(1));
        }
        foreach (var indexName in uniqueIndexes)
        {
            await using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info(\"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            var columns = new List<string>();
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(2));
            if (columns.SequenceEqual(["Kind", "EpisodeId", "RecipientId"], StringComparer.OrdinalIgnoreCase))
            {
                hasUniqueKindEpisodeRecipient = true;
                break;
            }
        }

        if (hasRecipientId && hasUniqueKindEpisodeRecipient) return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var recipientExpression = hasRecipientId
            ? "RecipientId"
            : "(SELECT r.Id FROM NotificationRecipient r WHERE r.TargetType=SystemNotificationDelivery_Legacy.TargetType AND r.OpenId=SystemNotificationDelivery_Legacy.TargetId)";
        var rebuildSql = $"""
            DROP INDEX IF EXISTS IX_SystemNotificationDelivery_Pending;
            DROP INDEX IF EXISTS IX_SystemNotificationDelivery_Created;
            ALTER TABLE SystemNotificationDelivery RENAME TO SystemNotificationDelivery_Legacy;
            CREATE TABLE SystemNotificationDelivery_New (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind INTEGER NOT NULL, EpisodeId TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, Status INTEGER NOT NULL, DeliveredAt TEXT NULL,
                Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL, TargetId TEXT NOT NULL,
                RecipientId INTEGER NULL, Message TEXT NOT NULL, Error TEXT NULL,
                SentParts INTEGER NOT NULL DEFAULT 0, TotalParts INTEGER NOT NULL DEFAULT 0,
                LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL,
                FOREIGN KEY(RecipientId) REFERENCES NotificationRecipient(Id) ON DELETE SET NULL,
                UNIQUE(Kind,EpisodeId,RecipientId));
            INSERT INTO SystemNotificationDelivery_New(
                Id,Kind,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,RecipientId,
                Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt)
            SELECT Id,Kind,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,TargetId,
                   CASE WHEN DuplicateNumber > 1 THEN NULL ELSE RecipientId END,
                   Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt
            FROM (
                SELECT SystemNotificationDelivery_Legacy.Id,SystemNotificationDelivery_Legacy.Kind,SystemNotificationDelivery_Legacy.EpisodeId,
                       SystemNotificationDelivery_Legacy.CreatedAt,SystemNotificationDelivery_Legacy.Status,SystemNotificationDelivery_Legacy.DeliveredAt,
                       SystemNotificationDelivery_Legacy.Channel,SystemNotificationDelivery_Legacy.TargetType,SystemNotificationDelivery_Legacy.TargetId,
                       {recipientExpression} AS RecipientId,SystemNotificationDelivery_Legacy.Message,SystemNotificationDelivery_Legacy.Error,
                       SystemNotificationDelivery_Legacy.SentParts,SystemNotificationDelivery_Legacy.TotalParts,SystemNotificationDelivery_Legacy.LastAttemptAt,
                       SystemNotificationDelivery_Legacy.NextAttemptAt,
                       ROW_NUMBER() OVER (PARTITION BY Kind,EpisodeId,{recipientExpression} ORDER BY Id) AS DuplicateNumber
                FROM SystemNotificationDelivery_Legacy);
            DROP TABLE SystemNotificationDelivery_Legacy;
            ALTER TABLE SystemNotificationDelivery_New RENAME TO SystemNotificationDelivery;
            CREATE INDEX IX_SystemNotificationDelivery_Pending ON SystemNotificationDelivery(Status,NextAttemptAt);
            CREATE INDEX IX_SystemNotificationDelivery_Created ON SystemNotificationDelivery(CreatedAt DESC);
            """;
        await ExecuteInTransactionAsync(connection, sqliteTransaction, rebuildSql, [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateSubjectPresenceEventSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasStateSince = false;
        var monitoringGapIsRequired = false;
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(SubjectPresenceEvent)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                hasStateSince |= string.Equals(name, "StateSince", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(name, "MonitoringGapId", StringComparison.OrdinalIgnoreCase))
                    monitoringGapIsRequired = reader.GetInt32(3) != 0;
            }
        }

        var monitoringGapDeleteIsSetNull = false;
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_list(SubjectPresenceEvent)";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(2), "MonitoringGap", StringComparison.OrdinalIgnoreCase))
                    monitoringGapDeleteIsSetNull = string.Equals(reader.GetString(6), "SET NULL", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (hasStateSince && !monitoringGapIsRequired && monitoringGapDeleteIsSetNull) return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        await ExecuteInTransactionAsync(connection, sqliteTransaction,
            "DROP INDEX IF EXISTS IX_SubjectPresenceEvent_Subject_Observed; DROP INDEX IF EXISTS UX_SubjectPresenceEvent_Stable; ALTER TABLE SubjectPresenceEvent RENAME TO SubjectPresenceEvent_Legacy; CREATE TABLE SubjectPresenceEvent_New (Id INTEGER PRIMARY KEY AUTOINCREMENT, SubjectId INTEGER NOT NULL, EventType INTEGER NOT NULL, ObservedAt TEXT NOT NULL, MonitoringGapId INTEGER NULL, StateSince TEXT NULL, FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE, FOREIGN KEY(MonitoringGapId) REFERENCES MonitoringGap(Id) ON DELETE SET NULL); INSERT INTO SubjectPresenceEvent_New(Id,SubjectId,EventType,ObservedAt,MonitoringGapId,StateSince) SELECT Id,SubjectId,EventType,ObservedAt,MonitoringGapId,NULL FROM SubjectPresenceEvent_Legacy; DROP TABLE SubjectPresenceEvent_Legacy; ALTER TABLE SubjectPresenceEvent_New RENAME TO SubjectPresenceEvent; CREATE INDEX IX_SubjectPresenceEvent_Subject_Observed ON SubjectPresenceEvent(SubjectId,ObservedAt DESC); CREATE UNIQUE INDEX UX_SubjectPresenceEvent_Stable ON SubjectPresenceEvent(SubjectId,EventType,ObservedAt);",
            [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateNotificationDeliverySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var needsMigration = false;
        var hasRecipientId = false;
        var uniqueIndexes = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(NotificationDelivery)";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                hasRecipientId |= string.Equals(name, "RecipientId", StringComparison.OrdinalIgnoreCase);
                if (name is "RuleId" or "SubjectId") needsMigration |= reader.GetInt32(3) != 0;
            }
        }
        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = "PRAGMA index_list(NotificationDelivery)";
            await using var reader = await indexes.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(2) != 0) uniqueIndexes.Add(reader.GetString(1));
        }
        var hasUniqueRuleEpisodeRecipient = false;
        foreach (var indexName in uniqueIndexes)
        {
            await using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info(\"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            var columns = new List<string>();
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(2));
            if (columns.SequenceEqual(["RuleId", "EpisodeId", "RecipientId"], StringComparer.OrdinalIgnoreCase))
            {
                hasUniqueRuleEpisodeRecipient = true;
                break;
            }
        }
        needsMigration |= !hasRecipientId || !hasUniqueRuleEpisodeRecipient;
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_list(NotificationDelivery)";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(2); var onDelete = reader.GetString(6);
                if (table is "NotificationRule" or "PresenceSubject") needsMigration |= !string.Equals(onDelete, "SET NULL", StringComparison.OrdinalIgnoreCase);
                if (table is "NotificationRecipient") needsMigration |= !string.Equals(onDelete, "SET NULL", StringComparison.OrdinalIgnoreCase);
            }
        }
        if (!needsMigration) return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var recipientExpression = hasRecipientId
            ? "RecipientId"
            : "(SELECT r.Id FROM NotificationRecipient r WHERE r.TargetType=NotificationDelivery_Legacy.TargetType AND r.OpenId=NotificationDelivery_Legacy.TargetId)";
        var rebuildSql = $"""
            DROP INDEX IF EXISTS IX_NotificationDelivery_Pending;
            DROP INDEX IF EXISTS IX_NotificationDelivery_Created;
            ALTER TABLE NotificationDelivery RENAME TO NotificationDelivery_Legacy;
            CREATE TABLE NotificationDelivery_New (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RuleId INTEGER NULL,
                SubjectId INTEGER NULL,
                EpisodeId TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Status INTEGER NOT NULL,
                DeliveredAt TEXT NULL,
                Channel INTEGER NOT NULL,
                TargetType INTEGER NOT NULL,
                TargetId TEXT NOT NULL, RecipientId INTEGER NULL,
                Message TEXT NOT NULL,
                Error TEXT NULL,
                SentParts INTEGER NOT NULL DEFAULT 0,
                TotalParts INTEGER NOT NULL DEFAULT 0,
                LastAttemptAt TEXT NULL,
                NextAttemptAt TEXT NULL,
                FOREIGN KEY(RuleId) REFERENCES NotificationRule(Id) ON DELETE SET NULL,
                FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE SET NULL,
                FOREIGN KEY(RecipientId) REFERENCES NotificationRecipient(Id) ON DELETE SET NULL,
                UNIQUE(RuleId,EpisodeId,RecipientId));
            INSERT INTO NotificationDelivery_New(
                Id,RuleId,SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,
                TargetId,RecipientId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt)
            SELECT Id,
                   CASE WHEN RuleId IS NOT NULL AND DuplicateNumber > 1 THEN NULL ELSE RuleId END,
                   SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,
                   TargetId,
                   CASE WHEN RuleId IS NOT NULL AND DuplicateNumber > 1 THEN NULL ELSE RecipientId END,
                   Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt
            FROM (
                SELECT Id,RuleId,SubjectId,EpisodeId,CreatedAt,Status,DeliveredAt,Channel,TargetType,
                       TargetId,{recipientExpression} AS RecipientId,Message,Error,SentParts,TotalParts,LastAttemptAt,NextAttemptAt,
                       ROW_NUMBER() OVER (PARTITION BY RuleId,EpisodeId,{recipientExpression} ORDER BY Id) AS DuplicateNumber
                FROM NotificationDelivery_Legacy);
            DROP TABLE NotificationDelivery_Legacy;
            ALTER TABLE NotificationDelivery_New RENAME TO NotificationDelivery;
            CREATE UNIQUE INDEX UX_NotificationDelivery_LegacyTarget
              ON NotificationDelivery(RuleId,EpisodeId,TargetType,TargetId)
              WHERE RuleId IS NOT NULL AND RecipientId IS NULL;
            CREATE INDEX IX_NotificationDelivery_Pending ON NotificationDelivery(Status,NextAttemptAt);
            CREATE INDEX IX_NotificationDelivery_Created ON NotificationDelivery(CreatedAt DESC);
            """;
        await ExecuteInTransactionAsync(connection, sqliteTransaction, rebuildSql, [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureLegacyNotificationDeliveryUniqueIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // SQLite treats NULLs as distinct in a regular UNIQUE constraint. Keep
        // legacy (unsaved-recipient) deliveries idempotent as well, while still
        // allowing one delivery per saved recipient in the new schema. Existing
        // duplicate history is retained by detaching only the later duplicate
        // from the rule, matching the historical migration behavior.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE NotificationDelivery
            SET RuleId=NULL
            WHERE Id IN (
              SELECT Id FROM (
                SELECT Id,
                       ROW_NUMBER() OVER (
                         PARTITION BY RuleId,EpisodeId,TargetType,TargetId
                         ORDER BY Id) AS DuplicateNumber
                FROM NotificationDelivery
                WHERE RuleId IS NOT NULL AND RecipientId IS NULL)
              WHERE DuplicateNumber > 1);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_NotificationDelivery_LegacyTarget
              ON NotificationDelivery(RuleId,EpisodeId,TargetType,TargetId)
              WHERE RuleId IS NOT NULL AND RecipientId IS NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<NotificationRule>> AttachNotificationRecipientIdsAsync(
        IReadOnlyList<NotificationRule> rules,
        CancellationToken cancellationToken)
    {
        if (rules.Count == 0) return rules;
        var ids = new Dictionary<long, List<long>>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RuleId,RecipientId FROM NotificationRuleRecipient ORDER BY RuleId,CreatedAt,RecipientId";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ruleId = reader.GetInt64(0);
            if (!ids.TryGetValue(ruleId, out var values)) ids[ruleId] = values = [];
            values.Add(reader.GetInt64(1));
        }
        return rules.Select(rule => rule with { RecipientIds = ids.GetValueOrDefault(rule.Id) ?? [] }).ToArray();
    }

    private async Task<IReadOnlyList<long>> GetNotificationRuleRecipientIdsAsync(long ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RecipientId FROM NotificationRuleRecipient WHERE RuleId=$rule ORDER BY CreatedAt,RecipientId";
        Add(command, "$rule", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<long>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static async Task InsertNotificationRuleRecipientsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ruleId,
        IReadOnlyCollection<long> recipientIds,
        CancellationToken cancellationToken,
        bool replace = false)
    {
        if (replace)
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM NotificationRuleRecipient WHERE RuleId=$rule", [("$rule", ruleId)], cancellationToken);

        foreach (var recipientId in recipientIds.Distinct())
        {
            var exists = await ScalarLongInTransactionAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM NotificationRecipient WHERE Id=$id)", [("$id", recipientId)], cancellationToken);
            if (exists == 0) throw new ArgumentException($"QQ 接收人 {recipientId} 不存在。", nameof(recipientIds));
            await ExecuteInTransactionAsync(connection, transaction,
                "INSERT OR IGNORE INTO NotificationRuleRecipient(RuleId,RecipientId,CreatedAt) VALUES($rule,$recipient,$created)",
                [("$rule", ruleId), ("$recipient", recipientId), ("$created", Time(DateTimeOffset.UtcNow))], cancellationToken);
        }
    }

    private static NotificationRecipient NormalizeRecipient(NotificationRecipient value)
    {
        if (value.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group))
            throw new ArgumentOutOfRangeException(nameof(value), "QQ 接收人类型无效。");
        var openId = value.OpenId.Trim();
        if (openId.Length is 0 or > 256 || openId.Any(char.IsWhiteSpace))
            throw new ArgumentException("QQ OpenID 无效。", nameof(value));
        var note = value.Note?.Trim() ?? string.Empty;
        if (note.Length > 120) throw new ArgumentException("接收人备注不能超过 120 个字符。", nameof(value));
        return value with { OpenId = openId, Note = note };
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

    private static Task CloseOpenSessionsAtAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset boundary, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(connection, transaction,
            "UPDATE PresenceSession SET EndedAt=$end,EndKnown=0 WHERE EndedAt IS NULL AND StartedAt <= $end",
            [("$end", Time(boundary))], cancellationToken);

    private static NotificationRule NormalizeRule(NotificationRule value)
    {
        if (value.SubjectId <= 0) throw new ArgumentException("通知规则必须绑定到主体。", nameof(value));
        if (value.Condition is not (NotificationCondition.OnlineFor or NotificationCondition.OfflineFor or NotificationCondition.DetectedOnline or NotificationCondition.DetectedOffline)) throw new ArgumentOutOfRangeException(nameof(value), "通知条件无效。");
        var isDurationCondition = value.Condition is NotificationCondition.OnlineFor or NotificationCondition.OfflineFor;
        if (isDurationCondition && (value.ThresholdSeconds < 60 || value.ThresholdSeconds > 365 * 24 * 60 * 60)) throw new ArgumentOutOfRangeException(nameof(value), "通知时长必须在 1 分钟到 365 天之间。");
        if (value.Channel != NotificationChannelType.QQ) throw new ArgumentOutOfRangeException(nameof(value), "当前只支持 QQ 通知。");
        if (value.TargetType is not (NotificationTargetType.Private or NotificationTargetType.Group)) throw new ArgumentOutOfRangeException(nameof(value), "QQ 通知目标类型无效。");
        var target = value.TargetId.Trim();
        if (target.Length is 0 or > 256 || target.Any(char.IsWhiteSpace)) throw new ArgumentException("QQ 目标 OpenID 无效。", nameof(value));
        var template = value.MessageTemplate?.Trim() ?? string.Empty;
        if (template.Length > 10_000) throw new ArgumentException("通知内容过长。", nameof(value));
        return value with { TargetId = target, MessageTemplate = template, ThresholdSeconds = isDurationCondition ? value.ThresholdSeconds : 0 };
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
        Add(command, "$targetType", (int)value.TargetType); Add(command, "$target", value.TargetId); Add(command, "$recipient", value.RecipientId); Add(command, "$message", value.Message);
        Add(command, "$error", value.Error); Add(command, "$sent", value.SentParts); Add(command, "$total", value.TotalParts);
        Add(command, "$last", value.LastAttemptAt is null ? null : Time(value.LastAttemptAt.Value)); Add(command, "$next", value.NextAttemptAt is null ? null : Time(value.NextAttemptAt.Value));
    }

    private static void AddSystemDeliveryParameters(SqliteCommand command, SystemNotificationDelivery value)
    {
        Add(command, "$kind", (int)value.Kind); Add(command, "$episode", value.EpisodeId); Add(command, "$created", Time(value.CreatedAt)); Add(command, "$status", (int)value.Status);
        Add(command, "$delivered", value.DeliveredAt is null ? null : Time(value.DeliveredAt.Value)); Add(command, "$channel", (int)value.Channel); Add(command, "$targetType", (int)value.TargetType); Add(command, "$target", value.TargetId); Add(command, "$recipient", value.RecipientId); Add(command, "$message", value.Message); Add(command, "$error", value.Error); Add(command, "$sent", value.SentParts); Add(command, "$total", value.TotalParts);
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

    private static PresenceState AggregateHistoricalState(IEnumerable<NetworkDevice> members)
    {
        var states = members.Select(value => value.LastKnownHistoricalState ?? value.CurrentObservedState).ToArray();
        if (states.Contains(PresenceState.Online)) return PresenceState.Online;
        if (states.Contains(PresenceState.Unknown)) return PresenceState.Unknown;
        return PresenceState.Offline;
    }

    private static Router ReadRouter(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetString(reader.GetOrdinal("MiotDid")), reader.GetString(reader.GetOrdinal("MiotModel")), reader.GetString(reader.GetOrdinal("PartnerId")), reader.GetString(reader.GetOrdinal("Name")), Text(reader, "HomeId"), Text(reader, "RoomId"), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("LastSeenAt"))));
    private static PresenceSubject ReadSubject(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), Guid.Parse(reader.GetString(reader.GetOrdinal("ExportId"))), reader.GetString(reader.GetOrdinal("DisplayName")), Text(reader, "Note"), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    private static SubjectCurrentState ReadSubjectCurrentState(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("SubjectId")),
        (PresenceState)reader.GetInt32(reader.GetOrdinal("CurrentState")),
        ParseTime(reader.GetString(reader.GetOrdinal("StateSince"))),
        ParseTime(reader.GetString(reader.GetOrdinal("LastObservedAt"))),
        Text(reader, "PendingOfflineSince") is { } pending ? ParseTime(pending) : null);
    private static NetworkDevice ReadDevice(SqliteDataReader reader)
    {
        var device = new NetworkDevice(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetInt64(reader.GetOrdinal("RouterId")), reader.GetString(reader.GetOrdinal("MacAddress")), Text(reader, "OriginalName"), Text(reader, "OriginName"), Text(reader, "CustomName"), Text(reader, "Note"), Text(reader, "LastIp"), Text(reader, "ConnectionType"), Integer(reader, "Signal"), (PresenceState)reader.GetInt32(reader.GetOrdinal("CurrentState")), ParseTime(reader.GetString(reader.GetOrdinal("FirstSeenAt"))), ParseTime(reader.GetString(reader.GetOrdinal("LastSeenAt"))), Text(reader, "LastStateChangedAt") is { } changed ? ParseTime(changed) : null);
        return device with { LastKnownHistoricalState = (PresenceState)reader.GetInt32(reader.GetOrdinal("LastKnownHistoricalState")) };
    }
    private static NotificationRule ReadNotificationRule(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.GetInt64(reader.GetOrdinal("SubjectId")), reader.GetInt32(reader.GetOrdinal("Enabled")) != 0, (NotificationCondition)reader.GetInt32(reader.GetOrdinal("RuleCondition")), reader.GetInt64(reader.GetOrdinal("ThresholdSeconds")), (NotificationChannelType)reader.GetInt32(reader.GetOrdinal("Channel")), (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")), reader.GetString(reader.GetOrdinal("TargetId")), reader.GetString(reader.GetOrdinal("MessageTemplate")), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    private static NotificationRecipient ReadNotificationRecipient(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("Id")),
        reader.GetString(reader.GetOrdinal("Note")),
        reader.GetString(reader.GetOrdinal("OpenId")),
        (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")),
        ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))));
    private static SubjectPresenceEvent ReadSubjectPresenceEvent(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("Id")),
        reader.GetInt64(reader.GetOrdinal("SubjectId")),
        (SubjectPresenceEventType)reader.GetInt32(reader.GetOrdinal("EventType")),
        ParseTime(reader.GetString(reader.GetOrdinal("ObservedAt"))),
        reader.IsDBNull(reader.GetOrdinal("MonitoringGapId")) ? null : reader.GetInt64(reader.GetOrdinal("MonitoringGapId")),
        Text(reader, "StateSince") is { } since ? ParseTime(since) : null);
    private static NotificationRuleState ReadNotificationRuleState(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("RuleId")),
        Text(reader, "CurrentEpisodeId"),
        Text(reader, "StateSince") is { } since ? ParseTime(since) : null,
        reader.GetInt32(reader.GetOrdinal("TriggeredForCurrentEpisode")) != 0,
        Text(reader, "TriggeredAt") is { } triggered ? ParseTime(triggered) : null,
        reader.GetInt32(reader.GetOrdinal("PendingDelivery")) != 0,
        reader.IsDBNull(reader.GetOrdinal("PendingDeliveryId")) ? null : reader.GetInt64(reader.GetOrdinal("PendingDeliveryId")),
        Text(reader, "LastDeliveryError"),
        ParseTime(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
        reader.IsDBNull(reader.GetOrdinal("LastProcessedSubjectEventId")) ? null : reader.GetInt64(reader.GetOrdinal("LastProcessedSubjectEventId")));
    private static NotificationDelivery ReadNotificationDelivery(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), reader.IsDBNull(reader.GetOrdinal("RuleId")) ? null : reader.GetInt64(reader.GetOrdinal("RuleId")), reader.IsDBNull(reader.GetOrdinal("SubjectId")) ? null : reader.GetInt64(reader.GetOrdinal("SubjectId")), reader.GetString(reader.GetOrdinal("EpisodeId")), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), (NotificationDeliveryStatus)reader.GetInt32(reader.GetOrdinal("Status")), Text(reader, "DeliveredAt") is { } delivered ? ParseTime(delivered) : null, (NotificationChannelType)reader.GetInt32(reader.GetOrdinal("Channel")), (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")), reader.GetString(reader.GetOrdinal("TargetId")), reader.GetString(reader.GetOrdinal("Message")), Text(reader, "Error"), reader.GetInt32(reader.GetOrdinal("SentParts")), reader.GetInt32(reader.GetOrdinal("TotalParts")), Text(reader, "LastAttemptAt") is { } attempted ? ParseTime(attempted) : null, Text(reader, "NextAttemptAt") is { } next ? ParseTime(next) : null, reader.IsDBNull(reader.GetOrdinal("RecipientId")) ? null : reader.GetInt64(reader.GetOrdinal("RecipientId")));
    private static SystemNotificationDelivery ReadSystemNotificationDelivery(SqliteDataReader reader) => new(reader.GetInt64(reader.GetOrdinal("Id")), (SystemNotificationKind)reader.GetInt32(reader.GetOrdinal("Kind")), reader.GetString(reader.GetOrdinal("EpisodeId")), ParseTime(reader.GetString(reader.GetOrdinal("CreatedAt"))), (NotificationDeliveryStatus)reader.GetInt32(reader.GetOrdinal("Status")), Text(reader, "DeliveredAt") is { } delivered ? ParseTime(delivered) : null, (NotificationChannelType)reader.GetInt32(reader.GetOrdinal("Channel")), (NotificationTargetType)reader.GetInt32(reader.GetOrdinal("TargetType")), reader.GetString(reader.GetOrdinal("TargetId")), reader.GetString(reader.GetOrdinal("Message")), Text(reader, "Error"), reader.GetInt32(reader.GetOrdinal("SentParts")), reader.GetInt32(reader.GetOrdinal("TotalParts")), Text(reader, "LastAttemptAt") is { } attempted ? ParseTime(attempted) : null, Text(reader, "NextAttemptAt") is { } next ? ParseTime(next) : null, reader.IsDBNull(reader.GetOrdinal("RecipientId")) ? null : reader.GetInt64(reader.GetOrdinal("RecipientId")));
    private static string? Text(SqliteDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetString(i); }
    private static int? Integer(SqliteDataReader reader, string name) { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : reader.GetInt32(i); }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) => left is null ? right : right is null ? left : Max(left.Value, right.Value);
    private static long? Max(long? left, long? right) => left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
}
