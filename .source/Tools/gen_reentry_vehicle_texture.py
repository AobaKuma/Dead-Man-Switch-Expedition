#!/usr/bin/env python3
"""Placeholder texture for the MIRV re-entry vehicle (docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §8.1).

Draws a blunt-nosed cone pointing north (incoming missile textures are all drawn nose-up) with an
ablative heat-shield gradient. Deterministic; rerun to regenerate. Replace with final art when it exists.

Usage: python -I -X utf8 .source/Tools/gen_reentry_vehicle_texture.py
"""
import os

import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "1.7", "Textures", "Things", "Missile", "ReentryVehicle", "skyFaller_incoming.png")

SIZE = 256
SS = 4  # supersampling


def main():
    n = SIZE * SS
    y, x = np.mgrid[0:n, 0:n].astype(np.float32) / SS
    cx = SIZE / 2
    tip, base = 30.0, 226.0          # cone from the nose (top) to the base plate
    nose_r, base_r = 9.0, 50.0       # blunt nose radius, base half-width

    t = np.clip((y - tip) / (base - tip), 0, 1)
    half = nose_r + (base_r - nose_r) * t
    inside = (y >= tip) & (y <= base) & (np.abs(x - cx) <= half)
    # Rounded nose cap.
    cap = (x - cx) ** 2 + (y - tip) ** 2 <= nose_r ** 2
    shape = inside | cap

    u = np.clip((x - cx) / np.maximum(half, 1e-3), -1, 1)       # -1 left edge .. 1 right edge
    light = 0.55 + 0.45 * np.cos((u + 0.35) * np.pi / 2.2)        # light from upper left
    # Heat shield: scorched brown near the nose, dark carbon towards the base.
    near = np.clip(1 - t * 1.6, 0, 1)[..., None]
    carbon = np.array([46, 44, 42], np.float32)
    scorch = np.array([120, 74, 40], np.float32)
    rgb = (carbon * (1 - near) + scorch * near) * light[..., None]
    # Base plate band and two thin seam rings.
    band = (y > base - 10)
    rgb[band] *= 0.6
    for ring in (0.45, 0.72):
        ry = tip + (base - tip) * ring
        rgb[np.abs(y - ry) < 0.9] *= 0.7

    alpha = shape.astype(np.float32) * 255
    img = np.dstack([np.clip(rgb, 0, 255), alpha]).astype(np.uint8)
    out = Image.fromarray(img, "RGBA").resize((SIZE, SIZE), Image.LANCZOS)
    # Soft 1 px outline so the cone reads on bright terrain.
    a = out.split()[3]
    outline = a.filter(ImageFilter.MaxFilter(3))
    dark = Image.new("RGBA", out.size, (20, 18, 16, 0))
    dark.putalpha(outline.point(lambda v: min(255, v)))
    final = Image.alpha_composite(dark, out)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    final.save(OUT)
    print("wrote", os.path.relpath(OUT, ROOT))


if __name__ == "__main__":
    main()
