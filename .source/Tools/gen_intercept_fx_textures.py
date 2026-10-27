"""
Procedural textures for the intercept debris FX (docs/攔截殘骸特效_實作與技術美術計畫.md §7, P6) and the missile
launch FX (docs/導彈發射特效_重製計畫.md §6).

Writes PNGs into 1.7/Textures/Things/Effects/InterceptDebris/ and .../MissileFx/. Deterministic (fixed seeds): rerun after
editing a recipe, never hand-edit the outputs. These are generated stand-ins with the right shape, size
and alpha falloff; a hand-painted set can replace any file with the same name.

Usage: python .source/Tools/gen_intercept_fx_textures.py [mod root]
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..')
OUT = os.path.join(ROOT, '1.7', 'Textures', 'Things', 'Effects', 'InterceptDebris')
OUT_LAUNCH = os.path.join(ROOT, '1.7', 'Textures', 'Things', 'Effects', 'MissileFx')


def save(img, *parts, out=None):
    path = os.path.join(out or OUT, *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)


def grid(n):
    y, x = np.mgrid[0:n, 0:n].astype(np.float32)
    c = (n - 1) / 2.0
    return (x - c) / c, (y - c) / c  # -1..1


def rgba(rgb, alpha):
    """rgb: (n,n,3) or 3-tuple in 0..1; alpha: (n,n) 0..1."""
    a = np.clip(alpha, 0, 1)
    if np.ndim(rgb) == 1 or isinstance(rgb, tuple):
        rgb = np.broadcast_to(np.array(rgb, np.float32), alpha.shape + (3,))
    arr = np.dstack([np.clip(rgb, 0, 1), a])
    return Image.fromarray((arr * 255 + 0.5).astype(np.uint8), 'RGBA')


def value_noise(n, cells, rng):
    """Smooth value noise in 0..1 (bilinear-upsampled random grid)."""
    g = rng.random((cells + 1, cells + 1)).astype(np.float32)
    img = Image.fromarray((g * 255).astype(np.uint8), 'L').resize((n, n), Image.BICUBIC)
    return np.asarray(img, np.float32) / 255.0


def fbm(n, rng, octaves=(4, 8, 16), weights=(0.55, 0.3, 0.15)):
    return sum(w * value_noise(n, c, rng) for c, w in zip(octaves, weights))


# ---------------------------------------------------------------- glow / spark

def glow_radial(n=128):
    x, y = grid(n)
    r = np.sqrt(x * x + y * y)
    a = np.clip(1 - r, 0, 1) ** 2.2
    return rgba((1, 1, 1), a)


def spark_star(n=64):
    x, y = grid(n)
    r = np.sqrt(x * x + y * y)
    core = np.clip(1 - r / 0.22, 0, 1) ** 1.5
    arm_h = np.clip(1 - np.abs(y) / 0.05, 0, 1) * np.clip(1 - np.abs(x), 0, 1) ** 1.6
    arm_v = np.clip(1 - np.abs(x) / 0.05, 0, 1) * np.clip(1 - np.abs(y), 0, 1) ** 1.6
    d1, d2 = (x + y) / math.sqrt(2), (x - y) / math.sqrt(2)
    arm_d = 0.45 * (np.clip(1 - np.abs(d1) / 0.035, 0, 1) * np.clip(1 - np.abs(d2) / 0.55, 0, 1)
                    + np.clip(1 - np.abs(d2) / 0.035, 0, 1) * np.clip(1 - np.abs(d1) / 0.55, 0, 1))
    a = np.clip(core + arm_h + arm_v + arm_d, 0, 1)
    return rgba((1, 1, 1), a)


# ---------------------------------------------------------------- smoke / fire

def puff_alpha(n, rng, lumps=7):
    """Cauliflower-like puff: a few overlapping soft discs eroded by noise."""
    x, y = grid(n)
    a = np.zeros((n, n), np.float32)
    for _ in range(lumps):
        cx, cy = rng.uniform(-0.28, 0.28, 2)
        rad = rng.uniform(0.45, 0.65)
        d = np.sqrt((x - cx) ** 2 + (y - cy) ** 2) / rad
        a = np.maximum(a, np.clip(1 - d, 0, 1) ** 0.8)
    noise = fbm(n, rng)
    a = np.clip(a * (0.75 + 0.6 * noise) * 1.35 - 0.05, 0, 1)
    edge = np.clip((1 - np.sqrt(x * x + y * y)) / 0.25, 0, 1)  # keep the quad edge clear
    return np.clip(a * edge, 0, 1)


def smoke_puff(seed, shade, n=128):
    rng = np.random.default_rng(seed)
    a = puff_alpha(n, rng)
    # Self-shading: lighter top-left, darker bottom-right, plus noise mottling.
    x, y = grid(n)
    light = 0.82 + 0.18 * np.clip(0.5 - 0.35 * (x - y), 0, 1) + 0.12 * (fbm(n, rng) - 0.5)
    rgb = np.dstack([np.clip(shade * light, 0, 1)] * 3)
    return rgba(rgb, a)


def fireball(seed, n=128):
    rng = np.random.default_rng(seed)
    a = puff_alpha(n, rng, lumps=6)
    x, y = grid(n)
    heat = np.clip(1 - np.sqrt(x * x + y * y) / 0.75, 0, 1) * (0.7 + 0.6 * fbm(n, rng))
    heat = np.clip(heat, 0, 1)
    c_core = np.array([1.0, 0.97, 0.78])
    c_mid = np.array([1.0, 0.62, 0.18])
    c_edge = np.array([0.55, 0.22, 0.08])
    t = heat[..., None]
    rgb = np.where(t > 0.5, c_mid + (c_core - c_mid) * ((t - 0.5) * 2), c_edge + (c_mid - c_edge) * (t * 2))
    return rgba(rgb, a)


# ---------------------------------------------------------------- metal debris

def shard(seed, n, kind):
    """Irregular metal fragment: polygon, brushed shading, dark rim, one bright edge."""
    rng = np.random.default_rng(seed)
    s = 4  # supersample
    N = n * s
    img = Image.new('RGBA', (N, N), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = N / 2
    if kind == 'panel':
        w, h = rng.uniform(0.55, 0.8) * N / 2, rng.uniform(0.3, 0.5) * N / 2
        ang = rng.uniform(0, math.pi)
        pts = []
        for px, py in [(-w, -h), (w, -h * rng.uniform(0.6, 1)), (w * rng.uniform(0.7, 1), h), (-w, h)]:
            pts.append((c + px * math.cos(ang) - py * math.sin(ang), c + px * math.sin(ang) + py * math.cos(ang)))
    else:
        k = rng.integers(4, 7) if kind == 'major' else rng.integers(3, 5)
        base = rng.uniform(0, 2 * math.pi)
        pts = []
        for i in range(k):
            t = base + 2 * math.pi * i / k + rng.uniform(-0.35, 0.35)
            r = N / 2 * rng.uniform(0.45, 0.9)
            pts.append((c + r * math.cos(t), c + r * math.sin(t)))
    base_shade = rng.uniform(0.62, 0.8)
    d.polygon(pts, fill=(255, 255, 255, 255))
    mask = np.asarray(img, np.float32)[..., 3] / 255.0
    yy, xx = np.mgrid[0:N, 0:N].astype(np.float32) / N
    brushed = 0.97 + 0.03 * np.sin((xx * math.cos(base_shade * 9) + yy) * rng.uniform(18, 30))
    shade = base_shade * brushed * (0.85 + 0.25 * (1 - yy))
    rgb = np.dstack([shade * 0.98, shade, shade * 1.04])
    if kind == 'panel':
        for _ in range(3):  # rivets
            rx, ry = pts[0][0] + (pts[2][0] - pts[0][0]) * rng.uniform(0.2, 0.8), pts[0][1] + (pts[2][1] - pts[0][1]) * rng.uniform(0.2, 0.8)
            rr = N * 0.025
            m = ((xx * N - rx) ** 2 + (yy * N - ry) ** 2) < rr * rr
            rgb[m] *= 0.6
    out = Image.fromarray((np.dstack([np.clip(rgb, 0, 1), mask]) * 255).astype(np.uint8), 'RGBA')
    d = ImageDraw.Draw(out)
    d.line(pts + [pts[0]], fill=(40, 42, 46, 255), width=max(2, s))      # dark rim
    i = rng.integers(len(pts))
    d.line([pts[i], pts[(i + 1) % len(pts)]], fill=(245, 245, 250, 255), width=max(2, s))  # lit edge
    return out.resize((n, n), Image.LANCZOS)


def scrap_filth(seed, n=64):
    rng = np.random.default_rng(seed)
    img = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    for _ in range(rng.integers(5, 9)):
        piece = shard(int(rng.integers(1 << 30)), 16, 'minor')
        x, y = rng.integers(4, n - 20, 2)
        img.alpha_composite(piece.rotate(float(rng.uniform(0, 360)), expand=False), (int(x), int(y)))
    return img


# ---------------------------------------------------------------- launch plume / ground cloud

def plume(seed, w, h, width_scale, core):
    """Rocket plume, nozzle at the TOP edge (texture v = 1 in game = toward the missile), tail fading downward.
    Half-width swells just behind the nozzle and tapers to the tail; the core is narrow and white-hot."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    v = y / (h - 1)                    # 0 at the nozzle (top row), 1 at the tail
    u = (x - (w - 1) / 2) / ((w - 1) / 2)  # -1..1 across
    swell = 0.55 + 0.45 * np.sin(np.pi * np.clip(v * 1.8, 0, 1))
    half = width_scale * swell * (1 - v) ** 0.75 + 1e-3
    if not core:
        # Ragged edge: per-row noise on the half-width.
        rows = np.asarray(Image.fromarray((rng.random((h // 8 + 1, 1)) * 255).astype(np.uint8), 'L')
                          .resize((1, h), Image.BICUBIC), np.float32)[:, 0] / 255.0
        half = half * (0.85 + 0.3 * rows[:, None])
    edge = np.exp(-((u / half) ** 2) * (3.0 if core else 2.0))
    intensity = (1 - v) ** (2.2 if core else 1.2) * np.clip(v / 0.03, 0, 1) ** 0.5
    a = np.clip(edge * intensity * (1.6 if core else 1.1), 0, 1)
    if not core:
        a *= 0.8 + 0.4 * fbm(max(w, h), rng)[:h, :w]
    return rgba((1, 1, 1), np.clip(a, 0, 1))


def ground_cloud(seed, n=128):
    """Dust/smoke billow for the launch ground cloud: a lumpy puff squashed to 85 % height with a darker underside.
    (60 % read as flat pancakes in game, 2026-10-26; the spread on the ground comes from the emitter's lobes instead.)"""
    rng = np.random.default_rng(seed)
    a = puff_alpha(n, rng, lumps=12)
    img = Image.fromarray((a * 255).astype(np.uint8), 'L').resize((n, int(n * 0.85)), Image.LANCZOS)
    canvas = Image.new('L', (n, n), 0)
    canvas.paste(img, (0, (n - img.size[1]) // 2))
    a = np.asarray(canvas, np.float32) / 255.0
    x, y = grid(n)
    shade = 0.92 - 0.18 * np.clip((y + 0.3) / 0.6, 0, 1) + 0.08 * (fbm(n, rng) - 0.5)
    return rgba(np.dstack([shade] * 3), a)


def main():
    save(glow_radial(), 'Glow_Radial.png')
    save(spark_star(), 'Spark_Star.png')
    for i, ch in enumerate('abc'):
        save(smoke_puff(100 + i, 0.95), 'SmokeLight', f'SmokeLight_{ch}.png')
        save(smoke_puff(200 + i, 0.32), 'SmokeDark', f'SmokeDark_{ch}.png')
        save(smoke_puff(250 + i, 0.6), 'SmokeMid', f'SmokeMid_{ch}.png')
        save(fireball(300 + i), 'Fireball', f'Fireball_{ch}.png')
    for i, ch in enumerate('abcdef'):
        save(shard(400 + i, 64, 'major'), 'Major', f'Major_{ch}.png')
    for i, ch in enumerate('abcdefgh'):
        save(shard(500 + i, 32, 'minor'), 'Minor', f'Minor_{ch}.png')
    for i, ch in enumerate('abcd'):
        save(shard(600 + i, 64, 'panel'), 'Pod', f'Pod_{ch}.png')
    for i, ch in enumerate('abc'):
        save(scrap_filth(700 + i), 'Filth', f'Filth_MetalScrap_{ch}.png')
    save(plume(800, 64, 256, 0.32, True), 'Plume_Core.png', out=OUT_LAUNCH)
    save(plume(801, 96, 256, 0.62, False), 'Plume_Outer', 'Plume_Outer_a.png', out=OUT_LAUNCH)
    save(plume(802, 96, 256, 0.62, False), 'Plume_Outer', 'Plume_Outer_b.png', out=OUT_LAUNCH)
    for i, ch in enumerate('abc'):
        save(ground_cloud(900 + i), 'GroundCloud', f'GroundCloud_{ch}.png', out=OUT_LAUNCH)
    print('wrote', OUT, 'and', OUT_LAUNCH)


if __name__ == '__main__':
    main()
