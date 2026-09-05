#!/usr/bin/env python3
"""Verify the CoreDevice tunnel over a paired Wi-Fi Lockdown connection."""

from __future__ import annotations

import argparse
import asyncio
import plistlib
import traceback

from pymobiledevice3.lockdown import create_using_tcp
from pymobiledevice3.remote.common import TunnelProtocol
from pymobiledevice3.remote.module_imports import start_tunnel
from pymobiledevice3.remote.tunnel_service import CoreDeviceTunnelProxy


async def main_async(address: str, udid: str) -> int:
    pair_path = rf"C:\ProgramData\Apple\Lockdown\{udid}.plist"
    with open(pair_path, "rb") as file:
        pair_record = plistlib.load(file)
    lockdown = await create_using_tcp(address, identifier=udid, autopair=False,
        pair_record=pair_record, keep_alive=True)
    try:
        print(f"lockdown udid={lockdown.udid} paired={lockdown.paired}")
        proxy = await CoreDeviceTunnelProxy.create(lockdown)
        print("CoreDeviceTunnelProxy connected")
        async with start_tunnel(proxy, protocol=TunnelProtocol.TCP) as tunnel:
            print(f"tunnel address={tunnel.address} port={tunnel.port}")
        return 0
    except Exception:
        traceback.print_exc()
        return 1
    finally:
        await lockdown.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--address", required=True)
    parser.add_argument("--udid", required=True)
    args = parser.parse_args()
    return asyncio.run(main_async(args.address, args.udid))


if __name__ == "__main__":
    raise SystemExit(main())
