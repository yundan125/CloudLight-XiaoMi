namespace CloudLight.Presence.Core.Models;

public enum PresenceState { Unknown = 0, Online = 1, Offline = 2 }
public enum PresenceEventType { Online = 1, Offline = 2, InitialObservation = 3 }
public enum PresenceSource { Polling = 1 }
public enum SubjectPresenceEventType
{
    // Keep the existing persisted values stable for prior monitoring-gap activity.
    DetectedOnlineAfterGap = 1,
    DetectedOfflineAfterGap = 2,
    InitialOnline = 3,
    InitialOffline = 4,
    ConfirmedOnline = 5,
    ConfirmedOffline = 6
}
public enum SubjectActivityType
{
    Online = 1,
    Offline = 2,
    UnknownPeriod = 3,
    DetectedOnlineAfterGap = 4,
    DetectedOfflineAfterGap = 5
}
public enum NotificationCondition
{
    OnlineFor = 1,
    OfflineFor = 2,
    DetectedOnline = 3,
    DetectedOffline = 4
}
public enum NotificationChannelType { QQ = 1 }
public enum NotificationTargetType { Private = 1, Group = 2 }
public enum NotificationDeliveryStatus { Pending = 1, Delivered = 2, Failed = 3, Canceled = 4 }
public enum SystemNotificationKind { XiaomiConnectionFailure = 1, XiaomiConnectionRecovery = 2 }
public enum NotificationConnectionState
{
    NotConfigured,
    Stopped,
    Authenticating,
    Connecting,
    Identifying,
    Connected,
    Reconnecting,
    AuthenticationFailed,
    GatewayFailed,
    Stopping
}

/// <summary>
/// A saved QQ OpenID.  OpenIDs remain ordinary local data so they can be
/// edited and reused; presentation code is responsible for masking them.
/// </summary>
public sealed record NotificationRecipient(
    long Id,
    string Note,
    string OpenId,
    NotificationTargetType TargetType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Note) ? $"QQ {TargetTypeText}" : Note;
    public string TargetTypeText => TargetType == NotificationTargetType.Group ? "群聊" : "私聊";
}

/// <summary>
/// A resolved notification target used by system notifications and legacy
/// compatibility paths.
/// </summary>
public sealed record NotificationRecipientTarget(
    long? RecipientId,
    NotificationTargetType TargetType,
    string TargetId,
    string? DisplayName = null);

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
    // CurrentState is the observation for the current monitoring run. It is
    // deliberately allowed to be Unknown between runs, while the historical
    // state remains available for continuity and duration calculations.
    public PresenceState? LastKnownHistoricalState { get; init; }
    public PresenceState CurrentObservedState => CurrentState;

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

/// <summary>
/// A subject-level observation made on the first successful poll after a
/// monitoring gap.  ObservedAt is intentionally not a claimed transition
/// time; the actual transition happened somewhere in the unobserved interval.
/// </summary>
public sealed record SubjectPresenceEvent(
    long Id,
    long SubjectId,
    SubjectPresenceEventType EventType,
    DateTimeOffset ObservedAt,
    long? MonitoringGapId,
    DateTimeOffset? StateSince = null)
{
    /// <summary>
    /// The effective boundary used by the confirmed subject timeline.  A
    /// confirmed offline transition may be persisted after its grace window,
    /// while still starting at the first all-offline observation.
    /// </summary>
    public DateTimeOffset EffectiveAt => StateSince ?? ObservedAt;
}

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

/// <summary>
/// The aggregate state captured before a monitoring gap is first reconciled.
/// It is deliberately immutable for that gap so a failed or cancelled
/// multi-MAC snapshot cannot change the comparison baseline for its retry.
/// </summary>
public sealed record MonitoringGapSubjectBaseline(
    long MonitoringGapId,
    long SubjectId,
    PresenceState State);

/// <summary>
/// The last confirmed aggregate state for a person/device subject.  This is
/// intentionally separate from individual MAC observations: one phone may
/// move between bands without changing the subject's online episode.
/// </summary>
public sealed record SubjectCurrentState(
    long SubjectId,
    PresenceState CurrentState,
    DateTimeOffset StateSince,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? PendingOfflineSince = null);

