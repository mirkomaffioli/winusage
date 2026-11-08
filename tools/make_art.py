#!/usr/bin/env python3
"""
Generatore della pixel art di WinUsage (tema subacqueo, personaggi e disegni originali).

Produce, da un'unica fonte:
  - app.ico                       icona dell'exe e della tray (16/32/48/64/128/256 px)
  - src/Sprites.cs                sprite come stringhe ASCII per il toast e le finestre
  - installer/welcome.bmp         immagine laterale dell'installer (164x314)
  - installer/header.bmp          immagine di intestazione dell'installer (150x57)

Uso:  python3 tools/make_art.py [cartella-anteprime]
Richiede Pillow.
"""
import math
import os
import struct
import sys
import io
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

PAL = {
    '.': None,
    'k': (11, 29, 58),      # contorno blu notte
    'w': (255, 244, 196),   # ottone: luce
    'y': (242, 193, 78),    # ottone chiaro
    'B': (201, 138, 43),    # ottone medio
    'b': (138, 90, 26),     # ottone scuro
    'd': (92, 58, 20),
    'C': (191, 243, 255),   # vetro: riflesso
    'c': (63, 184, 220),    # vetro chiaro
    'g': (20, 106, 156),    # vetro medio
    'a': (11, 61, 102),     # vetro scuro
    'W': (234, 248, 255),   # bianco azzurrato
    'u': (124, 200, 240),   # bolle
    'f': (240, 138, 36),    # pesce
    'F': (255, 193, 94),
    'o': (200, 90, 20),
    'e': (255, 255, 255),
    'G': (46, 158, 91),     # alghe
    'h': (27, 107, 62),
    's': (230, 201, 140),   # sabbia
    'S': (196, 164, 104),
    'r': (217, 75, 61),
    't': (64, 196, 168),    # pesce verde-acqua
    'T': (244, 232, 150),
    'n': (22, 112, 124),
}

BUBBLE_CHARS = 'uW'


# ---------------------------------------------------------------- sprite helpers

def blank(w, h):
    return [['.'] * w for _ in range(h)]


def put(g, x, y, c):
    if 0 <= y < len(g) and 0 <= x < len(g[0]):
        g[y][x] = c


def line(g, x0, y0, x1, y1, c):
    steps = max(abs(x1 - x0), abs(y1 - y0), 1)
    for i in range(steps + 1):
        t = i / steps
        put(g, int(round(x0 + (x1 - x0) * t)), int(round(y0 + (y1 - y0) * t)), c)


def outline(g, skip=BUBBLE_CHARS):
    h, w = len(g), len(g[0])
    add = []
    for y in range(h):
        for x in range(w):
            if g[y][x] != '.':
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and g[ny][nx] not in ('.', 'k') and g[ny][nx] not in skip:
                    add.append((x, y))
                    break
    for x, y in add:
        g[y][x] = 'k'


def rows(g):
    return [''.join(r) for r in g]


# ---------------------------------------------------------------- elmo da palombaro

