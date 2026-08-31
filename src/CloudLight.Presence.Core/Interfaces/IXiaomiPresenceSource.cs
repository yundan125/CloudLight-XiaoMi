using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Interfaces;

public sealed record XiaomiRouterDevice(
    string MiotDid,
    string MiotModel,
    string PartnerId,
    string Name,
    string? HomeId,
    string? RoomId);

public interface IXiaomiPresenceSource
{
    bool HasStoredLogin { get; }
    Task LoginAsync(CancellationToken cancellationToken);
    Task RestoreAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<XiaomiRouterDevice>> DiscoverRoutersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ObservedNetworkDevice>> GetDevicesAsync(string partnerId, CancellationToken cancellationToken);
}

/// <summary>
/// Optional richer probe contract. The legacy presence source contract stays
/// small so test sources and third-party integrations remain compatible.
/// </summary>
public interface IXiaomiPresenceDiagnosticsSource
{
    Task<RouterPresenceProbeResult> GetDevicesWithDiagnosticsAsync(
        XiaomiRouterDevice router,
        CancellationToken cancellationToken);
}

public sealed class RouterPresenceProbeException(
    string message,
    RouterCapabilityDiagnostic diagnostic,
    Exception? inner = null) : Exception(message, inner)
{
    public RouterCapabilityDiagnostic Diagnostic { get; } = diagnostic;
}

public class AuthenticationRequiredException(string message, Exception? inner = null) : Exception(message, inner);
