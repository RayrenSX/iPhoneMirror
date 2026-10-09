"""DDI cancellation, protocol compatibility and fallback regressions (no phone required)."""
import asyncio
import io
import json
import plistlib
import socket
import struct
import sys
import threading
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import httpx
import ddi_support as ddi
import usb_touch_bridge as bridge
from pymobiledevice3.services import mobile_image_mounter as upstream


class TicketTests(unittest.IsolatedAsyncioTestCase):
    def client(self, handler):
        real_client = httpx.AsyncClient
        return patch.object(ddi.httpx, 'AsyncClient', side_effect=lambda **kwargs:
                            real_client(transport=httpx.MockTransport(handler), **kwargs))

    async def test_valid_ticket_and_canonical_apple_endpoint(self):
        def handler(request):
            self.assertEqual(str(request.url), ddi.TSS_URL)
            self.assertEqual(str(request.url), ddi.TSS_CONTROLLER_ACTION_URL)
            self.assertEqual(request.content, b'request')
            return httpx.Response(200, content=b'STATUS=0&MESSAGE=SUCCESS&REQUEST_STRING=' +
                                  plistlib.dumps({'ApImg4Ticket': b'signed-ticket'}))
        with self.client(handler):
            self.assertEqual(await ddi.request_ticket(b'request'), b'signed-ticket')

    async def test_stalled_apple_request_times_out_without_blocking_event_loop(self):
        cancelled = asyncio.Event()
        ticks = []
        async def handler(_):
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        async def heartbeat():
            for _ in range(3):
                await asyncio.sleep(.005)
                ticks.append(True)
        with self.client(handler), patch.object(ddi, 'TSS_TIMEOUT_SECONDS', .04):
            pulse = asyncio.create_task(heartbeat())
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await asyncio.wait_for(ddi.request_ticket(b'request'), 1)
            await pulse
        self.assertEqual(raised.exception.code, 'developer_image_tss_timeout')
        self.assertTrue(cancelled.is_set())
        self.assertEqual(len(ticks), 3)

    async def test_user_cancellation_is_not_reclassified_as_mount_failure(self):
        started = asyncio.Event()
        async def handler(_):
            started.set()
            await asyncio.Event().wait()
        with self.client(handler):
            task = asyncio.create_task(ddi.request_ticket(b'request'))
            await started.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await asyncio.wait_for(task, 1)

    async def test_bad_response_does_not_leak_payload(self):
        for status, content in [(403, b'private-device'), (200, b'proxy-login'),
                                (200, b'MESSAGE=SUCCESS&REQUEST_STRING=<?xml broken'),
                                (200, b'MESSAGE=SUCCESS&REQUEST_STRING=' + plistlib.dumps({}))]:
            with self.subTest(status=status, content=content), self.client(
                    lambda _: httpx.Response(status, content=content)):
                with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                    await ddi.request_ticket(b'private-device')
                self.assertEqual(raised.exception.code, 'developer_image_tss_failed')
                self.assertNotIn('private-device', str(raised.exception))

    async def test_apple_rejection_allows_a_newer_image_candidate(self):
        with self.client(lambda _: httpx.Response(200, content=b'STATUS=94&MESSAGE=not-eligible')):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await ddi.request_ticket(b'request')
        self.assertEqual(raised.exception.code, 'developer_image_tss_rejected')

    async def test_transient_apple_failure_recovers_within_same_budget(self):
        for first in [httpx.Response(503), httpx.Response(429),
                      httpx.Response(200, content=b'STATUS=93&MESSAGE=internal-error')]:
            calls = []
            def handler(request):
                calls.append(request.content)
                if len(calls) == 1:
                    return first
                return httpx.Response(200, content=b'STATUS=0&MESSAGE=SUCCESS&REQUEST_STRING=' +
                                      plistlib.dumps({'ApImg4Ticket': b'ticket'}))
            with self.subTest(status=first.status_code), self.client(handler), \
                 patch.object(ddi, 'TSS_RETRY_DELAY_SECONDS', 0):
                self.assertEqual(await ddi.request_ticket(b'original-payload'), b'ticket')
            self.assertEqual(calls, [b'original-payload', b'original-payload'])

    async def test_exhausted_service_error_remains_service_error(self):
        calls = []
        def handler(_):
            calls.append(True)
            return httpx.Response(200, content=b'STATUS=93&MESSAGE=internal-error')
        with self.client(handler), patch.object(ddi, 'TSS_RETRY_DELAY_SECONDS', 0):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await ddi.request_ticket(b'request')
        self.assertEqual(raised.exception.code, 'developer_image_tss_unavailable')
        self.assertEqual(len(calls), 2)

    async def test_empty_http_502_is_network_failure_not_parser_or_image_failure(self):
        calls = []
        def handler(_):
            calls.append(True)
            return httpx.Response(502, content=b'')
        with self.client(handler), patch.object(ddi, 'TSS_RETRY_DELAY_SECONDS', 0):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await ddi.request_ticket(b'private-device')
        self.assertEqual(raised.exception.code, 'developer_image_tss_unavailable')
        self.assertEqual(bridge.bridge_error_code(raised.exception), 'developer_image_tss_unavailable')
        self.assertIn('HTTP 502', str(raised.exception))
        self.assertNotIn('private-device', str(raised.exception))
        self.assertEqual(len(calls), 2)

    async def test_rejection_is_not_retried_on_the_same_image(self):
        calls = []
        def handler(_):
            calls.append(True)
            return httpx.Response(200, content=b'STATUS=94&MESSAGE=ineligible')
        with self.client(handler):
            with self.assertRaises(ddi.BridgePrerequisiteError):
                await ddi.request_ticket(b'request')
        self.assertEqual(len(calls), 1)

    async def test_retry_backoff_is_cancellable(self):
        requested = asyncio.Event()
        def handler(_):
            requested.set()
            return httpx.Response(503)
        with self.client(handler), patch.object(ddi, 'TSS_RETRY_DELAY_SECONDS', 60):
            task = asyncio.create_task(ddi.request_ticket(b'request'))
            await requested.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await asyncio.wait_for(task, 1)

    async def test_request_matches_pinned_upstream_including_restore_rules(self):
        manifest = {'BuildIdentities': [{'ApBoardID': '0x1', 'ApChipID': '0x2', 'Manifest': {
            'LoadableTrustCache': {'Trusted': True, 'Info': {'RestoreRequestRules': [
                {'Conditions': {'ApRawProductionMode': True}, 'Actions': {'EPRO': True}}]}},
            'PersonalizedDMG': {'Trusted': True, 'Digest': b'digest', 'Info': {}},
            'Untrusted': {'Trusted': False, 'Info': {}},
        }}]}
        identifiers = {'BoardId': 1, 'ChipID': 2, 'Ap,Test': True}
        captured = []
        async def capture(request):
            captured.append(dict(request._request))
            return {'ApImg4Ticket': b'ticket'}
        with patch.object(upstream.TSSRequest, 'send_receive', capture):
            await upstream.request_personalization_manifest(manifest, identifiers, 123, b'nonce')
        actual = plistlib.loads(ddi.personalization_request(manifest, identifiers, 123, b'nonce'))
        actual.pop('@UUID')
        captured[0].pop('@UUID')
        self.assertEqual(actual, captured[0])


class LifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_mounter_uses_bounded_ticket_client_and_uploads_then_mounts(self):
        class Connection:
            async def send_recv_plist(self, message):
                command = message['Command']
                commands.append(command)
                return {
                    'LookupImage': {'ImagePresent': False},
                    'CopyDevices': {'EntryList': []},
                    'QueryPersonalizationManifest': {},
                    'QueryPersonalizationIdentifiers': {'PersonalizationIdentifiers': {'BoardId': 1, 'ChipID': 2}},
                    'QueryNonce': {'PersonalizationNonce': b'nonce'},
                    'ReceiveBytes': {'Status': 'ReceiveBytesAck'},
                    'MountImage': {'Status': 'Complete'},
                }[command]
            async def sendall(self, image):
                uploaded.append(image)
            async def recv_plist(self):
                return {'Status': 'Complete'}
            async def close(self):
                pass
        commands, uploaded, phases = [], [], []
        lockdown = SimpleNamespace(product_version='27.0', ecid=123,
                                   get_developer_mode_status=AsyncMock(return_value=True),
                                   start_lockdown_service=AsyncMock(side_effect=lambda *_a, **_k: Connection()))
        mounter = ddi.PersonalizedImageMounter(lockdown)
        async def status(code):
            phases.append(code)
        mounter.report_status = status
        with TemporaryDirectory() as directory:
            root = Path(directory)
            image, manifest, trust = (root / name for name in bridge.PERSONALIZED_DDI_FILES)
            image.write_bytes(b'image')
            trust.write_bytes(b'trust')
            manifest.write_bytes(plistlib.dumps({'BuildIdentities': [{
                'ApBoardID': '0x1', 'ApChipID': '0x2', 'Manifest': {}}]}))
            with patch.object(ddi, 'request_ticket', AsyncMock(return_value=b'ticket')) as ticket, \
                 patch.object(upstream.TSSRequest, 'send_receive', AsyncMock(side_effect=AssertionError('unsafe upstream HTTP'))):
                async with ddi.bounded_mounter(mounter):
                    await mounter.mount(image, manifest, trust)
            ticket.assert_awaited_once()
            self.assertEqual(plistlib.loads(ticket.call_args.args[0])['ApECID'], 123)
        self.assertEqual(uploaded, [b'image'])
        self.assertLess(commands.index('ReceiveBytes'), commands.index('MountImage'))
        self.assertEqual(phases, ['checking_developer_image_ticket', 'personalizing_developer_image',
                                  'uploading_developer_image', 'activating_developer_image'])

    async def test_stuck_service_open_is_bounded_and_closed(self):
        class Mounter:
            __aexit__ = AsyncMock()
            async def __aenter__(self):
                await asyncio.Event().wait()
        mounter = Mounter()
        with patch.object(ddi, 'SERVICE_TIMEOUT_SECONDS', .01):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                async with ddi.bounded_mounter(mounter):
                    self.fail('service cannot be entered')
        self.assertEqual(raised.exception.code, 'developer_image_service_timeout')
        mounter.__aexit__.assert_awaited_once()

    async def test_stuck_cleanup_does_not_hide_original_failure(self):
        class Mounter:
            async def __aenter__(self):
                return self
            async def __aexit__(self, *_):
                await asyncio.Event().wait()
        with patch.object(ddi, 'CLOSE_TIMEOUT_SECONDS', .01):
            with self.assertRaisesRegex(RuntimeError, 'original failure'):
                async with ddi.bounded_mounter(Mounter()):
                    raise RuntimeError('original failure')

    async def test_sdk_close_timeout_releases_socket_even_after_sdk_clears_handles(self):
        from pymobiledevice3.service_connection import ServiceConnection
        from unittest.mock import Mock
        raw, peer = socket.socketpair()
        self.addCleanup(raw.close)
        self.addCleanup(peer.close)
        service = ServiceConnection(raw)
        async def stalled_close():
            await asyncio.Event().wait()
        writer = SimpleNamespace(close=Mock(), wait_closed=stalled_close,
                                 transport=SimpleNamespace(abort=Mock()))
        service.writer = writer
        mounter = ddi.PersonalizedImageMounter(SimpleNamespace())
        mounter._service = service
        with patch.object(ddi, 'CLOSE_TIMEOUT_SECONDS', .02):
            with self.assertRaisesRegex(RuntimeError, 'original failure'):
                async with ddi.bounded_mounter(mounter):
                    raise RuntimeError('original failure')
        # The real SDK clears these fields on cancellation before it reaches
        # socket.close(). Keep an independent handle to detect the actual leak.
        self.assertIsNone(service.socket)
        self.assertIsNone(service.writer)
        self.assertEqual(-1, raw.fileno())
        writer.transport.abort.assert_called_once()

    async def test_cancel_during_sdk_cleanup_releases_owned_socket_and_propagates(self):
        from pymobiledevice3.service_connection import ServiceConnection
        from unittest.mock import Mock
        raw, peer = socket.socketpair()
        self.addCleanup(raw.close)
        self.addCleanup(peer.close)
        entered_close = asyncio.Event()
        async def stalled_close():
            entered_close.set()
            await asyncio.Event().wait()
        service = ServiceConnection(raw)
        writer = SimpleNamespace(close=Mock(), wait_closed=stalled_close,
                                 transport=SimpleNamespace(abort=Mock()))
        service.writer = writer
        mounter = ddi.PersonalizedImageMounter(SimpleNamespace())
        mounter._service = service
        async def run():
            async with ddi.bounded_mounter(mounter):
                pass
        task = asyncio.create_task(run())
        await asyncio.wait_for(entered_close.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await asyncio.wait_for(task, 1)
        self.assertEqual(-1, raw.fileno())
        writer.transport.abort.assert_called_once()

    async def test_upload_timeout_has_specific_code(self):
        mounter = ddi.PersonalizedImageMounter(SimpleNamespace())
        with patch.object(ddi.UpstreamMounter, 'upload_image', AsyncMock(side_effect=TimeoutError)), \
             self.assertRaises(ddi.BridgePrerequisiteError) as raised:
            await mounter.upload_image('Personalized', b'image', b'ticket')
        self.assertEqual(raised.exception.code, 'developer_image_upload_timeout')

    async def test_identity_query_timeout_is_not_a_network_signing_timeout(self):
        mounter = ddi.PersonalizedImageMounter(SimpleNamespace())
        with patch.object(mounter, 'query_personalization_identifiers', AsyncMock(side_effect=TimeoutError)), \
             patch.object(ddi, 'request_ticket', AsyncMock()) as ticket:
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await mounter.get_manifest_from_tss({})
        self.assertEqual(raised.exception.code, 'developer_image_service_timeout')
        ticket.assert_not_awaited()

    async def test_upload_connection_loss_and_locked_device_remain_actionable(self):
        for error, code in [(bridge.ConnectionTerminatedError(), 'apple_connection_lost'),
                            (bridge.PasswordRequiredError(), 'apple_device_locked')]:
            with self.subTest(code=code), patch.object(ddi.UpstreamMounter, 'upload_image', AsyncMock(side_effect=error)):
                with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                    await ddi.PersonalizedImageMounter(SimpleNamespace()).upload_image('Personalized', b'image', b'ticket')
            self.assertEqual(raised.exception.code, code)


class StartupTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        return bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)

    async def test_download_cancellation_notifies_worker(self):
        started, stopped = asyncio.Event(), threading.Event()
        loop = asyncio.get_running_loop()
        def download(_report, cancelled):
            loop.call_soon_threadsafe(started.set)
            if cancelled.wait(1):
                stopped.set()
            return (1, 2, 3)
        with patch.object(bridge, 'fetch_automatic_personalized_ddi_bundle', download):
            task = asyncio.create_task(self.session()._resolve_personalized_ddi_bundle(False))
            await started.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertTrue(await asyncio.to_thread(stopped.wait, 1))

    async def test_cancelled_download_cannot_publish_cached_files(self):
        cancelled = threading.Event()
        cancelled.set()
        with patch.object(bridge, '_personalized_ddi_cache_directory') as cache:
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                bridge.fetch_automatic_personalized_ddi_bundle(cancelled=cancelled)
        self.assertEqual(raised.exception.code, 'developer_image_download_cancelled')
        cache.assert_not_called()

    async def test_tss_failure_preserved_without_download_fallback(self):
        session = self.session()
        class Mounter:
            async def __aenter__(self):
                return self
            async def __aexit__(self, *_):
                pass
            async def mount(self, *_):
                raise ddi.BridgePrerequisiteError('developer_image_tss_timeout', 'timeout')
        with patch.object(bridge, 'PersonalizedImageMounter', lambda **_: Mounter()), \
             patch.object(session, '_local_ddi_candidates', return_value=[((1, 2, 3), 'bundled')]), \
             patch.object(session, '_resolve_personalized_ddi_bundle', AsyncMock()) as download:
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await session._mount_personalized_ddi(object())
        self.assertEqual(raised.exception.code, 'developer_image_tss_timeout')
        download.assert_not_awaited()

    async def test_downloaded_image_already_mounted_is_success(self):
        session = self.session()
        class Mounter:
            async def __aenter__(self):
                return self
            async def __aexit__(self, *_):
                pass
            async def mount(self, *_):
                raise bridge.AlreadyMountedError()
        with patch.object(bridge, 'PersonalizedImageMounter', lambda **_: Mounter()), \
             patch.object(session, '_resolve_personalized_ddi_bundle', AsyncMock(return_value=((1, 2, 3), 'github'))), \
             patch.object(session, '_is_personalized_ddi_mounted', AsyncMock(return_value=True)) as inventory:
            await session._mount_personalized_ddi(object())
        inventory.assert_awaited_once()

    async def test_false_already_mounted_is_not_success(self):
        session = self.session()
        class Mounter:
            async def __aenter__(self): return self
            async def __aexit__(self, *_): pass
            async def mount(self, *_): raise bridge.AlreadyMountedError()
        with patch.object(bridge, 'PersonalizedImageMounter', lambda **_: Mounter()), \
             patch.object(session, '_is_personalized_ddi_mounted', AsyncMock(return_value=False)) as inventory:
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await session._mount_ddi_candidate(object(), ((1, 2, 3), 'github'))
        self.assertEqual(raised.exception.code, 'developer_image_verification_failed')
        self.assertEqual(inventory.await_count, 3)

    async def test_incompatible_local_image_falls_back_but_device_failure_does_not(self):
        for code, expected_mounts in [('developer_image_download_incompatible', 2),
                                      ('apple_device_locked', 1), ('apple_connection_lost', 1),
                                      ('developer_image_tss_unavailable', 1)]:
            session = self.session()
            with self.subTest(code=code), \
                 patch.object(session, '_local_ddi_candidates', return_value=[((1, 2, 3), 'bundled')]), \
                 patch.object(session, '_resolve_personalized_ddi_bundle', AsyncMock(return_value=((4, 5, 6), 'github'))) as download, \
                 patch.object(session, '_mount_ddi_candidate', AsyncMock(side_effect=[ddi.BridgePrerequisiteError(code, code), None])) as mount:
                if expected_mounts == 2:
                    await session._mount_personalized_ddi(object())
                    download.assert_awaited_once()
                else:
                    with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                        await session._mount_personalized_ddi(object())
                    self.assertEqual(raised.exception.code, code)
                    download.assert_not_awaited()
                self.assertEqual(mount.await_count, expected_mounts)

    async def test_developer_mode_query_failure_is_not_disabled_mode(self):
        for error, code in [(TimeoutError(), 'developer_mode_check_timeout'),
                            (bridge.PasswordRequiredError(), 'apple_device_locked'),
                            (bridge.ConnectionTerminatedError(), 'apple_connection_lost')]:
            with self.subTest(code=code):
                with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                    await self.session()._preflight_developer_environment(
                        SimpleNamespace(get_developer_mode_status=AsyncMock(side_effect=error)))
                self.assertEqual(raised.exception.code, code)

    async def test_total_budget_bounds_all_candidates_and_refresh(self):
        session = self.session()
        async def stalled(_):
            await asyncio.Event().wait()
        with patch.object(bridge, 'PERSONALIZED_DDI_PREPARE_TIMEOUT_SECONDS', .01), \
             patch.object(session, '_prepare_developer_environment', stalled):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await session._preflight_developer_environment(object())
            self.assertEqual(raised.exception.code, 'developer_image_prepare_timeout')
        # A refresh cannot allocate another full preparation budget.
        with patch.object(session, '_refresh_personalized_ddi_impl', stalled):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await asyncio.wait_for(session._refresh_personalized_ddi(object()), 1)
        self.assertEqual(raised.exception.code, 'developer_image_prepare_timeout')

    async def test_stuck_inventory_is_bounded(self):
        async def stalled(_):
            await asyncio.Event().wait()
        with patch.object(bridge, 'PERSONALIZED_DDI_INVENTORY_TIMEOUT_SECONDS', .01), \
             patch.object(bridge.TouchSession, '_query_personalized_ddi_inventory', stalled):
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                await bridge.TouchSession._is_personalized_ddi_mounted(object())
        self.assertEqual(raised.exception.code, 'developer_image_service_timeout')


