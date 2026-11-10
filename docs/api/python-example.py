"""Python 3 client. Lists devices by default; opt in explicitly to a sample tap."""
import json
import os
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen


class IPhoneMirror:
    def __init__(self, key, address="http://127.0.0.1:17890"):
        self.key = key
        self.address = address.rstrip("/")
        self.sessions = {}

    def request(self, method, path, body=None, device=None, binary=False):
        headers = {"X-API-Key": self.key}
        if device in self.sessions:
            headers["X-Control-Session"] = self.sessions[device]
        data = None
        if body is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        request = Request(self.address + "/api/v1" + path, data=data, headers=headers, method=method)
        try:
            with urlopen(request, timeout=30) as response:
                data = response.read()
                return data if binary else json.loads(data) if data else None
        except HTTPError as error:
            # No automatic retry: a timed-out input may already have happened.
            try:
                detail = json.loads(error.read())["error"]
                raise RuntimeError(f'{detail["code"]}: {detail["message"]}') from None
            except (ValueError, KeyError):
                raise RuntimeError(f"HTTP {error.code}") from None

    def get_devices(self):
        return self.request("GET", "/devices")["devices"]

    def status(self, device):
        return self.request("GET", f"/devices/{device}/status")

    def acquire(self, device, duration_seconds=30):
        result = self.request("POST", f"/devices/{device}/control/acquire",
                              {"durationSeconds": duration_seconds}, device)
        self.sessions[device] = result["sessionToken"]
        return result

    def release(self, device):
        try:
            return self.request("POST", f"/devices/{device}/control/release", device=device)
        finally:
            self.sessions.pop(device, None)

    def input(self, device, action, **body):
        return self.request("POST", f"/devices/{device}/input/{action}", body, device)

    def tap(self, device, x, y):
        return self.input(device, "tap", x=x, y=y)

    def long_press(self, device, x, y, duration):
        return self.input(device, "long-press", x=x, y=y, duration=duration)

    def swipe(self, device, x1, y1, x2, y2, duration):
        return self.input(device, "swipe", x1=x1, y1=y1, x2=x2, y2=y2, duration=duration)

    def type_text(self, device, text):
        return self.input(device, "text", text=text)

    def get_clipboard(self, device):
        return self.request("GET", f"/devices/{device}/clipboard")["text"]

    def set_clipboard(self, device, text):
        return self.request("PUT", f"/devices/{device}/clipboard", {"text": text}, device)

    def screenshot(self, device, filename="iphone.png"):
        data = self.request("GET", f"/devices/{device}/screenshot", binary=True)
        Path(filename).write_bytes(data)
        return filename


if __name__ == "__main__":
    client = IPhoneMirror(os.environ["IPHONEMIRROR_API_KEY"],
                         os.environ.get("IPHONEMIRROR_API_URL", "http://127.0.0.1:17890"))
    devices = client.get_devices()
    if not devices:
        raise SystemExit("Connect and configure a phone in iPhoneMirror first.")
    device = os.environ.get("IPHONEMIRROR_DEVICE_ID")
    if not device:
        print(json.dumps(devices, ensure_ascii=False, indent=2))
        print("Set IPHONEMIRROR_DEVICE_ID to an id above to capture that device.")
        raise SystemExit(0)
    if not any(item["id"] == device for item in devices):
        raise SystemExit("IPHONEMIRROR_DEVICE_ID does not match a connected source.")
    status = client.status(device)
    capabilities = status["capabilities"]
    if capabilities["screenshot"]:
        print(client.screenshot(device))
    if os.environ.get("IPHONEMIRROR_SEND_TAP") != "1":
        print("Set IPHONEMIRROR_SEND_TAP=1 to send one sample tap.")
        raise SystemExit(0)
    width, height = status["geometry"]["width"], status["geometry"]["height"]
    if not capabilities["tap"] or width <= 0 or height <= 0:
        raise SystemExit("Start capture and absolute touch control for this device first.")
    client.acquire(device)
    try:
        client.input(device, "tap", x=width * .5, y=height * .5,
                     geometryVersion=status["geometry"]["version"])
    finally:
        client.release(device)
