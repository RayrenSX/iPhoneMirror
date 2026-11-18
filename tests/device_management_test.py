"""Exercise real pymobiledevice3 serialization against isolated device transports."""
import asyncio
import base64
from collections import deque
import io
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from device_management import ACTIONS, CHUNK, CONFIG, LOCATION, PREFIX, DeviceManagement, HubError, relative_path
from pymobiledevice3.remote.xpc_message import XpcInt64Type


class Xpc:
    def __init__(self, rsd, name):
        self.rsd, self.name = rsd, name
        self.closed = False
    async def connect(self):
        if self.rsd.fail_connect:
            raise ConnectionError()
    async def close(self): self.closed = True
    async def send_receive_request(self, message):
        self.rsd.messages.append((self.name, message))
        if self.rsd.block:
            self.rsd.started.set()
            await asyncio.Future()
        return {"CoreDevice.output": {}}


class Rsd:
    def __init__(self):
        self.channels, self.messages = [], []
        self.fail_connect = self.block = False
        self.started = asyncio.Event()
        self.peer_info = {"Services": {CONFIG: {}, LOCATION: {}}}
    def start_remote_service(self, name):
        connection = Xpc(self, name); self.channels.append(connection); return connection
    def require_feature(self, service, feature): pass
    def get_service_features(self, service): return []


