using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudLight.Presence.RouterCloud.Probe;

internal sealed class XiaomiDpapiSessionStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("CloudLight Presence/Xiaomi Router Cloud/v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public XiaomiDpapiSessionStore()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Presence");
        AuthPath = Path.Combine(DirectoryPath, "auth.dat");
        SettingsPath = Path.Combine(DirectoryPath, "settings.json");
    }

    public string DirectoryPath { get; }
    public string AuthPath { get; }
    public string SettingsPath { get; }

    public bool Exists => File.Exists(AuthPath);

    public async Task<XiaomiStoredSession> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var encrypted = await File.ReadAllBytesAsync(AuthPath, cancellationToken);
            var plaintext = ProtectedData.Unprotect(
                encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<XiaomiStoredSession>(plaintext, JsonOptions)
                    ?? throw new JsonException("Stored Xiaomi session is empty.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException exception)
        {
            throw new ProbeException(
                ProbeErrorCategory.AuthenticationExpired,
                "Stored Xiaomi session cannot be decrypted by the current Windows user.", exception);
        }
    }

    public async Task SaveAsync(
        XiaomiStoredSession storedSession,
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        await SaveSessionAsync(storedSession, cancellationToken);
        Directory.CreateDirectory(DirectoryPath);
        var settingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        var temporarySettingsPath = SettingsPath + ".new";
        await File.WriteAllTextAsync(
            temporarySettingsPath, settingsJson, Encoding.UTF8, cancellationToken);
        File.Move(temporarySettingsPath, SettingsPath, overwrite: true);
    }

    public async Task SaveSessionAsync(
        XiaomiStoredSession storedSession,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(storedSession, JsonOptions);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(
                plaintext, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var temporaryAuthPath = AuthPath + ".new";
        await File.WriteAllBytesAsync(temporaryAuthPath, encrypted, cancellationToken);
        File.Move(temporaryAuthPath, AuthPath, overwrite: true);
    }
}

internal sealed record XiaomiStoredSession(
    int Version,
    string Region,
    string UserId,
    string? AccountUserId,
    string? CUserId,
    string DeviceId,
    string PassToken,
    string ServiceToken,
    string Ssecurity,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastValidatedAt)
{
    public XiaomiRouterSession ToRouterSession() =>
        new(UserId, CUserId, PassToken, ServiceToken, Ssecurity);
}

internal sealed record ProbeSettings(
    string Region,
    string MiotModel,
    string Hardware,
    string SelectedRouterPrivateId,
    string SelectedRouterSerial,
    bool RememberLogin);
