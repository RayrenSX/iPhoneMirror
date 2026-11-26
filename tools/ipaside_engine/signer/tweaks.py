"""Inject tweaks the way Tetherless does.

A tweak goes in ``Frameworks`` and is loaded weakly from the app executable.
If it links ``libsubstrate.dylib`` (or one of the other names ElleKit answers to),
the chosen CydiaSubstrate framework is copied in beside it and that load command
is rewritten to the copy.
"""

from __future__ import annotations

import os
from pathlib import Path, PurePosixPath

from ..paths import resource_dir
from .binary import SignError
from . import macho

_SUBSTRATE_NAMES = {
    "libsubstrate.dylib",
    "libhooker.dylib",
    "libblackjack.dylib",
    "libellekit.dylib",
    "cydiasubstrate",
}
_PROVIDER_PATH = "@executable_path/Frameworks/CydiaSubstrate.framework/CydiaSubstrate"
_PROVIDERS = {"legacy": "Legacy", "ellekit": "ElleKit"}


def is_substrate_reference(path: str) -> bool:
    name = PurePosixPath(path.replace("\\", "/")).name.lower()
    return name in _SUBSTRATE_NAMES


def provider_root() -> Path:
    override = os.environ.get("IPASIDE_SUBSTRATE_PROVIDERS")
    if override:
        return Path(override)
    return resource_dir() / "vendor" / "SubstrateProviders"


def provider_files(provider: str) -> list[tuple[str, bytes]]:
    folder = _PROVIDERS.get(provider)
    if folder is None:
        raise SignError(f"Unknown Substrate provider {provider}.")
    base = provider_root() / folder / "CydiaSubstrate.framework"
    files: list[tuple[str, bytes]] = []
    for name in ("Info.plist", "CydiaSubstrate"):
        path = base / name
        if not path.is_file():
            raise SignError(
                f"The {provider} Substrate framework is missing ({path}). "
                "This build is incomplete."
            )
        files.append((f"CydiaSubstrate.framework/{name}", path.read_bytes()))
    return files


def inject(
    read,
    write,
    app_root: str,
    main_executable: str,
    dylibs: list[str],
    *,
    provider: str,
    extra_executables: list[str] | None = None,
    weak: bool = True,
) -> list[str]:
    """Write tweaks under ``Frameworks`` and wire them into ``main_executable``."""
    if not dylibs:
        return []
    taken: set[str] = set()
    prepared: list[tuple[str, bytes]] = []
    needs_provider = False
    for entry in dylibs:
        source = Path(entry)
        if not source.is_file():
            raise SignError(f"Tweak not found: {source.name or entry}")
        data = source.read_bytes()
        if not macho.is_macho(data):
            raise SignError(f"{source.name} is not a Mach-O library.")
        filename = _sanitized(source.name, taken)
        taken.add(filename)
        if _needs_provider(data):
            needs_provider = True
            data = _rewrite_provider(data, _PROVIDER_PATH)
        prepared.append((filename, data))

    if needs_provider:
        for relative, blob in provider_files(provider):
            _write(write, f"{app_root}Frameworks/{relative}", blob)

    loaded: list[str] = []
    main = read(main_executable)
    for filename, blob in prepared:
        destination = f"{app_root}Frameworks/{filename}"
        _write(write, destination, blob)
        load_path = _load_path(app_root, main_executable, filename)
        main = macho.insert_load_dylib(main, load_path, weak=weak)
        loaded.append(load_path)
        for executable in extra_executables or []:
            extra = macho.insert_load_dylib(
                read(executable),
                _load_path(app_root, executable, filename),
                weak=weak,
            )
            _write(write, executable, extra)
    _write(write, main_executable, main)
    return loaded


def _needs_provider(data: bytes) -> bool:
    arches = macho.fat_architectures(data)
    if not arches:
        return macho.find_dylib_load_command(data, is_substrate_reference) is not None
    for arch in arches:
        piece = data[arch.offset:arch.offset + arch.size]
        if macho.find_dylib_load_command(piece, is_substrate_reference) is not None:
            return True
    return False


def _rewrite_provider(data: bytes, new_path: str) -> bytes:
    arches = macho.fat_architectures(data)
    if not arches:
        return macho.replace_dylib_load_command(data, is_substrate_reference, new_path, weak=False) or data
    slices = []
    for arch in arches:
        piece = data[arch.offset:arch.offset + arch.size]
        rewritten = macho.replace_dylib_load_command(piece, is_substrate_reference, new_path, weak=False)
        slices.append((arch.cpu_type, arch.cpu_subtype, arch.align, rewritten or piece))
    return macho.build_fat(slices)


def _sanitized(original: str, taken: set[str]) -> str:
    base = PurePosixPath(original.replace("\\", "/")).name or "tweak.dylib"
    if not base.lower().endswith(".dylib"):
        base += ".dylib"
    if base not in taken:
        return base
    stem = base[:-6]
    index = 2
    while True:
        candidate = f"{stem}-{index}.dylib"
        if candidate not in taken:
            return candidate
        index += 1


def _load_path(app_root: str, executable: str, filename: str) -> str:
    exe_dir = executable.rsplit("/", 1)[0]
    frameworks = f"{app_root}Frameworks"
    relative = os.path.relpath(frameworks, exe_dir).replace("\\", "/")
    return f"@executable_path/{relative}/{filename}"


def _write(write, name: str, data: bytes) -> None:
    write(name, data)
