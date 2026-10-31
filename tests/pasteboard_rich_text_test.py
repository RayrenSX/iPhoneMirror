"""Text already present in rich formats must never require an iOS full pull."""
import asyncio
import plistlib
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from pasteboard_text import html_text, inline_rich_text
import usb_touch_bridge as bridge


class RichTextTests(unittest.IsolatedAsyncioTestCase):
    def test_visible_text_keeps_unicode_lines_cells_and_preformatted_text(self):
        self.assertEqual('中文😀 & café\n第二行\nA\tB\n  code\n    next', html_text(
            '<head><title>not copied</title></head><div>中文😀 &amp; <b>café</b></div>'
            '<p>第二行</p><table><tr><td>A</td><td>B</td></tr></table>'
            '<pre>  code\n    next</pre>'))

    def test_hidden_content_and_scripts_are_not_copied(self):
        self.assertEqual('before after', html_text(
            '<p>before <span hidden>secret</span><span style="display: none">secret</span>'
            '<script>document.write("secret")</script>after</p>'))

    def test_explicit_blank_lines_and_bounded_nesting(self):
        self.assertEqual('first\n\nsecond', html_text('<p>first<br><br>second</p>'))
        self.assertIsNone(html_text('<div>' * 257 + 'text' + '</div>' * 257))

    def test_encoding_is_decoded_without_fetching_resources(self):
        self.assertEqual('café', html_text(b'<meta charset=windows-1252><p>caf\xe9</p>'))
        self.assertEqual('中文', html_text('<p>中文</p>'.encode('utf-16')))
        self.assertEqual('中文', html_text('<p>中文</p>'.encode('gb18030'), 'gb18030'))
        self.assertIsNone(html_text(b'\xff\xff'))
        self.assertIsNone(html_text(b'hello', 'unicode-escape'))

    def test_webarchive_extracts_only_the_copied_main_document(self):
        archive = plistlib.dumps({'WebMainResource': {'WebResourceMIMEType': 'text/html',
            'WebResourceData': '<p>copied 中文😀</p>'.encode(),
            'WebResourceTextEncodingName': 'UTF-8'},
            'WebSubresources': [{'WebResourceData': b'secret'}]}, fmt=plistlib.FMT_BINARY)
        self.assertEqual('copied 中文😀', inline_rich_text({'com.apple.webarchive': {'data': archive}}))
        self.assertIsNone(inline_rich_text({'com.apple.webarchive': {'data': b'invalid'}}))
        self.assertIsNone(inline_rich_text({'public.html': {}}))

    async def test_primary_html_is_read_once_without_resolving_promised_alternatives(self):
        class Ipc:
            async def emit(self, event): pass
        policies = []
        class Service:
            def __init__(self, rsd): pass
            async def __aenter__(self): return self
            async def __aexit__(self, *args): pass
            async def get(self, data_policy=None):
                policies.append(data_policy)
                if data_policy != {'promiseSecondary': {}}:
                    raise TimeoutError('iOS rich conversion stalled')
                return {'items': [{'types': ['public.html', 'public.utf8-plain-text'], 'data': {
                    'public.html': {'data': b'<b>copied</b><br>second line'},
                    'public.utf8-plain-text': {}}}]}
        session = bridge.TouchSession(Ipc(),120)
        session.rsd = object()
        with patch.object(bridge, 'PasteboardService', Service):
            self.assertEqual('copied\nsecond line', await asyncio.wait_for(session._read_device_pasteboard(), 1))
        self.assertEqual([{'promiseSecondary': {}}], policies)


if __name__ == '__main__':
    unittest.main()
