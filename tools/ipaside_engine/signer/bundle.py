"""Find embedded frameworks and app extensions, and hash a bundle's resources.

Transcribed from Tetherless ``BundleScanner.swift``. A nested bundle is any
``.framework`` or ``.appex`` with its own Info.plist, at any depth. Its own
files are also hashed into the parent bundle's CodeResources.
"""

from __future__ import annotations

import hashlib
import plistlib
from dataclasses import dataclass

from .code_resources import FileEntry


@dataclass
class NestedBundle:
    root_prefix: str
    executable_path: str
    info_plist: bytes
    identifier: str
    is_extension: bool


def find_nested_bundles(app_root: str, paths: set[str], read) -> list[NestedBundle]:
    found: list[NestedBundle] = []
    for path in paths:
        if not path.startswith(app_root) or path == app_root + "Info.plist":
            continue
        if "/EmbeddedPayload/" in path or not path.endswith("/Info.plist"):
            continue
        folder = path[: -len("Info.plist")].rstrip("/").rsplit("/", 1)[-1]
        if not folder.endswith((".framework", ".appex", ".app")):
            continue
        data = read(path)
        if not data:
            continue
        try:
            info = plistlib.loads(data)
        except Exception:
            continue
        executable = info.get("CFBundleExecutable")
        if not isinstance(executable, str) or not executable:
            continue
        root = path[: -len("Info.plist")]
        executable_path = root + executable
        if executable_path not in paths:
            continue
        identifier = info.get("CFBundleIdentifier")
        if not isinstance(identifier, str) or not identifier:
            identifier = folder
        found.append(NestedBundle(root, executable_path, data, identifier, folder.endswith((".appex", ".app"))))
    return found


def code_resource_entries(root_prefix: str, exclude: set[str], paths: set[str], read) -> list[FileEntry]:
    entries: list[FileEntry] = []
    for path in paths:
        if not path.startswith(root_prefix) or path in exclude or path.endswith("/"):
            continue
        relative = path[len(root_prefix):]
        if not relative or relative == "_CodeSignature/CodeResources":
            continue
        data = read(path)
        entries.append(FileEntry(relative, hashlib.sha1(data).digest(), hashlib.sha256(data).digest()))
    return entries
