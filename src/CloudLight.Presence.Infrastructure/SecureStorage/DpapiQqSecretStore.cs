using System.Security.Cryptography;
using System.Text;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.Infrastructure.SecureStorage;

public sealed class DpapiQqSecretStore(IAppDataPaths paths)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudLight XiaoMi/QQ Official Bot AppSecret/v1");

    public string SecretPath => paths.QqAuthPath;
    public bool Exists => File.Exists(SecretPath);

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SecretPath)) return null;
        var encrypted = await File.ReadAllBytesAsync(SecretPath, cancellationToken);
        byte[] plaintext;
        try { plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException exception) { throw new InvalidDataException("已保存的 QQ 应用密钥无法解密，可能属于其他 Windows 用户或文件已损坏。", exception); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
        try { return new UTF8Encoding(false, true).GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveAsync(string secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("QQ AppSecret 不能为空。", nameof(secret));
        var plaintext = Encoding.UTF8.GetBytes(secret.Trim()); byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(Path.GetDirectoryName(SecretPath)!);
        var temporary = SecretPath + ".new";
        try { await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken); File.Move(temporary, SecretPath, true); }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(SecretPath)) File.Delete(SecretPath);
        return Task.CompletedTask;
    }
}
