namespace CloudLight.Presence.Core.Models;

public enum PresenceState { Unknown = 0, Online = 1, Offline = 2 }
public enum PresenceEventType { Online = 1, Offline = 2, InitialObservation = 3 }
public enum PresenceSource { Polling = 1 }

public sealed record Router(
    long Id,
    string MiotDid,
    string MiotModel,
    string PartnerId,
    string Name,
    string? HomeId,
    string? RoomId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

public sealed record NetworkDevice(
    long Id,
    long RouterId,
    string MacAddress,
    string? OriginalName,
    string? OriginName,
    string? CustomName,
    string? Note,
    string? LastIp,
    string? ConnectionType,
    int? Signal,
    PresenceState CurrentState,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastStateChangedAt)
{
    public string DisplayName => FirstNonEmpty(CustomName, OriginalName, OriginName, MacAddress);

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}

public sealed record PresenceEvent(
    long Id,
    long DeviceId,
    PresenceEventType EventType,
    DateTimeOffset ObservedAt,
    PresenceSource Source);

public sealed record PresenceSession(
    long Id,
    long DeviceId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool StartKnown,
    bool EndKnown);

public sealed record MonitoringGap(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Reason);

public sealed record ApplicationRun(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? LastSuccessfulCloudUpdateAt);

public sealed record PresenceStatistics(
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan KnownOnlineDuration,
    TimeSpan KnownOfflineDuration,
    TimeSpan UnknownDuration)
{
    public TimeSpan KnownDuration => KnownOnlineDuration + KnownOfflineDuration;
    public double Coverage => WindowDuration.TotalSeconds <= 0 ? 0 : KnownDuration.TotalSeconds / WindowDuration.TotalSeconds;
    public double OnlinePercentageOfKnownTime => KnownDuration.TotalSeconds <= 0 ? 0 : KnownOnlineDuration.TotalSeconds / KnownDuration.TotalSeconds;
    public TimeSpan WindowDuration => To > From ? To - From : TimeSpan.Zero;
}

public sealed record PresenceTimelineSegment(DateTimeOffset Start, DateTimeOffset End, PresenceState State);

public sealed record ObservedNetworkDevice(
    string MacAddress,
    string? Name,
    string? OriginName,
    string? Ip,
    bool Online,
    long? OnlineTime,
    string? ConnectionType,
    int? Signal,
    long? DownloadRate = null,
    long? UploadRate = null,
    long? Traffic = null);
