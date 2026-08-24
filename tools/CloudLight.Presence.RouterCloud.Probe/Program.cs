using System.Net;
using CloudLight.Presence.RouterCloud.Probe;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("CloudLight Presence - Xiaomi Router Cloud Phase 1B probe");
Console.WriteLine("Login reuses MiForge/migate QR and arbitrary-sid service acquisition.");
Console.WriteLine("Secrets are transferred in memory and protected at rest with Windows DPAPI.");
Console.WriteLine();

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var store = new XiaomiDpapiSessionStore();

try
{
    XiaomiStoredSession? stored = null;
    CloudHttpContext? context = null;
    RouterSnapshot? snapshot = null;

    if (store.Exists)
    {
        Console.WriteLine("Stored Xiaomi session found.");
        stored = await store.LoadAsync(cancellation.Token);
        Console.WriteLine("Session restored.");
        var diagnosticDirectV13 = args.Contains("--direct-v13", StringComparer.OrdinalIgnoreCase);
        if (args.Contains("--fetch-plugin-metadata", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--inspect-device-identity", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--current-plugin-client-list", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--current-plugin-remote-device-list", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--interactive-poll", StringComparer.OrdinalIgnoreCase) ||
            !diagnosticDirectV13)
        {
            var xiaomiIo = await MigateLoginBridge.RefreshServiceAsync(
                stored, "xiaomiio", cancellation.Token);
            if (args.Contains("--fetch-plugin-metadata", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("Fetching xiaomi.router.rd03 plugin metadata using the stored passToken...");
                using var plugin = await MiHomePluginClient.FetchAsync(
                    xiaomiIo, "xiaomi.router.rd03", cancellation.Token);
                Console.WriteLine(plugin.RootElement.GetRawText());
            }
            else if (args.Contains("--inspect-device-identity", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("Fetching the raw xiaomi.router.rd03 Mi Home device identity...");
                using var devices = await MiHomePluginClient.FetchHomeDevicesAsync(
                    xiaomiIo, "279001756619", cancellation.Token);
                PrintDeviceIdentity(devices.RootElement, "865004247", "xiaomi.router.rd03");
            }
            else
            {
                const string routerPartnerId = "2f105e53-da3d-846e-a069-2546a05907b2";
                if (args.Contains("--current-plugin-remote-device-list", StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Calling the RD03 plugin Router Remote device-list endpoint...");
                    using var remote = await MiHomePluginClient.FetchRouterRemoteDeviceListAsync(
                        xiaomiIo, routerPartnerId, cancellation.Token);
                    var root = remote.RootElement;
                    Console.WriteLine($"code={(root.TryGetProperty("code", out var remoteCode) ? remoteCode.ToString() : "(missing)")}");
                    Console.WriteLine($"root_fields={string.Join(',', root.EnumerateObject().Select(p => p.Name))}");
                    if (root.TryGetProperty("list", out var remoteList) &&
                        remoteList.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        Console.WriteLine($"list_count={remoteList.GetArrayLength()}");
                        if (remoteList.GetArrayLength() > 0)
                        {
                            Console.WriteLine($"item_fields={string.Join(',', remoteList[0].EnumerateObject().Select(p => p.Name))}");
                        }
                    }
                    return 0;
                }
                Console.WriteLine("Current Mi Home Router Gateway Test");
                Console.WriteLine("MIoT did=865004247");
                Console.WriteLine($"Resolved router remote id={routerPartnerId}");
                Console.WriteLine("Resolved from=Mi Home device partner_id");
                Console.WriteLine("Target=https://api.io.mi.com/app/appgateway/third/miwifi/app/s/api/device_list");
                Console.WriteLine("Method=POST (encrypted Mi Home envelope), forwarded method=GET");
                Console.WriteLine("Params=routerID,locale,v,refresh");
                using var response = await MiHomePluginClient.FetchRouterClientsAsync(
                    xiaomiIo,
                    routerPartnerId,
                    cancellation.Token);
                var clients = MiHomePluginClient.ParseRouterClients(response.RootElement);
                ClientPrinter.Print(clients, "Current Mi Home remote client list");
                if (args.Contains("--interactive-poll", StringComparer.OrdinalIgnoreCase))
                {
                    await RunCurrentPluginPollingAsync(
                        xiaomiIo, routerPartnerId, clients, cancellation.Token);
                }
            }
            return 0;
        }
        if (stored.Version < 3)
        {
            Console.WriteLine("Refreshing the staged session through migate service cookies...");
            var upgraded = await MigateLoginBridge.RefreshServiceAsync(stored, cancellation.Token);
            stored = FromMaterial(upgraded, stored.CreatedAt);
            await store.SaveSessionAsync(stored, cancellation.Token);
            Console.WriteLine("xiaoqiang service: refreshed");
        }
        Console.WriteLine("Validating session...");
        context = CloudHttpContext.Create(stored.ToRouterSession());
        try
        {
            snapshot = await ReadRouterSnapshotAsync(context, cancellation.Token);
            Console.WriteLine("Session validation succeeded.");
        }
        catch (Exception exception) when (
            ProbeErrorClassifier.Classify(exception).Category == ProbeErrorCategory.AuthenticationExpired)
        {
            var classified = ProbeErrorClassifier.Classify(exception);
            Console.WriteLine($"Stored xiaoqiang service is no longer valid: {classified.Message}");
            Console.WriteLine("Refreshing xiaoqiang service through migate using the stored passToken...");
            context.Dispose();
            var refreshed = await MigateLoginBridge.RefreshServiceAsync(stored, cancellation.Token);
            Console.WriteLine("passToken: restored");
            Console.WriteLine("xiaoqiang service: refreshed");
            stored = FromMaterial(refreshed, stored.CreatedAt);
            context = CloudHttpContext.Create(refreshed.ToRouterSession());
            snapshot = await ReadRouterSnapshotAsync(context, cancellation.Token);
        }
    }

    if (context is null)
    {
        Console.WriteLine("Login method: migate browser / Xiaomi official QR");
        Console.WriteLine("Complete login or QR scan in the Xiaomi official browser page.");
        var acquired = await MigateLoginBridge.LoginAsync(cancellation.Token);
        Console.WriteLine("passToken: acquired");
        Console.WriteLine("xiaoqiang service: acquired");
        context = CloudHttpContext.Create(acquired.ToRouterSession());
        stored = FromMaterial(acquired, DateTimeOffset.UtcNow);
        await store.SaveSessionAsync(stored, cancellation.Token);
        Console.WriteLine("DPAPI session staged.");
        snapshot = await ReadRouterSnapshotAsync(context, cancellation.Token);
    }

    if (snapshot is null)
    {
        throw new InvalidOperationException("Router snapshot missing.");
    }
    if (stored is null)
    {
        throw new InvalidOperationException("Xiaomi session missing.");
    }

    PrintRouter(snapshot.Router);
    ClientPrinter.Print(snapshot.Clients, "Remote client list");

    var validatedSession = stored with { LastValidatedAt = DateTimeOffset.UtcNow };
    var settings = new ProbeSettings(
        Region: "cn",
        MiotModel: snapshot.Router.Model,
        Hardware: snapshot.Router.Hardware,
        SelectedRouterPrivateId: snapshot.Router.RouterPrivateId,
        SelectedRouterSerial: snapshot.Router.RouterId,
        RememberLogin: true);
    await store.SaveAsync(validatedSession, settings, cancellation.Token);
    Console.WriteLine();
    Console.WriteLine("DPAPI session saved.");
    Console.WriteLine($"session_file={store.AuthPath}");

    var pollSeconds = ArgumentReader.GetInt(args, "--poll-seconds", 0);
    if (pollSeconds > 0)
    {
        if (pollSeconds is < 10 or > 30)
        {
            throw new InvalidOperationException("--poll-seconds must be between 10 and 30.");
        }
        Console.WriteLine();
        Console.WriteLine($"Waiting {pollSeconds} seconds before the second cloud query...");
        await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellation.Token);
        var second = await context.Cloud.GetClientListAsync(
            snapshot.Router, forceRefresh: true, cancellation.Token);
        ClientPrinter.Print(second, "Remote client list (second query)");
        ClientPrinter.PrintChanges(snapshot.Clients, second);
    }

    if (args.Contains("--interactive-poll", StringComparer.OrdinalIgnoreCase))
    {
        await RunInteractivePollingAsync(
            context.Cloud, snapshot.Router, snapshot.Clients, cancellation.Token);
    }

    context.Dispose();
    return 0;
}
catch (Exception exception)
{
    var classified = ProbeErrorClassifier.Classify(exception);
    Console.Error.WriteLine($"Probe failed [{classified.Category}]: {classified.Message}");
    return classified.Category == ProbeErrorCategory.AuthenticationExpired ? 2 : 1;
}

static XiaomiStoredSession FromMaterial(
    MigateSessionMaterial material,
    DateTimeOffset createdAt) =>
    new(
        Version: 3,
        Region: "cn",
        UserId: material.UserId,
        AccountUserId: material.AccountUserId,
        CUserId: material.CUserId,
        DeviceId: material.DeviceId,
        PassToken: material.PassToken,
        ServiceToken: material.ServiceToken,
        Ssecurity: material.Ssecurity,
        CreatedAt: createdAt,
        LastValidatedAt: DateTimeOffset.UtcNow);

static async Task<RouterSnapshot> ReadRouterSnapshotAsync(
    CloudHttpContext context,
    CancellationToken cancellationToken)
{
    const string miotDid = "865004247";
    const string routerPartnerId = "2f105e53-da3d-846e-a069-2546a05907b2";
    const string target = "/api/xqsystem/device_list";
    try
    {
        _ = await context.Cloud.FindRouterAsync("xiaomi.router.rd03", cancellationToken);
    }
    catch (Exception exception) when (
        ProbeErrorClassifier.Classify(exception).Category != ProbeErrorCategory.AuthenticationExpired)
    {
        Console.WriteLine($"xiaoqiang /s/admin/deviceList authenticated: {exception.Message}");
    }
    Console.WriteLine("Router Remote Test");
    Console.WriteLine($"MIoT did={miotDid}");
    Console.WriteLine($"Resolved router remote id={routerPartnerId}");
    Console.WriteLine("Resolved from=Mi Home device partner_id");
    Console.WriteLine($"Target=https://api.miwifi.com/r{target}");
    Console.WriteLine("Method=GET");
    Console.WriteLine("Identifier fields=deviceId,deviceID,routerID");
    using var response = await context.Cloud.CallRouterRemoteAsync(
        routerPartnerId,
        "GET",
        target,
        parameters: null,
        cancellationToken);
    Console.WriteLine("Router Remote response decrypted.");
    Console.WriteLine($"Response={RedactJson(response.RootElement)}");
    throw new InvalidOperationException(
        "Router Remote transport probe completed; client response parsing is not implemented yet.");
}

static string RedactJson(System.Text.Json.JsonElement element)
{
    if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        var fields = element.EnumerateObject().Select(property => property.Name);
        var code = element.TryGetProperty("code", out var codeValue) ? codeValue.ToString() : "(missing)";
        return $"code={code}; fields={string.Join(',', fields)}";
    }
    return $"kind={element.ValueKind}";
}

static void PrintDeviceIdentity(
    System.Text.Json.JsonElement element,
    string did,
    string model)
{
    if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        var isMatch =
            (element.TryGetProperty("did", out var didValue) && didValue.ToString() == did) ||
            (element.TryGetProperty("model", out var modelValue) && modelValue.ToString() == model);
        if (isMatch)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "did", "model", "name", "deviceID", "deviceId", "partnerId",
                "partner_id", "pid", "localip", "ssid", "bssid"
            };
            Console.WriteLine("Matching device fields:");
            foreach (var property in element.EnumerateObject())
            {
                if (allowed.Contains(property.Name))
                {
                    Console.WriteLine($"{property.Name}={property.Value}");
                }
            }
            Console.WriteLine($"all_field_names={string.Join(',', element.EnumerateObject().Select(p => p.Name))}");
            return;
        }
        foreach (var property in element.EnumerateObject())
        {
            PrintDeviceIdentity(property.Value, did, model);
        }
    }
    else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            PrintDeviceIdentity(item, did, model);
        }
    }
}

