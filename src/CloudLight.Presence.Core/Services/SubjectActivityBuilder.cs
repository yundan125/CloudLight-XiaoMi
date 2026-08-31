using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public static class SubjectActivityBuilder
{
    public static IReadOnlyList<SubjectActivityItem> Build(
        IReadOnlyList<PresenceTimelineSegment> timeline,
        bool includeUnknownPeriods,
        int limit = 15)
        => Build(timeline, includeUnknownPeriods, [], limit);

    public static IReadOnlyList<SubjectActivityItem> Build(
        IReadOnlyList<PresenceTimelineSegment> timeline,
        bool includeUnknownPeriods,
        IReadOnlyList<SubjectPresenceEvent> detectedAfterGap,
        int limit = 15)
    {
        if (limit <= 0 || (timeline.Count == 0 && detectedAfterGap.Count == 0)) return [];

        var normalized = includeUnknownPeriods
            ? CoalesceAdjacent(timeline)
            : CoalesceKnownStatesAcrossUnknownPeriods(timeline);

        // Subject transition records are authoritative whenever available.
        // Suppress the matching timeline boundary so an initial baseline never
        // appears as a fabricated "已上线/已离线" activity, and confirmed
        // transitions are represented exactly once.
        var eventBoundaries = detectedAfterGap.Select(value => value.EffectiveAt).ToHashSet();
        var activities = normalized
            .Where(segment => !eventBoundaries.Contains(segment.Start))
            .Select(segment => new SubjectActivityItem(segment.Start, ToActivityType(segment.State), segment.UnobservedReason))
            .Concat(detectedAfterGap.Select(ToActivityItem).Where(value => value is not null).Select(value => value!))
            .GroupBy(value => (value.OccurredAtUtc, value.Type, value.UnobservedReason))
            .Select(group => group.First())
            .OrderByDescending(value => value.OccurredAtUtc)
            .ThenByDescending(value => (int)value.Type)
            .Take(limit)
            .ToArray();

        return activities;
    }

    private static IReadOnlyList<PresenceTimelineSegment> CoalesceKnownStatesAcrossUnknownPeriods(
        IReadOnlyList<PresenceTimelineSegment> timeline)
        => Normalize(timeline, includeUnknownPeriods: false);

    private static IReadOnlyList<PresenceTimelineSegment> CoalesceAdjacent(
        IReadOnlyList<PresenceTimelineSegment> timeline)
        => Normalize(timeline, includeUnknownPeriods: true);

    private static IReadOnlyList<PresenceTimelineSegment> Normalize(
        IReadOnlyList<PresenceTimelineSegment> timeline,
        bool includeUnknownPeriods)
    {
        var result = new List<PresenceTimelineSegment>();
        var hasObservedKnownState = false;
        var firstKnownAfterGap = false;
        PresenceState? lastObservedKnownState = null;
        foreach (var segment in timeline)
        {
            if (segment.State == PresenceState.Unknown)
            {
                // Unknown before a device/subject's first known observation
                // is installation history, not a monitoring gap. Keep the
                // subsequent InitialObservation in Recent activity.
                if (hasObservedKnownState) firstKnownAfterGap = true;
                if (includeUnknownPeriods)
                {
                    if (result.Count > 0
                        && result[^1].State == PresenceState.Unknown
                        && result[^1].UnobservedReason == segment.UnobservedReason
                        && result[^1].End == segment.Start)
                        result[^1] = result[^1] with { End = segment.End };
                    else
                        result.Add(segment);
                }
                continue;
            }

            if (!hasObservedKnownState)
            {
                result.Add(segment);
                hasObservedKnownState = true;
                lastObservedKnownState = segment.State;
                continue;
            }

            // A known observation immediately following a real monitoring
            // gap cannot establish a transition time. Its optional detected
            // event is added separately. Keep its state as the new baseline
            // so a later actual state boundary is still shown even when it
            // happens to match the state from before the gap.
            if (firstKnownAfterGap)
            {
                firstKnownAfterGap = false;
                lastObservedKnownState = segment.State;
                continue;
            }

            if (lastObservedKnownState != segment.State)
                result.Add(segment);
            lastObservedKnownState = segment.State;
        }
        return result;
    }

    private static SubjectActivityType ToActivityType(PresenceState state) => state switch
    {
        PresenceState.Online => SubjectActivityType.Online,
        PresenceState.Offline => SubjectActivityType.Offline,
        _ => SubjectActivityType.UnknownPeriod
    };

    private static SubjectActivityItem? ToActivityItem(SubjectPresenceEvent value) => value.EventType switch
    {
        SubjectPresenceEventType.ConfirmedOnline => new SubjectActivityItem(value.EffectiveAt, SubjectActivityType.Online),
        SubjectPresenceEventType.ConfirmedOffline => new SubjectActivityItem(value.EffectiveAt, SubjectActivityType.Offline),
        SubjectPresenceEventType.DetectedOnlineAfterGap => new SubjectActivityItem(value.ObservedAt, SubjectActivityType.DetectedOnlineAfterGap),
        SubjectPresenceEventType.DetectedOfflineAfterGap => new SubjectActivityItem(value.ObservedAt, SubjectActivityType.DetectedOfflineAfterGap),
        _ => null
    };
}