class FileService:
    def __init__(self):
        self.paths, self.closed, self.position = [], False, 0
        self.data = b"abc" * (CHUNK // 3 + 10)
        self.handle_closed = False
    async def __aenter__(self): return self
    async def __aexit__(self, *args): self.closed = True
    async def stat(self, path):
        self.paths.append(path)
        if path in ("/", "/DCIM"): return {"st_ifmt": "S_IFDIR", "st_size": 0}
        if path == "/link": return {"st_ifmt": "S_IFLNK", "st_size": 0}
        return {"st_ifmt": "S_IFREG", "st_size": len(self.data), "st_mtime": 123}
    async def listdir(self, path): return [".", "..", "test.mov"]
    async def fopen(self, path, mode): return 7
    async def fseek(self, handle, offset): self.position = offset
    async def fread(self, handle, length): return self.data[self.position:self.position + length]
    async def fclose(self, handle): self.handle_closed = True


class ManagementTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.rsd = Rsd()
        self.management = DeviceManagement(SimpleNamespace(rsd=self.rsd, _generation=8))

    async def test_metal_hud_restart_uses_fixed_launch_wire_options(self):
        await self.management.request("restart_with_metal_hud", {"bundleId": "com.example.game"})
        name, packet = self.rsd.messages[-1]
        self.assertEqual(name, "com.apple.coredevice.appservice")
        self.assertEqual(packet["CoreDevice.featureIdentifier"], "com.apple.coredevice.feature.launchapplication")
        payload = packet["CoreDevice.input"]
        self.assertEqual(payload["applicationSpecifier"], {"bundleIdentifier": {"_0": "com.example.game"}})
        self.assertEqual(payload["options"]["environmentVariables"], {"MTL_HUD_ENABLED": "1"})
        self.assertTrue(payload["options"]["terminateExisting"])
        self.assertFalse(payload["options"]["startStopped"])
        self.assertEqual(payload["options"]["arguments"], [])
        self.assertTrue(self.rsd.channels[-1].closed)

        await self.management.request("launch", {"bundleId": "com.example.game"})
        ordinary = self.rsd.messages[-1][1]["CoreDevice.input"]["options"]
        self.assertEqual(ordinary["environmentVariables"], {})
        self.assertFalse(ordinary["terminateExisting"])
        self.assertTrue(self.rsd.channels[-1].closed)

    async def test_settings_exact_wire_payloads_on_selected_rsd(self):
        cases = [
            ("showBorders", True, "setshowborders", {"showBorders": {"enabled": True}}),
            ("voiceOver", False, "setvoiceover", {"voiceOverConfiguration": {"enabled": False}}),
            ("appearance", "dark", "setuserinterfacestyle", {"style": "dark"}),
            ("textSize", "accessibilityLarge", "setdevicetextsize", {"textSize": {"size": {"accessibilityLarge": {}}}}),
            ("liquidGlass", .4, "setliquidglassconfiguration", {"configuration": {"opacity": .4}}),
            ("largerAccessibilitySizes", True, "setlargeraccessibilitysizesenabled", {"enabled": True}),
        ]
        for key, value, action, payload in cases:
            await self.management.request("set_setting", {"key": key, "value": value})
            name, packet = self.rsd.messages[-1]
            self.assertEqual(name, CONFIG)
            self.assertEqual(packet["CoreDevice.actionIdentifier"], PREFIX + action)
            self.assertEqual(packet["CoreDevice.input"], payload)
            self.assertNotIn("CoreDevice.featureIdentifier", packet)
            self.assertTrue(self.rsd.channels[-1].closed)
        for key, value in (("appearance", "auto"), ("liquidGlass", float("nan")), ("liquidGlass", True),
                           ("textSize", "madeUp"), ("voiceOver", 1), ("lookAndFeel", "Liquid Glass")):
            with self.assertRaises(ValueError):
                await self.management.request("set_setting", {"key": key, "value": value})
        self.assertEqual(len(self.rsd.messages), len(cases))

    async def test_location_separate_service_bounds_and_clear(self):
        await self.management.request("set_location", {"latitude": -90, "longitude": 180})
        name, message = self.rsd.messages[-1]
        self.assertEqual(name, LOCATION)
        self.assertEqual(message["CoreDevice.input"], {"latitude": -90., "longitude": 180.})
        for a, b in ((91, 0), (0, -181), (True, 0), (0, float("inf"))):
            with self.assertRaises(ValueError):
                await self.management.request("set_location", {"latitude": a, "longitude": b})
        await self.management.request("clear_location", {})
        self.assertEqual(self.rsd.messages[-1][1]["CoreDevice.actionIdentifier"], PREFIX + "clearsimulatedlocation")
        self.assertEqual(len(self.rsd.messages), 2)

    async def test_cancel_or_failed_handshake_closes_channel_no_retry(self):
        self.rsd.fail_connect = True
        with self.assertRaises(ConnectionError):
            await self.management.request("get_setting", {"key": "appearance"})
        self.assertTrue(self.rsd.channels[-1].closed)
        self.rsd.fail_connect, self.rsd.block = False, True
        task = asyncio.create_task(self.management.request("set_setting", {"key": "showBorders", "value": True}))
        await self.rsd.started.wait(); task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertTrue(self.rsd.channels[-1].closed)
        self.assertEqual(len(self.rsd.messages), 1)

    async def test_files_chunks_and_symlink_rejection(self):
        files = FileService()
        with patch("device_management.AfcService", return_value=files):
            page = await self.management.request("list_dir", {"path": "DCIM", "limit": 1})
            self.assertEqual(page["entries"][0]["path"], "DCIM/test.mov")
            part = await self.management.request("read_file", {"path": "DCIM/test.mov", "offset": 9})
            self.assertEqual(len(base64.b64decode(part["data"])), CHUNK)
            self.assertFalse(part["eof"])
            self.assertTrue(files.handle_closed)
            with self.assertRaises(ValueError):
                await self.management.request("read_file", {"path": "link/secret"})
            self.assertNotIn("/link/secret", files.paths)
            with self.assertRaises(ValueError):
                await self.management.request("read_file", {"path": "DCIM/test.mov", "length": CHUNK + 1})
        for path in ("/etc/passwd", "../x", "DCIM/../x", "C:/secret", "DCIM\\x", "a//b", "a/\0b"):
            with self.assertRaises(ValueError): relative_path(path)

    async def test_changing_file_rejected_and_handle_closed(self):
        files = FileService()
        original = files.stat
        count = 0
        async def stat(path):
            nonlocal count
            result = await original(path)
            if path == "/file":
                count += 1
                result["st_size"] += count
            return result
        files.stat = stat
        with patch("device_management.AfcService", return_value=files):
            with self.assertRaises(HubError) as error:
                await self.management.request("read_file", {"path": "file"})
        self.assertEqual(error.exception.code, "FILE_CHANGED")
        self.assertTrue(files.handle_closed and files.closed)

    async def test_install_streams_to_device_and_removes_staging_on_failure(self):
        files = FileService()
        files.makedirs = AsyncMock(); files.fwrite = AsyncMock(); files.rm_single = AsyncMock()
        installer = SimpleNamespace(send_package=AsyncMock(side_effect=OSError("phone rejected IPA")))
        class Installer:
            async def __aenter__(self): return installer
            async def __aexit__(self, *args): pass
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.ipa"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("Payload/Test.app/Info.plist", b"plist")
                archive.writestr("Payload/Test.app/contents", b"x" * (CHUNK * 2))
            with patch("device_management.AfcService", return_value=files), patch("device_management.InstallationProxyService", return_value=Installer()):
                with self.assertRaises(OSError):
                    await self.management.request("install_app", {"path": str(path)})
        self.assertGreater(files.fwrite.await_count, 2)
        self.assertTrue(all(len(call.args[1]) <= CHUNK for call in files.fwrite.await_args_list))
        self.assertEqual(files.rm_single.await_count, 1)
        self.assertTrue(files.handle_closed and files.closed)
        self.assertEqual(installer.send_package.await_count, 1)

    async def test_syslog_oversized_line_bounded_and_cursor_reports_drops(self):
        stream = SimpleNamespace(recv_any=AsyncMock(side_effect=[b"x" * 8192, b"x" * 8192, b"\n\x00line\n", b""]))
        service = SimpleNamespace(service=stream, __aexit__=AsyncMock())
        self.management.log_service = service
        self.management.log_stream = "test"
        import time
        self.management.log_activity = time.monotonic()
        await self.management._read_logs()
        self.assertTrue(all(len(line["text"]) <= 8192 for line in self.management.log_lines))
        self.management.log_lines = deque(({"cursor": i, "text": "line"} for i in range(10, 13)), maxlen=2048)
        page = await self.management.request("syslog_poll", {"streamId": "test", "cursor": 5, "limit": 2})
        self.assertEqual((page["dropped"], page["cursor"]), (4, 11))
        with self.assertRaises(HubError):
            await self.management.request("syslog_poll", {"streamId": "another-client"})
        await self.management.close()
        self.assertIsNone(self.management.log_service)

    async def test_missing_profile_and_invalid_mutation_do_not_dispatch(self):
        service = SimpleNamespace(get_profile_list=AsyncMock(return_value={"ProfileMetadata": {}}), remove_profile=AsyncMock())
        class Context:
            async def __aenter__(self): return service
            async def __aexit__(self, *args): pass
        with patch("device_management.MobileConfigService", return_value=Context()):
            with self.assertRaises(HubError):
                await self.management.request("remove_profile", {"kind": "configuration", "id": "missing"})
        service.remove_profile.assert_not_awaited()
        for action, args in (("terminate_app", {"pid": 0}), ("terminate_app", {"pid": 1, "signal": 10}),
                             ("uninstall_app", {"bundleId": "com.apple/secret"}), ("device_action", {"action": "erase"})):
            with self.assertRaises(ValueError): await self.management.request(action, args)
        self.assertEqual(self.rsd.messages, [])

    async def test_contract_covers_all_dispatchers(self):
        media = {"media_start", "media_stop", "media_status", "record_start", "record_stop"}
        for action in ACTIONS:
            self.assertTrue(callable(getattr(self.management, "_" + action, None)), action)
        self.assertFalse(ACTIONS["install_app"]["public"])
        self.assertFalse(ACTIONS["install_profile"]["public"])
        self.assertFalse(ACTIONS["upload_file"]["public"])
        self.assertFalse(ACTIONS["upload_file"]["readOnly"])
        self.assertFalse(ACTIONS["delete_file"]["public"])
        self.assertFalse(ACTIONS["delete_file"]["readOnly"])
        self.assertTrue(media.isdisjoint(ACTIONS), "Unusable live-media actions must be removed from the contract")

    async def test_process_numeric_identifiers_stay_numeric(self):
        service = SimpleNamespace(service_name="app-service", list_processes=AsyncMock(return_value=[
            {"processIdentifier": XpcInt64Type(123), "executableURL": {"relative": "/Applications/Test.app/Test"}}]))
        class Context:
            async def __aenter__(self): return service
            async def __aexit__(self, *args): pass
        with patch("device_management.AppServiceService", return_value=Context()):
            result = await self.management.request("list_processes", {})
        self.assertIsInstance(result["processes"][0]["processIdentifier"], int)

    async def test_phone_button_cancel_releases_before_returning(self):
        started = asyncio.Event()
        calls = []
        async def button(page, code, state):
            calls.append((page, code, state))
            if state == "down":
                started.set()
                await asyncio.Future()
        self.management.owner._apply_button = button
        task = asyncio.create_task(self.management.request("press_button", {"button": "volume_up"}))
        await started.wait(); task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(calls, [(12, 233, "down"), (12, 233, "up")])
        with self.assertRaises(ValueError):
            await self.management.request("press_button", {"button": "erase"})


if __name__ == "__main__": unittest.main()
