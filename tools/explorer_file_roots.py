"""Probe readable device domains and adapt CoreDevice reads to the AFC contract.

Discovery never writes files or opens another device/tunnel. House Arrest
containers are preferred over Documents; CoreDevice domains are read-only.
"""
import asyncio
from contextlib import asynccontextmanager, suppress
import json
import math

from pymobiledevice3.remote.core_device.file_service import Domain, FileServiceService
from pymobiledevice3.remote.xpc_message import XpcInt64Type, XpcUInt64Type
from pymobiledevice3.lockdown import LockdownClient, create_using_usbmux
from pymobiledevice3.exceptions import CoreDeviceError
from pymobiledevice3.services.house_arrest import VEND_CONTAINER

from document_file_service import DocumentsFiles, HouseArrestService, VEND_DOCUMENTS

CORE_FEATURE = 'com.apple.coredevice.feature.filesystemoperation'
CORE_FILE_OPERATIONS_VERIFIED = True
CORE_DOMAINS = {'appDataContainer': Domain.APP_DATA_CONTAINER,
                'appGroupDataContainer': Domain.APP_GROUP_DATA_CONTAINER,
                'temporary': Domain.TEMPORARY, 'systemCrashLogs': Domain.SYSTEM_CRASH_LOGS}


def file_read_only(value):
    if value.get('readOnly') is True or value.get('readonly') is True:
        return True
    mode = value.get('st_mode', value.get('permissions'))
    try:
        if isinstance(mode, str):
            mode = int(mode, 8 if mode.startswith('0') else 10)
        if type(mode) is int and mode & 0o222 == 0:
            return True
        flags = value.get('st_flags', value.get('flags', 0))
        return bool(int(flags) & (0x2 | 0x4 | 0x20000 | 0x40000))
    except (TypeError, ValueError):
        return False


@asynccontextmanager
async def application_files(rsd, identifier, documents):
    # The RSD House Arrest shim can stall while direct trusted Lockdown still
    # works. Pin the direct route to this RSD's identity; never select a phone.
    lockdown = None
    provider = rsd
    if not isinstance(rsd, LockdownClient) and isinstance(getattr(rsd, 'udid', None), str):
        try:
            async with asyncio.timeout(1):
                lockdown = await create_using_usbmux(serial=rsd.udid, autopair=False)
            if lockdown.udid != rsd.udid:
                raise ValueError('Device identity changed')
            provider = lockdown
        except Exception:
            if lockdown is not None:
                with suppress(Exception): await lockdown.close()
                lockdown = None
    service = HouseArrestService(provider, documents_only=documents)
    try:
        # Vend before entering AFC, whose reader otherwise consumes the plist.
        await service.send_command(identifier, VEND_DOCUMENTS if documents else VEND_CONTAINER)
        async with service:
            yield DocumentsFiles(service) if documents else service
    finally:
        with suppress(Exception):
            await asyncio.wait_for(service.close(), 2)
        if lockdown is not None:
            with suppress(Exception):
                await asyncio.wait_for(lockdown.close(), 2)


