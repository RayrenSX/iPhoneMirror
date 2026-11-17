"""Bounded device services on the bridge's selected RSD; no discovery or retries."""
from __future__ import annotations

import asyncio
import base64
from collections import deque
from contextlib import asynccontextmanager, suppress
from datetime import datetime
import io
import json
import math
import os
from pathlib import Path
import re
import stat
import struct
import time
import uuid
import zipfile

from pymobiledevice3.remote.core_device.app_service import AppServiceService
from pymobiledevice3.remote.core_device.core_device_service import CoreDeviceService
from pymobiledevice3.exceptions import AfcException, AfcFileNotFoundError
from pymobiledevice3.services.afc import AfcOpcode, AfcService
from document_file_service import DocumentsFiles, HouseArrestService, VEND_DOCUMENTS
from pymobiledevice3.services.diagnostics import DiagnosticsService
from pymobiledevice3.services.installation_proxy import InstallationProxyService
from pymobiledevice3.services.misagent import MisagentService
from pymobiledevice3.services.mobile_config import MobileConfigService
from pymobiledevice3.services.springboard import SpringBoardServicesService
from pymobiledevice3.services.syslog import SyslogService


CONTRACT = json.loads(Path(__file__).with_name("device_hub_contract.json").read_text(encoding="utf-8"))
ACTIONS = CONTRACT["actions"]
CHUNK = 192 * 1024
MAX_IPA = 512 * 1024 * 1024
MAX_PROFILE = 8 * 1024 * 1024
CONFIG = "com.apple.coredevice.configuration"
LOCATION = "com.apple.coredevice.locationservice"
PREFIX = "com.apple.coredevice.action."
SETTINGS = {
    "showBorders": ("getshowborders", "setshowborders", "showBorders"),
    "increaseContrast": ("getdeviceincreasecontrast", "setdeviceincreasecontrast", "increaseContrast"),
    "reduceMotion": ("getreducemotion", "setreducemotion", "reduceMotion"),
    "reduceTransparency": ("getreducetransparency", "setreducetransparency", "reduceTransparency"),
    "voiceOver": ("getvoiceover", "setvoiceover", "voiceOverConfiguration"),
    "colorFilter": ("getcolorfilter", "setcolorfilter", "colorFilter"),
    "liquidGlass": ("getliquidglassconfiguration", "setliquidglassconfiguration", "configuration"),
    "textSize": ("getdevicetextsize", "setdevicetextsize", "textSize"),
    "appearance": ("getuserinterfacestyle", "setuserinterfacestyle", "style"),
    "lookAndFeel": ("getdevicelookandfeel", None, None),
    "supportedLooksAndFeels": ("getsupportedlooksandfeels", None, None),
    "largerAccessibilitySizes": ("getlargeraccessibilitysizesenabled", "setlargeraccessibilitysizesenabled", "enabled"),
}
TEXT_SIZES = ("extraSmall", "small", "medium", "large", "extraLarge", "extraExtraLarge",
              "extraExtraExtraLarge", "accessibilityMedium", "accessibilityLarge",
              "accessibilityExtraLarge", "accessibilityExtraExtraLarge", "accessibilityExtraExtraExtraLarge")


class HubError(RuntimeError):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


@asynccontextmanager
async def connected(factory, *args, **kwargs):
    service = factory(*args, **kwargs)
    try:
        entered = await service.__aenter__()
        yield entered if entered is not None else service
    finally:
        # Also close a service whose initial handshake failed or was cancelled.
        with suppress(Exception):
            await asyncio.wait_for(service.__aexit__(None, None, None), 2)


def bundle_id(value):
    if not isinstance(value, str) or len(value) > 512 or not re.fullmatch(r"[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+", value):
        raise ValueError("Invalid bundleId")
    return value


def integer(value, low, high):
    if type(value) is not int or not low <= value <= high:
        raise ValueError("Integer out of range")
    return value


def string(value, limit=512):
    if not isinstance(value, str) or not value or len(value) > limit or any(ord(c) < 32 for c in value):
        raise ValueError("Invalid text")
    return value


def json_value(value, depth=0):
    """Never let opaque XPC objects or unbounded service data reach the IPC writer."""
    if depth > 8:
        return None
    if value is None or isinstance(value, (str, bool, int, float)):
        if isinstance(value, str):
            return value[:8192]
        if isinstance(value, float) and not math.isfinite(value):
            return None
        return value
    if isinstance(value, (datetime, uuid.UUID)):
        return str(value)
    if isinstance(value, dict):
        return {str(k)[:256]: json_value(v, depth + 1) for k, v in list(value.items())[:128] if not isinstance(v, bytes)}
    if isinstance(value, (list, tuple)):
        return [json_value(v, depth + 1) for v in value[:512]]
    return str(value)[:256]


def relative_path(value):
    if not isinstance(value, str) or len(value) > 2048 or value.startswith("/") or any(c in value for c in ("\\", ":", "\0")):
        raise ValueError("Use a path relative to the selected device file service")
    parts = value.split("/") if value else []
    if any(p in (".", "..") or not p or any(ord(c) < 32 for c in p) for p in parts):
        raise ValueError("Invalid file path")
    return "/".join(parts)


