"""Generate placeholder skyfaller textures for the new generation missile bodies.

Usage:
    python state/gen_missile_placeholders.py

Writes 512x512 RGBA PNGs to 1.7/Textures/Things/Missile/<Model>/skyFaller_incoming.png.
Style matches the existing art in that folder: top-down view, nose pointing north,
flat military palette with a dark outline. These are deliberately simple stand-ins
and are meant to be replaced by hand-drawn art later.
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEX_ROOT = os.path.join(ROOT, "1.7", "Textures", "Things", "Missile")

SIZE = 512
SS = 4  # supersampling factor for smooth edges

OUTLINE = (26, 26, 28, 255)


def mirrored(points):
    """Return the given left-side polygon plus its x-mirrored twin."""
    return [points, [(-x, y) for (x, y) in points]]


def px(point, size):
    """Map normalized coords (-1..1, nose at y=-1) to pixels."""
    x, y = point
    return ((x + 1.0) * 0.5 * size, (y + 1.0) * 0.5 * size)


def draw_poly(draw, points, fill, size, outline=OUTLINE, width=3.0):
    pts = [px(p, size) for p in points]
    draw.polygon(pts, fill=fill, outline=outline, width=int(round(width * SS)))


def build(*, body, body_top, nose, fins, fin_color, acc_nodes=(), acc_color=None, extra=()):
    """Assemble one missile silhouette. Every list of points is a single polygon."""
    size = SIZE * SS
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # tail fins / wings first so the body outline sits on top of them
    for poly in fins:
        draw_poly(d, poly, fin_color, size)

    # nose cone: base sits exactly on the body's top edge, tip points north
    draw_poly(d, body, body_top, size)
    if nose:
        draw_poly(d, nose, body_top, size)

    if acc_color is not None:
        for poly in acc_nodes:
            draw_poly(d, poly, acc_color, size)

    for poly, color in extra:
        draw_poly(d, poly, color, size)

    return img.resize((SIZE, SIZE), Image.LANCZOS)


def body_rect(half, y_nose, y_tail):
    return [(-half, y_nose), (half, y_nose), (half, y_tail), (-half, y_tail)]


def cone(half, y_base, y_tip):
    return [(-half, y_base), (0.0, y_tip), (half, y_base)]


def texel(*, half, y_nose, y_tail, y_cone, fins, fin_color, body_color,
          acc_color=None, acc_bands=(), nav_tip=None):
    """y_cone = shoulder where the nose cone meets the body; y_nose = cone tip."""
    patches = []
    img = build(
        body=body_rect(half, y_cone, y_tail),
        body_top=body_color,
        nose=cone(half, y_cone, y_nose),
        fins=fins,
        fin_color=fin_color,
        acc_nodes=acc_bands,
        acc_color=acc_color,
        extra=patches,
    )
    if nav_tip is not None:
        size = SIZE
        d = ImageDraw.Draw(img)
        d.ellipse(
            [px((-0.03, nav_tip - 0.03), size)[0], px((0, nav_tip), size)[1],
             px((0.03, nav_tip + 0.03), size)[0], px((0, nav_tip + 0.06), size)[1]],
            fill=(210, 210, 210, 255),
        )
    return img


MISSILES = {}

# --- V1 (Fi 103): straight mid wings, boxy pulsejet bus, olive drab ------------
MISSILES["V1"] = texel(
    half=0.075, y_nose=-0.95, y_tail=0.82, y_cone=-0.72,
    fins=mirrored([(-0.075, 0.10), (-0.88, 0.26), (-0.88, 0.37), (-0.075, 0.42)])
    + mirrored([(-0.075, 0.64), (-0.30, 0.79), (-0.30, 0.87), (-0.075, 0.82)]),
    fin_color=(78, 82, 66, 255),
    body_color=(112, 120, 96, 255),
    acc_color=(150, 142, 108, 255),
    acc_bands=[[(-0.075, 0.44), (0.075, 0.44), (0.075, 0.50), (-0.075, 0.50)]],
)

# --- V2 (A4): long slender bus, four big trapezoid fins ----------------------
MISSILES["V2"] = texel(
    half=0.090, y_nose=-0.95, y_tail=0.88, y_cone=-0.78,
    fins=mirrored([(-0.090, 0.52), (-0.72, 0.78), (-0.72, 0.94), (-0.090, 0.86)])
    + mirrored([(-0.090, 0.04), (-0.38, 0.21), (-0.38, 0.31), (-0.090, 0.23)]),
    fin_color=(62, 66, 60, 255),
    body_color=(88, 92, 80, 255),
    acc_color=(140, 124, 96, 255),
    acc_bands=[
        [(-0.090, -0.70), (0.090, -0.70), (0.090, -0.62), (-0.090, -0.62)],
        [(-0.090, 0.62), (0.090, 0.62), (0.090, 0.70), (-0.090, 0.70)],
    ],
)

# --- Rheintochter: oversize main fins + canards, grey with bronze bands -----
MISSILES["Rheintochter"] = texel(
    half=0.080, y_nose=-0.95, y_tail=0.90, y_cone=-0.62,
    fins=mirrored([(-0.080, 0.18), (-0.82, 0.54), (-0.82, 0.72), (-0.080, 0.60)])
    + mirrored([(-0.080, -0.46), (-0.36, -0.36), (-0.36, -0.26), (-0.080, -0.30)]),
    fin_color=(62, 64, 68, 255),
    body_color=(122, 124, 126, 255),
    acc_color=(178, 132, 70, 255),
    acc_bands=[
        [(-0.080, -0.16), (0.080, -0.16), (0.080, -0.08), (-0.080, -0.08)],
        [(-0.080, 0.70), (0.080, 0.70), (0.080, 0.78), (-0.080, 0.78)],
    ],
)

# --- Hellfire: compact, mid fins + tail fins, seeker tip --------------------
MISSILES["Hellfire"] = texel(
    half=0.070, y_nose=-0.92, y_tail=0.90, y_cone=-0.80,
    fins=mirrored([(-0.070, 0.00), (-0.42, 0.17), (-0.42, 0.26), (-0.070, 0.17)])
    + mirrored([(-0.070, 0.62), (-0.55, 0.78), (-0.55, 0.87), (-0.070, 0.81)]),
    fin_color=(56, 58, 60, 255),
    body_color=(152, 154, 152, 255),
    acc_color=(208, 172, 62, 255),
    acc_bands=[
        [(-0.070, 0.30), (0.070, 0.30), (0.070, 0.40), (-0.070, 0.40)],
    ],
    nav_tip=-0.90,
)

# --- Minuteman III: very long slender stack, small tail fins ---------------
MISSILES["Minuteman"] = texel(
    half=0.055, y_nose=-0.96, y_tail=0.92, y_cone=-0.78,
    fins=mirrored([(-0.055, 0.60), (-0.34, 0.78), (-0.34, 0.90), (-0.055, 0.86)]),
    fin_color=(58, 60, 64, 255),
    body_color=(206, 206, 200, 255),
    acc_color=(152, 62, 54, 255),
    acc_bands=[
        [(-0.055, 0.42), (0.055, 0.42), (0.055, 0.50), (-0.055, 0.50)],
    ],
)

# --- Tomahawk: slender, straight pop-out wings, tail fins ------------------
MISSILES["Tomahawk"] = texel(
    half=0.065, y_nose=-0.92, y_tail=0.90, y_cone=-0.80,
    fins=mirrored([(-0.065, 0.22), (-0.80, 0.32), (-0.80, 0.43), (-0.065, 0.41)])
    + mirrored([(-0.065, 0.66), (-0.44, 0.79), (-0.44, 0.88), (-0.065, 0.84)]),
    fin_color=(70, 72, 74, 255),
    body_color=(142, 146, 148, 255),
    acc_color=(88, 122, 68, 255),
    acc_bands=[
        [(-0.065, 0.50), (0.065, 0.50), (0.065, 0.58), (-0.065, 0.58)],
    ],
)


def main():
    for name, img in MISSILES.items():
        folder = os.path.join(TEX_ROOT, name)
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, "skyFaller_incoming.png")
        img.save(path, "PNG", optimize=True)
        print(f"wrote {os.path.relpath(path, ROOT)} ({img.size[0]}x{img.size[1]})")


if __name__ == "__main__":
    main()
