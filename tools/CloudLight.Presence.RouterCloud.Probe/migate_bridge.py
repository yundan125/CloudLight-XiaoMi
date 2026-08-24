"""Thin in-memory bridge to MiForge/migate (MIT); never persists Xiaomi tokens."""

import json
import io
import sys

from migate.config import SERVICELOGIN_URL
from migate.login.browser_qr import handle_browser_qr
from migate.requester import get, session
from migate.service import get_service
import migate.service as migate_service
from rich.console import Console


def parse_xiaomi_json(text: str) -> dict:
    return json.loads(text[11:] if text.startswith("&&&START&&&") else text)


def acquire_pass_token(sid: str) -> dict:
    # This small orchestration follows migate.get_passtoken but intentionally skips
    # its ~/.migatesession plaintext cache. QR handling remains migate's code.
    auth_data = {"sid": sid, "_json": True}
    response = get(SERVICELOGIN_URL, params=auth_data)
    login_meta = parse_xiaomi_json(response.text)
    for key in ("serviceParam", "qs", "callback", "_sign"):
        auth_data[key] = login_meta[key]

    handle_browser_qr(auth_data, "1")
    cookies = session.cookies.get_dict()
    required = ("deviceId", "passToken", "userId")
    missing = [key for key in required if not cookies.get(key)]
    if missing:
        raise RuntimeError("QR login completed without required session cookies: " + ", ".join(missing))
    result = {key: cookies[key] for key in required}
    session.cookies.clear()
    return result


def acquire_service(auth_cookies: dict, sid: str) -> dict:
    original_console = migate_service.console
    migate_service.console = Console(file=io.StringIO())
    try:
        service = get_service(auth_cookies, {"sid": sid})
    finally:
        migate_service.console = original_console
    if not service:
        raise RuntimeError(f"migate could not acquire service data for sid={sid}")
    service_token = service.get("cookies", {}).get("serviceToken")
    ssecurity = service.get("servicedata", {}).get("ssecurity")
    if not service_token or not ssecurity:
        raise RuntimeError(f"migate returned incomplete service data for sid={sid}")
    return {
        "accountUserId": auth_cookies["userId"],
        "userId": service.get("cookies", {}).get("userId", auth_cookies["userId"]),
        "deviceId": auth_cookies["deviceId"],
        "passToken": auth_cookies["passToken"],
        "serviceToken": service_token,
        "ssecurity": ssecurity,
        "cUserId": service.get("cookies", {}).get(
            "cUserId", service.get("servicedata", {}).get("cUserId")
        ),
    }


def main() -> int:
    if len(sys.argv) != 2:
        raise RuntimeError("Named pipe path is required")
    with open(sys.argv[1], "r+b", buffering=0) as pipe:
        try:
            request = json.loads(pipe.readline().decode("utf-8"))
            sid = request.get("sid", "xiaoqiang")
            operation = request.get("operation")
            if operation == "login":
                auth_cookies = acquire_pass_token(sid)
            elif operation == "service":
                auth_cookies = request["authCookies"]
            else:
                raise RuntimeError("Unsupported bridge operation")
            result = acquire_service(auth_cookies, sid)
            pipe.write((json.dumps({"ok": True, "result": result}) + "\n").encode("utf-8"))
            return 0
        except Exception as exception:
            pipe.write((json.dumps({"ok": False, "error": str(exception)}) + "\n").encode("utf-8"))
            print(f"Xiaomi login bridge failed: {exception}", file=sys.stderr)
            return 1


if __name__ == "__main__":
    raise SystemExit(main())
