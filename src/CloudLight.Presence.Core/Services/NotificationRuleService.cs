using System.Text;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationRuleService(
    IPresenceRepository repository,
    ISubjectPresenceService presence,
    INotificationDiagnostics? diagnostics = null) : INotificationRuleService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly INotificationDiagnostics _diagnostics = diagnostics ?? NullNotificationDiagnostics.Instance;

    public async Task<IReadOnlyList<NotificationRequest>> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var requests = new List<NotificationRequest>();
            var rules = await repository.GetNotificationRulesAsync(enabledOnly: true, cancellationToken);
            foreach (var rule in rules)
            {
                try
                {
                    requests.AddRange(await EvaluateRuleAsync(rule, now, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await _diagnostics.RecordAsync("rule_evaluate", exception, rule.Id, null, cancellationToken);
                }
            }
            return requests;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<NotificationRequest>> EvaluateRuleAsync(NotificationRule rule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return rule.Condition switch
        {
            NotificationCondition.OnlineFor => await EvaluateContinuousAsync(rule, PresenceState.Online, now, cancellationToken),
            NotificationCondition.OfflineFor => await EvaluateContinuousAsync(rule, PresenceState.Offline, now, cancellationToken),
            NotificationCondition.DetectedOnline => await EvaluateDetectedAsync(rule, PresenceState.Online, now, cancellationToken),
            NotificationCondition.DetectedOffline => await EvaluateDetectedAsync(rule, PresenceState.Offline, now, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Condition, "通知条件无效。")
        };
    }

    private async Task<IReadOnlyList<NotificationRequest>> EvaluateContinuousAsync(
        NotificationRule rule,
        PresenceState expectedState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fact = await presence.GetCurrentFactAsync(rule.SubjectId, now, cancellationToken);
        var state = await GetRuleStateAsync(rule, now, cancellationToken);
        if (fact is null || fact.CurrentState != expectedState || fact.StateSince is null)
        {
            await repository.UpsertNotificationRuleStateAsync(
                await ResetInactiveRuleStateAsync(state, now, cancellationToken), cancellationToken);
            return [];
        }

        var stateSince = fact.StateSince.Value;
        var episodeId = StateEpisodeId(expectedState, stateSince);
        var allDeliveries = await repository.GetNotificationDeliveriesForRuleAsync(rule.Id, cancellationToken);
        var episodeDeliveries = allDeliveries
            .Where(value => string.Equals(value.EpisodeId, episodeId, StringComparison.Ordinal))
            .ToList();
        // Builds before the confirmed-subject migration used "1:<ticks>"
        // and "2:<ticks>" as duration episode IDs. Reuse one of those rows
        // when present so a delivered legacy reminder is not sent again after
        // upgrade/restart.
        if (episodeDeliveries.Count == 0)
        {
            var legacyEpisodeId = LegacyStateEpisodeId(expectedState, stateSince);
            if (!string.Equals(legacyEpisodeId, episodeId, StringComparison.Ordinal))
                episodeDeliveries = allDeliveries
                    .Where(value => string.Equals(value.EpisodeId, legacyEpisodeId, StringComparison.Ordinal))
                    .ToList();
            if (episodeDeliveries.Count > 0) episodeId = episodeDeliveries[0].EpisodeId;
        }
        if (episodeDeliveries.Count == 0)
        {
            // A legacy delivery can carry the old boundary in its episode ID
            // even when the one-time subject reconciliation corrected
            // StateSince. Match only a delivery created inside the current
            // confirmed state window and with the same state prefix; an
            // earlier episode can therefore never suppress a new reminder.
            episodeDeliveries = allDeliveries
                .Where(value => value.Status is not NotificationDeliveryStatus.Canceled
                    && value.CreatedAt >= stateSince && value.CreatedAt <= now
                    && IsStateEpisode(value.EpisodeId, expectedState))
                .OrderBy(value => value.CreatedAt)
                .ThenBy(value => value.Id)
                .ToList();
            if (episodeDeliveries.Count > 0) episodeId = episodeDeliveries[0].EpisodeId;
        }
        var targets = await ResolveTargetsAsync(rule, cancellationToken);
        var thresholdReached = now >= stateSince && now - stateSince >= TimeSpan.FromSeconds(rule.ThresholdSeconds);
        var deliveries = new List<NotificationDelivery>();
        if (thresholdReached)
        {
            foreach (var target in targets)
            {
                var delivery = episodeDeliveries.FirstOrDefault(value => MatchesTarget(value, target));
                if (delivery is null)
                {
                    var message = NotificationTemplateRenderer.Render(rule, fact, now);
                    delivery = await CreateDeliveryAsync(rule, episodeId, now, target, message, cancellationToken);
                }
                deliveries.Add(delivery);
            }
        }
        else deliveries.AddRange(episodeDeliveries);

        await repository.UpsertNotificationRuleStateAsync(ToRuleState(state, episodeId, stateSince, deliveries, now), cancellationToken);
        return deliveries
            .Where(value => value.Status is not (NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled) && IsDue(value, now))
            .Select(ToRequest)
            .ToArray();
    }

    private async Task<IReadOnlyList<NotificationRequest>> EvaluateDetectedAsync(
        NotificationRule rule,
        PresenceState expectedState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fact = await presence.GetCurrentFactAsync(rule.SubjectId, now, cancellationToken);
        var state = await GetRuleStateAsync(rule, now, cancellationToken);
        if (fact is null)
        {
            await repository.UpsertNotificationRuleStateAsync(state with { UpdatedAt = now }, cancellationToken);
            return [];
        }

        var afterEventId = state.LastProcessedSubjectEventId ?? 0;
        var events = (await repository.GetSubjectPresenceEventsAfterIdAsync(rule.SubjectId, afterEventId, cancellationToken))
            .OrderBy(value => value.Id)
            .ToArray();
        if (events.Length == 0)
        {
            await repository.UpsertNotificationRuleStateAsync(
                await ResetInactiveRuleStateAsync(state, now, cancellationToken), cancellationToken);
            return [];
        }

        var requests = new List<NotificationRequest>();
        foreach (var detected in events)
        {
            // The watermark advances over every new subject event, including
            // initial or opposite-state events.  They are consumed history,
            // not a reason to keep scanning and replaying them forever.
            state = state with { LastProcessedSubjectEventId = detected.Id };
            if (!EventMatches(detected, expectedState)) continue;

            var episodeId = EventEpisodeId(detected.Id);
            var episodeDeliveries = (await repository.GetNotificationDeliveriesForEpisodeAsync(rule.Id, episodeId, cancellationToken)).ToList();
            var deliveries = new List<NotificationDelivery>();
            var targets = await ResolveTargetsAsync(rule, cancellationToken);
            foreach (var target in targets)
            {
                var delivery = episodeDeliveries.FirstOrDefault(value => MatchesTarget(value, target));
                if (delivery is null)
                {
                    // An event reminder is allowed to be created only while the
                    // confirmed subject projection still agrees with the event.
                    // This blocks a late evaluation of an old Online event after
                    // the subject has already returned to Offline.
                    if (fact.CurrentState != expectedState) continue;
                    var eventFact = FactForEvent(fact, detected, now);
                    var message = NotificationTemplateRenderer.Render(rule, eventFact, now, detected);
                    delivery = await CreateDeliveryAsync(rule, episodeId, now, target, message, cancellationToken);
                    await _diagnostics.RecordDeliveryCreatedAsync(rule, eventFact, detected, delivery, cancellationToken);
                }

                deliveries.Add(delivery);
                if (delivery.Status is not (NotificationDeliveryStatus.Delivered or NotificationDeliveryStatus.Canceled) && IsDue(delivery, now))
                    requests.Add(ToRequest(delivery));
            }
            state = ToRuleState(state, episodeId, detected.EffectiveAt, deliveries, now);
        }

        await repository.UpsertNotificationRuleStateAsync(state, cancellationToken);
        return requests;
    }

    private async Task<IReadOnlyList<NotificationRecipientTarget>> ResolveTargetsAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        var recipients = await repository.GetNotificationRuleRecipientsAsync(rule.Id, cancellationToken);
        if (recipients.Count > 0)
            return recipients.Select(value => new NotificationRecipientTarget(value.Id, value.TargetType, value.OpenId, value.DisplayName)).ToArray();

        if (rule.RecipientIds.Count > 0)
        {
            var resolved = new List<NotificationRecipientTarget>();
            foreach (var recipientId in rule.RecipientIds.Distinct())
                if (await repository.GetNotificationRecipientAsync(recipientId, cancellationToken) is { } recipient)
                    resolved.Add(new(recipient.Id, recipient.TargetType, recipient.OpenId, recipient.DisplayName));
            if (resolved.Count > 0) return resolved;
        }

        // Rules created by older versions have no relationship rows. Keep
        // their original target live until the migration or the next edit.
        return [new NotificationRecipientTarget(null, rule.TargetType, rule.TargetId)];
    }

    private async Task<NotificationDelivery> CreateDeliveryAsync(
        NotificationRule rule,
        string episodeId,
        DateTimeOffset now,
        NotificationRecipientTarget target,
        string message,
        CancellationToken cancellationToken) =>
        await repository.CreateNotificationDeliveryAsync(new NotificationDelivery(
            0, rule.Id, rule.SubjectId, episodeId, now, NotificationDeliveryStatus.Pending, null,
            rule.Channel, target.TargetType, target.TargetId, message, null, 0, 0, null, now, target.RecipientId), cancellationToken);

    private static bool MatchesTarget(NotificationDelivery delivery, NotificationRecipientTarget target) =>
        target.RecipientId is { } recipientId
            ? delivery.RecipientId == recipientId
            : delivery.RecipientId is null
              && delivery.TargetType == target.TargetType
              && string.Equals(delivery.TargetId, target.TargetId, StringComparison.Ordinal);

    private async Task<NotificationRuleState> GetRuleStateAsync(NotificationRule rule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await repository.GetNotificationRuleStateAsync(rule.Id, cancellationToken);
        if (current is null)
        {
            var watermark = await repository.GetLatestSubjectPresenceEventIdAsync(rule.SubjectId, cancellationToken) ?? 0;
            var created = new NotificationRuleState(rule.Id, null, null, false, null, false, null, null, now, watermark);
            await repository.UpsertNotificationRuleStateAsync(created, cancellationToken);
            return created;
        }

        if (current.LastProcessedSubjectEventId is null)
        {
            var watermark = await repository.GetLatestSubjectPresenceEventIdAsync(rule.SubjectId, cancellationToken) ?? 0;
            current = current with { LastProcessedSubjectEventId = watermark };
            await repository.UpsertNotificationRuleStateAsync(current, cancellationToken);
        }
        return current;
    }

    private async Task<NotificationRuleState> ResetInactiveRuleStateAsync(
        NotificationRuleState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // A failed delivery belongs to a durable queue, not to the transient
        // current fact.  Keep its diagnostics pointer while the dispatcher
        // retries it after a state change (or after an application restart).
        if (state.PendingDeliveryId is { } pendingId)
        {
            var delivery = await repository.GetNotificationDeliveryAsync(pendingId, cancellationToken);
            if (delivery is { Status: NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed })
            {
                return state with
                {
                    CurrentEpisodeId = null,
                    StateSince = null,
                    TriggeredForCurrentEpisode = false,
                    TriggeredAt = null,
                    PendingDelivery = true,
                    PendingDeliveryId = delivery.Id,
                    LastDeliveryError = delivery.Error,
                    UpdatedAt = now
                };
            }
        }

        return state with
        {
            CurrentEpisodeId = null,
            StateSince = null,
            TriggeredForCurrentEpisode = false,
            TriggeredAt = null,
            PendingDelivery = false,
            PendingDeliveryId = null,
            LastDeliveryError = null,
            UpdatedAt = now
        };
    }

    private static NotificationRuleState ToRuleState(
        NotificationRuleState current,
        string episodeId,
        DateTimeOffset stateSince,
        IReadOnlyList<NotificationDelivery> deliveries,
        DateTimeOffset now)
    {
        var pendingDelivery = deliveries.FirstOrDefault(value => value.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed);
        var delivered = deliveries.Count > 0 && deliveries.All(value => value.Status == NotificationDeliveryStatus.Delivered);
        return current with
        {
            CurrentEpisodeId = episodeId,
            StateSince = stateSince,
            // A newly-created delivery is not a completed trigger. Only QQ's
            // successful send result closes this episode.
            TriggeredForCurrentEpisode = delivered,
            TriggeredAt = delivered ? deliveries.Max(value => value.DeliveredAt ?? value.CreatedAt) : null,
            PendingDelivery = pendingDelivery is not null,
            PendingDeliveryId = pendingDelivery?.Id,
            LastDeliveryError = pendingDelivery?.Error,
            UpdatedAt = now
        };
    }

    private static SubjectPresenceFact FactForEvent(SubjectPresenceFact current, SubjectPresenceEvent detected, DateTimeOffset now)
    {
        var state = SubjectPresenceService.StateFor(detected);
        var since = detected.EffectiveAt;
        var detectedAfterGap = SubjectPresenceService.IsDetectedAfterGap(detected.EventType);
        return current with
        {
            CurrentState = state,
            StateSince = since,
            StateSinceKnown = true,
            ConfirmedDuration = now > since ? now - since : TimeSpan.Zero,
            // A normal confirmed transition ends the previous state at its
            // effective boundary. After a monitoring gap that boundary is
            // unknown, so retain the last known pre-gap boundary instead of
            // claiming the detection timestamp was the last online/offline
            // time.
            LastOnlineTime = state == PresenceState.Offline && !detectedAfterGap ? since : current.LastOnlineTime,
            LastOfflineTime = state == PresenceState.Online && !detectedAfterGap ? since : current.LastOfflineTime,
            NotificationStateSince = since
        };
    }

    private static bool EventMatches(SubjectPresenceEvent value, PresenceState state) => state switch
    {
        PresenceState.Online => value.EventType is SubjectPresenceEventType.ConfirmedOnline or SubjectPresenceEventType.DetectedOnlineAfterGap,
        PresenceState.Offline => value.EventType is SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.DetectedOfflineAfterGap,
        _ => false
    };

    private static bool IsDue(NotificationDelivery delivery, DateTimeOffset now) => delivery.NextAttemptAt is null || delivery.NextAttemptAt <= now;
    private static string StateEpisodeId(PresenceState state, DateTimeOffset stateSince) => $"state:{(int)state}:{stateSince.UtcTicks}";
    private static string LegacyStateEpisodeId(PresenceState state, DateTimeOffset stateSince) => $"{(int)state}:{stateSince.UtcTicks}";
    private static string EventEpisodeId(long eventId) => $"event:{eventId}";

    private static bool IsStateEpisode(string episodeId, PresenceState state)
    {
        var parts = episodeId.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var legacyState))
            return legacyState == (int)state && long.TryParse(parts[1], out _);
        return parts.Length == 3 && parts[0] == "state" && int.TryParse(parts[1], out var currentState)
            && currentState == (int)state && long.TryParse(parts[2], out _);
    }

    private static NotificationRequest ToRequest(NotificationDelivery delivery) => new(
        delivery.Id, delivery.RuleId!.Value, delivery.SubjectId!.Value, delivery.EpisodeId, delivery.Channel,
        delivery.TargetType, delivery.TargetId, delivery.Message, delivery.CreatedAt);
}

