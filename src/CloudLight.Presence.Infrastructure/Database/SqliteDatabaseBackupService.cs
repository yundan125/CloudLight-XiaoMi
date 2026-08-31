using System.Text.Json;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace CloudLight.Presence.Infrastructure.Database;

public sealed record DatabaseMigrationInspection(
    int CurrentSchemaVersion,
    int TargetSchemaVersion,
    bool HasExistingSchema,
    bool NeedsMigration);

public sealed record DatabaseBackupStatus(
    string? LastMigrationBackupPath = null,
    DateTimeOffset? LastMigrationBackupAt = null,
    string? LastManualBackupPath = null,
    DateTimeOffset? LastManualBackupAt = null,
    string? LastFailure = null,
    DateTimeOffset? LastFailureAt = null);

/// <summary>
/// Owns safe SQLite snapshots and their small, non-sensitive status record.
/// The SQLite backup API reads the live database consistently, including
/// pages that are currently represented in WAL; no -wal/-shm file is copied.
/// </summary>
public sealed class SqliteDatabaseBackupService(IAppDataPaths paths)
{
    public const int CurrentSchemaVersion = 14;
    private const int MigrationBackupRetention = 10;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string StatusPath => Path.Combine(paths.RootDirectory, "database-backup-status.json");

    public static async Task<DatabaseMigrationInspection> InspectAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken);
        var tables = await ReadTablesAsync(connection, cancellationToken);
        var hasExistingSchema = tables.Count > 0;
        if (!hasExistingSchema)
            return new(version, CurrentSchemaVersion, false, false);

        var requiredTables = new[]
        {
            "Router", "NetworkDevice", "PresenceEvent", "PresenceSession", "MonitoringGap", "MonitoringGapSubjectBaseline", "ApplicationRun",
            "PresenceSubject", "SubjectCurrentState", "SubjectPresenceEvent", "SubjectDeviceMembership",
            "NotificationRule", "NotificationRecipient", "NotificationRuleRecipient", "NotificationRuleState",
            "NotificationDelivery", "ConnectionAlertState", "SystemNotificationDelivery",
            "RouterCapabilityDiagnostic"
        };
        var missingTable = requiredTables.Any(value => !tables.Contains(value));
        var missingColumn = missingTable ||
            !await HasColumnAsync(connection, "NetworkDevice", "LastKnownHistoricalState", cancellationToken) ||
            !await HasColumnAsync(connection, "SubjectCurrentState", "PendingOfflineSince", cancellationToken) ||
            !await HasColumnAsync(connection, "SubjectPresenceEvent", "StateSince", cancellationToken) ||
            !await HasColumnAsync(connection, "MonitoringGap", "RouterId", cancellationToken) ||
            !await HasColumnAsync(connection, "NotificationRuleState", "LastProcessedSubjectEventId", cancellationToken) ||
            !await HasColumnAsync(connection, "NotificationDelivery", "RecipientId", cancellationToken) ||
            !await HasColumnAsync(connection, "SystemNotificationDelivery", "RecipientId", cancellationToken);

        var needsStructuralMigration = missingColumn;
        if (!needsStructuralMigration)
        {
            // These are the same structural predicates used by the rebuild
            // migrations below.  A database with a stale user_version must
            // still be snapshotted when a previous interrupted/manual
            // migration left one of these constraints behind.
            needsStructuralMigration =
                !await HasNullableColumnAsync(connection, "SubjectPresenceEvent", "MonitoringGapId", cancellationToken) ||
                !await HasForeignKeyActionAsync(connection, "SubjectPresenceEvent", "MonitoringGap", "SET NULL", cancellationToken) ||
                !await HasMonitoringGapStableIndexAsync(connection, cancellationToken) ||
                !await HasUniqueIndexAsync(connection, "SystemNotificationDelivery", ["Kind", "EpisodeId", "RecipientId"], cancellationToken) ||
                !await HasNullableColumnAsync(connection, "NotificationDelivery", "RuleId", cancellationToken) ||
                !await HasNullableColumnAsync(connection, "NotificationDelivery", "SubjectId", cancellationToken) ||
                !await HasUniqueIndexAsync(connection, "NotificationDelivery", ["RuleId", "EpisodeId", "RecipientId"], cancellationToken) ||
                !await HasForeignKeyActionAsync(connection, "NotificationDelivery", "NotificationRule", "SET NULL", cancellationToken) ||
                !await HasForeignKeyActionAsync(connection, "NotificationDelivery", "PresenceSubject", "SET NULL", cancellationToken) ||
                !await HasForeignKeyActionAsync(connection, "NotificationDelivery", "NotificationRecipient", "SET NULL", cancellationToken) ||
                !await HasUniqueIndexAsync(connection, "NotificationDelivery", ["RuleId", "EpisodeId", "TargetType", "TargetId"], cancellationToken);
        }

        return new(version, CurrentSchemaVersion, true, version < CurrentSchemaVersion || needsStructuralMigration);
    }

    public async Task<DatabaseBackupStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatusPath)) return new();
        try
        {
            await using var stream = File.OpenRead(StatusPath);
            return await JsonSerializer.DeserializeAsync<DatabaseBackupStatus>(stream, JsonOptions, cancellationToken) ?? new();
        }
        catch
        {
            return new(LastFailure: "数据库备份状态文件无法读取。", LastFailureAt: DateTimeOffset.UtcNow);
        }
    }

    public async Task<string> CreateMigrationBackupAsync(
        SqliteConnection source,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        var fileName = $"presence-before-migration-v{Math.Max(0, fromVersion)}-to-v{CurrentSchemaVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        return await CreateBackupFromConnectionAsync(source, fileName, migration: true, cancellationToken);
    }

    public async Task<string> CreateManualBackupAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.DatabasePath)) throw new FileNotFoundException("当前数据库不存在。", paths.DatabasePath);
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            await using var source = new SqliteConnection(sourceConnectionString);
            await source.OpenAsync(cancellationToken);
            var fileName = $"presence-manual-{DateTime.Now:yyyyMMdd-HHmmss}.db";
            return await CreateBackupFromConnectionCoreAsync(source, fileName, migration: false, cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordFailureCoreAsync(exception, CancellationToken.None);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordMigrationFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await RecordFailureCoreAsync(exception, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<DatabaseBackupStatus> GetLatestStatusAsync(CancellationToken cancellationToken) =>
        await GetStatusAsync(cancellationToken);

    public static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> CreateBackupFromConnectionAsync(
        SqliteConnection source,
        string fileName,
        bool migration,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await CreateBackupFromConnectionCoreAsync(source, fileName, migration, cancellationToken); }
        catch (Exception exception)
        {
            await RecordFailureCoreAsync(exception, CancellationToken.None);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> CreateBackupFromConnectionCoreAsync(
        SqliteConnection source,
        string fileName,
        bool migration,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.BackupsDirectory);
        var targetPath = UniquePath(Path.Combine(paths.BackupsDirectory, fileName));
        var targetConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        try
        {
            await using var target = new SqliteConnection(targetConnectionString);
            await target.OpenAsync(cancellationToken);
            // Keep the snapshot itself in the ordinary rollback-journal mode.
            // The source may be in WAL mode, but a backup artifact must not
            // depend on a sibling -wal/-shm file after this method returns.
            await using (var journal = target.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                await journal.ExecuteNonQueryAsync(cancellationToken);
            }
            source.BackupDatabase(target);
            await using (var checkpoint = target.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var check = target.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite 备份校验失败：{result}");

            var now = DateTimeOffset.UtcNow;
            var current = await GetStatusAsync(CancellationToken.None);
            var updated = migration
                ? current with { LastMigrationBackupPath = targetPath, LastMigrationBackupAt = now, LastFailure = null, LastFailureAt = null }
                : current with { LastManualBackupPath = targetPath, LastManualBackupAt = now, LastFailure = null, LastFailureAt = null };
            await SaveStatusAsync(updated, CancellationToken.None);
            if (migration) RotateMigrationBackups(targetPath);
            return targetPath;
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
    }

    private async Task RecordFailureCoreAsync(Exception exception, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var current = await GetStatusAsync(CancellationToken.None);
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 500) message = message[..500];
        await SaveStatusAsync(current with { LastFailure = message, LastFailureAt = DateTimeOffset.UtcNow }, cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            var line = $"{DateTimeOffset.UtcNow:O}\tERROR\tdatabase-backup\t{message}{Environment.NewLine}";
            await File.AppendAllTextAsync(Path.Combine(paths.LogsDirectory, "database-backup.log"), line, cancellationToken);
        }
        catch
        {
            // The JSON status is the primary diagnostic channel. A logging
            // failure must not hide the original backup/migration failure.
        }
    }

    private async Task SaveStatusAsync(DatabaseBackupStatus status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var temporary = StatusPath + ".new";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, status, JsonOptions, cancellationToken);
        File.Move(temporary, StatusPath, overwrite: true);
    }

    private void RotateMigrationBackups(string newestPath)
    {
        if (!Directory.Exists(paths.BackupsDirectory)) return;
        var backups = Directory.EnumerateFiles(paths.BackupsDirectory, "presence-before-migration-v*-to-v*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        foreach (var old in backups.Skip(MigrationBackupRetention))
        {
            if (string.Equals(old, newestPath, StringComparison.OrdinalIgnoreCase)) continue;
            TryDelete(old);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var index = 2;
        string candidate;
        do { candidate = Path.Combine(directory, $"{stem}-{index++}.db"); } while (File.Exists(candidate));
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task<HashSet<string>> ReadTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<bool> HasNullableColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return reader.GetInt32(3) == 0;
        return false;
    }

    private static async Task<bool> HasForeignKeyActionAsync(
        SqliteConnection connection,
        string table,
        string referencedTable,
        string deleteAction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(2), referencedTable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reader.GetString(6), deleteAction, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<bool> HasUniqueIndexAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> expectedColumns,
        CancellationToken cancellationToken)
    {
        var indexes = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(2) != 0) indexes.Add(reader.GetString(1));
        }

        foreach (var index in indexes)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA index_info(\"{index.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = new List<string>();
            while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(2));
            if (columns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<bool> HasMonitoringGapStableIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='UX_MonitoringGap_Stable' AND tbl_name='MonitoringGap'";
        var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var normalized = sql.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        return normalized.Contains("IFNULL(ROUTERID,0),STARTEDAT,REASON", StringComparison.Ordinal);
    }
}
