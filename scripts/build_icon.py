from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw


ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def flatten_monochrome(source: Image.Image) -> Image.Image:
    gray = source.convert("L")

    # Remove generated glow/gradients while retaining antialiased edges.
    def level(value: int) -> int:
        if value <= 100:
            return 13
        if value >= 215:
            return 242
        return round(13 + (value - 100) * (229 / 115))

    gray = gray.point(level)
    rgb = Image.merge("RGB", (gray, gray, gray))
    # The generated connector contained a tiny USB trident that turns into
    # noise at 16–24 px. Keep the connector silhouette and clear its interior.
    details = ImageDraw.Draw(rgb)
    details.rounded_rectangle((472, 696, 552, 800), radius=8, fill=(13, 13, 13))
    rgba = rgb.convert("RGBA")

    # Transparent rounded corners look clean in Explorer and title bars while
    # preserving the opaque near-black tile behind the white mark.
    alpha = Image.new("L", rgba.size, 0)
    draw = ImageDraw.Draw(alpha)
    radius = round(min(rgba.size) * 0.16)
    draw.rounded_rectangle((0, 0, rgba.width - 1, rgba.height - 1), radius=radius, fill=255)
    rgba.putalpha(alpha)
    return rgba


def main() -> None:
    parser = argparse.ArgumentParser(description="Build the iPhoneMirror PNG and multi-size ICO.")
    parser.add_argument("source", type=Path)
    parser.add_argument("png", type=Path)
    parser.add_argument("ico", type=Path)
    args = parser.parse_args()

    with Image.open(args.source) as image:
        image = image.convert("RGBA")
        side = min(image.size)
        left = (image.width - side) // 2
        top = (image.height - side) // 2
        square = image.crop((left, top, left + side, top + side))
        square = square.resize((1024, 1024), Image.Resampling.LANCZOS)
        final = flatten_monochrome(square)

    args.png.parent.mkdir(parents=True, exist_ok=True)
    final.save(args.png, format="PNG", optimize=True)
    final.save(args.ico, format="ICO", sizes=[(size, size) for size in ICON_SIZES])

    with Image.open(args.ico) as icon:
        sizes = sorted(icon.ico.sizes())
    expected = {(size, size) for size in ICON_SIZES}
    if set(sizes) != expected:
        raise RuntimeError(f"ICO size mismatch: expected {sorted(expected)}, got {sizes}")

    print(f"PNG={args.png} size={final.size}")
    print(f"ICO={args.ico} frames={len(sizes)} sizes={sizes}")


if __name__ == "__main__":
    main()
