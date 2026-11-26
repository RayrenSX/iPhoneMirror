"""Readable root discovery, bounded CoreDevice reads and per-file permissions."""
import asyncio
from contextlib import asynccontextmanager
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from device_management import ACTIONS, DeviceManagement, HubError
from explorer_file_roots import CoreDeviceFiles, application_files, application_root, coredevice_files, file_read_only
from explorer_system_files import ExplorerDvtProvider, system_directories
from explorer_file_operations_test import Files


class ExplorerRootsTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancelled_system_handshake_closes_transport(self):
        entered = asyncio.Event()
        async def handshake():
            entered.set()
            await asyncio.Future()
        connection = SimpleNamespace(connect=handshake, aclose=AsyncMock())
        provider = object.__new__(ExplorerDvtProvider)
        provider._dtx, provider._owns_dtx = None, True
        provider._service_name, provider._strip_ssl, provider.sent_capabilities = 'fixture', False, {}
        provider._open_dtx_connection = AsyncMock(return_value=connection)
        async def read():
            async with system_directories(object()):
                self.fail('Cancelled handshake yielded files')
        with patch('explorer_system_files.ExplorerDvtProvider', return_value=provider):
            task = asyncio.create_task(read())
            await entered.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        connection.aclose.assert_awaited_once()
        self.assertIsNone(provider._dtx)

    async def test_initial_discovery_does_not_wait_for_apps_or_optional_services(self):
        management = DeviceManagement(SimpleNamespace(rsd=object()))
        with patch('device_management.readable', AsyncMock(return_value=True)) as probe, \
             patch('device_management.connected', side_effect=AssertionError('App inventory delayed startup')), \
             patch('device_management.system_directories', side_effect=AssertionError('Optional probe delayed startup')):
            result = await management._explorer_roots({'initialOnly': True})
        self.assertEqual(result, {'roots': [{'root': 'media', 'readOnly': False}, {'root': 'crashes', 'readOnly': True}],
                                  'total': 0, 'nextOffset': 0, 'pending': True})
        self.assertEqual(probe.await_count, 2)
        self.assertIsNone(management._explorer_apps)

    async def test_discovery_pages_all_apps_without_filtering_file_sharing_and_deduplicates_groups(self):
        apps = [{'CFBundleIdentifier': f'com.test.app{index}', 'CFBundleDisplayName': f'App {index}',
                 'GroupContainers': {'group.test': '/private/path'}, 'UIFileSharingEnabled': index == 9} for index in range(10)]
        installer = SimpleNamespace(browse=AsyncMock(return_value=apps))
        @asynccontextmanager
        async def connection(*args, **kwargs):
            yield installer
        owner = SimpleNamespace(rsd=object())
        management = DeviceManagement(owner)
        async def app_root(rsd, app, core_probe):
            self.assertIs(rsd, owner.rsd)
            return {'root': 'container', 'name': app['name'], 'bundleId': app['bundleId'], 'readOnly': False}
        with patch('device_management.connected', connection), patch('device_management.readable', AsyncMock(return_value=False)), \
             patch('device_management.application_root', side_effect=app_root) as probe:
            first = await management._explorer_roots({'offset': 0, 'limit': 4})
            second = await management._explorer_roots({'offset': first['nextOffset'], 'limit': 4})
            third = await management._explorer_roots({'offset': second['nextOffset'], 'limit': 4})
        self.assertEqual((first['total'], first['nextOffset'], third['nextOffset']), (11, 4, None))
        self.assertEqual(len(first['roots']) + len(second['roots']) + len(third['roots']), 10)
        self.assertEqual(probe.await_count, 10)
        self.assertEqual(first['roots'][0]['bundleId'], 'com.test.app9')
        installer.browse.assert_awaited_once()
        self.assertEqual(ACTIONS['explorer_roots'], {'readOnly': True, 'public': False, 'timeout': 60})

    async def test_container_preferred_documents_fallback_then_coredevice_and_denied_omitted(self):
        for results, expected in [([True], 'container'), ([False, True], 'documents'),
                                  ([False, False, True], 'coredevice'), ([False, False, False], None)]:
            with self.subTest(expected=expected), patch('explorer_file_roots.readable', AsyncMock(side_effect=results)):
                result = await application_root(object(), {'bundleId': 'com.test.app', 'name': 'App'})
                self.assertEqual(result['root'] if result else None, expected)

    async def test_vend_precedes_afc_and_failure_or_cancel_closes(self):
        service = SimpleNamespace(send_command=AsyncMock(side_effect=PermissionError()), close=AsyncMock())
        with patch('explorer_file_roots.HouseArrestService', return_value=service):
            with self.assertRaises(PermissionError):
                async with application_files(object(), 'com.test.app', False):
                    self.fail('A denied container was yielded')
        service.close.assert_awaited_once()
        service.send_command.assert_awaited_once_with('com.test.app', 'VendContainer')
        service.send_command.side_effect = asyncio.CancelledError()
        with patch('explorer_file_roots.HouseArrestService', return_value=service):
            with self.assertRaises(asyncio.CancelledError):
                async with application_files(object(), 'com.test.app', True):
                    pass
        self.assertEqual(service.close.await_count, 2)

    async def test_trusted_route_is_pinned_to_rsd_device_and_closed(self):
        rsd = SimpleNamespace(udid='fixture-device')
        lockdown = SimpleNamespace(udid='fixture-device', close=AsyncMock())
        service = Files()
        service.send_command = AsyncMock()
        service.close = AsyncMock()
        with patch('explorer_file_roots.create_using_usbmux', AsyncMock(return_value=lockdown)) as connect, \
             patch('explorer_file_roots.HouseArrestService', return_value=service) as factory:
            async with application_files(rsd, 'com.test.app', False) as files:
                self.assertIs(files, service)
            connect.assert_awaited_once_with(serial='fixture-device', autopair=False)
            factory.assert_called_once_with(lockdown, documents_only=False)
        lockdown.close.assert_awaited_once()

    async def test_coredevice_metadata_bounded_offsets_and_handles(self):
        requests = []
        def require(service, feature):
            self.assertEqual(feature, 'com.apple.coredevice.feature.filesystemoperation')
        async def request(args):
            requests.append(args)
            return {'GetAttributes': {'Attributes': json.dumps({'fileType': 'regular', 'size': 4, 'modificationTime': 10, 'permissions': 0o444})},
                    'OpenFile': {'FileDescriptor': 7}, 'ReadFile': {'FileData': b'ab'}, 'CloseFile': {}}[args['OperationType']]
        service = SimpleNamespace(rsd=SimpleNamespace(require_feature=require), CTRL_SERVICE_NAME='files', session='fixture',
                                  service=SimpleNamespace(next_message_id={1: 2}), send_receive_request=request, retrieve_directory_list=AsyncMock(return_value=['a.txt']))
        files = CoreDeviceFiles(service)
        metadata = await files.stat('/a.txt')
        self.assertEqual((metadata['st_mtime'], metadata['st_ifmt'], metadata['readOnly']), (978307210, 'S_IFREG', True))
        handle = await files.fopen('/a.txt', 'r')
        await files.fseek(handle, 2)
        self.assertEqual(await files.fread(handle, 2), b'ab')
        self.assertEqual(requests[-1]['Offset'], 2)
        self.assertEqual(requests[-1]['Length'], 2)
        self.assertFalse(requests[0]['FollowSymlinks'])
        with self.assertRaises(ValueError): await files.fread(handle, 192 * 1024 + 1)
        with self.assertRaises(PermissionError): await files.fopen('/a.txt', 'w')
        await files.fclose(handle)
        self.assertEqual(files.positions, {})
        for path in ('/../outside', '/a/../outside', '/a\\b'):
            with self.assertRaises(ValueError): CoreDeviceFiles.path(path)

    async def test_native_directory_pages_fixed_request_id_and_handle_cleanup(self):
        transport = SimpleNamespace(next_message_id={1: 99})
        requests = []
        pages = [
            {'Entries': [{'entry': {'name': '.'}}, {'entry': {'name': '..'}}, {'entry': {'name': 'Library'}}]},
            {'Entries': [{'entry': {'name': 'Documents'}}]},
            {'Response': 1},
        ]
        async def request(args):
            self.assertEqual(transport.next_message_id[1], 1)
            transport.next_message_id[1] = 2
            requests.append(args)
            if args['OperationType'] == 'OpenDirectory': return {'DirectoryDescriptor': 10000}
            if args['OperationType'] == 'ReadEntryWithAttributes': return pages.pop(0)
            return {}
        service = SimpleNamespace(rsd=SimpleNamespace(require_feature=lambda *args: None), service=transport,
                                  CTRL_SERVICE_NAME='files', session='fixture', send_receive_request=request)
        self.assertEqual(await CoreDeviceFiles(service).listdir('/'), ['Library', 'Documents'])
        self.assertEqual(requests[-1]['OperationType'], 'CloseDirectory')
        self.assertEqual(requests[-1]['DirectoryDescriptor'], 10000)
        self.assertEqual(len([item for item in requests if item['OperationType']=='ReadEntryWithAttributes']),3)

    async def test_cancelled_native_directory_read_closes_its_handle(self):
        entered=asyncio.Event()
        calls=[]
        files=CoreDeviceFiles(object())
        async def operation(kind,**args):
            calls.append(kind)
            if kind=='OpenDirectory': return {'DirectoryDescriptor':7}
            if kind=='ReadEntryWithAttributes':
                entered.set()
                await asyncio.Future()
            return {}
        files.operation=operation
        task=asyncio.create_task(files.listdir('/Library'))
        await entered.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual(calls[-1],'CloseDirectory')

    async def test_application_domain_exposes_accessible_children_when_container_root_is_denied(self):
        from pymobiledevice3.remote.core_device.file_service import Domain
        service = SimpleNamespace(domain=Domain.APP_DATA_CONTAINER)
        files = CoreDeviceFiles(service)
        async def operation(kind, **args):
            path = args.get('Path')
            if path == '.': self.fail('An application container root must not be probed as a native path')
            if kind == 'GetAttributes':
                if path=='tmp': raise PermissionError()
                return {'Attributes': json.dumps({'fileType':'directory', 'size':0, 'modificationTime':1})}
            if kind == 'OpenDirectory': return {'DirectoryDescriptor':10000}
            if kind == 'ReadEntryWithAttributes': return {'Response':1}
            return {}
        files.operation = operation
        self.assertEqual(await files.listdir('/'),['Documents','Library'])
        self.assertTrue((await files.stat('/'))['readOnly'])

    async def test_document_apps_keep_writable_documents_and_add_readable_library_scope(self):
        app={'bundleId':'com.test.app','name':'App'}
        with patch('explorer_file_roots.readable',AsyncMock(side_effect=[False,True])):
            result=await application_root(object(),app,AsyncMock(return_value=True))
        self.assertEqual(result['root'],'documents')
        self.assertFalse(result['readOnly'])
        extra=result['additionalRoots'][0]
        self.assertEqual((extra['root'],extra['domain'],extra['identifier']),('coredevice','appDataContainer','com.test.app'))
        self.assertTrue(extra['readOnly'])

    async def test_failed_coredevice_session_is_closed(self):
        rsd = SimpleNamespace(require_feature=lambda *args: None)
        service = SimpleNamespace(session=None, connect=AsyncMock(side_effect=PermissionError()), close=AsyncMock())
        with patch('explorer_file_roots.FileServiceService', return_value=service) as factory:
            factory.CTRL_SERVICE_NAME = 'files'
            with self.assertRaises(PermissionError):
                async with coredevice_files(rsd, 'temporary', ''):
                    pass
        service.close.assert_awaited_once()

    async def test_timeout_marks_core_service_unresponsive_without_repeating_for_each_app(self):
        hub = DeviceManagement(SimpleNamespace(rsd=object()))
        hub._explorer_apps = [{'bundleId': 'com.test.app', 'name': 'App'} for _ in range(5)]
        async def timeout(context, *, on_timeout=None):
            if on_timeout is not None:
                on_timeout()
            return False
        with patch('device_management.readable', side_effect=timeout) as probe, \
             patch('device_management.CORE_FILE_OPERATIONS_VERIFIED', True), \
             patch('device_management.application_root', AsyncMock(return_value=None)):
            hub._explorer_apps = []
            hub._explorer_groups = ['group.one', 'group.two', 'group.three', 'group.four']
            result = await hub._explorer_roots({'offset': 1, 'limit': 2})
        self.assertEqual(probe.await_count, 1)
        self.assertTrue(hub._explorer_core_unresponsive)
        self.assertEqual(result['roots'], [])

    async def test_unverified_core_rpc_is_not_probed_during_discovery(self):
        hub = DeviceManagement(SimpleNamespace(rsd=object()))
        hub._explorer_apps = []
        hub._explorer_groups = ['group.one', 'group.two']
        with patch('device_management.CORE_FILE_OPERATIONS_VERIFIED', False), patch('device_management.readable', AsyncMock()) as probe:
            result = await hub._explorer_roots({'offset': 1, 'limit': 1})
        probe.assert_not_awaited()
        self.assertEqual(result['roots'], [])

    async def test_crash_stat_is_readable_and_all_mutations_reject_read_only_domains(self):
        hub = DeviceManagement(SimpleNamespace(rsd=object()))
        files = Files()
        with patch('device_management.AfcService', return_value=files):
            self.assertEqual((await hub._stat_file({'root': 'crashes', 'path': 'folder/原始.txt'}))['size'], 8)
        for root in ('crashes', 'coredevice'):
            for action in ('write_mapped_file', 'make_directory', 'move_file', 'delete_mapped_file', 'set_mapped_file_time'):
                with self.subTest(root=root, action=action), self.assertRaises(ValueError):
                    await hub.request(action, {'root': root, 'path': 'a.txt'})

    async def test_individual_file_permissions_and_recursive_delete_preflight(self):
        for value in ({'permissions': 0o444}, {'st_mode': '0444'}, {'st_flags': 2}, {'readOnly': True}):
            self.assertTrue(file_read_only(value))
        self.assertFalse(file_read_only({'permissions': 0o644}))
        hub, files = DeviceManagement(SimpleNamespace(rsd=object())), Files()
        files.entries['/folder/原始.txt']['permissions'] = 0o444
        with patch('device_management.AfcService', return_value=files):
            page = await hub._list_dir({'root': 'media', 'path': 'folder'})
            self.assertTrue(page['entries'][0]['readOnly'])
            current = await hub._stat_file({'root': 'media', 'path': 'folder/原始.txt'})
            folder = await hub._stat_file({'root': 'media', 'path': 'folder'})
            for path, expected in [('folder/原始.txt', current), ('folder', folder)]:
                with self.assertRaises(HubError) as error:
                    await hub._delete_mapped_file({'root': 'media', 'path': path, 'expected': expected, 'recursive': True})
                self.assertEqual(error.exception.code, 'FILE_ACCESS_DENIED')
        self.assertEqual(files.removed, [])


if __name__ == '__main__': unittest.main()
