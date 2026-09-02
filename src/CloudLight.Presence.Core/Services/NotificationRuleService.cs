using System.Text;
using System.Security.Cryptography;
using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class NotificationRuleService(
    IPresenceRepository repository,
    ISubjectPresenceService presence,
    INotificationDiagnostics? diagnostics = null,
    Func<string?>? currentBotAppIdProvider = null) : INotificationRuleService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly INotificationDiagnostics _diagnostics = diagnostics ?? NullNotificationDiagnostics.Instance;
    private readonly Func<string?>? _currentBotAppIdProvider = currentBotAppIdProvider;

    public async Task<RuleEvaluationDiagnostic> EvaluateDiagnosticAsync(
        long ruleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rule = await repository.GetNotificationRuleAsync(ruleId, cancellationToken)
                ?? throw new InvalidOperationException("通知规则不存在，可能已被删除。 ");
            return await EvaluateDiagnosticCoreAsync(rule, now, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RuleEvaluationDiagnostic> EvaluateDiagnosticCoreAsync(
        NotificationRule rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await ReadRuleStateAsync(rule, cancellationToken);
        var allDeliveries = await repository.GetNotificationDeliveriesForRuleAsync(rule.Id, cancellationToken);
        var targets = await ResolveTargetsAsync(rule, cancellationToken);
        var lastSentAt = allDeliveries
            .Where(value => value.Status == NotificationDeliveryStatus.Delivered)
            .Select(value => value.DeliveredAt ?? value.CreatedAt)
            .DefaultIfEmpty()
            .Max();
        var lastDeliveryAt = allDeliveries
            .Where(value => value.Status is not NotificationDeliveryStatus.Canceled)
            .Select(value => value.CreatedAt)
            .DefaultIfEmpty()
            .Max();
        var lastError = state.LastDeliveryError ?? allDeliveries
            .Where(value => value.Status is (NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.PermanentFailed) && !string.IsNullOrWhiteSpace(value.Error))
            .OrderByDescending(value => value.CreatedAt)
            .Select(value => value.Error)
            .FirstOrDefault();
        var metadata = new DiagnosticMetadata(
            state.UpdatedAt == default ? null : state.UpdatedAt,
            state.TriggeredAt ?? (lastDeliveryAt == default ? null : lastDeliveryAt),
            lastSentAt == default ? null : lastSentAt,
            lastError,
            DisplayTargets(targets));

        if (!rule.Enabled)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.Disabled, "规则已关闭。", metadata: metadata);
        if (targets.Any(value => value.BindingMissing))
        {
            var missingNames = targets.Where(value => value.BindingMissing)
                .Select(value => value.DisplayName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var missingText = missingNames.Length == 0
                ? "接收人尚未绑定当前 QQ Bot"
                : $"接收人“{string.Join("”、“", missingNames)}”尚未绑定当前 QQ Bot";
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.RecipientBindingMissing,
                $"当前规则不会发送\n{missingText}，需要重新绑定后，新 Episode 才会发送。", metadata: metadata);
        }
        if (targets.Count == 0 || targets.Any(value => string.IsNullOrWhiteSpace(value.TargetId)))
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.RecipientUnavailable, "没有可用的 QQ 接收人，规则不会触发。", metadata: metadata);

        return rule.Condition is NotificationCondition.OnlineFor or NotificationCondition.OfflineFor
            ? await DiagnoseContinuousAsync(rule, now, state, allDeliveries, targets, metadata, cancellationToken)
            : await DiagnoseDetectedAsync(rule, now, state, allDeliveries, targets, metadata, cancellationToken);
    }

    private async Task<RuleEvaluationDiagnostic> DiagnoseContinuousAsync(
        NotificationRule rule,
        DateTimeOffset now,
        NotificationRuleState state,
        IReadOnlyList<NotificationDelivery> allDeliveries,
        IReadOnlyList<NotificationRecipientTarget> targets,
        DiagnosticMetadata metadata,
        CancellationToken cancellationToken)
    {
        var expected = rule.Condition == NotificationCondition.OnlineFor ? PresenceState.Online : PresenceState.Offline;
        var evaluation = await ReadContinuousConditionAsync(rule, expected, now, state, cancellationToken);
        var fact = evaluation.Fact;
        if (fact is null)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.SubjectUnavailable, "当前主体不可用，暂时无法评估。", metadata: metadata);
        if (fact.CurrentState == PresenceState.Unknown)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.WaitingForState, "当前主体状态未知，等待下一次成功的 Presence 检查。", fact, metadata: metadata);
        if (!evaluation.StateMatches)
        {
            var current = PresenceDurationFormatter.StateText(fact.CurrentState);
            var expectedText = PresenceDurationFormatter.StateText(expected);
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.WaitingForState,
                $"当前不会触发\n原因：当前主体处于{current}状态，规则要求连续{expectedText}。", fact, metadata: metadata);
        }

        var since = evaluation.StateSince!.Value;
        var duration = evaluation.Duration;
        var threshold = evaluation.Threshold;
        var progress = evaluation.Progress;
        var deliveries = evaluation.EpisodeDeliveries;
        var status = DeliveryDiagnosticStatus(deliveries, targets);
        if (status is not null)
            return Diagnostic(rule, now, status.Value, DeliveryExplanation(status.Value, deliveries), fact, since, duration, progress, metadata, deliveries);
        if (evaluation.ThresholdReached)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.ThresholdReached,
                $"当前条件已满足。\n{fact.Subject.DisplayName} 当前{PresenceDurationFormatter.StateText(expected)} {NotificationTemplateRenderer.FormatDuration(duration)}，已达到 {NotificationTemplateRenderer.FormatDuration(threshold)} 阈值。",
                fact, since, duration, progress, metadata, deliveries);

        var remaining = threshold - duration;
        return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.AccumulatingDuration,
            $"{fact.Subject.DisplayName} 当前{PresenceDurationFormatter.StateText(expected)} {NotificationTemplateRenderer.FormatDuration(duration)}。\n触发阈值：{NotificationTemplateRenderer.FormatDuration(threshold)}。\n预计还需要约 {NotificationTemplateRenderer.FormatDuration(remaining)}。",
            fact, since, duration, progress, metadata, deliveries, remaining);
    }

    private async Task<RuleEvaluationDiagnostic> DiagnoseDetectedAsync(
        NotificationRule rule,
        DateTimeOffset now,
        NotificationRuleState state,
        IReadOnlyList<NotificationDelivery> allDeliveries,
        IReadOnlyList<NotificationRecipientTarget> targets,
        DiagnosticMetadata metadata,
        CancellationToken cancellationToken)
    {
        var evaluation = await ReadDetectedConditionAsync(rule, PresenceStateFor(rule.Condition), now, state, includeHistory: true, cancellationToken: cancellationToken);
        var fact = evaluation.Fact;
        if (fact is null)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.SubjectUnavailable, "当前主体不可用，暂时无法评估。", metadata: metadata);

        var expected = PresenceStateFor(rule.Condition);
        metadata = metadata with { LastEventAt = evaluation.LastEventAt };
        var candidates = evaluation.CandidateEvents;
        if (candidates.Count == 0)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.WaitingForNewEvent,
                $"正在监听新的“{PresenceDurationFormatter.StateText(expected)}”事件。\n最近事件：{NotificationTemplateRenderer.FormatTime(metadata.LastEventAt)}。",
                fact, metadata: metadata);

        var detected = candidates[0];
        var episodeId = EventEpisodeId(detected.Id);
        var deliveries = allDeliveries.Where(value => string.Equals(value.EpisodeId, episodeId, StringComparison.Ordinal)).ToArray();
        var deliveryStatus = DeliveryDiagnosticStatus(deliveries, targets);
        if (deliveryStatus is not null)
            return Diagnostic(rule, now, deliveryStatus.Value, DeliveryExplanation(deliveryStatus.Value, deliveries), fact, detected.EffectiveAt, NonNegative(now - detected.EffectiveAt), 1, metadata, deliveries);
        if (fact.CurrentState != expected)
            return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.WaitingForState,
                $"检测到新的事件，但主体当前已处于{PresenceDurationFormatter.StateText(fact.CurrentState)}，等待状态重新确认。",
                fact, detected.EffectiveAt, NonNegative(now - detected.EffectiveAt), 1, metadata, deliveries);
        return Diagnostic(rule, now, RuleEvaluationDiagnosticStatus.ThresholdReached,
            $"当前条件已满足。\n最近检查：{detected.ObservedAt.ToLocalTime():HH:mm:ss}。\n如果这是一个新的状态 Episode，规则将发送给：\n{string.Join("\n", targets.Select(value => $"• {value.DisplayName ?? NotificationSettingsText(value)}"))}",
            fact, detected.EffectiveAt, NonNegative(now - detected.EffectiveAt), 1, metadata with { LastEventAt = detected.ObservedAt }, deliveries);
    }

    private async Task<NotificationRuleState> ReadRuleStateAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        var state = await repository.GetNotificationRuleStateAsync(rule.Id, cancellationToken);
        if (state is not null) return state;
        var latest = await repository.GetLatestSubjectPresenceEventIdAsync(rule.SubjectId, cancellationToken) ?? 0;
        return new NotificationRuleState(rule.Id, null, null, false, null, false, null, null, DateTimeOffset.MinValue, latest);
    }

    private async Task<bool> IsUserPausedEventAsync(SubjectPresenceEvent value, CancellationToken cancellationToken)
    {
        if (value.MonitoringGapId is not { } gapId) return false;
        var gaps = await repository.GetMonitoringGapsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken);
        return gaps.FirstOrDefault(gap => gap.Id == gapId)?.Reason is "UserPaused" or "用户暂停监控";
    }

    private async Task<ContinuousConditionEvaluation> ReadContinuousConditionAsync(
        NotificationRule rule,
        PresenceState expectedState,
        DateTimeOffset now,
        NotificationRuleState state,
        CancellationToken cancellationToken)
    {
        var fact = await presence.GetCurrentFactAsync(rule.SubjectId, now, cancellationToken);
        var allDeliveries = await repository.GetNotificationDeliveriesForRuleAsync(rule.Id, cancellationToken);
        var targets = await ResolveTargetsAsync(rule, cancellationToken);
        var matches = fact is { CurrentState: var current } && current == expectedState && fact.StateSince is not null;
        if (!matches)
            return new(fact, state, allDeliveries, targets, expectedState, false, null, string.Empty, [], TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(0, rule.ThresholdSeconds)), 0, false);

        var stateSince = fact!.StateSince!.Value;
        var threshold = TimeSpan.FromSeconds(Math.Max(0, rule.ThresholdSeconds));
        var duration = NonNegative(now - stateSince);
        var episodeId = StateEpisodeId(expectedState, stateSince);
        var deliveries = FindEpisodeDeliveries(rule, episodeId, stateSince, now, allDeliveries, expectedState);
        var progress = threshold <= TimeSpan.Zero ? 1 : Math.Clamp(duration.TotalSeconds / threshold.TotalSeconds, 0, 1);
        return new(fact, state, allDeliveries, targets, expectedState, true, stateSince, episodeId, deliveries, duration, threshold, progress, duration >= threshold);
    }

    private async Task<DetectedConditionEvaluation> ReadDetectedConditionAsync(
        NotificationRule rule,
        PresenceState expectedState,
        DateTimeOffset now,
        NotificationRuleState state,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var fact = await presence.GetCurrentFactAsync(rule.SubjectId, now, cancellationToken);
        var afterEventId = state.LastProcessedSubjectEventId ?? 0;
        var newEvents = (await repository.GetSubjectPresenceEventsAfterIdAsync(rule.SubjectId, afterEventId, cancellationToken))
            .OrderBy(value => value.Id)
            .ToArray();
        var candidates = new List<SubjectPresenceEvent>();
        foreach (var value in newEvents.Where(value => EventMatches(value, expectedState)))
            if (!await IsUserPausedEventAsync(value, cancellationToken)) candidates.Add(value);

        DateTimeOffset? lastEventAt = null;
        if (includeHistory)
        {
            var history = await repository.GetSubjectPresenceEventsAsync(rule.SubjectId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken);
            lastEventAt = history.Where(value => EventMatches(value, expectedState))
                .Select(value => (DateTimeOffset?)value.ObservedAt)
                .DefaultIfEmpty()
                .Max();
        }
        return new(fact, state, newEvents, candidates, lastEventAt);
    }

    private static IReadOnlyList<NotificationDelivery> FindEpisodeDeliveries(
        NotificationRule rule,
        string episodeId,
        DateTimeOffset since,
        DateTimeOffset now,
        IReadOnlyList<NotificationDelivery> allDeliveries,
        PresenceState expected)
    {
        var result = allDeliveries.Where(value => string.Equals(value.EpisodeId, episodeId, StringComparison.Ordinal)).ToList();
        if (result.Count > 0) return result;
        var legacy = LegacyStateEpisodeId(expected, since);
        result = allDeliveries.Where(value => string.Equals(value.EpisodeId, legacy, StringComparison.Ordinal)).ToList();
        if (result.Count > 0) return result;
        return allDeliveries.Where(value => value.Status is not NotificationDeliveryStatus.Canceled && value.CreatedAt >= since && value.CreatedAt <= now && IsStateEpisode(value.EpisodeId, expected)).ToList();
    }

    private static RuleEvaluationDiagnosticStatus? DeliveryDiagnosticStatus(
        IReadOnlyList<NotificationDelivery> deliveries,
        IReadOnlyList<NotificationRecipientTarget> targets)
    {
        if (deliveries.Any(value => value.Status == NotificationDeliveryStatus.BindingRequired)) return RuleEvaluationDiagnosticStatus.RecipientBindingMissing;
        if (deliveries.Any(value => value.Status is NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.PermanentFailed)) return RuleEvaluationDiagnosticStatus.DeliveryFailed;
        if (deliveries.Any(value => value.Status == NotificationDeliveryStatus.Pending)) return RuleEvaluationDiagnosticStatus.PendingDelivery;
        if (targets.Count > 0 && targets.All(target => deliveries.Any(value => value.Status == NotificationDeliveryStatus.Delivered && MatchesTarget(value, target))))
            return RuleEvaluationDiagnosticStatus.AlreadyTriggeredForEpisode;
        return null;
    }

    private static string DeliveryExplanation(RuleEvaluationDiagnosticStatus status, IReadOnlyList<NotificationDelivery> deliveries) => status switch
    {
        RuleEvaluationDiagnosticStatus.RecipientBindingMissing => "当前 QQ Bot 尚未绑定此联系人；该 Episode 不会自动补发，完成绑定后新的 Episode 才会发送。",
        RuleEvaluationDiagnosticStatus.DeliveryFailed when deliveries.Any(value => value.Status == NotificationDeliveryStatus.PermanentFailed)
            => "规则条件已满足，但 QQ API 拒绝了接收目标请求；该投递不会自动重试，请检查错误码或重新绑定接收人。",
        RuleEvaluationDiagnosticStatus.DeliveryFailed => "规则条件已满足，但最近一次 QQ 投递失败，等待重试。",
        RuleEvaluationDiagnosticStatus.PendingDelivery => "规则条件已满足，QQ 投递正在等待发送。",
        RuleEvaluationDiagnosticStatus.AlreadyTriggeredForEpisode => "当前状态 Episode 已经触发过，等待状态变化后才会再次触发。",
        _ => ""
    };

    private static RuleEvaluationDiagnostic Diagnostic(
        NotificationRule rule,
        DateTimeOffset now,
        RuleEvaluationDiagnosticStatus status,
        string explanation,
        SubjectPresenceFact? fact = null,
        DateTimeOffset? since = null,
        TimeSpan duration = default,
        double progress = 0,
        DiagnosticMetadata? metadata = null,
        IReadOnlyList<NotificationDelivery>? deliveries = null,
        TimeSpan? remaining = null) => new(
        rule.Id, rule.SubjectId, rule.Condition, status, now, DiagnosticTitle(status), explanation,
        fact?.CurrentState ?? PresenceState.Unknown, since ?? fact?.StateSince, duration, remaining, progress,
        now, metadata?.LastTriggeredAt, metadata?.LastSentAt, metadata?.LastError,
        metadata?.LastEventAt, metadata?.Targets);

    private static string DiagnosticTitle(RuleEvaluationDiagnosticStatus status) => status switch
    {
        RuleEvaluationDiagnosticStatus.WaitingForState => "等待状态",
        RuleEvaluationDiagnosticStatus.AccumulatingDuration => "正在累计",
        RuleEvaluationDiagnosticStatus.ThresholdReached => "条件已满足",
        RuleEvaluationDiagnosticStatus.AlreadyTriggeredForEpisode => "本次 Episode 已触发",
        RuleEvaluationDiagnosticStatus.WaitingForNewEvent => "等待新事件",
        RuleEvaluationDiagnosticStatus.Disabled => "规则已关闭",
        RuleEvaluationDiagnosticStatus.SubjectUnavailable => "主体不可用",
        RuleEvaluationDiagnosticStatus.RecipientUnavailable => "接收人不可用",
        RuleEvaluationDiagnosticStatus.RecipientBindingMissing => "需要重新绑定接收人",
        RuleEvaluationDiagnosticStatus.PendingDelivery => "等待发送",
        RuleEvaluationDiagnosticStatus.DeliveryFailed => "发送失败",
        RuleEvaluationDiagnosticStatus.Delivered => "已发送",
        _ => "未知"
    };

    private static string NotificationSettingsText(NotificationRecipientTarget target) =>
        $"QQ {target.TargetType switch { NotificationTargetType.Group => "群聊", _ => "私聊" }} {MaskTarget(target.TargetId)}";

    private static string MaskTarget(string value) => value.Length <= 6 ? value : $"{value[..3]}****{value[^3..]}";
    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    private sealed record DiagnosticMetadata(DateTimeOffset? LastEvaluationAt, DateTimeOffset? LastTriggeredAt, DateTimeOffset? LastSentAt, string? LastError, IReadOnlyList<NotificationRecipientTarget> Targets, DateTimeOffset? LastEventAt = null);
    private sealed record ContinuousConditionEvaluation(
        SubjectPresenceFact? Fact,
        NotificationRuleState State,
        IReadOnlyList<NotificationDelivery> AllDeliveries,
        IReadOnlyList<NotificationRecipientTarget> Targets,
        PresenceState ExpectedState,
        bool StateMatches,
        DateTimeOffset? StateSince,
        string EpisodeId,
        IReadOnlyList<NotificationDelivery> EpisodeDeliveries,
        TimeSpan Duration,
        TimeSpan Threshold,
        double Progress,
        bool ThresholdReached);
    private sealed record DetectedConditionEvaluation(
        SubjectPresenceFact? Fact,
        NotificationRuleState State,
        IReadOnlyList<SubjectPresenceEvent> NewEvents,
        IReadOnlyList<SubjectPresenceEvent> CandidateEvents,
        DateTimeOffset? LastEventAt);

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
        var state = await GetRuleStateAsync(rule, now, cancellationToken);
        var evaluation = await ReadContinuousConditionAsync(rule, expectedState, now, state, cancellationToken);
        if (!evaluation.StateMatches)
        {
            await repository.UpsertNotificationRuleStateAsync(
                await ResetInactiveRuleStateAsync(state, now, cancellationToken), cancellationToken);
            return [];
        }

        var fact = evaluation.Fact!;
        var stateSince = evaluation.StateSince!.Value;
        var episodeId = evaluation.EpisodeId;
        var episodeDeliveries = evaluation.EpisodeDeliveries.ToList();
        var targets = evaluation.Targets;
        var deliveries = new List<NotificationDelivery>();
        if (evaluation.ThresholdReached)
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
            .Where(value => IsRetryableDelivery(value) && IsDue(value, now))
            .Select(ToRequest)
            .ToArray();
    }

    private async Task<IReadOnlyList<NotificationRequest>> EvaluateDetectedAsync(
        NotificationRule rule,
        PresenceState expectedState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await GetRuleStateAsync(rule, now, cancellationToken);
        var evaluation = await ReadDetectedConditionAsync(rule, expectedState, now, state, includeHistory: false, cancellationToken: cancellationToken);
        var fact = evaluation.Fact;
        if (fact is null)
        {
            await repository.UpsertNotificationRuleStateAsync(state with { UpdatedAt = now }, cancellationToken);
            return [];
        }

        var events = evaluation.NewEvents;
        if (events.Count == 0)
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
            if (!evaluation.CandidateEvents.Any(value => value.Id == detected.Id)) continue;

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
                if (IsRetryableDelivery(delivery) && IsDue(delivery, now))
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
            return await ResolveRecipientTargetsAsync(recipients, cancellationToken);

        if (rule.RecipientIds.Count > 0)
        {
            var resolved = new List<NotificationRecipientTarget>();
            foreach (var recipientId in rule.RecipientIds.Distinct())
                if (await repository.GetNotificationRecipientAsync(recipientId, cancellationToken) is { } recipient)
                    resolved.Add(await ResolveRecipientTargetAsync(recipient, cancellationToken));
            if (resolved.Count > 0) return resolved;
        }

        // Active application configuration is authoritative. A legacy rule
        // without a recipient relationship cannot safely borrow its old target
        // for a different Bot/AppID.
        if (_currentBotAppIdProvider is not null)
            return [];

        // Rules created by older versions have no relationship rows. Keep
        // their original target live until the migration or the next edit.
        return [new NotificationRecipientTarget(null, rule.TargetType, rule.TargetId)];
    }

    private async Task<IReadOnlyList<NotificationRecipientTarget>> ResolveRecipientTargetsAsync(
        IReadOnlyList<NotificationRecipient> recipients,
        CancellationToken cancellationToken)
    {
        var result = new List<NotificationRecipientTarget>(recipients.Count);
        foreach (var recipient in recipients)
            result.Add(await ResolveRecipientTargetAsync(recipient, cancellationToken));
        return result;
    }

    private async Task<NotificationRecipientTarget> ResolveRecipientTargetAsync(
        NotificationRecipient recipient,
        CancellationToken cancellationToken)
    {
        if (_currentBotAppIdProvider is { } provider)
        {
            var appId = provider()?.Trim();
            if (string.IsNullOrWhiteSpace(appId))
                return new(recipient.Id, recipient.TargetType, string.Empty, recipient.DisplayName, BindingMissing: true);

            var profile = await repository.GetQqBotProfileByAppIdAsync(appId, cancellationToken);
            if (profile is null)
                return new(recipient.Id, recipient.TargetType, string.Empty, recipient.DisplayName,
                    BotAppIdFingerprint: SafeFingerprint(appId), BindingMissing: true);

            var binding = await repository.GetNotificationRecipientBotBindingAsync(recipient.Id, profile.Id, cancellationToken);
            if (binding is null)
                return new(recipient.Id, recipient.TargetType, string.Empty, recipient.DisplayName, profile.Id,
                    BindingMissing: true, BotAppIdFingerprint: SafeFingerprint(profile.AppId));

            return new(recipient.Id, binding.TargetType, binding.OpenId, recipient.DisplayName, profile.Id, binding.Id,
                BotAppIdFingerprint: SafeFingerprint(profile.AppId), MaskedTargetId: MaskTarget(binding.OpenId));
        }

        // Compatibility for existing callers/tests that do not yet provide an
        // active Bot scope. Initialized legacy contacts have an explicit
        // LegacyUnknown binding; the direct parent value is the final fallback
        // only for pre-binding mock/fixture data.
        var legacyProfile = await repository.GetQqBotProfileByAppIdAsync(QqBotProfile.LegacyUnknownAppId, cancellationToken);
        if (legacyProfile is not null && await repository.GetNotificationRecipientBotBindingAsync(recipient.Id, legacyProfile.Id, cancellationToken) is { } legacyBinding)
            return new(recipient.Id, legacyBinding.TargetType, legacyBinding.OpenId, recipient.DisplayName, legacyProfile.Id, legacyBinding.Id,
                BotAppIdFingerprint: "legacy-unknown", MaskedTargetId: MaskTarget(legacyBinding.OpenId));
        if (!string.IsNullOrWhiteSpace(recipient.LegacyOpenId) && !recipient.LegacyOpenId.Contains('*', StringComparison.Ordinal))
            return new(recipient.Id, recipient.TargetType, recipient.LegacyOpenId, recipient.DisplayName,
                MaskedTargetId: MaskTarget(recipient.LegacyOpenId));
        return new(recipient.Id, recipient.TargetType, string.Empty, recipient.DisplayName, BindingMissing: true);
    }

    private async Task<NotificationDelivery> CreateDeliveryAsync(
        NotificationRule rule,
        string episodeId,
        DateTimeOffset now,
        NotificationRecipientTarget target,
        string message,
        CancellationToken cancellationToken) =>
        await repository.CreateNotificationDeliveryAsync(new NotificationDelivery(
            0, rule.Id, rule.SubjectId, episodeId, now,
            target.BindingMissing ? NotificationDeliveryStatus.BindingRequired : NotificationDeliveryStatus.Pending,
            null, rule.Channel, target.TargetType, target.TargetId, message,
            target.BindingMissing ? "当前 QQ Bot 尚未绑定此联系人。" : null, 0, 0, null,
            target.BindingMissing ? null : now, target.RecipientId, target.BotProfileId, target.BindingId), cancellationToken);

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

    private static PresenceState PresenceStateFor(NotificationCondition condition) => condition switch
    {
        NotificationCondition.DetectedOnline => PresenceState.Online,
        NotificationCondition.DetectedOffline => PresenceState.Offline,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "事件规则条件无效。")
    };

    private static bool IsRetryableDelivery(NotificationDelivery delivery) => delivery.Status is NotificationDeliveryStatus.Pending or NotificationDeliveryStatus.Failed;
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

    private static IReadOnlyList<NotificationRecipientTarget> DisplayTargets(IReadOnlyList<NotificationRecipientTarget> targets) =>
        targets.Select(value => value with
        {
            TargetId = value.BindingMissing
                ? string.Empty
                : value.MaskedTargetId ?? MaskTarget(value.TargetId),
            MaskedTargetId = value.BindingMissing ? null : value.MaskedTargetId ?? MaskTarget(value.TargetId)
        }).ToArray();

    private static string SafeFingerprint(string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"len:{value.Length};sha256:{hash[..12]}";
    }
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
