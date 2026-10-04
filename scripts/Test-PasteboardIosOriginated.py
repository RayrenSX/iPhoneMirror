"""Seed device Pasteboard through a fresh bridge and verify Windows readback.

This deliberately uses the same shipped bridge executable as iPhoneMirror,
while the running app performs the PULL into the Windows clipboard.
"""
from __future__ import annotations

import argparse
import json
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("value")
    parser.add_argument("--bridge", default=str(Path(__file__).resolve().parents[1] / "dist" / "iUsbBridge.exe"))
    parser.add_argument("--udid", required=True)
    parser.add_argument("--evidence", type=Path)
    args = parser.parse_args()

    proc = subprocess.Popen(
        [args.bridge, "--wireless", "--udid", args.udid],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT, text=False,
    )
    lines: list[str] = []

    def read_output() -> None:
        assert proc.stdout is not None
        for raw in iter(proc.stdout.readline, b""):
            line = raw.decode("utf-8", "replace").rstrip()
            lines.append(line)
            print(line, flush=True)

    threading.Thread(target=read_output, daemon=True).start()
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline and not any('"event": "ready"' in line for line in lines):
        time.sleep(0.05)
    if not any('"event": "ready"' in line for line in lines):
        proc.kill()
        raise RuntimeError("bridge did not become ready")

    seq = 0
    set_reply = False
    assert proc.stdin is not None
    deadline = time.monotonic() + 70
    while time.monotonic() < deadline and not set_reply:
        seq += 1
        frame = {
            "schema": "iphoneMirror.touch.v2",
            "kind": "paste_text",
            "seq": seq,
            "timestampNs": time.monotonic_ns(),
            "text": args.value,
        }
        payload = json.dumps(frame, separators=(",", ":")).encode()
        proc.stdin.write(struct.pack("<I", len(payload)) + payload)
        proc.stdin.flush()
        print(f"seed-attempt {seq} {args.value}", flush=True)
        time.sleep(2)
        set_reply = any("pasteboard_set_reply" in line for line in lines)

    time.sleep(3)
    proc.stdin.close()
    try:
        proc.wait(timeout=8)
    except subprocess.TimeoutExpired:
        proc.kill()

    clipboard_command = (
        "Add-Type -AssemblyName System.Windows.Forms; "
        "[Windows.Forms.Clipboard]::GetText()"
    )
    actual = subprocess.check_output(
        ["powershell.exe", "-NoProfile", "-STA", "-Command", clipboard_command],
        text=True,
    ).rstrip("\r\n")
    result = {"expected": args.value, "actual": actual, "setReply": set_reply,
              "passed": set_reply and actual == args.value}
    print(json.dumps(result, ensure_ascii=False), flush=True)
    if args.evidence:
        args.evidence.parent.mkdir(parents=True, exist_ok=True)
        args.evidence.write_text(json.dumps({"result": result, "bridge": lines},
                                             ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
