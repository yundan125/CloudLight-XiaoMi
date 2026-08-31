using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;
using CloudLight.Presence.Infrastructure.Database;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.Infrastructure.Diagnostics;

public sealed record DiagnosticsExportResult(string FilePath, DateTimeOffset CreatedAt);

public static partial class DiagnosticsRedaction
{
    [GeneratedRegex(@"(?im)(?<key>\b(?:token|access[_-]?token|service[_-]?token|pass[_-]?token|auth[_-]?token|cookie|authorization|appsecret|app[_-]?secret|openid|open[_-]?id|session|password|secret|ssecurity|user[_-]?id|cuser[_-]?id)\b)(?<separator>\s*[:=]\s*)(?<value>[^\r\n,;}\]]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacRegex();

    public static string MaskOpenId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var trimmed = value.Trim();
        return trimmed.Length <= 6 ? "****" : $"{trimmed[..3]}****{trimmed[^3..]}";
    }

    public static string MaskMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var parts = value.Trim().Split(':');
        return parts.Length == 6
            ? $"{parts[0]}:{parts[1]}:{parts[2]}:**:**:{parts[5]}"
            : "**:**:**:**:**:**";
    }

    public static string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var redacted = SensitiveValueRegex().Replace(value, "${key}${separator}[REDACTED]");
        redacted = BearerRegex().Replace(redacted, "Bearer [REDACTED]");
        return MacRegex().Replace(redacted, match => MaskMac(match.Value));
    }
}

