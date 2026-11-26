"""Read SpringBoard direction over the existing capture usbmux interface.

This path does not mount a DDI, enable developer mode, pair, or send input.
The native parent owns its lifetime by keeping stdin open.
"""
import asyncio
import contextlib
import os
import sys

from pymobiledevice3.services.springboard import SpringBoardServicesService
from iostouch.apple_identity import capture_mux_address, capture_mux_environment_key


async def observe_orientation(bridge, udid):
    ipc = bridge.BridgeChannel()
    session = bridge.TouchSession(ipc, 120, udid=udid)

    async def observe():
        await session._start_capture_mux(attempts=1)
        address = capture_mux_address(os.environ.get('USBMUXD_SOCKET_ADDRESS'))
        if not address:
            raise ConnectionError('capture usbmux is unavailable')
        announced_mux = False
        unavailable = False

        def report_unavailable():
            nonlocal unavailable
            if not unavailable:
                print('IM_ORIENTATION_UNAVAILABLE', flush=True)
                unavailable = True

        while True:
            lockdown = None
            springboard = None
            try:
                lockdown = await asyncio.wait_for(bridge.create_using_usbmux(
                    serial=udid, connection_type='USB', autopair=False), 5)
                if not bridge._udid_matches(lockdown.udid, udid):
                    raise ConnectionError('orientation device identity mismatch')
                if not announced_mux:
                    print('IM_MUX ' + address.rsplit(':', 1)[1], flush=True)
                    announced_mux = True
                springboard = SpringBoardServicesService(lockdown)
                await asyncio.wait_for(springboard.connect(), 2)
                while True:
                    try:
                        orientation = int(await asyncio.wait_for(
                            springboard.get_interface_orientation(), 1))
                    except ValueError:
                        # An unknown value is a reply, so the stream remains usable.
                        orientation = 0
                    if orientation in (1, 2, 3, 4):
                        print(f'IM_ORIENTATION {orientation}', flush=True)
                        unavailable = False
                    else:
                        report_unavailable()
                    await asyncio.sleep(.1)
            except Exception:
                report_unavailable()
            finally:
                try:
                    if springboard is not None:
                        with contextlib.suppress(Exception):
                            await asyncio.wait_for(springboard.close(), 1)
                finally:
                    if lockdown is not None:
                        with contextlib.suppress(Exception):
                            await asyncio.wait_for(lockdown.close(), 2)
            # Only reopen the clients. The USB mux may be serving reverse control.
            await asyncio.sleep(1)

    observation = asyncio.create_task(observe())
    disconnected = asyncio.create_task(ipc.wait_for_disconnect())
    try:
        done, _ = await asyncio.wait((observation, disconnected), return_when=asyncio.FIRST_COMPLETED)
        for task in done:
            await task
    finally:
        for task in (observation, disconnected):
            task.cancel()
        await asyncio.gather(observation, disconnected, return_exceptions=True)
        session._close_capture_mux()
        await ipc.close()