public static class NotificationTemplateRenderer
{
    public static string Render(NotificationRule rule, SubjectPresenceFact fact, DateTimeOffset now, SubjectPresenceEvent? detected = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = fact.Subject.DisplayName,
            ["state"] = PresenceDurationFormatter.StateText(fact.CurrentState),
            ["duration"] = FormatDuration(fact.ConfirmedDuration),
            ["stateSince"] = FormatTime(fact.StateSince),
            ["lastOnlineTime"] = FormatTime(fact.LastOnlineTime),
            ["lastOfflineTime"] = FormatTime(fact.LastOfflineTime),
            // For a persisted event reminder, "current" means the time the
            // subject event was actually confirmed, not a later retry/timer
            // evaluation time. Continuous rules still use evaluation time.
            ["currentTime"] = FormatTime(detected?.ObservedAt ?? now),
            ["detectedTime"] = FormatTime(detected?.ObservedAt),
            ["routerName"] = string.IsNullOrWhiteSpace(fact.RouterName) ? "未知" : fact.RouterName!
        };
        var template = string.IsNullOrWhiteSpace(rule.MessageTemplate)
            ? DefaultTemplate(rule.Condition, detected?.MonitoringGapId is not null)
            : rule.MessageTemplate;
        var builder = new StringBuilder(template);
        foreach (var (key, value) in values) builder.Replace("{" + key + "}", value);
        return builder.ToString().Trim();
    }

    public static string DefaultTemplate(NotificationCondition condition) => DefaultTemplate(condition, detectedAfterGap: false);

    private static string DefaultTemplate(NotificationCondition condition, bool detectedAfterGap) => condition switch
    {
        NotificationCondition.OnlineFor => "{name} 已经连续在线 {duration}。\n本次上线时间：{stateSince}",
        NotificationCondition.OfflineFor => "{name} 已经连续离线 {duration}。\n最后在线：{lastOnlineTime}",
        NotificationCondition.DetectedOnline when detectedAfterGap => "{name} 检测到已上线。\n检测时间：{currentTime}",
        NotificationCondition.DetectedOnline => "{name} 已上线。\n检测时间：{currentTime}\n路由器：{routerName}",
        NotificationCondition.DetectedOffline when detectedAfterGap => "{name} 检测到已离线。\n检测时间：{currentTime}\n最后在线：{lastOnlineTime}",
        NotificationCondition.DetectedOffline => "{name} 已离线。\n检测时间：{currentTime}\n最后在线：{lastOnlineTime}",
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "通知条件无效。")
    };

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return duration.Hours == 0 ? $"{(int)duration.TotalDays}天" : $"{(int)duration.TotalDays}天{duration.Hours}小时";
        if (duration.TotalHours >= 1) return duration.Minutes == 0 ? $"{(int)duration.TotalHours}小时" : $"{(int)duration.TotalHours}小时{duration.Minutes}分钟";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}分钟";
        return "少于1分钟";
    }

    public static string FormatTime(DateTimeOffset? value) => value is null ? "未知" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
