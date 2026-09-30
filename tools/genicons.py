#!/usr/bin/env python3
"""Draws the level editor icons: isometric sand box, cylinder and ball, and the
open cardboard crate the sprayer is.

    python3 tools/genicons.py Sand/Resources

These are generated rather than rendered because there is no Unity editor in this
repository's loop and a screenshot of the object would need one. Flat isometric
shapes in the medium's own colour say what the object is at 128 pixels, which is
all an icon has to do.

Supersampled 3x and box-filtered down, because a hard isometric edge aliases badly.
"""
import math
import struct
import sys
import zlib

SIZE = 128
SS = 3                      # supersample factor
W = SIZE * SS

SAND = (216, 184, 124)
CARD = (183, 141, 90)


def shade(rgb, k):
    return tuple(max(0, min(255, int(c * k))) for c in rgb)


class Canvas(object):
    def __init__(self):
        self.px = [[0, 0, 0, 0] for _ in range(W * W)]

    def put(self, x, y, rgb):
        if 0 <= x < W and 0 <= y < W:
            self.px[y * W + x] = [rgb[0], rgb[1], rgb[2], 255]

    def polygon(self, pts, rgb):
        ys = [p[1] for p in pts]
        for y in range(int(min(ys)), int(max(ys)) + 1):
            xs = []
            n = len(pts)
            for i in range(n):
                x0, y0 = pts[i]
                x1, y1 = pts[(i + 1) % n]
                if (y0 <= y < y1) or (y1 <= y < y0):
                    xs.append(x0 + (y - y0) * (x1 - x0) / (y1 - y0))
            xs.sort()
            for i in range(0, len(xs) - 1, 2):
                for x in range(int(math.ceil(xs[i])), int(xs[i + 1]) + 1):
                    self.put(x, y, rgb)

    def ellipse(self, cx, cy, rx, ry, rgb):
        for y in range(int(cy - ry), int(cy + ry) + 1):
            dy = (y - cy) / ry
            if abs(dy) > 1:
                continue
            half = rx * math.sqrt(max(0.0, 1 - dy * dy))
            for x in range(int(cx - half), int(cx + half) + 1):
                self.put(x, y, rgb)

    def sphere(self, cx, cy, r, rgb):
        lx, ly, lz = -0.45, -0.6, 0.66
        for y in range(int(cy - r), int(cy + r) + 1):
            for x in range(int(cx - r), int(cx + r) + 1):
                nx, ny = (x - cx) / r, (y - cy) / r
                d = nx * nx + ny * ny
                if d > 1:
                    continue
                nz = math.sqrt(1 - d)
                lam = max(0.0, nx * lx + ny * ly + nz * lz)
                self.put(x, y, shade(rgb, 0.45 + 0.75 * lam))

    def save(self, path):
        # box filter down to SIZE
        rows = []
        for y in range(SIZE):
            row = bytearray([0])
            for x in range(SIZE):
                r = g = b = a = 0
                for j in range(SS):
                    for i in range(SS):
                        p = self.px[(y * SS + j) * W + (x * SS + i)]
                        r += p[0]; g += p[1]; b += p[2]; a += p[3]
                n = SS * SS
                row += bytes((r // n, g // n, b // n, a // n))
            rows.append(bytes(row))
        raw = b"".join(rows)

        def chunk(t, d):
            c = struct.pack(">I", len(d)) + t + d
            return c + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)

        out = b"\x89PNG\r\n\x1a\n"
        out += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
        out += chunk(b"IDAT", zlib.compress(raw, 9))
        out += chunk(b"IEND", b"")
        open(path, "wb").write(out)
        print("wrote %s" % path)


def iso(x, y, z, scale, ox, oy):
    """Unit-cube space to screen. The classic 2:1 isometric, z up the screen."""
    sx = (x - y) * math.cos(math.radians(30)) * scale + ox
    sy = ((x + y) * math.sin(math.radians(30)) - z) * scale + oy
    return (sx, sy)


def box(path, rgb, open_top=False):
    c = Canvas()
    s, ox, oy = W * 0.30, W * 0.5, W * 0.56
    h = 0.5

    def p(x, y, z):
        return iso(x, y, z, s, ox, oy)

    # Three faces are all an isometric cube ever shows, and which three depends on
    # the projection: with sx = (x - y) and sy = (x + y) - z the camera sees +x, +y
    # and +z. Drawing -x instead leaves a face missing and the cube reads as a flat L.
    c.polygon([p(h, -h, -h), p(h, h, -h), p(h, h, h), p(h, -h, h)], shade(rgb, 0.70))
    c.polygon([p(-h, h, -h), p(h, h, -h), p(h, h, h), p(-h, h, h)], shade(rgb, 0.88))
    c.polygon([p(-h, -h, h), p(-h, h, h), p(h, h, h), p(h, -h, h)], shade(rgb, 1.12))

    if open_top:
        # the mouth, inset, with two inner walls catching less light
        i = h * 0.72
        c.polygon([p(-i, -i, h), p(-i, i, h), p(i, i, h), p(i, -i, h)], shade(rgb, 0.38))
        # The inner walls you can see are the far ones: -x and -y.
        c.polygon([p(-i, -i, h), p(-i, i, h), p(-i, i, -i), p(-i, -i, -i)], shade(rgb, 0.56))
        c.polygon([p(-i, -i, h), p(i, -i, h), p(i, -i, -i), p(-i, -i, -i)], shade(rgb, 0.48))
    c.save(path)


def cylinder(path, rgb):
    c = Canvas()
    s, ox, oy = W * 0.30, W * 0.5, W * 0.54
    r, h = 0.5, 0.5
    rx, ry = r * s * math.cos(math.radians(30)) * 2, r * s * math.sin(math.radians(30)) * 2
    top_y = oy - h * s
    bot_y = oy + h * s

    # body first, then the cap over it
    c.polygon([(ox - rx, top_y), (ox + rx, top_y), (ox + rx, bot_y), (ox - rx, bot_y)],
              shade(rgb, 0.80))
    c.ellipse(ox, bot_y, rx, ry, shade(rgb, 0.70))
    # a soft vertical falloff so the body does not read as a flat rectangle
    for x in range(int(ox - rx), int(ox + rx) + 1):
        u = (x - ox) / rx
        k = 0.62 + 0.55 * math.sqrt(max(0.0, 1 - u * u))
        for y in range(int(top_y), int(bot_y) + 1):
            c.put(x, y, shade(rgb, k))
    c.ellipse(ox, bot_y, rx, ry, shade(rgb, 0.66))
    c.ellipse(ox, top_y, rx, ry, shade(rgb, 1.15))
    c.save(path)


def ball(path, rgb):
    c = Canvas()
    c.sphere(W * 0.5, W * 0.5, W * 0.36, rgb)
    c.save(path)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    box(out + "/SandBox_icon.png", SAND)
    cylinder(out + "/SandCylinder_icon.png", SAND)
    ball(out + "/SandBall_icon.png", SAND)
    box(out + "/SandSprayer_icon.png", CARD, open_top=True)