public sealed record PresenceSubject(
    long Id,
    Guid ExportId,
    string DisplayName,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SubjectDeviceMembership(
    long SubjectId,
    long NetworkDeviceId,
    DateTimeOffset CreatedAt);

public sealed record SubjectPresenceSnapshot(
    PresenceSubject Subject,
    IReadOnlyList<NetworkDevice> Members,
    PresenceState CurrentState,
    DateTimeOffset? LastStateChangedAt,
    NetworkDevice? ActiveDevice,
    DateTimeOffset? ConfirmedStateSince = null,
    DateTimeOffset? LastOnlineTime = null,
    DateTimeOffset? LastOfflineTime = null,
    string? RouterName = null);

public sealed record SubjectPresenceFact(
    PresenceSubject Subject,
    IReadOnlyList<NetworkDevice> Members,
    PresenceState CurrentState,
    DateTimeOffset? StateSince,
    bool StateSinceKnown,
    TimeSpan ConfirmedDuration,
    DateTimeOffset? LastOnlineTime,
    DateTimeOffset? LastOfflineTime,
    NetworkDevice? ActiveDevice,
    string? RouterName,
    DateTimeOffset? NotificationStateSince = null);

public sealed record NotificationRule(
    long Id,
    long SubjectId,
    bool Enabled,
    NotificationCondition Condition,
    long ThresholdSeconds,
    NotificationChannelType Channel,
    NotificationTargetType TargetType,
    string TargetId,
    string MessageTemplate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Saved recipients for the rule.  Empty means the legacy TargetType /
    /// TargetId pair is still in use.
    /// </summary>
    public IReadOnlyList<long> RecipientIds { get; init; } = [];
}

public sealed record NotificationRuleState(
    long RuleId,
    string? CurrentEpisodeId,
    DateTimeOffset? StateSince,
    bool TriggeredForCurrentEpisode,
    DateTimeOffset? TriggeredAt,
    bool PendingDelivery,
    long? PendingDeliveryId,
    string? LastDeliveryError,
    DateTimeOffset UpdatedAt,
    long? LastProcessedSubjectEventId = null);

public sealed record NotificationDelivery(
    long Id,
    long? RuleId,
    long? SubjectId,
    string EpisodeId,
    DateTimeOffset CreatedAt,
    NotificationDeliveryStatus Status,
    DateTimeOffset? DeliveredAt,
    NotificationChannelType Channel,
    NotificationTargetType TargetType,
    string TargetId,
    string Message,
    string? Error,
    int SentParts,
    int TotalParts,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    long? RecipientId = null);

public sealed record SystemNotificationDelivery(
    long Id,
    SystemNotificationKind Kind,
    string EpisodeId,
    DateTimeOffset CreatedAt,
    NotificationDeliveryStatus Status,
    DateTimeOffset? DeliveredAt,
    NotificationChannelType Channel,
    NotificationTargetType TargetType,
    string TargetId,
    string Message,
    string? Error,
    int SentParts,
    int TotalParts,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    long? RecipientId = null);

public sealed record ConnectionAlertState(
    string? FailureEpisodeId,
    DateTimeOffset? FailureStartedAt,
    DateTimeOffset? LastSuccessfulCloudUpdateAt,
    bool FailureAlertSent,
    bool RecoveryAlertSent,
    DateTimeOffset UpdatedAt);

public sealed record ConnectionAlertSettings(
    bool Enabled = true,
    bool RecoveryEnabled = true,
    bool UseDefaultTarget = true,
    NotificationTargetType TargetType = NotificationTargetType.Private,
    string TargetId = "")
{
    public IReadOnlyList<long> RecipientIds { get; init; } = [];
}

public sealed record ConnectionAlertConfiguration(
    ConnectionAlertSettings Settings,
    NotificationTargetType DefaultTargetType,
    string DefaultTargetId,
    IReadOnlyList<NotificationRecipientTarget>? DefaultTargets = null);

public sealed record NotificationRequest(
    long DeliveryId,
    long RuleId,
    long SubjectId,
    string EpisodeId,
    NotificationChannelType Channel,
    NotificationTargetType TargetType,
    string TargetId,
    string Message,
    DateTimeOffset CreatedAt);

public sealed record NotificationSendResult(
    bool Success,
    int SentParts,
    int TotalParts,
    string? Error = null,
    IReadOnlyList<string>? MessageIds = null);

public sealed record NotificationChannelStatus(
    NotificationChannelType Channel,
    bool Configured,
    bool Running,
    bool Connected,
    NotificationConnectionState ConnectionState,
    string? LastError = null,
    int ReconnectCount = 0,
    DateTimeOffset? AccessTokenExpiresAt = null,
    DateTimeOffset? LastConnectedAt = null,
    DateTimeOffset? LastHeartbeatAt = null,
    DateTimeOffset? LastHeartbeatAckAt = null);

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

public sealed record SubjectActivityItem(DateTimeOffset OccurredAtUtc, SubjectActivityType Type);

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
