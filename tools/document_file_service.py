"""AFC view restricted to an application's shared Documents subtree."""
from pymobiledevice3.services.house_arrest import HouseArrestService, VEND_DOCUMENTS


class DocumentsFiles:
    def __init__(self, service): self.service = service

    @staticmethod
    def path(value):
        parts = value.strip('/').split('/') if value.strip('/') else []
        if any(p in ('', '.', '..') or '\\' in p or ':' in p or '\0' in p for p in parts):
            raise ValueError('Invalid document path')
        return '/Documents' + ('/' + '/'.join(parts) if parts else '')

    async def stat(self, path): return await self.service.stat(self.path(path))
    async def listdir(self, path): return await self.service.listdir(self.path(path))
    async def fopen(self, path, mode): return await self.service.fopen(self.path(path), mode)
    async def rename(self, source, target): return await self.service.rename(self.path(source), self.path(target))
    async def makedirs(self, path): return await self.service.makedirs(self.path(path))
    async def rm_single(self, path, **kwargs): return await self.service.rm_single(self.path(path), **kwargs)
    async def fwrite(self, *args): return await self.service.fwrite(*args)
    async def fread(self, *args): return await self.service.fread(*args)
    async def fseek(self, *args): return await self.service.fseek(*args)
    async def fclose(self, *args): return await self.service.fclose(*args)
    async def _do_operation(self, opcode, payload):
        from pymobiledevice3.services.afc import AfcOpcode
        if opcode != AfcOpcode.SET_FILE_TIME or len(payload) < 10 or payload[-1:] != b'\0':
            raise ValueError('Unsupported document operation')
        path = payload[8:-1].decode('utf-8')
        return await self.service._do_operation(opcode, payload[:8] + self.path(path).encode('utf-8') + b'\0')