static void PrintRouter(RouterInfo router)
{
    Console.WriteLine();
    Console.WriteLine("Router:");
    Console.WriteLine($"name={router.Name}");
    Console.WriteLine($"miot_model={router.Model}");
    Console.WriteLine($"hardware={router.Hardware}");
    Console.WriteLine($"miot_did={router.Did}");
    Console.WriteLine($"router_serial={router.RouterId}");
    Console.WriteLine($"router_private_id={router.RouterPrivateId}");
    Console.WriteLine($"router_status={router.Status}");
}

static async Task RunInteractivePollingAsync(
    MiWifiCloudClient cloud,
    RouterInfo router,
    ClientList baseline,
    CancellationToken cancellationToken)
{
    const int intervalSeconds = 15;
    const int maximumPollsPerPhase = 12;
    Console.WriteLine();
    Console.WriteLine("Interactive polling: 15-second interval, up to 3 minutes per transition.");
    Console.WriteLine($"Baseline contains {baseline.Clients.Count} entries; " +
        $"online={baseline.Clients.Count(client => client.Online)}, " +
        $"offline={baseline.Clients.Count(client => !client.Online)}.");
    Console.WriteLine("请让一台当前不在线的设备连接 AX3000T Wi-Fi，然后立即按 Enter。");
    Console.ReadLine();
    var connectStarted = DateTimeOffset.UtcNow;
    var previous = baseline;
    RouterClient? target = null;

    for (var poll = 1; poll <= maximumPollsPerPhase; poll++)
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        var current = await cloud.GetClientListAsync(router, forceRefresh: true, cancellationToken);
        Console.WriteLine($"Snapshot connect-{poll} {current.ObservedAt:O}");
        ClientPrinter.PrintChanges(previous, current);
        target = FindBecameOnline(baseline, current);
        if (target is not null)
        {
            Console.WriteLine($"online_detected_after_seconds={(current.ObservedAt - connectStarted).TotalSeconds:F1}");
            ClientPrinter.Print(new ClientList([target], current.ObservedAt), "Detected client");
            previous = current;
            break;
        }
        previous = current;
    }

    if (target is null)
    {
        Console.WriteLine("No new online client was detected within 3 minutes.");
        return;
    }

    Console.WriteLine("请关闭该设备 Wi-Fi，然后立即按 Enter。");
    Console.ReadLine();
    var disconnectStarted = DateTimeOffset.UtcNow;
    for (var poll = 1; poll <= maximumPollsPerPhase; poll++)
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        var current = await cloud.GetClientListAsync(router, forceRefresh: true, cancellationToken);
        Console.WriteLine($"Snapshot disconnect-{poll} {current.ObservedAt:O}");
        ClientPrinter.PrintChanges(previous, current);
        var currentTarget = current.Clients.FirstOrDefault(client =>
            string.Equals(client.StableId, target.StableId, StringComparison.OrdinalIgnoreCase));
        if (currentTarget is null || !currentTarget.Online)
        {
            var disposition = currentTarget is null ? "removed" : "online=false";
            Console.WriteLine($"offline_detected_after_seconds={(current.ObservedAt - disconnectStarted).TotalSeconds:F1}");
            Console.WriteLine($"offline_disposition={disposition}");
            return;
        }
        previous = current;
    }
    Console.WriteLine("The client did not become offline or disappear within 3 minutes.");
}

