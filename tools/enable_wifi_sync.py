#!/usr/bin/env python3
"""Enable Apple's Wi-Fi lockdown connection setting for one USB-paired device.

This is the programmatic equivalent of Apple Devices' "Sync with this iPhone
over Wi-Fi" setting. It requires a USB connection and an unlocked, trusted
device while the setting is written.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
import time

from pymobiledevice3.lockdown import create_using_usbmux
from pymobiledevice3.usbmux import list_devices


async def connection_types(udid: str) -> set[str]:
    return {
        str(device.connection_type)
        for device in await list_devices()
        if device.serial.casefold() == udid.casefold()
    }


async def enable(udid: str, wait_seconds: float) -> int:
    usb_types = await connection_types(udid)
    if "USB" not in usb_types:
        print(f"ERROR: {udid} is not available over USB. Connect, unlock, and trust it first.")
        return 2

    device = await create_using_usbmux(serial=udid, connection_type="USB")
    try:
        before = await device.get_enable_wifi_connections()
        if not before:
            await device.set_enable_wifi_connections(True)
        after = await device.get_enable_wifi_connections()
    finally:
        await device.close()

    print(f"UDID={udid}")
    print(f"EnableWifiConnections.before={before}")
    print(f"EnableWifiConnections.after={after}")
    if not after:
        print("ERROR: device did not accept EnableWifiConnections=true")
        return 3

    deadline = time.monotonic() + wait_seconds
    while True:
        types = await connection_types(udid)
        if "Network" in types:
            print("Network device is available in usbmux.")
            return 0
        if time.monotonic() >= deadline:
            print("Wi-Fi sync is enabled. Disconnect the USB cable, keep the iPhone unlocked on the same Wi-Fi, then rerun this script to verify the Network device.")
            return 0
        await asyncio.sleep(1)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--udid", required=True, help="USB-connected iPhone UDID")
    parser.add_argument("--wait-seconds", type=float, default=5.0,
                        help="seconds to wait for usbmux Network enumeration")
    args = parser.parse_args()
    if args.wait_seconds < 0:
        parser.error("--wait-seconds must be non-negative")
    return asyncio.run(enable(args.udid, args.wait_seconds))


if __name__ == "__main__":
    sys.exit(main())
