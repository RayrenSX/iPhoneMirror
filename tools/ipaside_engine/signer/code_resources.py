"""``_CodeSignature/CodeResources``, from Tetherless ``CodeResourcesBuilder.swift``."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass
class FileEntry:
    relative_path: str
    sha1: bytes
    sha256: bytes


def build(entries: list[FileEntry]) -> dict:
    files: dict = {}
    files2: dict = {}
    for entry in entries:
        path = entry.relative_path
        is_lproj = ".lproj/" in path
        is_locversion = path.endswith(".lproj/locversion.plist")
        is_ds_store = path.endswith(".DS_Store")
        is_info = path == "Info.plist"
        is_pkg = path == "PkgInfo"
        if not is_locversion:
            if is_lproj:
                files[path] = {"hash": entry.sha1, "optional": True}
            else:
                files[path] = entry.sha1
        if not (is_locversion or is_ds_store or is_info or is_pkg):
            record: dict = {"hash": entry.sha1, "hash2": entry.sha256}
            if is_lproj:
                record["optional"] = True
            files2[path] = record
    return {
        "files": files,
        "files2": files2,
        "rules": {
            "^.*": True,
            "^.*\\.lproj/": {"optional": True, "weight": 1000.0},
            "^.*\\.lproj/locversion.plist$": {"omit": True, "weight": 1100.0},
            "^Base\\.lproj/": {"weight": 1010.0},
            "^version.plist$": True,
        },
        "rules2": {
            "^.*": True,
            ".*\\.dSYM($|/)": {"weight": 11.0},
            "^(.*/)?\\.DS_Store$": {"omit": True, "weight": 2000.0},
            "^.*\\.lproj/": {"optional": True, "weight": 1000.0},
            "^.*\\.lproj/locversion.plist$": {"omit": True, "weight": 1100.0},
            "^Base\\.lproj/": {"weight": 1010.0},
            "^Info\\.plist$": {"omit": True, "weight": 20.0},
            "^PkgInfo$": {"omit": True, "weight": 20.0},
            "^embedded\\.provisionprofile$": {"weight": 20.0},
            "^version\\.plist$": {"weight": 20.0},
        },
    }


def enumerate_bundle_files(bundle_root: Path, main_executable_name: str) -> list[str]:
    results: list[str] = []
    if not bundle_root.is_dir():
        return results
    for path in bundle_root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(bundle_root).as_posix()
        if relative in ("_CodeSignature/CodeResources", main_executable_name):
            continue
        results.append(relative)
    return results
