using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Interfaces;

public interface IPresenceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<Router> UpsertRouterAsync(Router router, CancellationToken cancellationToken);
    Task<IReadOnlyList<Router>> GetRoutersAsync(CancellationToken cancellationToken);
    Task<RouterCapabilityDiagnostic?> GetRouterCapabilityDiagnosticAsync(long routerId, CancellationToken cancellationToken);
    Task UpsertRouterCapabilityDiagnosticAsync(RouterCapabilityDiagnostic diagnostic, CancellationToken cancellationToken);
    Task<NetworkDevice?> FindDeviceAsync(long routerId, string macAddress, CancellationToken cancellationToken);
    Task<NetworkDevice?> GetDeviceAsync(long deviceId, CancellationToken cancellationToken);
    Task<NetworkDevice> InsertDeviceAsync(NetworkDevice device, CancellationToken cancellationToken);
    Task UpdateDeviceAsync(NetworkDevice device, CancellationToken cancellationToken);
    Task<IReadOnlyList<NetworkDevice>> GetDevicesAsync(long routerId, CancellationToken cancellationToken);
    Task ResetCurrentObservedStateAsync(long routerId, CancellationToken cancellationToken);
    Task UpdateDeviceMetadataAsync(long deviceId, string? customName, string? note, CancellationToken cancellationToken);
    Task<PresenceSubject> CreateSubjectAsync(string displayName, string? note, Guid exportId, DateTimeOffset createdAt, CancellationToken cancellationToken);
    Task UpdateSubjectAsync(long subjectId, string displayName, string? note, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task DeleteSubjectAsync(long subjectId, CancellationToken cancellationToken);
    Task<PresenceSubject?> GetSubjectAsync(long subjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceSubject>> GetSubjectsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NetworkDevice>> GetSubjectDevicesAsync(long subjectId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<long, long>> GetDeviceSubjectMapAsync(long routerId, CancellationToken cancellationToken);
    Task EnsureEveryDeviceHasSubjectAsync(CancellationToken cancellationToken);
    Task SetSubjectDevicesAsync(long subjectId, IReadOnlyCollection<long> deviceIds, DateTimeOffset createdAt, CancellationToken cancellationToken);
    Task AddEventAsync(PresenceEvent presenceEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceEvent>> GetEventsAsync(long deviceId, CancellationToken cancellationToken);
    Task<SubjectCurrentState?> GetSubjectCurrentStateAsync(long subjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SubjectCurrentState>> GetSubjectCurrentStatesAsync(IReadOnlyCollection<long> subjectIds, CancellationToken cancellationToken);
    Task UpsertSubjectCurrentStateAsync(SubjectCurrentState state, CancellationToken cancellationToken);
    Task RecordSubjectStateAndEventAsync(SubjectCurrentState state, SubjectPresenceEvent presenceEvent, CancellationToken cancellationToken);
    Task AddSubjectPresenceEventAsync(SubjectPresenceEvent presenceEvent, CancellationToken cancellationToken);
    Task<SubjectPresenceEvent?> GetSubjectPresenceEventAsync(long eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SubjectPresenceEvent>> GetSubjectPresenceEventsAfterIdAsync(long subjectId, long afterEventId, CancellationToken cancellationToken);
    Task<long?> GetLatestSubjectPresenceEventIdAsync(long subjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SubjectPresenceEvent>> GetSubjectPresenceEventsAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task AddSessionAsync(PresenceSession session, CancellationToken cancellationToken);
    Task CloseOpenSessionAsync(long deviceId, DateTimeOffset endedAt, CancellationToken cancellationToken);
    Task CloseOpenSessionAtBoundaryAsync(long deviceId, DateTimeOffset endedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceSession>> GetSessionsAsync(long deviceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonitoringGap>> GetMonitoringGapsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken, long? routerId = null);
    Task<IReadOnlyList<MonitoringGapSubjectBaseline>> GetMonitoringGapSubjectBaselinesAsync(long monitoringGapId, CancellationToken cancellationToken);
    Task AddMonitoringGapSubjectBaselineAsync(MonitoringGapSubjectBaseline baseline, CancellationToken cancellationToken);
    Task<long> StartMonitoringGapAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken, long? routerId = null);
    Task EndMonitoringGapAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken);
    Task CloseOpenMonitoringGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken, long? routerId = null);
    Task<long> StartApplicationRunAsync(DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task UpdateApplicationRunCloudUpdateAsync(long runId, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task EndApplicationRunAsync(long runId, DateTimeOffset endedAt, CancellationToken cancellationToken);
    Task<Router?> GetRouterAsync(long routerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationRule>> GetNotificationRulesAsync(bool enabledOnly, CancellationToken cancellationToken);
    Task<NotificationRule?> GetNotificationRuleAsync(long ruleId, CancellationToken cancellationToken);
    Task<NotificationRule> CreateNotificationRuleAsync(NotificationRule rule, CancellationToken cancellationToken);
    Task UpdateNotificationRuleAsync(NotificationRule rule, CancellationToken cancellationToken);
    Task DeleteNotificationRuleAsync(long ruleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationRecipient>> GetNotificationRecipientsAsync(CancellationToken cancellationToken);
    Task<NotificationRecipient?> GetNotificationRecipientAsync(long recipientId, CancellationToken cancellationToken);
    Task<NotificationRecipient> CreateNotificationRecipientAsync(NotificationRecipient recipient, CancellationToken cancellationToken);
    Task UpdateNotificationRecipientAsync(NotificationRecipient recipient, CancellationToken cancellationToken);
    Task DeleteNotificationRecipientAsync(long recipientId, CancellationToken cancellationToken);
    Task<int> GetNotificationRecipientUsageCountAsync(long recipientId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationRecipient>> GetNotificationRuleRecipientsAsync(long ruleId, CancellationToken cancellationToken);
    Task SetNotificationRuleRecipientsAsync(long ruleId, IReadOnlyCollection<long> recipientIds, CancellationToken cancellationToken);
    Task<NotificationRuleState?> GetNotificationRuleStateAsync(long ruleId, CancellationToken cancellationToken);
    Task UpsertNotificationRuleStateAsync(NotificationRuleState state, CancellationToken cancellationToken);
    Task ResetNotificationRuleStateAsync(long ruleId, long subjectId, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    Task EnsureNotificationRuleEventWatermarksAsync(CancellationToken cancellationToken);
    Task<NotificationDelivery?> GetNotificationDeliveryAsync(long deliveryId, CancellationToken cancellationToken);
    Task<NotificationDelivery?> GetNotificationDeliveryForEpisodeAsync(long ruleId, string episodeId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationDelivery>> GetNotificationDeliveriesForEpisodeAsync(long ruleId, string episodeId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationDelivery>> GetNotificationDeliveriesForRuleAsync(long ruleId, CancellationToken cancellationToken);
    Task<NotificationDelivery> CreateNotificationDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken);
    Task UpdateNotificationDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationDelivery>> GetPendingNotificationDeliveriesAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationDelivery>> GetRecentNotificationDeliveriesAsync(int limit, CancellationToken cancellationToken);
    Task<ConnectionAlertState?> GetConnectionAlertStateAsync(CancellationToken cancellationToken);
    Task UpsertConnectionAlertStateAsync(ConnectionAlertState state, CancellationToken cancellationToken);
    Task<SystemNotificationDelivery?> GetSystemNotificationDeliveryAsync(long deliveryId, CancellationToken cancellationToken);
    Task<SystemNotificationDelivery> CreateSystemNotificationDeliveryAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken);
    Task UpdateSystemNotificationDeliveryAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken);
    Task<IReadOnlyList<SystemNotificationDelivery>> GetPendingSystemNotificationDeliveriesAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<SystemNotificationDelivery>> GetRecentSystemNotificationDeliveriesAsync(int limit, CancellationToken cancellationToken);
    Task ReconcileSubjectIdentityAsync(CancellationToken cancellationToken);
    Task MergeSubjectsAsync(long sourceSubjectId, long targetSubjectId, DateTimeOffset updatedAt, CancellationToken cancellationToken);
}

public interface IPresenceStatisticsService
{
    Task<PresenceStatistics> GetStatisticsAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public interface ISubjectPresenceService
{
    TimeSpan OfflineGracePeriod { get; }
    Task<SubjectPresenceSnapshot?> GetSnapshotAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<SubjectPresenceFact?> GetCurrentFactAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<PresenceStatistics> GetSubjectStatisticsAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public interface INotificationRuleService
{
    Task<IReadOnlyList<NotificationRequest>> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<RuleEvaluationDiagnostic> EvaluateDiagnosticAsync(long ruleId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface INotificationChannel
{
    NotificationChannelType ChannelType { get; }
    NotificationChannelStatus Status { get; }
    event EventHandler<NotificationChannelStatus>? StatusChanged;
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<NotificationSendResult> SendAsync(NotificationRequest request, int startPart, CancellationToken cancellationToken);
    Task<NotificationSendResult> SendTestAsync(NotificationTargetType targetType, string targetId, CancellationToken cancellationToken);
}

public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationRequest request, CancellationToken cancellationToken);
    Task DispatchSystemAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken);
    Task RetryPendingAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// A deliberately small, credential-free diagnostic sink for automatic
/// notification work. Implementations must never throw back into the runtime.
/// </summary>
public interface INotificationDiagnostics
{
    Task RecordAsync(string stage, Exception exception, long? ruleId, long? deliveryId, CancellationToken cancellationToken);
    Task RecordDeliveryCreatedAsync(NotificationRule rule, SubjectPresenceFact fact, SubjectPresenceEvent presenceEvent, NotificationDelivery delivery, CancellationToken cancellationToken);
}

public interface ISecureSessionStore
{
    bool Exists { get; }
    Task<string> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(string json, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}
