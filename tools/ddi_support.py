"""Cancellable Personalized DDI operations for the pinned pymobiledevice3 runtime.

The upstream TSS client uses requests without a timeout inside an executor whose
context manager joins its thread on cancellation. Do not use that transport in
the bridge: a stalled Apple request must not stop IPC or USB recovery.
"""
from __future__ import annotations

import asyncio
import copy
import contextlib
import logging
import plistlib
import time
from contextlib import asynccontextmanager
from xml.parsers.expat import ExpatError

import httpx
from pymobiledevice3.exceptions import (
    NoSuchBuildIdentityError, AlreadyMountedError, MissingManifestError,
    DeveloperModeIsNotEnabledError, GetProhibitedError, NotPairedError,
    PasswordRequiredError, DeviceNotFoundError, ConnectionFailedToUsbmuxdError,
    ConnectionTerminatedError, MuxException, InvalidServiceError,
)
from pymobiledevice3.restore.tss import TSSRequest, TSS_CONTROLLER_ACTION_URL
from pymobiledevice3.services.mobile_image_mounter import PersonalizedImageMounter as UpstreamMounter

log = logging.getLogger('iphoneMirror.ddi')
# Preserve Apple's upstream signing endpoint. gs.apple.com's HTTPS listener
# does not provide a publicly trusted certificate on every route. The returned
# Apple ticket is verified by the device; never disable TLS verification.
TSS_URL = TSS_CONTROLLER_ACTION_URL
TSS_TIMEOUT_SECONDS = 40
SERVICE_TIMEOUT_SECONDS = 15
UPLOAD_TIMEOUT_SECONDS = 90
MOUNT_TIMEOUT_SECONDS = 45
CLOSE_TIMEOUT_SECONDS = 3
MAX_TSS_RESPONSE_BYTES = 4 * 1024 * 1024
TSS_ATTEMPTS = 2
TSS_RETRY_DELAY_SECONDS = 1


