#!/usr/bin/env python3
"""Original placeholder icons for Editor. Not derived from any Microsoft asset."""

import struct
import zlib
from pathlib import Path

INK = (28, 61, 90, 255)
PAPER = (244, 240, 230, 255)
CLAY = (196, 85, 42, 255)
RULE = (210, 204, 190, 255)


def png(width: int, height: int, pixels: bytes) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + pixels[y * width * 4:(y + 1) * width * 4] for y in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def blend(a, b, t: float):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(4))


def mark(width: int, height: int, wide: bool = False) -> bytes:
    pixels = bytearray(width * height * 4)
    radius = max(4, min(width, height) // 8)

    def set_px(x: int, y: int, color) -> None:
        if 0 <= x < width and 0 <= y < height:
            i = (y * width + x) * 4
            pixels[i:i + 4] = bytes(color)

    for y in range(height):
        for x in range(width):
            dx = min(x, width - 1 - x)
            dy = min(y, height - 1 - y)
            inside = dx >= radius or dy >= radius or (radius - dx) ** 2 + (radius - dy) ** 2 <= radius ** 2
            set_px(x, y, INK if inside else (0, 0, 0, 0))

    margin_x = width // 6 if not wide else width // 5
    margin_y = height // 6
    page_left = margin_x
    page_right = width - margin_x
    page_top = margin_y
    page_bottom = height - margin_y
    if wide:
        page_right = width // 2 + margin_x
    for y in range(page_top, page_bottom):
        for x in range(page_left, page_right):
            set_px(x, y, PAPER)

    rule_gap = max(3, height // 14)
    for y in range(page_top + rule_gap, page_bottom - 2, rule_gap):
        for x in range(page_left + 4, page_right - 4):
            set_px(x, y, RULE)

    caret_x = page_left + max(4, (page_right - page_left) // 5)
    for y in range(page_top + rule_gap, min(page_bottom - 2, page_top + rule_gap * 3)):
        set_px(caret_x, y, CLAY)
        if width > 40:
            set_px(caret_x + 1, y, CLAY)
    return png(width, height, bytes(pixels))


def main() -> None:
    out = Path(__file__).resolve().parents[1] / "src" / "Editor.App" / "Assets"
    out.mkdir(parents=True, exist_ok=True)
    files = {
        "Square44x44Logo.png": mark(44, 44),
        "Square150x150Logo.png": mark(150, 150),
        "StoreLogo.png": mark(50, 50),
        "Wide310x150Logo.png": mark(310, 150, wide=True),
        "SplashScreen.png": mark(620, 300, wide=True),
    }
    for name, data in files.items():
        (out / name).write_bytes(data)
        print(name, len(data))


if __name__ == "__main__":
    main()
