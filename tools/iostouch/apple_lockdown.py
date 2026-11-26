"""Open trusted Lockdown sessions while retaining ownership through cancellation."""
import asyncio
import contextlib
import logging

from pymobiledevice3 import lockdown as _lockdown
from pymobiledevice3.exceptions import DeviceNotFoundError

from iostouch.apple_identity import udid_matches

log = logging.getLogger(__name__)
LOCKDOWN_CLEANUP_TIMEOUT_SECONDS = 2


async def _close_owned_lockdown_service(service):
    writer, sock = service.writer, service.socket
    try:
        async with asyncio.timeout(LOCKDOWN_CLEANUP_TIMEOUT_SECONDS):
            await service.close()
    except Exception as error:
        log.warning('Lockdown service cleanup failed: %s', type(error).__name__)
    finally:
        if writer is not None:
            with contextlib.suppress(Exception): writer.transport.abort()
        if sock is not None:
            with contextlib.suppress(Exception): sock.close()


class _OwnedLockdownServices:
    async def close(self):
        await _close_owned_lockdown_service(self.service)

    async def start_lockdown_service(self, name, include_escrow_bag=False):
        # Match the SDK's public service/TLS protocol, but own the new connection
        # until TLS is complete. The mounter cannot clean up an unreturned socket.
        attributes = await self.get_service_connection_attributes(
            name, include_escrow_bag=include_escrow_bag)
        connection = await self.create_service_connection(attributes['Port'])
        adopted = False
        try:
            if attributes.get('EnableServiceSSL', False):
                with self.ssl_file() as certificate:
                    await connection.ssl_start(certificate)
            adopted = True
            return connection
        finally:
            if not adopted:
                await _close_owned_lockdown_service(connection)


class BridgeUsbmuxLockdownClient(_OwnedLockdownServices, _lockdown.UsbmuxLockdownClient):
    pass


class BridgePlistUsbmuxLockdownClient(_OwnedLockdownServices, _lockdown.PlistUsbmuxLockdownClient):
    pass


async def create_using_usbmux(*, serial=None, connection_type=None, autopair=False, usbmux_address=None):
    """Own the opened device socket until the pinned SDK client is initialized.

    The SDK convenience factory catches Exception, which excludes task
    cancellation. Use its public client factory with the same mux/BUID choice,
    retaining ownership across cancellation and partial initialization.
    """
    options = {'usbmux_address': usbmux_address} if usbmux_address is not None else {}
    open_service = _lockdown.ServiceConnection.create_using_usbmux
    try:
        service = await open_service(serial, _lockdown.SERVICE_PORT, connection_type=connection_type, **options)
    except DeviceNotFoundError:
        if not serial:
            raise
        # The SDK matches serials case-sensitively. The same device can have
        # different casing in the binding and in the active usbmux broker.
        devices = await _lockdown.usbmux.list_devices(**options)
        matches = (device for device in devices
                   if (connection_type is None or device.connection_type == connection_type)
                   and device.serial != serial and udid_matches(device.serial, serial))
        advertised = max(matches, key=lambda device: device.is_usb, default=None)
        if advertised is None:
            raise
        service = await open_service(advertised.serial, _lockdown.SERVICE_PORT,
                                     connection_type=advertised.connection_type, **options)
    adopted = False
    try:
        client_type = BridgeUsbmuxLockdownClient
        system_buid = _lockdown.SYSTEM_BUID
        async with await _lockdown.usbmux.create_mux(**options) as mux:
            if isinstance(mux, _lockdown.PlistMuxConnection):
                system_buid = await mux.get_buid()
                client_type = BridgePlistUsbmuxLockdownClient
        identifier = service.mux_device.serial if service.mux_device is not None else serial
        client = await client_type.create(service, identifier=identifier,
                                          system_buid=system_buid, autopair=autopair, **options)
        adopted = True
        return client
    finally:
        if not adopted:
            # Keep handles: SDK close clears attributes even if cancelled
            # while awaiting a TLS close, before closing the underlying socket.
            await _close_owned_lockdown_service(service)
