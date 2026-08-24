using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class SubjectPresenceService(IPresenceRepository repository, IPresenceStatisticsService deviceStatistics) : ISubjectPresenceService
{
    public TimeSpan OfflineGracePeriod { get; } = TimeSpan.FromSeconds(30);

    public async Task<SubjectPresenceSnapshot?> GetSnapshotAsync(long subjectId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subject = await repository.GetSubjectAsync(subjectId, cancellationToken);
        if (subject is null) return null;
        var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
        if (members.Count == 0) return new(subject, members, PresenceState.Unknown, null, null);
        var from = members.Min(value => value.FirstSeenAt);
        var timeline = await GetAggregateStateTimelineAsync(members, from, now, cancellationToken);
        var current = timeline.LastOrDefault();
        var active = members.Where(value => value.CurrentState == PresenceState.Online)
            .OrderByDescending(value => value.Signal ?? int.MinValue).ThenByDescending(value => value.LastSeenAt).FirstOrDefault();
        return new(subject, members, current?.State ?? PresenceState.Unknown, current?.Start, active);
    }

    private async Task<IReadOnlyList<PresenceTimelineSegment>> GetAggregateStateTimelineAsync(IReadOnlyList<NetworkDevice> members, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var sessions = new List<(NetworkDevice Device, IReadOnlyList<PresenceSession> Sessions)>(members.Count);
        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        foreach (var member in members)
        {
            var values = await repository.GetSessionsAsync(member.Id, cancellationToken);
            sessions.Add((member, values));
            boundaries.Add(Max(from, member.FirstSeenAt));
            foreach (var session in values)
            {
                var start = Max(from, session.StartedAt); var end = Min(to, session.EndedAt ?? to);
                if (end > start) { boundaries.Add(start); boundaries.Add(end); }
            }
        }

        var points = boundaries.ToArray();
        var raw = new List<PresenceTimelineSegment>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index]; var end = points[index + 1];
            var states = sessions.Select(value => start < value.Device.FirstSeenAt
                ? PresenceState.Unknown
                : value.Sessions.Any(session => session.StartedAt < end && (session.EndedAt ?? to) > start)
                    ? PresenceState.Online : PresenceState.Offline).ToArray();
            var state = states.Contains(PresenceState.Online) ? PresenceState.Online
                : states.All(value => value == PresenceState.Offline) ? PresenceState.Offline : PresenceState.Unknown;
            Append(raw, start, end, state);
        }
        ApplyOfflineGrace(raw, to);
        return Coalesce(raw);
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
            var state = states.Contains(PresenceState.Online) ? PresenceState.Online
                : states.All(value => value == PresenceState.Offline) ? PresenceState.Offline : PresenceState.Unknown;
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
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
