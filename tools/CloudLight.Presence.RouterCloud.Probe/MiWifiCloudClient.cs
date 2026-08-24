using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CloudLight.Presence.RouterCloud.Probe;

internal sealed class MiWifiCloudClient(HttpClient http, XiaomiRouterSession session)
{
    private const string ApiBase = "https://api.miwifi.com";

    public async Task<JsonDocument> CallRouterRemoteAsync(
        string routerIdentifier,
        string method,
        string localPath,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(routerIdentifier))
        {
            throw new ArgumentException("Router identifier is required.", nameof(routerIdentifier));
        }
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The Phase 1B probe currently supports GET only.");
        }
        if (!localPath.StartsWith("/api/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A local router Core API path is required.", nameof(localPath));
        }

        var remoteParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var pair in parameters)
            {
                remoteParameters[pair.Key] = pair.Value;
            }
        }

        // Xiaomi's ServerCallModifier injects all three aliases from the V13
        // routerId argument before signing and RC4-encrypting the request.
        remoteParameters["deviceId"] = routerIdentifier;
        remoteParameters["deviceID"] = routerIdentifier;
        remoteParameters["routerID"] = routerIdentifier;
        return await GetAsync($"/r{localPath}", remoteParameters, cancellationToken);
    }

    public async Task<RouterInfo> FindRouterAsync(string model, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            "/s/admin/deviceList", new Dictionary<string, string>(), cancellationToken);
        var root = document.RootElement;
        EnsureSuccess(root, "router list");
        var list = FindArray(root, "deviceList", "routerList", "list", "devices")
            ?? throw new InvalidOperationException("Router Cloud returned no router list.");

        var expectedHardware = model.Split('.').Last();
        foreach (var item in list.EnumerateArray())
        {
            var itemModel = GetString(item, "hardware", "model", "hardwareModel");
            if (!string.Equals(itemModel, model, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(itemModel, expectedHardware, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return new RouterInfo(
                GetString(item, "name", "routerName", "deviceName") ?? "(unnamed)",
                model,
                itemModel!,
                GetString(item, "miioDid", "did") ?? "(not returned by Router Cloud)",
                GetString(item, "serial", "routerId", "routerID")
                    ?? throw new InvalidOperationException("Router Cloud routerId missing."),
                GetString(item, "id", "routerPrivateId")
                    ?? throw new InvalidOperationException("Router Cloud routerPrivateId missing."),
                GetString(item, "status") ?? "(not returned)");
        }
        var diagnostics = list.EnumerateArray().Select(item =>
        {
            var fields = item.ValueKind == JsonValueKind.Object
                ? string.Join(',', item.EnumerateObject().Select(property => property.Name))
                : item.ValueKind.ToString();
            var hardware = GetString(item, "hardware", "model", "hardwareModel") ?? "(missing)";
            return $"hardware={hardware};fields={fields}";
        });
        var rootShape = root.ValueKind == JsonValueKind.Object
            ? string.Join(',', root.EnumerateObject().Select(property =>
                property.Value.ValueKind == JsonValueKind.Array
                    ? $"{property.Name}:array[{property.Value.GetArrayLength()}]"
                    : $"{property.Name}:{property.Value.ValueKind}"))
            : root.ValueKind.ToString();
        throw new InvalidOperationException(
            $"Router Cloud did not return model {model}. Root: {rootShape}. " +
            $"Candidates: {string.Join(" | ", diagnostics)}");
    }

    public async Task<ClientList> GetClientListAsync(
        RouterInfo router,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        using var document = await GetAsync("/s/api/device_list", new Dictionary<string, string>
        {
            ["routerID"] = router.RouterPrivateId,
            ["locale"] = "zh_CN",
            ["v"] = "2",
            ["refresh"] = forceRefresh ? "true" : "false"
        }, cancellationToken);
        var root = document.RootElement;
        EnsureSuccess(root, "remote client list");
        var list = FindArray(root, "devices", "list")
            ?? throw new InvalidOperationException("Router Cloud returned no devices array.");
        var clients = new List<RouterClient>();
        foreach (var item in list.EnumerateArray())
        {
            clients.Add(new RouterClient(
                GetString(item, "miot_id", "mac") ?? "(missing)",
                GetString(item, "mac"),
                GetString(item, "name", "originName"),
                GetString(item, "ip"),
                GetInt(item, "online") == 1,
                GetLong(item, "onlineTime", "online_time", "originatedTime"),
                GetInt(item, "connectionType", "type"),
                GetInt(item, "signal"),
                GetLong(item, "dSpeed"),
                GetLong(item, "uSpeed"),
                GetLong(item, "totalRX"),
                GetLong(item, "totalTX")));
        }
        return new ClientList(clients, DateTimeOffset.Now);
    }

    private async Task<JsonDocument> GetAsync(
        string path,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var signed = MiWifiCrypto.Sign("GET", path, parameters, session.Ssecurity);
        var query = signed.Parameters
            .Append(new KeyValuePair<string, string>("signature", signed.Signature))
            .Append(new KeyValuePair<string, string>("_nonce", signed.Nonce))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}?{string.Join('&', query)}");
        request.Headers.TryAddWithoutValidation("MiWiFi-Supported-Compression", "deflate");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProbeException(
                ProbeErrorCategory.AuthenticationExpired,
                "Router Cloud rejected the xiaoqiang service token (HTTP 401).");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ProbeException(
                ProbeErrorCategory.AuthenticationExpired,
                "Router Cloud rejected the authenticated request (HTTP 403).");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProbeException(
                ProbeErrorCategory.RateLimited,
                "Router Cloud rate limited the probe (HTTP 429).");
        }
        if ((int)response.StatusCode >= 500)
        {
            throw new ProbeException(
                ProbeErrorCategory.CloudUnavailable,
                $"Router Cloud returned HTTP {(int)response.StatusCode} for {path}.");
        }
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Router Cloud returned HTTP {(int)response.StatusCode} for {path}.");
        }
        var decrypted = MiWifiCrypto.DecryptResponse(body, signed.SignedNonce);
        if (response.Headers.TryGetValues("MiWiFi-Compression", out var compressionValues) &&
            string.Equals(compressionValues.FirstOrDefault(), "deflate", StringComparison.OrdinalIgnoreCase))
        {
            using var source = new MemoryStream(decrypted);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            await inflater.CopyToAsync(target, cancellationToken);
            decrypted = target.ToArray();
        }
        return JsonDocument.Parse(decrypted);
    }

    private static void EnsureSuccess(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("code", out var code))
        {
            return;
        }

        var codeText = code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : code.ToString();
        if (!int.TryParse(codeText, out var value))
        {
            throw new ProbeException(
                ProbeErrorCategory.InvalidResponse,
                $"Router Cloud returned a non-numeric code for {operation}.");
        }
        if (value != 0)
        {
            var category = value is 401 or 403 or 70016
                ? ProbeErrorCategory.AuthenticationExpired
                : ProbeErrorCategory.Unknown;
            throw new ProbeException(
                category,
                $"Router Cloud rejected {operation} (code={value}).");
        }
    }

    private static JsonElement? FindArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return int.TryParse(value, out var result) ? result : null;
    }

    private static long? GetLong(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return long.TryParse(value, out var result) ? result : null;
    }
}

