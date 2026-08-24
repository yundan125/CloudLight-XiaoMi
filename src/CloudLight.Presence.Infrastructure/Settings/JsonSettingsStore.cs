using System.Text.Json;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed record PresenceSettings(
    string? SelectedRouterPartnerId = null,
    bool StartWithWindows = false,
    bool StartMinimized = true,
    int PollingIntervalSeconds = 10);

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
