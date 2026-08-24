using System.Security.Cryptography;
using System.Text;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.Infrastructure.SecureStorage;

public sealed class DpapiSessionStore(IAppDataPaths paths) : ISecureSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudLight Presence/Xiaomi Router Cloud/v1");
    public bool Exists => File.Exists(paths.AuthPath);

    public async Task<string> LoadAsync(CancellationToken cancellationToken)
    {
        var encrypted = await File.ReadAllBytesAsync(paths.AuthPath, cancellationToken);
        var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveAsync(string json, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var plaintext = Encoding.UTF8.GetBytes(json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = paths.AuthPath + ".new";
        await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
        File.Move(temporary, paths.AuthPath, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(paths.AuthPath)) File.Delete(paths.AuthPath);
        return Task.CompletedTask;
    }
}
