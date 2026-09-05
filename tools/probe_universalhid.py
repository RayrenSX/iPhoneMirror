import asyncio
from pymobiledevice3.remote import userspace_tunnel
from pymobiledevice3.remote.remote_service import RemoteService

class Universal(RemoteService):
    SERVICE_NAME = "com.apple.coredevice.hid.universalhid"

    def __init__(self, rsd):
        super().__init__(rsd, self.SERVICE_NAME)

async def main():
    rsd = await userspace_tunnel.establish_userspace_rsd(serial="00008150-001903580A9B401C")
    try:
        async with Universal(rsd) as svc:
            for payload in ({"connectedServices": {}}, {"list": {}}):
                try:
                    result = await svc.service.send_receive_request({
                        "featureIdentifier": "com.apple.coredevice.feature.remote.universalhid",
                        "messageType": "Request", "payload": payload})
                    print(payload, result)
                except Exception as exc:
                    print(payload, type(exc).__name__, str(exc))
    finally:
        await rsd.__aexit__(None, None, None)

asyncio.run(main())
