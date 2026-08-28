using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class SubjectPresenceService(IPresenceRepository repository, IPresenceStatisticsService deviceStatistics) : ISubjectPresenceService
{
    public TimeSpan OfflineGracePeriod { get; } = TimeSpan.FromSeconds(30);

    public async Task<SubjectPresenceSnapshot?> GetSnapshotAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fact = await GetCurrentFactAsync(subjectId, now, cancellationToken);
        return fact is null ? null : new(fact.Subject, fact.Members, fact.CurrentState, LegacyStateChangedAt(fact), fact.ActiveDevice,
            fact.StateSince, fact.LastOnlineTime, fact.LastOfflineTime, fact.RouterName);
    }

    public async Task<SubjectPresenceFact?> GetCurrentFactAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subject = await repository.GetSubjectAsync(subjectId, cancellationToken);
        if (subject is null) return null;
        var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
        var active = members.Where(value => value.CurrentObservedState == PresenceState.Online)
            .OrderByDescending(value => value.Signal ?? int.MinValue).ThenByDescending(value => value.LastSeenAt).FirstOrDefault();
        var routerName = await GetRouterNameAsync(members, cancellationToken);
        if (members.Count == 0)
            return new(subject, members, PresenceState.Unknown, null, false, TimeSpan.Zero, null, null, active, routerName);

        var currentState = Aggregate(members.Select(value => value.CurrentObservedState));
        var from = members.Min(value => value.FirstSeenAt);
        var timeline = await GetTimelineAsync(subjectId, from, now, cancellationToken);
        if (currentState == PresenceState.Unknown)
            return new(subject, members, PresenceState.Unknown, null, false, TimeSpan.Zero,
                LastBoundary(timeline, PresenceState.Online, timeline.Count),
                LastBoundary(timeline, PresenceState.Offline, timeline.Count), active, routerName);

        var historicalCurrent = timeline.LastOrDefault();
        var currentIndex = historicalCurrent is null ? 0 : timeline.Count - 1;
        var persistedCurrent = await repository.GetSubjectCurrentStateAsync(subjectId, cancellationToken);
        var stateSince = persistedCurrent?.CurrentState == currentState
            ? persistedCurrent.StateSince
            : historicalCurrent?.State == currentState
                ? await ResolveConfirmedStateSinceAsync(members, historicalCurrent, now, cancellationToken)
                : ResolveMemberFallbackStateSince(members, currentState);
        var stateSinceKnown = stateSince is not null;
        var duration = stateSince is { } confirmedSince ? NonNegative(now - confirmedSince) : TimeSpan.Zero;
        DateTimeOffset? notificationStateSince = stateSince is { } value
            ? await ResolveNotificationStateSinceAsync(persistedCurrent, value, now, cancellationToken)
            : null;
        return new(subject, members, currentState, stateSince, stateSinceKnown, duration,
            LastBoundary(timeline, PresenceState.Online, currentIndex),
            LastBoundary(timeline, PresenceState.Offline, currentIndex), active, routerName, notificationStateSince);
    }

    public async Task<PresenceStatistics> GetSubjectStatisticsAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var timeline = await GetTimelineAsync(subjectId, from, to, cancellationToken);
        var online = Sum(timeline, PresenceState.Online);
        var offline = Sum(timeline, PresenceState.Offline);
        var unknown = Sum(timeline, PresenceState.Unknown);
        return new(from, to, online, offline, unknown);
    }

    public async Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long subjectId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) return [];
        var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
        if (members.Count == 0) return [new(from, to, PresenceState.Unknown)];
        var timelines = new List<IReadOnlyList<PresenceTimelineSegment>>(members.Count);
        foreach (var member in members)
            timelines.Add(await deviceStatistics.GetTimelineAsync(member.Id, from, to, cancellationToken));

        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        foreach (var segment in timelines.SelectMany(value => value)) { boundaries.Add(segment.Start); boundaries.Add(segment.End); }
        var points = boundaries.ToArray();
        var raw = new List<PresenceTimelineSegment>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index]; var end = points[index + 1];
            var states = timelines.Select(value => StateAt(value, start, end)).ToArray();
            var state = Aggregate(states);
            Append(raw, start, end, state);
        }

        ApplyOfflineGrace(raw, to);
        return Coalesce(raw);
    }

    private void ApplyOfflineGrace(List<PresenceTimelineSegment> raw, DateTimeOffset to)
    {
        // A short all-offline interval adjacent to an online interval is a band-switch grace period.
        // It remains a derived online interval; MAC-level sessions/events are never changed.
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

    private static PresenceState StateAt(IReadOnlyList<PresenceTimelineSegment> values, DateTimeOffset start, DateTimeOffset end) =>
        values.FirstOrDefault(value => value.Start < end && value.End > start)?.State ?? PresenceState.Unknown;
    public static PresenceState Aggregate(IEnumerable<PresenceState> states)
    {
        var values = states as PresenceState[] ?? states.ToArray();
        if (values.Contains(PresenceState.Online)) return PresenceState.Online;
        if (values.Contains(PresenceState.Unknown)) return PresenceState.Unknown;
        return PresenceState.Offline;
    }
    private static void Append(List<PresenceTimelineSegment> values, DateTimeOffset start, DateTimeOffset end, PresenceState state)
    {
        if (values.Count > 0 && values[^1].State == state && values[^1].End == start) values[^1] = values[^1] with { End = end };
        else values.Add(new(start, end, state));
    }
    private static IReadOnlyList<PresenceTimelineSegment> Coalesce(IEnumerable<PresenceTimelineSegment> source)
    {
        var result = new List<PresenceTimelineSegment>();
        foreach (var value in source) Append(result, value.Start, value.End, value.State);
        return result;
    }
    private static TimeSpan Sum(IEnumerable<PresenceTimelineSegment> values, PresenceState state) =>
        TimeSpan.FromTicks(values.Where(value => value.State == state).Sum(value => (value.End - value.Start).Ticks));
    private async Task<DateTimeOffset?> ResolveConfirmedStateSinceAsync(IReadOnlyList<NetworkDevice> members, PresenceTimelineSegment current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = new List<DateTimeOffset>();
        foreach (var member in members)
        {
            var relevant = current.State switch
            {
                PresenceState.Online => member.CurrentObservedState == PresenceState.Online,
                PresenceState.Offline => member.CurrentObservedState == PresenceState.Offline,
                _ => false
            };
            if (!relevant) continue;

            if (member.LastStateChangedAt is { } lastChangedAt)
                candidates.Add(lastChangedAt);

            var sessions = await repository.GetSessionsAsync(member.Id, cancellationToken);
            foreach (var session in sessions)
            {
                if (current.State == PresenceState.Online && session.StartKnown && session.EndedAt is null)
                    candidates.Add(session.StartedAt);
                if (current.State == PresenceState.Offline && session.EndKnown && session.EndedAt is { } endedAt)
                    candidates.Add(endedAt);
            }

            var events = await repository.GetEventsAsync(member.Id, cancellationToken);
            foreach (var value in events)
            {
                if ((current.State == PresenceState.Online && value.EventType == PresenceEventType.Online) ||
                    (current.State == PresenceState.Offline && value.EventType == PresenceEventType.Offline))
                    candidates.Add(value.ObservedAt);
            }
        }

        if (candidates.Count == 0) return null;
        var valid = candidates
            .Where(value => value <= now)
            .Distinct()
            .ToArray();
        if (valid.Length == 0) return null;
        return current.State == PresenceState.Online ? valid.Min() : valid.Max();
    }

    private static DateTimeOffset? ResolveMemberFallbackStateSince(
        IReadOnlyList<NetworkDevice> members,
        PresenceState currentState)
    {
        var candidates = members
            .Where(member => member.CurrentObservedState == currentState)
            .Select(member => member.LastStateChangedAt ?? member.LastSeenAt)
            .ToArray();
        if (candidates.Length == 0) return null;
        return currentState == PresenceState.Online ? candidates.Min() : candidates.Max();
    }

    private async Task<DateTimeOffset?> ResolveNotificationStateSinceAsync(
        SubjectCurrentState? persistedCurrent,
        DateTimeOffset stateSince,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (persistedCurrent is null) return stateSince;
        var gaps = await repository.GetMonitoringGapsAsync(stateSince, now, cancellationToken);
        // Until a successful snapshot at or after the end of every gap, a
        // restored visual duration is not safe notification evidence.
        if (gaps.Any(gap => gap.StartedAt > stateSince &&
                            (gap.EndedAt is null || gap.EndedAt > persistedCurrent.LastObservedAt)))
            return null;
        var latestGapEnd = gaps
            .Where(gap => gap.StartedAt > stateSince && gap.EndedAt is { } endedAt && endedAt <= persistedCurrent.LastObservedAt)
            .Select(gap => gap.EndedAt!.Value)
            .DefaultIfEmpty(stateSince)
            .Max();
        return latestGapEnd > stateSince ? latestGapEnd : stateSince;
    }

    private async Task<string?> GetRouterNameAsync(IReadOnlyList<NetworkDevice> members, CancellationToken cancellationToken)
    {
        var routerIds = members.Select(value => value.RouterId).Distinct().ToArray();
        if (routerIds.Length == 0) return null;
        var routers = await repository.GetRoutersAsync(cancellationToken);
        var names = routers.Where(value => routerIds.Contains(value.Id)).Select(value => value.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        return names.Length == 0 ? null : string.Join("、", names);
    }

    private static DateTimeOffset? LastBoundary(IReadOnlyList<PresenceTimelineSegment> timeline, PresenceState state, int exclusiveEnd)
    {
        for (var index = Math.Min(exclusiveEnd, timeline.Count) - 1; index >= 0; index--)
            if (timeline[index].State == state) return timeline[index].End;
        return null;
    }

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
    private static DateTimeOffset? LegacyStateChangedAt(SubjectPresenceFact fact)
    {
        return fact.StateSinceKnown ? fact.StateSince : null;
    }
}