class HostDisconnectTests(unittest.IsolatedAsyncioTestCase):
    async def test_host_eof_cancels_stalled_startup_and_runs_cleanup(self):
        channel = bridge.BridgeChannel()
        started, closed = asyncio.Event(), asyncio.Event()
        async def connect():
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                closed.set()
        async def eof():
            await started.wait()
            return None
        session = SimpleNamespace(connect=connect, _cleanup=AsyncMock(), _mux_checkpoint=None)
        with patch.object(channel, '_read_message', eof), \
             patch.object(bridge, 'BridgeChannel', return_value=channel), \
             patch.object(bridge, 'TouchSession', return_value=session):
            await asyncio.wait_for(bridge.main_async(120, None, 'usb'), 1)
        self.assertTrue(closed.is_set())
        session._cleanup.assert_awaited_once()

    async def test_disconnect_watch_preserves_all_frames(self):
        channel = bridge.BridgeChannel()
        frames = [{'seq': index} for index in range(20)]
        channel._stdin = io.BytesIO(b''.join(
            struct.pack('<I', len(payload)) + payload
            for payload in (json.dumps(frame).encode() for frame in frames)))
        watcher = asyncio.create_task(channel.wait_for_disconnect())
        await asyncio.sleep(0)
        received = [frame async for frame in channel.read_messages()]
        await asyncio.wait_for(watcher, 1)
        self.assertEqual(received, frames)

    async def test_cancelled_consumer_reuses_the_inflight_frame_read(self):
        channel = bridge.BridgeChannel()
        started, release = asyncio.Event(), asyncio.Event()
        reads = []
        async def read():
            reads.append(True)
            started.set()
            await release.wait()
            return {'seq': 1} if len(reads) == 1 else None
        with patch.object(channel, '_read_message', read):
            watcher = asyncio.create_task(channel.wait_for_disconnect())
            first = channel.read_messages()
            consumer = asyncio.create_task(anext(first))
            await started.wait()
            consumer.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await consumer
            await first.aclose()
            self.assertFalse(channel._reader_task.cancelled())
            release.set()
            received = [frame async for frame in channel.read_messages()]
            await asyncio.wait_for(watcher, 1)
        self.assertEqual(received, [{'seq': 1}])
        self.assertEqual(len(reads), 2)

    async def test_failed_startup_still_emits_original_error_and_cleans_up(self):
        channel = bridge.BridgeChannel()
        channel.emit = AsyncMock()
        async def waiting():
            await asyncio.Event().wait()
        session = SimpleNamespace(
            connect=AsyncMock(side_effect=ddi.BridgePrerequisiteError('developer_image_tss_rejected', 'rejected')),
            _cleanup=AsyncMock(), _mux_checkpoint=None)
        with patch.object(channel, 'wait_for_disconnect', waiting), \
             patch.object(bridge, 'BridgeChannel', return_value=channel), \
             patch.object(bridge, 'TouchSession', return_value=session):
            await asyncio.wait_for(bridge.main_async(120, None, 'usb'), 1)
        self.assertEqual(channel.emit.call_args_list[0].args[0]['code'], 'developer_image_tss_rejected')
        session._cleanup.assert_awaited_once()


