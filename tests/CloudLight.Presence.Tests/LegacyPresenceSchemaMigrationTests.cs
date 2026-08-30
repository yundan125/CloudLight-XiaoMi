using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class LegacyPresenceSchemaMigrationTests
{
    [Fact]
    public async Task ReopeningLegacySubjectStateAndEventTablesPreservesHistoryAndAddsGraceColumns()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Legacy-Migration-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE PresenceSubject (Id INTEGER PRIMARY KEY, ExportId TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL, Note TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                    CREATE TABLE MonitoringGap (Id INTEGER PRIMARY KEY, StartedAt TEXT NOT NULL, EndedAt TEXT NULL, Reason TEXT NOT NULL);
                    CREATE TABLE SubjectCurrentState (SubjectId INTEGER PRIMARY KEY, CurrentState INTEGER NOT NULL, StateSince TEXT NOT NULL, LastObservedAt TEXT NOT NULL);
                    CREATE TABLE SubjectPresenceEvent (Id INTEGER PRIMARY KEY, SubjectId INTEGER NOT NULL, EventType INTEGER NOT NULL, ObservedAt TEXT NOT NULL, MonitoringGapId INTEGER NOT NULL, FOREIGN KEY(SubjectId) REFERENCES PresenceSubject(Id) ON DELETE CASCADE, FOREIGN KEY(MonitoringGapId) REFERENCES MonitoringGap(Id) ON DELETE CASCADE);
                    INSERT INTO PresenceSubject(Id,ExportId,DisplayName,CreatedAt,UpdatedAt) VALUES(1,'00000000-0000-0000-0000-000000000001','历史主体','2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');
                    INSERT INTO MonitoringGap(Id,StartedAt,EndedAt,Reason) VALUES(1,'2026-08-29T01:00:00.0000000+00:00','2026-08-29T02:00:00.0000000+00:00','legacy');
                    INSERT INTO SubjectCurrentState(SubjectId,CurrentState,StateSince,LastObservedAt) VALUES(1,1,'2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:30:00.0000000+00:00');
                    INSERT INTO SubjectPresenceEvent(Id,SubjectId,EventType,ObservedAt,MonitoringGapId) VALUES(1,1,2,'2026-08-29T02:00:00.0000000+00:00',1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var state = await repository.GetSubjectCurrentStateAsync(1, CancellationToken.None);
            var history = await repository.GetSubjectPresenceEventsAsync(1, DateTimeOffset.Parse("2026-08-28T00:00:00Z"), DateTimeOffset.Parse("2026-08-30T00:00:00Z"), CancellationToken.None);

            Assert.NotNull(state);
            Assert.Equal(PresenceState.Online, state!.CurrentState);
            Assert.Null(state.PendingOfflineSince);
            var eventRecord = Assert.Single(history);
            Assert.Equal(1, eventRecord.Id);
            Assert.Equal(1, eventRecord.MonitoringGapId);
            Assert.Null(eventRecord.StateSince);

            await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
            await verify.OpenAsync();
            await using var columns = verify.CreateCommand();
            columns.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('SubjectCurrentState');";
            var stateColumns = (string?)await columns.ExecuteScalarAsync();
            Assert.Contains("PendingOfflineSince", stateColumns, StringComparison.Ordinal);
            columns.CommandText = "SELECT group_concat(name, ',') FROM pragma_table_info('SubjectPresenceEvent');";
            var eventColumns = (string?)await columns.ExecuteScalarAsync();
            Assert.Contains("StateSince", eventColumns, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyNotificationDeliveriesGainRuleEpisodeUniquenessWithoutDroppingAuditRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "CloudLight-Legacy-Delivery-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE PresenceSubject (Id INTEGER PRIMARY KEY, ExportId TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL, Note TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                    INSERT INTO PresenceSubject(Id,ExportId,DisplayName,CreatedAt,UpdatedAt) VALUES(1,'00000000-0000-0000-0000-000000000011','历史主体','2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');
                    CREATE TABLE NotificationRule (Id INTEGER PRIMARY KEY, SubjectId INTEGER NOT NULL, Enabled INTEGER NOT NULL, RuleCondition INTEGER NOT NULL, ThresholdSeconds INTEGER NOT NULL, Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL, TargetId TEXT NOT NULL, MessageTemplate TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                    INSERT INTO NotificationRule(Id,SubjectId,Enabled,RuleCondition,ThresholdSeconds,Channel,TargetType,TargetId,MessageTemplate,CreatedAt,UpdatedAt) VALUES(1,1,1,3,0,1,1,'target','历史','2026-08-29T00:00:00.0000000+00:00','2026-08-29T00:00:00.0000000+00:00');
                    CREATE TABLE NotificationDelivery (Id INTEGER PRIMARY KEY, RuleId INTEGER NOT NULL, SubjectId INTEGER NOT NULL, EpisodeId TEXT NOT NULL, CreatedAt TEXT NOT NULL, Status INTEGER NOT NULL, DeliveredAt TEXT NULL, Channel INTEGER NOT NULL, TargetType INTEGER NOT NULL, TargetId TEXT NOT NULL, Message TEXT NOT NULL, Error TEXT NULL, SentParts INTEGER NOT NULL DEFAULT 0, TotalParts INTEGER NOT NULL DEFAULT 0, LastAttemptAt TEXT NULL, NextAttemptAt TEXT NULL);
                    INSERT INTO NotificationDelivery(Id,RuleId,SubjectId,EpisodeId,CreatedAt,Status,Channel,TargetType,TargetId,Message) VALUES(1,1,1,'event:7','2026-08-29T01:00:00.0000000+00:00',2,1,1,'target','第一条');
                    INSERT INTO NotificationDelivery(Id,RuleId,SubjectId,EpisodeId,CreatedAt,Status,Channel,TargetType,TargetId,Message) VALUES(2,1,1,'event:7','2026-08-29T01:01:00.0000000+00:00',2,1,1,'target','第二条');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqlitePresenceRepository(paths);
            await repository.InitializeAsync(CancellationToken.None);
            var history = await repository.GetRecentNotificationDeliveriesAsync(10, CancellationToken.None);

            Assert.Equal(2, history.Count);
            Assert.Equal(1, history.Count(value => value.RuleId == 1));
            Assert.Equal(1, history.Count(value => value.RuleId is null));
            Assert.Equal(2, history.Count(value => value.EpisodeId == "event:7"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
