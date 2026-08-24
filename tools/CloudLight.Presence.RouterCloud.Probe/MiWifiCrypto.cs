using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CloudLight.Presence.RouterCloud.Probe;

internal static class MiWifiCrypto
{
    public static SignedRequest Sign(
        string method,
        string path,
        IReadOnlyDictionary<string, string> parameters,
        string ssecurity)
    {
        Span<byte> nonceBytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(nonceBytes[..8]);
        BinaryPrimitives.WriteInt32BigEndian(
            nonceBytes[8..], checked((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000)));
        var nonce = Convert.ToBase64String(nonceBytes);
        var securityBytes = Convert.FromBase64String(ssecurity);
        var signedNonceBytes = SHA256.HashData(securityBytes.Concat(nonceBytes.ToArray()).ToArray());
        var signedNonce = Convert.ToBase64String(signedNonceBytes);

        var plain = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            plain[pair.Key] = pair.Value;
        }
        plain["rc4_hash__"] = Sha1Base64(BuildSignatureText(method, path, parameters, signedNonce));
        var encrypted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in plain)
        {
            encrypted[pair.Key] = Encrypt(pair.Value, signedNonceBytes);
        }
        var signature = Sha1Base64(BuildSignatureText(method, path, encrypted, signedNonce));
        return new SignedRequest(encrypted, signature, nonce, signedNonceBytes);
    }

    public static string DecryptResponse(string body, byte[] signedNonce)
    {
        var encrypted = Convert.FromBase64String(body.Trim());
        return Encoding.UTF8.GetString(Rc4Drop(encrypted, signedNonce));
    }

    public static byte[] DecryptResponse(byte[] body, byte[] signedNonce) =>
        Rc4Drop(Convert.FromBase64String(Encoding.ASCII.GetString(body).Trim()), signedNonce);

    public static string Sha1Base64(string value) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Encrypt(string value, byte[] signedNonce) =>
        Convert.ToBase64String(Rc4Drop(Encoding.UTF8.GetBytes(value), signedNonce));

    private static string BuildSignatureText(
        string method,
        string path,
        IReadOnlyDictionary<string, string> parameters,
        string signedNonce)
    {
        var parts = new List<string> { method.ToUpperInvariant(), new Uri("https://api.miwifi.com" + path).AbsolutePath };
        parts.AddRange(parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
        parts.Add(signedNonce);
        return string.Join('&', parts);
    }

    private static byte[] Rc4Drop(byte[] input, byte[] key)
    {
        var state = new byte[256];
        for (var index = 0; index < state.Length; index++)
        {
            state[index] = (byte)index;
        }
        var j = 0;
        for (var index = 0; index < state.Length; index++)
        {
            j = (j + state[index] + key[index % key.Length]) & 0xff;
            (state[index], state[j]) = (state[j], state[index]);
        }

        var i = 0;
        j = 0;
        byte Next()
        {
            i = (i + 1) & 0xff;
            j = (j + state[i]) & 0xff;
            (state[i], state[j]) = (state[j], state[i]);
            return state[(state[i] + state[j]) & 0xff];
        }
        for (var drop = 0; drop < 1024; drop++)
        {
            _ = Next();
        }
        var output = new byte[input.Length];
        for (var index = 0; index < input.Length; index++)
        {
            output[index] = (byte)(input[index] ^ Next());
        }
        return output;
    }
}

internal sealed record SignedRequest(
    IReadOnlyDictionary<string, string> Parameters,
    string Signature,
    string Nonce,
    byte[] SignedNonce);
