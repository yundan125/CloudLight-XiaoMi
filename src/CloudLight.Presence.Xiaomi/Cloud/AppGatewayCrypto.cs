using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CloudLight.Presence.Xiaomi.Cloud;

internal static class AppGatewayCrypto
{
    public static SignedRequest Sign(string method, string path, IReadOnlyDictionary<string, string> parameters, string ssecurity)
    {
        Span<byte> nonceBytes = stackalloc byte[12]; RandomNumberGenerator.Fill(nonceBytes[..8]);
        BinaryPrimitives.WriteInt32BigEndian(nonceBytes[8..], checked((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60000)));
        var nonce = Convert.ToBase64String(nonceBytes);
        var signedNonceBytes = SHA256.HashData(Convert.FromBase64String(ssecurity).Concat(nonceBytes.ToArray()).ToArray());
        var signedNonce = Convert.ToBase64String(signedNonceBytes);
        var plain = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters) plain[parameter.Key] = parameter.Value;
        plain["rc4_hash__"] = Sha1(BuildText(method, path, parameters, signedNonce));
        var encrypted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in plain) encrypted[pair.Key] = Convert.ToBase64String(Rc4Drop(Encoding.UTF8.GetBytes(pair.Value), signedNonceBytes));
        return new SignedRequest(encrypted, Sha1(BuildText(method, path, encrypted, signedNonce)), nonce, signedNonceBytes);
    }

    public static byte[] Decrypt(byte[] body, byte[] signedNonce) => Rc4Drop(Convert.FromBase64String(Encoding.ASCII.GetString(body).Trim()), signedNonce);
    private static string Sha1(string value) => Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
    private static string BuildText(string method, string path, IReadOnlyDictionary<string, string> parameters, string nonce) =>
        string.Join('&', new[] { method.ToUpperInvariant(), new Uri("https://api.io.mi.com" + path).AbsolutePath }
            .Concat(parameters.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}"))
            .Append(nonce));
    private static byte[] Rc4Drop(byte[] input, byte[] key)
    {
        var state = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(); var j = 0;
        for (var x = 0; x < 256; x++) { j = (j + state[x] + key[x % key.Length]) & 255; (state[x], state[j]) = (state[j], state[x]); }
        var i = 0; j = 0;
        byte Next() { i = (i + 1) & 255; j = (j + state[i]) & 255; (state[i], state[j]) = (state[j], state[i]); return state[(state[i] + state[j]) & 255]; }
        for (var drop = 0; drop < 1024; drop++) _ = Next();
        var output = new byte[input.Length]; for (var x = 0; x < input.Length; x++) output[x] = (byte)(input[x] ^ Next()); return output;
    }
}

internal sealed record SignedRequest(IReadOnlyDictionary<string, string> Parameters, string Signature, string Nonce, byte[] SignedNonce);
