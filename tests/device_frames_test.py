"""Reject artwork/catalog mismatches before they can reach the embedded app."""
import hashlib
from pathlib import Path
import sys
import tempfile
import unittest
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from prepare_device_frames import verify_artwork, verify_screen_mask


class ArtworkValidationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'frame.png'
        self.image = Image.new('RGBA', (80, 160), (18, 20, 24, 255))
        ImageDraw.Draw(self.image).rectangle((8, 12, 71, 147), fill=(0, 0, 0, 0))
        self.row = dict(frameWidth=80, frameHeight=160, screenX=8, screenY=12,
                        screenWidth=64, screenHeight=136)

    def save(self):
        self.image.save(self.path)
        self.row['sha256'] = hashlib.sha256(self.path.read_bytes()).hexdigest()

    def test_shifted_catalog_rectangle_is_rejected(self):
        self.save()
        self.row['screenX'] += 1
        with self.assertRaisesRegex(ValueError, 'Screen geometry mismatch'):
            verify_artwork(self.row, self.path)

    def test_partial_alpha_is_preserved(self):
        self.image.putpixel((8, 80), (0, 0, 0, 128))
        self.save()
        _, mask = verify_artwork(self.row, self.path)
        self.assertEqual(int(mask[80, 8]), 127)
        self.assertEqual(int(mask[80, 9]), 255)
        self.assertEqual(int(mask[80, 7]), 0)

    def test_open_screen_contour_is_rejected(self):
        ImageDraw.Draw(self.image).line((0, 80, 8, 80), fill=(0, 0, 0, 0))
        self.row.update(screenX=0, screenWidth=72)
        self.save()
        with self.assertRaisesRegex(ValueError, 'Screen connects to exterior'):
            verify_artwork(self.row, self.path)

    def test_opaque_screen_is_rejected(self):
        self.image = Image.new('RGBA', (80, 160), (0, 0, 0, 255))
        self.save()
        with self.assertRaisesRegex(ValueError, 'Opaque screen center'):
            verify_artwork(self.row, self.path)

    def test_stale_embedded_mask_is_rejected(self):
        self.save()
        _, expected = verify_artwork(self.row, self.path)
        path = self.path.with_name('frame-screen.png')
        Image.fromarray(expected).save(path)
        verify_screen_mask(path, expected)
        wrong = expected.copy()
        wrong[12, 8] = 0
        Image.fromarray(wrong).save(path)
        with self.assertRaisesRegex(ValueError, 'Screen mask differs'):
            verify_screen_mask(path, expected)


if __name__ == '__main__':
    unittest.main()
