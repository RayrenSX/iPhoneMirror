"""Exercise actual HTTP socket buffering, rather than mocking iter_content."""
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as b


class StreamingDeadlineTests(unittest.TestCase):
    def test_trickled_bytes_do_not_hide_deadline_until_full_chunk(self):
        stopped = threading.Event()
        payload = b'x' * 200
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def do_GET(self):
                self.send_response(200)
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                try:
                    for byte in payload:
                        self.wfile.write(bytes([byte])); self.wfile.flush()
                        if stopped.wait(.01): break
                except (BrokenPipeError, ConnectionResetError, OSError): pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            # Local HTTP isolates socket buffering; production HTTPS/redirect
            # validation remains covered by the existing source tests.
            with TemporaryDirectory() as directory, patch.object(b, '_ddi_asset_url',
                    return_value=f'http://127.0.0.1:{server.server_port}/Image.dmg'), \
                    patch.object(b, '_validate_ddi_source_response'):
                asset = b.PersonalizedDdiAsset('Image.dmg', 'Image.dmg',
                    hashlib.sha1(b'blob 200\0' + payload).hexdigest(), 200,
                    hashlib.sha256(payload).hexdigest())
                started = time.monotonic()
                with self.assertRaises(b.BridgePrerequisiteError) as raised:
                    b._download_personalized_ddi_asset(b.PersonalizedDdiDownloadSource('local', 'raw'),
                        asset, Path(directory) / 'image', deadline=started + .15)
                self.assertEqual(raised.exception.code, 'developer_image_download_timeout')
                self.assertLess(time.monotonic() - started, .8)
                started = time.monotonic()
                with self.assertRaises(b.BridgePrerequisiteError) as metadata:
                    b._github_request_json(f'http://127.0.0.1:{server.server_port}/metadata',
                                           deadline=started + .15)
                self.assertEqual(metadata.exception.code, 'developer_image_download_timeout')
                self.assertLess(time.monotonic() - started, .8)
                with b.requests.get(f'http://127.0.0.1:{server.server_port}/probe', stream=True,
                                    timeout=1, **b._request_proxy_kwargs(None)) as response:
                    started = time.monotonic()
                    with self.assertRaises(TimeoutError):
                        b._read_probe_payload(response, 200, deadline=started + .15)
                    self.assertLess(time.monotonic() - started, .8)
        finally:
            stopped.set()
            server.shutdown(); server.server_close(); thread.join(timeout=2)
