import sys
import asyncio
from pathlib import Path
import unittest
import tempfile
import struct
from types import SimpleNamespace
from unittest.mock import patch, AsyncMock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from device_management import DeviceManagement, HubError
from document_file_service import DocumentsFiles
from explorer_file_operations_test import Files


class Documents(Files):
    def __init__(self, *args, **kwargs):
        super().__init__()
        self.entries = {'/Documents': self.directory(), '/Documents/中文.txt': self.file(b'original'), '/Library': self.directory()}
        self.vended = False
        self.entered = False

    async def send_command(self, identifier, command):
        assert not self.entered
        assert identifier == 'com.test.documents' and command == 'VendDocuments'
        self.vended = True

    async def __aenter__(self):
        assert self.vended
        self.entered = True
        return self

    async def close(self): self.closed = True
    async def __aexit__(self, *args):
        self.entered = False
        self.closed = True


class AppDocumentsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.files = Documents()
        self.hub = DeviceManagement(SimpleNamespace(rsd=object()))

    def scope(self, **kwargs):
        return dict(root='documents', bundleId='com.test.documents', **kwargs)

    async def test_vend_before_reader_and_confined_listing(self):
        with patch('explorer_file_roots.HouseArrestService', return_value=self.files):
            result = await self.hub._list_dir(self.scope(path=''))
        self.assertEqual([x['name'] for x in result['entries']], ['中文.txt'])
        self.assertTrue(self.files.closed)

    async def test_write_time_rename_delete_stay_in_documents(self):
        with patch('explorer_file_roots.HouseArrestService', return_value=self.files):
            with tempfile.TemporaryDirectory() as directory:
                source = Path(directory) / 'source.txt'
                source.write_bytes(b'new content')
                result = await self.hub._write_mapped_file(self.scope(path='new.txt', sourcePath=str(source), expected=None))
            self.assertEqual(bytes(self.files.entries['/Documents/new.txt']['data']), b'new content')
            result = await self.hub._set_mapped_file_time(self.scope(path='new.txt', expected=result, modifiedNs=1_000_000_000))
            self.assertIn(b'/Documents/new.txt\0', self.files.time_operations[-1][1])
            result = await self.hub._move_file(self.scope(path='new.txt', destination='renamed.txt', expected=result, destinationExpected=None))
            await self.hub._delete_mapped_file(self.scope(path='renamed.txt', expected=result))
            self.assertNotIn('/Documents/renamed.txt', self.files.entries)
            self.assertIn('/Library', self.files.entries)

    async def test_invalid_bundle_or_escape_never_vends(self):
        for path in ('../Library', 'a/../../Library', 'a\\b'):
            with self.assertRaises(ValueError):
                DocumentsFiles.path(path)
        with patch('explorer_file_roots.HouseArrestService') as factory:
            with self.assertRaises(ValueError):
                await self.hub._stat_file(dict(root='documents', bundleId='../bad', path=''))
            factory.assert_not_called()

    async def test_failed_vend_closes_service_before_afc_reader(self):
        self.files.send_command = AsyncMock(side_effect=ConnectionError('denied'))
        with patch('explorer_file_roots.HouseArrestService', return_value=self.files):
            with self.assertRaises(ConnectionError):
                await self.hub._list_dir(self.scope(path=''))
        self.assertTrue(self.files.closed)
        self.assertFalse(self.files.entered)

    async def test_cancelled_listing_closes_and_symlinks_cannot_escape(self):
        self.files.block_list = '/Documents'
        with patch('explorer_file_roots.HouseArrestService', return_value=self.files):
            task = asyncio.create_task(self.hub._list_dir(self.scope(path='')))
            await self.files.started.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
            self.assertTrue(self.files.closed)
            self.files.entries['/Documents/link'] = {'st_ifmt': 'S_IFLNK', 'st_size': 0}
            with self.assertRaises(ValueError):
                await self.hub._list_dir(self.scope(path='link'))

    async def test_only_file_sharing_apps_are_listed(self):
        service = SimpleNamespace(browse=AsyncMock(return_value=[
            {'CFBundleIdentifier': 'com.test.private', 'CFBundleDisplayName': 'Private'},
            {'CFBundleIdentifier': 'com.test.string', 'UIFileSharingEnabled': 'true'},
            {'CFBundleIdentifier': 'com.test.documents', 'CFBundleDisplayName': 'Docs', 'UIFileSharingEnabled': True}]),
            close=AsyncMock(), __aenter__=None)
        from contextlib import asynccontextmanager
        @asynccontextmanager
        async def connection(*args, **kwargs):
            try: yield service
            finally: await service.close()
        with patch('device_management.connected', connection):
            result = await self.hub._shared_document_apps({})
        self.assertEqual(result, {'apps': [{'bundleId': 'com.test.documents', 'name': 'Docs'}]})
        service.close.assert_awaited_once()


if __name__ == '__main__': unittest.main()
