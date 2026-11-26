"""Create, verify, and restore split tar archives (gzip, or optional fast Zstandard)."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import sys
import tarfile
import time

for dependency_directory in (Path(__file__).resolve().parent / 'python-deps',
                             Path(__file__).resolve().parent.parent / 'work/storage-maintenance/python-deps'):
    if dependency_directory.is_dir():
        sys.path.insert(0, str(dependency_directory))
try:
    import zstandard
except ImportError:
    zstandard = None


def write_json(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + '.pending')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, path)


class VolumesWriter:
    def __init__(self, directory: Path, limit: int, reserve: int, extension: str = 'gz'):
        self.directory, self.limit, self.reserve = directory, limit, reserve
        self.extension = extension
        self.parts = []
        self.stream = None
        self.size = self.total = self.checked_at = 0
        self.digest = None

    def finish_part(self):
        if self.stream is not None:
            self.stream.close()
            self.parts.append({'name': self.name, 'bytes': self.size, 'sha256': self.digest.hexdigest()})
            self.stream = None

    def write(self, data):
        length = len(data)
        while data:
            if self.stream is None:
                self.name = f'data.tar.{self.extension}.{len(self.parts) + 1:03d}'
                self.stream = (self.directory / self.name).open('xb', buffering=1024 * 1024)
                self.size = 0
                self.digest = hashlib.sha256()
            if self.total - self.checked_at >= 8 * 1024**2 or self.total == 0:
                if shutil.disk_usage(self.directory).free < self.reserve + 16 * 1024**2:
                    raise OSError('Archive drive is approaching its reserved free space; source retained.')
                self.checked_at = self.total
            chunk = data[: self.limit - self.size]
            self.stream.write(chunk)
            self.digest.update(chunk)
            self.size += len(chunk)
            self.total += len(chunk)
            data = data[len(chunk):]
            if self.size == self.limit:
                self.finish_part()
        return length

    def flush(self):
        if self.stream is not None:
            self.stream.flush()

    def close(self):
        self.finish_part()


class VolumesReader(io.RawIOBase):
    def __init__(self, directory: Path, parts: list[dict]):
        self.directory, self.parts = directory, parts
        self.index = 0
        self.stream = None

    def readable(self):
        return True

    def readinto(self, buffer):
        while self.index < len(self.parts):
            part = self.parts[self.index]
            if Path(part['name']).name != part['name']:
                raise ValueError('Unsafe volume filename.')
            if self.stream is None:
                self.stream = (self.directory / part['name']).open('rb', buffering=1024 * 1024)
                self.digest = hashlib.sha256()
                self.size = 0
            data = self.stream.read(len(buffer))
            if data:
                buffer[:len(data)] = data
                self.digest.update(data)
                self.size += len(data)
                return len(data)
            self.stream.close()
            self.stream = None
            if self.size != part['bytes'] or self.digest.hexdigest() != part['sha256']:
                raise ValueError(f'Archive volume failed SHA-256 verification: {part["name"]}')
            self.index += 1
        return 0

    def close(self):
        if self.stream is not None:
            self.stream.close()
            self.stream = None
        super().close()


class HashReader:
    def __init__(self, stream):
        self.stream, self.digest = stream, hashlib.sha256()

    def read(self, count):
        data = self.stream.read(count)
        self.digest.update(data)
        return data


def inventory(root: Path):
    files, directories = {}, []
    pending = [root]
    while pending:
        current = pending.pop()
        with os.scandir(current) as entries:
            for entry in entries:
                metadata = entry.stat(follow_symlinks=False)
                if metadata.st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                    raise ValueError(f'Junction or symbolic link found: {entry.path}')
                relative = Path(entry.path).relative_to(root).as_posix()
                if entry.is_dir(follow_symlinks=False):
                    directories.append(relative)
                    pending.append(Path(entry.path))
                elif entry.is_file(follow_symlinks=False):
                    files[relative] = {'bytes': metadata.st_size, 'mtime_ns': metadata.st_mtime_ns}
                else:
                    raise ValueError(f'Unsupported file type: {entry.path}')
    return files, sorted(directories)


def member_name(name: str) -> str:
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or '\\' in name or ':' in name:
        raise ValueError(f'Unsafe archive member: {name}')
    return '' if name in ('.', './') else path.as_posix().rstrip('/')


def read_archive(directory: Path, manifest: dict, destination: Path | None = None):
    expected = manifest['files']
    directories = set(manifest['directories']) | {''}
    seen_files, seen_directories = set(), set()
    codec = manifest.get('codec', 'gzip')
    if codec not in ('gzip', 'zstd'):
        raise ValueError('Unsupported archive codec.')
    if codec == 'zstd' and zstandard is None:
        raise ValueError('Install zstandard==0.25.0 to read this archive.')
    with VolumesReader(directory, manifest['parts']) as raw:
        with io.BufferedReader(raw, buffer_size=1024 * 1024) as buffered:
            decoder = gzip.GzipFile(fileobj=buffered, mode='rb') if codec == 'gzip' else zstandard.ZstdDecompressor().stream_reader(buffered, closefd=False, read_across_frames=True)
            with decoder as compressed:
                with tarfile.open(fileobj=compressed, mode='r|') as archive:
                    for member in archive:
                        name = member_name(member.name)
                        if member.isdir():
                            if name not in directories or name in seen_directories:
                                raise ValueError(f'Unexpected directory: {name}')
                            seen_directories.add(name)
                            if destination is not None:
                                (destination / name).mkdir(parents=True, exist_ok=True)
                            continue
                        if not member.isfile() or name not in expected or name in seen_files:
                            raise ValueError(f'Unexpected or unsafe file: {name}')
                        record = expected[name]
                        if member.size != record['bytes']:
                            raise ValueError(f'File size differs: {name}')
                        digest, count = hashlib.sha256(), 0
                        output = None
                        if destination is not None:
                            target = destination / name
                            # Reject existing links in every parent before writing.
                            current = destination
                            for part in PurePosixPath(name).parts[:-1]:
                                current /= part
                                if current.is_symlink() or current.is_junction():
                                    raise ValueError(f'Restore path passes through a link: {current}')
                            target.parent.mkdir(parents=True, exist_ok=True)
                            output = target.open('xb')
                        try:
                            with archive.extractfile(member) as source:
                                while data := source.read(1024 * 1024):
                                    digest.update(data)
                                    count += len(data)
                                    if output is not None:
                                        output.write(data)
                        finally:
                            if output is not None:
                                output.close()
                        if count != record['bytes'] or digest.hexdigest() != record['sha256']:
                            raise ValueError(f'File failed SHA-256 verification: {name}')
                        if destination is not None:
                            os.utime(destination / name, ns=(record['mtime_ns'], record['mtime_ns']))
                            os.chmod(destination / name, member.mode)
                        seen_files.add(name)
                # Read the gzip footer and the final volume checksums as well as tar members.
                while compressed.read(1024 * 1024):
                    pass
    if seen_files != set(expected) or seen_directories != directories:
        raise ValueError('Archive is missing files or empty directories.')


def pack(source: Path, destination: Path, volume_bytes: int, reserve_bytes: int, codec: str = 'auto'):
    if source.is_symlink() or source.is_junction():
        raise ValueError('Archive source must be a local directory.')
    before, directories = inventory(source)
    destination.mkdir(parents=True, exist_ok=False)
    if codec == 'auto':
        codec = 'zstd' if zstandard is not None else 'gzip'
    if codec == 'zstd' and zstandard is None:
        raise ValueError('Install zstandard==0.25.0 to use fast compression.')
    writer = VolumesWriter(destination, volume_bytes, reserve_bytes, 'zst' if codec == 'zstd' else 'gz')
    started = last_progress = time.monotonic()
    original_bytes = sum(record['bytes'] for record in before.values())
    processed = 0
    try:
        if codec == 'gzip':
            encoder = gzip.GzipFile(fileobj=writer, mode='wb', compresslevel=1, mtime=0, filename='')
        else:
            parameters = zstandard.ZstdCompressionParameters.from_level(1, window_log=27, enable_ldm=True, threads=4, write_checksum=True)
            encoder = zstandard.ZstdCompressor(compression_params=parameters).stream_writer(writer, closefd=False)
        with encoder as compressed:
            with tarfile.open(fileobj=compressed, mode='w|', format=tarfile.PAX_FORMAT, dereference=True) as archive:
                archive.addfile(archive.gettarinfo(str(source), arcname='.'))
                for name in directories:
                    archive.addfile(archive.gettarinfo(str(source / name), arcname=name))
                # Keep repeated runtime DLLs adjacent so compression can reuse their bytes.
                ordered_files = sorted(before.items(), key=lambda item: (Path(item[0]).name.casefold(), item[1]['bytes'], item[0].casefold()))
                for name, record in ordered_files:
                    path = source / name
                    metadata = path.stat()
                    if metadata.st_size != record['bytes'] or metadata.st_mtime_ns != record['mtime_ns']:
                        raise ValueError(f'Source changed before packing: {name}')
                    with path.open('rb') as stream:
                        reader = HashReader(stream)
                        archive.addfile(archive.gettarinfo(str(path), arcname=name), reader)
                        record['sha256'] = reader.digest.hexdigest()
                    processed += record['bytes']
                    if time.monotonic() - last_progress >= 20:
                        print(f'Packing {source.name}: {processed / 1024**3:.2f}/{original_bytes / 1024**3:.2f} GiB', flush=True)
                        last_progress = time.monotonic()
    finally:
        writer.close()
    after, after_directories = inventory(source)
    if after_directories != directories or after != {name: {k: v for k, v in record.items() if k != 'sha256'} for name, record in before.items()}:
        raise ValueError('Source changed during packing; source retained.')
    manifest = {'format': 'iphoneMirror-split-tar-v1', 'codec': codec, 'source': str(source), 'original_bytes': original_bytes,
                'files': before, 'directories': directories, 'parts': writer.parts, 'verified': False}
    write_json(destination / 'manifest.json', manifest)
    print(f'Verifying {source.name}: {len(before)} files, {writer.total / 1024**3:.2f} GiB compressed', flush=True)
    read_archive(destination, manifest)
    manifest['verified'] = True
    write_json(destination / 'manifest.json', manifest)
    summary = {'source': str(source), 'original_bytes': original_bytes, 'compressed_bytes': writer.total,
               'files': len(before), 'volumes': len(writer.parts), 'verified': True, 'codec': codec,
               'elapsed_seconds': round(time.monotonic() - started, 2)}
    write_json(destination / 'summary.json', summary)
    print(json.dumps(summary), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    create = sub.add_parser('pack')
    create.add_argument('--source', type=Path, required=True)
    create.add_argument('--destination', type=Path, required=True)
    create.add_argument('--volume-bytes', type=int, default=2 * 1024**3)
    create.add_argument('--reserve-bytes', type=int, default=4 * 1024**3)
    create.add_argument('--codec', choices=('auto', 'gzip', 'zstd'), default='auto')
    for name in ('verify', 'restore'):
        operation = sub.add_parser(name)
        operation.add_argument('--manifest', type=Path, required=True)
        if name == 'restore':
            operation.add_argument('--destination', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'pack':
        if not 1024 <= args.volume_bytes <= 4095 * 1024**2:
            parser.error('Volume size must be between 1 KiB and 4095 MiB.')
        if args.source.is_symlink() or args.source.is_junction():
            raise ValueError('Archive source must not be a link.')
        pack(args.source.resolve(), args.destination.resolve(), args.volume_bytes, args.reserve_bytes, args.codec)
    else:
        manifest = json.loads(args.manifest.read_text(encoding='utf-8'))
        if args.command == 'restore' and (args.destination.is_symlink() or args.destination.is_junction()):
            raise ValueError('Restore destination must not be a link.')
        destination = args.destination.resolve() if args.command == 'restore' else None
        if destination is not None:
            if destination.is_symlink() or destination.is_junction():
                raise ValueError('Restore destination must not be a link.')
            if destination.exists() and any(p.name != 'WORKSPACE-ARCHIVE.json' for p in destination.iterdir()):
                raise ValueError('Restore destination must be empty or contain only its archive pointer.')
            destination.mkdir(parents=True, exist_ok=True)
        read_archive(args.manifest.parent, manifest, destination)
        print('Archive files and volumes passed SHA-256 verification.', flush=True)


if __name__ == '__main__':
    main()
