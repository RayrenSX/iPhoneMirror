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
from unittest.mock import AsyncMock, call, patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from device_management import ACTIONS, CHUNK, CONFIG, LOCATION, PREFIX, DeviceManagement, HubError, relative_path
from pymobiledevice3.exceptions import CoreDeviceError, InvalidServiceError
from pymobiledevice3.remote.xpc_message import XpcInt64Type, XpcUInt64Type, create_xpc_wrapper


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

    @staticmethod
    def remote_lockdown_connection():
        return SimpleNamespace(writer=None, start=AsyncMock(), close=AsyncMock(),
                               send_recv_plist=AsyncMock(return_value={"Request": "RSDCheckin"}),
                               recv_plist=AsyncMock(return_value={"Request": "StartService"}))

    async def test_device_info_opens_fresh_lockdown_on_the_selected_rsd(self):
        self.rsd.udid = "selected-device"
        self.rsd.get_value = AsyncMock(side_effect=NotImplementedError)
        connections = [self.remote_lockdown_connection() for _ in range(2)]
        self.rsd.start_lockdown_service_without_checkin = AsyncMock(side_effect=connections)
        values = [{"ProductType": "iPhone13,1", "InternalField": "omitted"},
                  {"BatteryCurrentCapacity": 72}, {"TotalDiskCapacity": 128}]
        readers = [SimpleNamespace(all_values=values[0], get_value=AsyncMock(side_effect=values[1:])) for _ in connections]
        with patch("device_management.create_using_remote", side_effect=readers) as open_lockdown:
            for _ in connections:
                result = await self.management.request("device_info", {})
                self.assertEqual(result, {
                    "device": {"available": True, "values": {"ProductType": "iPhone13,1"}},
                    "battery": {"available": True, "values": values[1]},
                    "disk": {"available": True, "values": values[2]},
                })
        self.rsd.get_value.assert_not_awaited()
        self.assertEqual(self.rsd.start_lockdown_service_without_checkin.await_args_list,
                         [call("com.apple.mobile.lockdown.remote.trusted")] * 2)
        self.assertEqual(open_lockdown.await_args_list,
                         [call(connection, identifier=self.rsd.udid, autopair=False) for connection in connections])
        for connection, reader in zip(connections, readers):
            connection.close.assert_awaited_once()
            self.assertEqual(reader.get_value.await_args_list,
                             [call(domain="com.apple.mobile.battery"), call(domain="com.apple.disk_usage")])

    async def test_device_info_keeps_partial_results_if_a_domain_fails(self):
        self.rsd.udid = "selected-device"
        connection = self.remote_lockdown_connection()
        self.rsd.start_lockdown_service_without_checkin = AsyncMock(
            side_effect=[InvalidServiceError("unavailable", self.rsd.udid, "18.7.8"), connection])
        reader = SimpleNamespace(all_values={"ProductType": "iPhone13,1"},
                                 get_value=AsyncMock(side_effect=[ValueError(), None]))
        with patch("device_management.create_using_remote", return_value=reader):
            result = await self.management.request("device_info", {})
        self.assertEqual(self.rsd.start_lockdown_service_without_checkin.await_args_list,
                         [call("com.apple.mobile.lockdown.remote.trusted"), call("com.apple.mobile.lockdown.remote.untrusted")])
        self.assertTrue(result["device"]["available"])
        self.assertEqual(result["battery"], {"available": False, "error": "ValueError"})
        self.assertEqual(result["disk"], {"available": False, "error": "MissingValues"})
        connection.close.assert_awaited_once()

    async def test_device_info_reopens_after_timeout_and_reads_only_pending_domains(self):
        self.rsd.udid = "selected-device"
        connections = [self.remote_lockdown_connection() for _ in range(2)]
        self.rsd.start_lockdown_service_without_checkin = AsyncMock(side_effect=connections)
        first = SimpleNamespace(all_values={"ProductType": "iPhone13,1"},
                                get_value=AsyncMock(side_effect=[{"BatteryCurrentCapacity": 72}, TimeoutError()]))
        second = SimpleNamespace(all_values={}, get_value=AsyncMock(return_value={"TotalDiskCapacity": 128}))
        with patch("device_management.create_using_remote", side_effect=[first, second]):
            result = await self.management.request("device_info", {})
        self.assertTrue(all(section["available"] for section in result.values()))
        self.assertEqual(result["disk"]["values"], {"TotalDiskCapacity": 128})
        second.get_value.assert_awaited_once_with(domain="com.apple.disk_usage")
        for connection in connections:
            connection.close.assert_awaited_once()

    async def test_device_info_reads_disk_after_battery_retries_are_exhausted(self):
        self.rsd.udid = "selected-device"
        connections = [self.remote_lockdown_connection() for _ in range(3)]
        self.rsd.start_lockdown_service_without_checkin = AsyncMock(side_effect=connections)
        readers = [SimpleNamespace(all_values={"ProductType": "iPhone13,1"},
                                   get_value=AsyncMock(side_effect=TimeoutError)) for _ in range(2)]
        disk = SimpleNamespace(get_value=AsyncMock(return_value={"TotalDiskCapacity": 128}))
        with patch("device_management.create_using_remote", side_effect=[*readers, disk]):
            result = await self.management.request("device_info", {})
        self.assertTrue(result["device"]["available"])
        self.assertEqual(result["battery"], {"available": False, "error": "TimeoutError"})
        self.assertEqual(result["disk"], {"available": True, "values": {"TotalDiskCapacity": 128}})
        disk.get_value.assert_awaited_once_with(domain="com.apple.disk_usage")
        for connection in connections:
            connection.close.assert_awaited_once()

    async def test_cancelled_device_info_checkin_closes_the_dialled_socket(self):
        connection = self.remote_lockdown_connection()
        self.rsd.start_lockdown_service_without_checkin = AsyncMock(return_value=connection)
        checkin_started = asyncio.Event()

        async def checkin(request):
            self.assertEqual(request["Request"], "RSDCheckin")
            checkin_started.set()
            await asyncio.Future()

        connection.send_recv_plist.side_effect = checkin
        with patch("device_management.create_using_remote") as open_lockdown:
            operation = asyncio.create_task(self.management.request("device_info", {}))
            await asyncio.wait_for(checkin_started.wait(), 2)
            operation.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await operation
        connection.close.assert_awaited_once()
        open_lockdown.assert_not_called()

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

    async def test_close_app_matches_only_its_main_executable(self):
        identifier = "com.example.game"
        directory = "/private/var/containers/Bundle/Application/ONE/My Game.app"
        installer = AsyncMock()
        installer.__aenter__.return_value = installer
        installer.lookup.return_value = {identifier: {"Path": directory, "CFBundleExecutable": "Game"}}
        target = {"processIdentifier": XpcInt64Type(123), "processIdentifierVersion": XpcUInt64Type(456),
                  "executableURL": {"relative": "file:///var/containers/Bundle/Application/ONE/My%20Game.app/Game"}}
        other_processes = [
            {"processIdentifier": 1, "executableURL": {"relative": directory + "/PlugIns/Game.appex/Game"}},
            {"processIdentifier": 2, "executableURL": {"relative": directory.replace("/ONE/", "/TWO/") + "/Game"}},
        ]
        self.rsd.get_service_features = lambda _: ["com.apple.coredevice.feature.streamprocesslist"]

        processes = [*other_processes, target]

        async def stream_processes(_):
            for process in processes:
                yield process

        with patch("device_management.InstallationProxyService", return_value=installer) as connect_installer, \
                patch("device_management.AppServiceService.stream_processes", stream_processes):
            self.assertEqual(await self.management.request("close_app", {"bundleId": identifier}),
                             {"status": "signal-sent", "bundleId": identifier})
            connect_installer.assert_called_once_with(self.rsd)
            installer.lookup.assert_awaited_once_with({"BundleIDs": [identifier], "ApplicationType": "Any",
                                                       "ReturnAttributes": ["Path", "CFBundleExecutable"]})
            name, packet = self.rsd.messages[-1]
            self.assertEqual(name, "com.apple.coredevice.appservice")
            self.assertEqual(packet["CoreDevice.featureIdentifier"], "com.apple.coredevice.feature.sendsignaltoprocess")
            self.assertEqual(packet["CoreDevice.input"], {
                "process": {"processIdentifier": 123, "processIdentifierVersion": 456}, "signal": 9,
            })
            self.assertIsInstance(packet["CoreDevice.input"]["process"]["processIdentifierVersion"], XpcUInt64Type)
            create_xpc_wrapper(packet, message_id=1, wanting_reply=True)
            self.assertTrue(self.rsd.channels[-1].closed)
            processes.pop()
            self.rsd.messages.clear()
            self.assertEqual(await self.management.request("close_app", {"bundleId": identifier}),
                             {"status": "not-running", "bundleId": identifier})
            self.assertEqual(self.rsd.messages, [])
            installer.lookup.reset_mock()
            with self.assertRaises(ValueError):
                await self.management.request("close_app", {"bundleId": identifier, "pid": 2})
            installer.lookup.assert_not_called()

    async def test_close_app_requires_a_complete_process_stream_before_signaling(self):
        identifier = "com.apple.mobiletimer"
        directory = "/Applications/MobileTimer.app"
        installer = AsyncMock()
        installer.__aenter__.return_value = installer
        installer.lookup.return_value = {identifier: {"Path": directory, "CFBundleExecutable": "MobileTimer"}}
        target = {"processIdentifier": 81, "executableURL": {"relative": directory + "/MobileTimer"}}
        self.rsd.get_service_features = lambda _: ["com.apple.coredevice.feature.streamprocesslist"]
        processes = [target, {"processIdentifier": 91}, {"processIdentifier": 92}]
        stream_closed = False

        async def stream(_):
            nonlocal stream_closed
            try:
                for process in processes:
                    yield process
            finally:
                stream_closed = True

        with patch("device_management.InstallationProxyService", return_value=installer), \
                patch("device_management.AppServiceService.stream_processes", stream), \
                patch("device_management.MAX_PROCESSES", 2):
            with self.assertRaisesRegex(HubError, "INVALID_DEVICE_RESPONSE"):
                await self.management.request("close_app", {"bundleId": identifier})
            self.assertTrue(stream_closed)
            self.assertTrue(self.rsd.channels[-1].closed)
            self.assertEqual(self.rsd.messages, [])
            processes.pop()
            self.assertEqual(await self.management.request("close_app", {"bundleId": identifier}),
                             {"status": "signal-sent", "bundleId": identifier})
            self.assertEqual(self.rsd.messages[-1][1]["CoreDevice.input"]["process"], {"processIdentifier": 81})

    async def test_close_app_handles_uninstalled_apps_and_refuses_ambiguous_or_failed_signals(self):
        identifier = "com.example.game"
        directory = "/Applications/Game.app"
        installer = AsyncMock()
        installer.__aenter__.return_value = installer
        arguments = {"bundleId": identifier}
        target = {"processIdentifier": 81, "executableURL": {"relative": directory + "/Game"}}
        with patch("device_management.InstallationProxyService", return_value=installer), \
                patch("device_management.AppServiceService.list_processes") as list_processes, \
                patch("device_management.AppServiceService.invoke") as invoke:
            installer.lookup.return_value = {}
            self.assertEqual(await self.management.request("close_app", arguments),
                             {"status": "not-running", "bundleId": identifier})
            installer.lookup.return_value = None
            with self.assertRaisesRegex(HubError, "INVALID_DEVICE_RESPONSE"):
                await self.management.request("close_app", arguments)
            list_processes.assert_not_called()
            installer.lookup.return_value = {identifier: {"Path": directory, "CFBundleExecutable": "Game"}}
            list_processes.return_value = [target, dict(target, processIdentifier=82)]
            with self.assertRaisesRegex(HubError, "INVALID_DEVICE_RESPONSE"):
                await self.management.request("close_app", arguments)
            invoke.assert_not_called()
            list_processes.return_value = [target]
            invoke.side_effect = CoreDeviceError("Signal rejected")
            with self.assertRaisesRegex(HubError, "APP_CLOSE_FAILED"):
                await self.management.request("close_app", arguments)
            invoke.assert_awaited_once()

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
        bridge_only = {"mirror_start", "mirror_stop"}
        for action in ACTIONS.keys() - bridge_only:
            self.assertTrue(callable(getattr(self.management, "_" + action, None)), action)
        for action in bridge_only:
            with self.assertRaises(ValueError):
                await self.management.request(action, {})
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
