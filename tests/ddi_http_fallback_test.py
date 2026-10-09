"""Exercise bad HTTP responses through the actual downloader and cache publisher."""
from contextlib import ExitStack
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import plistlib
import sys
from tempfile import TemporaryDirectory
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b


class HttpFallbackTests(unittest.TestCase):
    def exercise(self, modes, expected_error=None):
        data = {'Image.dmg': b'test-image' * 1024, 'Image.trustcache': b'test-trust',
                'BuildManifest.plist': plistlib.dumps({'ProductBuildVersion': b.LATEST_DDI_BUILD_ID})}
        assets = tuple(b.PersonalizedDdiAsset(name, name,
            hashlib.sha1(f'blob {len(data[name])}\0'.encode() + data[name]).hexdigest(),
            len(data[name]), hashlib.sha256(data[name]).hexdigest(), 'test')
            for name in b.PERSONALIZED_DDI_FILES)
        attempts = []

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def do_GET(self):
                mode, name = self.path.lstrip('/').split('/')
                attempts.append((mode, name))
                payload = data[name]
                self.send_response(429 if mode == 'limited' else 200)
                if mode == 'limited': self.send_header('X-RateLimit-Remaining', '0')
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                try:
                    if mode == 'truncated': payload = payload[:len(payload)//2]
                    # Match size to prove hashes, not just Content-Length, reject it.
                    if mode == 'html': payload = (b'<html>login required</html>' * len(payload))[:len(payload)]
                    self.wfile.write(payload)
                except OSError: pass
                self.close_connection = True

        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with TemporaryDirectory() as directory, ExitStack() as stack:
                cache = Path(directory) / 'cache'
                sources = tuple(b.PersonalizedDdiDownloadSource(mode, 'mirror') for mode in modes)
                stack.enter_context(patch.object(b, '_personalized_ddi_cache_directory', return_value=cache))
                stack.enter_context(patch.object(b, 'PERSONALIZED_DDI_ASSETS', assets))
                stack.enter_context(patch.object(b, 'PERSONALIZED_DDI_GITHUB_REF', b.PERSONALIZED_DDI_PINNED_REVISION))
                stack.enter_context(patch.object(b, '_personalized_ddi_download_sources', return_value=sources))
                stack.enter_context(patch.object(b, '_measure_ddi_sources', return_value=[(s, 1) for s in sources]))
                stack.enter_context(patch.object(b, '_ddi_asset_url', side_effect=lambda source, asset:
                    f'http://127.0.0.1:{server.server_port}/{source.name}/{asset.local_name}'))
                # Only the HTTPS-origin policy is bypassed for loopback. HTTP parsing,
                # socket reads, size/hash validation, fallback and publication are real.
                stack.enter_context(patch.object(b, '_validate_ddi_source_response'))
                if expected_error:
                    with self.assertRaises(b.BridgePrerequisiteError) as raised:
                        b.fetch_automatic_personalized_ddi_bundle()
                    self.assertEqual(expected_error, raised.exception.code)
                    self.assertIsNone(b._valid_personalized_ddi_bundle(cache))
                else:
                    bundle = b.fetch_automatic_personalized_ddi_bundle()
                    self.assertEqual(bundle, b._valid_personalized_ddi_bundle(cache))
                    self.assertEqual([data[name] for name in b.PERSONALIZED_DDI_FILES],
                                     [path.read_bytes() for path in bundle])
                    self.assertEqual([(m, 'BuildManifest.plist') for m in modes], attempts[:len(modes)])
                    self.assertEqual([('valid', 'Image.dmg'), ('valid', 'Image.trustcache')], attempts[len(modes):])
                self.assertEqual([], list(Path(directory).glob('iphoneMirror-ddi-*')))
        finally:
            server.shutdown(); server.server_close(); thread.join(timeout=2)

    def test_login_html_truncation_and_rate_limit_fall_back_to_verified_source(self):
        self.exercise(('html', 'truncated', 'limited', 'valid'))

    def test_all_corrupt_sources_never_publish_and_preserve_integrity_error(self):
        self.exercise(('html', 'truncated'), 'developer_image_download_integrity_failed')

    def test_all_rate_limited_sources_have_actionable_error(self):
        self.exercise(('limited',), 'developer_image_download_rate_limited')