/// <summary>
/// Builds a small support bundle from existing runtime/repository state.
/// Credentials, the live SQLite database, and raw log files are deliberately
/// excluded. Log entries are filtered before they enter the ZIP.
/// </summary>
public sealed class DiagnosticsExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IAppDataPaths _paths;
    private readonly IPresenceRepository _repository;
    private readonly PresenceMonitor _monitor;
    private readonly NotificationRuntime _notificationRuntime;
    private readonly INotificationChannel? _qq;
    private readonly JsonSettingsStore _settings;
    private readonly SqliteDatabaseBackupService _databaseBackup;
    private readonly Assembly _applicationAssembly;

    public DiagnosticsExportService(
        IAppDataPaths paths,
        IPresenceRepository repository,
        PresenceMonitor monitor,
        NotificationRuntime notificationRuntime,
        INotificationChannel? qq,
        JsonSettingsStore settings,
        SqliteDatabaseBackupService databaseBackup,
        Assembly? applicationAssembly = null)
    {
        _paths = paths;
        _repository = repository;
        _monitor = monitor;
        _notificationRuntime = notificationRuntime;
        _qq = qq;
        _settings = settings;
        _databaseBackup = databaseBackup;
        _applicationAssembly = applicationAssembly ?? Assembly.GetEntryAssembly() ?? typeof(DiagnosticsExportService).Assembly;
    }

    public async Task<DiagnosticsExportResult> ExportAsync(string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("诊断包路径不能为空。", nameof(destinationPath));
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath) ?? _paths.DiagnosticsDirectory;
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(_paths.DiagnosticsDirectory);
        var temporaryPath = fullPath + ".new";
        TryDelete(temporaryPath);

        var routers = await _repository.GetRoutersAsync(cancellationToken);
        var subjects = await _repository.GetSubjectsAsync(cancellationToken);
        var rules = await _repository.GetNotificationRulesAsync(enabledOnly: false, cancellationToken);
        var recipients = await _repository.GetNotificationRecipientsAsync(cancellationToken);
        var backupStatus = await _databaseBackup.GetStatusAsync(cancellationToken);
        var settings = await _settings.LoadAsync(cancellationToken);
        var schemaVersion = await ReadSchemaVersionAsync(cancellationToken);
        var databaseSize = File.Exists(_paths.DatabasePath) ? new FileInfo(_paths.DatabasePath).Length : 0L;
        var appVersion = _applicationAssembly.GetName().Version?.ToString(3) ?? "development";
        var fileVersion = GetFileVersion(_applicationAssembly) ?? appVersion;
        var qqStatus = _qq?.Status;
        var monitorStatus = _monitor.LastStatus;
        var createdAt = DateTimeOffset.UtcNow;

        var diagnostics = new
        {
            AppVersion = appVersion,
            FileVersion = fileVersion,
            OSVersion = RuntimeInformation.OSDescription,
            DotNetVersion = Environment.Version.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DatabaseSchemaVersion = schemaVersion,
            DatabaseSize = databaseSize,
            XiaomiConnectionState = _monitor.IsPaused ? CloudConnectionState.Paused.ToString() : monitorStatus?.State.ToString() ?? "Unknown",
            RouterCount = routers.Count,
            RouterModels = routers.Select(value => value.MiotModel).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PresenceSubjectCount = subjects.Count,
            QQConnectionState = qqStatus?.ConnectionState.ToString() ?? "Unknown",
            NotificationRuleCount = rules.Count,
            RecipientCount = recipients.Count,
            LastSuccessfulPresencePoll = _monitor.LastSuccessfulCloudUpdate,
            LastNotificationEvaluation = _notificationRuntime.LastEvaluationAt
        };

        var redactedSettings = new
        {
            PollingIntervalSeconds = settings.PollingIntervalSeconds,
            StartWithWindows = settings.StartWithWindows,
            StartMinimized = settings.StartMinimized,
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
            PauseUntil = settings.PauseUntil,
            ProxyMode = settings.Qq?.ProxyMode,
            QqEnabled = settings.Qq?.Enabled ?? false,
            QqAutoConnect = settings.Qq?.AutoConnect ?? false,
            QqRecipientCount = settings.Qq?.DefaultRecipientIds.Count ?? 0,
            ConnectionAlertsEnabled = settings.ConnectionAlerts?.Enabled ?? false,
            ConnectionAlertRecipientCount = settings.ConnectionAlerts?.RecipientIds.Count ?? 0
        };

        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                await WriteTextEntryAsync(archive, "diagnostics.json", JsonSerializer.Serialize(diagnostics, JsonOptions), cancellationToken);
                await WriteTextEntryAsync(archive, "settings-redacted.json", JsonSerializer.Serialize(redactedSettings, JsonOptions), cancellationToken);
                await WriteTextEntryAsync(archive, "database-info.txt", BuildDatabaseInfo(schemaVersion, databaseSize, backupStatus), cancellationToken);
                await WriteTextEntryAsync(archive, "runtime-info.txt", BuildRuntimeInfo(monitorStatus, qqStatus), cancellationToken);
                await AddSanitizedLogsAsync(archive, cancellationToken);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
            return new(fullPath, createdAt);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task<int> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath)) return 0;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        return await SqliteDatabaseBackupService.ReadUserVersionAsync(connection, cancellationToken);
    }

    private async Task AddSanitizedLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.LogsDirectory)) return;
        var total = 0L;
        foreach (var path in Directory.EnumerateFiles(_paths.LogsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(value => value.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total >= 10 * 1024 * 1024) break;
            string raw;
            try
            {
                raw = await File.ReadAllTextAsync(path, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                raw = $"[日志无法读取：{DiagnosticsRedaction.RedactText(exception.Message)}]";
            }
            var sanitized = DiagnosticsRedaction.RedactText(raw);
            const int maxPerFile = 2 * 1024 * 1024;
            if (sanitized.Length > maxPerFile) sanitized = sanitized[..maxPerFile] + Environment.NewLine + "[日志已截断]";
            total += Encoding.UTF8.GetByteCount(sanitized);
            var name = Path.GetFileName(path);
            await WriteTextEntryAsync(archive, $"logs/{name}", sanitized, cancellationToken);
        }
    }

    private static string BuildDatabaseInfo(int schemaVersion, long databaseSize, DatabaseBackupStatus status) =>
        $"数据库文件：presence.db{Environment.NewLine}" +
        $"Schema：{schemaVersion}{Environment.NewLine}" +
        $"大小：{databaseSize} bytes{Environment.NewLine}" +
        $"最近迁移备份：{FormatFile(status.LastMigrationBackupPath, status.LastMigrationBackupAt)}{Environment.NewLine}" +
        $"最近手动备份：{FormatFile(status.LastManualBackupPath, status.LastManualBackupAt)}{Environment.NewLine}" +
        $"最近备份失败：{(status.LastFailureAt is null ? "暂无" : $"{status.LastFailureAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {DiagnosticsRedaction.RedactText(status.LastFailure)}")}";

    private string BuildRuntimeInfo(MonitorStatus? monitorStatus, NotificationChannelStatus? qqStatus) =>
        $"Presence：{(_monitor.IsPaused ? "Paused" : monitorStatus?.State.ToString() ?? "Unknown")}{Environment.NewLine}" +
        $"最近成功轮询：{FormatTime(_monitor.LastSuccessfulCloudUpdate)}{Environment.NewLine}" +
        $"QQ：{qqStatus?.ConnectionState.ToString() ?? "Unknown"}{Environment.NewLine}" +
        $"QQ 最近错误：{DiagnosticsRedaction.RedactText(qqStatus?.LastError)}{Environment.NewLine}" +
        $"Notification Runtime：{(_notificationRuntime.IsRunning ? "Running" : "Stopped")}{Environment.NewLine}" +
        $"最近评估：{FormatTime(_notificationRuntime.LastEvaluationAt)}{Environment.NewLine}" +
        $"最近评估错误：{DiagnosticsRedaction.RedactText(_notificationRuntime.LastEvaluationError)}";

    private static string FormatFile(string? path, DateTimeOffset? at) =>
        at is null ? "暂无" : $"{at.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Path.GetFileName(path ?? string.Empty)}";

    private static string FormatTime(DateTimeOffset? value) => value is null ? "暂无" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private static string? GetFileVersion(Assembly assembly)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(assembly.Location)) return null;
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        }
        catch { return null; }
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
