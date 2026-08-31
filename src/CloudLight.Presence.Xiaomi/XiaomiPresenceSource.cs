using System.Text.Json;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Xiaomi.Authentication;
using CloudLight.Presence.Xiaomi.Cloud;

namespace CloudLight.Presence.Xiaomi;

public sealed class XiaomiPresenceSource : IXiaomiPresenceSource, IXiaomiPresenceDiagnosticsSource, IXiaomiAccountDeviceSource, IXiaomiDeviceControlSource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ISecureSessionStore _store;
    private readonly MigateLoginBridge _bridge;
    private readonly XiaomiAppGatewayClient _gateway = new();
    private readonly XiaomiDeviceCapabilityResolver _capabilityResolver = new();
    private XiaomiSession? _session;

    public XiaomiPresenceSource(ISecureSessionStore store, string migatePythonPath, string? logsDirectory = null)
    {
        _store = store;
        _bridge = new MigateLoginBridge(migatePythonPath, logsDirectory);
    }

    public bool HasStoredLogin => _store.Exists;

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var acquired = await _bridge.LoginAsync(cancellationToken);
        _session = acquired.ToSession();
        await _store.SaveAsync(JsonSerializer.Serialize(_session, Options), cancellationToken);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!_store.Exists) throw new AuthenticationRequiredException("没有已保存的 Xiaomi 登录。");
        XiaomiSession stored;
        try
        {
            stored = JsonSerializer.Deserialize<XiaomiSession>(
                         await _store.LoadAsync(cancellationToken), Options) ??
                     throw new JsonException("empty");
        }
        catch (Exception exception) when (exception is JsonException or System.Security.Cryptography.CryptographicException)
        {
            throw new AuthenticationRequiredException("保存的 Xiaomi 登录无法恢复。", exception);
        }

        var refreshed = await _bridge.RefreshAsync(stored, cancellationToken);
        _session = refreshed.ToSession(stored.CreatedAt);
        await _store.SaveAsync(JsonSerializer.Serialize(_session, Options), cancellationToken);
    }

    public Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesAsync(
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => DiscoverAccountDevicesWithSessionAsync(session, cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(
        CancellationToken cancellationToken)
    {
        var devices = await WithRefreshAsync(
            session => DiscoverAccountDevicesWithSessionAsync(session, cancellationToken),
            cancellationToken);
        return devices
            .Where(value => value.IsRouter)
            .Select(value => new XiaomiRouterDevice(
                value.Did,
                value.Model ?? "unknown",
                value.PartnerId ?? string.Empty,
                value.DisplayName,
                value.HomeId,
                value.RoomId))
            .GroupBy(value => value.MiotDid, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(
        string partnerId,
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => _gateway.GetRouterClientsAsync(session, partnerId, cancellationToken),
            cancellationToken);

    public async Task<RouterPresenceProbeResult> GetDevicesWithDiagnosticsAsync(
        XiaomiRouterDevice router,
        CancellationToken cancellationToken)
    {
        var endpoint = XiaomiApiEndpoints.AppGatewayBaseUrl + XiaomiApiEndpoints.RouterClientListPath;
        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(router.PartnerId))
        {
            return new([], new RouterCapabilityDiagnostic(
                0, router.MiotDid, router.MiotModel, false, endpoint, null, false, [], null, false,
                "未返回 Router ID / partner_id。", now));
        }

        try
        {
            var probe = await WithRefreshAsync(
                session => _gateway.GetRouterClientsWithDiagnosticsAsync(session, router.PartnerId, cancellationToken),
                cancellationToken);
            return new(probe.Devices, new RouterCapabilityDiagnostic(
                0, router.MiotDid, router.MiotModel, true, endpoint, probe.ApiCode,
                probe.ClientListAvailable, probe.SuccessfulFields, probe.ClientListAvailable ? now : null,
                probe.ClientListAvailable, probe.Error, now));
        }
        catch (RouterPresenceProbeException)
        {
            throw;
        }
        catch (XiaomiCloudException exception)
        {
            throw new RouterPresenceProbeException(
                exception.Message,
                new RouterCapabilityDiagnostic(0, router.MiotDid, router.MiotModel, true, endpoint,
                    exception.XiaomiCode, false, [], null, false, exception.Message, now), exception);
        }
        catch (Exception exception)
        {
            throw new RouterPresenceProbeException(
                exception.Message,
                new RouterCapabilityDiagnostic(0, router.MiotDid, router.MiotModel, true, endpoint,
                    null, false, [], null, false, exception.Message, now), exception);
        }
    }

    public Task<XiaomiPowerStateResult> ReadPowerStateAsync(
        XiaomiAccountDevice device,
        XiaomiPowerCapability capability,
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => _gateway.GetPowerPropertyAsync(session, capability, device.Did, cancellationToken),
            cancellationToken);

    public Task<XiaomiPowerStateResult> SetPowerStateAsync(
        XiaomiAccountDevice device,
        XiaomiPowerCapability capability,
        bool value,
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => _gateway.SetPowerPropertyAsync(session, capability, device.Did, value, cancellationToken),
            cancellationToken);

    public async Task<XiaomiDeviceDefinition?> GetDeviceDefinitionAsync(
        XiaomiAccountDevice device,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.SpecType)) return device.Definition;
        return await _capabilityResolver.ResolveDefinitionAsync(device, cancellationToken);
    }

    public Task<IReadOnlyList<XiaomiPropertyReadResult>> GetPropertiesAsync(
        XiaomiAccountDevice device,
        IReadOnlyList<XiaomiPropertyDefinition> properties,
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => _gateway.GetPropertiesAsync(session, device.Did, properties, cancellationToken),
            cancellationToken);

    public Task<XiaomiPropertyOperationResult> SetPropertyAsync(
        XiaomiAccountDevice device,
        XiaomiPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => _gateway.SetPropertyAsync(session, device.Did, property, value, cancellationToken),
            cancellationToken);

    public Task<XiaomiActionInvocationResult> InvokeActionAsync(
        XiaomiAccountDevice device,
        XiaomiActionDefinition action,
        IReadOnlyList<object?> inputArguments,
        CancellationToken cancellationToken) =>
        WithRefreshAsync(
            session => _gateway.InvokeActionAsync(session, device.Did, action, inputArguments, cancellationToken),
            cancellationToken);

    private async Task<IReadOnlyList<XiaomiAccountDevice>> DiscoverAccountDevicesWithSessionAsync(
        XiaomiSession session,
        CancellationToken cancellationToken)
    {
        var rawDevices = await _gateway.GetAccountDevicesAsync(session, cancellationToken);
        var devices = rawDevices
            .Select(value => XiaomiAccountDeviceMapper.Map(
                value.Data,
                value.Home?.Id,
                value.Home?.Name,
                value.Room?.Id,
                value.Room?.Name,
                value.IsShared))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

        foreach (var group in devices
                     .Where(value => !string.IsNullOrWhiteSpace(value.SpecType))
                     .GroupBy(value => value.SpecType!, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var definition = await _capabilityResolver.ResolveDefinitionAsync(group.First(), cancellationToken);
                var capabilities = await _capabilityResolver.ResolveCapabilitiesAsync(
                    group.First() with { Definition = definition }, cancellationToken);
                foreach (var value in group)
                {
                    var merged = MergeCapabilities(value.Capabilities, capabilities);
                    var type = XiaomiDeviceCapabilityResolver.ClassifyDeviceType(value.Model, value.SpecType, merged);
                    var index = devices.IndexOf(value);
                    if (index >= 0) devices[index] = value with
                    {
                        Capabilities = merged,
                        DeviceType = type,
                        Definition = definition
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A missing or temporarily unavailable spec must not hide the account device.
            }
        }

        return devices;
    }

    private static XiaomiDeviceCapabilities MergeCapabilities(
        XiaomiDeviceCapabilities metadata,
        XiaomiDeviceCapabilities resolved) =>
        resolved with
        {
            IsRouter = metadata.IsRouter || resolved.IsRouter,
            IsSwitch = metadata.IsSwitch || resolved.IsSwitch,
            IsLight = metadata.IsLight || resolved.IsLight,
            IsSensor = metadata.IsSensor || resolved.IsSensor,
            IsAirConditioner = metadata.IsAirConditioner || resolved.IsAirConditioner,
            IsCamera = metadata.IsCamera || resolved.IsCamera,
            IsVacuum = metadata.IsVacuum || resolved.IsVacuum,
            IsLock = metadata.IsLock || resolved.IsLock,
            IsPlug = metadata.IsPlug || resolved.IsPlug
        };

    private async Task<T> WithRefreshAsync<T>(
        Func<XiaomiSession, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var session = _session ?? throw new AuthenticationRequiredException("Xiaomi 会话尚未初始化。");
        try
        {
            return await action(session);
        }
        catch (XiaomiCloudException exception) when (exception.AuthenticationExpired)
        {
            var refreshed = await _bridge.RefreshAsync(session, cancellationToken);
            _session = refreshed.ToSession(session.CreatedAt);
            await _store.SaveAsync(JsonSerializer.Serialize(_session, Options), cancellationToken);
            try
            {
                return await action(_session);
            }
            catch (XiaomiCloudException retryException) when (retryException.AuthenticationExpired)
            {
                throw new AuthenticationRequiredException("Xiaomi 登录凭据已明确失效，需要重新登录。", retryException);
            }
        }
    }
}
