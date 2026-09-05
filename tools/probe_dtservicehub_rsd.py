import asyncio
from pymobiledevice3.remote import userspace_tunnel
from pymobiledevice3.dtx_service_provider import DtxServiceProvider

class Hub(DtxServiceProvider):
    SERVICE_NAME = "com.apple.instruments.dtservicehub"
    RSD_SERVICE_NAME = SERVICE_NAME

async def main():
    rsd = await userspace_tunnel.establish_userspace_rsd(serial="00008150-001903580A9B401C")
    try:
        async with Hub(rsd) as hub:
            print("dtx_connected", type(hub.dtx).__name__)
            print("channels", hub.dtx.channels)
    finally:
        await rsd.__aexit__(None, None, None)

asyncio.run(main())
