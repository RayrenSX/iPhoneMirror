"""Read-only binary/inventory audit; run using the bridge build environment.

This does not equate static dependency closure with device functional testing.
"""
import argparse
import collections
import hashlib
import json
import os
import struct
import zlib
from pathlib import Path

import pefile
from PyInstaller.archive.readers import CArchiveReader


def dotnet_bundle(path):
    """Read the .NET 6+ single-file bundle index, not the PE import table."""
    data = path.read_bytes()
    signature = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
    marker = data.find(signature)
    if marker < 8:
        raise ValueError(f'No .NET bundle marker in {path}')
    position, = struct.unpack_from('<q', data, marker - 8)

    def read(fmt):
        nonlocal position
        values = struct.unpack_from(fmt, data, position)
        position += struct.calcsize(fmt)
        return values

    def string():
        length = shift = 0
        for _ in range(5):
            value, = read('<B')
            length |= (value & 127) << shift
            if not value & 128:
                break
            shift += 7
        else:
            raise ValueError('Invalid bundle string length')
        nonlocal position
        result = data[position:position + length].decode('utf-8')
        position += length
        return result

    major, minor, count = read('<III')
    if major != 6 or count > 10000:
        raise ValueError(f'Unsupported .NET bundle version/count: {major}.{minor}/{count}')
    identifier = string()
    read('<qqqqQ')  # deps/runtimeconfig locations and flags
    entries = {}
    for _ in range(count):
        offset, size, compressed, kind = read('<qqqB')
        name = string()
        if offset < 0 or size < 0 or offset + (compressed or size) > len(data):
            raise ValueError('Invalid bundle entry bounds')
        content = data[offset:offset + (compressed or size)]
        if compressed:
            content = zlib.decompress(content, -15)
        if len(content) != size:
            raise ValueError('Bundle decompression length mismatch')
        entries[name] = {'size': size, 'compressed_size': compressed, 'type': kind,
                         'sha256': hashlib.sha256(content).hexdigest()}
    return {'id': identifier, 'version': f'{major}.{minor}', 'bytes': len(data), 'entries': entries}


def bridge_inventory(root):
    files = [root / 'iUsbBridge.exe', *sorted((root / '_internal').rglob('*'))]
    return {str(p.relative_to(root)).replace('\\', '/'): p.stat().st_size
            for p in files if p.is_file()}


def bridge_modules(root):
    archive = CArchiveReader(str(root / 'iUsbBridge.exe'))
    pyz_name = next(name for name, entry in archive.toc.items() if entry[-1] == 'z')
    return set(archive.open_embedded_archive(pyz_name).toc)


