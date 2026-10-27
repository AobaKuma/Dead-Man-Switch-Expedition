"""Missile sprite sizes by real length (docs/Missile_Skyfaller轉Projectile_計畫.md §8.8).

On-screen length of the FULL missile (with booster) = max(1.2, 3.9 x (length / 9.7 m) ** 0.75) cells:
Moskit (9.7 m) stays at 3.9 cells, the real 12x spread becomes about 5x, nothing is smaller than 1.2 cells.
Every texture of one model (launch, incoming without booster, item/ammo) is drawn at the same pixel scale,
so one drawSize per model serves all of its defs: drawSize = screen length / (full sprite length / texture height).

Writes <drawSize> (and scales item <drawOffset> z by the same ratio) for every def listed in MODELS; launch
projectiles copy their incoming def's drawSize when gen_missile_launch_defs.py is rerun (done by main()).

  python .source/Tools/apply_missile_sizes.py [mod root]            apply
  python .source/Tools/apply_missile_sizes.py [mod root] --report   outline report only (match V1's outline)
  python .source/Tools/apply_missile_sizes.py [mod root] --only Tochka,Yj   apply to these models only
"""
import glob
import os
import re
import subprocess
import sys

import numpy as np
from PIL import Image

ROOT = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else os.path.join(os.path.dirname(__file__), "..", "..")
TEX = os.path.join(ROOT, "1.7", "Textures")
REPORT = "--report" in sys.argv
ONLY = sys.argv[sys.argv.index("--only") + 1].split(",") if "--only" in sys.argv else None

ANCHOR_LENGTH, ANCHOR_CELLS, POWER, MIN_CELLS = 9.7, 3.9, 0.75, 1.2

# model: (full length in m incl. booster, full texture, defs that draw this model, other textures of the model)
# Reference lengths: the real model where there is one; AGM-65 and YJ are known without booster, so the boosted
# length follows from the art (full sprite length / no-booster sprite length): AGM-65 2.49 x 482/347, YJ 6.3 x 494/281.
MODELS = {
    "Stinger":           (1.52, "Things/Weapons/DMSE_Projectile_Stinger", ["DMSE_Incoming_Stinger", "DMSE_Missile_Stinger"], []),
    "AdvancedLight":     (1.63, "Things/Missile/Hellfire/skyFaller_incoming", ["DMSE_Incoming_AdvancedLightMissile", "DMSE_Missile_AdvancedLight"], ["Things/Missile/Hellfire/ammo"]),
    "Maverick":          (2.49 * 482 / 347, "Things/Missile/AGM65/skyFaller", ["DMSE_Incoming_Maverick", "DMSE_Missile_Maverick"], ["Things/Missile/AGM65/skyFaller_incoming", "Things/Missile/AGM65/ammo"]),
    "Light":             (2.56, "Things/Missile/Grison/skyFaller", ["DMSE_SkyFaller_LightMissile", "DMSE_Incoming_LightMissile", "DMSE_SkyFaller_Interceptor", "DMSE_Missile_Grison", "DMSE_InterceptorMissile"], ["Things/Missile/Grison/ammo"]),
    "Sparrow":           (3.66, "Things/Missile/Aim120/skyFaller", ["DMSE_Incoming_Sparrow", "DMSE_Missile_Sparrow"], ["Things/Missile/Aim120/ammo"]),
    "AdvancedCruise":    (5.56, "Things/Missile/Tomahawk/skyFaller_incoming", ["DMSE_Incoming_AdvancedCruiseMissile", "DMSE_Missile_AdvancedCruise"], ["Things/Missile/Tomahawk/ammo"]),
    "Shahed":            (3.5, "Things/Missile/Shahed/skyFaller_incoming", ["DMSE_Incoming_Shahed", "DMSE_Missile_Shahed"], ["Things/Missile/Shahed/ammo"]),
    "Loiter":            (4.0, "Things/Missile/Termite/missile_north", ["DMSE_SkyFaller_Loiter", "DMSE_Incoming_Loiter", "DMSE_Missile_Termite"], ["Things/Missile/Termite/missile_incoming", "Things/Missile/Termite/missile_fold"]),
    "LegacyInterceptor": (6.3, "Things/Missile/Rheintochter/skyFaller", ["DMSE_Incoming_LegacyInterceptor", "DMSE_Missile_Rheintochter"], ["Things/Missile/Rheintochter/skyFaller_incoming"]),
    "LegacyLoiter":      (8.3, "Things/Missile/V1/skyFaller_incoming", ["DMSE_Incoming_LegacyLoiter", "DMSE_Missile_LegacyLoiter"], ["Things/Missile/V1/ammo"]),
    "Yj":                (6.3 * 494 / 281, "Things/Missile/YJ20/skyFaller", ["DMSE_Incoming_Yj", "DMSE_Missile_Yj"], ["Things/Missile/YJ20/skyFaller_incoming"]),
    "Cruise":            (7.0, "Things/Missile/Moskit/skyFaller", ["DMSE_SkyFaller_CruiseMissile", "DMSE_Incoming_CruiseMissile"], []),
    "Moskit":            (9.7, "Things/Missile/Moskit/skyFaller", ["DMSE_Incoming_Moskit", "DMSE_Missile_Moskit"], ["Things/Missile/Moskit/skyFaller_incoming"]),
    "Tochka":            (6.4, "Things/Missile/Tochka/skyFaller_incoming", ["DMSE_Incoming_Tochka", "DMSE_Missile_Tochka"], []),
    "Ballistic":         (12.0, "Things/Missile/Moskit/skyFaller", ["DMSE_SkyFaller_BallisticMissile", "DMSE_Incoming_BallisticMissile"], []),
    "LegacyBallistic":   (14.0, "Things/Missile/V2/skyFaller_incoming", ["DMSE_Incoming_LegacyBallisticMissile"], []),
    "AdvancedBallistic": (18.2, "Things/Missile/Minuteman/skyFaller_incoming", ["DMSE_Incoming_AdvancedBallisticMissile"], []),
}
OUTLINE_REFERENCE = "LegacyLoiter"  # V1


