"""Deletion only removes the selected, unchanged regular media file, once."""
import asyncio
from datetime import datetime
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from device_management import DeviceManagement, HubError, json_value
from pymobiledevice3.exceptions import AfcException


class DeleteFiles:
    def __init__(self):
        self.entries = {
            "/": {"st_ifmt": "S_IFDIR"},
            "/Downloads": {"st_ifmt": "S_IFDIR"},
            "/Downloads/测试.txt": {"st_ifmt": "S_IFREG", "st_size": 12, "st_mtime": datetime(2026, 11, 16)},
            "/keep.txt": {"st_ifmt": "S_IFREG", "st_size": 20, "st_mtime": 123},
            "/link": {"st_ifmt": "S_IFLNK"},
            "/special": {"st_ifmt": "S_IFSOCK"},
        }
        self.removed = []
        self.closed = False
        self.fail = False
        self.block_stat = False
        self.started = asyncio.Event()

    async def __aenter__(self): return self
    async def __aexit__(self, *args): self.closed = True
    async def stat(self, path):
        self.started.set()
        if self.block_stat: await asyncio.Future()
        if path not in self.entries: raise OSError("Missing file")
        return self.entries[path]

    async def rm_single(self, path, force=False):
        self.removed.append((path, force))
        if self.fail: raise AfcException("Permission denied", 10)
        del self.entries[path]
        return True


class DeleteTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.management = DeviceManagement(SimpleNamespace(rsd=object(), _generation=1))
        self.files = DeleteFiles()
        value = self.files.entries["/Downloads/测试.txt"]
        self.snapshot = {"size": value["st_size"], "modified": json_value(value["st_mtime"])}

    async def delete(self, **args):
        with patch("device_management.AfcService", return_value=self.files):
            return await self.management.request("delete_file", {
                "root": "media", "path": "Downloads/测试.txt", "expected": self.snapshot, **args})

    async def test_only_the_selected_file_is_deleted_once(self):
        before = self.files.entries.copy()
        result = await self.delete()
        self.assertEqual(result, {"status": "deleted", "path": "Downloads/测试.txt"})
        del before["/Downloads/测试.txt"]
        self.assertEqual(self.files.entries, before)
        self.assertEqual(self.files.removed, [("/Downloads/测试.txt", False)])
        self.assertTrue(self.files.closed)

    async def test_crashes_root_directories_symlinks_and_invalid_paths_are_refused(self):
        cases = [{"root": "crashes"}, {"root": "other"}, *({"path": path} for path in
            ("", "/", "Downloads", "../keep.txt", "/keep.txt", "Downloads/../keep.txt", "link", "link/file", "special"))]
        for args in cases:
            with self.subTest(args=args), self.assertRaises(ValueError): await self.delete(**args)
        self.assertEqual(self.files.removed, [])

    async def test_changed_size_or_time_and_missing_snapshot_never_delete(self):
        for snapshot in ({**self.snapshot, "size": 13}, {**self.snapshot, "modified": 123}):
            with self.subTest(snapshot=snapshot), self.assertRaises(HubError) as raised:
                await self.delete(expected=snapshot)
            self.assertEqual(raised.exception.code, "FILE_DELETE_CHANGED")
        for snapshot in (None, {}, {"size": True, "modified": 123}, {"size": 12}):
            with self.subTest(snapshot=snapshot), self.assertRaises(ValueError): await self.delete(expected=snapshot)
        self.assertEqual(self.files.removed, [])
        self.assertIn("/Downloads/测试.txt", self.files.entries)

    async def test_failure_and_missing_file_are_reported_without_retry(self):
        self.files.fail = True
        with self.assertRaises(HubError) as raised: await self.delete()
        self.assertEqual(raised.exception.code, "FILE_DELETE_FAILED")
        self.assertEqual(len(self.files.removed), 1)
        self.assertIn("/Downloads/测试.txt", self.files.entries)
        self.assertTrue(self.files.closed)
        with self.assertRaises(HubError) as raised: await self.delete(path="missing")
        self.assertEqual(raised.exception.code, "FILE_DELETE_FAILED")
        self.assertEqual(len(self.files.removed), 1)

    async def test_cancellation_before_remove_closes_service_without_deleting(self):
        self.files.block_stat = True
        task = asyncio.create_task(self.delete())
        await self.files.started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(self.files.removed, [])
        self.assertTrue(self.files.closed)


if __name__ == "__main__": unittest.main()
