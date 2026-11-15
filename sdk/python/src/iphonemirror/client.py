from __future__ import annotations

import json
import base64
from contextlib import contextmanager
import http.client
import math
import os
from pathlib import Path
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

    def actions(self):
        return self.request("GET", "/hub/actions")["actions"]

    def upload(self, path: str, source: Path, session: str):
        url = urlsplit(self.address)
        connection = http.client.HTTPConnection(url.hostname, url.port, timeout=max(420, self.timeout))
        try:
            with source.open("rb") as file:
                size = os.fstat(file.fileno()).st_size
                maximum = 512 * 1024 * 1024 if source.suffix.lower() == ".ipa" else 8 * 1024 * 1024
                if not 0 < size <= maximum:
                    raise ValueError("The package exceeds the upload size limit.")
                connection.putrequest("POST", "/api/v1" + path)
                for name, value in {"X-API-Key": self.key, "X-Control-Session": session,
                    "Content-Type": "application/octet-stream", "X-File-Name": "package" + source.suffix.lower(),
                    "Content-Length": str(size)}.items():
                    connection.putheader(name, value)
                connection.endheaders()
                remaining = size
                while remaining:
                    data = file.read(min(65536, remaining))
                    if not data:
                        raise MirrorError("FILE_CHANGED", "The package changed during upload.")
                    connection.send(data)
                    remaining -= len(data)
                response = connection.getresponse()
                data = response.read(4 * 1024 * 1024 + 1)
                if len(data) > 4 * 1024 * 1024:
                    raise MirrorError("INVALID_RESPONSE", "Oversized upload response.")
                result = json.loads(data)
                if response.status != 200:
                    error = result.get("error", {})
                    raise MirrorError(error.get("code", "HTTP_ERROR"), error.get("message", "Installation failed."), response.status)
                return result
        except (OSError, http.client.HTTPException) as error:
            raise MirrorError("CONNECTION_FAILED", f"Upload failed ({type(error).__name__}); do not retry automatically.") from None
        finally:
            connection.close()


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

    def prepare(self, enable_control=True):
        return self.client.request("POST", self.path + "/prepare", {"enableControl": bool(enable_control)},
                                   timeout=max(420, self.client.timeout))

    def open_window(self):
        return self.client.request("POST", self.path + "/window/open")

    @contextmanager
    def _keep_lease(self):
        token = self._owned()
        stop = threading.Event()
        errors = []
        def renew():
            while not stop.wait(min(30, self._seconds / 3)):
                try:
                    # Renewal is read-only with respect to the cached capture geometry.
                    self.client.request("POST", self.path + "/control/acquire",
                                        {"durationSeconds": self._seconds}, session=token)
                    self._renew_at = time.monotonic() + self._seconds * .5
                except Exception as error:
                    errors.append(error)
                    return
        worker = threading.Thread(target=renew, name="iPhoneMirror-lease", daemon=True)
        worker.start()
        try:
            yield token
            if errors:
                raise MirrorError("CONTROL_LOCKED", "Lease renewal failed during the operation; inspect its result before retrying.")
        finally:
            stop.set()
            worker.join(timeout=self.client.timeout + 1)

    def hub(self, operation: str, **arguments):
        """Invoke a published action. Mutations require an explicitly acquired lease."""
        if not isinstance(operation, str) or not operation.replace("_", "").isalnum():
            raise ValueError("Invalid Device Hub action")
        actions = self.client.actions()
        if operation not in actions:
            raise ValueError("Unknown or private Device Hub action")
        policy = actions[operation]
        path = self.path + "/hub/" + operation
        if policy["readOnly"]:
            return self.client.request("POST", path, arguments, timeout=max(self.client.timeout, policy["timeout"] + 10))
        with self._lock, self._keep_lease() as token:
            return self.client.request("POST", path, arguments, session=token,
                                       timeout=max(self.client.timeout, policy["timeout"] + 10))

    def device_info(self): return self.hub("device_info")
    def capabilities(self): return self.hub("capabilities")
    def tunnel_info(self): return self.hub("tunnel_info")
    def list_apps(self, include_hidden=False, include_internal=False, include_clips=False):
        return self.hub("apps", includeHidden=include_hidden, includeInternal=include_internal, includeClips=include_clips)
    def app_icon(self, bundle_id): return self.hub("get_app_icon", bundleId=bundle_id)
    def processes(self): return self.hub("list_processes")
    def terminate(self, pid, signal=15): return self.hub("terminate_app", pid=pid, signal=signal)
    def uninstall_app(self, bundle_id): return self.hub("uninstall_app", bundleId=bundle_id)
    def profiles(self, kind="all"): return self.hub("list_profiles", kind=kind)
    def remove_profile(self, kind, identifier): return self.hub("remove_profile", kind=kind, id=identifier)
    def settings(self): return self.hub("get_settings")
    def get_setting(self, key): return self.hub("get_setting", key=key)
    def set_setting(self, key, value): return self.hub("set_setting", key=key, value=value)
    def set_location(self, latitude, longitude): return self.hub("set_location", latitude=latitude, longitude=longitude)
    def clear_location(self): return self.hub("clear_location")
    def device_action(self, action): return self.hub("device_action", action=action)
    def press_button(self, button): return self.hub("press_button", button=button)
    def list_dir(self, path="", root="media", offset=0, limit=200, refresh=False):
        return self.hub("list_dir", root=root, path=path, offset=offset, limit=limit, refresh=refresh)
    def read_file(self, path, root="media", offset=0, length=192 * 1024):
        return self.hub("read_file", root=root, path=path, offset=offset, length=length)
    def start_logs(self): return self.hub("syslog_start")
    def poll_logs(self, stream_id, cursor=0, limit=200):
        return self.hub("syslog_poll", streamId=stream_id, cursor=cursor, limit=limit)
    def stop_logs(self, stream_id): return self.hub("syslog_stop", streamId=stream_id)
    def start_recording(self, format="h265"): return self.hub("record_start", format=format)
    def stop_recording(self): return self.hub("record_stop")

    def install_app(self, path):
        source = Path(path)
        if source.suffix.lower() != ".ipa":
            raise ValueError("Choose an .ipa file.")
        with self._lock, self._keep_lease() as token:
            return self.client.upload(self.path + "/apps/install", source, token)

    def install_profile(self, path):
        source = Path(path)
        if source.suffix.lower() not in (".mobileprovision", ".mobileconfig"):
            raise ValueError("Choose a .mobileprovision or .mobileconfig file.")
        with self._lock, self._keep_lease() as token:
            return self.client.upload(self.path + "/profiles/install", source, token)

    def download(self, remote_path, destination, root="media", max_bytes=1024**3):
        """Download in bounded chunks; refuse replacement and detect a changing source."""
        target = Path(destination)
        initial = None
        created = False
        offset = 0
        try:
            with target.open("xb") as file:
                created = True
                while True:
                    part = self.read_file(remote_path, root, offset)
                    identity = (part["size"], part.get("modified"))
                    if initial is None:
                        initial = identity
                    if identity != initial or part["offset"] != offset:
                        raise MirrorError("FILE_CHANGED", "The device file changed during download.")
                    if not 0 <= part["size"] <= max_bytes:
                        raise ValueError("File exceeds the download limit.")
                    data = base64.b64decode(part["data"], validate=True)
                    if len(data) > 192 * 1024 or offset + len(data) > part["size"]:
                        raise MirrorError("INVALID_RESPONSE", "Invalid file chunk.")
                    file.write(data)
                    offset += len(data)
                    if part["eof"]:
                        if offset != part["size"]:
                            raise MirrorError("FILE_CHANGED", "The file ended early.")
                        break
                    if not data:
                        raise MirrorError("INVALID_RESPONSE", "Download made no progress.")
            return {"path": str(target.resolve()), "bytes": offset}
        except Exception:
            # Never remove a pre-existing target when exclusive creation failed.
            if created:
                target.unlink(missing_ok=True)
            raise

    def export_crashes(self, destination, max_bytes=1024**3, max_files=5000):
        target = Path(destination)
        target.mkdir(parents=True, exist_ok=False)
        pending = [("", target, 0)]
        files, total, directories = 0, 0, 1
        while pending:
            remote, local, depth = pending.pop()
            if depth > 16:
                raise ValueError("Crash report nesting exceeds 16 directories.")
            cursor = 0
            while True:
                page = self.list_dir(remote, "crashes", cursor, refresh=(not remote and cursor == 0))
                for entry in page["entries"]:
                    name = entry["name"]
                    if (name in ("", ".", "..") or name.rstrip(" .") != name or
                            any(c in name for c in '/\\:\0<>|"?*') or
                            name.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}):
                        raise MirrorError("INVALID_RESPONSE", "Unsafe device file name.")
                    child = local / name
                    path = "/".join(p for p in (remote, name) if p)
                    if entry["path"] != path:
                        raise MirrorError("INVALID_RESPONSE", "Unexpected device path.")
                    if entry["type"] == "directory":
                        directories += 1
                        if directories > max_files:
                            raise ValueError("Crash report export exceeds its directory budget.")
                        child.mkdir(exist_ok=False)
                        pending.append((path, child, depth + 1))
                    elif entry["type"] == "file":
                        if files >= max_files or entry["size"] > max_bytes - total:
                            raise ValueError("Crash report export exceeds its file/byte budget.")
                        result = self.download(path, child, "crashes", max_bytes - total)
                        total += result["bytes"]
                        files += 1
                cursor = page["nextOffset"]
                if cursor is None:
                    break
        return {"path": str(target.resolve()), "files": files, "bytes": total}
