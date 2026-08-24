using System.Net;
using System.Text;
using System.Text.Json;

namespace CloudLight.Presence.RouterCloud.Probe;

internal sealed class MiHomePluginClient
{
    private const string RequestPath = "/v2/plugin/fetch_plugin";
    public static async Task<JsonDocument> FetchAsync(
        MigateSessionMaterial service,
        string model,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            latest_req = new
            {
                region = "CN",
                app_platform = "Android",
                plugins = new[] { new { model } },
                api_version = 10090,
                package_type = ""
            },
            backup_req = new
            {
                api_level = 10090,
                plugins = new[] { new { model } },
                app_platform = "phone"
            }
        });
        return await CallAsync(service, RequestPath, data, cancellationToken);
    }

    public static Task<JsonDocument> FetchHomeDevicesAsync(
        MigateSessionMaterial service,
        string homeId,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            home_owner = long.Parse(service.AccountUserId),
            home_id = long.Parse(homeId),
            limit = 200,
            get_split_device = true,
            support_smart_home = true
        });
        return CallAsync(service, "/v2/home/home_device_list", data, cancellationToken);
    }

    public static Task<JsonDocument> FetchRouterClientsAsync(
        MigateSessionMaterial service,
        string routerPartnerId,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            method = "GET",
            @params = new
            {
                routerID = routerPartnerId,
                locale = "zh_CN",
                v = "2",
                refresh = "1"
            }
        });
        return CallAsync(
            service,
            "/appgateway/third/miwifi/app/s/api/device_list",
            data,
            cancellationToken);
    }

    public static Task<JsonDocument> FetchRouterRemoteDeviceListAsync(
        MigateSessionMaterial service,
        string routerPartnerId,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            method = "GET",
            @params = new { deviceId = routerPartnerId }
        });
        return CallAsync(
            service,
            "/appgateway/third/miwifi/app/r/api/xqsystem/device_list",
            data,
            cancellationToken);
    }

    public static ClientList ParseRouterClients(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || code.ToString() != "0")
        {
            throw new ProbeException(
                ProbeErrorCategory.InvalidResponse,
                $"Mi Home Router Gateway rejected device_list (code={code}).");
        }
        if (!root.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
        {
            throw new ProbeException(
                ProbeErrorCategory.InvalidResponse,
                "Mi Home Router Gateway returned no devices array.");
        }
        var clients = devices.EnumerateArray().Select(item => new RouterClient(
            ReadString(item, "mac") ?? "(missing)",
            ReadString(item, "mac"),
            ReadString(item, "name") is { Length: > 0 } name ? name : ReadString(item, "originName"),
            ReadString(item, "ip"),
            ReadInt(item, "online") == 1,
            ReadLong(item, "onlineTime"),
            ReadInt(item, "connectionType"),
            ReadInt(item, "signal"),
            ReadLong(item, "dSpeed"),
            ReadLong(item, "uSpeed"),
            ReadLong(item, "totalRX"),
            ReadLong(item, "totalTX"))).ToArray();
        return new ClientList(clients, DateTimeOffset.Now);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static int? ReadInt(JsonElement item, string name) =>
        int.TryParse(ReadString(item, name), out var value) ? value : null;

    private static long? ReadLong(JsonElement item, string name) =>
        long.TryParse(ReadString(item, name), out var value) ? value : null;

    private static async Task<JsonDocument> CallAsync(
        MigateSessionMaterial service,
        string path,
        string data,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All
        };
        var apiUri = new Uri("https://api.io.mi.com/");
        handler.CookieContainer.Add(apiUri, new Cookie("userId", service.UserId, "/", apiUri.Host));
        handler.CookieContainer.Add(apiUri, new Cookie("serviceToken", service.ServiceToken, "/", apiUri.Host));
        handler.CookieContainer.Add(apiUri, new Cookie("yetAnotherServiceToken", service.ServiceToken, "/", apiUri.Host));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("APP/com.xiaomi.mihome APPV/10.5.201");
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        http.DefaultRequestHeaders.TryAddWithoutValidation("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");

        var signed = MiWifiCrypto.Sign(
            "POST",
            path,
            new Dictionary<string, string> { ["data"] = data },
            service.Ssecurity);
        var query = signed.Parameters
            .Append(new KeyValuePair<string, string>("signature", signed.Signature))
            .Append(new KeyValuePair<string, string>("ssecurity", service.Ssecurity))
            .Append(new KeyValuePair<string, string>("_nonce", signed.Nonce))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        var requestUrl = "https://api.io.mi.com/app" + path;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{requestUrl}?{string.Join('&', query)}")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Mi Home plugin metadata returned HTTP {(int)response.StatusCode}.");
        }
        return JsonDocument.Parse(MiWifiCrypto.DecryptResponse(body, signed.SignedNonce));
    }
}
