using System.Text.Json;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Xiaomi.Authentication;
using CloudLight.Presence.Xiaomi.Cloud;

namespace CloudLight.Presence.Xiaomi;

public sealed class XiaomiPresenceSource(ISecureSessionStore store, string migatePythonPath) : IXiaomiPresenceSource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly MigateLoginBridge _bridge = new(migatePythonPath);
    private readonly XiaomiAppGatewayClient _gateway = new();
    private XiaomiSession? _session;
    public bool HasStoredLogin => store.Exists;

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var acquired = await _bridge.LoginAsync(cancellationToken); _session = acquired.ToSession();
        await store.SaveAsync(JsonSerializer.Serialize(_session, Options), cancellationToken);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!store.Exists) throw new AuthenticationRequiredException("没有已保存的 Xiaomi 登录。");
        XiaomiSession stored;
        try { stored = JsonSerializer.Deserialize<XiaomiSession>(await store.LoadAsync(cancellationToken), Options) ?? throw new JsonException("empty"); }
        catch (Exception exception) when (exception is JsonException or System.Security.Cryptography.CryptographicException)
        { throw new AuthenticationRequiredException("保存的 Xiaomi 登录无法恢复。", exception); }
        var refreshed = await _bridge.RefreshAsync(stored, cancellationToken);
        _session = refreshed.ToSession(stored.CreatedAt);
        await store.SaveAsync(JsonSerializer.Serialize(_session, Options), cancellationToken);
    }

    public async Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken) =>
        await WithRefreshAsync(async session =>
        {
            var homes = await _gateway.GetHomesAsync(session, cancellationToken); var routers = new Dictionary<string, XiaomiRouterDevice>(StringComparer.Ordinal);
            foreach (var home in homes)
            {
                using var devices = await _gateway.GetHomeDevicesAsync(session, home.Id, cancellationToken);
                foreach (var item in XiaomiAppGatewayClient.FindRouterObjects(devices.RootElement))
                {
                    var did = XiaomiAppGatewayClient.Text(item, "did");
                    var model = XiaomiAppGatewayClient.Text(item, "model");
                    var partner = XiaomiAppGatewayClient.Text(item, "partner_id", "partnerId", "partnerID");
                    if (did is null || model is null || partner is null) continue;
                    routers[did] = new XiaomiRouterDevice(did, model, partner, XiaomiAppGatewayClient.Text(item, "name") ?? model, home.Id, home.Rooms.GetValueOrDefault(did));
                }
            }
            return (IReadOnlyList<XiaomiRouterDevice>)routers.Values.ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken) =>
        WithRefreshAsync(session => _gateway.GetRouterClientsAsync(session, partnerId, cancellationToken), cancellationToken);

    private async Task<T> WithRefreshAsync<T>(Func<XiaomiSession, Task<T>> action, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new AuthenticationRequiredException("Xiaomi 会话尚未初始化。");
        try { return await action(session); }
        catch (XiaomiCloudException exception) when (exception.AuthenticationExpired)
        {
            var refreshed = await _bridge.RefreshAsync(session, cancellationToken); _session = refreshed.ToSession(session.CreatedAt);
            await store.SaveAsync(JsonSerializer.Serialize(_session, Options), cancellationToken);
            try { return await action(_session); }
            catch (XiaomiCloudException retryException) when (retryException.AuthenticationExpired)
            { throw new AuthenticationRequiredException("Xiaomi 登录凭据已明确失效，需要重新登录。", retryException); }
        }
    }
}
