import asyncio
from pymobiledevice3.lockdown import create_using_usbmux

UDID = "00008150-001903580A9B401C"

async def main():
    lockdown = await create_using_usbmux(serial=UDID, autopair=False)
    try:
        for name in (
            "com.apple.instruments.dtservicehub",
            "com.apple.instruments.remoteserver.DVTSecureSocketProxy",
            "com.apple.coredevice.hid.universalhidservice",
            "com.apple.coredevice.displayservice",
        ):
            try:
                value = await lockdown.get_service_connection_attributes(name, False)
                print(name, value)
            except Exception as exc:
                print(name, type(exc).__name__, str(exc))
    finally:
        await lockdown.close()

if __name__ == "__main__":
    asyncio.run(main())
