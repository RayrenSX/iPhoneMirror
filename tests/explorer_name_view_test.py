import asyncio
from pathlib import Path
import sys
import unittest
from unittest.mock import AsyncMock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from explorer_name_view import ExplorerNameView, name_map
from explorer_system_files import SystemDirectoryFiles
from explorer_file_operations_test import Files


class NameViewTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.files = Files()
        self.files.entries.update({'/date+08:00': self.files.directory(), '/date+08:00/a||b': self.files.file(b'abc'),
                                  '/CON': self.files.file(b'x'), '/end. ': self.files.file(b'y'),
                                  '/Aa': self.files.file(b'1'), '/aa': self.files.file(b'2'),
                                  '/date%2B08%3A00': self.files.file(b'3')})
        self.view = ExplorerNameView(self.files)

    async def test_illegal_windows_names_are_listed_and_resolve_to_originals(self):
        names = await self.view.listdir('/')
        self.assertIn('date+08%3A00', names)
        self.assertIn('%43ON', names)
        self.assertIn('end.%20', names)
        self.assertEqual(await self.view.listdir('/date+08%3A00'), ['a%7C%7Cb'])
        self.assertEqual((await self.view.stat('/date+08%3A00/a%7C%7Cb'))['st_size'], 3)
        self.assertEqual(len({name.casefold() for name in names}), len(names))
        self.assertEqual(names, await self.view.listdir('/'))
        self.assertEqual(len(name_map(['a:b', 'a%3Ab'])), 2)
        mapping = name_map(['a:b', 'a%3Ab'])
        self.assertEqual({native for _, native in mapping.values()}, {'a:b', 'a%3Ab'})

    async def test_mutations_keep_native_parent_and_new_literal_names(self):
        await self.view.makedirs('/date+08%3A00/new')
        self.assertIn('/date+08:00/new', self.files.entries)
        await self.view.rename('/date+08%3A00/a%7C%7Cb', '/date+08%3A00/new/file.bin')
        self.assertIn('/date+08:00/new/file.bin', self.files.entries)
        await self.view.rm_single('/date+08%3A00/new/file.bin')
        self.assertNotIn('/date+08:00/new/file.bin', self.files.entries)

    async def test_new_target_after_first_missing_stat_is_seen(self):
        with self.assertRaises(FileNotFoundError): await self.view.stat('/new.txt')
        self.files.entries['/new.txt'] = self.files.file(b'concurrent')
        self.assertEqual((await self.view.stat('/new.txt'))['st_size'], 10)

    async def test_readable_links_are_read_only_and_nested_links_cannot_escape(self):
        self.files.entries['/alias'] = {'st_ifmt': 'S_IFLNK', 'LinkTarget': 'date+08:00', 'st_size': 0, 'st_mtime': 1}
        self.files.entries['/chain'] = {'st_ifmt': 'S_IFLNK', 'LinkTarget': 'alias/a||b', 'st_size': 0, 'st_mtime': 1}
        value = await self.view.stat('/chain')
        self.assertEqual(value['st_size'], 3)
        self.assertTrue(value['readOnly'])
        self.assertTrue((await self.view.stat('/alias/a%7C%7Cb'))['readOnly'])
        for action in (lambda: self.view.makedirs('/alias/new'), lambda: self.view.rm_single('/chain'),
                       lambda: self.view.rename('/chain', '/new')):
            with self.assertRaises(PermissionError): await action()
        self.files.entries['/bad'] = {'st_ifmt': 'S_IFLNK', 'LinkTarget': '../outside', 'st_size': 0}
        self.files.entries['/indirect'] = {'st_ifmt': 'S_IFLNK', 'LinkTarget': 'bad/file', 'st_size': 0}
        self.view.names.clear()
        with self.assertRaises(PermissionError): await self.view.stat('/indirect')

    async def test_cycles_missing_targets_and_unlisted_paths_fail(self):
        self.files.entries['/loop'] = {'st_ifmt': 'S_IFLNK', 'LinkTarget': 'loop', 'st_size': 0}
        self.files.entries['/missing'] = {'st_ifmt': 'S_IFLNK', 'LinkTarget': 'absent', 'st_size': 0}
        with self.assertRaises(PermissionError): await self.view.stat('/loop')
        with self.assertRaises(Exception): await self.view.stat('/missing')
        for path in ('/../outside', '/a/../b', '/date+08:00'):
            with self.assertRaises((ValueError, FileNotFoundError)): await self.view.stat(path)

    async def test_name_maps_bound_lengths_and_preserve_unicode(self):
        names = ['中文😀.txt', 'x' * 240 + ':', 'CON.txt', 'LPT1', 'trailing.']
        aliases = list(name_map(names).values())
        self.assertIn(('中文😀.txt', '中文😀.txt'), aliases)
        self.assertTrue(all(len(alias.encode('utf-8')) <= 255 for alias, _ in aliases))
        self.assertTrue(all(not any(char in alias for char in '<>:"/\\|?*') for alias, _ in aliases))


class SystemDirectoriesTests(unittest.IsolatedAsyncioTestCase):
    async def test_only_directories_that_open_are_shown_and_denied_siblings_do_not_hide_them(self):
        active = 0
        maximum = 0
        async def listing(path):
            nonlocal active, maximum
            active += 1
            maximum = max(maximum, active)
            try:
                await asyncio.sleep(0)
                if path == '/.': return ['usr', 'blocked', 'file.txt', 'empty']
                if path == '/usr/.': return ['lib']
                if path == '/empty/.': return []
                raise PermissionError(path)
            finally: active -= 1
        service = SystemDirectoryFiles(type('Info', (), {'ls': staticmethod(listing)})())
        self.assertEqual(await service.listdir('/'), ['usr', 'empty'])
        self.assertTrue((await service.stat('/usr'))['readOnly'])
        self.assertLessEqual(maximum, 8)
        self.assertEqual(service.directories['/usr'], ['lib'])
        with self.assertRaises(ValueError): await service.stat('/usr/../etc')


if __name__ == '__main__': unittest.main()
