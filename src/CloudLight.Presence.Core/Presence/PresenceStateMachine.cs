using CloudLight.Presence.Core.Interfaces;
using CloudLight.Presence.Core.Models;

namespace CloudLight.Presence.Core.Presence;

public sealed class PresenceStateMachine(IPresenceRepository repository)
{
    public async Task ApplySnapshotAsync(
        long routerId,
        IReadOnlyList<ObservedNetworkDevice> observations,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
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
                    observedAt, observedAt, null), cancellationToken);
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
    }

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
        var updated = existing with
        {
            OriginalName = observed?.Name ?? existing.OriginalName,
            OriginName = observed?.OriginName ?? existing.OriginName,
            LastIp = observed?.Ip ?? existing.LastIp,
            ConnectionType = observed?.ConnectionType ?? existing.ConnectionType,
            Signal = observed?.Signal ?? existing.Signal,
            CurrentState = nextState,
            LastKnownHistoricalState = historicalChanged ? nextState : historicalState,
            LastSeenAt = observed is null ? existing.LastSeenAt : observedAt,
            LastStateChangedAt = historicalChanged ? observedAt : existing.LastStateChangedAt
        };
        await repository.UpdateDeviceAsync(updated, cancellationToken);

        if (!observedChanged || !historicalChanged) return;
        if (historicalState == PresenceState.Unknown)
        {
            await repository.AddEventAsync(new PresenceEvent(
                0, existing.Id, PresenceEventType.InitialObservation, observedAt,
                PresenceSource.Polling), cancellationToken);
            if (nextState == PresenceState.Online)
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
            await repository.CloseOpenSessionAsync(existing.Id, observedAt, cancellationToken);
            await repository.AddEventAsync(new PresenceEvent(
                0, existing.Id, PresenceEventType.Offline, observedAt,
                PresenceSource.Polling), cancellationToken);
        }
    }

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
