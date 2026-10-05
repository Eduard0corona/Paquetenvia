#!/usr/bin/env python3
"""Generates the PWA icons in public/icons/ with the standard library only (zlib + struct).

The glyph is a neutral parcel box with no brand name or lettering. Run from apps/web:

    python3 scripts/generate-pwa-icons.py
"""

import struct
import zlib
from pathlib import Path

BACKGROUND = (0x10, 0x2A, 0x43)  # theme_color
LID = (0xC9, 0x8B, 0x4F)
BODY = (0xE0, 0xA9, 0x6D)
TAPE = (0xF0, 0xF4, 0xF8)
SUPERSAMPLE = 4


def inside_rounded_rect(x, y, left, top, right, bottom, radius):
    if x < left or x > right or y < top or y > bottom:
        return False
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius


def glyph_shapes(scale):
    """Box glyph in unit coordinates, scaled around the centre (maskable keeps the safe zone)."""

    def s(value):
        return 0.5 + (value - 0.5) * scale

    return [
        # (left, top, right, bottom, radius, color)
        (s(0.24), s(0.40), s(0.76), s(0.78), 0.03 * scale, BODY),
        (s(0.20), s(0.28), s(0.80), s(0.42), 0.03 * scale, LID),
        (s(0.45), s(0.28), s(0.55), s(0.78), 0.0, TAPE),
    ]


def sample(x, y, maskable):
    if maskable:
        color, alpha = BACKGROUND, 1.0
    elif inside_rounded_rect(x, y, 0.0, 0.0, 1.0, 1.0, 0.18):
        color, alpha = BACKGROUND, 1.0
    else:
        return (0, 0, 0, 0.0)
    for left, top, right, bottom, radius, shape_color in glyph_shapes(0.8 if maskable else 1.0):
        if inside_rounded_rect(x, y, left, top, right, bottom, radius):
            color = shape_color
    return (*color, alpha)


def render(size, maskable):
    rows = []
    step = 1.0 / (size * SUPERSAMPLE)
    for py in range(size):
        row = bytearray([0])  # filter type 0
        for px in range(size):
            red = green = blue = alpha = 0.0
            for sy in range(SUPERSAMPLE):
                for sx in range(SUPERSAMPLE):
                    r, g, b, a = sample(
                        (px * SUPERSAMPLE + sx + 0.5) * step,
                        (py * SUPERSAMPLE + sy + 0.5) * step,
                        maskable,
                    )
                    red += r * a
                    green += g * a
                    blue += b * a
                    alpha += a
            count = SUPERSAMPLE * SUPERSAMPLE
            if alpha == 0:
                row += bytes(4)
            else:
                row += bytes(
                    (
                        round(red / alpha),
                        round(green / alpha),
                        round(blue / alpha),
                        round(255 * alpha / count),
                    )
                )
        rows.append(bytes(row))
    return b"".join(rows)


def png(size, pixels):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(pixels, 9))
        + chunk(b"IEND", b"")
    )


def main():
    target = Path(__file__).resolve().parent.parent / "public" / "icons"
    target.mkdir(parents=True, exist_ok=True)
    for name, size, maskable in (
        ("icon-192.png", 192, False),
        ("icon-512.png", 512, False),
        ("icon-maskable-192.png", 192, True),
        ("icon-maskable-512.png", 512, True),
    ):
        (target / name).write_bytes(png(size, render(size, maskable)))
        print(f"public/icons/{name}")


if __name__ == "__main__":
    main()
