"""Specification provenance, cross-language catalog drift and parser regressions."""
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('display_catalog_update', ROOT / 'scripts/update_apple_display_catalog.py')
updater = importlib.util.module_from_spec(spec)
spec.loader.exec_module(updater)


class AppleDisplayCatalogTests(unittest.TestCase):
    def test_downloaded_index_coverage_and_generated_outputs(self):
        catalog = json.loads(updater.CATALOG.read_text(encoding='utf-8'))
        identifiers = updater.validate(catalog)
        self.assertEqual(174, len(identifiers))
        self.assertEqual({'iPhone', 'iPad'}, set(catalog['scope']))
        self.assertEqual(updater.generated_header(catalog), updater.HEADER.read_text(encoding='utf-8'))
        self.assertEqual(updater.generated_documentation(catalog), updater.DOCUMENTATION.read_text(encoding='utf-8'))
        for product in catalog['products']:
            self.assertTrue(product['appleSpecUrl'].startswith('https://support.apple.com/'))
            self.assertEqual(64, len(product['appleSpecSha256']))
            self.assertIn('resolution', product['displayQuote'].casefold())

    def test_display_pixels_are_not_camera_or_video_pixels(self):
        source = b'<title>iPhone test - Apple Support</title><h2>Display</h2><p>2340-by-1080-pixel resolution at 476 ppi</p><h2>Video</h2><p>3840-by-2160-pixel resolution at 60 fps</p>'
        result = updater.parse_spec(('test', 'iPhone test', ['iPhone13,1'], 'https://support.apple.com/test'), source)
        self.assertEqual((1080, 2340), (result['width'], result['height']))

    def test_wrong_m5_article_is_rejected_instead_of_copying_11_inch_pixels(self):
        source = b'<title>iPad Pro 11-inch (M5) - Apple Support</title><p>2420-by-1668-pixel resolution at 264 ppi</p>'
        with self.assertRaisesRegex(ValueError, 'wrong M5 display size'):
            updater.parse_spec(('iPadPro13M5', 'iPad Pro 13 M5', ['iPad17,3'], 'https://support.apple.com/test'), source)

    def test_missing_dimensions_never_become_a_guessed_native_size(self):
        source = b'<title>iPhone - Apple Support</title><p>No display specification is available.</p>'
        with self.assertRaisesRegex(ValueError, 'no display resolution'):
            updater.parse_spec(('test', 'iPhone', ['iPhone99,1'], 'https://support.apple.com/test'), source)

    def test_foldable_closed_panel_is_not_replaced_by_first_inner_resolution(self):
        source = b'<title>iPhone Duo - Apple Support</title><p>Inner display 1878-by-2670-pixel resolution at 430 ppi</p><p>Outer display 1398-by-2034-pixel resolution at 460 ppi</p><p>Both displays</p>'
        result = updater.parse_spec(('iPhoneDuo', 'iPhone Duo', ['iPhone19,4'], 'https://support.apple.com/test'), source)
        self.assertEqual((1398, 2034), (result['width'], result['height']))


if __name__ == '__main__':
    unittest.main()