class CacheValidationTests(unittest.TestCase):
    def test_missing_control_service_is_not_mislabeled_as_ddi(self):
        error = bridge.InvalidServiceError('InvalidService', 'test-device', '27.0')
        self.assertEqual(bridge.bridge_error_code(error), 'remote_control_service_unavailable')
        self.assertEqual(ddi.device_failure(error).code, 'developer_image_service_unavailable')

    def make_cache(self, root, manifest):
        assets = []
        for name in bridge.PERSONALIZED_DDI_FILES:
            path = root / name
            path.write_bytes(manifest if name == 'BuildManifest.plist' else b'image-or-trust')
            size = path.stat().st_size
            assets.append(bridge.PersonalizedDdiAsset(
                name, name, bridge._git_blob_sha1_file(path, size), size,
                bridge._sha256_file(path), 'test-revision'))
        return tuple(assets)

    def test_shuffled_cache_metadata_is_valid_but_duplicates_are_not(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            assets = self.make_cache(root, plistlib.dumps({'ProductBuildVersion': bridge.LATEST_DDI_BUILD_ID}))
            bridge._write_ddi_metadata(root, (assets[1], assets[2], assets[0]), bridge.LATEST_DDI_BUILD_ID)
            self.assertEqual(bridge._valid_personalized_ddi_bundle(root),
                             tuple(root / name for name in bridge.PERSONALIZED_DDI_FILES))
            bridge._write_ddi_metadata(root, (*assets, assets[0]), bridge.LATEST_DDI_BUILD_ID)
            self.assertIsNone(bridge._valid_personalized_ddi_bundle(root))

    def test_malformed_xml_cache_is_discarded_and_local_bundle_is_actionable(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            assets = self.make_cache(root, b'<?xml version="1.0"?><plist><dict><broken>')
            bridge._write_ddi_metadata(root, assets, bridge.LATEST_DDI_BUILD_ID)
            self.assertIsNone(bridge._valid_personalized_ddi_bundle(root))
            with self.assertRaises(ddi.BridgePrerequisiteError) as raised:
                bridge.local_personalized_ddi_bundle(root)
            self.assertEqual(raised.exception.code, 'developer_image_bundle_invalid')
