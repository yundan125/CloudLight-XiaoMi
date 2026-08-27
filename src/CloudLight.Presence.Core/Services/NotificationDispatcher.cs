using System.Collections.Concurrent;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationDispatcher : INotificationDispatcher, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private readonly IPresenceRepository _repository;
    private readonly IReadOnlyDictionary<NotificationChannelType, INotificationChannel> _channels;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private bool _disposed;

    public NotificationDispatcher(IPresenceRepository repository, IEnumerable<INotificationChannel> channels)
    {
        _repository = repository;
        _channels = channels.ToDictionary(value => value.ChannelType);
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
            if (rule is null || !rule.Enabled) return;
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
            var request = new NotificationRequest(delivery.Id, delivery.RuleId!.Value, delivery.SubjectId!.Value, delivery.EpisodeId, delivery.Channel, delivery.TargetType, delivery.TargetId, delivery.Message, delivery.CreatedAt);
            await DispatchAsync(request, cancellationToken);
        }
        foreach (var delivery in await _repository.GetPendingSystemNotificationDeliveriesAsync(now, cancellationToken))
            await DispatchSystemAsync(delivery, cancellationToken);
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
        try { await RetryPendingAsync(DateTimeOffset.UtcNow, CancellationToken.None); } catch { }
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
            if (delivery.RuleId is { } ruleId) await ClearPendingStateAsync(ruleId, delivery.Id, cancellationToken);
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
        }
    }

    private async Task RecordFailureAsync(NotificationDelivery delivery, string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateNotificationDeliveryAsync(delivery with { Status = NotificationDeliveryStatus.Failed, Error = SafeError(error), LastAttemptAt = now, NextAttemptAt = now.Add(RetryDelay) }, cancellationToken);
        if (delivery.RuleId is { } ruleId) await MarkPendingStateAsync(ruleId, delivery.Id, error, cancellationToken);
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
        }
    }

    private async Task RecordSystemFailureAsync(SystemNotificationDelivery delivery, string error, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateSystemNotificationDeliveryAsync(delivery with { Status = NotificationDeliveryStatus.Failed, Error = SafeError(error), LastAttemptAt = now, NextAttemptAt = now.Add(RetryDelay) }, cancellationToken);
    }

    private async Task ClearPendingStateAsync(long ruleId, long deliveryId, CancellationToken cancellationToken)
    {
        var state = await _repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        if (state is null || state.PendingDeliveryId != deliveryId) return;
        await _repository.UpsertNotificationRuleStateAsync(state with { PendingDelivery = false, PendingDeliveryId = null, LastDeliveryError = null, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
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