static RouterClient? FindBecameOnline(ClientList baseline, ClientList current)
{
    var before = baseline.Clients.ToDictionary(
        client => client.StableId, StringComparer.OrdinalIgnoreCase);
    return current.Clients.FirstOrDefault(client =>
        client.Online &&
        (!before.TryGetValue(client.StableId, out var old) || !old.Online));
}

static async Task RunCurrentPluginPollingAsync(
    MigateSessionMaterial service,
    string routerPartnerId,
    ClientList baseline,
    CancellationToken cancellationToken)
{
    const int intervalSeconds = 15;
    const int maximumPollsPerPhase = 12;
    Console.WriteLine("请让一台当前不在线的设备连接 AX3000T Wi-Fi，然后立即按 Enter。");
    Console.ReadLine();
    var started = DateTimeOffset.Now;
    var previous = baseline;
    RouterClient? target = null;
    for (var poll = 1; poll <= maximumPollsPerPhase; poll++)
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        using var response = await MiHomePluginClient.FetchRouterClientsAsync(
            service, routerPartnerId, cancellationToken);
        var current = MiHomePluginClient.ParseRouterClients(response.RootElement);
        Console.WriteLine($"Snapshot connect-{poll} {current.ObservedAt:O}");
        ClientPrinter.PrintChanges(previous, current);
        target = FindBecameOnline(baseline, current);
        if (target is not null)
        {
            Console.WriteLine($"online_detected_after_seconds={(current.ObservedAt - started).TotalSeconds:F1}");
            ClientPrinter.Print(new ClientList([target], current.ObservedAt), "Detected client");
            previous = current;
            break;
        }
        previous = current;
    }
    if (target is null)
    {
        Console.WriteLine("No new online client was detected within 3 minutes.");
        return;
    }
    Console.WriteLine("请关闭该设备 Wi-Fi，然后立即按 Enter。");
    Console.ReadLine();
    started = DateTimeOffset.Now;
    for (var poll = 1; poll <= maximumPollsPerPhase; poll++)
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        using var response = await MiHomePluginClient.FetchRouterClientsAsync(
            service, routerPartnerId, cancellationToken);
        var current = MiHomePluginClient.ParseRouterClients(response.RootElement);
        Console.WriteLine($"Snapshot disconnect-{poll} {current.ObservedAt:O}");
        ClientPrinter.PrintChanges(previous, current);
        var observed = current.Clients.FirstOrDefault(client =>
            string.Equals(client.StableId, target.StableId, StringComparison.OrdinalIgnoreCase));
        if (observed is null || !observed.Online)
        {
            Console.WriteLine($"offline_detected_after_seconds={(current.ObservedAt - started).TotalSeconds:F1}");
            Console.WriteLine($"offline_disposition={(observed is null ? "removed" : "online=false")}");
            return;
        }
        previous = current;
    }
    Console.WriteLine("The client did not become offline or disappear within 3 minutes.");
}