def helmet(n):
    g = blank(n, n)
    cx, cy, R = n * 0.45, n * 0.50, n * 0.33
    big = n >= 32

    # colletto
    x0, x1 = cx - 1.0 * R, cx + 1.0 * R
    y0, y1 = cy + 0.72 * R, cy + 1.28 * R
    for y in range(n):
        for x in range(n):
            px, py = x + .5, y + .5
            if x0 <= px <= x1 and y0 <= py <= y1:
                t = (py - y0) / (y1 - y0)
                g[y][x] = 'y' if t < 0.22 else ('B' if t < 0.62 else 'b')

    # presa d'aria sul lato destro (solo grande)
    if big:
        for y in range(int(cy - 1.5), int(cy + 3)):
            for x in range(int(cx + R - 1), int(cx + R + 3)):
                g[y][x] = 'y' if y == int(cy - 1.5) else 'B'
        put(g, int(cx + R + 2), int(cy + 2), 'b')

    # pomello
    kr = max(1.0, 0.2 * R)
    kx, ky = cx, cy - R - kr * 0.6
    for y in range(n):
        for x in range(n):
            if math.hypot(x + .5 - kx, y + .5 - ky) <= kr:
                g[y][x] = 'y' if (x + .5 < kx and y + .5 < ky) else 'B'

    # cupola con luce da in alto a sinistra
    for y in range(n):
        for x in range(n):
            dist = math.hypot(x + .5 - cx, y + .5 - cy)
            if dist <= R:
                nx, ny = (x + .5 - cx) / R, (y + .5 - cy) / R
                d = -0.55 * nx - 0.65 * ny
                r = dist / R
                if d > 0.62 and r < 0.85:
                    c = 'w'
                elif d > 0.18:
                    c = 'y'
                elif d > -0.32:
                    c = 'B'
                else:
                    c = 'b'
                if r > 0.88 and d < 0.1:
                    c = 'b'
                g[y][x] = c

    # oblò con orologio (le 3:00)
    pcx, pcy, pr = cx, cy + 0.10 * R, 0.56 * R
    rw = max(1.0, 0.14 * R)
    for y in range(n):
        for x in range(n):
            px, py = x + .5, y + .5
            dist = math.hypot(px - pcx, py - pcy)
            nx, ny = (px - pcx) / pr, (py - pcy) / pr
            l = -0.7 * nx - 0.7 * ny
            if dist <= pr:
                g[y][x] = 'c' if l > 0.45 else ('g' if l > -0.3 else 'a')
                if big and dist > pr - 0.9:
                    g[y][x] = 'k'
            elif dist <= pr + rw:
                g[y][x] = 'y' if l > 0 else 'b'
    ix, iy = int(math.floor(pcx)), int(math.floor(pcy))
    line(g, ix, iy, ix, int(round(iy - 0.72 * pr)), 'W')            # lancetta dei minuti (ore 12)
    line(g, ix, iy, int(round(ix + 0.52 * pr)), iy, 'W')            # lancetta delle ore (ore 3)
    gx, gy = int(round(pcx - 0.5 * pr)), int(round(pcy - 0.55 * pr))
    put(g, gx, gy, 'C')
    if big:
        put(g, gx + 1, gy, 'C')
        put(g, gx, gy + 1, 'C')

    # rivetti
    if big:
        for i in range(8):
            a = math.radians(22.5 + 45 * i)
            rr = pr + rw + 1.7
            put(g, int(round(pcx + rr * math.cos(a) - .5)), int(round(pcy + rr * math.sin(a) - .5)), 'w')
        for bx in (cx - 0.55 * R, cx, cx + 0.55 * R):
            put(g, int(round(bx - .5)), int(round((y0 + y1) / 2 - .5)), 'd')

    outline(g)

    # bolle (sopra e a destra, senza contorno)
    if big:
        b5 = [".uuu.", "uW..u", "u...u", "u...u", ".uuu."]
        for j, r in enumerate(b5):
            for i, c in enumerate(r):
                if c != '.':
                    put(g, 26 + i, 4 + j, c)
        b3 = [".u.", "u.u", ".u."]
        for j, r in enumerate(b3):
            for i, c in enumerate(r):
                if c != '.':
                    put(g, 28 + i, 12 + j, c)
        put(g, 25, 14, 'u')
    else:
        put(g, 13, 2, 'u'); put(g, 14, 3, 'u'); put(g, 13, 4, 'u'); put(g, 12, 3, 'u')
        put(g, 14, 7, 'W')
    return g


# ---------------------------------------------------------------- altri sprite

def fish():
    art = [
        "......kkkk.....",
        ".kk..kttnttkk..",
        "knnkkttnttttkk.",
        "knnnttnttteketk",
        "knnnttntttttttk",
        "knnkkTTnTTTTkk.",
        ".kk..kTTnTTkk..",
        "......kkkk.....",
    ]
    return [list(r) for r in art]


