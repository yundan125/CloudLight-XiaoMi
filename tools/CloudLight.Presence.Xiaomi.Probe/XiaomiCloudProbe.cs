using System.Net.Http.Json;
using System.Text.Json;

namespace CloudLight.Presence.Xiaomi.Probe;

internal sealed class XiaomiCloudProbe
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _accessToken;

    public XiaomiCloudProbe(HttpClient http, string region, string accessToken)
    {
        _http = http;
        _baseUri = new Uri($"https://{XiaomiEndpoints.ApiHost(region)}");
        _accessToken = accessToken;
    }

    public async Task<CloudProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        using var homesDocument = await PostAsync(
            "/app/v2/homeroom/gethome",
            new { limit = 150, fetch_share = true, fetch_share_dev = true, plat_form = 0, app_ver = 9 },
            cancellationToken);
        var homeResult = RequireResult(homesDocument.RootElement, "home list");
        var locations = ReadLocations(homeResult);

        Console.WriteLine();
        Console.WriteLine($"Homes returned: {locations.HomeNames.Count}");
        foreach (var home in locations.HomeNames)
        {
            Console.WriteLine($"  home_id={home.Key} name={home.Value}");
        }

        var devices = await GetDevicesAsync(locations.DeviceLocations, cancellationToken);
        Console.WriteLine($"Devices returned: {devices.Count}");

        var routers = new List<JsonElement>();
        foreach (var device in devices)
        {
            var did = Text(device, "did");
            var model = Text(device, "model");
            var spec = Text(device, "spec_type");
            var location = did is not null && locations.DeviceLocations.TryGetValue(did, out var found)
                ? found
                : default;
            Console.WriteLine(
                $"  name={Text(device, "name") ?? "<none>"} model={model ?? "<none>"} " +
                $"did={did ?? "<none>"} home_id={location.HomeId ?? "<none>"} " +
                $"room_id={location.RoomId ?? "<none>"} spec={spec ?? "<none>"} online={Bool(device, "isOnline")}");

            if (model?.Contains(".router.", StringComparison.OrdinalIgnoreCase) == true ||
                spec?.Contains(":device:router:", StringComparison.OrdinalIgnoreCase) == true)
            {
                routers.Add(device.Clone());
            }
        }

        if (routers.Count == 0)
        {
            throw new InvalidOperationException("No MIoT router was returned by Xiaomi Cloud.");
        }

        foreach (var routerElement in routers)
        {
            Console.WriteLine(
                $"Router candidate: name={Text(routerElement, "name") ?? "<none>"} " +
                $"model={Text(routerElement, "model") ?? "<none>"} did={Text(routerElement, "did") ?? "<none>"} " +
                $"spec={Text(routerElement, "spec_type") ?? "<none>"}");
        }

        var selected = routers.FirstOrDefault(item =>
            Text(item, "name")?.Contains("AX3000T", StringComparison.OrdinalIgnoreCase) == true);
        if (selected.ValueKind == JsonValueKind.Undefined && routers.Count == 1)
        {
            selected = routers[0];
        }

        if (selected.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                "Multiple routers were returned and none was named AX3000T; the probe will not guess which router to test.");
        }

        var selectedDid = Text(selected, "did") ?? throw new InvalidOperationException("Router DID missing.");
        var selectedSpec = Text(selected, "spec_type") ?? throw new InvalidOperationException("Router spec_type missing.");
        var selectedLocation = locations.DeviceLocations.GetValueOrDefault(selectedDid);
        var router = new RouterProbeTarget(
            Text(selected, "name"),
            Text(selected, "model") ?? throw new InvalidOperationException("Router model missing."),
            selectedDid,
            selectedLocation.HomeId,
            selectedLocation.RoomId,
            selectedSpec);
        Console.WriteLine();
        Console.WriteLine(
            $"Selected AX3000T: name={router.Name ?? "<none>"} model={router.Model} did={router.Did} " +
            $"home_id={router.HomeId ?? "<none>"} room_id={router.RoomId ?? "<none>"} spec={router.SpecType}");

        var routerSpec = await GetRouterSpecAsync(router.SpecType, cancellationToken);
        await ProbeRouterPropertiesAsync(router, routerSpec, cancellationToken);
        return new CloudProbeResult(router, routerSpec);
    }

    private async Task<List<JsonElement>> GetDevicesAsync(
        IReadOnlyDictionary<string, DeviceLocation> locations,
        CancellationToken cancellationToken)
    {
        var dids = locations.Keys.Order(StringComparer.Ordinal).ToArray();
        var devices = new List<JsonElement>();
        var chunks = dids.Length == 0 ? [Array.Empty<string>()] : dids.Chunk(150).ToArray();

        foreach (var chunk in chunks)
        {
            string? startDid = null;
            do
            {
                var payload = startDid is null
                    ? new Dictionary<string, object?>
                    {
                        ["limit"] = 200,
                        ["get_split_device"] = true,
                        ["get_third_device"] = true,
                        ["dids"] = chunk
                    }
                    : new Dictionary<string, object?>
                    {
                        ["limit"] = 200,
                        ["get_split_device"] = true,
                        ["get_third_device"] = true,
                        ["dids"] = chunk,
                        ["start_did"] = startDid
                    };
                using var document = await PostAsync(
                    "/app/v2/home/device_list_page", payload, cancellationToken);
                var result = RequireResult(document.RootElement, "device list");
                if (result.TryGetProperty("list", out var list))
                {
                    devices.AddRange(list.EnumerateArray().Select(item => item.Clone()));
                }

                startDid = result.TryGetProperty("has_more", out var hasMore) && hasMore.GetBoolean() &&
                    result.TryGetProperty("next_start_did", out var next)
                    ? next.GetString()
                    : null;
            }
            while (!string.IsNullOrWhiteSpace(startDid));
        }

        return devices;
    }

    private async Task ProbeRouterPropertiesAsync(
        RouterProbeTarget router,
        RouterSpecIds spec,
        CancellationToken cancellationToken)
    {
        var connectDeviceIdsPiid = spec.ConnectDeviceIdsPropertyIid ?? 20;
        Console.WriteLine();
        Console.WriteLine($"Router property probe: name={router.Name} model={router.Model} did={router.Did}");
        if (spec.ConnectDeviceIdsPropertyIid is null)
        {
            Console.WriteLine(
                "Actual spec does not declare connect-device-ids; probing expected siid=2/piid=20 to capture the real cloud response.");
        }
        using var document = await PostAsync(
            "/app/v2/miotspec/prop/get",
            new
            {
                datasource = 1,
                @params = new[]
                {
                    new
                    {
                        did = router.Did,
                        siid = spec.RouterServiceIid,
                        piid = spec.ConnectedDeviceNumberPropertyIid
                    },
                    new
                    {
                        did = router.Did,
                        siid = spec.RouterServiceIid,
                        piid = connectDeviceIdsPiid
                    }
                }
            },
            cancellationToken);
        var result = RequireResult(document.RootElement, "router properties");
        foreach (var property in result.EnumerateArray())
        {
            var value = property.TryGetProperty("value", out var propertyValue)
                ? propertyValue.GetRawText()
                : "<not returned>";
            Console.WriteLine(
                $"  siid={property.GetProperty("siid")} piid={property.GetProperty("piid")} " +
                $"code={property.GetProperty("code")} dataType={ValueType(property)} value={value}");
            Console.WriteLine($"  rawResponse={property.GetRawText()}");

            if (property.TryGetProperty("piid", out var piid) &&
                piid.GetInt32() == connectDeviceIdsPiid &&
                property.TryGetProperty("value", out var clients))
            {
                DescribeClientValue(clients);
            }
        }
    }

    private async Task<RouterSpecIds> GetRouterSpecAsync(string specType, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder("https://miot-spec.org/miot-spec-v2/instance")
        {
            Query = $"type={Uri.EscapeDataString(specType)}"
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MIoT spec endpoint returned HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var service = document.RootElement.GetProperty("services").EnumerateArray().FirstOrDefault(item =>
            Text(item, "type")?.Contains(":service:router:", StringComparison.Ordinal) == true);
        if (service.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Actual router spec {specType} has no router service.");
        }

        var properties = service.TryGetProperty("properties", out var propertyList)
            ? propertyList.EnumerateArray().ToArray()
            : [];
        var events = service.TryGetProperty("events", out var eventList)
            ? eventList.EnumerateArray().ToArray()
            : [];
        var connectedNumber = FindIid(properties, ":property:connected-device-number:");
        var connectIds = FindIid(properties, ":property:connect-device-ids:");
        var connectEvents = FindIids(events, ":event:device-connect:");
        var disconnectEvents = FindIids(events, ":event:device-disconnect:");
        if (connectedNumber is null)
        {
            throw new InvalidOperationException(
                $"Actual router spec {specType} does not declare connected-device-number.");
        }

        var result = new RouterSpecIds(
            service.GetProperty("iid").GetInt32(),
            connectedNumber.Value,
            connectIds,
            connectEvents,
            disconnectEvents);
        Console.WriteLine(
            $"Actual spec IDs: router siid={result.RouterServiceIid}, " +
            $"connected-device-number piid={result.ConnectedDeviceNumberPropertyIid}, " +
            $"connect-device-ids piid={(result.ConnectDeviceIdsPropertyIid?.ToString() ?? "not-declared")}, " +
            $"device-connect eiid=[{string.Join(',', result.DeviceConnectEventIids)}], " +
            $"device-disconnect eiid=[{string.Join(',', result.DeviceDisconnectEventIids)}]");
        return result;
    }

    private async Task<JsonDocument> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path))
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("X-Client-BizId", "haapi");
        request.Headers.TryAddWithoutValidation("X-Client-AppId", XiaomiEndpoints.OAuthClientId);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer{_accessToken}");
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Xiaomi API {path} returned HTTP {(int)response.StatusCode}.");
        }

        var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
        {
            var message = document.RootElement.TryGetProperty("message", out var value)
                ? value.GetString()
                : "unknown Xiaomi API error";
            document.Dispose();
            throw new InvalidOperationException($"Xiaomi API {path} failed: {message}");
        }

        return document;
    }

    private static JsonElement RequireResult(JsonElement root, string operation) =>
        root.TryGetProperty("result", out var result)
            ? result
            : throw new InvalidOperationException($"Xiaomi {operation} response did not contain result.");

    private static Locations ReadLocations(JsonElement result)
    {
        var homeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var devices = new Dictionary<string, DeviceLocation>(StringComparer.Ordinal);
        foreach (var sourceName in new[] { "homelist", "share_home_list" })
        {
            if (!result.TryGetProperty(sourceName, out var homes))
            {
                continue;
            }

            foreach (var home in homes.EnumerateArray())
            {
                var homeId = home.GetProperty("id").ToString();
                homeNames[homeId] = Text(home, "name") ?? string.Empty;
                if (home.TryGetProperty("dids", out var homeDids))
                {
                    foreach (var did in homeDids.EnumerateArray())
                    {
                        devices[did.GetString() ?? string.Empty] = new DeviceLocation(homeId, homeId);
                    }
                }

                if (!home.TryGetProperty("roomlist", out var rooms))
                {
                    continue;
                }

                foreach (var room in rooms.EnumerateArray())
                {
                    var roomId = room.GetProperty("id").ToString();
                    if (!room.TryGetProperty("dids", out var roomDids))
                    {
                        continue;
                    }

                    foreach (var did in roomDids.EnumerateArray())
                    {
                        devices[did.GetString() ?? string.Empty] = new DeviceLocation(homeId, roomId);
                    }
                }
            }
        }

        devices.Remove(string.Empty);
        return new Locations(homeNames, devices);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static string Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean().ToString()
            : "<none>";

    private static int? FindIid(IEnumerable<JsonElement> items, string typeFragment)
    {
        var item = items.FirstOrDefault(value =>
            Text(value, "type")?.Contains(typeFragment, StringComparison.Ordinal) == true);
        return item.ValueKind == JsonValueKind.Undefined ? null : item.GetProperty("iid").GetInt32();
    }

    private static IReadOnlyList<int> FindIids(IEnumerable<JsonElement> items, string typeFragment) =>
        items.Where(value => Text(value, "type")?.Contains(typeFragment, StringComparison.Ordinal) == true)
            .Select(value => value.GetProperty("iid").GetInt32())
            .Distinct()
            .Order()
            .ToArray();

    private static string ValueType(JsonElement property)
    {
        if (!property.TryGetProperty("value", out var value))
        {
            return "not-returned";
        }

        return value.ValueKind == JsonValueKind.String ? "string" : value.ValueKind.ToString();
    }

    private static void DescribeClientValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            Console.WriteLine($"  clientPayloadFormat={value.ValueKind}");
            return;
        }

        var embedded = value.GetString();
        if (string.IsNullOrWhiteSpace(embedded))
        {
            Console.WriteLine("  clientPayloadFormat=empty string");
            return;
        }

        try
        {
            using var parsed = JsonDocument.Parse(embedded);
            var fields = CollectFieldNames(parsed.RootElement).Order(StringComparer.Ordinal).ToArray();
            Console.WriteLine($"  clientPayloadFormat=JSON {parsed.RootElement.ValueKind}");
            Console.WriteLine($"  clientFields=[{string.Join(", ", fields)}]");
        }
        catch (JsonException)
        {
            Console.WriteLine("  clientPayloadFormat=non-JSON string");
        }
    }

    private static HashSet<string> CollectFieldNames(JsonElement value)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        Collect(value, fields);
        return fields;

        static void Collect(JsonElement item, HashSet<string> destination)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.EnumerateObject())
                {
                    destination.Add(property.Name);
                    Collect(property.Value, destination);
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.EnumerateArray())
                {
                    Collect(child, destination);
                }
            }
        }
    }
}

internal sealed record Locations(
    IReadOnlyDictionary<string, string> HomeNames,
    IReadOnlyDictionary<string, DeviceLocation> DeviceLocations);

internal readonly record struct DeviceLocation(string? HomeId, string? RoomId);

internal sealed record RouterProbeTarget(
    string? Name,
    string Model,
    string Did,
    string? HomeId,
    string? RoomId,
    string SpecType);

internal sealed record RouterSpecIds(
    int RouterServiceIid,
    int ConnectedDeviceNumberPropertyIid,
    int? ConnectDeviceIdsPropertyIid,
    IReadOnlyList<int> DeviceConnectEventIids,
    IReadOnlyList<int> DeviceDisconnectEventIids);

internal sealed record CloudProbeResult(RouterProbeTarget Router, RouterSpecIds RouterSpec);
