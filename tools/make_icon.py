#!/usr/bin/env python3
"""Generates Tersus.ico (multi-size, PNG-compressed) with no third-party libraries.

Design: rounded square (navy -> azure gradient) with a white donut chart that has a missing slice,
a nod to "where did the space go". Run:  python3 tools/make_icon.py Tersus.App/Assets/Tersus.ico
"""
import math
import struct
import sys
import zlib

NAVY = (0x14, 0x29, 0x43)
AZURE = (0x22, 0x65, 0xD1)
SS = 4  # supersampling


def render(size):
    n = size * SS
    px = [[(0, 0, 0, 0)] * n for _ in range(n)]
    r_corner = 0.22 * n
    cx = cy = n / 2
    r_out, r_in = 0.34 * n, 0.19 * n
    gap_a0, gap_a1 = math.radians(-70), math.radians(-25)  # missing slice
    for y in range(n):
        for x in range(n):
            # rounded square
            dx = max(abs(x + 0.5 - cx) - (cx - r_corner), 0)
            dy = max(abs(y + 0.5 - cy) - (cy - r_corner), 0)
            if dx * dx + dy * dy > r_corner * r_corner:
                continue
            t = (x + y) / (2 * n)
            col = tuple(int(NAVY[i] + (AZURE[i] - NAVY[i]) * t) for i in range(3))
            # donut with gap
            ddx, ddy = x + 0.5 - cx, y + 0.5 - cy
            d = math.hypot(ddx, ddy)
            if r_in <= d <= r_out:
                a = math.atan2(ddy, ddx)
                if not (gap_a0 <= a <= gap_a1):
                    col = (255, 255, 255)
            # small dot in the gap (the "space to recover")
            gx = cx + math.cos((gap_a0 + gap_a1) / 2) * (r_in + r_out) / 2 * 1.12
            gy = cy + math.sin((gap_a0 + gap_a1) / 2) * (r_in + r_out) / 2 * 1.12
            if math.hypot(x + 0.5 - gx, y + 0.5 - gy) <= 0.055 * n:
                col = (0x7F, 0xD1, 0xAE)
            px[y][x] = (*col, 255)
    # downsample
    out = []
    for y in range(size):
        row = []
        for x in range(size):
            r = g = b = a = 0
            for j in range(SS):
                for i in range(SS):
                    p = px[y * SS + j][x * SS + i]
                    r += p[0] * p[3]
                    g += p[1] * p[3]
                    b += p[2] * p[3]
                    a += p[3]
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                row.append((r // a, g // a, b // a, a // (SS * SS)))
        out.append(row)
    return out


def png(rows):
    h, w = len(rows), len(rows[0])
    raw = b"".join(b"\x00" + b"".join(bytes(p) for p in row) for row in rows)

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main(path):
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [png(render(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for s, img in zip(sizes, images):
        entries += struct.pack("<BBBBHHII", 0 if s >= 256 else s, 0 if s >= 256 else s, 0, 0, 1, 32, len(img), offset)
        offset += len(img)
    with open(path, "wb") as fh:
        fh.write(header + entries + b"".join(images))
    print(f"wrote {path} ({offset} bytes, sizes {sizes})")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "Tersus.ico")
