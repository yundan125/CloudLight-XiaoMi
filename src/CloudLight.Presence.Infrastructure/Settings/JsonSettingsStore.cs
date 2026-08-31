using System.Text.Json;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed record PresenceSettings(
    string? SelectedRouterPartnerId = null,
    bool StartWithWindows = false,
    bool StartMinimized = true,
    int PollingIntervalSeconds = 10)
{
    /// <summary>Stable router identity used when partner_id is absent or rotated.</summary>
    public string? SelectedRouterMiotDid { get; init; }
    /// <summary>Keep the existing background-first close behavior by default.</summary>
    public bool MinimizeToTrayOnClose { get; init; } = true;
    /// <summary>Null means not paused; MaxValue represents manual pause.</summary>
    public DateTimeOffset? PauseUntil { get; init; }
    public DateTimeOffset? LastUpdateCheckAt { get; init; }
    public QqNotificationSettings Qq { get; init; } = new();
    public ConnectionAlertSettings ConnectionAlerts { get; init; } = new();
}

public sealed record QqNotificationSettings(
    bool Enabled = false,
    bool AutoConnect = true,
    string AppId = "",
    bool GatewayReconnectEnabled = true,
    string ProxyMode = "environment",
    string ProxyUrl = "",
    NotificationTargetType DefaultTargetType = NotificationTargetType.Private,
    string DefaultTargetId = "")
{
    /// <summary>
    /// IDs of saved QQ recipients used for system/default notifications.
    /// DefaultTargetType/DefaultTargetId remain for importing older settings.
    /// </summary>
    public IReadOnlyList<long> DefaultRecipientIds { get; init; } = [];
}

public sealed class JsonSettingsStore(IAppDataPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PresenceSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SettingsPath)) return new PresenceSettings();
        await using var stream = File.OpenRead(paths.SettingsPath);
        var value = await JsonSerializer.DeserializeAsync<PresenceSettings>(stream, Options, cancellationToken)
            ?? new PresenceSettings();

        // System.Text.Json materializes IReadOnlyList<T> as List<T>.  Normalize
        // the new collection-valued settings so older record equality checks and
        // callers see the same stable shape as settings created in memory.
        var qq = value.Qq ?? new QqNotificationSettings();
        var connectionAlerts = value.ConnectionAlerts ?? new ConnectionAlertSettings();
        return value with
        {
            Qq = qq with { DefaultRecipientIds = (qq.DefaultRecipientIds ?? []).ToArray() },
            ConnectionAlerts = connectionAlerts with { RecipientIds = (connectionAlerts.RecipientIds ?? []).ToArray() }
        };
    }

    public async Task SaveAsync(PresenceSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var temporary = paths.SettingsPath + ".new";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        }
        File.Move(temporary, paths.SettingsPath, overwrite: true);
    }
}
