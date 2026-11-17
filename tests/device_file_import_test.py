"""File imports preserve existing content and clean up interrupted transfers."""
import asyncio
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from device_management import CHUNK, DeviceManagement, HubError


class ImportFiles:
    def __init__(self):
        self.files = {}
        self.handles = {}
        self.chunks = []
        self.closed = False
        self.on_write = None
        self.started = asyncio.Event()
        self.block = False
        self.fail = False

    async def __aenter__(self): return self
    async def __aexit__(self, *args): self.closed = True

    async def stat(self, path):
        if path in ("/", "/Downloads"):
            return {"st_ifmt": "S_IFDIR"}
        if path == "/link":
            return {"st_ifmt": "S_IFLNK"}
        return {"st_ifmt": "S_IFREG", "st_size": len(self.files[path])}

    async def listdir(self, path):
        prefix = path.rstrip("/") + "/"
        return [name[len(prefix):] for name in self.files if name.startswith(prefix) and "/" not in name[len(prefix):]]

    async def fopen(self, path, mode):
        assert mode == "w" and path not in self.files
        self.files[path] = bytearray()
        self.handles[7] = path
        return 7

    async def fwrite(self, handle, data):
        self.chunks.append(len(data))
        self.files[self.handles[handle]].extend(data)
        if self.on_write:
            callback, self.on_write = self.on_write, None
            callback()
        self.started.set()
        if self.block:
            await asyncio.Future()
        if self.fail:
            raise OSError("Device full")

    async def fclose(self, handle): del self.handles[handle]
    async def rm_single(self, path, force): self.files.pop(path, None)

    async def rename(self, source, target):
        assert not self.handles, "The file must be closed before publishing it"
        assert target not in self.files, "Existing files must survive imports"
        self.files[target] = self.files.pop(source)


class ImportTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.management = DeviceManagement(SimpleNamespace(rsd=object(), _generation=1))
        self.files = ImportFiles()
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.source = Path(self.folder.name) / "测试 video.bin"

    async def upload(self, **args):
        with patch("device_management.AfcService", return_value=self.files):
            return await self.management.request("upload_file", {"sourcePath": str(self.source), **args})

    async def test_binary_and_empty_files_in_open_directory(self):
        content = bytes(range(256)) * (CHUNK // 128 + 1) + b"\x00\xff"
        self.source.write_bytes(content)
        result = await self.upload(path="Downloads")
        self.assertEqual(result["path"], "Downloads/测试 video.bin")
        self.assertEqual(result["size"], len(content))
        self.assertEqual(self.files.files["/" + result["path"]], content)
        self.assertGreater(len(self.files.chunks), 1)
        self.assertLessEqual(max(self.files.chunks), CHUNK)
        self.assertTrue(self.files.closed)
        self.source.write_bytes(b"")
        result = await self.upload()
        self.assertEqual(self.files.files["/测试 video.bin"], b"")
        self.assertEqual(result["size"], 0)

    async def test_name_conflicts_keep_both_including_files_added_during_transfer(self):
        self.source.write_bytes(b"new content")
        self.files.files["/Downloads/测试 VIDEO.bin"] = b"original"
        self.files.on_write = lambda: self.files.files.update({"/Downloads/测试 video (1).bin": b"concurrent"})
        result = await self.upload(path="Downloads")
        self.assertEqual(result["name"], "测试 video (2).bin")
        self.assertEqual(self.files.files, {
            "/Downloads/测试 VIDEO.bin": b"original", "/Downloads/测试 video (1).bin": b"concurrent",
            "/Downloads/测试 video (2).bin": b"new content"})

    async def test_crash_reports_traversal_and_symlinks_are_not_writable(self):
        self.source.write_bytes(b"test")
        for args in ({"root": "crashes"}, {"path": "../Downloads"}, {"path": "link/child"}):
            with self.subTest(args=args), self.assertRaises(ValueError):
                await self.upload(**args)
        self.assertEqual(self.files.files, {})

    async def test_cancelled_transfer_closes_handle_and_removes_partial_file(self):
        self.source.write_bytes(b"a" * (CHUNK + 1))
        self.files.block = True
        task = asyncio.create_task(self.upload())
        await self.files.started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(self.files.files, {})
        self.assertEqual(self.files.handles, {})
        self.assertTrue(self.files.closed)

    async def test_write_failure_does_not_publish_a_partial_file_or_touch_existing(self):
        self.source.write_bytes(b"a" * (CHUNK + 1))
        self.files.files["/测试 video.bin"] = b"keep me"
        self.files.fail = True
        with self.assertRaises(HubError) as raised: await self.upload()
        self.assertEqual(raised.exception.code, "FILE_IMPORT_FAILED")
        self.assertEqual(self.files.files, {"/测试 video.bin": b"keep me"})
        self.assertEqual(self.files.handles, {})
        self.assertTrue(self.files.closed)

    async def test_source_changes_are_not_published(self):
        self.source.write_bytes(b"a" * (CHUNK + 1))
        self.files.on_write = lambda: self.source.write_bytes(b"changed")
        with self.assertRaises(HubError) as raised: await self.upload()
        self.assertEqual(raised.exception.code, "FILE_CHANGED")
        self.assertEqual(self.files.files, {})
        self.assertTrue(self.files.closed)


if __name__ == "__main__": unittest.main()
