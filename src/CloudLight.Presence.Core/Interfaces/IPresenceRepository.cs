using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Interfaces;

public interface IPresenceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<Router> UpsertRouterAsync(Router router, CancellationToken cancellationToken);
    Task<IReadOnlyList<Router>> GetRoutersAsync(CancellationToken cancellationToken);
    Task<NetworkDevice?> FindDeviceAsync(long routerId, string macAddress, CancellationToken cancellationToken);
    Task<NetworkDevice?> GetDeviceAsync(long deviceId, CancellationToken cancellationToken);
    Task<NetworkDevice> InsertDeviceAsync(NetworkDevice device, CancellationToken cancellationToken);
    Task UpdateDeviceAsync(NetworkDevice device, CancellationToken cancellationToken);
    Task<IReadOnlyList<NetworkDevice>> GetDevicesAsync(long routerId, CancellationToken cancellationToken);
    Task UpdateDeviceMetadataAsync(long deviceId, string? customName, string? note, CancellationToken cancellationToken);
    Task AddEventAsync(PresenceEvent presenceEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceEvent>> GetEventsAsync(long deviceId, CancellationToken cancellationToken);
    Task AddSessionAsync(PresenceSession session, CancellationToken cancellationToken);
    Task CloseOpenSessionAsync(long deviceId, DateTimeOffset endedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceSession>> GetSessionsAsync(long deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitoringGap>> GetMonitoringGapsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<long> StartMonitoringGapAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken);
    Task EndMonitoringGapAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken);
    Task CloseOpenMonitoringGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken);
}

public interface IPresenceStatisticsService
{
    Task<PresenceStatistics> GetStatisticsAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public interface ISecureSessionStore
{
    bool Exists { get; }
    Task<string> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(string json, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}