file sealed record RouterSnapshot(RouterInfo Router, ClientList Clients);

file sealed class CloudHttpContext(HttpClientHandler handler, HttpClient http) : IDisposable
{
    private XiaomiRouterSession? _session;
    private MiWifiCloudClient? _cloud;

    public HttpClient Http { get; } = http;
    public CookieContainer Cookies => handler.CookieContainer;
    public MiWifiCloudClient Cloud => _cloud
        ?? throw new InvalidOperationException("Router Cloud session is not configured.");

    public static CloudHttpContext Create(XiaomiRouterSession session)
    {
        var newHandler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All
        };
        var newHttp = new HttpClient(newHandler) { Timeout = TimeSpan.FromSeconds(30) };
        newHttp.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 APP/com.xiaomi.router APPV/5.4.7");
        var context = new CloudHttpContext(newHandler, newHttp);
        context.SetSession(session);
        return context;
    }

    public void SetSession(XiaomiRouterSession session)
    {
        _session = session;
        var apiUri = new Uri("https://api.miwifi.com/");
        Cookies.Add(apiUri, new Cookie("userId", session.UserId, "/", apiUri.Host));
        if (!string.IsNullOrWhiteSpace(session.CUserId))
        {
            Cookies.Add(apiUri, new Cookie("cUserId", session.CUserId, "/", apiUri.Host));
        }
        Cookies.Add(apiUri, new Cookie("passToken", session.PassToken, "/", apiUri.Host));
        Cookies.Add(apiUri, new Cookie("serviceToken", session.ServiceToken, "/", apiUri.Host));
        Cookies.Add(apiUri, new Cookie("yetAnotherServiceToken", session.ServiceToken, "/", apiUri.Host));
        _cloud = new MiWifiCloudClient(Http, session);
    }

    public void Dispose()
    {
        Http.Dispose();
        handler.Dispose();
        _session = null;
        _cloud = null;
    }
}

file static class ArgumentReader
{
    public static int GetInt(string[] args, string name, int fallback)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], out var value))
            {
                return value;
            }
        }
        return fallback;
    }
}
