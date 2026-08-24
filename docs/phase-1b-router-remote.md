# Phase 1B: AX3000T current Mi Home router gateway

Status: passed on 2026-08-24 for the current `xiaomi.router.rd03` Mi Home
plugin path. The direct legacy `api.miwifi.com/r/...` path remains rejected for
this account/device binding and is not the path used by the current main client
list screen.

## Official plugin

`POST https://api.io.mi.com/app/v2/plugin/fetch_plugin` returned:

- model: `xiaomi.router.rd03`
- plugin id: `1016239`
- plugin package id: `1033989`
- version: `5`
- SDK version: `10090`
- platform: Android RN
- package creation time: 2023-07-29
- official archive MD5: `bb884bd1ead6963d71fbcd1c1dc63f7f`

The extracted bundle is at `.research/rd03-plugin-v5/android/main.bundle`.
There are no `callRouterRemoteApiV13` or `callRouterRemoteApi` calls in this
bundle. The plugin uses `Service.callSmartHomeAPI`.

## Identifier resolution

The matching Mi Home device record contains:

- `did=865004247`
- `model=xiaomi.router.rd03`
- `partner_id=2f105e53-da3d-846e-a069-2546a05907b2`

The plugin consistently resolves its MiWiFi router identifier as:

```text
Device.deviceID starts with "miwifi."
  ? Device.deviceID without "miwifi."
  : Device.partnerId
```

RD03 takes the `partnerId` branch. Therefore its Router Gateway `routerID` is
the UUID-valued `partner_id`, not MIoT did `865004247`.

## Current client-list call

The main device screen polls every 10 seconds in the plugin. Its call is:

```text
Service.callSmartHomeAPI(
  "/appgateway/third/miwifi/app/s/api/device_list",
  {
    method: "GET",
    params: {
      routerID: Device.partnerId,
      locale: <router locale>,
      v: "2",
      refresh: "1"
    }
  }
)
```

On the wire, the host sends an RC4-encrypted Xiaomi Home envelope:

```text
POST https://api.io.mi.com/app/appgateway/third/miwifi/app/s/api/device_list
query/form fields: data, rc4_hash__, signature, ssecurity, _nonce
cookie fields: userId, serviceToken, yetAnotherServiceToken
```

Secret values are neither logged nor written outside the existing DPAPI store.
The response is RC4-decrypted with the signed nonce.

The call succeeded with five retained devices. The returned `devices` item
fields include `mac`, `name`, `originName`, `ip`, `online`, `onlineTime`,
`connectionType`, `signal`, `dSpeed`, `uSpeed`, `totalRX`, `totalTX`, plus rate,
Wi-Fi quality, policy, vendor, model, port and application fields.

The plugin also contains a real Router Remote caller for mesh/repeater device
queries:

```text
Service.callSmartHomeAPI(
  "/appgateway/third/miwifi/app/r/api/xqsystem/device_list",
  { method: "GET", params: { deviceId: <router/repeater partner id> } }
)
```

Calling it for the AX3000T partner id succeeded with `code=0` and a two-item
`list`. Item fields were `isap`, `parent`, `ip`, `port`, `hostname`, `mac`,
`origin_name`, `ptype`, `authority`, `company`, `push`, `name`, `times`, `type`,
`statistics`, `ctype`, and `online`. It is not the main client page's data
source.

No `/api/misystem/devicelist` string or caller exists in the current RD03
bundle. The current plugin still uses `/s/api/device_list` for its complete
client list.

## Direct legacy V13 diagnostic

Static MiWiFi client code shows the old remote modifier changing a local
`/api/...` path to `/r/api/...`, injecting the V13 router identifier as
`deviceId`, `deviceID`, and `routerID`, then RC4-encrypting parameters and
adding `rc4_hash__`, `signature`, and `_nonce`.

For this account, direct
`GET https://api.miwifi.com/r/api/xqsystem/device_list` with the confirmed
partner id returned HTTP 401 both before and after refreshing `sid=xiaoqiang`.
In the same runs, `/s/admin/deviceList` authenticated and decrypted normally as
`{"code":"0","deviceList":[]}`. The direct failure is therefore at the
`/r` router-binding/authorization layer, not Xiaomi login or token restoration.

## Live state transition

Using the current plugin path with a 15-second probe interval:

- connect: detected on poll 1 after 15.2 seconds; `online=false -> true`; IP,
  MAC, name, 5 GHz connection type, and signal were present;
- disconnect: detected on poll 1 after 15.2 seconds; the entry remained in the
  list with MAC/name/IP and changed to `online=false`.

This supports 15-30 second polling. Phase 1B is passed for the current Mi Home
Router Gateway route and can proceed to Phase 2. The direct legacy V13 route is
retained only as a diagnostic and must not replace the working appgateway path.

## Probe commands

```powershell
dotnet run --project tools/CloudLight.Presence.RouterCloud.Probe
dotnet run --project tools/CloudLight.Presence.RouterCloud.Probe -- --interactive-poll
dotnet run --project tools/CloudLight.Presence.RouterCloud.Probe -- --current-plugin-remote-device-list
dotnet run --project tools/CloudLight.Presence.RouterCloud.Probe -- --direct-v13
```
