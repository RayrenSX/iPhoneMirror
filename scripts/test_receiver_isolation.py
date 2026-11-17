"""Exercise real native receiver lifetimes; run with the desktop app closed."""
import argparse
import ctypes
import os
from pathlib import Path
import plistlib
import socket
import subprocess
import time
import urllib.request


def request(port, route, method="GET", payload=b"", expected=200):
    with socket.create_connection(("127.0.0.1", port), 3) as client:
        client.settimeout(3)
        client.sendall((f"{method} {route} HTTP/1.1\r\nHost: localhost\r\n"
                        f"Content-Length: {len(payload)}\r\n\r\n").encode() + payload)
        data = b""
        while b"\r\n\r\n" not in data:
            chunk = client.recv(4096)
            assert chunk, "receiver closed before response headers"
            data += chunk
        headers, body = data.split(b"\r\n\r\n", 1)
        assert headers.startswith(f"HTTP/1.1 {expected} ".encode()), headers.splitlines()[0]
        length = int(next((line.split(b":", 1)[1] for line in headers.splitlines()
                          if line.lower().startswith(b"content-length:")), b"0"))
        while len(body) < length:
            chunk = client.recv(4096)
            assert chunk, "receiver closed before response body"
            body += chunk
        return plistlib.loads(body[:length]) if length else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("delivery", type=Path)
    parser.add_argument("--control-smoke", type=Path)
    parser.add_argument("--fcup-smoke", type=Path)
    args = parser.parse_args()
    # Parent-exit monitoring may take a moment to release a previous host.
    # Never accidentally test an older receiver still listening on these ports.
    deadline = time.monotonic() + 5
    while True:
        occupied = []
        for port in (5001, 5011, 7001, 7011, 8090):
            with socket.socket() as probe:
                probe.settimeout(0.1)
                if probe.connect_ex(("127.0.0.1", port)) == 0:
                    occupied.append(port)
        if not occupied:
            break
        if time.monotonic() >= deadline:
            raise AssertionError(f"Close existing receivers before testing: {occupied}")
        time.sleep(0.1)
    root = args.delivery.resolve()
    directory = os.add_dll_directory(str(root))
    core = ctypes.CDLL(str(root / "iPhoneMirror.Core.dll"))
    class MediaRequest(ctypes.Structure):
        _fields_ = [("struct_size", ctypes.c_uint32), ("api_version", ctypes.c_uint32),
                    ("command_id", ctypes.c_uint64), ("command", ctypes.c_uint32),
                    ("reserved", ctypes.c_uint32), ("duration", ctypes.c_double),
                    ("start_position", ctypes.c_double), ("volume", ctypes.c_double),
                    ("url", ctypes.c_wchar * 16384)]
    core.im_media_cast_get_request.argtypes = [ctypes.POINTER(MediaRequest)]
    host = str(root / "Wireless/iPhoneMirror.WirelessHost.exe")
    for name in ("im_media_cast_receiver_start", "im_wireless_receiver_start"):
        getattr(core, name).argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p]
    for name in ("im_media_cast_receiver_get_status", "im_wireless_receiver_get_status"):
        getattr(core, name).argtypes = [ctypes.POINTER(ctypes.c_int)] * 2

    def ready(prefix):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            running, state = ctypes.c_int(), ctypes.c_int()
            assert getattr(core, f"im_{prefix}_receiver_get_status")(
                ctypes.byref(running), ctypes.byref(state)) == 0
            if running.value and state.value == 1:
                return
            time.sleep(0.1)
        raise AssertionError(f"{prefix} receiver did not become ready")

    assert core.im_initialize() == 0
    try:
        assert core.im_media_cast_receiver_start("Isolation Video", host) == 0
        ready("media_cast")
        media = request(7011, "/server-info")
        media_info = request(5011, "/info")
        assert media["features"] & 0x84 == 0, hex(media["features"])
        assert media_info["features"] & ((1 << 42) | 0x80) == 0
        assert "displays" not in media_info
        request(7011, "/fp-setup2", "POST", bytes(120), expected=421)
        request(5011, "/fp-setup2", "POST", bytes(120), expected=421)
        request(5011, "/stream", "SETUP", plistlib.dumps({"streams": [{"type": 110}]},
                fmt=plistlib.FMT_BINARY), expected=403)
        assert core.im_wireless_receiver_start("Isolation Mirror", host) == 0
        ready("wireless")
        mirror = request(5001, "/info")
        assert mirror["features"] & 0x11 == 0
        assert mirror["deviceID"].lower() != media["deviceid"].lower()
        if args.control_smoke:
            subprocess.run([str(args.control_smoke.resolve())], check=True, timeout=20)
            deadline = time.monotonic() + 3
            received = 0
            while time.monotonic() < deadline:
                command = MediaRequest(struct_size=ctypes.sizeof(MediaRequest))
                assert core.im_media_cast_get_request(ctypes.byref(command)) == 0
                if command.command_id:
                    assert command.url == "http://127.0.0.1:9999/control-smoke.mp4"
                    received += 1
                    if received == 2:
                        break
                time.sleep(0.02)
            else:
                raise AssertionError("encrypted play did not reach media queue")
            assert request(5001, "/info")["deviceID"] == mirror["deviceID"]
        request(7011, "/reverse", "POST", expected=101)
        if args.fcup_smoke:
            phone = subprocess.Popen([str(args.fcup_smoke.resolve())])
            try:
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    command = MediaRequest(struct_size=ctypes.sizeof(MediaRequest))
                    assert core.im_media_cast_get_request(ctypes.byref(command)) == 0
                    if command.command_id:
                        assert command.url.startswith("http://127.0.0.1:")
                        break
                    time.sleep(0.02)
                else:
                    raise AssertionError("FCUP play did not reach media queue")
                with urllib.request.urlopen(command.url, timeout=10) as reply:
                    master = reply.read().decode()
                child = next(line for line in master.splitlines() if line and not line.startswith("#"))
                assert child.startswith("http://127.0.0.1:")
                with urllib.request.urlopen(child, timeout=10) as reply:
                    playlist = reply.read().decode()
                assert "https://example.invalid/segment.ts" in playlist
                assert phone.wait(timeout=10) == 0
                assert request(5001, "/info")["deviceID"] == mirror["deviceID"]
            finally:
                if phone.poll() is None:
                    phone.kill()
                    phone.wait()
        url = "http://127.0.0.1:9999/isolation.mp4"
        for payload in (f"Content-Location: {url}\r\nStart-Position: 0\r\n".encode(),
                        plistlib.dumps({"Content-Location": url}, fmt=plistlib.FMT_BINARY),
                        plistlib.dumps({"Content-Location": url}, fmt=plistlib.FMT_XML)):
            request(7011, "/play", "POST", payload)
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                command = MediaRequest(struct_size=ctypes.sizeof(MediaRequest))
                assert core.im_media_cast_get_request(ctypes.byref(command)) == 0
                if command.command_id:
                    assert command.url == url
                    break
                time.sleep(0.02)
            else:
                raise AssertionError("play request did not reach independent media queue")
            assert request(5001, "/info")["deviceID"] == mirror["deviceID"]
        core.im_media_cast_receiver_stop()
        ready("wireless")
        assert request(5001, "/info")["deviceID"] == mirror["deviceID"]
        assert core.im_media_cast_receiver_start("Isolation Video", host) == 0
        ready("media_cast")
        core.im_wireless_receiver_stop()
        ready("media_cast")
        assert request(7011, "/server-info")["deviceid"] == media["deviceid"]
        assert core.im_wireless_receiver_start("Isolation Mirror", host) == 0
        ready("wireless")
        ready("media_cast")
        print("PASS: independent startup, identities, stop, restart, and simultaneous service responses")
    finally:
        core.im_shutdown()
        directory.close()


if __name__ == "__main__":
    main()
