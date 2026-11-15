from __future__ import annotations

import json
import math
import os
import threading
import time
from dataclasses import dataclass
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, build_opener, ProxyHandler, HTTPRedirectHandler


class MirrorError(RuntimeError):
    def __init__(self, code: str, message: str, status: int = 0):
        super().__init__(f"{code}: {message}")
        self.code, self.status = code, status


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise MirrorError("INVALID_RESPONSE", "The local API must not redirect.", code)


@dataclass(frozen=True)
class Screenshot:
    png: bytes
    geometry: dict[str, Any]


class Client:
    """No implicit device selection, transport startup, or input retry.

    Credentials default to IPHONEMIRROR_API_KEY and IPHONEMIRROR_API_URL.
    Only the loopback HTTP listener is accepted; proxies and redirects are disabled.
    """

    def __init__(self, key: str | None = None, address: str | None = None, timeout: float = 30):
        self.key = key or os.environ.get("IPHONEMIRROR_API_KEY", "")
        self.address = (address or os.environ.get("IPHONEMIRROR_API_URL", "http://127.0.0.1:17890")).rstrip("/")
        url = urlsplit(self.address)
        if (url.scheme != "http" or url.hostname not in ("127.0.0.1", "localhost") or
                url.username or url.password or url.path or url.query or url.fragment or not url.port):
            raise ValueError("Use http://127.0.0.1:PORT for the local API.")
        if len(self.key) != 64 or any(c not in "0123456789abcdefABCDEF" for c in self.key):
            raise ValueError("Set IPHONEMIRROR_API_KEY to the key copied from Developer tools.")
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be positive and finite")
        self.timeout = timeout
        self._http = build_opener(ProxyHandler({}), _NoRedirect())

    def request(self, method: str, path: str, body=None, *, session=None, binary=False, timeout=None):
        if not path.startswith("/") or ".." in path.split("/") or "?" in path or "#" in path:
            raise ValueError("Invalid API path")
        headers = {"X-API-Key": self.key, "Accept": "image/png" if binary else "application/json"}
        if session:
            headers["X-Control-Session"] = session
        data = None
        if body is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(body, ensure_ascii=False, allow_nan=False).encode("utf-8")
        req = Request(self.address + "/api/v1" + path, data=data, headers=headers, method=method)
        try:
            with self._http.open(req, timeout=timeout or self.timeout) as response:
                limit = 64 * 1024 * 1024 if binary else 4 * 1024 * 1024
                content = response.read(limit + 1)
                if len(content) > limit:
                    raise MirrorError("INVALID_RESPONSE", "API response exceeds the client limit.")
                if binary:
                    if response.headers.get_content_type() != "image/png" or not content.startswith(b"\x89PNG\r\n\x1a\n"):
                        raise MirrorError("INVALID_RESPONSE", "Expected a PNG screenshot.")
                    return content
                return json.loads(content) if content else None
        except HTTPError as error:
            try:
                detail = json.loads(error.read(131072))["error"]
                code, message = detail["code"], detail["message"]
            except (ValueError, KeyError, TypeError):
                code, message = "HTTP_ERROR", f"Local API returned HTTP {error.code}."
            raise MirrorError(code, message, error.code) from None
        except (URLError, TimeoutError, ConnectionError, OSError) as error:
            # Never include the request/headers or automatically replay an uncertain action.
            raise MirrorError("CONNECTION_FAILED", f"Local API connection failed ({type(error).__name__}); input may already have happened.") from None
        except (ValueError, UnicodeError):
            raise MirrorError("INVALID_RESPONSE", "The local API did not return valid JSON.") from None

    def devices(self) -> list[dict[str, Any]]:
        return self.request("GET", "/devices")["devices"]

    def device(self, device_id: str) -> Device:
        if not isinstance(device_id, str) or not device_id or len(device_id) > 512:
            raise ValueError("Pass an explicit opaque device id from devices().")
        return Device(self, device_id)