class BridgePrerequisiteError(RuntimeError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def device_failure(error):
    """Preserve actionable device errors across DDI candidate fallback."""
    for types, code in (
        ((ConnectionFailedToUsbmuxdError,), 'apple_usbmux_unavailable'),
        ((GetProhibitedError, NotPairedError), 'apple_device_not_trusted'),
        ((PasswordRequiredError,), 'apple_device_locked'),
        ((DeviceNotFoundError,), 'apple_device_not_found'),
        ((DeveloperModeIsNotEnabledError,), 'developer_mode_required'),
        ((ConnectionTerminatedError, MuxException, ConnectionError), 'apple_connection_lost'),
        ((InvalidServiceError,), 'developer_image_service_unavailable'),
    ):
        if isinstance(error, types):
            return BridgePrerequisiteError(code, f'DDI device operation failed ({type(error).__name__}).')
    return None


@asynccontextmanager
async def bounded_mounter(mounter):
    # Bound entering AND leaving the service, not just mount(). A failed open
    # may still own a connection, so always close even when __aenter__ fails.
    try:
        try:
            async with asyncio.timeout(SERVICE_TIMEOUT_SECONDS):
                await mounter.__aenter__()
        except TimeoutError as error:
            raise BridgePrerequisiteError('developer_image_service_timeout',
                                          'Opening the image-mounter service timed out.') from error
        except Exception as error:
            raise device_failure(error) or BridgePrerequisiteError(
                'developer_image_service_unavailable',
                f'Opening the image-mounter service failed ({type(error).__name__}).') from error
        yield mounter
    finally:
        # The pinned SDK clears ServiceConnection.writer/socket in a finally
        # block even if cancellation interrupts wait_closed before socket.close.
        # Retain only this mounter's connection, never the parent Lockdown one.
        service = getattr(mounter, '_service', None)
        writer = getattr(service, 'writer', None)
        sock = getattr(service, 'socket', None)
        closed = False
        try:
            async with asyncio.timeout(CLOSE_TIMEOUT_SECONDS):
                await mounter.__aexit__(None, None, None)
            closed = True
        except Exception as error:
            log.warning('DDI service cleanup failed: %s', type(error).__name__)
        finally:
            if not closed:
                if writer is not None:
                    with contextlib.suppress(Exception):
                        writer.transport.abort()
                if sock is not None:
                    with contextlib.suppress(Exception):
                        sock.close()


def personalization_request(build_manifest, identifiers, ecid, nonce):
    """Build the same DDI ticket request as pymobiledevice3 11.3.1.

    Keep Apple's identity selection and restore rules; replace only the HTTP
    transport. Never log this payload (it contains device identifiers/nonces).
    """
    board, chip = identifiers['BoardId'], identifiers['ChipID']
    identity = next((item for item in build_manifest['BuildIdentities']
                     if int(item['ApBoardID'], 0) == board and
                     int(item['ApChipID'], 0) == chip), None)
    if identity is None:
        raise NoSuchBuildIdentityError('No DDI identity matches this device.')
    request = TSSRequest()
    payload = {key: value for key, value in identifiers.items() if key.startswith('Ap,')}
    payload.update({
        '@ApImg4Ticket': True, '@BBTicket': True, 'ApBoardID': board,
        'ApChipID': chip, 'ApECID': ecid, 'ApNonce': nonce,
        'ApProductionMode': True, 'ApSecurityDomain': 1, 'ApSecurityMode': True,
        'SepNonce': bytes(20), 'UID_MODE': False,
    })
    parameters = {'ApProductionMode': True, 'ApSecurityDomain': 1,
                  'ApSecurityMode': True, 'ApSupportsImg4': True}
    manifest = identity['Manifest']
    rules = manifest.get('LoadableTrustCache', {}).get('Info', {}).get('RestoreRequestRules', [])
    for key, entry in manifest.items():
        if entry.get('Info') is None or not entry.get('Trusted', False):
            continue
        value = copy.deepcopy(entry)
        value.pop('Info')
        if rules:
            value = request.apply_restore_request_rules(value, parameters, rules)
        if entry.get('Digest') is None:
            value['Digest'] = b''
        payload[key] = value
    request.update(payload)
    return plistlib.dumps(request._request)


async def _request_ticket_once(payload: bytes, headers) -> bytes:
    async with httpx.AsyncClient(timeout=httpx.Timeout(18, connect=8),
                                 follow_redirects=False) as client:
        async with client.stream('POST', TSS_URL, headers=headers, content=payload) as response:
            if response.status_code in (408, 429, 500, 502, 503, 504):
                raise BridgePrerequisiteError('developer_image_tss_unavailable',
                                              f'Apple signing service returned HTTP {response.status_code}.')
            response.raise_for_status()
            content = bytearray()
            async for chunk in response.aiter_bytes():
                content.extend(chunk)
                if len(content) > MAX_TSS_RESPONSE_BYTES:
                    raise ValueError('TSS response exceeds the size limit')
    envelope, separator, document = bytes(content).partition(b'REQUEST_STRING=')
    fields = dict(part.split(b'=', 1) for part in envelope.split(b'&') if b'=' in part)
    status = fields.get(b'STATUS', b'0')
    if status == b'93':
        raise BridgePrerequisiteError('developer_image_tss_unavailable',
                                      'Apple signing service reported an internal error (93).')
    if status != b'0':
        raise BridgePrerequisiteError('developer_image_tss_rejected',
                                      'Apple rejected personalization of this developer image.')
    if fields.get(b'MESSAGE') != b'SUCCESS' or not separator:
        raise ValueError('Apple did not return a successful personalization response')
    ticket = plistlib.loads(document).get('ApImg4Ticket')
    if not isinstance(ticket, bytes) or not ticket:
        raise ValueError('Apple returned no personalization ticket')
    return ticket


async def request_ticket(payload: bytes) -> bytes:
    headers = {'Cache-Control': 'no-cache', 'Content-Type': 'text/xml; charset="utf-8"',
               'User-Agent': 'InetURL/1.0'}
    try:
        # Wall-clock bound also covers slow trickle responses and connection
        # setup. Async sockets cancel without joining a blocking worker thread.
        async with asyncio.timeout(TSS_TIMEOUT_SECONDS):
            for attempt in range(TSS_ATTEMPTS):
                try:
                    return await _request_ticket_once(payload, headers)
                except BridgePrerequisiteError as error:
                    if error.code != 'developer_image_tss_unavailable' or attempt + 1 == TSS_ATTEMPTS:
                        raise
                except (httpx.TransportError, TimeoutError):
                    if attempt + 1 == TSS_ATTEMPTS:
                        raise
                log.info('Apple signing request retry=%d/%d', attempt + 1, TSS_ATTEMPTS - 1)
                await asyncio.sleep(TSS_RETRY_DELAY_SECONDS)
    except (TimeoutError, httpx.TimeoutException) as error:
        raise BridgePrerequisiteError('developer_image_tss_timeout',
                                      'Apple DDI personalization timed out; check access to gs.apple.com.') from error
    except (httpx.HTTPError, ValueError, TypeError, AttributeError, plistlib.InvalidFileException, ExpatError) as error:
        # Do not copy proxy URLs, response bodies, identifiers or tickets into logs.
        raise BridgePrerequisiteError('developer_image_tss_failed',
                                      f'Apple DDI personalization failed ({type(error).__name__}).') from error


class PersonalizedImageMounter(UpstreamMounter):
    report_status = None

    async def _phase(self, code, timeout, operation):
        started = time.monotonic()
        try:
            if self.report_status is not None:
                await self.report_status(code)
            async with asyncio.timeout(timeout):
                return await operation()
        except TimeoutError as error:
            error_code = {
                'checking_developer_image_ticket': 'developer_image_service_timeout',
                'personalizing_developer_image': 'developer_image_tss_timeout',
                'uploading_developer_image': 'developer_image_upload_timeout',
                'activating_developer_image': 'developer_image_mount_timeout',
            }[code]
            raise BridgePrerequisiteError(error_code,
                                          f'DDI phase {code} timed out after {timeout} seconds.') from error
        except (BridgePrerequisiteError, AlreadyMountedError, MissingManifestError):
            raise
        except Exception as error:
            known = device_failure(error)
            if known is not None:
                raise known from error
            error_code = {
                'checking_developer_image_ticket': 'developer_image_service_unavailable',
                'personalizing_developer_image': 'developer_image_tss_failed',
                'uploading_developer_image': 'developer_image_upload_failed',
                'activating_developer_image': 'developer_image_mount_failed',
            }[code]
            raise BridgePrerequisiteError(error_code,
                                          f'DDI phase {code} failed ({type(error).__name__}).') from error
        finally:
            log.info('DDI phase=%s elapsed_ms=%d', code, (time.monotonic() - started) * 1000)

    async def query_personalization_manifest(self, image_type, signature):
        return await self._phase('checking_developer_image_ticket', SERVICE_TIMEOUT_SECONDS,
                                lambda: super(PersonalizedImageMounter, self).query_personalization_manifest(
                                    image_type, signature))

    async def get_manifest_from_tss(self, build_manifest):
        async def personalize():
            try:
                async with asyncio.timeout(SERVICE_TIMEOUT_SECONDS):
                    identifiers = await self.query_personalization_identifiers()
                    nonce = await self.query_nonce('DeveloperDiskImage')
            except TimeoutError as error:
                raise BridgePrerequisiteError('developer_image_service_timeout',
                                              'Reading device personalization identifiers timed out.') from error
            try:
                payload = personalization_request(build_manifest, identifiers, self.lockdown.ecid, nonce)
            except (NoSuchBuildIdentityError, KeyError, ValueError, TypeError, AttributeError) as error:
                raise BridgePrerequisiteError('developer_image_download_incompatible',
                                              'The DDI build manifest does not support this device.') from error
            return await request_ticket(payload)
        return await self._phase('personalizing_developer_image', TSS_TIMEOUT_SECONDS + SERVICE_TIMEOUT_SECONDS,
                                personalize)

    async def upload_image(self, image_type, image, signature):
        return await self._phase('uploading_developer_image', UPLOAD_TIMEOUT_SECONDS,
                                lambda: super(PersonalizedImageMounter, self).upload_image(image_type, image, signature))

    async def mount_image(self, image_type, signature, extras=None):
        return await self._phase('activating_developer_image', MOUNT_TIMEOUT_SECONDS,
                                lambda: super(PersonalizedImageMounter, self).mount_image(image_type, signature, extras))
