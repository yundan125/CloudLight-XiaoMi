using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Xiaomi.Authentication;
using CloudLight.Presence.Xiaomi.Cloud;
using Xunit;

namespace CloudLight.Presence.Tests;

public sealed class XiaomiOfficialControlRequestTests
{
    [Fact]
    public async Task PowerSetUsesOfficialEndpointAndTheResolvedSiidPiid()
    {
        var ssecurity = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var session = new XiaomiSession(
            4,
            "cn",
            "user",
            "user",
            "user",
            "device",
            "pass",
            "service",
            ssecurity,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var handler = new EncryptedResponseHandler(session);
        using var http = new HttpClient(handler);
        var client = new XiaomiAppGatewayClient(http);

        var result = await client.SetPowerPropertyAsync(
            session,
            new XiaomiPowerCapability(3, 2, true, true),
            "did-switch",
            true,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://api.io.mi.com/app/miotspec/prop/set", handler.RequestUri?.GetLeftPart(UriPartial.Path));
        Assert.True(handler.Payload.HasValue);
        var payload = handler.Payload!.Value.GetProperty("params")[0];
        Assert.Equal("did-switch", payload.GetProperty("did").GetString());
        Assert.Equal(3, payload.GetProperty("siid").GetInt32());
        Assert.Equal(2, payload.GetProperty("piid").GetInt32());
        Assert.True(payload.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task ActionUsesOfficialEndpointResolvedSiidAiidAndDynamicArguments()
    {
        var ssecurity = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var session = new XiaomiSession(
            4,
            "cn",
            "user",
            "user",
            "user",
            "device",
            "pass",
            "service",
            ssecurity,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var handler = new EncryptedResponseHandler(session);
        using var http = new HttpClient(handler);
        var client = new XiaomiAppGatewayClient(http);
        var action = new XiaomiActionDefinition(
            7, 4, "urn:miot-spec-v2:action:execute:00002804:1", "execute", "执行", [], []);

        var result = await client.InvokeActionAsync(session, "did-device", action, [30L, 2L], CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://api.io.mi.com/app/miotspec/action", handler.RequestUri?.GetLeftPart(UriPartial.Path));
        Assert.True(handler.Payload.HasValue);
        var payload = handler.Payload!.Value.GetProperty("params");
        Assert.Equal("did-device", payload.GetProperty("did").GetString());
        Assert.Equal(7, payload.GetProperty("siid").GetInt32());
        Assert.Equal(4, payload.GetProperty("aiid").GetInt32());
        Assert.Equal([30L, 2L], payload.GetProperty("value").EnumerateArray().Select(value => value.GetInt64()).ToArray());
    }

    [Fact]
    public async Task PropertyReadUsesOfficialEndpointAndReturnsTheCurrentTypedValue()
    {
        var ssecurity = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var session = new XiaomiSession(
            4,
            "cn",
            "user",
            "user",
            "user",
            "device",
            "pass",
            "service",
            ssecurity,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var handler = new EncryptedResponseHandler(session)
        {
            ResponsePayload = "{\"code\":0,\"result\":[{\"siid\":8,\"piid\":3,\"code\":0,\"value\":60}]}"
        };
        using var http = new HttpClient(handler);
        var client = new XiaomiAppGatewayClient(http);
        var property = new XiaomiPropertyDefinition(
            8, 3, "urn:miot-spec-v2:property:brightness:0000000D:1", "brightness", "亮度", true, true,
            false, XiaomiMiotValueType.Integer, new XiaomiValueRange(1, 100, 1), [], "%");

        var result = Assert.Single(await client.GetPropertiesAsync(session, "did-light", [property], CancellationToken.None));

        Assert.True(result.Success);
        Assert.Equal(60L, result.Value);
        Assert.Equal("https://api.io.mi.com/app/miotspec/prop/get", handler.RequestUri?.GetLeftPart(UriPartial.Path));
        Assert.True(handler.Payload.HasValue);
        var payload = handler.Payload!.Value.GetProperty("params")[0];
        Assert.Equal("did-light", payload.GetProperty("did").GetString());
        Assert.Equal(8, payload.GetProperty("siid").GetInt32());
        Assert.Equal(3, payload.GetProperty("piid").GetInt32());
    }

    private sealed class EncryptedResponseHandler(XiaomiSession session) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public JsonElement? Payload { get; private set; }
        public string ResponsePayload { get; init; } = "{\"code\":0,\"result\":[{\"code\":0}]}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            var query = request.RequestUri!.Query.TrimStart('&', '?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split('=', 2))
                .ToDictionary(value => Uri.UnescapeDataString(value[0]), value => Uri.UnescapeDataString(value[1]), StringComparer.Ordinal);
            var nonceBytes = Convert.FromBase64String(query["_nonce"]);
            var signedNonce = SHA256.HashData(Convert.FromBase64String(session.Ssecurity).Concat(nonceBytes).ToArray());
            var plain = AppGatewayCrypto.Decrypt(Encoding.ASCII.GetBytes(query["data"]), signedNonce);
            using var requestDocument = JsonDocument.Parse(plain);
            Payload = requestDocument.RootElement.Clone();

            var response = Convert.ToBase64String(Rc4Drop(
                Encoding.UTF8.GetBytes(ResponsePayload),
                signedNonce));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes(response))
            });
        }

        private static byte[] Rc4Drop(byte[] input, byte[] key)
        {
            var state = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            var j = 0;
            for (var index = 0; index < 256; index++)
            {
                j = (j + state[index] + key[index % key.Length]) & 255;
                (state[index], state[j]) = (state[j], state[index]);
            }

            var i = 0;
            j = 0;
            byte Next()
            {
                i = (i + 1) & 255;
                j = (j + state[i]) & 255;
                (state[i], state[j]) = (state[j], state[i]);
                return state[(state[i] + state[j]) & 255];
            }

            for (var index = 0; index < 1024; index++) _ = Next();
            var output = new byte[input.Length];
            for (var index = 0; index < input.Length; index++) output[index] = (byte)(input[index] ^ Next());
            return output;
        }
    }
}