def load(tex):
    for suffix in (".png", "_north.png"):
        p = os.path.join(TEX, tex + suffix)
        if os.path.exists(p):
            return np.asarray(Image.open(p).convert("RGBA"), np.float32) / 255.0
    raise FileNotFoundError(tex)


def length_fraction(tex):
    a = load(tex)[..., 3] > 0.5
    ys = np.where(a.any(1))[0]
    return (ys[-1] - ys[0] + 1) / a.shape[0]


def outline_px(tex):
    """Black outline width in pixels: layers in from the transparent edge whose median brightness is black."""
    a = load(tex)
    opaque = a[..., 3] > 0.5
    luma = a[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
    dist = np.zeros(opaque.shape, np.int32)
    cur, k = opaque.copy(), 0
    while cur.any() and k < 40:
        k += 1
        pad = np.pad(cur, 1)
        er = cur & pad[:-2, 1:-1] & pad[2:, 1:-1] & pad[1:-1, :-2] & pad[1:-1, 2:]
        dist[cur & ~er] = k
        cur = er
    med = [float(np.median(luma[dist == i])) for i in range(1, 30) if (dist == i).sum() > 20]
    for i, m in enumerate(med):
        if m >= 0.1:
            prev = med[i - 1] if i else 0.0
            return i + (0.1 - prev) / (m - prev)
    return float(len(med))


def screen_cells(length):
    return max(MIN_CELLS, ANCHOR_CELLS * (length / ANCHOR_LENGTH) ** POWER)


def draw_sizes():
    return {k: round(screen_cells(L) / length_fraction(tex), 2) for k, (L, tex, _, _) in MODELS.items()}


def apply(sizes):
    target = {d: sizes[k] for k, (_, _, defs, _) in MODELS.items() for d in defs if ONLY is None or k in ONLY}
    done = set()
    for path in glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True):
        with open(path, encoding="utf-8", newline="") as f:
            text = f.read()

        def fix(m):
            block = m.group(0)
            name = re.search(r"<defName>(.*?)</defName>", block)
            if not name or name.group(1) not in target:
                return block
            new = target[name.group(1)]
            g = re.search(r"<graphicData>.*?</graphicData>", block, re.S)
            graphic = g.group(0)
            old = re.search(r"<drawSize>\(?\s*([0-9.]+)", graphic)
            old_size = float(old.group(1))
            value = "%g" % new
            graphic2 = re.sub(r"<drawSize>[^<]*</drawSize>",
                              lambda _: "<drawSize>%s</drawSize>" % (value if "(" not in old.group(0) else "(%s,%s)" % (value, value)), graphic)
            # Items: keep the missile where it sat relative to its size (offset scales with the sprite).
            graphic2 = re.sub(r"<drawOffset>\(([-0-9.]+),([-0-9.]+),([-0-9.]+)\)</drawOffset>",
                              lambda o: "<drawOffset>(%s,%s,%g)</drawOffset>" % (o.group(1), o.group(2), round(float(o.group(3)) * new / old_size, 2)), graphic2)
            done.add(name.group(1))
            return block[:g.start()] + graphic2 + block[g.end():]

        # ThingDef blocks; descriptionHyperlinks contain <ThingDef> tags, so match on the def's own opening tag.
        new_text = re.sub(r'<ThingDef (?:Name|ParentName|Class)[^>]*>.*?\n[ \t]*</ThingDef>', fix, text, flags=re.S)
        if new_text != text:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(new_text)
            print("wrote", os.path.relpath(path, ROOT))
    missing = set(target) - done
    if missing:
        sys.exit("defs not found: %s" % sorted(missing))
    subprocess.check_call([sys.executable, "-I", os.path.join(ROOT, ".source", "Tools", "gen_missile_launch_defs.py"), ROOT])


def report(sizes):
    ref_L, ref_tex, _, _ = MODELS[OUTLINE_REFERENCE]
    ref_w = load(ref_tex).shape[1]
    ref_cells = outline_px(ref_tex) / ref_w * sizes[OUTLINE_REFERENCE]
    print("Outline reference: V1 %s at drawSize %.2f -> %.4f cells (%.1f px at 64 px/cell zoom)" % (ref_tex, sizes[OUTLINE_REFERENCE], ref_cells, ref_cells * 64))
    print("| model | texture | drawSize | outline now (px / tex) | in game (cells) | target px | change |")
    print("|---|---|---|---|---|---|---|")
    seen = set()
    for k, (L, tex, _, others) in MODELS.items():
        for t in [tex] + others:
            if (k, t) in seen:
                continue
            seen.add((k, t))
            w = load(t).shape[1]
            px = outline_px(t)
            cells = px / w * sizes[k]
            target = ref_cells * w / sizes[k]
            print("| %s | %s | %.2f | %.1f / %d | %.4f | %.1f | %+.1f px (%+.0f%%) |"
                  % (k, t.replace("Things/", ""), sizes[k], px, w, cells, target, target - px, (target / px - 1) * 100))


if __name__ == "__main__":
    s = draw_sizes()
    for k, v in s.items():
        print("%-18s %6.2f m  screen %.2f cells  drawSize %.2f" % (k, MODELS[k][0], screen_cells(MODELS[k][0]), v))
    if REPORT:
        report(s)
    else:
        apply(s)
