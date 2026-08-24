# Phase 1: Xiaomi Cloud validation

Status: live-account validation completed for the official HA HTTP and MIPS
paths. Neither path returned a usable router client list for the tested AX3000T.

## Live validation record (2026-08-24)

### OAuth and discovery

- The isolated Edge process still received HTTP 502 for the registered
  `homeassistant.local` callback through the local network environment.
- Replacing only the callback host with `127.0.0.1` succeeded.
- OAuth state validation and code exchange succeeded. No code or token was logged
  or persisted.
- Homes returned: 1 (`home_id=279001756619`).
- Devices returned: 2.
- The real router result was `name=zhenfeng`, `model=xiaomi.router.rd03`,
  `did=865004247`, `home_id=279001756619`, `room_id=279001756620`, and
  `spec=urn:miot-spec-v2:device:router:0000A036:xiaomi-rd03:1`.

### Router properties

The real `xiaomi-rd03:1` spec declares router service `siid=2` and
`connected-device-number` at `piid=3`. It does not declare
`connect-device-ids`, `device-connect`, or `device-disconnect`.

- `siid=2/piid=3`: code `-704030013`, no value returned.
- `siid=2/piid=20`: code `-704040003`, no value returned. This PIID was probed
  explicitly even though it is absent from the real spec.

No stable client ID, MAC, hostname, device name, IP address, band, or client
online state was obtained.

### MIPS event observation

- TLS and MQTT v5 connection to `cn-ha.mqtt.io.mi.com:8883` succeeded.
- OAuth token authentication and SUBACK for
  `device/865004247/up/event_occured/#` succeeded at QoS 2.
- A real client connected to and disconnected from the router around 16:40 local
  time.
- No MQTT PUBLISH was received during the operation or the following five-minute
  observation window.
- The TCP session remained established during the observation.

This proves that the subscription itself succeeded and that Xiaomi Cloud did not
deliver an event during this test. It does not prove that the cloud will never
deliver such an event under other firmware/account conditions.

Phase 1 stops here because neither event delivery nor the tested MIoT properties
provide the current router client list. CloudEvent cannot be selected, and the
proposed MIoT polling fallback is not available through these endpoints.

## Confirmed from official sources

- `ha_xiaomi_home` uses OAuth 2.0 authorization code flow and never receives the
  Xiaomi account password.
- Its cloud HTTP base host is `ha.api.io.mi.com` for Mainland China.
- Its MQTT broker is `cn-ha.mqtt.io.mi.com:8883` for Mainland China.
- Cloud event topics use `device/{did}/up/event_occured/{siid}/{eiid}`. The
  `event_occured` spelling is intentional and matches Xiaomi's implementation.
- The official MIoT spec for `xiaomi.router.rd03v2` declares router service 2,
  `connected-device-number` property 3, `connect-device-ids` property 20,
  `device-connect` event 1, and `device-disconnect` event 2.

These declarations do not establish that a real AX3000T is returned under that
model, that property 20 is readable for the account, or that either event is
delivered through cloud MIPS.

## OAuth product constraint

The OAuth client ID in `ha_xiaomi_home` is registered for the Home Assistant
integration. Xiaomi fixes its redirect base to `http://homeassistant.local:8123`.
This probe uses that public integration identity only to test technical
feasibility and must not become the production authentication identity of an
independent desktop product. A production release needs a Xiaomi-approved OAuth
client and redirect URI with access to the same HA cloud APIs.

## Run the probe

```powershell
dotnet run --project tools/CloudLight.Presence.Xiaomi.Probe -- --region cn
```

Complete authorization in Xiaomi's page. If the final
`homeassistant.local:8123` URL does not open, change only the host in the browser
address bar to `127.0.0.1`, preserving the path and query string. The tool keeps
tokens in memory only and prints the live home/device/router/property response.

Do not publish the output: it contains private device identifiers and may contain
network client identifiers.

The host replacement is a validation workaround documented by the official
project for non-default Home Assistant addresses. It is not a production desktop
OAuth design.
