using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NullNotificationDiagnostics : INotificationDiagnostics
{
    public static readonly NullNotificationDiagnostics Instance = new();

    private NullNotificationDiagnostics()
    {
    }

    public Task RecordAsync(string stage, Exception exception, long? ruleId, long? deliveryId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordDeliveryCreatedAsync(NotificationRule rule, SubjectPresenceFact fact, SubjectPresenceEvent presenceEvent, NotificationDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
}
