using System.Net;
using System.Text;
using System.Text.Json;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Xiaomi.Authentication;

namespace CloudLight.Presence.Xiaomi.Cloud;

internal sealed class XiaomiAppGatewayClient
{
    private readonly HttpClient _http;

    public XiaomiAppGatewayClient(HttpClient? http = null) => _http = http ?? CreateHttpClient();

    public async Task<JsonDocument> CallAsync(
        XiaomiSession session,
        string path,
        object data,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(data);
        var signed = AppGatewayCrypto.Sign(
            "POST",
            path,
            new Dictionary<string, string> { ["data"] = payload },
            session.Ssecurity);
        var query = signed.Parameters
            .Append(new("signature", signed.Signature))
            .Append(new("ssecurity", session.Ssecurity))
            .Append(new("_nonce", signed.Nonce))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{XiaomiApiEndpoints.AppGatewayBaseUrl}{path}?{string.Join('&', query)}")
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"cUserId={session.CUserId ?? session.UserId}; userId={session.UserId}; " +
            $"serviceToken={session.ServiceToken}; yetAnotherServiceToken={session.ServiceToken}");
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new XiaomiCloudException("Xiaomi xiaomiio 会话已失效。", authenticationExpired: true);
        if (!response.IsSuccessStatusCode)
            throw new XiaomiCloudException($"Xiaomi AppGateway HTTP {(int)response.StatusCode}。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(AppGatewayCrypto.Decrypt(body, signed.SignedNonce));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new XiaomiCloudException("Xiaomi AppGateway 返回无法解析的数据。", inner: exception);
        }

