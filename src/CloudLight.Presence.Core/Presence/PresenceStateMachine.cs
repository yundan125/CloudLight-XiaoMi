using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;
using CloudLight.Presence.Core.Services;

namespace CloudLight.Presence.Core.Presence;

public sealed class PresenceStateMachine(IPresenceRepository repository)
{
    public async Task ApplySnapshotAsync(
        long routerId,
        IReadOnlyList<ObservedNetworkDevice> observations,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        // Capture the subject state before applying the first successful
        // snapshot that closes a monitoring gap.  We write one observation at
        // the subject boundary after the complete router snapshot is applied,
        // never one event per MAC address.
        var gapAtObservation = await FindMonitoringGapAtObservationAsync(observedAt, cancellationToken);
        var subjectStatesBeforeGap = gapAtObservation is null
            ? new Dictionary<long, PresenceState>()
            : await CaptureOrReadGapSubjectBaselinesAsync(routerId, gapAtObservation, cancellationToken);
        var observedMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var observed in observations)
        {
            var mac = NormalizeMac(observed.MacAddress);
            if (!observedMacs.Add(mac)) continue;
            var existing = await repository.FindDeviceAsync(routerId, mac, cancellationToken);
            var nextState = observed.Online ? PresenceState.Online : PresenceState.Offline;
            if (existing is null)
            {
                var created = await repository.InsertDeviceAsync(new NetworkDevice(
                    0, routerId, mac, observed.Name, observed.OriginName, null, null,
                    observed.Ip, observed.ConnectionType, observed.Signal, nextState,
                    observedAt, observedAt, observedAt), cancellationToken);
                await repository.AddEventAsync(new PresenceEvent(
                    0, created.Id, PresenceEventType.InitialObservation, observedAt,
                    PresenceSource.Polling), cancellationToken);
                if (nextState == PresenceState.Online)
                {
                    await repository.AddSessionAsync(new PresenceSession(
                        0, created.Id, observedAt, null, StartKnown: false, EndKnown: false),
                        cancellationToken);
                }
                continue;
            }

            await ApplyKnownDeviceAsync(existing, observed, nextState, observedAt, cancellationToken);
        }

        // The router response is a complete snapshot. A previously known
        // client that is absent from it is therefore confirmed offline.
        foreach (var existing in await repository.GetDevicesAsync(routerId, cancellationToken))
        {
            if (observedMacs.Contains(existing.MacAddress)) continue;
            await ApplyKnownDeviceAsync(existing, null, PresenceState.Offline, observedAt, cancellationToken);
        }

