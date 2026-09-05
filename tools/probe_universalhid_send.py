import asyncio
from pymobiledevice3.remote import userspace_tunnel
from pymobiledevice3.remote.remote_service import RemoteService
from pymobiledevice3.remote.core_device.hid_service import build_touchscreen_report, TOUCHSCREEN_STATE_RELEASE
from pymobiledevice3.remote.xpc_message import XpcUInt64Type

class Universal(RemoteService):
    SERVICE_NAME = "com.apple.coredevice.hid.universalhid"
    def __init__(self, rsd): super().__init__(rsd, self.SERVICE_NAME)

async def main():
    rsd = await userspace_tunnel.establish_userspace_rsd(serial="00008150-001903580A9B401C")
    try:
        async with Universal(rsd) as svc:
            report = build_touchscreen_report(TOUCHSCREEN_STATE_RELEASE, 0, 0)
            request = {"featureIdentifier": "com.apple.coredevice.feature.remote.universalhid",
                       "messageType": "Request",
                       "payload": {"send": {"_0": report, "_1": XpcUInt64Type(257)}}}
            try:
                result = await asyncio.wait_for(svc.service.send_receive_request(request), 5)
                print("result", result)
            except Exception as exc:
                print(type(exc).__name__, str(exc))
    finally:
        await rsd.__aexit__(None, None, None)

asyncio.run(main())