def seaweed(h, phase=0.0):
    w = 6
    g = blank(w, h)
    for y in range(h):
        sway = int(round(1.4 * math.sin(phase + y * 0.55)))
        x = 2 + sway
        t = (h - y) / h
        put(g, x, y, 'G')
        if t > 0.2:
            put(g, x + 1, y, 'h')
        if y % 4 == 1:
            put(g, x - 1, y, 'G')
    return g


# ---------------------------------------------------------------- rendering

def to_image(g, scale=1):
    h, w = len(g), len(g[0])
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    px = im.load()
    for y in range(h):
        for x in range(w):
            c = PAL[g[y][x]]
            if c:
                px[x, y] = c + (255,)
    if scale != 1:
        im = im.resize((w * scale, h * scale), Image.NEAREST)
    return im


def paste(dst, g, x, y):
    dst.alpha_composite(to_image(g), (x, y))


def scene(w, h, with_helmet=True, helm16=False, seed=3):
    """Scena oceano a bande (come il blu profondo dei fondali), logica a bassa risoluzione."""
    bands = [(124, 214, 235), (90, 190, 226), (58, 160, 214), (36, 128, 196),
             (26, 98, 170), (20, 72, 140), (14, 52, 108), (10, 36, 80)]
    im = Image.new("RGBA", (w, h), (0, 0, 0, 255))
    px = im.load()
    sea_h = int(h * 0.86)
    nb = len(bands)
    for y in range(h):
        pos = min(y / max(sea_h - 1, 1), 0.9999) * nb
        i = int(pos)
        frac = pos - i
        for x in range(w):
            j = i
            # sfumatura a scacchiera ai bordi delle bande
            if i + 1 < nb and ((x + y) % 2 == 0 and frac > 0.75 or (x % 2 == 0 and y % 2 == 0 and frac > 0.5)):
                j = i + 1
            px[x, y] = bands[j] + (255,)
    # sabbia
    for y in range(sea_h, h):
        for x in range(w):
            px[x, y] = ((230, 201, 140) if (x * 7 + y * 3) % 5 else (196, 164, 104)) + (255,)
    for x in range(w):
        px[x, sea_h] = (196, 164, 104, 255)
    # alghe
    for k, x in enumerate(range(3, w - 4, max(w // 4, 8))):
        sw = seaweed(10 + (k * 5) % 9, phase=k * 1.3)
        paste(im, sw, x, sea_h - len(sw) + 2)
    # bolle
    import random
    rnd = random.Random(seed)
    for _ in range(max(4, w // 10)):
        bx, by = rnd.randrange(3, w - 4), rnd.randrange(3, int(sea_h * 0.8))
        paste(im, [list(r) for r in (".u.", "u.u", ".u.")], bx, by)
    # pesce
    f = fish()
    paste(im, f, int(w * 0.5), int(sea_h * 0.62))
    if with_helmet:
        hm = helmet(16 if helm16 else 32)
        paste(im, hm, (w - len(hm[0])) // 2 - (2 if not helm16 else 0), max(2, int(sea_h * 0.22)))
    return im


def write_ico(path, images):
    """ICO con voci DIB (<=128 px) e PNG (256 px)."""
    entries = []
    for im in images:
        w, h = im.size
        if w >= 256:
            bio = io.BytesIO()
            im.save(bio, "PNG")
            data = bio.getvalue()
        else:
            px = im.convert("RGBA").load()
            xor = bytearray()
            for y in range(h - 1, -1, -1):
                for x in range(w):
                    r, g_, b, a = px[x, y]
                    xor += bytes((b, g_, r, a))
            row = ((w + 31) // 32) * 4
            andmask = bytearray()
            for y in range(h - 1, -1, -1):
                bits = bytearray(row)
                for x in range(w):
                    if px[x, y][3] == 0:
                        bits[x // 8] |= 0x80 >> (x % 8)
                andmask += bits
            hdr = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(andmask), 0, 0, 0, 0)
            data = hdr + bytes(xor) + bytes(andmask)
        entries.append((w, h, data))
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for w, h, data in entries:
        out += struct.pack("<BBBBHHII", 0 if w >= 256 else w, 0 if h >= 256 else h, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, _, data in entries:
        out += data
    with open(path, "wb") as f:
        f.write(out)


def cs_array(name, g):
    lines = ",\n".join('            "%s"' % r for r in rows(g))
    return "        public static readonly string[] %s =\n        {\n%s\n        };\n" % (name, lines)


def write_sprites_cs(path):
    pal_lines = []
    for k, v in PAL.items():
        if v:
            pal_lines.append("            { '%s', Color.FromArgb(%d, %d, %d) }," % ((k,) + v))
    parts = [
        "// <auto-generated> da tools/make_art.py: non modificare a mano. </auto-generated>\n"
        "using System.Collections.Generic;\nusing System.Drawing;\n\n"
        "namespace WinUsage\n{\n    internal static class Sprites\n    {\n"
        "        public static readonly Dictionary<char, Color> Palette = new Dictionary<char, Color>\n        {\n"
        + "\n".join(pal_lines) + "\n        };\n\n",
        cs_array("Helmet32", helmet(32)), "\n",
        cs_array("Helmet16", helmet(16)), "\n",
        cs_array("Fish", fish()), "\n",
        cs_array("Seaweed0", seaweed(7, 0.0)), "\n",
        cs_array("Seaweed1", seaweed(7, 2.0)), "\n",
        cs_array("Bubble5", [list(r) for r in [".uuu.", "uW..u", "u...u", "u...u", ".uuu."]]), "\n",
        cs_array("Bubble3", [list(r) for r in [".u.", "u.u", ".u."]]),
        "    }\n}\n",
    ]
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(parts))


def main():
    preview = sys.argv[1] if len(sys.argv) > 1 else None
    h16, h32 = helmet(16), helmet(32)
    i16, i32 = to_image(h16), to_image(h32)
    images = [i16, i32, to_image(h16, 3), to_image(h32, 2), to_image(h32, 4), to_image(h32, 8)]
    write_ico(os.path.join(ROOT, "app.ico"), images)
    write_sprites_cs(os.path.join(ROOT, "src", "Sprites.cs"))

    os.makedirs(os.path.join(ROOT, "installer"), exist_ok=True)
    welcome = scene(82, 157).resize((164, 314), Image.NEAREST).convert("RGB")
    welcome.save(os.path.join(ROOT, "installer", "welcome.bmp"), "BMP")
    header = scene(50, 19, helm16=True, seed=7).resize((150, 57), Image.NEAREST).convert("RGB")
    header.save(os.path.join(ROOT, "installer", "header.bmp"), "BMP")

    if preview:
        os.makedirs(preview, exist_ok=True)
        sheet = Image.new("RGBA", (760, 330), (30, 60, 100, 255))
        sheet.alpha_composite(to_image(h16, 8), (10, 10))
        sheet.alpha_composite(to_image(h32, 8), (160, 10))
        sheet.alpha_composite(to_image(fish(), 8), (440, 10))
        sheet.alpha_composite(to_image(seaweed(12, 0), 8), (600, 10))
        sheet.alpha_composite(to_image(h16, 1), (440, 150))
        sheet.alpha_composite(to_image(h32, 1), (470, 150))
        sheet.alpha_composite(to_image(h16, 2), (520, 150))
        sheet.save(os.path.join(preview, "sprites.png"))
        welcome.save(os.path.join(preview, "welcome.png"))
        header.save(os.path.join(preview, "header.png"))
    print("ok")


if __name__ == "__main__":
    main()
