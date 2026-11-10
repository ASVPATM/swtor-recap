#!/usr/bin/env python3
"""Render the project's crossed-lightsaber emblem as SVG, PNG and multi-size ICO."""
from pathlib import Path
import math
import struct
import zlib

root = Path(__file__).resolve().parents[1] / 'assets'
root.mkdir(exist_ok=True)
shapes = []

def line(a, b, width, color):
    shapes.append(('line', a, b, width, color))

# A gold recording arc, two energy blades, and machined hilts. Original artwork.
arc = [(128 + 103 * math.cos(math.radians(a)), 128 + 103 * math.sin(math.radians(a))) for a in range(-67, 248, 3)]
for a, b in zip(arc, arc[1:]):
    line(a, b, 5, '#D8B36A')
shapes.append(('polygon', [(154, 22), (178, 32), (156, 45)], '#D8B36A'))
for start, end, color in [((78, 169), (169, 61), '#5CD9F5'), ((178, 169), (87, 61), '#F27974')]:
    dx, dy = end[0] - start[0], end[1] - start[1]
    length = math.hypot(dx, dy)
    ux, uy = dx / length, dy / length
    line(start, end, 17, color)
    line(start, end, 7, '#F6F6E8')
    grip = (start[0] - ux * 35, start[1] - uy * 35)
    line(grip, start, 17, '#AEBEC9')
    for distance in [9, 18, 27]:
        x, y = start[0] - ux * distance, start[1] - uy * distance
        line((x - uy * 8, y + ux * 8), (x + uy * 8, y - ux * 8), 4, '#283745')
    line((start[0] - uy * 13, start[1] + ux * 13), (start[0] + uy * 13, start[1] - ux * 13), 7, '#E0C895')
svg = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="SWTOR Recap: crossed lightsabers inside a recording arc">', '<rect width="256" height="256" rx="48" fill="#101720"/>']
for shape in shapes:
    if shape[0] == 'line':
        _, a, b, w, color = shape
        svg.append(f'<path d="M{a[0]:.2f} {a[1]:.2f} L{b[0]:.2f} {b[1]:.2f}" fill="none" stroke="{color}" stroke-width="{w}" stroke-linecap="round"/>')
    else:
        _, points, color = shape
        svg.append('<polygon points="' + ' '.join(f'{x},{y}' for x, y in points) + f'" fill="{color}"/>')
(root / 'logo.svg').write_text('\n'.join(svg) + '\n</svg>\n')

def rgba(color):
    return tuple(int(color[i:i + 2], 16) for i in (1, 3, 5)) + (255,)

# Precompute geometry so producing the ICO's small images remains inexpensive.
prepared = []
for s in shapes:
    if s[0] == 'line':
        _, (ax, ay), (bx, by), w, color = s
        prepared.append(('line', ax, ay, bx - ax, by - ay, (bx - ax)**2 + (by - ay)**2, (w / 2)**2, rgba(color)))
    else:
        prepared.append(('polygon', s[1], rgba(s[2])))

def pixel(x, y):
    cx, cy = min(max(x, 48), 208), min(max(y, 48), 208)
    if (x - cx)**2 + (y - cy)**2 > 48**2:
        return (0, 0, 0, 0)
    color = (16, 23, 32, 255)
    for s in prepared:
        if s[0] == 'line':
            _, ax, ay, dx, dy, length, radius, ink = s
            u = min(1, max(0, ((x - ax) * dx + (y - ay) * dy) / length))
            if (x - ax - u * dx)**2 + (y - ay - u * dy)**2 <= radius:
                color = ink
        else:
            _, pts, ink = s
            crosses = [(pts[(i + 1) % 3][0] - pts[i][0]) * (y - pts[i][1]) - (pts[(i + 1) % 3][1] - pts[i][1]) * (x - pts[i][0]) for i in range(3)]
            if min(crosses) >= 0 or max(crosses) <= 0:
                color = ink
    return color

def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)

def png(size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            samples = [pixel((x + (sx + .5) / 2) * 256 / size, (y + (sy + .5) / 2) * 256 / size) for sy in range(2) for sx in range(2)]
            raw.extend(round(sum(p[c] for p in samples) / 4) for c in range(4))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')

sizes = [16, 24, 32, 48, 64, 128, 256]
images = [png(size) for size in sizes]
(root / 'logo.png').write_bytes(images[-1])
offset = 6 + 16 * len(images)
entries = bytearray()
for size, data in zip(sizes, images):
    entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
(root / 'app.ico').write_bytes(struct.pack('<HHH', 0, 1, len(images)) + entries + b''.join(images))
