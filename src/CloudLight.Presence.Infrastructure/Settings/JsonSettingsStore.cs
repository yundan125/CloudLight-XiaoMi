using System.Text.Json;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed record PresenceSettings(
    string? SelectedRouterPartnerId = null,
    bool StartWithWindows = false,
    bool StartMinimized = true,
    int PollingIntervalSeconds = 10)
{
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
    string DefaultTargetId = "");

public sealed class JsonSettingsStore(IAppDataPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PresenceSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SettingsPath)) return new PresenceSettings();
        await using var stream = File.OpenRead(paths.SettingsPath);
        return await JsonSerializer.DeserializeAsync<PresenceSettings>(stream, Options, cancellationToken)
            ?? new PresenceSettings();
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
