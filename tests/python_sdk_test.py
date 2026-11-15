"""SDK + real stdio MCP round trips against an isolated HTTP contract fixture."""
import asyncio
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import sys
import threading
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'sdk/python/src'))
from iphonemirror import Client, MirrorError

KEY = 'a' * 64
TOKEN = 'b' * 64
PNG = bytes.fromhex('89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000b49444154789c636000020000050001a5f645400000000049454e44ae426082')


class Handler(BaseHTTPRequestHandler):
    geometry = {'width': 100, 'height': 200, 'version': '1'}
    requests = []
    leased = False
    fail_input = False
    def log_message(self, *args): pass
    def do_GET(self): self.handle_request()
    def do_POST(self): self.handle_request()
    def handle_request(self):
        raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        body = raw if self.headers.get('Content-Type') == 'application/octet-stream' else json.loads(raw or b'null')
        type(self).requests.append((self.command, self.path, body, self.headers.get('X-Control-Session')))
        if self.headers.get('X-API-Key') != KEY:
            return self.reply({'error': {'code': 'UNAUTHORIZED', 'message': 'Invalid key'}}, 401)
        if self.path == '/api/v1/devices':
            return self.reply({'devices': [{'id': 'phone-a'}]})
        if self.path == '/api/v1/hub/actions':
            actions = json.loads((ROOT / 'tools/device_hub_contract.json').read_text())['actions']
            return self.reply({'actions': {k: v for k, v in actions.items() if v['public']}})
        if '/hub/' in self.path:
            action = self.path.rsplit('/', 1)[-1]
            if action == 'read_file':
                return self.reply({'size': 6, 'offset': body.get('offset', 0), 'modified': 1, 'eof': True, 'data': 'YWJjMTIz'})
            return self.reply({'action': action, 'arguments': body})
        if self.path.endswith('/install'):
            if self.headers.get('X-Control-Session') != TOKEN:
                return self.reply({'error': {'code': 'CONTROL_LOCKED', 'message': 'Acquire first'}}, 409)
            return self.reply({'bytes': len(raw), 'name': self.headers.get('X-File-Name')})
        if self.path.endswith('/status') and '/coredevice/' not in self.path:
            return self.reply({'geometry': self.geometry, 'control': {'available': True}})
        if self.path.endswith('/control/acquire'):
            if self.leased and self.headers.get('X-Control-Session') != TOKEN:
                return self.reply({'error': {'code': 'CONTROL_LOCKED', 'message': 'Owner changed'}}, 409)
            type(self).leased = True
            return self.reply({'sessionToken': TOKEN, 'expiresAt': '2099-01-01T00:00:00Z'})
        if self.path.endswith('/control/release'):
            type(self).leased = False
            return self.reply(None, 204)
        if self.path.endswith('/screenshot'):
            return self.reply(PNG)
        if '/input/' in self.path or self.path.endswith('/apps/launch'):
            if not self.leased or self.headers.get('X-Control-Session') != TOKEN:
                return self.reply({'error': {'code': 'CONTROL_LOCKED', 'message': 'Acquire first'}}, 409)
            if self.fail_input:
                return self.reply({'error': {'code': 'INPUT_FAILED', 'message': 'Unknown effect'}}, 500)
            return self.reply({'status': 'dispatched'}, 202)
        return self.reply({'apps': [], 'running': True})

    def reply(self, value, code=200):
        binary = isinstance(value, bytes)
        data = value if binary else b'' if value is None else json.dumps(value).encode()
        self.send_response(code)
        self.send_header('Content-Type', 'image/png' if binary else 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class SdkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True); cls.thread.start()
        cls.address = f'http://127.0.0.1:{cls.server.server_port}'
    @classmethod
    def tearDownClass(cls): cls.server.shutdown(); cls.server.server_close(); cls.thread.join()
    def setUp(self):
        Handler.requests, Handler.leased, Handler.fail_input = [], False, False
        Handler.geometry = {'width': 100, 'height': 200, 'version': '1'}
        self.client = Client(KEY, self.address)

    def test_explicit_device_auth_lease_and_fraction_bounds(self):
        self.assertEqual(self.client.devices()[0]['id'], 'phone-a')
        device = self.client.device('phone-a')
        with self.assertRaises(MirrorError): device.tap(1, 2)
        with device:
            device.tap_fraction(1, 1)
            _, _, body, token = Handler.requests[-1]
            self.assertEqual((body['x'], body['y'], body['geometryVersion'], token), (99, 199, '1', TOKEN))
            self.assertEqual(device.screenshot().png, PNG)
            device.launch_app('com.apple.Preferences')
        self.assertFalse(Handler.leased)

    def test_changed_geometry_and_uncertain_input_never_retry(self):
        device = self.client.device('phone-a').acquire()
        Handler.geometry = {'width': 200, 'height': 100, 'version': '2'}
        with self.assertRaises(MirrorError) as changed: device.tap_fraction(.5, .5)
        self.assertEqual(changed.exception.code, 'GEOMETRY_CHANGED')
        self.assertFalse(any('/input/' in path for _, path, _, _ in Handler.requests))
        Handler.fail_input = True
        with self.assertRaises(MirrorError): device.tap(20, 30)
        self.assertEqual(sum('/input/' in path for _, path, _, _ in Handler.requests), 1)
        device.release()

    def test_renewal_preserves_token_and_bad_coordinates_rejected(self):
        device = self.client.device('phone-a').acquire()
        device._renew_at = 0
        device.key('ENTER')
        acquire = [r for r in Handler.requests if r[1].endswith('/acquire')]
        self.assertEqual([r[3] for r in acquire], [None, TOKEN])
        for value in (-.1, 1.01, float('nan'), True):
            with self.assertRaises(ValueError): device.tap_fraction(value, .5)
        device.release()

    def test_loopback_and_credentials_only(self):
        for address in ('https://127.0.0.1:80', 'http://example.com:80', self.address + '/path', 'http://user:pass@127.0.0.1:80'):
            with self.assertRaises(ValueError): Client(KEY, address)
        with self.assertRaises(ValueError): Client('not-a-key', self.address)
        with self.assertRaises(MirrorError) as invalid: Client('c' * 64, self.address).devices()
        self.assertEqual(invalid.exception.code, 'UNAUTHORIZED')

    def test_mcp_stdio_actual_handshake_tools_and_png(self):
        try:
            import mcp
        except ImportError: self.skipTest('Install sdk/python[mcp]')
        async def run():
            from mcp import ClientSession, StdioServerParameters
            from mcp.client.stdio import stdio_client
            env = {**os.environ, 'IPHONEMIRROR_API_KEY': KEY, 'IPHONEMIRROR_API_URL': self.address,
                   'PYTHONPATH': str(ROOT / 'sdk/python/src')}
            params = StdioServerParameters(command=sys.executable, args=['-m', 'iphonemirror.mcp_server'], env=env)
            async with stdio_client(params) as (read, write):
                async with ClientSession(read, write) as session:
                    await session.initialize()
                    tools = await session.list_tools()
                    self.assertEqual(len(tools.tools), 36)
                    self.assertTrue({'install_app', 'uninstall_app', 'set_phone_setting', 'set_simulated_location',
                        'export_crash_reports', 'poll_syslog', 'terminate_process', 'device_info'} <= {t.name for t in tools.tools})
                    annotations = {t.name: t.annotations for t in tools.tools}
                    self.assertTrue(annotations['uninstall_app'].destructiveHint)
                    self.assertTrue(annotations['get_phone_settings'].readOnlyHint)
                    result = await session.call_tool('list_devices', {})
                    self.assertFalse(result.isError)
                    result = await session.call_tool('acquire_control', {'device_id': 'phone-a'})
                    self.assertFalse(result.isError)
                    result = await session.call_tool('set_phone_setting', {'device_id': 'phone-a', 'key': 'appearance', 'value': 'dark'})
                    self.assertFalse(result.isError)
                    result = await session.call_tool('device_power', {'device_id': 'phone-a', 'action': 'sleep'})
                    self.assertFalse(result.isError)
                    result = await session.call_tool('tap', {'device_id': 'phone-a', 'x': .5, 'y': .5})
                    self.assertFalse(result.isError)
                    shot = await session.call_tool('screenshot', {'device_id': 'phone-a'})
                    self.assertFalse(shot.isError)
                    self.assertEqual([c.type for c in shot.content], ['image', 'text'])
                    await session.call_tool('release_control', {'device_id': 'phone-a'})
        asyncio.run(run())

    def test_management_mutations_uploads_and_downloads(self):
        device = self.client.device('phone-a')
        self.assertEqual(device.device_info()['action'], 'device_info')
        with self.assertRaises(MirrorError): device.set_setting('appearance', 'dark')
        self.assertFalse(any(path.endswith('/hub/set_setting') for _, path, _, _ in Handler.requests))
        with device:
            device.set_setting('appearance', 'dark')
            device.set_location(31.2, 121.5)
            device.device_action('sleep')
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'test.ipa'
                path.write_bytes(b'x' * 200000)
                result = device.install_app(path)
                self.assertEqual(result, {'bytes': 200000, 'name': 'package.ipa'})
                target = Path(directory) / 'download'
                result = device.download('DCIM/file', target)
                self.assertEqual(target.read_bytes(), b'abc123')
                with self.assertRaises(FileExistsError): device.download('DCIM/file', target)
                self.assertEqual(target.read_bytes(), b'abc123')
        self.assertEqual(sum(path.endswith('/hub/set_setting') for _, path, _, _ in Handler.requests), 1)
        with self.assertRaises(ValueError): device.hub('install_app', path='C:/secret.ipa')


if __name__ == '__main__': unittest.main()