class Device:
    def __init__(self, client: Client, device_id: str):
        self.client, self.id = client, device_id
        self.path = "/devices/" + quote(device_id, safe="")
        self._token: str | None = None
        self._geometry: dict | None = None
        self._renew_at = 0.0
        self._seconds = 30
        self._lock = threading.RLock()

    def status(self):
        return self.client.request("GET", self.path + "/status")

    def acquire(self, seconds=30):
        if type(seconds) is not int or not 5 <= seconds <= 300:
            raise ValueError("Lease duration must be 5..300 seconds.")
        with self._lock:
            status = self.status()
            result = self.client.request("POST", self.path + "/control/acquire",
                                         {"durationSeconds": seconds}, session=self._token)
            self._token, self._seconds = result["sessionToken"], seconds
            self._geometry = status["geometry"]
            self._renew_at = time.monotonic() + seconds * 0.5
            return self

    def release(self):
        with self._lock:
            token, self._token = self._token, None
            self._geometry = None
            if token:
                self.client.request("POST", self.path + "/control/release", session=token)

    def __enter__(self):
        return self.acquire()

    def __exit__(self, exc_type, exc, tb):
        try:
            self.release()
        except MirrorError:
            if exc_type is None:
                raise

    def _owned(self):
        if not self._token:
            raise MirrorError("CONTROL_LOCKED", "Acquire this device before sending input.")
        if time.monotonic() >= self._renew_at:
            self.acquire(self._seconds)  # same token; a revoked lease is never silently replaced
        return self._token

    def screenshot(self) -> Screenshot:
        before = self.status()["geometry"]
        png = self.client.request("GET", self.path + "/screenshot", binary=True)
        after = self.status()["geometry"]
        if before != after:
            raise MirrorError("GEOMETRY_CHANGED", "The capture changed during screenshot; take a new screenshot.")
        return Screenshot(png, after)

    def _input(self, action: str, **body):
        with self._lock:
            token = self._owned()
            body["geometryVersion"] = self._geometry["version"]
            return self.client.request("POST", self.path + "/input/" + action, body, session=token)

    def _point(self, x, y):
        if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or not 0 <= v <= 1 for v in (x, y)):
            raise ValueError("Fractional coordinates must be finite and between 0 and 1.")
        geometry = self.status()["geometry"]
        if self._geometry is None or geometry != self._geometry:
            raise MirrorError("GEOMETRY_CHANGED", "Acquire control using the current screenshot geometry.")
        w, h = geometry["width"], geometry["height"]
        if not w or not h:
            raise MirrorError("SCREENSHOT_UNAVAILABLE", "Wait for a decoded frame.")
        return min(x * w, w - 1), min(y * h, h - 1)

    def tap(self, x, y):
        return self._input("tap", x=x, y=y)

    def tap_fraction(self, x, y):
        with self._lock:
            self._owned()
            px, py = self._point(x, y)
            return self.tap(px, py)

    def swipe_fraction(self, x1, y1, x2, y2, duration=.3):
        with self._lock:
            self._owned()
            start, end = self._point(x1, y1), self._point(x2, y2)
            return self._input("swipe", x1=start[0], y1=start[1], x2=end[0], y2=end[1], duration=duration)

    def key(self, key: str):
        return self._input("key", key=key)

    def text(self, text: str):
        return self._input("text", text=text)

    def apps(self):
        return self.client.request("GET", self.path + "/apps")

    def launch_app(self, bundle_id: str):
        with self._lock:
            return self.client.request("POST", self.path + "/apps/launch", {"bundleId": bundle_id}, session=self._owned())

    def start_coredevice(self, audio=True):
        """Explicit preparation; may mount DDI. Does not silently stop another capture."""
        return self.client.request("POST", self.path + "/coredevice/start", {"audio": bool(audio)}, timeout=max(420, self.client.timeout))

    def media_status(self):
        return self.client.request("GET", self.path + "/coredevice/status")

    def stop_coredevice(self):
        return self.client.request("POST", self.path + "/coredevice/stop")