        await UpdateSubjectCurrentStatesAsync(routerId, observedAt, gapAtObservation, subjectStatesBeforeGap, cancellationToken);
    }

    private async Task<MonitoringGap?> FindMonitoringGapAtObservationAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var from = observedAt == DateTimeOffset.MinValue ? observedAt : observedAt.AddTicks(-1);
        var to = observedAt == DateTimeOffset.MaxValue ? observedAt : observedAt.AddTicks(1);
        return (await repository.GetMonitoringGapsAsync(from, to, cancellationToken))
            .Where(value => value.StartedAt < observedAt && (value.EndedAt is null || value.EndedAt >= observedAt))
            .OrderByDescending(value => value.StartedAt)
            .FirstOrDefault();
    }

    private async Task<Dictionary<long, PresenceState>> CaptureOrReadGapSubjectBaselinesAsync(
        long routerId,
        MonitoringGap gap,
        CancellationToken cancellationToken)
    {
        // Persist every subject baseline before changing any member device.
        // If a later write in this complete router snapshot fails, a retry
        // compares against these same pre-gap values rather than against a
        // partially updated multi-MAC aggregate.
        var subjectIds = (await repository.GetDeviceSubjectMapAsync(routerId, cancellationToken))
            .Values
            .Distinct()
            .ToArray();
        var current = (await repository.GetSubjectCurrentStatesAsync(subjectIds, cancellationToken))
            .ToDictionary(value => value.SubjectId, value => value.CurrentState);
        var existing = (await repository.GetMonitoringGapSubjectBaselinesAsync(gap.Id, cancellationToken))
            .ToDictionary(value => value.SubjectId, value => value.State);
        foreach (var (subjectId, state) in current)
        {
            if (existing.ContainsKey(subjectId)) continue;
            await repository.AddMonitoringGapSubjectBaselineAsync(
                new MonitoringGapSubjectBaseline(gap.Id, subjectId, state), cancellationToken);
        }

        // Read after INSERT OR IGNORE so concurrent/manual retry callers use
        // the first persisted baseline, never a later partial observation.
        return (await repository.GetMonitoringGapSubjectBaselinesAsync(gap.Id, cancellationToken))
            .Where(value => current.ContainsKey(value.SubjectId))
            .ToDictionary(value => value.SubjectId, value => value.State);
    }

    private async Task UpdateSubjectCurrentStatesAsync(
        long routerId,
        DateTimeOffset observedAt,
        MonitoringGap? gapAtObservation,
        IReadOnlyDictionary<long, PresenceState> subjectStatesBeforeGap,
        CancellationToken cancellationToken)
    {
        var subjectIds = (await repository.GetDeviceSubjectMapAsync(routerId, cancellationToken))
            .Values
            .Distinct()
            .ToArray();
        foreach (var subjectId in subjectIds)
        {
            var members = await repository.GetSubjectDevicesAsync(subjectId, cancellationToken);
            if (members.Count == 0) continue;
            var currentState = SubjectPresenceService.Aggregate(members.Select(member => member.CurrentObservedState));
            if (currentState is not (PresenceState.Online or PresenceState.Offline)) continue;
            var previous = await EnsureConfirmedBaselineAsync(
                subjectId, members, currentState, observedAt, gapAtObservation,
                subjectStatesBeforeGap.GetValueOrDefault(subjectId), cancellationToken);

            if (gapAtObservation is not null && subjectStatesBeforeGap.TryGetValue(subjectId, out var stateBeforeGap))
            {
                await ApplyGapObservationAsync(subjectId, previous, stateBeforeGap, currentState, gapAtObservation, observedAt, cancellationToken);
                continue;
            }

            await ApplyContinuousObservationAsync(subjectId, previous, currentState, observedAt, cancellationToken);
        }
    }

    private async Task<SubjectCurrentState> EnsureConfirmedBaselineAsync(
        long subjectId,
        IReadOnlyList<NetworkDevice> members,
        PresenceState observedState,
        DateTimeOffset observedAt,
        MonitoringGap? gapAtObservation,
        PresenceState stateBeforeGap,
        CancellationToken cancellationToken)
    {
        var previous = await repository.GetSubjectCurrentStateAsync(subjectId, cancellationToken);
        var events = await repository.GetSubjectPresenceEventsAsync(subjectId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken);
        if (events.Any(IsConfirmedHistoryEvent))
        {
            if (previous is not null) return previous;

            // Imports or an interrupted older run can leave canonical history
            // behind without its current-state projection. Rebuild that
            // projection from the latest confirmed fact instead of falling
            // back to a new, incorrect "now" boundary.
            var latest = events
                .Where(IsConfirmedHistoryEvent)
                .OrderBy(value => value.EffectiveAt)
                .ThenBy(value => value.ObservedAt)
                .Last();
            var restored = new SubjectCurrentState(
                subjectId,
                SubjectPresenceService.StateFor(latest),
                latest.EffectiveAt,
                observedAt);
            await repository.UpsertSubjectCurrentStateAsync(restored, cancellationToken);
            return restored;
        }

        // A safe upgrade baseline comes from the old grace-normalized subject
        // timeline, never from one arbitrary MAC.  For the first snapshot that
        // closes a gap, inspect only the pre-gap portion if a prior confirmed
        // state exists so same-state gaps keep their original episode.
        var baselineEnd = gapAtObservation is not null && stateBeforeGap is (PresenceState.Online or PresenceState.Offline)
            ? gapAtObservation.StartedAt
            : observedAt;
        var legacy = new SubjectPresenceService(repository, new PresenceStatisticsService(repository));
        var from = members.Min(value => value.FirstSeenAt);
        var timeline = await legacy.GetLegacyTimelineAsync(subjectId, from, baselineEnd, cancellationToken);
        var expectedState = stateBeforeGap is (PresenceState.Online or PresenceState.Offline)
            ? stateBeforeGap
            : observedState;
        var segment = timeline.LastOrDefault(value => value.State == expectedState);
        var correctedLegacySince = segment is not null && await HasLegacyBoundaryEvidenceAsync(
            members, expectedState, segment.Start, cancellationToken)
            ? segment.Start
            : (DateTimeOffset?)null;
        var stateSince = correctedLegacySince
            ?? (previous?.CurrentState == expectedState ? (DateTimeOffset?)previous.StateSince : null)
            ?? observedAt;
        var baseline = new SubjectCurrentState(subjectId, expectedState, stateSince, observedAt);
        await repository.RecordSubjectStateAndEventAsync(baseline, new SubjectPresenceEvent(
            0,
            subjectId,
            expectedState == PresenceState.Online ? SubjectPresenceEventType.InitialOnline : SubjectPresenceEventType.InitialOffline,
            observedAt,
            null,
            stateSince), cancellationToken);
        return baseline;
    }

    private async Task<bool> HasLegacyBoundaryEvidenceAsync(
        IReadOnlyList<NetworkDevice> members,
        PresenceState state,
        DateTimeOffset boundary,
        CancellationToken cancellationToken)
    {
        foreach (var member in members)
        {
            if (state == PresenceState.Online)
            {
                if ((await repository.GetSessionsAsync(member.Id, cancellationToken)).Any(value => value.StartedAt == boundary))
                    return true;
                if ((await repository.GetEventsAsync(member.Id, cancellationToken)).Any(value => value.EventType == PresenceEventType.Online && value.ObservedAt == boundary))
                    return true;
            }
            else if ((await repository.GetEventsAsync(member.Id, cancellationToken)).Any(value => value.EventType == PresenceEventType.Offline && value.ObservedAt == boundary)
                || (await repository.GetSessionsAsync(member.Id, cancellationToken)).Any(value => value.EndedAt == boundary))
            {
                return true;
            }
        }
        return false;
    }

    private async Task ApplyGapObservationAsync(
        long subjectId,
        SubjectCurrentState previous,
        PresenceState stateBeforeGap,
        PresenceState observedState,
        MonitoringGap gap,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (stateBeforeGap == observedState)
        {
            await repository.UpsertSubjectCurrentStateAsync(previous with
            {
                CurrentState = stateBeforeGap,
                LastObservedAt = observedAt,
                PendingOfflineSince = null
            }, cancellationToken);
            return;
        }

        await repository.RecordSubjectStateAndEventAsync(new SubjectCurrentState(subjectId, observedState, observedAt, observedAt), new SubjectPresenceEvent(
            0,
            subjectId,
            observedState == PresenceState.Online ? SubjectPresenceEventType.DetectedOnlineAfterGap : SubjectPresenceEventType.DetectedOfflineAfterGap,
            observedAt,
            gap.Id,
            observedAt), cancellationToken);
    }

    private async Task ApplyContinuousObservationAsync(
        long subjectId,
        SubjectCurrentState previous,
        PresenceState observedState,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (previous.CurrentState == observedState)
        {
            await repository.UpsertSubjectCurrentStateAsync(previous with
            {
                LastObservedAt = observedAt,
                PendingOfflineSince = null
            }, cancellationToken);
            return;
        }

        if (observedState == PresenceState.Online)
        {
            await repository.RecordSubjectStateAndEventAsync(new SubjectCurrentState(subjectId, PresenceState.Online, observedAt, observedAt), new SubjectPresenceEvent(
                0, subjectId, SubjectPresenceEventType.ConfirmedOnline, observedAt, null, observedAt), cancellationToken);
            return;
        }

        var pendingSince = previous.PendingOfflineSince ?? observedAt;
        if (observedAt - pendingSince < SubjectPresenceService.DefaultOfflineGracePeriod)
        {
            await repository.UpsertSubjectCurrentStateAsync(previous with
            {
                LastObservedAt = observedAt,
                PendingOfflineSince = pendingSince
            }, cancellationToken);
            return;
        }

        await repository.RecordSubjectStateAndEventAsync(new SubjectCurrentState(subjectId, PresenceState.Offline, pendingSince, observedAt), new SubjectPresenceEvent(
            0, subjectId, SubjectPresenceEventType.ConfirmedOffline, observedAt, null, pendingSince), cancellationToken);
    }

    private static bool IsConfirmedHistoryEvent(SubjectPresenceEvent value) => value.EventType is
        SubjectPresenceEventType.InitialOnline or
        SubjectPresenceEventType.InitialOffline or
        SubjectPresenceEventType.ConfirmedOnline or
        SubjectPresenceEventType.ConfirmedOffline;

    private async Task ApplyKnownDeviceAsync(
        NetworkDevice existing,
        ObservedNetworkDevice? observed,
        PresenceState nextState,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var historicalState = existing.LastKnownHistoricalState ?? existing.CurrentState;
        var observedChanged = existing.CurrentObservedState != nextState;
        var historicalChanged = historicalState != nextState;
        var sessions = await repository.GetSessionsAsync(existing.Id, cancellationToken);
        var openSession = sessions.FirstOrDefault(value => value.EndedAt is null);
        var events = await repository.GetEventsAsync(existing.Id, cancellationToken);
        var lastEvidenceAt = existing.FirstSeenAt;
        if (existing.LastStateChangedAt is { } lastChangedAt) lastEvidenceAt = Max(lastEvidenceAt, lastChangedAt);
        if (existing.LastSeenAt is { } lastSeenAt) lastEvidenceAt = Max(lastEvidenceAt, lastSeenAt);
        if (events.Count > 0) lastEvidenceAt = Max(lastEvidenceAt, events.Max(value => value.ObservedAt));
        foreach (var session in sessions)
        {
            lastEvidenceAt = Max(lastEvidenceAt, session.StartedAt);
            if (session.EndKnown && session.EndedAt is { } endedAt) lastEvidenceAt = Max(lastEvidenceAt, endedAt);
        }
        var gaps = await repository.GetMonitoringGapsAsync(existing.FirstSeenAt, observedAt, cancellationToken);
        var crossedMonitoringGap = gaps.Any(gap => gap.StartedAt < observedAt && (gap.EndedAt ?? observedAt) > lastEvidenceAt);
        var sessionGapBoundary = openSession is null
            ? null
            : gaps.Where(gap => gap.StartedAt > openSession.StartedAt && gap.StartedAt < observedAt)
                .Select(gap => (DateTimeOffset?)gap.StartedAt)
                .OrderBy(value => value)
                .FirstOrDefault();
        var sessionStartedDuringGapBoundary = openSession is null
            ? null
            : gaps.Where(gap => gap.StartedAt <= openSession.StartedAt && (gap.EndedAt ?? observedAt) > openSession.StartedAt)
                .Select(gap => (DateTimeOffset?)gap.StartedAt)
                .OrderBy(value => value)
                .FirstOrDefault();
        var sessionBoundary = sessionGapBoundary ?? sessionStartedDuringGapBoundary;
        var replaceOpenSession = nextState == PresenceState.Online && crossedMonitoringGap && openSession is not null &&
            sessionBoundary is not null;
        var startUnconfirmedSession = nextState == PresenceState.Online &&
            (historicalState == PresenceState.Unknown || crossedMonitoringGap || (observedChanged && openSession is null));
        var updated = existing with
        {
            OriginalName = observed?.Name ?? existing.OriginalName,
            OriginName = observed?.OriginName ?? existing.OriginName,
            LastIp = observed?.Ip ?? existing.LastIp,
            ConnectionType = observed?.ConnectionType ?? existing.ConnectionType,
            Signal = observed?.Signal ?? existing.Signal,
            CurrentState = nextState,
            LastKnownHistoricalState = historicalChanged ? nextState : historicalState,
            // Every successful complete snapshot is evidence, including an
            // absent client which the snapshot confirms as offline.
            LastSeenAt = observedAt,
            // A transition first seen after a gap starts a new current-state
            // episode at this successful observation.  If the state matches
            // the pre-gap state, keep the original boundary instead.
            LastStateChangedAt = historicalChanged ? observedAt : existing.LastStateChangedAt
        };
        await repository.UpdateDeviceAsync(updated, cancellationToken);

        if (nextState == PresenceState.Offline && openSession is not null)
        {
            if (sessionBoundary is { } gapBoundary)
                await repository.CloseOpenSessionAtBoundaryAsync(existing.Id, gapBoundary, cancellationToken);
            else if (historicalState == PresenceState.Online && historicalChanged && !crossedMonitoringGap)
                await repository.CloseOpenSessionAsync(existing.Id, observedAt, cancellationToken);
            else
                await repository.CloseOpenSessionAtBoundaryAsync(existing.Id, observedAt, cancellationToken);
        }
        else if (replaceOpenSession)
        {
            await repository.CloseOpenSessionAtBoundaryAsync(existing.Id, sessionBoundary ?? observedAt, cancellationToken);
        }

        if (!observedChanged || !historicalChanged)
        {
            if (startUnconfirmedSession && (openSession is null || replaceOpenSession))
                await repository.AddSessionAsync(new PresenceSession(
                    0, existing.Id, observedAt, null, StartKnown: false, EndKnown: false),
                    cancellationToken);
            return;
        }

        if (historicalState == PresenceState.Unknown)
        {
            await repository.AddEventAsync(new PresenceEvent(
                0, existing.Id, PresenceEventType.InitialObservation, observedAt,
                PresenceSource.Polling), cancellationToken);
            if (nextState == PresenceState.Online && (openSession is null || replaceOpenSession))
                await repository.AddSessionAsync(new PresenceSession(
                    0, existing.Id, observedAt, null, StartKnown: false, EndKnown: false),
                    cancellationToken);
            return;
        }

        if (crossedMonitoringGap)
        {
            if (startUnconfirmedSession && (openSession is null || replaceOpenSession))
                await repository.AddSessionAsync(new PresenceSession(
                    0, existing.Id, observedAt, null, StartKnown: false, EndKnown: false),
                    cancellationToken);
            return;
        }

        if (nextState == PresenceState.Online)
        {
            await repository.AddEventAsync(new PresenceEvent(
                0, existing.Id, PresenceEventType.Online, observedAt,
                PresenceSource.Polling), cancellationToken);
            await repository.AddSessionAsync(new PresenceSession(
                0, existing.Id, observedAt, null, StartKnown: true, EndKnown: false),
                cancellationToken);
        }
        else
        {
            await repository.AddEventAsync(new PresenceEvent(
                0, existing.Id, PresenceEventType.Offline, observedAt,
                PresenceSource.Polling), cancellationToken);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    public static string NormalizeMac(string value)
    {
        var compact = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (compact.Length != 12)
        {
            throw new ArgumentException("A 12-digit MAC address is required.", nameof(value));
        }
        return string.Join(':', Enumerable.Range(0, 6).Select(index => compact.Substring(index * 2, 2)));
    }
}
