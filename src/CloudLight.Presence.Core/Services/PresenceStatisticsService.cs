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
        var unknown = (to - from) - online - offline;
        return new PresenceStatistics(from, to, online, offline, unknown < TimeSpan.Zero ? TimeSpan.Zero : unknown);
    }

    public async Task<IReadOnlyList<PresenceTimelineSegment>> GetTimelineAsync(long deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) return [];
        var sessions = await repository.GetSessionsAsync(deviceId, cancellationToken);
        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        var gaps = await repository.GetMonitoringGapsAsync(from, to, cancellationToken);
        var knownFrom = device is null ? to : Max(from, device.FirstSeenAt);
        var boundaries = new SortedSet<DateTimeOffset> { from, knownFrom, to };
        foreach (var session in sessions)
        {
            var start = Max(from, session.StartedAt);
            var end = Min(to, session.EndedAt ?? to);
            if (end > start) { boundaries.Add(start); boundaries.Add(end); }
        }
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
            var online = sessions.Any(session => session.StartedAt < end && (session.EndedAt ?? to) > start);
            var state = unknown ? PresenceState.Unknown : online ? PresenceState.Online : PresenceState.Offline;
            if (result.Count > 0 && result[^1].State == state && result[^1].End == start)
                result[^1] = result[^1] with { End = end };
            else result.Add(new PresenceTimelineSegment(start, end, state));
        }
        return result;
    }

    private static TimeSpan Sum(IEnumerable<PresenceTimelineSegment> values, PresenceState state) =>
        TimeSpan.FromTicks(values.Where(value => value.State == state).Sum(value => (value.End - value.Start).Ticks));
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
