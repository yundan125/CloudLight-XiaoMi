"""Thin in-memory MiForge/migate bridge; Xiaomi tokens are never persisted here."""
import io
import json
import sys
from migate.config import SERVICELOGIN_URL
from migate.login.browser_qr import handle_browser_qr
from migate.requester import get, session
from migate.service import get_service
import migate.service as migate_service
from rich.console import Console

def parse_xiaomi_json(text):
    return json.loads(text[11:] if text.startswith("&&&START&&&") else text)

def acquire_pass_token(sid):
    auth = {"sid": sid, "_json": True}
    meta = parse_xiaomi_json(get(SERVICELOGIN_URL, params=auth).text)
    for key in ("serviceParam", "qs", "callback", "_sign"):
        auth[key] = meta[key]
    handle_browser_qr(auth, "1")
    cookies = session.cookies.get_dict()
    required = ("deviceId", "passToken", "userId")
    missing = [key for key in required if not cookies.get(key)]
    if missing:
        raise RuntimeError("QR login completed without: " + ", ".join(missing))
    result = {key: cookies[key] for key in required}
    session.cookies.clear()
    return result

def acquire_service(auth, sid):
    original = migate_service.console
    migate_service.console = Console(file=io.StringIO())
    try:
        service = get_service(auth, {"sid": sid})
    finally:
        migate_service.console = original
    token = service.get("cookies", {}).get("serviceToken") if service else None
    security = service.get("servicedata", {}).get("ssecurity") if service else None
    if not token or not security:
        raise RuntimeError(f"migate returned incomplete service data for sid={sid}")
    return {"accountUserId": auth["userId"], "userId": service.get("cookies", {}).get("userId", auth["userId"]),
            "deviceId": auth["deviceId"], "passToken": auth["passToken"], "serviceToken": token,
            "ssecurity": security, "cUserId": service.get("cookies", {}).get("cUserId", service.get("servicedata", {}).get("cUserId"))}

def main():
    with open(sys.argv[1], "r+b", buffering=0) as pipe:
        try:
            request = json.loads(pipe.readline().decode("utf-8"))
            sid = request.get("sid", "xiaomiio")
            auth = acquire_pass_token(sid) if request.get("operation") == "login" else request["authCookies"]
            result = acquire_service(auth, sid)
            pipe.write((json.dumps({"ok": True, "result": result}) + "\n").encode("utf-8"))
            return 0
        except Exception as error:
            pipe.write((json.dumps({"ok": False, "error": str(error)}) + "\n").encode("utf-8"))
            return 1

if __name__ == "__main__":
    raise SystemExit(main())
