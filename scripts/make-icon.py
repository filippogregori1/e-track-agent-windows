#!/usr/bin/env python3
"""Genera le icone dell'agente in assets/ (solo libreria standard, nessuna dipendenza).

- activity-tracker.ico  icona dell'app e dell'installer: tre barre su una piastrella scura
                        (lo stesso motivo del simbolo "chart.bar.xaxis" del Mac)
- tray-tracking.ico     icona nella tray mentre si traccia: barre verdi
- tray-idle.ico         icona nella tray quando non si traccia: barre grigie

Le misure fino a 64 px sono bitmap (DIB 32 bit con alfa), la 256 px è PNG: la forma che Windows legge ovunque.
"""
import os
import struct
import sys
import zlib

SS = 4  # sovracampionamento per l'antialiasing


def rounded_rect(px, py, x0, y0, x1, y1, r):
    """True se (px, py) cade nel rettangolo [x0,x1]×[y0,y1] con angoli di raggio r (unità 0..1)."""
    if px < x0 or px > x1 or py < y0 or py > y1:
        return False
    cx = min(max(px, x0 + r), x1 - r)
    cy = min(max(py, y0 + r), y1 - r)
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def render(size, shapes):
    """shapes: lista di (x0, y0, x1, y1, raggio, colore). Restituisce righe di pixel RGBA dall'alto."""
    n = size * SS
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    px = (x * SS + sx + 0.5) / n
                    py = (y * SS + sy + 0.5) / n
                    for (x0, y0, x1, y1, r, color) in reversed(shapes):
                        if rounded_rect(px, py, x0, y0, x1, y1, r):
                            cr, cg, cb = hex_rgb(color)
                            acc[0] += cr
                            acc[1] += cg
                            acc[2] += cb
                            acc[3] += 255
                            break
            k = SS * SS
            a = acc[3] / k
            if a > 0:
                # colore medio dei soli campioni coperti, alfa = copertura
                cov = acc[3] / 255
                row.append((round(acc[0] / cov), round(acc[1] / cov), round(acc[2] / cov), round(a)))
            else:
                row.append((0, 0, 0, 0))
        rows.append(row)
    return rows


def png(size, rows):
    raw = b"".join(b"\x00" + bytes(c for px in row for c in px) for row in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def dib(size, rows):
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b"".join(bytes((b, g, r, a)) for row in reversed(rows) for (r, g, b, a) in row)
    mask_row = ((size + 31) // 32) * 4
    return header + pixels + b"\x00" * (mask_row * size)


def ico(path, sizes, shapes):
    images = []
    for s in sizes:
        rows = render(s, shapes)
        images.append((s, png(s, rows) if s >= 256 else dib(s, rows)))
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for s, data in images:
        dim = 0 if s >= 256 else s
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b"".join(data for _, data in images)
    with open(path, "wb") as f:
        f.write(out)
    print(f"  {os.path.basename(path)}: {', '.join(str(s) for s in sizes)} px, {len(out)} byte")


def bars(colors, x0, width, gap, bottom, heights, radius):
    shapes = []
    for i, (h, c) in enumerate(zip(heights, colors)):
        x = x0 + i * (width + gap)
        shapes.append((x, bottom - h, x + width, bottom, radius, c))
    return shapes


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(root, "assets")
    os.makedirs(out, exist_ok=True)
    print("==> icone in", out)

    tile = [(0.04, 0.04, 0.96, 0.96, 0.22, "#18191A")]
    app = tile + bars(["#F4F4F6", "#F4F4F6", "#59D499"], 0.22, 0.15, 0.055, 0.76, [0.26, 0.40, 0.54], 0.035)
    ico(os.path.join(out, "activity-tracker.ico"), [16, 20, 24, 32, 40, 48, 64, 256], app)

    tray_heights = [0.42, 0.66, 0.90]
    ico(os.path.join(out, "tray-tracking.ico"), [16, 20, 24, 32, 40, 48, 64],
        bars(["#2EB872"] * 3, 0.08, 0.24, 0.10, 0.95, tray_heights, 0.05))
    ico(os.path.join(out, "tray-idle.ico"), [16, 20, 24, 32, 40, 48, 64],
        bars(["#8A8C8E"] * 3, 0.08, 0.24, 0.10, 0.95, tray_heights, 0.05))
    return 0


if __name__ == "__main__":
    sys.exit(main())
