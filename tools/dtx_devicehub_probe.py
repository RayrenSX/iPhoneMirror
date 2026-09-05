"""Minimal proof-of-life for Apple's DeviceHub/DTX path over USB."""
import asyncio
from pathlib import Path

from pymobiledevice3.dtx_service_provider import DtxServiceProvider
from pymobiledevice3.remote import userspace_tunnel


class DeviceHub(DtxServiceProvider):
    SERVICE_NAME = "com.apple.instruments.dtservicehub"
    RSD_SERVICE_NAME = SERVICE_NAME


async def main() -> None:
    rsd = await userspace_tunnel.establish_userspace_rsd(
        serial="00008150-001903580A9B401C"
    )
    try:
        async with DeviceHub(rsd) as hub:
            screenshot = await hub.dtx.open_channel(
                "com.apple.instruments.server.services.screenshot"
            )
            data = bytes(await screenshot.invoke("takeScreenshot"))
            Path("tmp_devicehub_screenshot.png").write_bytes(data)
            print({"dtx": "ok", "channel": screenshot.identifier,
                   "bytes": len(data), "magic": data[:8].hex()})
            await hub.dtx.cancel_channel(screenshot._channel)
    finally:
        await rsd.__aexit__(None, None, None)


if __name__ == "__main__":
    asyncio.run(main())
