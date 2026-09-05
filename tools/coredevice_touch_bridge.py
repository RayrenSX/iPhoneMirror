"""USB CoreDevice touch bridge using pymobiledevice3.

Input is JSONL: {"op":"down|move|up", "x":0..65535, "y":0..65535}.
The process keeps one auth-gated media stream alive for the whole session.
"""
import argparse
import asyncio
import json
import sys

from pymobiledevice3.remote import userspace_tunnel
from pymobiledevice3.remote.core_device.hid_service import (
    TOUCHSCREEN_STATE_CONTACT,
    TOUCHSCREEN_STATE_RELEASE,
    touch_session,
)


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--udid")
    args = parser.parse_args()
    rsd = await userspace_tunnel.establish_userspace_rsd(serial=args.udid)
    try:
        async with touch_session(rsd) as hid:
            print(json.dumps({"event": "ready", "protocol": 1,
                              "serviceId": 257, "capabilities": ["touch_5pt"]}), flush=True)
            for line in sys.stdin:
                if not line.strip():
                    continue
                msg = json.loads(line)
                op = msg.get("op")
                if op == "stop":
                    break
                if op not in ("down", "move", "up"):
                    raise ValueError("op must be down, move, up, or stop")
                x = max(0, min(65535, int(msg["x"])))
                y = max(0, min(65535, int(msg["y"])))
                state = TOUCHSCREEN_STATE_RELEASE if op == "up" else TOUCHSCREEN_STATE_CONTACT
                await hid.send_touchscreen(state, x, y, service_id=257)
                print(json.dumps({"event": "sent", "op": op, "x": x, "y": y}), flush=True)
    finally:
        await rsd.__aexit__(None, None, None)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as exc:
        print(json.dumps({"event": "error", "message": str(exc)}, ensure_ascii=False), flush=True)
        raise
