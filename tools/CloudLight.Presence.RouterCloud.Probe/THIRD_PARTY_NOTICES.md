# Third-party notices

## MiForge/migate

- Repository: https://github.com/MiForge/migate
- Version used by the Probe: 1.1.10
- License: MIT
- Copyright: Copyright (c) 2026 offici5l
- Usage: the Probe directly imports migate's QR/browser login and arbitrary-service
  token exchange. `migate_bridge.py` adapts the orchestration of
  `get_passtoken` to avoid migate's plaintext `~/.migatesession` cache; tokens
  are transferred to the .NET process through a current-user named pipe.

The full MIT license text is available in the upstream repository and permits
use, modification, distribution, and sublicensing provided the copyright and
permission notice are retained.