        if (IsAuthenticationFailure(document.RootElement))
        {
            document.Dispose();
            throw new XiaomiCloudException("Xiaomi xiaomiio 服务令牌已失效。", authenticationExpired: true);
        }
        return document;
    }

    public async Task<IReadOnlyList<XiaomiHomeInfo>> GetHomesAsync(
        XiaomiSession session,
        CancellationToken cancellationToken)
    {
        using var document = await CallAsync(
            session,
            "/v2/homeroom/gethome",
            new { limit = 150, fetch_share = true, fetch_share_dev = true, plat_form = 0, app_ver = 9 },
            cancellationToken);
        var result = RequireResult(document.RootElement, "home list");
        var homes = new List<XiaomiHomeInfo>();
        foreach (var listName in new[] { "homelist", "share_home_list" })
        {
            if (!result.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var home in list.EnumerateArray())
            {
                var id = Text(home, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var ownerId = Text(home, "uid", "owner_uid", "ownerId") ?? session.AccountUserId ?? session.UserId;
                var shared = string.Equals(listName, "share_home_list", StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(ownerId, session.AccountUserId ?? session.UserId, StringComparison.Ordinal);
                homes.Add(new XiaomiHomeInfo(id, Text(home, "name") ?? "Xiaomi Home", ownerId, shared, ReadRooms(home)));
            }
        }

        return homes
            .GroupBy(value => value.Id, StringComparer.Ordinal)
            .Select(group => group.FirstOrDefault(value => !value.IsShared) ?? group.First())
            .ToArray();
    }

    public async Task<IReadOnlyList<XiaomiDiscoveredAccountDevice>> GetAccountDevicesAsync(
        XiaomiSession session,
        CancellationToken cancellationToken)
    {
        var homes = await GetHomesAsync(session, cancellationToken);
        var devices = new Dictionary<string, XiaomiDiscoveredAccountDevice>(StringComparer.Ordinal);
        XiaomiCloudException? lastHomeError = null;

        foreach (var home in homes)
        {
            try
            {
                var objects = await GetHomeDeviceObjectsAsync(session, home, cancellationToken);
                foreach (var item in objects)
                {
                    var did = Text(item, "did");
                    if (string.IsNullOrWhiteSpace(did)) continue;
                    var roomId = Text(item, "room_id", "roomId") ?? home.Rooms.GetValueOrDefault(did)?.Id;
                    var room = roomId is not null
                        ? home.Rooms.Values.FirstOrDefault(value => string.Equals(value.Id, roomId, StringComparison.Ordinal))
                        : null;
                    AddPreferOwned(devices, new XiaomiDiscoveredAccountDevice(item, home, room, home.IsShared));
                }
            }
            catch (XiaomiCloudException exception) when (!exception.AuthenticationExpired)
            {
                lastHomeError = exception;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastHomeError = new XiaomiCloudException("Xiaomi 家庭设备列表暂时不可用。", inner: exception);
            }
        }

        try
        {
            foreach (var item in await GetSharedDeviceObjectsAsync(session, cancellationToken))
            {
                var did = Text(item, "did");
                if (string.IsNullOrWhiteSpace(did)) continue;
                AddPreferOwned(devices, new XiaomiDiscoveredAccountDevice(item, null, null, true));
            }
        }
        catch (XiaomiCloudException exception) when (exception.AuthenticationExpired)
        {
            throw;
        }
        catch (XiaomiCloudException)
        {
            // A shared-device endpoint can be unavailable while home lists are healthy.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A shared-device endpoint can be unavailable while home lists are healthy.
        }

        if (devices.Count == 0 && lastHomeError is not null) throw lastHomeError;
        return devices.Values.ToArray();
    }

    public Task<JsonDocument> GetHomeDevicesAsync(
        XiaomiSession session,
        string homeId,
        CancellationToken cancellationToken) =>
        CallAsync(
            session,
            "/v2/home/home_device_list",
            new
            {
                home_owner = long.Parse(session.AccountUserId ?? session.UserId),
                home_id = long.Parse(homeId),
                limit = 200,
                get_split_device = true,
                support_smart_home = true
            },
            cancellationToken);

    public async Task<IReadOnlyList<ObservedNetworkDevice>> GetRouterClientsAsync(
        XiaomiSession session,
        string partnerId,
        CancellationToken cancellationToken)
        => (await GetRouterClientsWithDiagnosticsAsync(session, partnerId, cancellationToken)).Devices;

    public async Task<RouterClientProbeResult> GetRouterClientsWithDiagnosticsAsync(
        XiaomiSession session,
        string partnerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(partnerId))
            return new([], null, [], "未返回 Router ID / partner_id。", false);

        using var document = await CallAsync(
            session,
            XiaomiApiEndpoints.RouterClientListPath,
            new
            {
                method = "GET",
                @params = new { routerID = partnerId, locale = "zh_CN", v = "2", refresh = "1" }
            },
            cancellationToken);
        var root = document.RootElement;
        var body = root.TryGetProperty("devices", out _)
            ? root
            : root.TryGetProperty("result", out var nested) ? nested : root;
        var hasCode = TryCode(root, out var code) || TryCode(body, out code);
        if (!hasCode || code != 0)
            return new([], code, [], $"device_list API 返回错误（code={(hasCode ? code.ToString() : "missing")}）。", false);
        if (!body.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return new([], code, ["code"], "未返回客户端列表字段 devices。", false);

        var fields = devices.EnumerateArray().SelectMany(value => value.EnumerateObject().Select(property => property.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var normalized = devices.EnumerateArray().Select(item => new ObservedNetworkDevice(
            Text(item, "mac") ?? throw new XiaomiCloudException("device_list 设备缺少 MAC。"),
            Text(item, "name"),
            Text(item, "originName"),
            Text(item, "ip"),
            Integer(item, "online") == 1,
            Long(item, "onlineTime"),
            ConnectionName(Integer(item, "connectionType")),
            Integer(item, "signal"),
            Long(item, "dSpeed"),
            Long(item, "uSpeed"),
            (Long(item, "totalRX") ?? 0) + (Long(item, "totalTX") ?? 0))).ToArray();
        return new(normalized, code, fields, null, true);
    }

    public async Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(
        XiaomiSession session,
        string did,
        IReadOnlyList<XiaomiPropertyDefinition> properties,
        CancellationToken cancellationToken)
    {
        if (properties.Count == 0) return [];
        using var document = await CallAsync(
            session,
            "/miotspec/prop/get",
            new
            {
                datasource = 1,
                @params = properties.Select(value => new { did, siid = value.Siid, piid = value.Piid }).ToArray()
            },
            cancellationToken);
        var result = RequireResult(document.RootElement, "property read");
        if (result.ValueKind != JsonValueKind.Array)
            throw new XiaomiCloudException("Xiaomi property read 响应缺少属性结果。");

        var values = new Dictionary<(int Siid, int Piid), XiaomiPropertyReadResult>();
        foreach (var item in result.EnumerateArray())
        {
            var siid = Integer(item, "siid") ?? 0;
            var piid = Integer(item, "piid") ?? 0;
            var code = Integer(item, "code") ?? -1;
            var success = code == 0;
            values[(siid, piid)] = item.TryGetProperty("value", out var value)
                ? new XiaomiPropertyReadResult(siid, piid, success, success ? ReadUntyped(value) : null, code,
                    success ? null : $"Xiaomi 属性读取失败（code={code}）。")
                : new XiaomiPropertyReadResult(siid, piid, false, null, code,
                    success ? "Xiaomi 属性读取结果缺少值。" : $"Xiaomi 属性读取失败（code={code}）。");
        }

        return properties.Select(property => values.TryGetValue((property.Siid, property.Piid), out var value)
            ? value
            : new XiaomiPropertyReadResult(property.Siid, property.Piid, false, null, null, "Xiaomi 属性读取结果缺失。"))
            .ToArray();
    }

    public async Task<XiaomiPropertyOperationResult> SetPropertyAsync(
        XiaomiSession session,
        string did,
        XiaomiPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken)
    {
        using var document = await CallAsync(
            session,
            "/miotspec/prop/set",
            new
            {
                @params = new[] { new { did, siid = property.Siid, piid = property.Piid, value } }
            },
            cancellationToken);
        var item = FirstPropertyResult(document.RootElement, "property set");
        var code = Integer(item, "code") ?? -1;
        return code is 0 or 1
            ? new XiaomiPropertyOperationResult(true, code)
            : new XiaomiPropertyOperationResult(false, code, $"Xiaomi 属性设置失败（code={code}）。");
    }

    public async Task<XiaomiActionInvocationResult> InvokeActionAsync(
        XiaomiSession session,
        string did,
        XiaomiActionDefinition action,
        IReadOnlyList<object?> inputArguments,
        CancellationToken cancellationToken)
    {
        using var document = await CallAsync(
            session,
            "/miotspec/action",
            new
            {
                @params = new { did, siid = action.Siid, aiid = action.Aiid, value = inputArguments.ToArray() }
            },
            cancellationToken);
        var result = RequireResult(document.RootElement, "action");
        var item = result.ValueKind == JsonValueKind.Array
            ? result.GetArrayLength() > 0 ? result[0] : throw new XiaomiCloudException("Xiaomi action 响应缺少结果。")
            : result;
        var code = Integer(item, "code") ?? -1;
        if (code is not (0 or 1))
            return new XiaomiActionInvocationResult(false, [], code, $"Xiaomi 操作失败（code={code}）。");
        var output = item.TryGetProperty("out", out var outputValues) && outputValues.ValueKind == JsonValueKind.Array
            ? outputValues.EnumerateArray().Select(ReadUntyped).ToArray()
            : [];
        return new XiaomiActionInvocationResult(true, output, code);
    }

    public async Task<XiaomiPowerStateResult> GetPowerPropertyAsync(
        XiaomiSession session,
        XiaomiPowerCapability capability,
        string did,
        CancellationToken cancellationToken)
    {
        var property = new XiaomiPropertyDefinition(
            capability.Siid,
            capability.Piid,
            capability.PropertyType ?? string.Empty,
            "on",
            "电源",
            capability.Readable,
            capability.Writable,
            false,
            XiaomiMiotValueType.Boolean,
            null,
            [],
            null);
        var result = (await GetPropertiesAsync(session, did, [property], cancellationToken)).Single();
        if (!result.Success) return new XiaomiPowerStateResult(false, null, result.XiaomiCode, result.Error);
        if (result.Value is not bool value)
            return new XiaomiPowerStateResult(false, null, result.XiaomiCode, "Xiaomi 属性读取结果不是布尔值。");
        return new XiaomiPowerStateResult(true, value, result.XiaomiCode);
    }

    public async Task<XiaomiPowerStateResult> SetPowerPropertyAsync(
        XiaomiSession session,
        XiaomiPowerCapability capability,
        string did,
        bool value,
        CancellationToken cancellationToken)
    {
        var property = new XiaomiPropertyDefinition(
            capability.Siid,
            capability.Piid,
            capability.PropertyType ?? string.Empty,
            "on",
            "电源",
            capability.Readable,
            capability.Writable,
            false,
            XiaomiMiotValueType.Boolean,
            null,
            [],
            null);
        var result = await SetPropertyAsync(session, did, property, value, cancellationToken);
        return new XiaomiPowerStateResult(result.Success, null, result.XiaomiCode, result.Error);
    }

    public static IEnumerable<JsonElement> FindRouterObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (Text(root, "model")?.Contains(".router.", StringComparison.OrdinalIgnoreCase) == true)
                yield return root;
            foreach (var property in root.EnumerateObject())
                foreach (var item in FindRouterObjects(property.Value)) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in root.EnumerateArray())
                foreach (var item in FindRouterObjects(value)) yield return item;
        }
    }

    public static string? Text(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return null;
    }

    private async Task<IReadOnlyList<JsonElement>> GetHomeDeviceObjectsAsync(
        XiaomiSession session,
        XiaomiHomeInfo home,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(home.Id, out var homeId))
            throw new XiaomiCloudException("Xiaomi 家庭 ID 无法解析。");
        if (!long.TryParse(home.OwnerId ?? session.AccountUserId ?? session.UserId, out var ownerId))
            throw new XiaomiCloudException("Xiaomi 家庭所有者 ID 无法解析。");

        var devices = new List<JsonElement>();
        string? startDid = null;
        for (var page = 0; page < 100; page++)
        {
            var payload = new Dictionary<string, object?>
            {
                ["home_owner"] = ownerId,
                ["home_id"] = homeId,
                ["limit"] = 200,
                ["get_split_device"] = true,
                ["support_smart_home"] = true,
                ["get_cariot_device"] = true,
                ["get_third_device"] = true
            };
            if (!string.IsNullOrWhiteSpace(startDid)) payload["start_did"] = startDid;
            using var document = await CallAsync(session, "/v2/home/home_device_list", payload, cancellationToken);
            var result = RequireResult(document.RootElement, "home device list");
            foreach (var item in FindDeviceObjects(result)) devices.Add(item);
            var hasMore = Boolean(result, "has_more") == true;
            var next = Text(result, "max_did", "next_start_did");
            if (!hasMore || string.IsNullOrWhiteSpace(next) || string.Equals(next, startDid, StringComparison.Ordinal)) break;
            startDid = next;
        }

        return devices
            .GroupBy(value => Text(value, "did") ?? Guid.NewGuid().ToString("N"), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<JsonElement>> GetSharedDeviceObjectsAsync(
        XiaomiSession session,
        CancellationToken cancellationToken)
    {
        using var document = await CallAsync(
            session,
            "/v2/home/device_list_page",
            new
            {
                ssid = "<unknown ssid>",
                bssid = "02:00:00:00:00:00",
                getVirtualModel = true,
                getHuamiDevices = 1,
                get_split_device = true,
                support_smart_home = true,
                get_cariot_device = true,
                get_third_device = true,
                get_phone_device = true,
                get_miwear_device = true
            },
            cancellationToken);
        var result = RequireResult(document.RootElement, "shared device list");
        if (!result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Where(IsSharedDeviceCandidate).Select(value => value.Clone()).ToArray();
    }

    private static bool IsSharedDeviceCandidate(JsonElement item)
    {
        var owner = Boolean(item, "owner");
        var shared = Boolean(item, "is_shared", "isShared", "shared");
        return owner == true || shared == true || (owner is null && shared is null);
    }

    private static void AddPreferOwned(
        IDictionary<string, XiaomiDiscoveredAccountDevice> devices,
        XiaomiDiscoveredAccountDevice discovered)
    {
        var did = Text(discovered.Data, "did");
        if (string.IsNullOrWhiteSpace(did)) return;
        if (!devices.TryGetValue(did, out var existing) || (existing.IsShared && !discovered.IsShared))
            devices[did] = discovered;
    }

    private static IReadOnlyDictionary<string, XiaomiRoomInfo> ReadRooms(JsonElement home)
    {
        var rooms = new Dictionary<string, XiaomiRoomInfo>(StringComparer.Ordinal);
        if (home.TryGetProperty("roomlist", out var roomList) && roomList.ValueKind == JsonValueKind.Array)
            foreach (var room in roomList.EnumerateArray())
            {
                var roomId = Text(room, "id");
                if (string.IsNullOrWhiteSpace(roomId) || !room.TryGetProperty("dids", out var dids) || dids.ValueKind != JsonValueKind.Array)
                    continue;
                var info = new XiaomiRoomInfo(roomId, Text(room, "name"));
                foreach (var did in dids.EnumerateArray())
                {
                    var value = did.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) rooms[value] = info;
                }
            }
        return rooms;
    }

    private static IEnumerable<JsonElement> FindDeviceObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(Text(root, "did")) &&
                (Text(root, "model") is not null || Text(root, "name") is not null))
                yield return root.Clone();
            foreach (var property in root.EnumerateObject())
                foreach (var item in FindDeviceObjects(property.Value)) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in root.EnumerateArray())
                foreach (var item in FindDeviceObjects(value)) yield return item;
        }
    }

    private static JsonElement FirstPropertyResult(JsonElement root, string operation)
    {
        var result = RequireResult(root, operation);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new XiaomiCloudException($"Xiaomi {operation} 响应缺少属性结果。");
        return result[0];
    }

    private static JsonElement RequireResult(JsonElement root, string operation)
    {
        if (TryCode(root, out var code) && code != 0)
            throw new XiaomiCloudException($"Xiaomi {operation} 请求失败（code={code}）。", xiaomiCode: code);
        return root.TryGetProperty("result", out var result)
            ? result
            : throw new XiaomiCloudException($"Xiaomi {operation} 响应缺少 result。");
    }

    private static bool IsAuthenticationFailure(JsonElement root) =>
        TryCode(root, out var code) && code is 401 or 403 or 70016 or -6;

    private static bool TryCode(JsonElement item, out int code)
    {
        code = 0;
        if (item.ValueKind != JsonValueKind.Object) return false;
        return item.TryGetProperty("code", out var value) && int.TryParse(value.ToString(), out code);
    }

    private static bool? Boolean(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && TryBoolean(value, out var result)) return result;
        return null;
    }

    private static bool TryBoolean(JsonElement value, out bool result)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }
        if (int.TryParse(value.ToString(), out var integer) && integer is 0 or 1)
        {
            result = integer == 1;
            return true;
        }
        if (bool.TryParse(value.ToString(), out result)) return true;
        return false;
    }

    private static object? ReadUntyped(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.Clone()
    };

    private static int? Integer(JsonElement item, string name) =>
        int.TryParse(Text(item, name), out var value) ? value : null;

    private static long? Long(JsonElement item, string name) =>
        long.TryParse(Text(item, name), out var value) ? value : null;

    private static string ConnectionName(int? value) => value switch
    {
        1 => "2.4G",
        2 => "5G",
        3 => "访客网络",
        4 => "有线",
        5 => "Zigbee",
        null => "未知",
        _ => $"未知({value})"
    };

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("APP/com.xiaomi.mihome APPV/10.5.201");
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-xiaomi-protocal-flag-cli", "PROTOCAL-HTTP2");
        http.DefaultRequestHeaders.TryAddWithoutValidation("MIOT-ENCRYPT-ALGORITHM", "ENCRYPT-RC4");
        return http;
    }
}

internal sealed record RouterClientProbeResult(
    IReadOnlyList<ObservedNetworkDevice> Devices,
    int? ApiCode,
    IReadOnlyList<string> SuccessfulFields,
    string? Error,
    bool ClientListAvailable);

internal sealed record XiaomiHomeInfo(
    string Id,
    string Name,
    string? OwnerId,
    bool IsShared,
    IReadOnlyDictionary<string, XiaomiRoomInfo> Rooms);

internal sealed record XiaomiRoomInfo(string Id, string? Name);

internal sealed record XiaomiDiscoveredAccountDevice(
    JsonElement Data,
    XiaomiHomeInfo? Home,
    XiaomiRoomInfo? Room,
    bool IsShared);
