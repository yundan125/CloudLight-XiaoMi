# CloudLight XiaoMi 1.0.0

CloudLight XiaoMi is a Windows desktop application that records the observed
online/offline presence of devices connected to a Xiaomi router owned by the signed-in
user. It uses Xiaomi's official browser/QR login, Xiaomi AppGateway polling, Windows
DPAPI for authentication storage, and SQLite for local history.

## Windows application

- Modern WPF device-card dashboard and device notes
- 10-second cloud polling with tray/background operation
- 24-hour, 3-day, 7-day, and 30-day statistics and presence timelines
- Explicit Unknown periods for monitoring gaps
- Versioned `.clpresence` export/import without Xiaomi credentials
- Per-user startup registration and per-user installation

For compatibility with existing 1.0.0 data, user data stays under the legacy storage
path `%LocalAppData%\CloudLight Presence` and is not removed by the
installer or uninstaller. The 1.0.0 installer contains a private Python 3.14 runtime and
MiForge/migate 1.1.10; it does not install or modify system Python.

## Build

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet publish .\src\CloudLight.Presence.App\CloudLight.Presence.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -o .\artifacts\win-x64-1.0.0

.\installer\Prepare-MigateRuntime.ps1 `
  -PublishDirectory .\artifacts\win-x64-1.0.0

& 'C:\Program Files\Inno Setup 7\ISCC.exe' `
  .\installer\CloudLightXiaoMi.iss
```

The generated installer is
`artifacts\CloudLight-XiaoMi-Setup-1.0.0.exe`.

## Architecture notes

The confirmed Xiaomi path is:

```text
MiForge/migate official login
→ DPAPI-protected passToken
→ sid=xiaomiio
→ Xiaomi AppGateway
→ /s/api/device_list
→ Presence state machine
→ SQLite
```

Protocol validation and historical probes remain in `docs/` and `tools/`. They are not
required by the installed desktop application.
