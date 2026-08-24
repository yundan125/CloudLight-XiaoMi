using System.Text.Json;

namespace CloudLight.Presence.Infrastructure.Settings;

public sealed record PresenceSettings(
    string? SelectedRouterPartnerId = null,
    bool StartWithWindows = false,
    bool StartMinimized = true);

public sealed class JsonSettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PresenceSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.Settings)) return new PresenceSettings();
        await using var stream = File.OpenRead(paths.Settings);
        return await JsonSerializer.DeserializeAsync<PresenceSettings>(stream, Options, cancellationToken)
            ?? new PresenceSettings();
    }

    public async Task SaveAsync(PresenceSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var temporary = paths.Settings + ".new";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        }
        File.Move(temporary, paths.Settings, overwrite: true);
    }
}
