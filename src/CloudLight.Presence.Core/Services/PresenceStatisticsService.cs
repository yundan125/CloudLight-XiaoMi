using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Services;

public sealed class PresenceStatisticsService(IPresenceRepository repository) : IPresenceStatisticsService
{
    public async Task<PresenceStatistics> GetStatisticsAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var segments = await GetTimelineAsync(deviceId, from, to, cancellationToken);
        var online = Sum(segments, PresenceState.Online);
        var offline = Sum(segments, PresenceState.Offline);
        var unknown = Sum(segments, PresenceState.Unknown);
        return new PresenceStatistics(from, to, online, offline, unknown);
    }

    public async Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) return [];
        var sessions = await repository.GetSessionsAsync(deviceId, cancellationToken);
        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        var gaps = await repository.GetMonitoringGapsAsync(from, to, cancellationToken);
        var knownFrom = device is null ? to : Clamp(device.FirstSeenAt, from, to);
        var onlineIntervals = sessions
            .Select(session => ClipSession(session, gaps, from, to))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var boundaries = new SortedSet<DateTimeOffset> { from, knownFrom, to };
        foreach (var (start, end) in onlineIntervals) { boundaries.Add(start); boundaries.Add(end); }
        foreach (var gap in gaps)
        {
            var start = Max(from, gap.StartedAt);
            var end = Min(to, gap.EndedAt ?? to);
            if (end > start) { boundaries.Add(start); boundaries.Add(end); }
        }

        var points = boundaries.ToArray();
        var result = new List<PresenceTimelineSegment>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index]; var end = points[index + 1];
            var unknown = start < knownFrom || gaps.Any(gap => gap.StartedAt < end && (gap.EndedAt ?? to) > start);
            var online = onlineIntervals.Any(session => session.Start < end && session.End > start);
            var state = unknown ? PresenceState.Unknown : online ? PresenceState.Online : PresenceState.Offline;
            if (result.Count > 0 && result[^1].State == state && result[^1].End == start)
                result[^1] = result[^1] with { End = end };
            else result.Add(new PresenceTimelineSegment(start, end, state));
        }
        return result;
    }

    private static (DateTimeOffset Start, DateTimeOffset End)? ClipSession(
        PresenceSession session,
        IReadOnlyList<MonitoringGap> gaps,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var start = Max(from, session.StartedAt);
        var end = Min(to, session.EndedAt ?? to);
        if (end <= start) return null;

        // A session written by an older version may remain open across a gap.
        // It is valid only up to the first monitoring boundary after it began;
        // it must never resume after that boundary without a new observation.
        var startedDuringGap = gaps.Any(gap => gap.StartedAt <= session.StartedAt && (gap.EndedAt ?? to) > session.StartedAt);
        if (startedDuringGap) return null;

        var firstGapStart = gaps
            .Where(gap => gap.StartedAt > session.StartedAt && gap.StartedAt < end)
            .Select(gap => gap.StartedAt)
            .DefaultIfEmpty(end)
            .Min();
        end = Min(end, firstGapStart);
        return end > start ? (start, end) : null;
    }

    private static TimeSpan Sum(IEnumerable<PresenceTimelineSegment> values, PresenceState state) =>
        TimeSpan.FromTicks(values.Where(value => value.State == state).Sum(value => (value.End - value.Start).Ticks));
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset from, DateTimeOffset to) => Max(from, Min(value, to));
}
