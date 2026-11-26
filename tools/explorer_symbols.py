"""Bounded, pull-based access to the device's advertised shared-library files."""
import asyncio
from contextlib import suppress

from pymobiledevice3.services.remote_fetch_symbols import RemoteFetchSymbolsService
from pymobiledevice3.remote.xpc_message import XpcFlags


class SymbolFileCatalog:
    def __init__(self, rsd):
        self.rsd = rsd
        self.files = None
        self.directories = {''}
        self.service = None
        self.current = None
        self.position = 0
        self.buffer = b''
        self.idle = None
        self.reading = False

    def _touch(self):
        if self.idle is not None:
            self.idle.cancel()
        service = self.service
        self.idle = asyncio.get_running_loop().call_later(30, lambda: asyncio.create_task(self._close_idle(service)))

    async def _close_idle(self, service):
        if not self.reading and self.service is service:
            await self.close()

    async def _connect(self):
        if self.service is not None:
            self._touch()
            return
        service = RemoteFetchSymbolsService(self.rsd)
        try:
            await service.connect()
            entries = await service.get_dsc_file_list()
            if not isinstance(entries, list) or len(entries) > 5000:
                raise ValueError('Invalid shared-library catalog')
            files = {}
            for index, entry in enumerate(entries):
                path = entry.file_path
                if not isinstance(path, str) or not path.startswith('/') or any(part in ('', '.', '..') for part in path[1:].split('/')) or '\0' in path:
                    raise ValueError('Invalid shared-library path')
                if not isinstance(entry.file_size, int) or isinstance(entry.file_size, bool) or entry.file_size < 0 or entry.file_size > 2**53 - 1 or path[1:] in files:
                    raise ValueError('Invalid shared-library metadata')
                files[path[1:]] = (int(entry.file_size), index)
            if self.files is not None and {path: size for path, (size, _) in files.items()} != {path: size for path, (size, _) in self.files.items()}:
                raise OSError('Shared-library catalog changed; reconnect the mapping')
            self.files = files
            for path in files:
                parts = path.split('/')
                self.directories.update('/'.join(parts[:index]) for index in range(1, len(parts)))
            self.service = service
            self._touch()
        except BaseException:
            with suppress(Exception): await service.close()
            raise

    async def metadata(self, path):
        if self.files is None:
            await self._connect()
        path = path.strip('/')
        if path in self.directories:
            return {'st_ifmt': 'S_IFDIR', 'st_size': 0, 'st_mtime': 0, 'readOnly': True}
        if path not in self.files:
            raise FileNotFoundError(path)
        return {'st_ifmt': 'S_IFREG', 'st_size': self.files[path][0], 'st_mtime': 0, 'readOnly': True}

    async def listdir(self, path):
        value = await self.metadata(path)
        if value['st_ifmt'] != 'S_IFDIR':
            raise NotADirectoryError(path)
        path = path.strip('/')
        prefix = path + '/' if path else ''
        return sorted({name[len(prefix):].split('/')[0] for name in self.directories | self.files.keys()
                       if name.startswith(prefix) and len(name) > len(prefix)})

    async def _take(self, count):
        if not self.buffer:
            # The SDK's iter_file_chunks starts a reader with an unbounded
            # queue. Pull HTTP/2 frames directly so an idle mapped file cannot
            # download/cache several GiB in the background.
            stream_id = (self.files[self.current][1] + 1) * 2
            while True:
                frame = await self.service.service._receive_next_data_frame()
                if frame.stream_id == stream_id and frame.data:
                    if len(frame.data) > 1024 * 1024:
                        raise OSError('Oversized shared-library frame')
                    self.buffer = frame.data
                    break
                if frame.stream_id == stream_id and 'END_STREAM' in frame.flags:
                    raise OSError('Shared-library transfer ended early')
        data, self.buffer = self.buffer[:count], self.buffer[count:]
        self.position += len(data)
        return data

    async def read(self, path, offset, count):
        self.reading = True
        try:
            return await self._read(path, offset, count)
        finally:
            self.reading = False

    async def _read(self, path, offset, count):
        self._touch()
        path = path.strip('/')
        value = await self.metadata(path)
        if value['st_ifmt'] != 'S_IFREG' or not 0 <= offset <= value['st_size'] or not 0 <= count <= 192 * 1024:
            raise ValueError('Invalid shared-library read')
        count = min(count, value['st_size'] - offset)
        if count == 0:
            return b''
        try:
            if self.current != path or offset < self.position:
                await self.close()
                await self._connect()
                self.current, self.position, self.buffer = path, 0, b''
                await self.service.service._open_channel((self.files[path][1] + 1) * 2, XpcFlags.FILE_TX_STREAM_RESPONSE)
            # This service has no seek command. Sequential reads keep one
            # transfer; a backwards seek reopens it. Forward seeks discard
            # bounded pieces and remain cancellable without caching the file.
            while self.position < offset:
                await self._take(min(192 * 1024, offset - self.position))
            result = bytearray()
            while len(result) < count:
                result.extend(await self._take(count - len(result)))
            self._touch()
            return bytes(result)
        except BaseException:
            await self.close()
            raise

    async def close(self):
        if self.idle is not None:
            self.idle.cancel()
            self.idle = None
        service, self.service = self.service, None
        self.current, self.position, self.buffer = None, 0, b''
        if service is not None:
            with suppress(Exception):
                await asyncio.wait_for(service.close(), 2)


class SymbolFiles:
    def __init__(self, catalog):
        self.catalog = catalog
        self.handles = {}
        self.next_handle = 1

    async def stat(self, path): return await self.catalog.metadata(path)
    async def listdir(self, path): return await self.catalog.listdir(path)
    async def fopen(self, path, mode):
        if mode != 'r': raise PermissionError('Shared libraries are read-only')
        value = await self.stat(path)
        if value['st_ifmt'] != 'S_IFREG': raise IsADirectoryError(path)
        handle, self.next_handle = self.next_handle, self.next_handle + 1
        self.handles[handle] = (path, 0)
        return handle
    async def fseek(self, handle, offset): self.handles[handle] = (self.handles[handle][0], offset)
    async def fread(self, handle, count):
        path, offset = self.handles[handle]
        value = await self.catalog.read(path, offset, count)
        self.handles[handle] = (path, offset + len(value))
        return value
    async def fclose(self, handle): self.handles.pop(handle, None)