class CoreDeviceFiles:
    """Read-only, bounded, offset-based access using FileSystemOperation."""
    def __init__(self, service):
        self.service = service
        self.positions = {}
        self.app_roots = None

    async def _application_roots(self):
        if self.app_roots is None:
            self.app_roots = []
            for folder in ('Documents', 'Library', 'tmp'):
                try:
                    if (await self.stat('/' + folder)).get('st_ifmt') == 'S_IFDIR':
                        await self.listdir('/' + folder)
                        self.app_roots.append(folder)
                except (CoreDeviceError, PermissionError, FileNotFoundError):
                    continue
        if not self.app_roots:
            raise PermissionError('The application has no accessible data directories')
        return self.app_roots

    @staticmethod
    def path(path):
        value = path.strip('/')
        if value and any(p in ('', '.', '..') or '\\' in p or ':' in p or '\0' in p for p in value.split('/')):
            raise ValueError('Invalid CoreDevice path')
        return value or '.'

    async def operation(self, kind, **args):
        self.service.rsd.require_feature(self.service.CTRL_SERVICE_NAME, CORE_FEATURE)
        # FileService is a session protocol: go-ios keeps request ID 1 for
        # every control request. Incrementing RemoteXPC's ID makes Apple's
        # native reply handler abort the service on subsequent operations.
        self.service.service.next_message_id[1] = 1
        return await self.service.send_receive_request({'Cmd': 'FileSystemOperation', 'OperationType': kind,
                                                       'SessionID': self.service.session, **args})

    async def stat(self, path):
        if self.path(path) == '.' and getattr(self.service, 'domain', None) == Domain.APP_DATA_CONTAINER:
            await self._application_roots()
            return {'st_ifmt': 'S_IFDIR', 'st_size': 0, 'st_mtime': 0, 'readOnly': True}
        reply = await self.operation('GetAttributes', Path=self.path(path), FollowSymlinks=False)
        value = json.loads(reply['Attributes'])
        modified = value['modificationTime']
        size = value['size']
        if type(size) is not int or size < 0 or type(modified) not in (int, float) or not math.isfinite(modified):
            raise ValueError('Invalid CoreDevice attributes')
        result = {'st_ifmt': {'directory': 'S_IFDIR', 'regular': 'S_IFREG', 'symbolicLink': 'S_IFLNK'}.get(value['fileType'], 'other'),
                'st_size': size, 'st_mtime': modified + 978307200, 'permissions': value.get('permissions'),
                'flags': value.get('flags', 0), 'readOnly': True}
        if result['st_ifmt'] == 'S_IFLNK':
            result['LinkTarget'] = (await self.operation('ReadSymbolicLink', Path=self.path(path)))['TargetPath']
        return result

    async def listdir(self, path):
        if self.path(path) == '.' and getattr(self.service, 'domain', None) == Domain.APP_DATA_CONTAINER:
            return await self._application_roots()
        opened = await self.operation('OpenDirectory', Path=self.path(path))
        descriptor = opened['DirectoryDescriptor']
        names, seen = [], set()
        try:
            while True:
                result = await self.operation('ReadEntryWithAttributes', DirectoryDescriptor=XpcInt64Type(descriptor), MaxCount=XpcInt64Type(128))
                entries = result.get('Entries', [])
                if not isinstance(entries, list) or len(entries) > 128:
                    raise ValueError('Invalid CoreDevice directory page')
                if not entries:
                    return names
                for entry in entries:
                    name = entry['entry']['name']
                    if not isinstance(name, str) or not name or '/' in name or '\0' in name:
                        raise ValueError('Invalid CoreDevice directory name')
                    if name in ('.', '..'):
                        continue
                    if name in seen or len(names) >= 100000:
                        raise ValueError('CoreDevice directory changed or exceeds its limit')
                    names.append(name)
                    seen.add(name)
        finally:
            with suppress(Exception):
                await asyncio.wait_for(self.operation('CloseDirectory', DirectoryDescriptor=XpcInt64Type(descriptor)), 2)

    async def fopen(self, path, mode):
        if mode != 'r':
            raise PermissionError('CoreDevice mappings are read-only')
        reply = await self.operation('OpenFile', Path=self.path(path), Flags=XpcInt64Type(0), Mode=XpcUInt64Type(0o644))
        handle = reply['FileDescriptor']
        self.positions[handle] = 0
        return handle

    async def fseek(self, handle, offset):
        self.positions[handle] = offset

    async def fread(self, handle, count):
        if not 0 <= count <= 192 * 1024:
            raise ValueError('Invalid CoreDevice read size')
        reply = await self.operation('ReadFile', FileDescriptor=XpcInt64Type(handle),
                                     Offset=XpcUInt64Type(self.positions[handle]), Length=XpcInt64Type(count))
        data = reply['FileData']
        if not isinstance(data, bytes) or len(data) > count:
            raise ValueError('Invalid CoreDevice read')
        self.positions[handle] += len(data)
        return data

    async def fclose(self, handle):
        try:
            await self.operation('CloseFile', FileDescriptor=XpcInt64Type(handle))
        finally:
            self.positions.pop(handle, None)


@asynccontextmanager
async def coredevice_files(rsd, domain, identifier):
    if domain not in CORE_DOMAINS or (domain in ('temporary', 'systemCrashLogs') and identifier):
        raise ValueError('Invalid CoreDevice domain')
    rsd.require_feature(FileServiceService.CTRL_SERVICE_NAME, CORE_FEATURE)
    service = FileServiceService(rsd, CORE_DOMAINS[domain], identifier)
    try:
        await service.connect()
        yield CoreDeviceFiles(service)
    finally:
        # Own the service even if CreateSession or a cancelled read failed.
        if service.session is not None:
            with suppress(Exception):
                async with asyncio.timeout(2):
                    service.service.next_message_id[1] = 1
                    await service.send_receive_request({'Cmd': 'EndSession', 'SessionID': service.session})
        with suppress(Exception):
            await asyncio.wait_for(service.close(), 2)


async def readable(context, *, on_timeout=None, timeout=1.5):
    try:
        async with asyncio.timeout(timeout):
            async with context as files:
                return (await files.stat('/')).get('st_ifmt') == 'S_IFDIR' and isinstance(await files.listdir('/'), list)
    except TimeoutError:
        if on_timeout is not None:
            on_timeout()
        return False
    except Exception:
        return False


async def application_root(rsd, app, core_probe=None):
    identifier, name = app['bundleId'], app['name']
    if await readable(application_files(rsd, identifier, False)):
        return {'root': 'container', 'bundleId': identifier, 'name': name, 'readOnly': False}
    if await readable(application_files(rsd, identifier, True)):
        result = {'root': 'documents', 'bundleId': identifier, 'name': name, 'readOnly': False}
        if core_probe and await core_probe('appDataContainer', identifier):
            result['additionalRoots'] = [{'root': 'coredevice', 'domain': 'appDataContainer', 'identifier': identifier,
                                           'name': name + ' · Library, tmp', 'readOnly': True}]
        return result
    available = await core_probe('appDataContainer', identifier) if core_probe else await readable(coredevice_files(rsd, 'appDataContainer', identifier))
    if available:
        return {'root': 'coredevice', 'domain': 'appDataContainer', 'identifier': identifier, 'name': name, 'readOnly': True}
    return None
