"""DDI download candidates and HID source fallback regression tests."""
import asyncio
import hashlib
import json
import os
import plistlib
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b


def assets_for(data, revision='test-revision'):
    return tuple(b.PersonalizedDdiAsset(name, name,
        hashlib.sha1(f'blob {len(data[name])}\0'.encode() + data[name]).hexdigest(),
        len(data[name]), hashlib.sha256(data[name]).hexdigest(), revision)
        for name in b.PERSONALIZED_DDI_FILES)


class DownloadSourceTests(unittest.TestCase):
    def setUp(self):
        self.directory = self.enterContext(TemporaryDirectory())
        self.root = Path(self.directory) / 'cache'
        self.data = {'Image.dmg': b'image', 'Image.trustcache': b'trust',
                     'BuildManifest.plist': plistlib.dumps({'ProductBuildVersion': b.LATEST_DDI_BUILD_ID})}
        self.assets = assets_for(self.data)
        self.enterContext(patch.object(b, '_personalized_ddi_cache_directory', return_value=self.root))
        self.enterContext(patch.object(b, 'PERSONALIZED_DDI_ASSETS', self.assets))
        self.enterContext(patch.object(b, 'PERSONALIZED_DDI_GITHUB_REF', b.PERSONALIZED_DDI_PINNED_REVISION))
        self.enterContext(patch.object(b.requests.sessions.Session, 'send',
            side_effect=AssertionError('unit tests must not use the network')))
        self.source = b.PersonalizedDdiDownloadSource('raw', 'raw')
        self.enterContext(patch.object(b, '_personalized_ddi_download_sources', return_value=(self.source,)))

    def download(self, source, asset, path, **kwargs):
        kwargs['check_cancelled']()
        path.write_bytes(self.data[asset.local_name])

    def install_cache(self):
        self.root.mkdir()
        for name, content in self.data.items():
            (self.root / name).write_bytes(content)
        b._write_ddi_metadata(self.root, self.assets, b.LATEST_DDI_BUILD_ID)
        return tuple(self.root / name for name in b.PERSONALIZED_DDI_FILES)

    def test_default_pinned_build_needs_no_metadata_api(self):
        with patch.object(b, '_resolve_github_personalized_ddi_assets', side_effect=AssertionError('API required')), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download):
            result = b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(result, b._valid_personalized_ddi_bundle(self.root))

    def test_local_cache_io_failures_are_actionable(self):
        for owner, name in ((b.Path, 'mkdir'), (b.tempfile, 'mkdtemp'),
                            (b, '_write_ddi_metadata'), (b.os, 'replace')):
            with self.subTest(operation=name), \
                 patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download), \
                 patch.object(owner, name, side_effect=PermissionError('cache write denied')):
                with self.assertRaises(b.BridgePrerequisiteError) as raised:
                    b.fetch_automatic_personalized_ddi_bundle()
                self.assertEqual(raised.exception.code, 'developer_image_cache_failed')

    def test_disk_write_failure_does_not_retry_other_sources(self):
        response = MagicMock(status_code=200, headers={})
        response.__enter__.return_value = response
        with patch.object(b.requests, 'get', return_value=response) as request, \
             patch.object(b, '_validate_ddi_source_response'), \
             patch.object(b.Path, 'open', side_effect=OSError(b.errno.ENOSPC, 'disk full')):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b._download_ddi_bundle(self.root, self.assets, None, lambda: None)
        self.assertEqual(raised.exception.code, 'developer_image_cache_failed')
        request.assert_called_once()

    def test_refresh_preserves_files_already_selected_for_mount(self):
        old_bundle = self.install_cache()
        old_data = [path.read_bytes() for path in old_bundle]
        self.data['Image.dmg'] = b'new compatible image'
        new_assets = assets_for(self.data, 'new-revision')
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=new_assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download):
            new_bundle = b.fetch_automatic_personalized_ddi_bundle(force_refresh=True)
        self.assertEqual([path.read_bytes() for path in old_bundle], old_data)
        self.assertEqual(new_bundle[0].read_bytes(), self.data['Image.dmg'])
        self.assertEqual(b._valid_personalized_ddi_bundle(self.root), new_bundle)

    def test_failed_publication_preserves_previous_cache(self):
        old_bundle = self.install_cache()
        old_data = [path.read_bytes() for path in old_bundle]
        self.data['Image.dmg'] = b'new compatible image'
        new_assets = assets_for(self.data, 'new-revision')
        replace = os.replace
        def fail_after_first_publication(source, target):
            if Path(target).name == '.iphoneMirror-ddi-current.json':
                raise OSError('injected publication interruption')
            directory = Path(source).is_dir()
            replace(source, target)
            if not directory and self.root in Path(target).parents:
                raise OSError('injected publication interruption')
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=new_assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download), \
             patch.object(b.os, 'replace', side_effect=fail_after_first_publication):
            with self.assertRaises((OSError, b.BridgePrerequisiteError)):
                b.fetch_automatic_personalized_ddi_bundle(force_refresh=True)
        self.assertEqual([path.read_bytes() for path in old_bundle], old_data)
        self.assertEqual(b._valid_personalized_ddi_bundle(self.root), old_bundle)

    def test_api_failure_can_use_pinned_files_over_mirror(self):
        mirror = b.PersonalizedDdiDownloadSource('mirror', 'mirror', 'https://test.invalid/')
        with patch.object(b, 'PERSONALIZED_DDI_GITHUB_REF', 'main'), \
             patch.object(b, '_resolve_github_personalized_ddi_assets', side_effect=b.BridgePrerequisiteError('developer_image_download_failed', 'API offline')), \
             patch.object(b, '_personalized_ddi_download_sources', return_value=(mirror,)), \
             patch.object(b, '_measure_ddi_sources', return_value=[(mirror, 1)]), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download) as download:
            b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(download.call_count, 3)

    def test_snapshot_rejection_survives_cache_pointer_resolution(self):
        with patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download):
            bundle = b.fetch_automatic_personalized_ddi_bundle()
        b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=self.assets), \
             patch.object(b, '_download_personalized_ddi_asset') as download:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-a')
        self.assertEqual(raised.exception.code, 'developer_image_tss_rejected')
        download.assert_not_called()

    def test_cancellation_after_snapshot_rename_does_not_select_it(self):
        old_bundle = self.install_cache()
        self.data['Image.dmg'] = b'new image'
        new_assets = assets_for(self.data)
        import threading
        cancelled = threading.Event()
        replace = os.replace
        def cancel_after_rename(source, target):
            directory = Path(source).is_dir()
            replace(source, target)
            if directory: cancelled.set()
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=new_assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download), \
             patch.object(b.os, 'replace', side_effect=cancel_after_rename):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle(cancelled=cancelled, force_refresh=True)
        self.assertEqual(raised.exception.code, 'developer_image_download_cancelled')
        self.assertEqual(b._valid_personalized_ddi_bundle(self.root), old_bundle)

    def test_concurrent_publications_return_their_own_complete_snapshot(self):
        import concurrent.futures
        import threading
        barrier = threading.Barrier(2)
        def publish(index):
            data = dict(self.data, **{'Image.dmg': f'image-{index}'.encode()})
            staging = Path(self.directory) / f'staging-{index}'
            staging.mkdir()
            for name, payload in data.items(): (staging / name).write_bytes(payload)
            b._write_ddi_metadata(staging, assets_for(data), b.LATEST_DDI_BUILD_ID)
            barrier.wait(timeout=5)
            return b._publish_ddi_bundle(staging, self.root, lambda: None)
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            results = list(pool.map(publish, range(2)))
        for index, bundle in enumerate(results):
            self.assertEqual(bundle[0].read_bytes(), f'image-{index}'.encode())
            self.assertEqual(b._valid_personalized_ddi_bundle(bundle[0].parent), bundle)
        self.assertIn(b._valid_personalized_ddi_bundle(self.root), results)

    def test_windows_cache_pointer_contention_retries_without_losing_snapshot(self):
        replace = os.replace
        attempts = []
        def busy_once(source, target):
            if Path(target).name == '.iphoneMirror-ddi-current.json':
                attempts.append(target)
                if len(attempts) == 1:
                    error = PermissionError('sharing violation')
                    error.winerror = 32
                    raise error
            replace(source, target)
        with patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download), \
             patch.object(b.os, 'replace', side_effect=busy_once):
            result = b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(len(attempts), 2)
        self.assertEqual(result, b._valid_personalized_ddi_bundle(self.root))

    def test_cancelled_cache_pointer_retry_preserves_previous_selection(self):
        old_bundle = self.install_cache()
        import threading
        cancelled = threading.Event()
        replace = os.replace
        attempts = []
        def busy(source, target):
            if Path(target).name == '.iphoneMirror-ddi-current.json':
                attempts.append(target)
                cancelled.set()
                error = PermissionError('sharing violation')
                error.winerror = 32
                raise error
            replace(source, target)
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=self.assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download), \
             patch.object(b.os, 'replace', side_effect=busy):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle(cancelled=cancelled, force_refresh=True)
        self.assertEqual(raised.exception.code, 'developer_image_download_cancelled')
        self.assertEqual(len(attempts), 1)
        self.assertEqual(b._valid_personalized_ddi_bundle(self.root), old_bundle)

    def test_persistent_windows_cache_denial_is_bounded(self):
        error = PermissionError('access denied')
        error.winerror = 5
        replace = os.replace
        attempts = []
        def denied(source, target):
            if Path(target).name == '.iphoneMirror-ddi-current.json':
                attempts.append(target)
                raise error
            replace(source, target)
        with patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download), \
             patch.object(b.os, 'replace', side_effect=denied), patch.object(b.time, 'sleep') as sleep:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(raised.exception.code, 'developer_image_cache_failed')
        self.assertEqual(len(attempts), 6)
        self.assertLessEqual(sum(call.args[0] for call in sleep.call_args_list), .75)

    def test_incompatible_remote_manifest_falls_back_before_downloading_large_image(self):
        bad_data = dict(self.data, **{'BuildManifest.plist': plistlib.dumps({'ProductBuildVersion': 'wrong-build'})})
        bad_assets = assets_for(bad_data, 'wrong-revision')
        attempts = []
        def download(source, asset, path, **kwargs):
            attempts.append((asset.revision, asset.local_name))
            path.write_bytes((bad_data if asset.revision == 'wrong-revision' else self.data)[asset.local_name])
        with patch.object(b, 'PERSONALIZED_DDI_GITHUB_REF', 'main'), \
             patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=bad_assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=download):
            b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual([item for item in attempts if item[0] == 'wrong-revision'],
                         [('wrong-revision', 'BuildManifest.plist')])
        self.assertIsNotNone(b._valid_personalized_ddi_bundle(self.root))

    def test_mirror_ranking_is_reused_for_all_files(self):
        slow = b.PersonalizedDdiDownloadSource('slow', 'mirror', 'https://slow.invalid/')
        fast = b.PersonalizedDdiDownloadSource('fast', 'mirror', 'https://fast.invalid/')
        with patch.object(b, '_personalized_ddi_download_sources', return_value=(slow, fast)), \
             patch.object(b, '_measure_ddi_sources', return_value=[(fast, 100), (slow, 1)]) as measure, \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download) as download:
            b.fetch_automatic_personalized_ddi_bundle()
        measure.assert_called_once()
        self.assertEqual([call.args[0].name for call in download.call_args_list], ['fast'] * 3)

    def test_failed_direct_and_proxy_phases_leave_time_for_mirrors(self):
        tick = [0.]
        proxy = b.PersonalizedDdiDownloadSource('proxy', 'raw', proxy='http://test.invalid')
        mirror = b.PersonalizedDdiDownloadSource('mirror', 'mirror', 'https://test.invalid/')
        attempted = []
        def download(source, asset, path, **kwargs):
            attempted.append(source.name)
            if source.kind != 'mirror':
                tick[0] += 21
                raise b.BridgePrerequisiteError('developer_image_download_timeout', 'timeout')
            path.write_bytes(self.data[asset.local_name])
        with patch.object(b.time, 'monotonic', side_effect=lambda: tick[0]), \
             patch.object(b, '_personalized_ddi_download_sources', return_value=(self.source, proxy, mirror)), \
             patch.object(b, '_measure_ddi_sources', return_value=[(mirror, 1)]), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=download):
            b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(attempted, ['raw', 'proxy', 'mirror', 'mirror', 'mirror'])

    def test_direct_proxy_settings_override_environment_proxy(self):
        with patch.dict(os.environ, {'HTTPS_PROXY': 'http://127.0.0.1:7890',
                                     'ALL_PROXY': 'http://127.0.0.1:7891'}, clear=True):
            with b.requests.Session() as session:
                settings = session.merge_environment_settings('https://raw.githubusercontent.com/test',
                    b._request_proxy_kwargs(None)['proxies'], True, None, None)
        self.assertFalse(any(settings['proxies'].values()))

    def test_source_attempt_leaves_budget_for_following_mirror(self):
        tick = [0.]
        slow = b.PersonalizedDdiDownloadSource('slow', 'mirror', 'https://slow.invalid/')
        fast = b.PersonalizedDdiDownloadSource('fast', 'mirror', 'https://fast.invalid/')
        attempted = []
        def download(source, asset, path, **kwargs):
            attempted.append(source.name)
            if source is slow:
                self.assertLessEqual(kwargs['deadline'] - tick[0], b.PERSONALIZED_DDI_SOURCE_ATTEMPT_SECONDS)
                tick[0] = kwargs['deadline']
                raise b.BridgePrerequisiteError('developer_image_download_timeout', 'trickling source')
            path.write_bytes(self.data[asset.local_name])
        with patch.object(b.time, 'monotonic', side_effect=lambda: tick[0]), \
             patch.object(b, '_personalized_ddi_download_sources', return_value=(slow, fast)), \
             patch.object(b, '_measure_ddi_sources', return_value=[(slow, 2), (fast, 1)]), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=download):
            bundle = b.fetch_automatic_personalized_ddi_bundle()
        self.assertIsNotNone(bundle)
        self.assertEqual(attempted, ['slow', 'fast', 'fast', 'fast'])

    def test_failed_range_probe_does_not_discard_working_full_download(self):
        mirror = b.PersonalizedDdiDownloadSource('range-blocked', 'mirror', 'https://mirror.invalid/')
        with patch.object(b, '_personalized_ddi_download_sources', return_value=(mirror,)), \
             patch.object(b, '_measure_ddi_sources', return_value=[]), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download) as download:
            bundle = b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(download.call_count, 3)
        self.assertEqual(bundle[0].read_bytes(), self.data['Image.dmg'])

    def test_unmeasured_mirror_remains_after_ranked_mirror_fails(self):
        ranked = b.PersonalizedDdiDownloadSource('ranked', 'mirror', 'https://ranked.invalid/')
        unmeasured = b.PersonalizedDdiDownloadSource('unmeasured', 'mirror', 'https://other.invalid/')
        attempted = []
        def download(source, asset, path, **kwargs):
            attempted.append(source.name)
            if source is ranked:
                raise b.BridgePrerequisiteError('developer_image_download_failed', 'full GET rejected')
            self.download(source, asset, path, **kwargs)
        with patch.object(b, '_personalized_ddi_download_sources', return_value=(ranked, unmeasured)), \
             patch.object(b, '_measure_ddi_sources', return_value=[(ranked, 100)]), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=download):
            b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(attempted, ['ranked', 'unmeasured', 'unmeasured', 'unmeasured'])


    def test_rejected_cache_refreshes_metadata_without_retrying_identical_content(self):
        bundle = self.install_cache()
        b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=self.assets) as resolve, \
             patch.object(b, '_download_personalized_ddi_asset') as download:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-a')
        self.assertEqual(raised.exception.code, 'developer_image_tss_rejected')
        self.assertEqual(resolve.call_args.args, ('main',))
        resolve.assert_called_once()
        download.assert_not_called()

    def test_rejected_cache_accepts_different_compatible_content(self):
        bundle = self.install_cache()
        b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        self.data['Image.dmg'] = b'new compatible image'
        new_assets = assets_for(self.data, 'new-revision')
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=new_assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download):
            b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-a')
        self.assertEqual(b._read_ddi_rejection(self.root, 'device-a'), ((), None))
        self.assertEqual(b._valid_personalized_ddi_bundle(self.root)[0].read_bytes(), b'new compatible image')

    def test_incompatible_latest_cannot_restore_the_rejected_pinned_cache(self):
        bundle = self.install_cache()
        b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        bad_data = dict(self.data, **{'BuildManifest.plist': plistlib.dumps({'ProductBuildVersion': 'wrong-build'})})
        bad_assets = assets_for(bad_data)
        def download(source, asset, path, **kwargs): path.write_bytes(bad_data[asset.local_name])
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=bad_assets), \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=download) as transfer:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-a')
        self.assertEqual(raised.exception.code, 'developer_image_tss_rejected')
        self.assertEqual(transfer.call_count, 1)

    def test_rejection_is_scoped_and_expires_without_blocking_other_devices(self):
        bundle = self.install_cache()
        with patch.object(b.time, 'time', return_value=1000):
            b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        with patch.object(b, '_resolve_github_personalized_ddi_assets', side_effect=AssertionError('unnecessary API')):
            self.assertEqual(b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-b'), bundle)
            with patch.object(b.time, 'time', return_value=1000 + b.DDI_REJECTION_TTL_SECONDS):
                self.assertEqual(b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-a'), bundle)

    def test_two_devices_retain_independent_rejections_of_shared_bundle(self):
        bundle = self.install_cache()
        codes = {'device-a': 'developer_image_tss_rejected',
                 'device-b': 'developer_image_download_incompatible'}
        for scope, code in codes.items():
            b._record_ddi_rejection(bundle, code, scope)
        for scope, code in codes.items():
            with self.subTest(scope=scope):
                self.assertEqual((b._ddi_bundle_blobs(bundle), code),
                                 b._read_ddi_rejection(self.root, scope))
                with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=self.assets), \
                     patch.object(b, '_download_personalized_ddi_asset') as download:
                    with self.assertRaises(b.BridgePrerequisiteError) as raised:
                        b.fetch_automatic_personalized_ddi_bundle(rejection_scope=scope)
                    self.assertEqual(code, raised.exception.code)
                    download.assert_not_called()
        self.assertEqual(b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-c'), bundle)

    def test_metadata_reordering_does_not_bypass_rejected_content_check(self):
        bundle = self.install_cache()
        metadata_path = b._ddi_metadata_path(self.root)
        metadata = json.loads(metadata_path.read_text())
        metadata['assets'].reverse()
        metadata_path.write_text(json.dumps(metadata))
        b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        self.assertEqual(b._read_ddi_rejection(self.root, 'device-a')[0], b._ddi_bundle_blobs(bundle))
        with patch.object(b, '_resolve_github_personalized_ddi_assets', return_value=self.assets), \
             patch.object(b, '_download_personalized_ddi_asset') as download:
            with self.assertRaises(b.BridgePrerequisiteError):
                b.fetch_automatic_personalized_ddi_bundle(rejection_scope='device-a')
        download.assert_not_called()

    def test_legacy_rejection_remains_scoped_and_new_record_takes_precedence(self):
        bundle = self.install_cache()
        with patch.object(b.time, 'time', return_value=1000):
            b._record_ddi_rejection(bundle, 'developer_image_tss_rejected', 'device-a')
        b._ddi_rejection_path(self.root, 'device-a').rename(self.root / '.iphoneMirror-ddi-rejected.json')
        with patch.object(b.time, 'time', return_value=1001):
            self.assertEqual(b._read_ddi_rejection(self.root, 'device-a')[1], 'developer_image_tss_rejected')
            self.assertEqual(b._read_ddi_rejection(self.root, 'device-b'), ((), None))
            b._record_ddi_rejection(bundle, 'developer_image_bundle_invalid', 'device-a')
            self.assertEqual(b._read_ddi_rejection(self.root, 'device-a')[1], 'developer_image_bundle_invalid')
        with patch.object(b.time, 'time', return_value=1001 + b.DDI_REJECTION_TTL_SECONDS):
            self.assertEqual(b._read_ddi_rejection(self.root, 'device-a'), ((), None))

    def test_metadata_budget_leaves_time_for_pinned_download(self):
        tick = [0.]
        def request(*_, **kwargs):
            tick[0] += 26
            raise b.BridgePrerequisiteError('developer_image_download_timeout', 'offline')
        with patch.object(b.time, 'monotonic', side_effect=lambda: tick[0]), \
             patch.object(b, 'PERSONALIZED_DDI_GITHUB_REF', 'main'), \
             patch.object(b, '_local_proxy_candidates', return_value=('http://proxy.invalid',)), \
             patch.object(b, '_github_request_json', side_effect=request) as metadata, \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=self.download) as download:
            b.fetch_automatic_personalized_ddi_bundle()
        metadata.assert_called_once()
        self.assertEqual(download.call_count, 3)
        self.assertTrue(all(call.kwargs['deadline'] <= 150 for call in download.call_args_list))
        self.assertTrue(all(call.kwargs['deadline'] <= tick[0] + b.PERSONALIZED_DDI_SOURCE_ATTEMPT_SECONDS
                            for call in download.call_args_list))

    def test_expired_download_budget_does_not_probe_more_sources_or_publish_cache(self):
        tick = [0.]
        mirror = b.PersonalizedDdiDownloadSource('mirror', 'mirror', 'https://test.invalid/')
        def download(*_, **kwargs):
            tick[0] = b.PERSONALIZED_DDI_DOWNLOAD_TIMEOUT_SECONDS
            raise b.BridgePrerequisiteError('developer_image_download_timeout', 'expired')
        with patch.object(b.time, 'monotonic', side_effect=lambda: tick[0]), \
             patch.object(b, '_personalized_ddi_download_sources', return_value=(self.source, mirror)), \
             patch.object(b, '_measure_ddi_sources') as measure, \
             patch.object(b, '_download_personalized_ddi_asset', side_effect=download):
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                b.fetch_automatic_personalized_ddi_bundle()
        self.assertEqual(raised.exception.code, 'developer_image_download_timeout')
        measure.assert_not_called()
        self.assertFalse(self.root.exists())

    def test_rejection_scope_changes_with_device_or_os_and_normalizes_udid(self):
        def scope(udid, build):
            return b._ddi_rejection_scope(SimpleNamespace(udid=udid, all_values={'BuildVersion': build}))
        self.assertEqual(scope('AA-BB', '1'), scope('aabb', '1'))
        self.assertNotEqual(scope('AA-BB', '1'), scope('CC-DD', '1'))
        self.assertNotEqual(scope('AA-BB', '1'), scope('AA-BB', '2'))


class CandidateTests(unittest.IsolatedAsyncioTestCase):
    def session(self): return b.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)

    async def test_mount_rejection_refreshes_once_and_preserves_original_error_if_unchanged(self):
        s = self.session()
        candidate = ((1, 2, 3), 'github')
        rejected = b.BridgePrerequisiteError('developer_image_tss_rejected', 'Apple rejected this image')
        with patch.object(s, '_local_ddi_candidates', return_value=[]), \
             patch.object(s, '_mount_ddi_candidate', AsyncMock(side_effect=rejected)) as mount, \
             patch.object(b, '_record_ddi_rejection') as record, \
             patch.object(b, '_ddi_bundle_blobs', return_value=('a', 'b', 'c')), \
             patch.object(s, '_resolve_personalized_ddi_bundle', AsyncMock(side_effect=
                 b.BridgePrerequisiteError('developer_image_no_new_candidate', 'unchanged'))) as resolve:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._mount_personalized_ddi(object(), prepared=candidate)
        self.assertIs(raised.exception, rejected)
        mount.assert_awaited_once()
        record.assert_called_once_with(candidate[0], rejected.code, None)
        resolve.assert_awaited_once_with(allow_local=False, force_refresh=True, rejected_blobs=('a', 'b', 'c'))

    async def test_mount_replacement_failure_cannot_start_another_refresh(self):
        s = self.session()
        candidate, replacement = ((1, 2, 3), 'github'), ((4, 5, 6), 'github')
        rejected = b.BridgePrerequisiteError('developer_image_tss_rejected', 'rejected')
        with patch.object(s, '_local_ddi_candidates', return_value=[]), \
             patch.object(s, '_mount_ddi_candidate', AsyncMock(side_effect=rejected)) as mount, \
             patch.object(b, '_record_ddi_rejection') as record, \
             patch.object(b, '_ddi_bundle_blobs', return_value=('a', 'b', 'c')), \
             patch.object(s, '_resolve_personalized_ddi_bundle', AsyncMock(return_value=replacement)) as resolve:
            with self.assertRaises(b.BridgePrerequisiteError):
                await s._mount_personalized_ddi(object(), prepared=candidate)
        self.assertEqual([call.args[1] for call in mount.call_args_list], [candidate, replacement])
        resolve.assert_awaited_once()
        self.assertEqual(record.call_count, 2)

    async def test_mount_network_failure_does_not_reject_cache_or_refresh_image(self):
        s = self.session()
        with patch.object(s, '_local_ddi_candidates', return_value=[]), \
             patch.object(s, '_mount_ddi_candidate', AsyncMock(side_effect=
                 b.BridgePrerequisiteError('developer_image_tss_timeout', 'network timeout'))), \
             patch.object(b, '_record_ddi_rejection') as record, \
             patch.object(s, '_resolve_personalized_ddi_bundle', AsyncMock()) as resolve:
            with self.assertRaises(b.BridgePrerequisiteError):
                await s._mount_personalized_ddi(object(), prepared=((1, 2, 3), 'github'))
        resolve.assert_not_awaited()
        record.assert_not_called()

    async def test_refresh_keeps_environment_candidate_after_bundled_failure(self):
        s = self.session()
        bundled, environment = ((1, 2, 3), 'bundled'), ((4, 5, 6), 'environment')
        with patch.object(s, '_local_ddi_candidates', return_value=[bundled, environment]), \
             patch.object(s, '_resolve_personalized_ddi_bundle', AsyncMock()) as download, \
             patch.object(s, '_mount_ddi_candidate', AsyncMock(side_effect=[
                 b.BridgePrerequisiteError('developer_image_download_incompatible', 'old build'), None])) as mount:
            await s._mount_personalized_ddi(object(), prepared=bundled)
        self.assertEqual([call.args[1][1] for call in mount.call_args_list], ['bundled', 'environment'])
        download.assert_not_awaited()

    async def test_modern_without_touchscreen_falls_back_without_leaking_keyboard_surface(self):
        s = self.session()
        modern_name = b.UniversalHIDServiceService.SERVICE_NAME
        s.rsd = SimpleNamespace(peer_info={'Services': {modern_name: {}, b.LEGACY_UNIVERSAL_HID_SERVICE: {}}})
        attempts = []
        class Modern:
            SERVICE_NAME = modern_name
            def __init__(self, _): attempts.append(self.SERVICE_NAME)
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            async def list_connected_services(self): return {'services': [{'_ServiceID': 512}]}
        class Legacy(Modern):
            SERVICE_NAME = b.LEGACY_UNIVERSAL_HID_SERVICE
            async def list_connected_services(self): return {'services': [{'_ServiceID': 257}]}
        with patch.object(b, 'UniversalHIDServiceService', Modern), \
             patch.object(b, 'LegacyUniversalHIDServiceService', Legacy), \
             patch.object(s, '_ping_hid', AsyncMock()):
            await s._init_touch()
        self.assertEqual(attempts, [modern_name, b.LEGACY_UNIVERSAL_HID_SERVICE])
        self.assertIsInstance(s.hid, Legacy)
        self.assertIsNone(s.keyboard_service_id)

    async def test_missing_snapshot_does_not_repeat_identical_source_queries(self):
        s = self.session()
        s.rsd = SimpleNamespace(peer_info={'Services': {}})
        with patch.object(s, '_init_touch', AsyncMock(side_effect=RuntimeError('No such service: HID'))) as init:
            with self.assertRaises(b.BridgePrerequisiteError) as raised:
                await s._initialize_touch_with_retry(False)
        self.assertEqual(raised.exception.code, 'touch_surface_unavailable')
        init.assert_awaited_once()

    async def test_hid_channel_reset_or_failed_ping_tries_legacy(self):
        from pymobiledevice3.exceptions import StreamClosedError
        for stage in ('open', 'inventory', 'ping'):
            with self.subTest(stage=stage):
                s = self.session()
                s.rsd = SimpleNamespace(peer_info={'Services': {}})
                closed = []
                class Modern:
                    SERVICE_NAME = 'modern'
                    def __init__(self, _): pass
                    async def __aenter__(self):
                        if stage == 'open': raise StreamClosedError('RST_STREAM')
                        return self
                    async def __aexit__(self, *_): closed.append(self.SERVICE_NAME)
                    async def list_connected_services(self):
                        if stage == 'inventory': raise StreamClosedError('RST_STREAM')
                        return {'services': [{'_ServiceID': 257}]}
                class Legacy(Modern):
                    SERVICE_NAME = b.LEGACY_UNIVERSAL_HID_SERVICE
                    async def __aenter__(self): return self
                    async def list_connected_services(self): return {'services': [{'_ServiceID': 257}]}
                async def ping(hid):
                    if hid.SERVICE_NAME == 'modern': raise ConnectionError('HID reset after inventory')
                with patch.object(b, 'UniversalHIDServiceService', Modern), \
                     patch.object(b, 'LegacyUniversalHIDServiceService', Legacy), \
                     patch.object(s, '_ping_hid', side_effect=ping) as probe:
                    await s._init_touch()
                self.assertIsInstance(s.hid, Legacy)
                self.assertEqual(closed, ['modern'])
                self.assertIs(probe.call_args.args[0], s.hid)

    async def test_dead_tunnel_does_not_retry_hid_candidates(self):
        s = self.session()
        s.rsd = SimpleNamespace(peer_info={'Services': {}})
        failure = ConnectionError('tunnel ended')
        candidate = SimpleNamespace(__aenter__=AsyncMock(side_effect=failure), __aexit__=AsyncMock())
        with patch.object(b, 'UniversalHIDServiceService', return_value=candidate), \
             patch.object(b, 'LegacyUniversalHIDServiceService') as legacy, \
             patch.object(s, '_transport_failure_reason', return_value='reader ended'):
            with self.assertRaises(ConnectionError): await s._init_touch()
        legacy.assert_not_called()

    async def test_direct_authentication_survives_optional_session_fallback(self, recovering=False, failure=None):
        s = self.session()
        s._recovering = recovering
        class Dial:
            dial = AsyncMock()
            def __init__(self, *_): pass
            async def __aenter__(self): return self
        class Rsd:
            udid = 'test-device'
            def __init__(self, *_, **kwargs): pass
            async def __aenter__(self): return self
        class Session:
            async def __aenter__(self):
                raise failure or RuntimeError('No such service: com.apple.coredevice.hid.universalhidservice')
            async def __aexit__(self, *_): pass
        async def initialize():
            s.hid = object()
            s.gate_open = True
            s.auth_mode = 'direct'
        modes = []
        async def ready(): modes.append(s.auth_mode)
        result = SimpleNamespace(client=SimpleNamespace(tun=SimpleNamespace(set_peer=lambda _: None)), address='test', port=1)
        with patch('pymobiledevice3.remote.userspace_tunnel.UserspaceDialPlane', Dial), \
             patch.object(b, 'RemoteServiceDiscoveryService', Rsd), \
             patch.object(b, 'touch_session', return_value=Session()), \
             patch.object(s, '_initialize_touch_with_retry', initialize), \
             patch.object(s, '_emit_ready', ready), patch.object(s, '_run_serve', AsyncMock()), \
             patch.object(s, '_cleanup', AsyncMock()):
            await s._connect_with_tunnel_result(result)
        self.assertEqual(modes, ['direct'])

    async def test_recovery_can_use_the_same_optional_hid_fallback_as_initial_start(self):
        await self.test_direct_authentication_survives_optional_session_fallback(recovering=True)

    async def test_media_hid_stream_reset_still_reaches_explicit_candidates(self):
        await self.test_direct_authentication_survives_optional_session_fallback(
            failure=b.StreamClosedError('RST_STREAM'))

    def test_upstream_media_timeout_is_eligible_for_verified_direct_fallback(self):
        self.assertTrue(b.TouchSession._can_fallback_to_direct_hid(
            RuntimeError('Timed out starting the media stream that gates HID auth.')))
        self.assertFalse(b.TouchSession._can_fallback_to_direct_hid(ConnectionError('connection reset')))

    async def test_9021_after_hid_selection_preserves_direct_and_closes_media_resources(self):
        s = self.session()
        s.hid = object()
        s.rsd = SimpleNamespace(service=SimpleNamespace(address=('test-device', 1)))
        closed = []
        class Display:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): closed.append('display')
            async def start_video_stream(self, **kwargs): raise RuntimeError('startmediastream returned 9021')
        transport = SimpleNamespace(port=1, close=lambda: closed.append('receiver'))
        with patch.object(b, 'DisplayService', return_value=Display()), \
             patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver', return_value=(transport, 'local')):
            await s._open_gate()
        self.assertEqual(s.auth_mode, 'direct')
        self.assertTrue(s.gate_open)
        self.assertEqual(closed, ['receiver', 'display'])

    async def test_cancelled_hid_open_closes_candidate_without_trying_next_source(self):
        s = self.session()
        s.rsd = SimpleNamespace(peer_info={'Services': {}})
        started = asyncio.Event()
        closed = asyncio.Event()
        class Modern:
            SERVICE_NAME = 'modern'
            def __init__(self, _): pass
            async def __aenter__(self):
                started.set()
                await asyncio.Event().wait()
            async def __aexit__(self, *_): closed.set()
        with patch.object(b, 'UniversalHIDServiceService', Modern), \
             patch.object(b, 'LegacyUniversalHIDServiceService') as legacy:
            task = asyncio.create_task(s._init_touch())
            await started.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError): await task
        self.assertTrue(closed.is_set())
        legacy.assert_not_called()


class MediaSessionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.rsd = SimpleNamespace(service=SimpleNamespace(address=('phone', 1)))
        self.display = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(),
            start_video_stream=AsyncMock(), stop_media_stream=AsyncMock())
        self.hid = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock())
        self.closed = []
        async def receive(): await asyncio.Event().wait()
        self.receiver = SimpleNamespace(port=1, close=lambda: self.closed.append(True), recv=receive)
        self.enterContext(patch.object(b, 'DisplayService', return_value=self.display))
        self.enterContext(patch.object(b, 'UniversalHIDServiceService', return_value=self.hid))
        self.enterContext(patch('pymobiledevice3.remote.core_device.screen_stream.open_media_receiver',
                                return_value=(self.receiver, 'local')))

    async def test_failed_media_auth_closes_receiver_and_display(self):
        self.display.start_video_stream.side_effect = RuntimeError('startmediastream returned 9021')
        with self.assertRaisesRegex(RuntimeError, '9021'):
            async with b.touch_session(self.rsd): self.fail('must not yield HID')
        self.assertEqual(self.closed, [True])
        self.display.__aexit__.assert_awaited_once()
        self.hid.__aenter__.assert_not_awaited()

    async def test_cancelled_partial_display_open_is_closed(self):
        started = asyncio.Event()
        async def open_display():
            started.set()
            await asyncio.Event().wait()
        self.display.__aenter__.side_effect = open_display
        async def run():
            async with b.touch_session(self.rsd): pass
        task = asyncio.create_task(run())
        await started.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        self.display.__aexit__.assert_awaited_once()

    async def test_stalled_stop_and_close_cannot_hide_original_error(self):
        self.display.start_video_stream.return_value = {'connection': {'options': {
            'avcMediaStreamOptionClientSessionID': {'uuid': '00000000-0000-0000-0000-000000000001'}}}}
        async def stalled(*_): await asyncio.Event().wait()
        self.display.stop_media_stream.side_effect = stalled
        self.display.__aexit__.side_effect = stalled
        self.hid.__aexit__.side_effect = stalled
        timeout = asyncio.timeout
        with patch.object(b.asyncio, 'timeout', side_effect=lambda _: timeout(.01)):
            with self.assertRaisesRegex(RuntimeError, 'original failure'):
                async with b.touch_session(self.rsd): raise RuntimeError('original failure')
        self.assertEqual(self.closed, [True])
        self.display.stop_media_stream.assert_awaited_once()
        self.display.__aexit__.assert_awaited_once()
        self.hid.__aexit__.assert_awaited_once()

    async def test_media_timeout_releases_receiver_before_direct_fallback(self):
        async def stalled(**_): await asyncio.Event().wait()
        self.display.start_video_stream.side_effect = stalled
        wait_for = asyncio.wait_for
        async def bounded_wait(operation, _):
            return await wait_for(operation, .01)
        with patch.object(b.asyncio, 'wait_for', side_effect=bounded_wait):
            with self.assertRaisesRegex(RuntimeError, 'Timed out starting the media stream') as raised:
                async with b.touch_session(self.rsd): pass
        self.assertTrue(b.TouchSession._can_fallback_to_direct_hid(raised.exception))
        self.assertEqual(self.closed, [True])
        self.display.__aexit__.assert_awaited_once()
