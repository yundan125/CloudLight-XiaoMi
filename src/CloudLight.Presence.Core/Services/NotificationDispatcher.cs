using System.Collections.Concurrent;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationDispatcher : INotificationDispatcher, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private readonly IPresenceRepository _repository;
    private readonly IReadOnlyDictionary<NotificationChannelType, INotificationChannel> _channels;
    private readonly INotificationDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private bool _disposed;

    public NotificationDispatcher(IPresenceRepository repository, IEnumerable<INotificationChannel> channels, INotificationDiagnostics? diagnostics = null)
    {
        _repository = repository;
        _channels = channels.ToDictionary(value => value.ChannelType);
        _diagnostics = diagnostics ?? NullNotificationDiagnostics.Instance;
        foreach (var channel in _channels.Values) channel.StatusChanged += ChannelStatusChanged;
    }

    public async Task DispatchAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (_disposed || !_inFlight.TryAdd($"rule:{request.DeliveryId}", 0)) return;
        try
        {
            var delivery = await _repository.GetNotificationDeliveryAsync(request.DeliveryId, cancellationToken);
            if (delivery is null || delivery.Status == NotificationDeliveryStatus.Delivered) return;
            if (delivery.RuleId is not { } ruleId) return;
            var rule = await _repository.GetNotificationRuleAsync(ruleId, cancellationToken);
            if (rule is null || !rule.Enabled)
            {
                await CancelDisabledRuleDeliveryAsync(delivery, cancellationToken);
                return;
            }
            if (IsDetectedCondition(rule.Condition)
                && !await IsValidEventDeliveryAsync(delivery, rule, cancellationToken))
            {
                await CancelInvalidEventDeliveryAsync(delivery, cancellationToken);
                return;
            }
            if (!_channels.TryGetValue(request.Channel, out var channel))
            {
                await RecordFailureAsync(delivery, "没有可用的通知通道。", cancellationToken);
                return;
            }

            NotificationSendResult result;
            try { result = await channel.SendAsync(request, delivery.SentParts, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { result = new NotificationSendResult(false, 0, delivery.TotalParts, SafeError(exception.Message)); }
            await RecordResultAsync(delivery, result, cancellationToken);
        }
        finally { _inFlight.TryRemove($"rule:{request.DeliveryId}", out _); }
    }

    public async Task DispatchSystemAsync(SystemNotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (_disposed || !_inFlight.TryAdd($"system:{delivery.Id}", 0)) return;
        try
        {
            var current = await _repository.GetSystemNotificationDeliveryAsync(delivery.Id, cancellationToken);
            if (current is null || current.Status is NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled) return;
            if (!_channels.TryGetValue(current.Channel, out var channel))
            {
                await RecordSystemFailureAsync(current, "没有可用的通知通道。", cancellationToken);
                return;
            }

            NotificationSendResult result;
            try
            {
                result = await channel.SendAsync(new NotificationRequest(0, 0, 0, current.EpisodeId, current.Channel, current.TargetType, current.TargetId, current.Message, current.CreatedAt), current.SentParts, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { result = new NotificationSendResult(false, 0, current.TotalParts, SafeError(exception.Message)); }
            await RecordSystemResultAsync(current, result, cancellationToken);
        }
        finally { _inFlight.TryRemove($"system:{delivery.Id}", out _); }
    }

    public async Task RetryPendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        var pending = await _repository.GetPendingNotificationDeliveriesAsync(now, cancellationToken);
        foreach (var delivery in pending)
        {
            try
            {
                var request = new NotificationRequest(delivery.Id, delivery.RuleId!.Value, delivery.SubjectId!.Value, delivery.EpisodeId, delivery.Channel, delivery.TargetType, delivery.TargetId, delivery.Message, delivery.CreatedAt);
                await DispatchAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _diagnostics.RecordAsync("retry_dispatch", exception, delivery.RuleId, delivery.Id, cancellationToken);
            }
        }
        foreach (var delivery in await _repository.GetPendingSystemNotificationDeliveriesAsync(now, cancellationToken))
        {
            try
            {
                await DispatchSystemAsync(delivery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _diagnostics.RecordAsync("retry_system_dispatch", exception, null, delivery.Id, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var channel in _channels.Values) channel.StatusChanged -= ChannelStatusChanged;
    }

    private async void ChannelStatusChanged(object? sender, NotificationChannelStatus status)
    {
        if (!status.Connected || _disposed) return;
        try
        {
            await RetryPendingAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await _diagnostics.RecordAsync("channel_connected_retry", exception, null, null, CancellationToken.None);
        }
    }

    private async Task RecordResultAsync(NotificationDelivery delivery, NotificationSendResult result, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var total = Math.Max(delivery.TotalParts, result.TotalParts);
        var sent = Math.Clamp(delivery.SentParts + Math.Max(0, result.SentParts), 0, total == 0 ? int.MaxValue : total);
        if (result.Success)
        {
            total = Math.Max(total, sent);
            sent = total;
            await _repository.UpdateNotificationDeliveryAsync(delivery with
            {
                Status = NotificationDeliveryStatus.Delivered,
                DeliveredAt = now,
                Error = null,
                SentParts = sent,
                TotalParts = total,
                LastAttemptAt = now,
                NextAttemptAt = null
            }, cancellationToken);
            if (delivery.RuleId is { } ruleId) await MarkDeliveredStateAsync(ruleId, delivery with { DeliveredAt = now }, cancellationToken);
        }
        else
        {
            await _repository.UpdateNotificationDeliveryAsync(delivery with
            {
                Status = NotificationDeliveryStatus.Failed,
                Error = SafeError(result.Error ?? "通知发送失败。"),
                SentParts = sent,
                TotalParts = total,
                LastAttemptAt = now,
                NextAttemptAt = now.Add(RetryDelay)
            }, cancellationToken);
            if (delivery.RuleId is { } ruleId) await MarkPendingStateAsync(ruleId, delivery.Id, result.Error, cancellationToken);
            await _diagnostics.RecordAsync("dispatch_result", new InvalidOperationException(result.Error ?? "通知发送失败。"), delivery.RuleId, delivery.Id, cancellationToken);
        }
    }

    private async Task RecordFailureAsync(NotificationDelivery delivery, string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateNotificationDeliveryAsync(delivery with { Status = NotificationDeliveryStatus.Failed, Error = SafeError(error), LastAttemptAt = now, NextAttemptAt = now.Add(RetryDelay) }, cancellationToken);
        if (delivery.RuleId is { } ruleId) await MarkPendingStateAsync(ruleId, delivery.Id, error, cancellationToken);
        await _diagnostics.RecordAsync("dispatch_channel", new InvalidOperationException(error), delivery.RuleId, delivery.Id, cancellationToken);
    }

    private async Task RecordSystemResultAsync(SystemNotificationDelivery delivery, NotificationSendResult result, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var total = Math.Max(delivery.TotalParts, result.TotalParts);
        var sent = Math.Clamp(delivery.SentParts + Math.Max(0, result.SentParts), 0, total == 0 ? int.MaxValue : total);
        if (result.Success)
        {
            total = Math.Max(total, sent); sent = total;
            await _repository.UpdateSystemNotificationDeliveryAsync(delivery with { Status = NotificationDeliveryStatus.Delivered, DeliveredAt = now, Error = null, SentParts = sent, TotalParts = total, LastAttemptAt = now, NextAttemptAt = null }, cancellationToken);
        }
        else
        {
            await _repository.UpdateSystemNotificationDeliveryAsync(delivery with { Status = NotificationDeliveryStatus.Failed, Error = SafeError(result.Error ?? "通知发送失败。"), SentParts = sent, TotalParts = total, LastAttemptAt = now, NextAttemptAt = now.Add(RetryDelay) }, cancellationToken);
            await _diagnostics.RecordAsync("system_dispatch_result", new InvalidOperationException(result.Error ?? "通知发送失败。"), null, delivery.Id, cancellationToken);
        }
    }

    private async Task RecordSystemFailureAsync(SystemNotificationDelivery delivery, string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateSystemNotificationDeliveryAsync(delivery with { Status = NotificationDeliveryStatus.Failed, Error = SafeError(error), LastAttemptAt = now, NextAttemptAt = now.Add(RetryDelay) }, cancellationToken);
        await _diagnostics.RecordAsync("system_dispatch_channel", new InvalidOperationException(error), null, delivery.Id, cancellationToken);
    }

    private async Task CancelDisabledRuleDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Status is NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled) return;
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateNotificationDeliveryAsync(delivery with
        {
            Status = NotificationDeliveryStatus.Canceled,
            Error = "自动提醒已关闭，投递已取消。",
            LastAttemptAt = now,
            NextAttemptAt = null
        }, cancellationToken);
        if (delivery.RuleId is { } ruleId) await ClearPendingStateAsync(ruleId, delivery.Id, cancellationToken);
    }

    private async Task CancelInvalidEventDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        if (delivery.Status is NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled) return;
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateNotificationDeliveryAsync(delivery with
        {
            Status = NotificationDeliveryStatus.Canceled,
            Error = "事件投递与主体事件不一致，已取消。",
            LastAttemptAt = now,
            NextAttemptAt = null
        }, cancellationToken);
        if (delivery.RuleId is { } ruleId) await ClearPendingStateAsync(ruleId, delivery.Id, cancellationToken);
    }

    private async Task<bool> IsValidEventDeliveryAsync(
        NotificationDelivery delivery,
        NotificationRule rule,
        CancellationToken cancellationToken)
    {
        if (!TryGetEventId(delivery.EpisodeId, out var eventId)) return false;
        var presenceEvent = await _repository.GetSubjectPresenceEventAsync(eventId, cancellationToken);
        if (presenceEvent is null || presenceEvent.SubjectId != rule.SubjectId || delivery.SubjectId != rule.SubjectId) return false;
        var expected = rule.Condition == NotificationCondition.DetectedOnline ? PresenceState.Online : PresenceState.Offline;
        if (!EventMatches(presenceEvent, expected)) return false;

        // A valid pending retry is created during the same evaluation window
        // as the event.  A much later creation is a historical replay from an
        // old runtime and must not be sent after restart/migration.
        return delivery.CreatedAt <= presenceEvent.ObservedAt.AddMinutes(10);
    }

    private static bool IsDetectedCondition(NotificationCondition condition) =>
        condition is NotificationCondition.DetectedOnline or NotificationCondition.DetectedOffline;

    private static bool TryGetEventId(string episodeId, out long eventId)
    {
        eventId = 0;
        return episodeId.StartsWith("event:", StringComparison.Ordinal)
            && long.TryParse(episodeId.AsSpan("event:".Length), out eventId)
            && eventId > 0;
    }

    private static bool EventMatches(SubjectPresenceEvent value, PresenceState expectedState) => expectedState switch
    {
        PresenceState.Online => value.EventType is SubjectPresenceEventType.ConfirmedOnline or SubjectPresenceEventType.DetectedOnlineAfterGap,
        PresenceState.Offline => value.EventType is SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.DetectedOfflineAfterGap,
        _ => false
    };

    private async Task ClearPendingStateAsync(long ruleId, long deliveryId, CancellationToken cancellationToken)
    {
        var state = await _repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        if (state is null || state.PendingDeliveryId != deliveryId) return;
        await _repository.UpsertNotificationRuleStateAsync(state with { PendingDelivery = false, PendingDeliveryId = null, LastDeliveryError = null, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    }

    private async Task MarkDeliveredStateAsync(long ruleId, NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var state = await _repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        if (state is null || state.PendingDeliveryId != delivery.Id) return;
        var belongsToCurrentEpisode = string.Equals(state.CurrentEpisodeId, delivery.EpisodeId, StringComparison.Ordinal);
        await _repository.UpsertNotificationRuleStateAsync(state with
        {
            TriggeredForCurrentEpisode = belongsToCurrentEpisode || state.TriggeredForCurrentEpisode,
            TriggeredAt = belongsToCurrentEpisode ? delivery.DeliveredAt ?? DateTimeOffset.UtcNow : state.TriggeredAt,
            PendingDelivery = false,
            PendingDeliveryId = null,
            LastDeliveryError = null,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task MarkPendingStateAsync(long ruleId, long deliveryId, string? error, CancellationToken cancellationToken)
    {
        var state = await _repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        if (state is null) return;
        await _repository.UpsertNotificationRuleStateAsync(state with { PendingDelivery = true, PendingDeliveryId = deliveryId, LastDeliveryError = SafeError(error ?? "通知发送失败。"), UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
    }

    private static string SafeError(string error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "通知发送失败。" : error.Trim();
        value = new string(value.Select(character => character is '\r' or '\n' or '\t' ? ' ' : character).ToArray());
        return value.Length > 500 ? value[..500] : value;
    }
}
