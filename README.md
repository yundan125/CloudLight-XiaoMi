# CloudLight Presence

Phase 2 contains the Windows `.NET 8` WPF MVP under `src/`. Its production data path is
Xiaomi official browser/QR login through MiForge/migate 1.1.10, DPAPI session recovery,
`sid=xiaomiio`, dynamic router discovery, and Xiaomi AppGateway
`/app/appgateway/third/miwifi/app/s/api/device_list` using the router device's `partner_id`.
Presence observations are stored in `%LocalAppData%\CloudLight Presence\presence.db`.

Phase 1B AX3000T Router Gateway findings are recorded in
[`docs/phase-1b-router-remote.md`](docs/phase-1b-router-remote.md).

CloudLight Presence is intended to be a Windows desktop application that records
the observed online/offline state of client devices connected to a router owned
by the signed-in user. Xiaomi Router is the first planned provider.

Phase 1 live-account validation is complete. OAuth, homes, devices, router
discovery, MIoT properties, and MIPS subscription were tested against the owner's
real AX3000T, but Xiaomi Cloud did not expose a usable router client list through
the tested paths. No mock device data, Presence history, database, or desktop UI
has been implemented. The repository currently contains only the Phase 1 probe. See
[`docs/phase-1-xiaomi-cloud.md`](docs/phase-1-xiaomi-cloud.md) for confirmed facts,
the live test result, the OAuth product constraint, and the command to retry.

The probe uses Xiaomi's OAuth page and never asks for a Xiaomi account password.
It does not persist access or refresh tokens. Live output can contain private
device identifiers and must not be published.
