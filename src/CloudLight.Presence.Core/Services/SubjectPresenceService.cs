using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

/// <summary>
/// Builds every subject-facing presence view from the persisted, confirmed
/// subject history. Raw MAC state remains evidence only; it never gets to
/// define a separate card, timeline, or notification boundary.
/// </summary>
public sealed class SubjectPresenceService(IPresenceRepository repository, IPresenceStatisticsService deviceStatistics) : ISubjectPresenceService
{
    public static readonly TimeSpan DefaultOfflineGracePeriod = TimeSpan.FromSeconds(30);
    public TimeSpan OfflineGracePeriod => DefaultOfflineGracePeriod;

    public async Task<SubjectPresenceSnapshot?> GetSnapshotAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fact = await GetCurrentFactAsync(subjectId, now, cancellationToken);
        return fact is null ? null : new(fact.Subject, fact.Members, fact.CurrentState, fact.StateSinceKnown ? fact.StateSince : null,
            fact.ActiveDevice, fact.StateSince, fact.LastOnlineTime, fact.LastOfflineTime, fact.RouterName);
    }

    public async Task<SubjectPresenceFact?> GetCurrentFactAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subject = await repository.GetSubjectAsync(subjectId, cancellationToken);
        if (subject is null) return null;

        var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
        var active = members.Where(value => value.CurrentObservedState == PresenceState.Online)
            .OrderByDescending(value => value.Signal ?? int.MinValue)
            .ThenByDescending(value => value.LastSeenAt)
            .FirstOrDefault();
        var routerName = await GetRouterNameAsync(members, cancellationToken);
        if (members.Count == 0)
            return new(subject, members, PresenceState.Unknown, null, false, TimeSpan.Zero, null, null, active, routerName);

        var gapFrom = now == DateTimeOffset.MinValue ? now : now.AddTicks(-1);
        var gapTo = now == DateTimeOffset.MaxValue ? now : now.AddTicks(1);
        if ((await GetMonitoringGapsForMembersAsync(members, gapFrom, gapTo, cancellationToken))
            .Any(value => value.StartedAt <= now && (value.EndedAt is null || value.EndedAt > now)))
            return new(subject, members, PresenceState.Unknown, null, false, TimeSpan.Zero, null, null, null, routerName);

        var observedState = Aggregate(members.Select(value => value.CurrentObservedState));
        // A new monitoring run deliberately clears per-MAC observations to
        // Unknown before its first successful router snapshot. Do not expose
        // a stale confirmed state (or evaluate a reminder) during that
        // interval. Once an Online observation exists it is reliable enough
        // to win immediately; only an all-offline observation needs the
        // persisted Online + pending-offline grace protection.
        if (observedState == PresenceState.Unknown)
            return new(subject, members, PresenceState.Unknown, null, false, TimeSpan.Zero, null, null, active, routerName);

        var persisted = await repository.GetSubjectCurrentStateAsync(subjectId, cancellationToken);
        var currentState = observedState == PresenceState.Online
            ? PresenceState.Online
            : persisted is { CurrentState: PresenceState.Online, PendingOfflineSince: not null }
                ? PresenceState.Online
                : PresenceState.Offline;
        if (currentState is not (PresenceState.Online or PresenceState.Offline))
            return new(subject, members, PresenceState.Unknown, null, false, TimeSpan.Zero, null, null, active, routerName);

        var from = members.Min(value => value.FirstSeenAt);
        var timeline = await GetTimelineAsync(subjectId, from, now, cancellationToken);
        var currentSegment = timeline.LastOrDefault(value => value.State == currentState && value.End == now);

        // Before the first post-upgrade snapshot writes a canonical baseline,
        // safely prefer the existing grace-normalized legacy boundary. The
        // state machine persists that same boundary on its first snapshot.
        var stateSince = persisted?.CurrentState == currentState
            ? persisted.StateSince
            : currentSegment?.Start;
        var stateSinceKnown = stateSince is not null;
        var duration = stateSince is { } since ? NonNegative(now - since) : TimeSpan.Zero;
        return new(subject, members, currentState, stateSince, stateSinceKnown, duration,
            LastBoundary(timeline, PresenceState.Online, timeline.Count),
            LastBoundary(timeline, PresenceState.Offline, timeline.Count),
            active, routerName,
            // Continuous rules deliberately use the exact confirmed state
            // boundary. Monitoring gaps that later reconcile to the same
            // state are continuous by product definition.
            stateSince);
    }

    public async Task<PresenceStatistics> GetSubjectStatisticsAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var timeline = await GetTimelineAsync(subjectId, from, to, cancellationToken);
        return new(from, to, Sum(timeline, PresenceState.Online), Sum(timeline, PresenceState.Offline), Sum(timeline, PresenceState.Unknown));
    }

    public async Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) return [];
        var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
        if (members.Count == 0) return [new(from, to, PresenceState.Unknown)];

        var events = await repository.GetSubjectPresenceEventsAsync(subjectId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken);
        var seeds = events.Where(IsConfirmedHistoryEvent).OrderBy(value => value.EffectiveAt).ThenBy(value => value.ObservedAt).ThenBy(value => value.Id).ToArray();
        if (seeds.Length == 0)
            return await GetLegacyTimelineAsync(subjectId, from, to, cancellationToken);

        var transitions = events.Where(IsTimelineEvent).OrderBy(value => value.EffectiveAt).ThenBy(value => value.ObservedAt).ThenBy(value => value.Id).ToArray();

        var canonicalStart = seeds[0].EffectiveAt;
        var routerIds = members.Select(value => value.RouterId).Distinct().ToArray();
        if (canonicalStart <= from)
            return await BuildConfirmedTimelineAsync(transitions, from, to, routerIds, cancellationToken);

        var prefixEnd = canonicalStart < to ? canonicalStart : to;
        var result = new List<PresenceTimelineSegment>();
        if (prefixEnd > from)
            result.AddRange(await GetLegacyTimelineAsync(subjectId, from, prefixEnd, cancellationToken));
        if (canonicalStart < to)
            result.AddRange(await BuildConfirmedTimelineAsync(transitions, canonicalStart, to, routerIds, cancellationToken));
        return Coalesce(result);
    }

    /// <summary>
    /// Compatibility path for pre-confirmation-history data only. It is also
    /// used once by the state machine to seed a conservative post-upgrade
    /// baseline, after which subject views stop applying this derived grace.
    /// </summary>
    internal async Task<IReadOnlyList<PresenceTimelineSegment>> GetLegacyTimelineAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) return [];
        var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
        if (members.Count == 0) return [new(from, to, PresenceState.Unknown)];

        var timelines = new List<IReadOnlyList<PresenceTimelineSegment>>(members.Count);
        foreach (var member in members)
            timelines.Add(await deviceStatistics.GetTimelineAsync(member.Id, from, to, cancellationToken));

        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        foreach (var segment in timelines.SelectMany(value => value))
        {
            boundaries.Add(segment.Start);
            boundaries.Add(segment.End);
        }

        var points = boundaries.ToArray();
        var raw = new List<PresenceTimelineSegment>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            Append(raw, start, end, Aggregate(timelines.Select(value => StateAt(value, start, end))));
        }

        ApplyLegacyOfflineGrace(raw, to);
        return Coalesce(raw);
    }

    private async Task<IReadOnlyList<PresenceTimelineSegment>> BuildConfirmedTimelineAsync(
        IReadOnlyList<SubjectPresenceEvent> confirmed,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<long> routerIds,
        CancellationToken cancellationToken)
    {
        var gaps = (await GetMonitoringGapsForRouterIdsAsync(routerIds, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken))
            .ToDictionary(value => value.Id);
        var events = confirmed.Where(value => value.EffectiveAt <= to).ToArray();
        var prior = events.LastOrDefault(value => value.EffectiveAt <= from);
        PresenceState? currentState = prior is null ? null : StateFor(prior);
        var cursor = from;
        var result = new List<PresenceTimelineSegment>();

        foreach (var transition in events.Where(value => value.EffectiveAt > from && value.EffectiveAt < to))
        {
            AppendIntervalBeforeTransition(result, currentState, cursor, transition, gaps);
            cursor = transition.EffectiveAt;
            currentState = StateFor(transition);
        }

        if (currentState is { } state)
            Append(result, cursor, to, state);
        else
            Append(result, cursor, to, PresenceState.Unknown, "暂无历史记录");
        return ApplyGapOverlay(result, gaps.Values, from, to);
    }

    private static IReadOnlyList<PresenceTimelineSegment> ApplyGapOverlay(
        IReadOnlyList<PresenceTimelineSegment> source,
        IEnumerable<MonitoringGap> gaps,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var relevantGaps = gaps
            .Select(value => (Gap: value, Start: Max(from, value.StartedAt), End: Min(to, value.EndedAt ?? to)))
            .Where(value => value.End > value.Start)
            .OrderBy(value => value.Start)
            .ToArray();
        if (relevantGaps.Length == 0) return Coalesce(source);

        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        foreach (var segment in source) { boundaries.Add(segment.Start); boundaries.Add(segment.End); }
        foreach (var gap in relevantGaps) { boundaries.Add(gap.Start); boundaries.Add(gap.End); }

        var points = boundaries.ToArray();
        var result = new List<PresenceTimelineSegment>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            var gap = relevantGaps.FirstOrDefault(value => value.Start < end && value.End > start).Gap;
            if (gap is not null)
                Append(result, start, end, PresenceState.Unknown, gap.Reason);
            else
            {
                var state = source.FirstOrDefault(value => value.Start < end && value.End > start);
                Append(result, start, end, state?.State ?? PresenceState.Unknown, state?.UnobservedReason);
            }
        }
        return result;
    }

    private static void AppendIntervalBeforeTransition(
        List<PresenceTimelineSegment> result,
        PresenceState? currentState,
        DateTimeOffset cursor,
        SubjectPresenceEvent transition,
        IReadOnlyDictionary<long, MonitoringGap> gaps)
    {
        var transitionAt = transition.EffectiveAt;
        if (transitionAt <= cursor) return;
        if (currentState is not { } state)
        {
            Append(result, cursor, transitionAt, PresenceState.Unknown, "暂无历史记录");
            return;
        }

        if (IsDetectedAfterGap(transition.EventType) && transition.MonitoringGapId is { } gapId && gaps.TryGetValue(gapId, out var gap))
        {
            var unknownStart = Max(cursor, gap.StartedAt);
            if (unknownStart > cursor) Append(result, cursor, unknownStart, state);
            if (transitionAt > unknownStart) Append(result, unknownStart, transitionAt, PresenceState.Unknown, gap.Reason);
            return;
        }

        Append(result, cursor, transitionAt, state);
    }

    private void ApplyLegacyOfflineGrace(List<PresenceTimelineSegment> raw, DateTimeOffset to)
    {
        // Old installs have only MAC-level history. This is intentionally a
        // one-time compatibility interpretation; new confirmed subject events
        // never pass through this second grace path.
        for (var index = 0; index < raw.Count; index++)
        {
            var segment = raw[index];
            if (segment.State != PresenceState.Offline || segment.End - segment.Start > OfflineGracePeriod) continue;
            var onlineBefore = index > 0 && raw[index - 1].State == PresenceState.Online;
            var onlineAfter = index + 1 < raw.Count && raw[index + 1].State == PresenceState.Online;
            var trailingNow = segment.End == to && onlineBefore;
            if (onlineBefore && (onlineAfter || trailingNow)) raw[index] = segment with { State = PresenceState.Online };
        }
    }

    private static bool IsConfirmedHistoryEvent(SubjectPresenceEvent value) => value.EventType is
        SubjectPresenceEventType.InitialOnline or
        SubjectPresenceEventType.InitialOffline or
        SubjectPresenceEventType.ConfirmedOnline or
        SubjectPresenceEventType.ConfirmedOffline;

    private static bool IsTimelineEvent(SubjectPresenceEvent value) => StateFor(value) is PresenceState.Online or PresenceState.Offline;

    public static bool IsDetectedAfterGap(SubjectPresenceEventType value) => value is
        SubjectPresenceEventType.DetectedOnlineAfterGap or SubjectPresenceEventType.DetectedOfflineAfterGap;

    internal static PresenceState StateFor(SubjectPresenceEvent value) => value.EventType switch
    {
        SubjectPresenceEventType.InitialOnline or SubjectPresenceEventType.ConfirmedOnline or SubjectPresenceEventType.DetectedOnlineAfterGap => PresenceState.Online,
        SubjectPresenceEventType.InitialOffline or SubjectPresenceEventType.ConfirmedOffline or SubjectPresenceEventType.DetectedOfflineAfterGap => PresenceState.Offline,
        _ => PresenceState.Unknown
    };

    private static PresenceState StateAt(IReadOnlyList<PresenceTimelineSegment> values, DateTimeOffset start, DateTimeOffset end) =>
        values.FirstOrDefault(value => value.Start < end && value.End > start)?.State ?? PresenceState.Unknown;

    public static PresenceState Aggregate(IEnumerable<PresenceState> states)
    {
        var values = states as PresenceState[] ?? states.ToArray();
        if (values.Contains(PresenceState.Online)) return PresenceState.Online;
        if (values.Contains(PresenceState.Unknown)) return PresenceState.Unknown;
        return PresenceState.Offline;
    }

    private static void Append(List<PresenceTimelineSegment> values, DateTimeOffset start, DateTimeOffset end, PresenceState state, string? unobservedReason = null)
    {
        if (end <= start) return;
        if (values.Count > 0 && values[^1].State == state && values[^1].UnobservedReason == unobservedReason && values[^1].End == start)
            values[^1] = values[^1] with { End = end };
        else
            values.Add(new PresenceTimelineSegment(start, end, state, unobservedReason));
    }

    private static IReadOnlyList<PresenceTimelineSegment> Coalesce(IEnumerable<PresenceTimelineSegment> source)
    {
        var result = new List<PresenceTimelineSegment>();
        foreach (var value in source) Append(result, value.Start, value.End, value.State, value.UnobservedReason);
        return result;
    }

    private static TimeSpan Sum(IEnumerable<PresenceTimelineSegment> values, PresenceState state) =>
        TimeSpan.FromTicks(values.Where(value => value.State == state).Sum(value => (value.End - value.Start).Ticks));

    private async Task<string?> GetRouterNameAsync(IReadOnlyList<NetworkDevice> members, CancellationToken cancellationToken)
    {
        var routerIds = members.Select(value => value.RouterId).Distinct().ToArray();
        if (routerIds.Length == 0) return null;
        var routers = await repository.GetRoutersAsync(cancellationToken);
        var names = routers.Where(value => routerIds.Contains(value.Id)).Select(value => value.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        return names.Length == 0 ? null : string.Join("、", names);
    }

    private async Task<IReadOnlyList<MonitoringGap>> GetMonitoringGapsForMembersAsync(
        IReadOnlyList<NetworkDevice> members,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken) =>
        await GetMonitoringGapsForRouterIdsAsync(members.Select(value => value.RouterId).Distinct().ToArray(), from, to, cancellationToken);

    private async Task<IReadOnlyList<MonitoringGap>> GetMonitoringGapsForRouterIdsAsync(
        IReadOnlyCollection<long> routerIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (routerIds.Count == 0)
            return await repository.GetMonitoringGapsAsync(from, to, cancellationToken);

        var result = new Dictionary<long, MonitoringGap>();
        foreach (var routerId in routerIds)
            foreach (var gap in await repository.GetMonitoringGapsAsync(from, to, cancellationToken, routerId))
                result[gap.Id] = gap;
        return result.Values.OrderBy(value => value.StartedAt).ToArray();
    }

    private static DateTimeOffset? LastBoundary(IReadOnlyList<PresenceTimelineSegment> timeline, PresenceState state, int exclusiveEnd)
    {
        for (var index = Math.Min(exclusiveEnd, timeline.Count) - 1; index >= 0; index--)
            if (timeline[index].State == state) return timeline[index].End;
        return null;
    }

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
