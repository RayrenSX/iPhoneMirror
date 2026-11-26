"""A Windows-compatible name view over a device's existing file-service root.

Resolve names against each live directory, never by unescaping user input.
Readable links stay within this service root and are exposed read-only.
"""
import hashlib
import unicodedata


def windows_name(name):
    invalid = '<>:"/\\|?*'
    value = ''.join(''.join(f'%{byte:02X}' for byte in char.encode('utf-8'))
                    if char in invalid or unicodedata.category(char) == 'Cc' else char for char in name)
    while value.endswith(('.', ' ')):
        value = value[:-1] + ('%2E' if value[-1] == '.' else '%20')
    stem = value.split('.')[0].rstrip(' ').upper()
    if stem in ('CON', 'PRN', 'AUX', 'NUL') or len(stem) == 4 and stem[:3] in ('COM', 'LPT') and stem[3] in '0123456789¹²³':
        value = f'%{ord(value[0]):02X}' + value[1:]
    if len(value.encode('utf-8')) > 200:
        value = value.encode('utf-8')[:180].decode('utf-8', 'ignore') + '~' + hashlib.sha256(name.encode('utf-8')).hexdigest()[:12]
    return value


def name_map(names):
    result, groups = {}, {}
    for name in sorted(set(names)):
        if name in ('.', '..') or not name or '/' in name or '\0' in name:
            continue
        basis = windows_name(name)
        groups.setdefault(basis.casefold(), []).append((basis, name))
    for group in groups.values():
        for basis, name in group:
            alias = basis if len(group) == 1 else basis + '~' + hashlib.sha256(name.encode('utf-8')).hexdigest()[:12]
            # A literal device name may match another entry's generated suffix.
            while alias.casefold() in result:
                alias += '~'
            result[alias.casefold()] = (alias, name)
    return result


class ExplorerNameView:
    def __init__(self, service):
        self.service = service
        self.names = {}

    async def _names(self, parent, refresh=False):
        if refresh or parent not in self.names:
            self.names[parent] = name_map(await self.service.listdir(parent))
        return self.names[parent]

    async def _follow(self, parts, visited, write):
        pending, resolved, linked = list(parts), [], False
        while pending:
            resolved.append(pending.pop(0))
            current = '/' + '/'.join(resolved)
            value = await self.service.stat(current)
            if value.get('st_ifmt') != 'S_IFLNK':
                continue
            if write:
                raise PermissionError('Mapped links are read-only')
            if current in visited or len(visited) >= 32:
                raise PermissionError('A device link forms a cycle')
            visited.add(current)
            target = value.get('LinkTarget')
            if not isinstance(target, str) or not target or '\0' in target:
                raise PermissionError('The device link has no readable target')
            replacement = [] if target.startswith('/') else resolved[:-1]
            for component in target.split('/'):
                if component in ('', '.'):
                    continue
                if component == '..':
                    if not replacement:
                        raise PermissionError('The device link leaves the exposed root')
                    replacement.pop()
                else:
                    replacement.append(component)
            pending, resolved, linked = replacement + pending, [], True
        return resolved, linked

    async def _resolve(self, path, *, create=False, write=False):
        parts = path.strip('/').split('/') if path.strip('/') else []
        if any(part in ('', '.', '..') or '\\' in part or ':' in part or '\0' in part for part in parts):
            raise ValueError('Invalid mapped path')
        native, linked, visited = [], False, set()
        for index, part in enumerate(parts):
            parent = '/' + '/'.join(native)
            names = await self._names(parent)
            selected = names.get(part.casefold())
            if selected is None:
                selected = (await self._names(parent, refresh=True)).get(part.casefold())
            if selected is None:
                if not create or index != len(parts) - 1:
                    raise FileNotFoundError(path)
                native.append(part)
                break
            native.append(selected[1])
            native, followed = await self._follow(native, visited, write)
            linked |= followed
        if write and linked:
            raise PermissionError('Mapped links are read-only')
        return '/' + '/'.join(native), linked

    async def stat(self, path):
        native, linked = await self._resolve(path)
        value = await self.service.stat(native)
        return {**value, **({'readOnly': True} if linked else {})}

    async def listdir(self, path):
        native, _ = await self._resolve(path)
        return [alias for alias, _ in (await self._names(native, refresh=True)).values()]

    async def fopen(self, path, mode):
        native, _ = await self._resolve(path, create=mode != 'r', write=mode != 'r')
        return await self.service.fopen(native, mode)

    async def rename(self, source, target):
        source, _ = await self._resolve(source, write=True)
        target, _ = await self._resolve(target, create=True, write=True)
        result = await self.service.rename(source, target)
        self.names.clear()
        return result

    async def makedirs(self, path):
        native, _ = await self._resolve(path, create=True, write=True)
        result = await self.service.makedirs(native)
        self.names.clear()
        return result

    async def rm_single(self, path, **kwargs):
        native, _ = await self._resolve(path, write=True)
        result = await self.service.rm_single(native, **kwargs)
        self.names.clear()
        return result

    async def _do_operation(self, opcode, payload):
        from pymobiledevice3.services.afc import AfcOpcode
        if opcode != AfcOpcode.SET_FILE_TIME or len(payload) < 10 or payload[-1:] != b'\0':
            raise ValueError('Unsupported mapped operation')
        native, _ = await self._resolve(payload[8:-1].decode('utf-8'), write=True)
        return await self.service._do_operation(opcode, payload[:8] + native.encode('utf-8') + b'\0')

    def __getattr__(self, name):
        if name not in ('fread', 'fwrite', 'fseek', 'fclose'):
            raise AttributeError(name)
        return getattr(self.service, name)
