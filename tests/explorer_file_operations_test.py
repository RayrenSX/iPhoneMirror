"""Explorer mutations use exact paths, guarded snapshots and bounded AFC work."""
import asyncio
from pathlib import Path
import sys
import struct
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from device_management import ACTIONS, CHUNK, DeviceManagement, HubError
from pymobiledevice3.exceptions import AfcException, AfcFileNotFoundError
from pymobiledevice3.services.afc import AfcOpcode


class Files:
    def __init__(self):
        self.entries = {"/": self.directory(), "/folder": self.directory(),
                        "/folder/原始.txt": self.file(b"original"), "/other": self.directory()}
        self.handles = {}
        self.removed = []
        self.renamed = []
        self.written = []
        self.closed = False
        self.denied = None
        self.after_write = None
        self.after_list = None
        self.after_stat = None
        self.block_list = None
        self.block_write = False
        self.started = asyncio.Event()
        self.clock = 10
        self.fail_rename = False
        self.time_operations = []
        self.time_error = None
        self.block_time = False

    @staticmethod
    def directory(): return {"st_ifmt": "S_IFDIR", "st_size": 0, "st_mtime": 1}

    @staticmethod
    def file(data): return {"st_ifmt": "S_IFREG", "st_size": len(data), "st_mtime": 1, "data": bytearray(data)}

    async def __aenter__(self): return self
    async def __aexit__(self, *args): self.closed = True

    async def stat(self, path):
        if path == self.denied: raise AfcException("Permission denied", 10)
        if path not in self.entries: raise AfcFileNotFoundError("Missing", 8)
        value = self.entries[path]
        result = {**value, "st_size": len(value["data"]) if "data" in value else value["st_size"]}
        if self.after_stat:
            self.after_stat(path)
        return result

    async def listdir(self, path):
        await self.stat(path)
        if path == self.block_list:
            self.started.set()
            await asyncio.Future()
        prefix = path.rstrip("/") + "/"
        names = [entry[len(prefix):] for entry in self.entries
                 if entry.startswith(prefix) and entry != path and "/" not in entry[len(prefix):]]
        if self.after_list:
            callback, self.after_list = self.after_list, None
            callback()
        return names

    async def fopen(self, path, mode):
        assert mode == "w"
        assert path not in self.entries, "An existing target must never be opened for writing"
        self.entries[path] = self.file(b"")
        handle = len(self.handles) + 1
        self.handles[handle] = path
        return handle

    async def fwrite(self, handle, data):
        path = self.handles[handle]
        self.written.append(len(data))
        self.entries[path]["data"].extend(data)
        self.started.set()
        if self.after_write:
            callback, self.after_write = self.after_write, None
            callback()
        if self.block_write: await asyncio.Future()

    async def fclose(self, handle): del self.handles[handle]

    async def _do_operation(self, opcode, payload):
        self.time_operations.append((opcode, payload))
        if self.time_error: raise self.time_error
        self.started.set()
        if self.block_time: await asyncio.Future()
        modified = struct.unpack("<Q", payload[:8])[0]
        path = payload[8:-1].decode("utf-8")
        self.entries[path]["st_mtime"] = modified / 1_000_000_000

    async def rename(self, source, target):
        if self.fail_rename: raise AfcException("Denied rename", 10)
        assert not self.handles
        await self.stat(source)
        self.renamed.append((source, target))
        moving = [(entry, value) for entry, value in self.entries.items()
                  if entry == source or entry.startswith(source + "/")]
        if target in self.entries:
            if self.entries[target]["st_ifmt"] == "S_IFDIR" and await self.listdir(target):
                raise AfcException("Not empty", 33)
            del self.entries[target]
        for entry, _ in moving: del self.entries[entry]
        for entry, value in moving:
            self.entries[target + entry[len(source):]] = value

    async def makedirs(self, path):
        if path in self.entries: raise AfcException("Exists", 16)
        assert path.rpartition("/")[0] or "/" in self.entries
        self.entries[path] = self.directory()

    async def rm_single(self, path, force=False):
        if path not in self.entries:
            if force: return False
            raise AfcFileNotFoundError("Missing", 8)
        if self.entries[path]["st_ifmt"] == "S_IFDIR" and await self.listdir(path):
            raise AfcException("Not empty", 33)
        self.removed.append(path)
        del self.entries[path]
        return True


class ExplorerFilesTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.management = DeviceManagement(SimpleNamespace(rsd=object(), _generation=1))
        self.files = Files()
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.source = Path(self.folder.name) / "spool.bin"
        self.source.write_bytes(bytes(range(256)) * (CHUNK // 128 + 1))

    async def call(self, action, **args):
        with patch("device_management.AfcService", return_value=self.files):
            return await self.management.request(action, {"root": "media", **args})

    async def snapshot(self, path): return await self.call("stat_file", path=path)

    async def assert_code(self, code, action, **args):
        with self.assertRaises(HubError) as raised: await self.call(action, **args)
        self.assertEqual(raised.exception.code, code)

    def no_temporary_files(self):
        self.assertFalse(any(".iPhoneMirror-map-" in path for path in self.files.entries))
        self.assertEqual(self.files.handles, {})

    async def test_contract_actions_are_private_and_metadata_distinguishes_missing(self):
        for action in ("stat_file", "write_mapped_file", "make_directory", "move_file", "delete_mapped_file", "set_mapped_file_time"):
            self.assertFalse(ACTIONS[action]["public"])
            self.assertEqual(ACTIONS[action]["readOnly"], action == "stat_file")
        self.assertEqual(await self.snapshot("folder/原始.txt"),
                         {"path": "folder/原始.txt", "name": "原始.txt", "type": "file", "size": 8, "modified": 1})
        self.assertEqual((await self.snapshot(""))["type"], "directory")
        await self.assert_code("FILE_NOT_FOUND", "stat_file", path="missing")
        self.files.denied = "/folder/原始.txt"
        await self.assert_code("FILE_ACCESS_DENIED", "stat_file", path="folder/原始.txt")

    async def test_exact_binary_create_and_empty_replace(self):
        result = await self.call("write_mapped_file", path="folder/新 file.bin", sourcePath=str(self.source))
        self.assertEqual(result["path"], "folder/新 file.bin")
        self.assertEqual(self.files.entries["/folder/新 file.bin"]["data"], self.source.read_bytes())
        self.assertGreater(len(self.files.written), 1)
        self.assertLessEqual(max(self.files.written), CHUNK)
        self.source.write_bytes(b"")
        result = await self.call("write_mapped_file", path=result["path"], sourcePath=str(self.source), expected=result)
        self.assertEqual(result["size"], 0)
        self.assertEqual(self.files.entries["/folder/新 file.bin"]["data"], b"")
        self.no_temporary_files()

    async def test_duplicate_and_stale_write_never_touch_target(self):
        before = await self.snapshot("folder/原始.txt")
        await self.assert_code("FILE_MAPPING_CONFLICT", "write_mapped_file", path=before["path"], sourcePath=str(self.source))
        await self.assert_code("FILE_MAPPING_CONFLICT", "write_mapped_file", path=before["path"], sourcePath=str(self.source),
                               expected={**before, "size": 100})
        self.assertEqual(self.files.entries["/folder/原始.txt"]["data"], b"original")
        self.assertEqual(self.files.renamed, [])
        self.no_temporary_files()

    async def test_write_rechecks_target_added_or_changed_while_copying(self):
        self.files.after_write = lambda: self.files.entries.update({"/new.bin": self.files.file(b"concurrent")})
        await self.assert_code("FILE_MAPPING_CONFLICT", "write_mapped_file", path="new.bin", sourcePath=str(self.source))
        self.assertEqual(self.files.entries["/new.bin"]["data"], b"concurrent")
        expected = await self.snapshot("folder/原始.txt")
        self.files.after_write = lambda: self.files.entries["/folder/原始.txt"].update(st_mtime=2)
        await self.assert_code("FILE_MAPPING_CONFLICT", "write_mapped_file", path=expected["path"],
                               sourcePath=str(self.source), expected=expected)
        self.assertEqual(self.files.entries["/folder/原始.txt"]["data"], b"original")
        self.no_temporary_files()

    async def test_source_change_and_denied_rename_keep_original(self):
        expected = await self.snapshot("folder/原始.txt")
        self.files.after_write = lambda: self.source.write_bytes(b"changed")
        await self.assert_code("FILE_CHANGED", "write_mapped_file", path=expected["path"], sourcePath=str(self.source), expected=expected)
        self.files.fail_rename = True
        await self.assert_code("FILE_ACCESS_DENIED", "write_mapped_file", path=expected["path"], sourcePath=str(self.source), expected=expected)
        self.assertEqual(self.files.entries["/folder/原始.txt"]["data"], b"original")
        self.no_temporary_files()

    async def test_cancelled_write_removes_partial_and_closes_handle(self):
        self.files.block_write = True
        task = asyncio.create_task(self.call("write_mapped_file", path="new.bin", sourcePath=str(self.source)))
        await self.files.started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.no_temporary_files()
        self.assertNotIn("/new.bin", self.files.entries)
        self.assertTrue(self.files.closed)

    async def test_mkdir_requires_parent_and_refuses_duplicates(self):
        result = await self.call("make_directory", path="folder/新目录")
        self.assertEqual(result["type"], "directory")
        await self.assert_code("FILE_MAPPING_CONFLICT", "make_directory", path=result["path"])
        await self.assert_code("FILE_NOT_FOUND", "make_directory", path="missing/child")
        self.assertNotIn("/missing", self.files.entries)

    async def test_move_directory_keeps_children_and_exact_destination(self):
        original = await self.snapshot("folder")
        result = await self.call("move_file", path="folder", destination="renamed folder", expected=original)
        self.assertEqual(result["path"], "renamed folder")
        self.assertNotIn("/folder", self.files.entries)
        self.assertEqual(self.files.entries["/renamed folder/原始.txt"]["data"], b"original")
        await self.assert_code("FILE_MAPPING_CONFLICT", "move_file", path=result["path"],
                               destination="renamed folder/sub", expected=result)

    async def test_move_overwrite_needs_matching_destination_and_preserves_on_failure(self):
        source = await self.snapshot("folder/原始.txt")
        self.files.entries["/other/target.txt"] = self.files.file(b"keep")
        target = await self.snapshot("other/target.txt")
        await self.assert_code("FILE_MAPPING_CONFLICT", "move_file", path=source["path"], destination=target["path"], expected=source)
        self.files.fail_rename = True
        await self.assert_code("FILE_ACCESS_DENIED", "move_file", path=source["path"], destination=target["path"],
                               expected=source, destinationExpected=target)
        self.assertEqual(self.files.entries["/other/target.txt"]["data"], b"keep")
        self.files.fail_rename = False
        result = await self.call("move_file", path=source["path"], destination=target["path"], expected=source, destinationExpected=target)
        self.assertEqual(result["path"], target["path"])
        self.assertEqual(self.files.entries["/other/target.txt"]["data"], b"original")

    async def test_move_rechecks_source_after_target_checks(self):
        source = await self.snapshot("folder/原始.txt")
        def change_source_after_parent_lookup(path):
            if path == "/other":
                self.files.after_stat = None
                self.files.entries["/folder/原始.txt"]["st_mtime"] = 2
        self.files.after_stat = change_source_after_parent_lookup
        await self.assert_code("FILE_MAPPING_CONFLICT", "move_file", path=source["path"],
                               destination="other/target.txt", expected=source)
        self.assertEqual(self.files.renamed, [])
        self.assertIn("/folder/原始.txt", self.files.entries)

    async def test_delete_file_empty_directory_and_recursive_tree(self):
        await self.call("delete_mapped_file", path="other", expected=await self.snapshot("other"))
        await self.assert_code("FILE_MAPPING_CONFLICT", "delete_mapped_file", path="folder", expected=await self.snapshot("folder"))
        self.assertEqual(self.files.removed, ["/other"])
        self.files.entries["/folder/nested"] = self.files.directory()
        self.files.entries["/folder/nested/child"] = self.files.file(b"data")
        result = await self.call("delete_mapped_file", path="folder", expected=await self.snapshot("folder"), recursive=True)
        self.assertEqual(result, {"status": "deleted", "path": "folder"})
        self.assertEqual(self.files.entries, {"/": self.files.directory()})
        self.assertLess(self.files.removed.index("/folder/nested/child"), self.files.removed.index("/folder/nested"))
        self.assertEqual(self.files.removed[-1], "/folder")

    async def test_recursive_preflight_rejects_links_specials_and_limits_before_delete(self):
        for kind in ("S_IFLNK", "S_IFSOCK"):
            self.files.entries["/folder/unsafe"] = {"st_ifmt": kind, "st_size": 0, "st_mtime": 1}
            with self.assertRaises(ValueError):
                await self.call("delete_mapped_file", path="folder", expected=await self.snapshot("folder"), recursive=True)
            self.assertEqual(self.files.removed, [])
        del self.files.entries["/folder/unsafe"]
        for index in range(10000): self.files.entries[f"/folder/file-{index}"] = self.files.file(b"")
        await self.assert_code("DIRECTORY_TOO_LARGE", "delete_mapped_file", path="folder", expected=await self.snapshot("folder"), recursive=True)
        self.assertEqual(self.files.removed, [])

    async def test_cancelled_delete_preflight_removes_nothing(self):
        expected = await self.snapshot("folder")
        self.files.block_list = "/folder"
        task = asyncio.create_task(self.call("delete_mapped_file", path="folder", expected=expected, recursive=True))
        await self.files.started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(self.files.removed, [])
        self.assertIn("/folder/原始.txt", self.files.entries)
        self.assertTrue(self.files.closed)

    async def test_file_time_wire_layout_is_little_endian_nanoseconds_and_utf8_path(self):
        expected = await self.snapshot("folder/原始.txt")
        ns = 1_700_000_000_123_456_700
        result = await self.call("set_mapped_file_time", path=expected["path"], expected=expected, modifiedNs=ns)
        self.assertEqual(len(self.files.time_operations), 1)
        opcode, payload = self.files.time_operations[0]
        self.assertEqual(opcode, AfcOpcode.SET_FILE_TIME)
        self.assertEqual(int(opcode), 0x1e)
        self.assertEqual(payload, struct.pack("<Q", ns) + "/folder/原始.txt".encode("utf-8") + b"\0")
        self.assertEqual(result["modified"], ns / 1_000_000_000)
        self.assertEqual(self.files.entries["/folder/原始.txt"]["data"], b"original")
        directory = await self.snapshot("folder")
        result = await self.call("set_mapped_file_time", path="folder", expected=directory, modifiedNs=0)
        self.assertEqual(result["type"], "directory")

    async def test_file_time_conflict_and_invalid_times_never_send(self):
        expected = await self.snapshot("folder/原始.txt")
        for value in (-1, 2**63, True, "123", 1.5, None):
            with self.subTest(value=value), self.assertRaises(ValueError):
                await self.call("set_mapped_file_time", path=expected["path"], expected=expected, modifiedNs=value)
        await self.assert_code("FILE_MAPPING_CONFLICT", "set_mapped_file_time", path=expected["path"],
                               expected={**expected, "size": 9}, modifiedNs=1)
        self.assertEqual(self.files.time_operations, [])

    async def test_file_time_denial_and_cancel_propagate_and_close_service(self):
        expected = await self.snapshot("folder/原始.txt")
        self.files.time_error = AfcException("Permission denied", 10)
        await self.assert_code("FILE_ACCESS_DENIED", "set_mapped_file_time", path=expected["path"], expected=expected, modifiedNs=1)
        self.assertTrue(self.files.closed)
        self.files.time_error = None
        self.files.block_time = True
        self.files.started.clear()
        task = asyncio.create_task(self.call("set_mapped_file_time", path=expected["path"], expected=expected, modifiedNs=1))
        await self.files.started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual((await self.snapshot(expected["path"]))["modified"], expected["modified"])

    async def test_all_mutations_reject_root_cross_service_traversal_and_links(self):
        self.files.entries["/link"] = {"st_ifmt": "S_IFLNK", "st_size": 0, "st_mtime": 1}
        for action in ("write_mapped_file", "make_directory", "move_file", "delete_mapped_file", "set_mapped_file_time"):
            for path in ("", "/absolute", "../escape", "folder/../escape", "link/child", "a\\b", "a:b"):
                with self.subTest(action=action, path=path), self.assertRaises((ValueError, HubError)):
                    await self.call(action, path=path, destination="target", expected={"type": "file", "size": 0, "modified": 1}, sourcePath=str(self.source), modifiedNs=1)
            with self.assertRaises(ValueError):
                await self.call(action, path="safe", root="crashes", sourcePath=str(self.source))
        self.assertEqual(self.files.renamed, [])
        self.assertEqual(self.files.removed, [])


if __name__ == "__main__": unittest.main()
