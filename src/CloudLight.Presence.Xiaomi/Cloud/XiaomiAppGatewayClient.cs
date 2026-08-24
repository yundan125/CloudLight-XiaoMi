using System.Net;
using System.Text;
using System.Text.Json;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Xiaomi.Authentication;

namespace CloudLight.Presence.Xiaomi.Cloud;

internal sealed class XiaomiAppGatewayClient
{
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<JsonDocument> CallAsync(XiaomiSession session, string path, object data, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(data);
        var signed = AppGatewayCrypto.Sign("POST", path, new Dictionary<string, string> { ["data"] = payload }, session.Ssecurity);
        var query = signed.Parameters.Append(new("signature", signed.Signature)).Append(new("ssecurity", session.Ssecurity)).Append(new("_nonce", signed.Nonce))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.io.mi.com/app{path}?{string.Join('&', query)}")
        { Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.TryAddWithoutValidation("Cookie", $"userId={session.UserId}; serviceToken={session.ServiceToken}; yetAnotherServiceToken={session.ServiceToken}");
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new XiaomiCloudException("Xiaomi xiaomiio 会话已失效。", authenticationExpired: true);
        if (!response.IsSuccessStatusCode) throw new XiaomiCloudException($"Xiaomi AppGateway HTTP {(int)response.StatusCode}。");
        JsonDocument document;
        try { document = JsonDocument.Parse(AppGatewayCrypto.Decrypt(body, signed.SignedNonce)); }
        catch (Exception exception) when (exception is JsonException or FormatException)
        { throw new XiaomiCloudException("Xiaomi AppGateway 返回无法解析的数据。", inner: exception); }
        if (IsAuthenticationFailure(document.RootElement))
        {
            document.Dispose(); throw new XiaomiCloudException("Xiaomi xiaomiio 服务令牌已失效。", authenticationExpired: true);
        }
        return document;
    }

    public async Task<IReadOnlyList<(string Id, string Name, Dictionary<string, string?> Rooms)>> GetHomesAsync(XiaomiSession session, CancellationToken cancellationToken)
    {
        using var document = await CallAsync(session, "/v2/homeroom/gethome", new { limit = 150, fetch_share = true, fetch_share_dev = true, plat_form = 0, app_ver = 9 }, cancellationToken);
        var result = RequireResult(document.RootElement, "home list"); var homes = new List<(string, string, Dictionary<string, string?>)>();
        foreach (var listName in new[] { "homelist", "share_home_list" })
        {
            if (!result.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var home in list.EnumerateArray())
            {
                var id = Text(home, "id");
                if (id is null) continue;
                var rooms = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (home.TryGetProperty("dids", out var dids) && dids.ValueKind == JsonValueKind.Array)
                    foreach (var did in dids.EnumerateArray()) if (did.ToString() is { Length: > 0 } value) rooms[value] = null;
                if (home.TryGetProperty("roomlist", out var roomList) && roomList.ValueKind == JsonValueKind.Array)
                    foreach (var room in roomList.EnumerateArray()) if (room.TryGetProperty("dids", out var roomDids))
                        foreach (var did in roomDids.EnumerateArray()) rooms[did.ToString()] = Text(room, "id");
                homes.Add((id, Text(home, "name") ?? "Xiaomi Home", rooms));
            }
        }
        return homes;
    }

    public async Task<JsonDocument> GetHomeDevicesAsync(XiaomiSession session, string homeId, CancellationToken cancellationToken) =>
        await CallAsync(session, "/v2/home/home_device_list", new { home_owner = long.Parse(session.AccountUserId ?? session.UserId), home_id = long.Parse(homeId), limit = 200, get_split_device = true, support_smart_home = true }, cancellationToken);

    public async Task<IReadOnlyList<ObservedNetworkDevice>> GetRouterClientsAsync(XiaomiSession session, string partnerId, CancellationToken cancellationToken)
    {
        using var document = await CallAsync(session, "/appgateway/third/miwifi/app/s/api/device_list", new { method = "GET", @params = new { routerID = partnerId, locale = "zh_CN", v = "2", refresh = "1" } }, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.ToString() != "0" || !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            throw new XiaomiCloudException($"device_list 响应无效（code={(root.TryGetProperty("code", out code) ? code.ToString() : "missing")}）。");
        return devices.EnumerateArray().Select(item => new ObservedNetworkDevice(
            Text(item, "mac") ?? throw new XiaomiCloudException("device_list 设备缺少 MAC。"), Text(item, "name"), Text(item, "originName"), Text(item, "ip"),
            Integer(item, "online") == 1, Long(item, "onlineTime"), ConnectionName(Integer(item, "connectionType")), Integer(item, "signal"),
            Long(item, "dSpeed"), Long(item, "uSpeed"), (Long(item, "totalRX") ?? 0) + (Long(item, "totalTX") ?? 0))).ToArray();
    }

    public static IEnumerable<JsonElement> FindRouterObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (Text(root, "model")?.Contains(".router.", StringComparison.OrdinalIgnoreCase) == true) yield return root;
            foreach (var property in root.EnumerateObject()) foreach (var item in FindRouterObjects(property.Value)) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Array) foreach (var value in root.EnumerateArray()) foreach (var item in FindRouterObjects(value)) yield return item;
    }

    public static string? Text(JsonElement item, params string[] names) { foreach (var name in names) if (item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null) return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(); return null; }
    private static int? Integer(JsonElement item, string name) => int.TryParse(Text(item, name), out var value) ? value : null;
    private static long? Long(JsonElement item, string name) => long.TryParse(Text(item, name), out var value) ? value : null;
    private static string ConnectionName(int? value) => value switch { 1 => "2.4G", 2 => "5G", 3 => "访客网络", 4 => "有线", 5 => "Zigbee", null => "未知", _ => $"未知({value})" };
    private static JsonElement RequireResult(JsonElement root, string operation) => root.TryGetProperty("result", out var result) ? result : throw new XiaomiCloudException($"Xiaomi {operation} 响应缺少 result。");
    private static bool IsAuthenticationFailure(JsonElement root) => root.TryGetProperty("code", out var code) && (code.ToString() is "401" or "403" or "70016" or "-6");
    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("APP/com.xiaomi.mihome APPV/10.5.201");
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        http.DefaultRequestHeaders.TryAddWithoutValidation("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");
        return http;
    }
}
