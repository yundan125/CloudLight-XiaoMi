# Third-party notices

## MiForge/migate

- Project: https://github.com/MiForge/migate
- Version: 1.1.10
- License: MIT
- Copyright: Copyright (c) 2026 offici5l
- Usage: Xiaomi official browser/QR login and `sid=xiaomiio` service-token acquisition.

CloudLight Presence calls migate through an isolated Python process and a current-user
named pipe. The adapter avoids migate's plaintext session cache; authentication material
is protected at rest with Windows DPAPI.

The Windows installer includes a private Python 3.14 runtime and the locked Python
packages listed in `installer/requirements-migate-runtime.txt`. Their license metadata is
retained inside the packaged Python distribution and installed `*.dist-info` directories.
This private runtime is located inside the application directory and does not modify the
system Python installation.
