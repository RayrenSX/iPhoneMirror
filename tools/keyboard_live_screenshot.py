"""Opt-in independent phone screenshot for the keyboard hardware harness.
Uses only the supplied test bridge process (and its children) to find the
existing local usbmux endpoint. Does not read the mirroring frame cache.
Requires the project's developer Python dependencies plus psutil.
"""
import argparse
import asyncio
import os
from pathlib import Path

import psutil
from pymobiledevice3.usbmux import list_devices
from pymobiledevice3.remote.userspace_tunnel import establish_userspace_rsd
from pymobiledevice3.dtx_service_provider import DtxServiceProvider


class Hub(DtxServiceProvider):
    SERVICE_NAME = "com.apple.instruments.dtservicehub"
    RSD_SERVICE_NAME = SERVICE_NAME


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--udid", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = psutil.Process(args.pid)
    addresses = []
    for process in [root, *root.children(recursive=True)]:
        for connection in process.net_connections(kind="tcp"):
            if connection.status == psutil.CONN_LISTEN and connection.laddr.ip in ("127.0.0.1", "::1"):
                addresses.append(f"{connection.laddr.ip}:{connection.laddr.port}")
    for address in addresses:
        try:
            devices = await asyncio.wait_for(list_devices(usbmux_address=address), 2)
            if any(d.serial.replace("-", "").lower() == args.udid.replace("-", "").lower() for d in devices):
                os.environ["USBMUXD_SOCKET_ADDRESS"] = address
                print("Using existing test bridge usbmux endpoint", flush=True)
                break
        except Exception:
            continue
    rsd = await establish_userspace_rsd(serial=args.udid)
    try:
        async with Hub(rsd) as hub:
            service = await hub.dtx.open_channel("com.apple.instruments.server.services.screenshot")
            data = bytes(await service.invoke("takeScreenshot"))
            if not data.startswith(b"\x89PNG"):
                raise ValueError("Device returned a non-PNG screenshot")
            args.output.write_bytes(data)
            print(f"Independent DVT screenshot: {len(data)} bytes", flush=True)
    finally:
        await rsd.__aexit__(None, None, None)


if __name__ == "__main__":
    asyncio.run(main())