internal sealed record RouterInfo(
    string Name,
    string Model,
    string Hardware,
    string Did,
    string RouterId,
    string RouterPrivateId,
    string Status);

internal sealed record RouterClient(
    string StableId,
    string? Mac,
    string? Name,
    string? Ip,
    bool Online,
    long? OnlineTime,
    int? ConnectionType,
    int? Signal,
    long? DownloadSpeed,
    long? UploadSpeed,
    long? TotalRx,
    long? TotalTx);

internal sealed record ClientList(IReadOnlyList<RouterClient> Clients, DateTimeOffset ObservedAt);

internal static class ClientPrinter
{
    public static void Print(ClientList list, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"{title}:");
        Console.WriteLine($"observed_at={list.ObservedAt:O}");
        Console.WriteLine($"count={list.Clients.Count}");
        for (var index = 0; index < list.Clients.Count; index++)
        {
            var client = list.Clients[index];
            Console.WriteLine();
            Console.WriteLine($"Client {index + 1}");
            Console.WriteLine($"id={SanitizeStableId(client.StableId)}");
            Console.WriteLine($"mac={SanitizeMac(client.Mac)}");
            Console.WriteLine($"name={client.Name ?? "(not returned)"}");
            Console.WriteLine($"ip={client.Ip ?? "(not returned)"}");
            Console.WriteLine($"online={client.Online.ToString().ToLowerInvariant()}");
            Console.WriteLine($"online_time={client.OnlineTime?.ToString() ?? "(not returned)"}");
            Console.WriteLine($"connection={ConnectionName(client.ConnectionType)}");
            Console.WriteLine($"signal={client.Signal?.ToString() ?? "(not returned)"}");
            Console.WriteLine($"d_speed={client.DownloadSpeed?.ToString() ?? "(not returned)"}");
            Console.WriteLine($"u_speed={client.UploadSpeed?.ToString() ?? "(not returned)"}");
            Console.WriteLine($"total_rx={client.TotalRx?.ToString() ?? "(not returned)"}");
            Console.WriteLine($"total_tx={client.TotalTx?.ToString() ?? "(not returned)"}");
        }
    }

    public static void PrintChanges(ClientList before, ClientList after)
    {
        var oldById = before.Clients.ToDictionary(client => client.StableId, StringComparer.OrdinalIgnoreCase);
        var newById = after.Clients.ToDictionary(client => client.StableId, StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();
        foreach (var client in after.Clients)
        {
            if (!oldById.TryGetValue(client.StableId, out var old))
            {
                changes.Add($"added id={SanitizeStableId(client.StableId)} online={client.Online.ToString().ToLowerInvariant()}");
            }
            else if (old.Online != client.Online)
            {
                changes.Add($"state id={SanitizeStableId(client.StableId)} {old.Online}->{client.Online}");
            }
        }
        foreach (var client in before.Clients.Where(client => !newById.ContainsKey(client.StableId)))
        {
            changes.Add($"removed id={SanitizeStableId(client.StableId)}");
        }
        Console.WriteLine();
        Console.WriteLine("Changes:");
        Console.WriteLine(changes.Count == 0 ? "none" : string.Join(Environment.NewLine, changes));
    }

    private static string ConnectionName(int? value) => value switch
    {
        1 => "2.4G",
        2 => "5G",
        3 => "guest",
        4 => "wired",
        5 => "zigbee",
        null => "(not returned)",
        _ => $"unknown({value})"
    };

    private static string SanitizeStableId(string value) =>
        value.Contains(':', StringComparison.Ordinal) ? SanitizeMac(value) : value;

    private static string SanitizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(not returned)";
        }
        var parts = value.Split(':');
        return parts.Length == 6
            ? $"{parts[0]}:{parts[1]}:{parts[2]}:**:**:**"
            : "(redacted)";
    }
}
