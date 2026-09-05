import asyncio
from pymobiledevice3.remote import userspace_tunnel

async def main():
    rsd = await userspace_tunnel.establish_userspace_rsd(serial="00008150-001903580A9B401C")
    try:
        print("product", rsd.product_version)
        print("service_count", len(rsd.peer_info.get("Services", {})))
        for name, info in sorted(rsd.peer_info.get("Services", {}).items()):
            if any(x in name.lower() for x in ("hid", "devicehub", "dtservice", "display", "remote")):
                print(name, info)
    finally:
        await rsd.__aexit__(None, None, None)

asyncio.run(main())
