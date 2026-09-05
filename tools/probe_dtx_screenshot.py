import asyncio
from pathlib import Path
from pymobiledevice3.remote import userspace_tunnel
from pymobiledevice3.dtx_service_provider import DtxServiceProvider

class Hub(DtxServiceProvider):
    SERVICE_NAME = "com.apple.instruments.dtservicehub"
    RSD_SERVICE_NAME = SERVICE_NAME

async def main():
    rsd = await userspace_tunnel.establish_userspace_rsd(serial="00008150-001903580A9B401C")
    try:
        async with Hub(rsd) as hub:
            svc = await hub.dtx.open_channel("com.apple.instruments.server.services.screenshot")
            result = await svc.invoke("takeScreenshot")
            data = bytes(result)
            Path("tmp_dtx_screenshot.bin").write_bytes(data)
            print("screenshot_bytes", len(data), data[:8].hex())
            await hub.dtx.cancel_channel(svc._channel)
    finally:
        await rsd.__aexit__(None, None, None)

asyncio.run(main())
