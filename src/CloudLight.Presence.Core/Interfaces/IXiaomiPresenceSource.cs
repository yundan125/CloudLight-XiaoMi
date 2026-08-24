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

public class AuthenticationRequiredException(string message, Exception? inner = null) : Exception(message, inner);
