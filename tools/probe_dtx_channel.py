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
            print("handshake=ok")
            for identifier in (
                "com.apple.instruments.server.services.deviceinfo",
                "com.apple.instruments.server.services.screenshot",
                "com.apple.instruments.server.services.processcontrol",
            ):
                try:
                    service = await hub.dtx.open_channel(identifier)
                    print("channel=ok", identifier, type(service).__name__)
                    await hub.dtx.cancel_channel(service._channel)
                except Exception as exc:
                    print("channel=error", identifier, type(exc).__name__, str(exc))
    finally:
        await rsd.__aexit__(None, None, None)

asyncio.run(main())
