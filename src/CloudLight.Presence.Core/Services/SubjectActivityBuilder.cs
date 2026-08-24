using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public static class SubjectActivityBuilder
{
    public static IReadOnlyList<SubjectActivityItem> Build(
        IReadOnlyList<PresenceTimelineSegment> timeline,
        bool includeUnknownPeriods,
        int limit = 15)
    {
        if (limit <= 0 || timeline.Count == 0) return [];

        var normalized = includeUnknownPeriods
            ? CoalesceAdjacent(timeline)
            : CoalesceKnownStatesAcrossUnknownPeriods(timeline);

        return normalized
            .Select(segment => new SubjectActivityItem(segment.Start, ToActivityType(segment.State)))
            .Reverse()
            .Take(limit)
            .ToArray();
    }

    private static IReadOnlyList<PresenceTimelineSegment> CoalesceKnownStatesAcrossUnknownPeriods(
        IReadOnlyList<PresenceTimelineSegment> timeline)
    {
        var result = new List<PresenceTimelineSegment>();
        foreach (var segment in timeline.Where(value => value.State != PresenceState.Unknown))
        {
            if (result.Count > 0 && result[^1].State == segment.State)
                result[^1] = result[^1] with { End = segment.End };
            else
                result.Add(segment);
        }
        return result;
    }

    private static IReadOnlyList<PresenceTimelineSegment> CoalesceAdjacent(
        IReadOnlyList<PresenceTimelineSegment> timeline)
    {
        var result = new List<PresenceTimelineSegment>();
        foreach (var segment in timeline)
        {
            if (result.Count > 0 && result[^1].State == segment.State && result[^1].End == segment.Start)
                result[^1] = result[^1] with { End = segment.End };
            else
                result.Add(segment);
        }
        return result;
    }

    private static SubjectActivityType ToActivityType(PresenceState state) => state switch
    {
        PresenceState.Online => SubjectActivityType.Online,
        PresenceState.Offline => SubjectActivityType.Offline,
        _ => SubjectActivityType.UnknownPeriod
    };
}
