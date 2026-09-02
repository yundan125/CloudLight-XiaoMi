using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationRuleAdministrationService(IPresenceRepository repository)
{
    public async Task DisableRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        var rule = await GetRuleAsync(ruleId, cancellationToken);
        await UpdateCoreAsync(rule with { Enabled = false }, rule, cancellationToken);
    }

    public async Task EnableRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        var rule = await GetRuleAsync(ruleId, cancellationToken);
        if (!rule.Enabled) await UpdateCoreAsync(rule with { Enabled = true }, rule, cancellationToken);
    }

    public async Task UpdateRuleAsync(NotificationRule updatedRule, CancellationToken cancellationToken)
    {
        var current = await GetRuleAsync(updatedRule.Id, cancellationToken);
        await UpdateCoreAsync(updatedRule, current, cancellationToken);
    }

    public async Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken)
    {
        _ = await GetRuleAsync(ruleId, cancellationToken);
        await repository.DeleteNotificationRuleAsync(ruleId, cancellationToken);
    }

    private async Task<NotificationRule> GetRuleAsync(long ruleId, CancellationToken cancellationToken) =>
        await repository.GetNotificationRuleAsync(ruleId, cancellationToken)
        ?? throw new InvalidOperationException("通知规则不存在，可能已被删除。 ");

    private async Task UpdateCoreAsync(NotificationRule requested, NotificationRule current, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalized = requested with { CreatedAt = current.CreatedAt, UpdatedAt = now };
        var triggerSemanticsChanged = current.SubjectId != normalized.SubjectId
            || current.Condition != normalized.Condition
            || current.ThresholdSeconds != normalized.ThresholdSeconds;
        var isReenabled = !current.Enabled && normalized.Enabled;
        var recipientsChanged = !current.RecipientIds
            .Distinct()
            .OrderBy(value => value)
            .SequenceEqual(normalized.RecipientIds.Distinct().OrderBy(value => value));
        var legacyTargetChanged = current.TargetType != normalized.TargetType
            || !string.Equals(current.TargetId, normalized.TargetId, StringComparison.Ordinal);

        await repository.UpdateNotificationRuleAsync(normalized, cancellationToken);
        if (triggerSemanticsChanged || isReenabled)
            await ResetStateAsync(current.Id, normalized.SubjectId, now, cancellationToken);
        else if (!normalized.Enabled)
            await CancelPendingDeliveryAsync(current.Id, now, cancellationToken);
        else if (recipientsChanged || legacyTargetChanged)
            await CancelRemovedTargetDeliveriesAsync(normalized, now, cancellationToken);
    }

    private async Task ResetStateAsync(long ruleId, long subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        foreach (var delivery in await repository.GetNotificationDeliveriesForRuleAsync(ruleId, cancellationToken))
        {
            if (delivery.Status is NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled or NotificationDeliveryStatus.PermanentFailed) continue;
            await repository.UpdateNotificationDeliveryAsync(delivery with
            {
                Status = NotificationDeliveryStatus.Canceled,
                Error = "规则触发条件已修改，旧投递已取消。",
                LastAttemptAt = now,
                NextAttemptAt = null
            }, cancellationToken);
        }

        await repository.ResetNotificationRuleStateAsync(ruleId, subjectId, now, cancellationToken);
    }

    private async Task CancelPendingDeliveryAsync(long ruleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        var deliveries = await repository.GetNotificationDeliveriesForRuleAsync(ruleId, cancellationToken);
        var pending = deliveries.Where(delivery => delivery.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed).ToArray();
        foreach (var delivery in pending)
        {
            await repository.UpdateNotificationDeliveryAsync(delivery with
            {
                Status = NotificationDeliveryStatus.Canceled,
                Error = "自动提醒已关闭，旧投递已取消。",
                LastAttemptAt = now,
                NextAttemptAt = null
            }, cancellationToken);
        }
        if (state is null || pending.Length == 0) return;
        await repository.UpsertNotificationRuleStateAsync(state with { PendingDelivery = false, PendingDeliveryId = null, LastDeliveryError = null, UpdatedAt = now }, cancellationToken);
    }

    private async Task CancelRemovedTargetDeliveriesAsync(NotificationRule rule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recipientIds = rule.RecipientIds.Distinct().ToHashSet();
        var deliveries = await repository.GetNotificationDeliveriesForRuleAsync(rule.Id, cancellationToken);
        var stale = deliveries
            .Where(delivery => delivery.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed)
            .Where(delivery => !IsSelectedTarget(delivery, rule, recipientIds))
            .ToArray();
        foreach (var delivery in stale)
        {
            await repository.UpdateNotificationDeliveryAsync(delivery with
            {
                Status = NotificationDeliveryStatus.Canceled,
                Error = "规则接收人已移除，投递已取消。",
                LastAttemptAt = now,
                NextAttemptAt = null
            }, cancellationToken);
        }

        if (stale.Length == 0) return;
        var state = await repository.GetNotificationRuleStateAsync(rule.Id, cancellationToken);
        if (state?.PendingDeliveryId is not { } pendingId || stale.All(value => value.Id != pendingId)) return;

        var staleIds = stale.Select(value => value.Id).ToHashSet();
        var replacement = deliveries
            .Where(value => !staleIds.Contains(value.Id))
            .Where(value => value.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed)
            .OrderBy(value => value.Id)
            .FirstOrDefault();
        await repository.UpsertNotificationRuleStateAsync(state with
        {
            PendingDelivery = replacement is not null,
            PendingDeliveryId = replacement?.Id,
            LastDeliveryError = replacement?.Error,
            UpdatedAt = now
        }, cancellationToken);
    }

    private static bool IsSelectedTarget(
        NotificationDelivery delivery,
        NotificationRule rule,
        IReadOnlySet<long> recipientIds) =>
        recipientIds.Count > 0
            ? delivery.RecipientId is { } recipientId && recipientIds.Contains(recipientId)
            : delivery.RecipientId is null
              && delivery.TargetType == rule.TargetType
              && string.Equals(delivery.TargetId, rule.TargetId, StringComparison.Ordinal);
}
