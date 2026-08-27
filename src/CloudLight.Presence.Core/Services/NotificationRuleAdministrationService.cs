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

        await repository.UpdateNotificationRuleAsync(normalized, cancellationToken);
        if (triggerSemanticsChanged)
            await ResetStateAsync(current.Id, now, cancellationToken);
        else if (!normalized.Enabled)
            await CancelPendingDeliveryAsync(current.Id, now, cancellationToken);
    }

    private async Task ResetStateAsync(long ruleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        if (state?.PendingDeliveryId is { } pendingId && await repository.GetNotificationDeliveryAsync(pendingId, cancellationToken) is { } delivery && delivery.Status is not NotificationDeliveryStatus.Delivered and not NotificationDeliveryStatus.Canceled)
            await repository.UpdateNotificationDeliveryAsync(delivery with
            {
                Status = NotificationDeliveryStatus.Canceled,
                Error = "规则触发条件已修改，旧投递已取消。",
                LastAttemptAt = now,
                NextAttemptAt = null
            }, cancellationToken);

        await repository.UpsertNotificationRuleStateAsync(new NotificationRuleState(ruleId, null, null, false, null, false, null, null, now), cancellationToken);
    }

    private async Task CancelPendingDeliveryAsync(long ruleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await repository.GetNotificationRuleStateAsync(ruleId, cancellationToken);
        if (state?.PendingDeliveryId is not { } pendingId) return;
        var delivery = await repository.GetNotificationDeliveryAsync(pendingId, cancellationToken);
        if (delivery is null || delivery.Status is NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled) return;
        await repository.UpdateNotificationDeliveryAsync(delivery with
        {
            Status = NotificationDeliveryStatus.Canceled,
            Error = "自动提醒已关闭，旧投递已取消。",
            LastAttemptAt = now,
            NextAttemptAt = null
        }, cancellationToken);
        await repository.UpsertNotificationRuleStateAsync(state with { PendingDelivery = false, PendingDeliveryId = null, LastDeliveryError = null, UpdatedAt = now }, cancellationToken);
    }
}
