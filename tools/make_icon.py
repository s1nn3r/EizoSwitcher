"""Renders the EizoSwitcher icon (ICO with all Windows sizes + PNGs for docs).

usage: python make_icon.py <out_dir>        (requires Pillow)
"""
import io, struct, sys
from pathlib import Path
from PIL import Image, ImageDraw

BEZEL = (30, 36, 48, 255)
BEZEL_EDGE = (148, 160, 178, 255)
WARM = ((255, 95, 109), (255, 195, 113))
COOL = ((54, 209, 220), (91, 134, 229))
BADGE = (248, 250, 252, 255)


def gradient(size, c1, c2):
    """Diagonal gradient from top-left (c1) to bottom-right (c2)."""
    w, h = size
    g = Image.new("RGBA", size)
    px = g.load()
    for y in range(h):
        for x in range(w):
            t = (x / max(w - 1, 1) + y / max(h - 1, 1)) / 2
            px[x, y] = tuple(int(a + (b - a) * t) for a, b in zip(c1, c2)) + (255,)
    return g


def screen(img, box, radius, divider):
    """Screen area split diagonally into a warm and a cool half."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    rounded = Image.new("L", (w, h), 0)
    ImageDraw.Draw(rounded).rounded_rectangle((0, 0, w - 1, h - 1), radius, fill=255)
    warm_mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(warm_mask).polygon([(0, 0), (w, 0), (0, h)], fill=255)
    cool_mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(cool_mask).polygon([(w, 0), (w, h), (0, h)], fill=255)
    layer = Image.new("RGBA", (w, h))
    layer.paste(gradient((w, h), *WARM), (0, 0), warm_mask)
    layer.paste(gradient((w, h), *COOL), (0, 0), cool_mask)
    if divider:
        ImageDraw.Draw(layer).line([(0, h), (w, 0)], fill=(255, 255, 255, 235), width=divider)
    img.paste(layer, (x0, y0), rounded)


def swap_badge(d, cx, cy, r, s):
    """White circle with two curved arrows (= switching)."""
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=BADGE, outline=BEZEL, width=int(6 * s))
    ar = r * 0.56
    w = int(9 * s)
    box = (cx - ar, cy - ar, cx + ar, cy + ar)
    d.arc(box, 200, 330, fill=BEZEL, width=w)
    d.arc(box, 20, 150, fill=BEZEL, width=w)
    head = 11 * s
    # arrow heads at the end of each arc (330° and 150°), pointing along the turn direction.
    # Pillow draws the arc width inward, so the stroke's center line is half a width inside the box.
    import math
    rc = ar - w / 2
    for ang in (330, 150):
        a = math.radians(ang)
        px, py = cx + rc * math.cos(a), cy + rc * math.sin(a)
        tx, ty = -math.sin(a), math.cos(a)  # tangent (clockwise)
        nx, ny = math.cos(a), math.sin(a)
        d.polygon([
            (px + tx * head * 1.2, py + ty * head * 1.2),
            (px + nx * head, py + ny * head),
            (px - nx * head, py - ny * head),
        ], fill=BEZEL)


def draw_large(px):
    s = px * 4 / 256  # supersampled scale (256-unit design grid)
    big = Image.new("RGBA", (px * 4, px * 4), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)
    S = lambda *v: tuple(int(round(c * s)) for c in v)
    # stand
    d.polygon([S(112, 184), S(144, 184), S(152, 210), S(104, 210)], fill=(42, 49, 64, 255))
    d.rounded_rectangle(S(76, 204, 180, 222), int(8 * s), fill=BEZEL, outline=BEZEL_EDGE, width=max(1, int(3 * s)))
    # bezel
    d.rounded_rectangle(S(12, 26, 244, 190), int(22 * s), fill=BEZEL, outline=BEZEL_EDGE, width=max(1, int(4 * s)))
    screen(big, S(26, 40, 230, 176), int(10 * s), max(1, int(4 * s)))
    swap_badge(d, 198 * s, 186 * s, 50 * s, s)
    return big.resize((px, px), Image.LANCZOS)


def draw_small(px):
    """Simplified glyph for 16-24 px (tray): no badge, thicker bezel, short stand."""
    k = 8
    s = px * k / 16
    big = Image.new("RGBA", (px * k, px * k), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)
    S = lambda *v: tuple(int(round(c * s)) for c in v)
    d.rectangle(S(6.5, 12, 9.5, 14), fill=(42, 49, 64, 255))
    d.rounded_rectangle(S(4, 13.6, 12, 15.4), int(0.8 * s), fill=BEZEL, outline=BEZEL_EDGE, width=int(0.5 * s))
    d.rounded_rectangle(S(0.4, 1.6, 15.6, 12.4), int(2 * s), fill=BEZEL, outline=BEZEL_EDGE, width=int(0.6 * s))
    screen(big, S(1.8, 3, 14.2, 11), int(0.8 * s), int(0.7 * s))
    return big.resize((px, px), Image.LANCZOS)


def write_ico(path, images):
    """ICO with PNG-compressed entries (supported since Windows Vista)."""
    blobs = []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, "PNG")
        blobs.append(buf.getvalue())
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for im, blob in zip(images, blobs):
        w, h = im.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    Path(path).write_bytes(header + entries + b"".join(blobs))


def main(out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [draw_small(n) if n <= 24 else draw_large(n) for n in sizes]
    write_ico(out / "EizoSwitcher.ico", images)
    draw_large(512).save(out / "EizoSwitcher-512.png")
    draw_small(32).save(out / "EizoSwitcher-tray-32.png")
    # preview sheet on light and dark background
    sheet = Image.new("RGBA", (sum(sizes) + 10 * len(sizes) + 10, 2 * 276), (240, 240, 240, 255))
    sheet.paste((32, 32, 32, 255), (0, 276, sheet.width, sheet.height))
    x = 10
    for n, im in zip(sizes, images):
        sheet.alpha_composite(im, (x, 10))
        sheet.alpha_composite(im, (x, 286))
        x += n + 10
    sheet.save(out / "preview.png")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