def setting_input(key, value):
    if key not in SETTINGS or not SETTINGS[key][1]:
        raise ValueError("Setting is not writable")
    wrapper = SETTINGS[key][2]
    if key == "appearance":
        if value not in ("light", "dark"):
            raise ValueError("Appearance must be light or dark")
        return {"style": value}
    if key == "liquidGlass":
        if type(value) not in (int, float) or not math.isfinite(value) or not 0 <= value <= 1:
            raise ValueError("Opacity must be between 0 and 1")
        return {"configuration": {"opacity": float(value)}}
    if key == "textSize":
        if value not in TEXT_SIZES:
            raise ValueError("Unknown text size")
        return {"textSize": {"size": {value: {}}}}
    if type(value) is not bool:
        raise ValueError("Setting requires a boolean")
    return {"enabled": value} if wrapper == "enabled" else {wrapper: {"enabled": value}}


class DeviceManagement:
    def __init__(self, owner):
        self.owner = owner
        self.log_task = None
        self.log_service = None
        self.log_stream = None
        self.log_lines = deque(maxlen=2048)
        self.log_seq = 0
        self.log_error = None
        self.log_activity = 0

    @property
    def rsd(self):
        return self.owner.rsd

    async def request(self, action, args):
        handler = getattr(self, "_" + action, None)
        if handler is None:
            raise ValueError("Unknown device operation")
        return await handler(args)

    async def _capabilities(self, args):
        services = getattr(self.rsd, "peer_info", {}).get("Services", {})
        return {"contract": CONTRACT, "advertisedServices": sorted(str(k) for k in services)[:512],
                "settings": {k: {"writable": bool(v[1])} for k, v in SETTINGS.items()},
                "textSizes": TEXT_SIZES, "fileChunkBytes": CHUNK, "maxIpaBytes": MAX_IPA,
                "maxProfileBytes": MAX_PROFILE}

    async def _tunnel_info(self, args):
        address = getattr(getattr(self.rsd, "service", None), "address", None)
        return {"ready": True, "transport": "existing-selected-device-rsd",
                "address": json_value(address), "generation": self.owner._generation}

    async def _device_info(self, args):
        result = {}
        for key, domain in (("device", None), ("battery", "com.apple.mobile.battery"), ("disk", "com.apple.disk_usage")):
            try:
                async with asyncio.timeout(8):
                    values = await self.rsd.get_value(domain=domain)
                if key == "device" and isinstance(values, dict):
                    values = {k: values[k] for k in ("DeviceName", "ProductType", "ProductVersion", "BuildVersion",
                        "HardwareModel", "CPUArchitecture", "UniqueDeviceID", "SerialNumber", "DeviceClass",
                        "WiFiAddress", "BluetoothAddress", "ModelNumber", "RegionInfo") if k in values}
                result[key] = {"available": True, "values": json_value(values)}
            except Exception as error:
                result[key] = {"available": False, "error": type(error).__name__}
        return result

    async def _apps(self, args):
        for key in ("includeHidden", "includeInternal", "includeClips"):
            if key in args and type(args[key]) is not bool:
                raise ValueError("App filter must be boolean")
        try:
            async with asyncio.timeout(15):
                async with connected(AppServiceService, self.rsd) as service:
                    apps = await service.list_apps(include_app_clips=args.get("includeClips", False),
                        include_hidden_apps=args.get("includeHidden", False), include_internal_apps=args.get("includeInternal", False))
            if isinstance(apps, dict):
                apps = apps.get("apps", apps.get("applications", []))
            if not isinstance(apps, list):
                raise HubError("INVALID_DEVICE_RESPONSE")
            source = "coredevice"
        except Exception:
            # Read-only fallback for devices whose older DDI advertises a hanging list-apps action.
            async with connected(InstallationProxyService, self.rsd) as service:
                apps = await service.browse(attributes=["CFBundleIdentifier", "CFBundleDisplayName", "CFBundleName",
                    "CFBundleShortVersionString", "ApplicationType", "IsAppClip", "Entitlements"])
            source = "installation-proxy"
        result = []
        for app in apps[:5000]:
            if not isinstance(app, dict):
                continue
            bundle = app.get("bundleIdentifier", app.get("CFBundleIdentifier"))
            if not isinstance(bundle, str):
                continue
            result.append({"bundleId": bundle[:512], "name": str(app.get("name") or app.get("CFBundleDisplayName") or
                app.get("CFBundleName") or bundle)[:512], "version": str(app.get("version", app.get("CFBundleShortVersionString", "")))[:128],
                "isFirstParty": bool(app.get("isFirstParty", app.get("ApplicationType") == "System")),
                "isDeveloper": bool(app.get("isDeveloper", (app.get("Entitlements") or {}).get("get-task-allow") is True)),
                "isAppClip": bool(app.get("isAppClip", app.get("IsAppClip", False)))})
        return {"apps": sorted(result, key=lambda a: (a["name"].casefold(), a["bundleId"])), "source": source}

    async def _shared_document_apps(self, args):
        async with connected(InstallationProxyService, self.rsd) as service:
            apps = await service.browse(attributes=["CFBundleIdentifier", "CFBundleDisplayName", "CFBundleName", "UIFileSharingEnabled"])
        result = []
        for app in apps[:5000]:
            if app.get("UIFileSharingEnabled") is not True:
                continue
            identifier = bundle_id(app.get("CFBundleIdentifier"))
            result.append({"bundleId": identifier, "name": str(app.get("CFBundleDisplayName") or app.get("CFBundleName") or identifier)[:512]})
        return {"apps": sorted(result, key=lambda item: (item["name"].casefold(), item["bundleId"]))}

    async def _get_app_icon(self, args):
        bundle = bundle_id(args.get("bundleId"))
        async with connected(SpringBoardServicesService, self.rsd) as service:
            data = await service.get_icon_pngdata(bundle)
        if not isinstance(data, bytes) or not data.startswith(b"\x89PNG\r\n\x1a\n") or len(data) > 2 * 1024 * 1024:
            raise HubError("INVALID_DEVICE_RESPONSE")
        return {"bundleId": bundle, "mimeType": "image/png", "data": base64.b64encode(data).decode()}

    async def _launch(self, args):
        bundle = bundle_id(args.get("bundleId"))
        async with connected(AppServiceService, self.rsd) as service:
            result = await service.launch_application(bundle, kill_existing=False)
        return {"status": "launched", "bundleId": bundle, "process": json_value(result)}

    async def _list_processes(self, args):
        async with connected(AppServiceService, self.rsd) as service:
            features = self.rsd.get_service_features(service.service_name)
            if "com.apple.coredevice.feature.streamprocesslist" in features:
                processes = []
                async for process in service.stream_processes():
                    processes.append(process)
                    if len(processes) >= 5000:
                        break
            else:
                processes = await service.list_processes()
        return {"processes": [json_value(p) for p in processes[:5000]]}

    async def _terminate_app(self, args):
        pid = integer(args.get("pid"), 1, 2**31 - 1)
        signal = integer(args.get("signal", 15), 9, 15)
        if signal not in (9, 15):
            raise ValueError("Use SIGTERM (15) or SIGKILL (9)")
        async with connected(AppServiceService, self.rsd) as service:
            await service.send_signal_to_process(pid, signal)
        return {"status": "signal-sent", "pid": pid, "signal": signal}

    async def _uninstall_app(self, args):
        bundle = bundle_id(args.get("bundleId"))
        async with connected(InstallationProxyService, self.rsd) as service:
            await service.uninstall(bundle)
        return {"status": "uninstalled", "bundleId": bundle}

    async def _install_app(self, args):
        path = Path(string(args.get("path"), 32768))
        if not path.is_file() or path.suffix.lower() != ".ipa" or not 0 < path.stat().st_size <= MAX_IPA:
            raise ValueError("Choose an IPA file up to 512 MiB")
        # Validate a real app package without extracting untrusted ZIP paths.
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > 100000 or not any(re.fullmatch(r"Payload/[^/]+\.app/Info\.plist", e.filename) for e in entries):
                raise ValueError("IPA has no app payload")
        # Stream to AFC rather than loading a 512 MiB package into the bridge.
        remote = "/PublicStaging/iphoneMirror-" + uuid.uuid4().hex + ".ipa"
        async with connected(InstallationProxyService, self.rsd) as installer:
            async with connected(AfcService, self.rsd) as afc:
                await afc.makedirs("/PublicStaging")
                try:
                    handle = await afc.fopen(remote, "w")
                    try:
                        with path.open("rb") as file:
                            while data := file.read(CHUNK):
                                await afc.fwrite(handle, data)
                    finally:
                        await afc.fclose(handle)
                    await installer.send_package("Install", {}, None, remote)
                finally:
                    with suppress(Exception):
                        await asyncio.wait_for(afc.rm_single(remote, force=True), 3)
        return {"status": "installed"}

    async def _list_profiles(self, args):
        result, errors = [], {}
        kind = args.get("kind", "all")
        if kind not in ("all", "provisioning", "configuration"):
            raise ValueError("Invalid profile kind")
        if kind in ("all", "provisioning"):
            try:
                async with connected(MisagentService, self.rsd) as service:
                    profiles = await service.copy_all()
                for profile in profiles[:2000]:
                    p = profile.plist
                    result.append({"kind": "provisioning", "id": str(p.get("UUID", "")), "name": str(p.get("Name", "")),
                        "expires": json_value(p.get("ExpirationDate")), "team": json_value(p.get("TeamName")),
                        "appId": str((p.get("Entitlements") or {}).get("application-identifier", ""))})
            except Exception as error:
                errors["provisioning"] = type(error).__name__
        if kind in ("all", "configuration"):
            try:
                async with connected(MobileConfigService, self.rsd) as service:
                    profiles = (await service.get_profile_list()).get("ProfileMetadata", {})
                for identifier, p in list(profiles.items())[:2000]:
                    result.append({"kind": "configuration", "id": str(identifier), "name": str(p.get("PayloadDisplayName", identifier)),
                        "uuid": str(p.get("PayloadUUID", "")), "description": str(p.get("PayloadDescription", ""))[:2048],
                        "organization": str(p.get("PayloadOrganization", ""))})
            except Exception as error:
                errors["configuration"] = type(error).__name__
        return {"profiles": result, "errors": errors}

    async def _install_profile(self, args):
        path = Path(string(args.get("path"), 32768))
        if not path.is_file() or not 0 < path.stat().st_size <= MAX_PROFILE:
            raise ValueError("Choose a profile up to 8 MiB")
        if path.suffix.lower() == ".mobileprovision":
            async with connected(MisagentService, self.rsd) as service:
                await service.install(io.BytesIO(path.read_bytes()))
        elif path.suffix.lower() == ".mobileconfig":
            async with connected(MobileConfigService, self.rsd) as service:
                await service.install_profile(path.read_bytes())
        else:
            raise ValueError("Use .mobileprovision or .mobileconfig")
        return {"status": "installation-requested", "requiresDeviceConfirmation": path.suffix.lower() == ".mobileconfig"}

    async def _remove_profile(self, args):
        identifier = string(args.get("id"))
        if args.get("kind") == "provisioning":
            async with connected(MisagentService, self.rsd) as service:
                await service.remove(identifier)
        elif args.get("kind") == "configuration":
            async with connected(MobileConfigService, self.rsd) as service:
                if identifier not in (await service.get_profile_list()).get("ProfileMetadata", {}):
                    raise HubError("PROFILE_NOT_FOUND")
                await service.remove_profile(identifier)
        else:
            raise ValueError("Invalid profile kind")
        return {"status": "removed", "id": identifier}

    @asynccontextmanager
    async def _files(self, args):
        root = args.get("root", "media")
        if root == "documents":
            identifier = bundle_id(args.get("bundleId"))
            service = HouseArrestService(self.rsd, documents_only=True)
            try:
                # Vend must finish before AFC's packet reader starts; otherwise
                # that reader can consume the House Arrest plist response.
                await service.send_command(identifier, VEND_DOCUMENTS)
                async with service:
                    if (await service.stat("/Documents")).get("st_ifmt") != "S_IFDIR":
                        raise ValueError("Shared Documents is not a directory")
                    yield DocumentsFiles(service)
            finally:
                with suppress(Exception):
                    await asyncio.wait_for(service.close(), 2)
            return
        if root not in ("media", "crashes"):
            raise ValueError("Choose media or crashes")
        name = None if root == "media" else "com.apple.crashreportcopymobile.shim.remote"
        if root == "crashes" and args.get("refresh") is True:
            mover = await self.rsd.start_lockdown_service("com.apple.crashreportmover.shim.remote")
            try:
                if await mover.recvall(5) != b"ping\x00":
                    raise HubError("INVALID_DEVICE_RESPONSE")
            finally:
                await mover.close()
        async with connected(AfcService, self.rsd, service_name=name) as service:
            yield service

    async def _safe_stat(self, afc, path):
        current = ""
        for part in path.split("/") if path else []:
            current += "/" + part
            stat = await afc.stat(current)
            if stat.get("st_ifmt") == "S_IFLNK":
                raise ValueError("Symbolic links cannot be traversed")
        return await afc.stat("/" + path) if not path else stat

    async def _list_dir(self, args):
        path = relative_path(args.get("path", ""))
        offset = integer(args.get("offset", 0), 0, 100000)
        limit = integer(args.get("limit", 200), 1, 500)
        async with self._files(args) as service:
            if (await self._safe_stat(service, path)).get("st_ifmt") != "S_IFDIR":
                raise ValueError("Not a directory")
            names = await service.listdir("/" + path)
            if len(names) > 100000:
                raise HubError("DIRECTORY_TOO_LARGE")
            names = sorted(n for n in names if n not in (".", ".."))
            entries = []
            for name in names[offset:offset + limit]:
                try:
                    relative_path(name)
                    if "/" in name:
                        raise ValueError("Invalid entry")
                    child = "/".join(p for p in (path, name) if p)
                    stat = await service.stat("/" + child)
                    entries.append({"name": name, "path": child, "type": {"S_IFDIR": "directory", "S_IFREG": "file"}.get(stat.get("st_ifmt"), "other"),
                        "size": int(stat.get("st_size", 0)), "modified": json_value(stat.get("st_mtime"))})
                except ValueError:
                    continue
        return {"path": path, "entries": entries, "total": len(names), "nextOffset": offset + limit if offset + limit < len(names) else None}

    async def _read_file(self, args):
        path = relative_path(args.get("path", ""))
        offset = integer(args.get("offset", 0), 0, 2**53 - 1)
        length = integer(args.get("length", CHUNK), 1, CHUNK)
        async with self._files(args) as service:
            stat = await self._safe_stat(service, path)
            if stat.get("st_ifmt") != "S_IFREG":
                raise ValueError("Not a regular file")
            size = int(stat["st_size"])
            if not 0 <= offset <= size:
                raise ValueError("Offset exceeds file size")
            handle = await service.fopen("/" + path, "r")
            try:
                await service.fseek(handle, offset)
                data = await service.fread(handle, min(length, size - offset)) if offset < size else b""
            finally:
                await service.fclose(handle)
            after = await service.stat("/" + path)
            if stat.get("st_mtime") != after.get("st_mtime") or size != int(after.get("st_size", -1)):
                raise HubError("FILE_CHANGED")
        return {"path": path, "offset": offset, "size": size, "modified": json_value(stat.get("st_mtime")),
                "data": base64.b64encode(data).decode(), "eof": offset + len(data) >= size}

    async def _delete_file(self, args):
        if args.get("root", "media") not in ("media", "documents"):
            raise ValueError("Only media files can be deleted")
        path = relative_path(args.get("path", ""))
        if not path:
            raise ValueError("Choose a file")
        expected = args.get("expected")
        if not isinstance(expected, dict) or type(expected.get("size")) is not int or expected["size"] < 0 or "modified" not in expected:
            raise ValueError("A file listing snapshot is required")
        try:
            async with self._files(args) as service:
                current = await self._safe_stat(service, path)
                if current.get("st_ifmt") != "S_IFREG":
                    raise ValueError("Only regular files can be deleted")
                if expected["size"] != int(current.get("st_size", -1)) or expected["modified"] != json_value(current.get("st_mtime")):
                    raise HubError("FILE_DELETE_CHANGED")
                # A single remove never recursively deletes a directory or retries.
                await service.rm_single("/" + path)
            return {"status": "deleted", "path": path}
        except (OSError, AfcException) as error:
            raise HubError("FILE_DELETE_FAILED") from error

    async def _upload_file(self, args):
        if args.get("root", "media") not in ("media", "documents"):
            raise ValueError("Only the media file service accepts imports")
        directory = relative_path(args.get("path", ""))
        source = Path(string(args.get("sourcePath"), 32768))
        name = relative_path(source.name)
        if not name or len(name.encode("utf-8")) > 255:
            raise ValueError("Unsupported file name")
        temporary = "/" + relative_path("/".join(filter(None,
            (directory, ".iPhoneMirror-import-" + uuid.uuid4().hex + ".partial"))))
        try:
            with source.open("rb") as input_file:
                before = os.fstat(input_file.fileno())
                if not stat.S_ISREG(before.st_mode):
                    raise ValueError("Choose a regular file")
                async with self._files(args) as service:
                    if (await self._safe_stat(service, directory)).get("st_ifmt") != "S_IFDIR":
                        raise ValueError("Not a directory")
                    committed = False
                    try:
                        handle = await service.fopen(temporary, "w")
                        written = 0
                        try:
                            while data := input_file.read(CHUNK):
                                if written + len(data) > before.st_size:
                                    raise HubError("FILE_CHANGED")
                                await service.fwrite(handle, data)
                                written += len(data)
                        finally:
                            await asyncio.wait_for(service.fclose(handle), 2)
                        after = os.fstat(input_file.fileno())
                        if written != before.st_size or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                            raise HubError("FILE_CHANGED")
                        if int((await service.stat(temporary)).get("st_size", -1)) != written:
                            raise HubError("FILE_IMPORT_FAILED")
                        # Choose the final name after writing so existing files are
                        # preserved, including names added while the transfer ran.
                        existing = {entry.casefold() for entry in await service.listdir("/" + directory)}
                        target_name = name
                        for number in range(1, 100001):
                            if target_name.casefold() not in existing:
                                break
                            suffix = f" ({number})" + source.suffix
                            budget = 255 - len(suffix.encode("utf-8"))
                            if budget <= 0:
                                raise ValueError("Unsupported file name")
                            target_name = source.stem.encode("utf-8")[:budget].decode("utf-8", "ignore") + suffix
                        else:
                            raise HubError("DIRECTORY_TOO_LARGE")
                        target = relative_path("/".join(filter(None, (directory, target_name))))
                        await service.rename(temporary, "/" + target)
                        committed = True
                    finally:
                        if not committed:
                            with suppress(Exception):
                                await asyncio.wait_for(service.rm_single(temporary, force=True), 2)
            return {"status": "imported", "path": target, "name": target_name, "size": written}
        except (OSError, AfcException) as error:
            raise HubError("FILE_IMPORT_FAILED") from error

    @staticmethod
    def _mapped_path(args, key="path", allow_root=False):
        if args.get("root", "media") not in ("media", "documents"):
            raise ValueError("Explorer mapping only exposes the media file service")
        path = relative_path(args.get(key, ""))
        if not allow_root and not path:
            raise ValueError("The mapped root cannot be changed")
        if any(len(part.encode("utf-8")) > 255 for part in path.split("/")):
            raise ValueError("Unsupported file name")
        return path

    @staticmethod
    def _mapped_metadata(path, value):
        kind = {"S_IFDIR": "directory", "S_IFREG": "file"}.get(value.get("st_ifmt"))
        if kind is None:
            raise ValueError("Explorer mapping does not expose links or special files")
        return {"path": path, "name": path.rsplit("/", 1)[-1], "type": kind,
                "size": int(value.get("st_size", 0)), "modified": json_value(value.get("st_mtime"))}

    @staticmethod
    def _mapped_expected(current, expected):
        if not isinstance(expected, dict) or expected.get("type") not in ("file", "directory") or \
                type(expected.get("size")) is not int or expected["size"] < 0 or "modified" not in expected:
            raise ValueError("A mapping metadata snapshot is required")
        if any(current[key] != expected[key] for key in ("type", "size", "modified")):
            raise HubError("FILE_MAPPING_CONFLICT")

    @staticmethod
    def _mapped_error(error):
        if isinstance(error, (AfcFileNotFoundError, FileNotFoundError)) or \
                isinstance(error, AfcException) and error.status == 8:
            return HubError("FILE_NOT_FOUND")
        if isinstance(error, PermissionError) or isinstance(error, AfcException) and error.status == 10:
            return HubError("FILE_ACCESS_DENIED")
        if isinstance(error, AfcException) and error.status in (9, 16, 33):
            return HubError("FILE_MAPPING_CONFLICT")
        return HubError("FILE_OPERATION_FAILED")

    async def _mapped_stat(self, service, path, optional=False):
        try:
            return self._mapped_metadata(path, await self._safe_stat(service, path))
        except (AfcFileNotFoundError, FileNotFoundError):
            if optional:
                return None
            raise HubError("FILE_NOT_FOUND") from None
        except AfcException as error:
            if optional and error.status == 8:
                return None
            raise self._mapped_error(error) from error

    async def _mapped_target(self, service, path, expected):
        current = await self._mapped_stat(service, path, optional=True)
        if expected is None:
            if current is not None:
                raise HubError("FILE_MAPPING_CONFLICT")
        elif current is None:
            raise HubError("FILE_MAPPING_CONFLICT")
        else:
            self._mapped_expected(current, expected)
        return current

    async def _mapped_parent(self, service, path):
        parent = path.rpartition("/")[0]
        if (await self._mapped_stat(service, parent))["type"] != "directory":
            raise HubError("FILE_MAPPING_CONFLICT")

    async def _stat_file(self, args):
        path = self._mapped_path(args, allow_root=True)
        try:
            async with self._files(args) as service:
                return await self._mapped_stat(service, path)
        except (OSError, AfcException) as error:
            raise self._mapped_error(error) from error

    async def _write_mapped_file(self, args):
        path = self._mapped_path(args)
        expected = args.get("expected")
        source = Path(string(args.get("sourcePath"), 32768))
        parent = path.rpartition("/")[0]
        temporary = "/" + "/".join(filter(None, (parent, ".iPhoneMirror-map-" + uuid.uuid4().hex + ".partial")))
        try:
            with source.open("rb") as input_file:
                before = os.fstat(input_file.fileno())
                if not stat.S_ISREG(before.st_mode):
                    raise ValueError("Choose a regular spool file")
                async with self._files(args) as service:
                    await self._mapped_parent(service, path)
                    existing = await self._mapped_target(service, path, expected)
                    if existing is not None and existing["type"] != "file":
                        raise HubError("FILE_MAPPING_CONFLICT")
                    committed = False
                    try:
                        handle = await service.fopen(temporary, "w")
                        written = 0
                        try:
                            while data := input_file.read(CHUNK):
                                if written + len(data) > before.st_size:
                                    raise HubError("FILE_CHANGED")
                                await service.fwrite(handle, data)
                                written += len(data)
                        finally:
                            await asyncio.wait_for(service.fclose(handle), 2)
                        after = os.fstat(input_file.fileno())
                        if written != before.st_size or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                            raise HubError("FILE_CHANGED")
                        if int((await service.stat(temporary)).get("st_size", -1)) != written:
                            raise HubError("FILE_OPERATION_FAILED")
                        # Recheck after the transfer: a PUT must never silently
                        # rename itself or replace a target changed while writing.
                        await self._mapped_parent(service, path)
                        await self._mapped_target(service, path, expected)
                        await service.rename(temporary, "/" + path)
                        committed = True
                        return await self._mapped_stat(service, path)
                    finally:
                        if not committed:
                            with suppress(Exception):
                                await asyncio.wait_for(service.rm_single(temporary, force=True), 2)
        except (OSError, AfcException) as error:
            raise self._mapped_error(error) from error

    async def _make_directory(self, args):
        path = self._mapped_path(args)
        try:
            async with self._files(args) as service:
                await self._mapped_parent(service, path)
                await self._mapped_target(service, path, None)
                await service.makedirs("/" + path)
                return await self._mapped_stat(service, path)
        except (OSError, AfcException) as error:
            raise self._mapped_error(error) from error

    async def _set_mapped_file_time(self, args):
        path = self._mapped_path(args)
        modified = integer(args.get("modifiedNs"), 0, 2**63 - 1)
        try:
            async with self._files(args) as service:
                current = await self._mapped_stat(service, path)
                self._mapped_expected(current, args.get("expected"))
                # AFC SET_FILE_TIME matches libimobiledevice afc_set_file_time:
                # unsigned little-endian epoch nanoseconds, then UTF-8 path/NUL.
                # The pinned pymobiledevice3 exposes the opcode but no helper.
                payload = struct.pack("<Q", modified) + ("/" + path).encode("utf-8") + b"\0"
                await service._do_operation(AfcOpcode.SET_FILE_TIME, payload)
                return await self._mapped_stat(service, path)
        except (OSError, AfcException) as error:
            raise self._mapped_error(error) from error

    async def _move_file(self, args):
        path = self._mapped_path(args)
        destination = self._mapped_path(args, "destination")
        if path == destination or destination.startswith(path + "/"):
            raise HubError("FILE_MAPPING_CONFLICT")
        try:
            async with self._files(args) as service:
                current = await self._mapped_stat(service, path)
                self._mapped_expected(current, args.get("expected"))
                await self._mapped_parent(service, destination)
                target = await self._mapped_target(service, destination, args.get("destinationExpected"))
                if target is not None:
                    if target["type"] != current["type"]:
                        raise HubError("FILE_MAPPING_CONFLICT")
                    if target["type"] == "directory" and any(name not in (".", "..") for name in await service.listdir("/" + destination)):
                        raise HubError("FILE_MAPPING_CONFLICT")
                # AFC's single rename preserves the source if replacement fails.
                # Do not pre-delete the destination or retry a failed rename.
                self._mapped_expected(await self._mapped_stat(service, path), args.get("expected"))
                await self._mapped_target(service, destination, args.get("destinationExpected"))
                await service.rename("/" + path, "/" + destination)
                return await self._mapped_stat(service, destination)
        except (OSError, AfcException) as error:
            raise self._mapped_error(error) from error

    async def _delete_mapped_file(self, args):
        path = self._mapped_path(args)
        recursive = args.get("recursive", False)
        if type(recursive) is not bool:
            raise ValueError("recursive must be boolean")
        try:
            async with self._files(args) as service:
                current = await self._mapped_stat(service, path)
                self._mapped_expected(current, args.get("expected"))
                pending = [(path, current, 0)]
                plan = []
                # Preflight the entire bounded tree before the first deletion.
                # Links and special entries abort without removing any file.
                while pending:
                    target, metadata, depth = pending.pop()
                    if depth > 64 or len(plan) + len(pending) >= 10000:
                        raise HubError("DIRECTORY_TOO_LARGE")
                    plan.append((target, metadata))
                    if metadata["type"] != "directory":
                        continue
                    names = [name for name in await service.listdir("/" + target) if name not in (".", "..")]
                    if names and not recursive:
                        raise HubError("FILE_MAPPING_CONFLICT")
                    for name in names:
                        if "/" in relative_path(name):
                            raise ValueError("Invalid directory entry")
                        child = target + "/" + name
                        pending.append((child, await self._mapped_stat(service, child), depth + 1))
                        if len(plan) + len(pending) > 10000:
                            raise HubError("DIRECTORY_TOO_LARGE")
                for target, metadata in reversed(plan):
                    fresh = await self._mapped_stat(service, target)
                    if metadata["type"] == "file":
                        self._mapped_expected(fresh, metadata)
                    elif fresh["type"] != "directory" or any(name not in (".", "..") for name in await service.listdir("/" + target)):
                        raise HubError("FILE_MAPPING_CONFLICT")
                    await service.rm_single("/" + target)
                return {"status": "deleted", "path": path}
        except (OSError, AfcException) as error:
            raise self._mapped_error(error) from error

    async def _syslog_start(self, args):
        if self.log_stream and time.monotonic() - self.log_activity < 60:
            raise HubError("LOG_STREAM_BUSY")
        await self.close()
        self.log_service = SyslogService(self.rsd)
        try:
            await self.log_service.__aenter__()
        except BaseException:
            await self.close()
            raise
        self.log_stream = uuid.uuid4().hex
        self.log_lines.clear()
        self.log_error = None
        self.log_seq = 0
        self.log_activity = time.monotonic()
        self.log_task = asyncio.create_task(self._read_logs())
        return {"streamId": self.log_stream, "cursor": 0}

    def _log_id(self, args):
        if not self.log_stream or args.get("streamId") != self.log_stream:
            raise HubError("LOG_STREAM_EXPIRED")
        self.log_activity = time.monotonic()

    async def _read_logs(self):
        pending = b""
        try:
            while time.monotonic() - self.log_activity < 60:
                try:
                    data = await asyncio.wait_for(self.log_service.service.recv_any(4096), 5)
                except TimeoutError:
                    continue
                if not data:
                    raise EOFError()
                pending += data.replace(b"\x00", b"\n")
                while b"\n" in pending or len(pending) > 8192:
                    cut = pending.find(b"\n", 0, 8193)
                    cut = min(len(pending), 8192) if cut < 0 else cut
                    line, pending = pending[:cut], pending[cut + (pending[cut:cut+1] == b"\n"):]
                    if line:
                        self.log_seq += 1
                        self.log_lines.append({"cursor": self.log_seq, "text": line.decode("utf-8", errors="replace")})
            self.log_error = "IDLE_TIMEOUT"
        except asyncio.CancelledError:
            raise
        except Exception as error:
            self.log_error = type(error).__name__
        finally:
            if self.log_service:
                with suppress(Exception):
                    await self.log_service.__aexit__(None, None, None)

    async def _syslog_poll(self, args):
        self._log_id(args)
        cursor = integer(args.get("cursor", 0), 0, 2**53 - 1)
        limit = integer(args.get("limit", 200), 1, 500)
        lines, size = [], 0
        for line in self.log_lines:
            if line["cursor"] <= cursor:
                continue
            size += len(line["text"].encode("utf-8"))
            if size > 512 * 1024 or len(lines) >= limit:
                break
            lines.append(line)
        first = self.log_lines[0]["cursor"] if self.log_lines else self.log_seq + 1
        return {"streamId": self.log_stream, "lines": lines, "cursor": lines[-1]["cursor"] if lines else cursor,
                "dropped": max(0, first - cursor - 1), "running": bool(self.log_task and not self.log_task.done()), "error": self.log_error}

    async def _syslog_stop(self, args):
        self._log_id(args)
        await self.close()
        return {"running": False}

    async def _settings_probe(self, args):
        services = getattr(self.rsd, "peer_info", {}).get("Services", {})
        return {"configurationAdvertised": CONFIG in services, "locationAdvertised": LOCATION in services,
                "settings": {k: {"writable": bool(v[1])} for k, v in SETTINGS.items()}, "textSizes": TEXT_SIZES}

    async def _get_setting(self, args):
        key = args.get("key")
        if key not in SETTINGS:
            raise ValueError("Unknown setting")
        async with connected(CoreDeviceService, self.rsd, CONFIG) as service:
            result = await service.invoke(action_identifier=PREFIX + SETTINGS[key][0], input_={})
        return {"key": key, "value": json_value(result)}

    async def _get_settings(self, args):
        results = {}
        for key in SETTINGS:
            try:
                async with asyncio.timeout(2):
                    results[key] = {"available": True, **await self._get_setting({"key": key})}
            except Exception as error:
                results[key] = {"available": False, "error": type(error).__name__}
        return {"settings": results}

    async def _set_setting(self, args):
        key, value = args.get("key"), args.get("value")
        payload = setting_input(key, value)
        async with connected(CoreDeviceService, self.rsd, CONFIG) as service:
            result = await service.invoke(action_identifier=PREFIX + SETTINGS[key][1], input_=payload)
        return {"key": key, "status": "applied", "result": json_value(result)}

    async def _set_location(self, args):
        latitude, longitude = args.get("latitude"), args.get("longitude")
        for value, maximum in ((latitude, 90), (longitude, 180)):
            if type(value) not in (int, float) or not math.isfinite(value) or not -maximum <= value <= maximum:
                raise ValueError("Invalid latitude or longitude")
        async with connected(CoreDeviceService, self.rsd, LOCATION) as service:
            await service.invoke(action_identifier=PREFIX + "setsimulatedlocation",
                input_={"latitude": float(latitude), "longitude": float(longitude)})
        return {"status": "location-set", "latitude": latitude, "longitude": longitude}

    async def _clear_location(self, args):
        async with connected(CoreDeviceService, self.rsd, LOCATION) as service:
            await service.invoke(action_identifier=PREFIX + "clearsimulatedlocation", input_={})
        return {"status": "location-cleared"}

    async def _device_action(self, args):
        action = args.get("action")
        if action not in ("restart", "shutdown", "sleep"):
            raise ValueError("Choose restart, shutdown or sleep")
        async with connected(DiagnosticsService, self.rsd) as service:
            await getattr(service, action)()
        return {"status": "requested", "action": action}

    async def _press_button(self, args):
        button = args.get("button")
        codes = {"home": 0x40, "app_switcher": 0x40, "lock": 0x30, "volume_up": 0xE9, "volume_down": 0xEA}
        if button not in codes:
            raise ValueError("Unknown phone button")
        if getattr(self.owner, "_held_buttons", None) or getattr(self.owner, "_active_touch_ids", None):
            raise HubError("CONTROL_LOCKED")
        if button == "app_switcher":
            await self.owner._apply_button(0x0C, 0x40, "double_press")
        else:
            try:
                await self.owner._apply_button(0x0C, codes[button], "down")
                await asyncio.sleep(.08)
            finally:
                cleanup = asyncio.create_task(self.owner._apply_button(0x0C, codes[button], "up"))
                try:
                    await asyncio.wait_for(asyncio.shield(cleanup), 3)
                except BaseException:
                    cleanup.cancel()
                    await asyncio.gather(cleanup, return_exceptions=True)
                    if health := getattr(self.owner, "_media_health", None):
                        health.error = RuntimeError("Button release failed; repair HID route")
                    raise
        return {"status": "dispatched", "button": button}

    async def close(self):
        task, self.log_task = self.log_task, None
        if task:
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
        if self.log_service:
            with suppress(Exception):
                await asyncio.wait_for(self.log_service.__aexit__(None, None, None), 2)
            self.log_service = None
        self.log_stream = None
