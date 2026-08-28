using System.Text;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationRuleService(IPresenceRepository repository, ISubjectPresenceService presence) : INotificationRuleService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<NotificationRequest>> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var requests = new List<NotificationRequest>();
            var rules = await repository.GetNotificationRulesAsync(enabledOnly: true, cancellationToken);
            foreach (var rule in rules)
            {
                var fact = await presence.GetCurrentFactAsync(rule.SubjectId, now, cancellationToken);
                if (fact is null) continue;

                var state = await repository.GetNotificationRuleStateAsync(rule.Id, cancellationToken)
                    ?? new NotificationRuleState(rule.Id, null, null, false, null, false, null, null, now);
                var pending = state.PendingDeliveryId is { } pendingId
                    ? await repository.GetNotificationDeliveryAsync(pendingId, cancellationToken)
                    : null;
                if (pending is { Status: NotificationDeliveryStatus.Canceled or NotificationDeliveryStatus.Delivered }) pending = null;
                var notificationStateSince = fact.NotificationStateSince;
                var currentEpisodeId = notificationStateSince is not null && (fact.CurrentState is PresenceState.Online or PresenceState.Offline)
                    ? EpisodeId(fact.CurrentState, notificationStateSince.Value)
                    : null;
                var currentStateChanged = !string.Equals(state.CurrentEpisodeId, currentEpisodeId, StringComparison.Ordinal);

                if (currentStateChanged)
                {
                    state = state with
                    {
                        CurrentEpisodeId = currentEpisodeId,
                        StateSince = notificationStateSince,
                        TriggeredForCurrentEpisode = false,
                        TriggeredAt = null,
                        PendingDelivery = pending is { Status: not NotificationDeliveryStatus.Delivered },
                        PendingDeliveryId = pending is { Status: not NotificationDeliveryStatus.Delivered } ? pending.Id : null,
                        LastDeliveryError = pending is { Status: not NotificationDeliveryStatus.Delivered } ? pending.Error : null,
                        UpdatedAt = now
                    };
                }
                else if (pending is null or { Status: NotificationDeliveryStatus.Delivered })
                {
                    if (state.PendingDelivery || state.PendingDeliveryId is not null)
                        state = state with { PendingDelivery = false, PendingDeliveryId = null, LastDeliveryError = null, UpdatedAt = now };
                }
                var currentDelivery = currentEpisodeId is null
                    ? null
                    : await repository.GetNotificationDeliveryForEpisodeAsync(rule.Id, currentEpisodeId, cancellationToken);
                if (currentDelivery is { Status: NotificationDeliveryStatus.Canceled or NotificationDeliveryStatus.Delivered })
                    currentDelivery = currentDelivery.Status == NotificationDeliveryStatus.Delivered ? currentDelivery : null;
                if (pending is null && currentDelivery is { Status: not NotificationDeliveryStatus.Delivered })
                {
                    pending = currentDelivery;
                    state = state with
                    {
                        PendingDelivery = true,
                        PendingDeliveryId = currentDelivery.Id,
                        LastDeliveryError = currentDelivery.Error,
                        UpdatedAt = now
                    };
                }
                if (currentDelivery is { Status: NotificationDeliveryStatus.Delivered } && !state.TriggeredForCurrentEpisode)
                {
                    state = state with { TriggeredForCurrentEpisode = true, TriggeredAt = currentDelivery.DeliveredAt ?? currentDelivery.CreatedAt, UpdatedAt = now };
                }

                if (pending is { Status: not NotificationDeliveryStatus.Delivered } && IsDue(pending, now))
                    requests.Add(ToRequest(pending));

                var expectedState = rule.Condition == NotificationCondition.OnlineFor ? PresenceState.Online : PresenceState.Offline;
                var notificationDuration = notificationStateSince is { } safeSince
                    ? now > safeSince ? now - safeSince : TimeSpan.Zero
                    : TimeSpan.Zero;
                var thresholdReached = currentEpisodeId is not null &&
                    fact.CurrentState == expectedState && notificationDuration >= TimeSpan.FromSeconds(rule.ThresholdSeconds);
                if (pending is null && currentDelivery is null && thresholdReached && !state.TriggeredForCurrentEpisode)
                {
                    var message = NotificationTemplateRenderer.Render(rule, fact, now);
                    var delivery = await repository.CreateNotificationDeliveryAsync(new NotificationDelivery(
                        0, rule.Id, rule.SubjectId, currentEpisodeId!, now, NotificationDeliveryStatus.Pending, null,
                        rule.Channel, rule.TargetType, rule.TargetId, message, null, 0, 0, null, now), cancellationToken);
                    state = state with
                    {
                        CurrentEpisodeId = currentEpisodeId,
                        StateSince = notificationStateSince,
                        TriggeredForCurrentEpisode = true,
                        TriggeredAt = now,
                        PendingDelivery = true,
                        PendingDeliveryId = delivery.Id,
                        LastDeliveryError = null,
                        UpdatedAt = now
                    };
                    requests.Add(ToRequest(delivery));
                }

                await repository.UpsertNotificationRuleStateAsync(state with { UpdatedAt = now }, cancellationToken);
            }
            return requests;
        }
        finally { _gate.Release(); }
    }

    private static bool IsDue(NotificationDelivery delivery, DateTimeOffset now) => delivery.NextAttemptAt is null || delivery.NextAttemptAt <= now;

    private static string EpisodeId(PresenceState state, DateTimeOffset stateSince) => $"{(int)state}:{stateSince.UtcTicks}";

    private static NotificationRequest ToRequest(NotificationDelivery delivery) => new(
        delivery.Id, delivery.RuleId!.Value, delivery.SubjectId!.Value, delivery.EpisodeId, delivery.Channel,
        delivery.TargetType, delivery.TargetId, delivery.Message, delivery.CreatedAt);
}

public static class NotificationTemplateRenderer
{
    public static string Render(NotificationRule rule, SubjectPresenceFact fact, DateTimeOffset now)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = fact.Subject.DisplayName,
            ["state"] = PresenceDurationFormatter.StateText(fact.CurrentState),
            ["duration"] = FormatDuration(fact.ConfirmedDuration),
            ["stateSince"] = FormatTime(fact.StateSince),
            ["lastOnlineTime"] = FormatTime(fact.LastOnlineTime),
            ["lastOfflineTime"] = FormatTime(fact.LastOfflineTime),
            ["currentTime"] = FormatTime(now),
            ["routerName"] = string.IsNullOrWhiteSpace(fact.RouterName) ? "未知" : fact.RouterName!
        };
        var template = string.IsNullOrWhiteSpace(rule.MessageTemplate) ? DefaultTemplate(rule.Condition) : rule.MessageTemplate;
        var builder = new StringBuilder(template);
        foreach (var (key, value) in values) builder.Replace("{" + key + "}", value);
        return builder.ToString().Trim();
    }

    public static string DefaultTemplate(NotificationCondition condition) => condition switch
    {
        NotificationCondition.OfflineFor => "{name} 已经连续离线 {duration}。\n最后在线：{lastOnlineTime}",
        _ => "{name} 已经连续在线 {duration}。\n本次上线时间：{stateSince}"
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
