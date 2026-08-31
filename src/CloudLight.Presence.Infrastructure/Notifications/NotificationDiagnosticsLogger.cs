using System.Text;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Infrastructure.Diagnostics;
using CloudLight.Presence.Infrastructure.Settings;

namespace CloudLight.Presence.Infrastructure.Notifications;

/// <summary>
/// Writes only operational metadata for notification work. It never logs
/// channel credentials, targets, request headers, or message bodies.
/// </summary>
public sealed class NotificationDiagnosticsLogger(IAppDataPaths paths) : INotificationDiagnostics
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Path => System.IO.Path.Combine(paths.LogsDirectory, "notification-runtime.log");

    public async Task RecordAsync(string stage, Exception exception, long? ruleId, long? deliveryId, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                var entry = $"[{DateTimeOffset.UtcNow:O}] stage={Safe(stage, 80)}; ruleId={ruleId?.ToString() ?? "-"}; deliveryId={deliveryId?.ToString() ?? "-"}; exception={exception.GetType().Name}; message={Safe(exception.Message, 500)}{Environment.NewLine}";
                await File.AppendAllTextAsync(Path, entry, Encoding.UTF8, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // A diagnostics failure must never stop presence monitoring or a
            // pending notification retry.
        }
    }

    public async Task RecordDeliveryCreatedAsync(NotificationRule rule, SubjectPresenceFact fact, SubjectPresenceEvent presenceEvent, NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                var entry = $"[{DateTimeOffset.UtcNow:O}] stage=delivery_created; ruleId={rule.Id}; subjectId={rule.SubjectId}; subjectEventId={presenceEvent.Id}; episodeId={Safe(delivery.EpisodeId, 160)}; condition={rule.Condition}; currentState={fact.CurrentState}; eventType={presenceEvent.EventType}; eventOccurredAt={presenceEvent.ObservedAt:O}; deliveryId={delivery.Id}{Environment.NewLine}";
                await File.AppendAllTextAsync(Path, entry, Encoding.UTF8, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // A diagnostics failure must never stop presence monitoring or a
            // pending notification retry.
        }
    }

    private static string Safe(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim();
        normalized = new string(normalized.Select(value => value is '\r' or '\n' or '\t' ? ' ' : value).ToArray());
        normalized = DiagnosticsRedaction.RedactText(normalized);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