def image(path):
    pe = pefile.PE(str(path), fast_load=True)
    pe.parse_data_directories(directories=[
        pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_IMPORT'],
        pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXPORT']])
    return pe


def airplay_import_audit(root):
    directory = root / 'third_party/airplay-server/bin/x64'
    runtime = ['avcodec-58.dll', 'avutil-56.dll', 'swresample-3.dll', 'swscale-5.dll']
    dlls = {name: image(directory / name) for name in runtime}
    consumers = {**dlls, 'airplay2dll.dll': image(directory / 'airplay2dll.dll')}
    required, missing = {}, []
    for consumer, pe in consumers.items():
        for imported in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
            name = imported.dll.decode().lower()
            if name not in dlls:
                continue
            exported = {entry.name for entry in dlls[name].DIRECTORY_ENTRY_EXPORT.symbols}
            ordinals = {entry.ordinal for entry in dlls[name].DIRECTORY_ENTRY_EXPORT.symbols}
            symbols = []
            for entry in imported.imports:
                symbol = entry.name.decode() if entry.name else f'ordinal:{entry.ordinal}'
                symbols.append(symbol)
                if (entry.name not in exported if entry.name else entry.ordinal not in ordinals):
                    missing.append(f'{consumer}->{name}:{symbol}')
            required[f'{consumer}->{name}'] = sorted(symbols)
    result = {'required_exports': required, 'missing_exports': missing,
              'machines': {name: hex(pe.FILE_HEADER.Machine) for name, pe in consumers.items()},
              'imports': {name: [entry.dll.decode() for entry in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', [])]
                          for name, pe in consumers.items()}}
    for pe in consumers.values():
        pe.close()
    return result


def native_tree_audit(root):
    if not root.is_dir():
        raise ValueError(f'Native runtime directory does not exist: {root}')
    files = [path for path in root.rglob('*') if path.suffix.lower() in ('.exe', '.dll', '.pyd')]
    if not files:
        raise ValueError(f'Native runtime directory contains no PE files: {root}')
    names = {path.name.lower() for path in files}
    windows = Path(os.environ['SystemRoot']) / 'System32'
    native, managed, wrong_arch, auxiliary_arch, unresolved = 0, 0, [], [], []
    for path in files:
        pe = pefile.PE(str(path), fast_load=True)
        try:
            # AnyCPU IL assemblies can have the i386 PE marker; this is not an
            # x86 native dependency. Audit native machine types separately.
            if pe.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress:
                managed += 1
                continue
            native += 1
            if pe.FILE_HEADER.Machine != 0x8664:
                relative = str(path.relative_to(root)).replace('\\', '/')
                # Upstream Wintun intentionally supplies four architectures;
                # get_python_arch() selects amd64 for the bundled x64 Python.
                if relative in [f'tools/_internal/pytun_pmd3/wintun/bin/{arch}/wintun.dll'
                                for arch in ('arm', 'arm64', 'x86')]:
                    auxiliary_arch.append({'path': relative, 'machine': hex(pe.FILE_HEADER.Machine)})
                else:
                    wrong_arch.append(relative)
            pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_IMPORT']])
            for entry in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', []):
                name = entry.dll.decode().lower()
                if name not in names and not name.startswith(('api-ms-win-', 'ext-ms-win-')) and not (windows / name).is_file():
                    unresolved.append(f'{path.relative_to(root)}->{name}')
        finally:
            pe.close()
    return {'native_pe_checked': native, 'managed_pe_files': managed,
            'auxiliary_architecture_resources': auxiliary_arch,
            'unexpected_native_architecture': wrong_arch, 'unresolved_direct_imports': unresolved}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--before', type=Path, required=True)
    parser.add_argument('--after', type=Path, required=True)
    parser.add_argument('--driver-before', type=Path)
    parser.add_argument('--driver-after', type=Path)
    parser.add_argument('--portable-app', type=Path)
    parser.add_argument('--installer-directory', type=Path)
    args = parser.parse_args()
    before, after = bridge_inventory(args.before), bridge_inventory(args.after)
    removed = {name: size for name, size in before.items() if name not in after}
    removed_groups = collections.Counter()
    for name, size in removed.items():
        removed_groups[name.split('/')[1] if name.startswith('_internal/') else name] += size
    before_modules, after_modules = bridge_modules(args.before), bridge_modules(args.after)
    ffmpeg = args.root / 'src/App/native/tools/ffmpeg/ffmpeg.exe'
    with ffmpeg.open('rb') as stream:
        ffmpeg_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
    result = {
        'bridge': {
            'before_path': str(args.before.resolve()), 'after_path': str(args.after.resolve()),
            'before_bytes_excluding_manifest': sum(before.values()),
            'after_bytes_excluding_manifest': sum(after.values()),
            'before_files': len(before), 'after_files': len(after),
            'removed_files': removed, 'removed_file_groups': dict(removed_groups),
            'added_files': {name: size for name, size in after.items() if name not in before},
            'before_frozen_modules': len(before_modules), 'after_frozen_modules': len(after_modules),
            'removed_frozen_modules': sorted(before_modules - after_modules),
            'added_frozen_modules': sorted(after_modules - before_modules),
        },
        'airplay': airplay_import_audit(args.root),
        'ffmpeg': {'bytes': ffmpeg.stat().st_size, 'sha256': ffmpeg_hash},
    }
    if args.driver_before and args.driver_after:
        old, new = dotnet_bundle(args.driver_before), dotnet_bundle(args.driver_after)
        result['driver'] = {'before': old, 'after': new,
                            'removed': sorted(set(old['entries']) - set(new['entries'])),
                            'added': sorted(set(new['entries']) - set(old['entries'])),
                            'changed': sorted(name for name in set(new['entries']) & set(old['entries'])
                                              if old['entries'][name]['sha256'] != new['entries'][name]['sha256'])}
    if args.portable_app and args.installer_directory:
        bundle = dotnet_bundle(args.portable_app)
        missing, changed = [], []
        for name, entry in bundle['entries'].items():
            installed = args.installer_directory / name
            if not installed.is_file():
                missing.append(name)
                continue
            with installed.open('rb') as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != entry['sha256']:
                    changed.append(name)
        result['app_bundle_to_installer'] = {'bundle_bytes': bundle['bytes'],
            'bundled_files': len(bundle['entries']), 'missing_in_installer': missing,
            'changed_in_installer': changed}
        result['installer_native'] = native_tree_audit(args.installer_directory)
        result['uxplay_native'] = native_tree_audit(args.root / 'outputs/uxplay-component-runtime')
    print(json.dumps(result, indent=2))
    failures = []
    if result['airplay']['missing_exports']:
        failures.append('AirPlay runtime has missing imported exports')
    for key in ('installer_native', 'uxplay_native'):
        audit = result.get(key, {})
        if audit.get('unexpected_native_architecture') or audit.get('unresolved_direct_imports'):
            failures.append(f'{key} has unexpected architectures or unresolved direct imports')
    bundle_audit = result.get('app_bundle_to_installer', {})
    # Single-file and loose-file deployment intentionally produce different
    # deps metadata, but all actual assemblies/resources must remain identical.
    unexpected_changes = set(bundle_audit.get('changed_in_installer', [])) - {'iPhoneMirror.deps.json'}
    if bundle_audit.get('missing_in_installer') or unexpected_changes:
        failures.append('Portable bundle and installer payload differ unexpectedly')
    if failures:
        raise SystemExit('; '.join(failures))


if __name__ == '__main__':
    main()
