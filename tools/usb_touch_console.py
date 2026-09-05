"""Interactive USB iPhone touch test for the project's bridge.

The process sends no input until the operator enters a command. Coordinates are
normalized to 0..1 and encoded as the project's touch_batch message.
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


def read_bridge_output(stream) -> None:
    for line in iter(stream.readline, ""):
        print("[bridge] " + line.rstrip(), flush=True)


def send_touch_batch(process: subprocess.Popen, sequence: int, points: list[dict]) -> None:
    message = {
        "schema": "iphoneMirror.touch.v2",
        "kind": "touch_batch",
        "seq": sequence,
        "timestampNs": time.monotonic_ns(),
        "points": points,
    }
    payload = json.dumps(message, separators=(",", ":")).encode("utf-8")
    proc.stdin.write(struct.pack("<I", len(payload)) + payload)
    proc.stdin.flush()


def coord(value: str) -> float:
    result = float(value)
    if not 0.0 <= result <= 1.0:
        raise ValueError("coordinates must be between 0 and 1")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--bridge",
        default=str(Path(__file__).resolve().parents[1] / "dist" / "UsbTouchBridge.exe"),
    )
    parser.add_argument("--udid")
    parser.add_argument("--rate-hz", type=int, default=120)
    args = parser.parse_args()

    command = [args.bridge, "--rate-hz", str(args.rate_hz)]
    if args.udid:
        command += ["--udid", args.udid]
    print("Starting USB touch bridge. No touch is sent automatically.", flush=True)
    proc = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
    )
    assert proc.stdin is not None and proc.stdout is not None
    threading.Thread(target=read_bridge_output, args=(proc.stdout,), daemon=True).start()
    print("Wait for bridge readiness, then use: tap x y | drag x1 y1 x2 y2 | quit", flush=True)
    seq = 0
    try:
        while proc.poll() is None:
            raw = input("> ").strip()
            if not raw:
                continue
            parts = raw.split()
            op = parts[0].lower()
            if op in ("quit", "exit"):
                break
            if op == "tap" and len(parts) == 3:
                x, y = coord(parts[1]), coord(parts[2])
                seq += 1
                send_touch_batch(proc, seq, [{"pointerId": 1, "action": "down", "normalizedX": x, "normalizedY": y}])
                time.sleep(0.06)
                seq += 1
                send_touch_batch(proc, seq, [{"pointerId": 1, "action": "up", "normalizedX": x, "normalizedY": y}])
                print("sent tap", flush=True)
                continue
            if op == "drag" and len(parts) == 5:
                x1, y1, x2, y2 = map(coord, parts[1:])
                seq += 1
                send_touch_batch(proc, seq, [{"pointerId": 1, "action": "down", "normalizedX": x1, "normalizedY": y1}])
                for i in range(1, 21):
                    t = i / 20.0
                    seq += 1
                    send_touch_batch(proc, seq, [{
                        "pointerId": 1,
                        "action": "move" if i < 20 else "up",
                        "normalizedX": x1 + (x2 - x1) * t,
                        "normalizedY": y1 + (y2 - y1) * t,
                    }])
                    time.sleep(0.02)
                print("sent drag", flush=True)
                continue
            print("commands: tap x y | drag x1 y1 x2 y2 | quit", flush=True)
    except (EOFError, BrokenPipeError, KeyboardInterrupt):
        pass
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
