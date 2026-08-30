using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Presence;
using CloudLight.Presence.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace CloudLight.Presence.Infrastructure.Database;

public sealed record ImportResult(int AddedDevices, int UpdatedDevices, int AddedEvents, int SkippedDuplicates);

public sealed class PresenceDataTransferService(IAppDataPaths paths)
{
    private const string Format = "CloudLight.Presence.Export";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString();

    public async Task ExportAsync(string targetPath, CancellationToken cancellationToken)
    {
        var model = new ExportDocument(
            new ExportManifest(Format, 2, DateTimeOffset.UtcNow, Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "development", false),
            [], [], [], [], [], [], [], [], [], [], []);
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ReadAsync(connection, "SELECT MiotDid,MiotModel,PartnerId,Name,HomeId,RoomId,CreatedAt,LastSeenAt FROM Router", reader =>
            model.Routers.Add(new ExportRouter(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4), Text(reader, 5), Time(reader, 6), Time(reader, 7))), cancellationToken);
        await ReadAsync(connection, "SELECT r.MiotDid,d.MacAddress,d.OriginalName,d.OriginName,d.CustomName,d.Note,d.LastIp,d.ConnectionType,d.Signal,d.CurrentState,d.FirstSeenAt,d.LastSeenAt,d.LastStateChangedAt,d.LastKnownHistoricalState FROM NetworkDevice d JOIN Router r ON r.Id=d.RouterId", reader =>
            model.Devices.Add(new ExportDevice(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.GetInt32(9), Time(reader, 10), Time(reader, 11), NullableTime(reader, 12), reader.IsDBNull(13) ? null : reader.GetInt32(13))), cancellationToken);
        await ReadAsync(connection, "SELECT r.MiotDid,d.MacAddress,e.EventType,e.ObservedAt,e.Source FROM PresenceEvent e JOIN NetworkDevice d ON d.Id=e.DeviceId JOIN Router r ON r.Id=d.RouterId", reader =>
            model.Events.Add(new ExportEvent(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), Time(reader, 3), reader.GetInt32(4))), cancellationToken);
        await ReadAsync(connection, "SELECT r.MiotDid,d.MacAddress,s.StartedAt,s.EndedAt,s.StartKnown,s.EndKnown FROM PresenceSession s JOIN NetworkDevice d ON d.Id=s.DeviceId JOIN Router r ON r.Id=d.RouterId", reader =>
            model.Sessions.Add(new ExportSession(reader.GetString(0), reader.GetString(1), Time(reader, 2), NullableTime(reader, 3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0)), cancellationToken);
        await ReadAsync(connection, "SELECT StartedAt,EndedAt,Reason FROM MonitoringGap", reader =>
            model.MonitoringGaps.Add(new ExportGap(Time(reader, 0), NullableTime(reader, 1), reader.GetString(2))), cancellationToken);
        await ReadAsync(connection, "SELECT ExportId,DisplayName,Note,CreatedAt,UpdatedAt FROM PresenceSubject", reader =>
            model.Subjects!.Add(new ExportSubject(Guid.Parse(reader.GetString(0)), reader.GetString(1), Text(reader, 2), Time(reader, 3), Time(reader, 4))), cancellationToken);
        await ReadAsync(connection, "SELECT s.ExportId,r.MiotDid,d.MacAddress,m.CreatedAt FROM SubjectDeviceMembership m JOIN PresenceSubject s ON s.Id=m.SubjectId JOIN NetworkDevice d ON d.Id=m.NetworkDeviceId JOIN Router r ON r.Id=d.RouterId", reader =>
            model.SubjectDeviceMemberships!.Add(new ExportSubjectDeviceMembership(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), Time(reader, 3))), cancellationToken);
        await ReadAsync(connection, "SELECT s.ExportId,c.CurrentState,c.StateSince,c.LastObservedAt,c.PendingOfflineSince FROM SubjectCurrentState c JOIN PresenceSubject s ON s.Id=c.SubjectId", reader =>
            model.SubjectCurrentStates!.Add(new ExportSubjectCurrentState(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), Time(reader, 2), Time(reader, 3), NullableTime(reader, 4))), cancellationToken);
        await ReadAsync(connection, "SELECT Note,OpenId,TargetType,CreatedAt,UpdatedAt FROM NotificationRecipient", reader =>
            model.NotificationRecipients!.Add(new ExportNotificationRecipient(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), Time(reader, 3), Time(reader, 4))), cancellationToken);
        var ruleTargets = new Dictionary<long, List<ExportNotificationRecipientTarget>>();
        await ReadAsync(connection, "SELECT rr.RuleId,n.TargetType,n.OpenId FROM NotificationRuleRecipient rr JOIN NotificationRecipient n ON n.Id=rr.RecipientId", reader =>
        {
            if (!ruleTargets.TryGetValue(reader.GetInt64(0), out var values)) ruleTargets[reader.GetInt64(0)] = values = [];
            values.Add(new ExportNotificationRecipientTarget(reader.GetInt32(1), reader.GetString(2)));
        }, cancellationToken);
        await ReadAsync(connection, "SELECT r.Id,s.ExportId,r.Enabled,r.RuleCondition,r.ThresholdSeconds,r.Channel,r.TargetType,r.TargetId,r.MessageTemplate,r.CreatedAt,r.UpdatedAt FROM NotificationRule r JOIN PresenceSubject s ON s.Id=r.SubjectId", reader =>
            model.NotificationRules!.Add(new ExportNotificationRule(Guid.Parse(reader.GetString(1)), reader.GetInt32(2) != 0, reader.GetInt32(3), reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8), Time(reader, 9), Time(reader, 10), reader.GetInt64(0), ruleTargets.GetValueOrDefault(reader.GetInt64(0)))), cancellationToken);
        await ReadAsync(connection, "SELECT s.ExportId,e.EventType,e.ObservedAt,g.StartedAt,g.Reason,e.StateSince FROM SubjectPresenceEvent e JOIN PresenceSubject s ON s.Id=e.SubjectId LEFT JOIN MonitoringGap g ON g.Id=e.MonitoringGapId", reader =>
            model.SubjectPresenceEvents!.Add(new ExportSubjectPresenceEvent(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), Time(reader, 2), NullableTime(reader, 3), Text(reader, 4), NullableTime(reader, 5))), cancellationToken);
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
                id = await InsertIdAsync(connection, "INSERT INTO NetworkDevice(RouterId,MacAddress,OriginalName,OriginName,CustomName,Note,LastIp,ConnectionType,Signal,CurrentState,FirstSeenAt,LastSeenAt,LastStateChangedAt,LastKnownHistoricalState) VALUES($router,$mac,$original,$origin,$custom,$note,$ip,$connection,$signal,$state,$first,$last,$changed,$historical)", DeviceParameters(device, routerId, mac), cancellationToken); addedDevices++;
            }
            else
            {
                var existing = await ReadDeviceRuntimeAsync(connection, id, cancellationToken);
                var merged = MergeDeviceRuntime(existing, device);
                await ExecuteAsync(connection, "UPDATE NetworkDevice SET CurrentState=$state,FirstSeenAt=$first,LastSeenAt=$last,LastStateChangedAt=$changed,LastKnownHistoricalState=$historical,CustomName=CASE WHEN CustomName IS NULL OR trim(CustomName)='' THEN $custom ELSE CustomName END,Note=CASE WHEN Note IS NULL OR trim(Note)='' THEN $note ELSE Note END WHERE Id=$id",
                    [("$state", (int)merged.CurrentState), ("$first", Iso(merged.FirstSeenAt)), ("$last", Iso(merged.LastSeenAt)), ("$changed", merged.LastStateChangedAt is null ? null : Iso(merged.LastStateChangedAt.Value)), ("$historical", (int)merged.LastKnownHistoricalState), ("$custom", device.CustomName), ("$note", device.Note), ("$id", id)], cancellationToken); updatedDevices++;
            }
            devices[Key(device.RouterMiotDid, mac)] = id;
        }

        var addedEvents = 0; var skipped = 0;
        foreach (var value in document.Events)
        {
            var id = devices[Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))];
            var changed = await InsertOrIgnoreAsync(connection, "INSERT OR IGNORE INTO PresenceEvent(DeviceId,EventType,ObservedAt,Source) VALUES($device,$type,$at,$source)", [("$device", id), ("$type", value.EventType), ("$at", Iso(value.ObservedAt)), ("$source", value.Source)], cancellationToken);
            if (changed == 1) addedEvents++; else skipped++;
        }
        foreach (var value in document.Sessions)
        {
            var id = devices[Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))];
            var changed = await InsertOrIgnoreAsync(connection, "INSERT OR IGNORE INTO PresenceSession(DeviceId,StartedAt,EndedAt,StartKnown,EndKnown) VALUES($device,$start,$end,$sk,$ek)", [("$device", id), ("$start", Iso(value.StartedAt)), ("$end", value.EndedAt is null ? null : Iso(value.EndedAt.Value)), ("$sk", value.StartKnown ? 1 : 0), ("$ek", value.EndKnown ? 1 : 0)], cancellationToken);
            if (changed == 0)
            {
                await ExecuteAsync(connection, "UPDATE PresenceSession SET EndedAt=CASE WHEN EndedAt IS NULL THEN $end WHEN $end IS NULL THEN EndedAt WHEN EndedAt < $end THEN $end ELSE EndedAt END,StartKnown=CASE WHEN StartKnown=1 OR $sk=1 THEN 1 ELSE 0 END,EndKnown=CASE WHEN EndKnown=1 OR $ek=1 THEN 1 ELSE 0 END WHERE DeviceId=$device AND StartedAt=$start",
                    [("$device", id), ("$start", Iso(value.StartedAt)), ("$end", value.EndedAt is null ? null : Iso(value.EndedAt.Value)), ("$sk", value.StartKnown ? 1 : 0), ("$ek", value.EndKnown ? 1 : 0)], cancellationToken);
                skipped++;
            }
        }
        foreach (var value in document.MonitoringGaps)
        {
            var changed = await InsertOrIgnoreAsync(connection, "INSERT OR IGNORE INTO MonitoringGap(StartedAt,EndedAt,Reason) VALUES($start,$end,$reason)", [("$start", Iso(value.StartedAt)), ("$end", value.EndedAt is null ? null : Iso(value.EndedAt.Value)), ("$reason", value.Reason)], cancellationToken);
            if (changed == 0)
            {
                await ExecuteAsync(connection, "UPDATE MonitoringGap SET EndedAt=CASE WHEN EndedAt IS NULL THEN $end WHEN $end IS NULL THEN EndedAt WHEN EndedAt < $end THEN $end ELSE EndedAt END WHERE StartedAt=$start AND Reason=$reason",
                    [("$start", Iso(value.StartedAt)), ("$end", value.EndedAt is null ? null : Iso(value.EndedAt.Value)), ("$reason", value.Reason)], cancellationToken);
                skipped++;
            }
        }
        var subjects = new Dictionary<Guid, long>();
        foreach (var value in document.Subjects ?? [])
        {
            var id = await ScalarLongAsync(connection, "SELECT Id FROM PresenceSubject WHERE ExportId=$export", [("$export", value.ExportId.ToString("D"))], cancellationToken);
            if (id == 0) id = await InsertIdAsync(connection, "INSERT INTO PresenceSubject(ExportId,DisplayName,Note,CreatedAt,UpdatedAt) VALUES($export,$name,$note,$created,$updated)", [("$export", value.ExportId.ToString("D")), ("$name", value.DisplayName), ("$note", value.Note), ("$created", Iso(value.CreatedAt)), ("$updated", Iso(value.UpdatedAt))], cancellationToken);
            else await ExecuteAsync(connection, "UPDATE PresenceSubject SET DisplayName=$name,Note=$note,UpdatedAt=$updated WHERE Id=$id", [("$name", value.DisplayName), ("$note", value.Note), ("$updated", Iso(value.UpdatedAt)), ("$id", id)], cancellationToken);
            subjects[value.ExportId] = id;
        }
        foreach (var value in document.SubjectDeviceMemberships ?? [])
        {
            var subjectId = subjects[value.SubjectExportId];
            var deviceId = devices[Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))];
            await ExecuteAsync(connection, "INSERT INTO SubjectDeviceMembership(SubjectId,NetworkDeviceId,CreatedAt) VALUES($subject,$device,$created) ON CONFLICT(NetworkDeviceId) DO UPDATE SET SubjectId=$subject", [("$subject", subjectId), ("$device", deviceId), ("$created", Iso(value.CreatedAt))], cancellationToken);
        }
        foreach (var value in document.SubjectCurrentStates ?? [])
        {
            var subjectId = subjects[value.SubjectExportId];
            await ExecuteAsync(connection,
                "INSERT INTO SubjectCurrentState(SubjectId,CurrentState,StateSince,LastObservedAt,PendingOfflineSince) VALUES($subject,$state,$since,$observed,$pending) ON CONFLICT(SubjectId) DO UPDATE SET CurrentState=CASE WHEN excluded.LastObservedAt>=SubjectCurrentState.LastObservedAt THEN excluded.CurrentState ELSE SubjectCurrentState.CurrentState END,StateSince=CASE WHEN excluded.LastObservedAt>=SubjectCurrentState.LastObservedAt THEN excluded.StateSince ELSE SubjectCurrentState.StateSince END,LastObservedAt=CASE WHEN excluded.LastObservedAt>=SubjectCurrentState.LastObservedAt THEN excluded.LastObservedAt ELSE SubjectCurrentState.LastObservedAt END,PendingOfflineSince=CASE WHEN excluded.LastObservedAt>=SubjectCurrentState.LastObservedAt THEN excluded.PendingOfflineSince ELSE SubjectCurrentState.PendingOfflineSince END",
                [("$subject", subjectId), ("$state", value.CurrentState), ("$since", Iso(value.StateSince)), ("$observed", Iso(value.LastObservedAt)), ("$pending", value.PendingOfflineSince is null ? null : Iso(value.PendingOfflineSince.Value))], cancellationToken);
        }
        foreach (var value in document.SubjectPresenceEvents ?? [])
        {
            var subjectId = subjects[value.SubjectExportId];
            long? gapId = null;
            if (value.MonitoringGapStartedAt is { } gapStartedAt && !string.IsNullOrWhiteSpace(value.MonitoringGapReason))
            {
                var resolvedGapId = await ScalarLongAsync(connection,
                    "SELECT Id FROM MonitoringGap WHERE StartedAt=$start AND Reason=$reason LIMIT 1",
                    [("$start", Iso(gapStartedAt)), ("$reason", value.MonitoringGapReason)], cancellationToken);
                if (resolvedGapId == 0)
                {
                    skipped++;
                    continue;
                }
                gapId = resolvedGapId;
            }
            var changed = await InsertOrIgnoreAsync(connection,
                "INSERT OR IGNORE INTO SubjectPresenceEvent(SubjectId,EventType,ObservedAt,MonitoringGapId,StateSince) VALUES($subject,$type,$at,$gap,$since)",
                [("$subject", subjectId), ("$type", value.EventType), ("$at", Iso(value.ObservedAt)), ("$gap", gapId), ("$since", value.StateSince is null ? null : Iso(value.StateSince.Value))], cancellationToken);
            if (changed == 1) addedEvents++; else skipped++;
        }
        var importedRecipients = new Dictionary<(int TargetType, string TargetId), long>();
        foreach (var value in document.NotificationRecipients ?? [])
        {
            await ExecuteAsync(connection, "INSERT OR IGNORE INTO NotificationRecipient(Note,OpenId,TargetType,CreatedAt,UpdatedAt) VALUES($note,$openid,$type,$created,$updated)",
                [("$note", value.Note), ("$openid", value.OpenId), ("$type", value.TargetType), ("$created", Iso(value.CreatedAt)), ("$updated", Iso(value.UpdatedAt))], cancellationToken);
            var recipientId = await ScalarLongAsync(connection, "SELECT Id FROM NotificationRecipient WHERE TargetType=$type AND OpenId=$openid LIMIT 1", [ ("$type", value.TargetType), ("$openid", value.OpenId) ], cancellationToken);
            importedRecipients[(value.TargetType, value.OpenId)] = recipientId;
        }
        foreach (var value in document.NotificationRules ?? [])
        {
            var subjectId = subjects[value.SubjectExportId];
            var existing = await ScalarLongAsync(connection, "SELECT Id FROM NotificationRule WHERE SubjectId=$subject AND RuleCondition=$condition AND ThresholdSeconds=$threshold AND Channel=$channel AND TargetType=$targetType AND TargetId=$target AND MessageTemplate=$template LIMIT 1",
                [("$subject", subjectId), ("$condition", value.Condition), ("$threshold", value.ThresholdSeconds), ("$channel", value.Channel), ("$targetType", value.TargetType), ("$target", value.TargetId), ("$template", value.MessageTemplate)], cancellationToken);
            if (existing == 0)
            {
                await ExecuteAsync(connection, "INSERT INTO NotificationRule(SubjectId,Enabled,RuleCondition,ThresholdSeconds,Channel,TargetType,TargetId,MessageTemplate,CreatedAt,UpdatedAt) VALUES($subject,$enabled,$condition,$threshold,$channel,$targetType,$target,$template,$created,$updated)",
                    [("$subject", subjectId), ("$enabled", value.Enabled ? 1 : 0), ("$condition", value.Condition), ("$threshold", value.ThresholdSeconds), ("$channel", value.Channel), ("$targetType", value.TargetType), ("$target", value.TargetId), ("$template", value.MessageTemplate), ("$created", Iso(value.CreatedAt)), ("$updated", Iso(value.UpdatedAt))], cancellationToken);
                existing = await ScalarLongAsync(connection, "SELECT last_insert_rowid()", [], cancellationToken);
            }
            else skipped++;
            var targets = value.Recipients is { Count: > 0 }
                ? value.Recipients
                : [new ExportNotificationRecipientTarget(value.TargetType, value.TargetId)];
            foreach (var target in targets)
            {
                if (!importedRecipients.TryGetValue((target.TargetType, target.TargetId), out var recipientId))
                {
                    await ExecuteAsync(connection, "INSERT OR IGNORE INTO NotificationRecipient(Note,OpenId,TargetType,CreatedAt,UpdatedAt) VALUES($note,$openid,$type,$now,$now)",
                        [("$note", "已有接收人"), ("$openid", target.TargetId), ("$type", target.TargetType), ("$now", Iso(DateTimeOffset.UtcNow))], cancellationToken);
                    recipientId = await ScalarLongAsync(connection, "SELECT Id FROM NotificationRecipient WHERE TargetType=$type AND OpenId=$openid LIMIT 1", [("$type", target.TargetType), ("$openid", target.TargetId)], cancellationToken);
                    importedRecipients[(target.TargetType, target.TargetId)] = recipientId;
                }
                await ExecuteAsync(connection, "INSERT OR IGNORE INTO NotificationRuleRecipient(RuleId,RecipientId,CreatedAt) VALUES($rule,$recipient,$now)",
                    [("$rule", existing), ("$recipient", recipientId), ("$now", Iso(DateTimeOffset.UtcNow))], cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        var repository = new SqlitePresenceRepository(paths);
        await repository.EnsureEveryDeviceHasSubjectAsync(cancellationToken);
        await repository.EnsureSubjectCurrentStatesAsync(cancellationToken);
        await repository.ReconcileSubjectIdentityAsync(cancellationToken);
        await repository.EnsureNotificationRuleEventWatermarksAsync(cancellationToken);
        return new ImportResult(addedDevices, updatedDevices, addedEvents, skipped);
    }

    private static void Validate(ExportDocument document)
    {
        if (document.Manifest.Format != Format || document.Manifest.Version is < 1 or > 2 || document.Manifest.ContainsAuthentication) throw new InvalidDataException("不支持或不安全的 CloudLight XiaoMi 备份格式。 ");
        if (document.Routers.Any(value => string.IsNullOrWhiteSpace(value.MiotDid) || string.IsNullOrWhiteSpace(value.PartnerId))) throw new InvalidDataException("路由器数据不完整。 ");
        var routerIds = document.Routers.Select(value => value.MiotDid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.Devices.Any(value => !routerIds.Contains(value.RouterMiotDid))) throw new InvalidDataException("设备引用了不存在的路由器。 ");
        var deviceKeys = document.Devices.Select(value => Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.Events.Any(value => !deviceKeys.Contains(Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress)))) || document.Sessions.Any(value => !deviceKeys.Contains(Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))))) throw new InvalidDataException("历史记录引用了不存在的设备。 ");
        var subjectIds = (document.Subjects ?? []).Select(value => value.ExportId).ToHashSet();
        if ((document.SubjectDeviceMemberships ?? []).Any(value => !subjectIds.Contains(value.SubjectExportId) || !deviceKeys.Contains(Key(value.RouterMiotDid, PresenceStateMachine.NormalizeMac(value.MacAddress))))) throw new InvalidDataException("主体关联引用了不存在的主体或设备。 ");
        if ((document.SubjectCurrentStates ?? []).Any(value => !subjectIds.Contains(value.SubjectExportId) || value.CurrentState is not (1 or 2) || value.StateSince > value.LastObservedAt || (value.PendingOfflineSince is { } pending && (pending < value.StateSince || pending > value.LastObservedAt)))) throw new InvalidDataException("主体当前状态数据不完整或无效。 ");
        if ((document.SubjectPresenceEvents ?? []).Any(value => !subjectIds.Contains(value.SubjectExportId) || value.EventType is < 1 or > 6 || ((value.EventType is 1 or 2) && (value.MonitoringGapStartedAt is null || string.IsNullOrWhiteSpace(value.MonitoringGapReason))) || (value.EventType is >= 3 and <= 6 && value.MonitoringGapStartedAt is not null))) throw new InvalidDataException("主体活动记录不完整或无效。 ");
        if ((document.NotificationRecipients ?? []).Any(value => value.TargetType is not (1 or 2) || string.IsNullOrWhiteSpace(value.OpenId) || value.OpenId.Any(char.IsWhiteSpace) || value.OpenId.Length > 256 || value.Note is null || value.Note.Length > 120)) throw new InvalidDataException("QQ 接收人数据不完整或超出允许范围。 ");
        if ((document.NotificationRules ?? []).Any(value =>
            !subjectIds.Contains(value.SubjectExportId) ||
            value.Condition is < 1 or > 4 ||
            (value.Condition is 1 or 2
                ? value.ThresholdSeconds is < 60 or > 365L * 24 * 60 * 60
                : value.ThresholdSeconds != 0) ||
            value.Channel != 1 || value.TargetType is not (1 or 2) ||
            string.IsNullOrWhiteSpace(value.TargetId) || value.TargetId.Any(char.IsWhiteSpace) ||
            value.TargetId.Length > 256 || value.MessageTemplate is null || value.MessageTemplate.Length > 10_000 ||
            (value.Recipients ?? []).Any(target => target.TargetType is not (1 or 2) || string.IsNullOrWhiteSpace(target.TargetId) || target.TargetId.Any(char.IsWhiteSpace) || target.TargetId.Length > 256)))
            throw new InvalidDataException("通知规则数据不完整或超出允许范围。 ");
    }

    private static List<(string, object?)> DeviceParameters(ExportDevice value, long routerId, string mac) => [("$router", routerId), ("$mac", mac), ("$original", value.OriginalName), ("$origin", value.OriginName), ("$custom", value.CustomName), ("$note", value.Note), ("$ip", value.LastIp), ("$connection", value.ConnectionType), ("$signal", value.Signal), ("$state", value.CurrentState), ("$first", Iso(value.FirstSeenAt)), ("$last", Iso(value.LastSeenAt)), ("$changed", value.LastStateChangedAt is null ? null : Iso(value.LastStateChangedAt.Value)), ("$historical", value.LastKnownHistoricalState ?? value.CurrentState)];
    private static async Task<DeviceRuntime> ReadDeviceRuntimeAsync(SqliteConnection connection, long id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT CurrentState,FirstSeenAt,LastSeenAt,LastStateChangedAt,LastKnownHistoricalState FROM NetworkDevice WHERE Id=$id", [("$id", id)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("导入目标设备不存在。 ");
        return new((PresenceState)reader.GetInt32(0), ParseTime(reader.GetString(1)), ParseTime(reader.GetString(2)), reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)), (PresenceState)reader.GetInt32(4));
    }

    private static DeviceRuntime MergeDeviceRuntime(DeviceRuntime existing, ExportDevice imported)
    {
        var importedChanged = imported.LastStateChangedAt;
        var existingChanged = existing.LastStateChangedAt;
        var importedEvidence = importedChanged ?? imported.LastSeenAt;
        var existingEvidence = existingChanged ?? existing.LastSeenAt;
        var importedState = (PresenceState)imported.CurrentState;
        var importedHistorical = imported.LastKnownHistoricalState is { } historical ? (PresenceState)historical : importedState;
        var sameState = existing.CurrentState == importedState;
        var useImportedState = sameState || importedEvidence >= existingEvidence;
        var state = useImportedState ? importedState : existing.CurrentState;
        var historicalState = useImportedState ? importedHistorical : existing.LastKnownHistoricalState;
        var changed = sameState
            ? MaxKnown(existingChanged, importedChanged)
            : useImportedState ? importedChanged : existingChanged;
        return new(state, existing.FirstSeenAt <= imported.FirstSeenAt ? existing.FirstSeenAt : imported.FirstSeenAt,
            existing.LastSeenAt >= imported.LastSeenAt ? existing.LastSeenAt : imported.LastSeenAt, changed, historicalState);
    }

    private static DateTimeOffset? MaxKnown(DateTimeOffset? left, DateTimeOffset? right) => left is null ? right : right is null ? left : left >= right ? left : right;
    private sealed record DeviceRuntime(PresenceState CurrentState, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? LastStateChangedAt, PresenceState LastKnownHistoricalState);
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
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    public sealed record ExportDocument(ExportManifest Manifest, List<ExportRouter> Routers, List<ExportDevice> Devices, List<ExportEvent> Events, List<ExportSession> Sessions, List<ExportGap> MonitoringGaps, List<ExportSubject>? Subjects = null, List<ExportSubjectDeviceMembership>? SubjectDeviceMemberships = null, List<ExportNotificationRule>? NotificationRules = null, List<ExportSubjectPresenceEvent>? SubjectPresenceEvents = null, List<ExportSubjectCurrentState>? SubjectCurrentStates = null, List<ExportNotificationRecipient>? NotificationRecipients = null);
    public sealed record ExportManifest(string Format, int Version, DateTimeOffset CreatedAtUtc, string AppVersion, bool ContainsAuthentication);
    public sealed record ExportRouter(string MiotDid, string MiotModel, string PartnerId, string Name, string? HomeId, string? RoomId, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);
    public sealed record ExportDevice(string RouterMiotDid, string MacAddress, string? OriginalName, string? OriginName, string? CustomName, string? Note, string? LastIp, string? ConnectionType, int? Signal, int CurrentState, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? LastStateChangedAt, int? LastKnownHistoricalState = null);
    public sealed record ExportEvent(string RouterMiotDid, string MacAddress, int EventType, DateTimeOffset ObservedAt, int Source);
    public sealed record ExportSession(string RouterMiotDid, string MacAddress, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool StartKnown, bool EndKnown);
    public sealed record ExportGap(DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Reason);
    public sealed record ExportSubject(Guid ExportId, string DisplayName, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record ExportSubjectDeviceMembership(Guid SubjectExportId, string RouterMiotDid, string MacAddress, DateTimeOffset CreatedAt);
    public sealed record ExportNotificationRule(Guid SubjectExportId, bool Enabled, int Condition, long ThresholdSeconds, int Channel, int TargetType, string TargetId, string? MessageTemplate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long SourceRuleId = 0, List<ExportNotificationRecipientTarget>? Recipients = null);
    public sealed record ExportNotificationRecipient(string Note, string OpenId, int TargetType, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record ExportNotificationRecipientTarget(int TargetType, string TargetId);
    public sealed record ExportSubjectPresenceEvent(Guid SubjectExportId, int EventType, DateTimeOffset ObservedAt, DateTimeOffset? MonitoringGapStartedAt, string? MonitoringGapReason, DateTimeOffset? StateSince = null);
    public sealed record ExportSubjectCurrentState(Guid SubjectExportId, int CurrentState, DateTimeOffset StateSince, DateTimeOffset LastObservedAt, DateTimeOffset? PendingOfflineSince = null);
}
