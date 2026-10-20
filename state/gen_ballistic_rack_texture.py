"""Generate the placeholder texture for the ballistic missile launch rack.

The rack is drawn empty on purpose: the assembled missile body is rendered on top at
runtime by DMSE.CompMissileBodyRenderer, so the centre of the cradle must stay open.

Usage:
    python state/gen_ballistic_rack_texture.py

Writes 1.7/Textures/Things/Building/BallisticRack/building.png (512x512 RGBA).
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "1.7", "Textures", "Things", "Building", "BallisticRack", "building.png")

SIZE = 512
SS = 4

OUTLINE = (13, 17, 15, 255)
DARK = (44, 56, 51, 255)
MID = (70, 86, 78, 255)
LIGHT = (90, 109, 99, 255)
BOLT = (24, 31, 28, 255)
BOLT_HI = (120, 134, 126, 255)


def px(point, size):
    x, y = point
    return ((x + 1.0) * 0.5 * size, (y + 1.0) * 0.5 * size)


def rect(d, x0, y0, x1, y1, fill, size, outline=True):
    a = px((x0, y0), size)
    b = px((x1, y1), size)
    d.rectangle([a[0], a[1], b[0], b[1]],
                fill=fill,
                outline=OUTLINE if outline else None,
                width=int(round(2.0 * SS)))


def ring(d, cx, cy, r_out, r_in, color, size):
    """Donut with a transparent hole so the missile shows through."""
    box_out = [px((cx - r_out, cy - r_out), size), px((cx + r_out, cy + r_out), size)]
    d.ellipse([box_out[0][0], box_out[0][1], box_out[1][0], box_out[1][1]], fill=OUTLINE)
    r_edge = r_out - 0.012
    box = [px((cx - r_edge, cy - r_edge), size), px((cx + r_edge, cy + r_edge), size)]
    d.ellipse([box[0][0], box[0][1], box[1][0], box[1][1]], fill=color)

    box_in = [px((cx - r_in, cy - r_in), size), px((cx + r_in, cy + r_in), size)]
    d.ellipse([box_in[0][0], box_in[0][1], box_in[1][0], box_in[1][1]], fill=OUTLINE)
    r_hole = r_in - 0.012
    box_hole = [px((cx - r_hole, cy - r_hole), size), px((cx + r_hole, cy + r_hole), size)]
    d.ellipse(
        [box_hole[0][0], box_hole[0][1], box_hole[1][0], box_hole[1][1]],
        fill=(0, 0, 0, 0),
    )


def bolt(d, cx, cy, r, size):
    box = [px((cx - r, cy - r), size), px((cx + r, cy + r), size)]
    d.ellipse([box[0][0], box[0][1], box[1][0], box[1][1]], fill=BOLT)
    r2 = r * 0.45
    box2 = [px((cx - r2, cy - r2), size), px((cx + r2, cy + r2), size)]
    d.ellipse([box2[0][0], box2[0][1], box2[1][0], box2[1][1]], fill=BOLT_HI)


def main():
    size = SIZE * SS
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # base deck (slightly inset) — the "架子" floor plate
    rect(d, -0.86, -0.86, 0.86, 0.86, DARK, size)

    # cross beams (north / south)
    rect(d, -0.80, -0.78, 0.80, -0.60, MID, size)
    rect(d, -0.80, 0.60, 0.80, 0.78, MID, size)

    # side rails (west / east)
    rect(d, -0.80, -0.60, -0.62, 0.60, MID, size)
    rect(d, 0.62, -0.60, 0.80, 0.60, MID, size)

    # cradle arms from the rails to the launch ring (west / east)
    rect(d, -0.62, -0.10, -0.40, 0.10, LIGHT, size)
    rect(d, 0.40, -0.10, 0.62, 0.10, LIGHT, size)

    # launch ring: open in the middle so the missile stands inside the frame
    ring(d, 0.0, 0.0, 0.40, 0.29, LIGHT, size)

    # corner plates + bolts
    for sx in (-1, 1):
        for sy in (-1, 1):
            cx, cy = 0.68 * sx, 0.68 * sy
            rect(d, cx - 0.13, cy - 0.13, cx + 0.13, cy + 0.13, LIGHT, size)
            bolt(d, cx, cy, 0.045, size)

    # hazard stripes on the south beam: reads as a launch platform
    for i in range(-4, 5):
        x = i * 0.16
        a = px((x - 0.04, 0.63), size)
        b = px((x + 0.04, 0.63), size)
        c = px((x + 0.10, 0.75), size)
        e = px((x + 0.02, 0.75), size)
        d.polygon([a, b, c, e], fill=(168, 142, 58, 255))

    img = img.resize((SIZE, SIZE), Image.LANCZOS)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT, "PNG", optimize=True)
    print(f"wrote {os.path.relpath(OUT, ROOT)} ({img.size[0]}x{img.size[1]})")


if __name__ == "__main__":
    main()
