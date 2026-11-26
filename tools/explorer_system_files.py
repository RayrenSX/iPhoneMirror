"""Read-only directory browsing through the device's Instruments service.

This service exposes directory names, not general file contents. Only entries
whose directory can actually be opened are included; no fictitious files.
"""
import asyncio
from contextlib import asynccontextmanager, suppress

from pymobiledevice3.services.dvt.instruments.device_info import DeviceInfo
from pymobiledevice3.services.dvt.instruments.dvt_provider import DvtProvider
from pymobiledevice3.exceptions import DvtDirListError


class ExplorerDvtProvider(DvtProvider):
    async def connect(self):
        if self._dtx is not None:
            return
        # Retain ownership before awaiting the handshake. The upstream provider
        # assigns _dtx afterwards and does not close it on CancelledError.
        self._dtx = await self._open_dtx_connection(self._service_name, strip_ssl=self._strip_ssl)
        self._dtx.sent_capabilities = self.sent_capabilities
        await self._dtx.connect()
        if self.REGISTER_SERVICES:
            self._dtx.register_services(*self.REGISTER_SERVICES)


class SystemDirectoryFiles:
    def __init__(self, info):
        self.info = info
        self.directories = {}

    async def _list(self, path):
        parts = path.strip('/').split('/') if path.strip('/') else []
        if any(part in ('', '.', '..') or '\0' in part for part in parts):
            raise ValueError('Invalid system directory path')
        path = '/' + '/'.join(parts)
        if path not in self.directories:
            # '/.' requires a directory even if an implementation otherwise
            # returns a value when directoryListingForPath is given a file.
            try:
                names = await self.info.ls(path.rstrip('/') + '/.')
            except DvtDirListError as error:
                raise FileNotFoundError('The device did not expose this directory') from error
            if not isinstance(names, list) or len(names) > 100000 or any(not isinstance(name, str) for name in names):
                raise ValueError('Invalid system directory response')
            self.directories[path] = names
        return self.directories[path]

    async def stat(self, path):
        await self._list(path)
        return {'st_ifmt': 'S_IFDIR', 'st_size': 0, 'st_mtime': 0, 'readOnly': True}

    async def listdir(self, path):
        names = await self._list(path)
        gate = asyncio.Semaphore(8)
        async def directory(name):
            if name in ('.', '..') or not name or '/' in name or '\0' in name:
                return None
            async with gate:
                try:
                    await self._list(path.rstrip('/') + '/' + name)
                    return name
                except asyncio.CancelledError:
                    raise
                except Exception:
                    return None
        # Batch without admitting an unbounded number of DTX requests/tasks.
        folders = []
        for offset in range(0, len(names), 64):
            folders.extend(name for name in await asyncio.gather(*(directory(name) for name in names[offset:offset + 64])) if name is not None)
        return folders


@asynccontextmanager
async def system_directories(rsd):
    provider = ExplorerDvtProvider(rsd)
    try:
        await provider.__aenter__()
        async with DeviceInfo(provider) as info:
            yield SystemDirectoryFiles(info)
    finally:
        with suppress(Exception):
            await asyncio.wait_for(provider.__aexit__(None, None, None), 2)
