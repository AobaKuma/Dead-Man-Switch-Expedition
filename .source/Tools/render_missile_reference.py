"""Reference sheet of every missile at the drawSize currently in the defs (docs/Missile_Skyfaller轉Projectile_計畫.md §8.8).

Row 1: flight sprite of the full missile (launch texture, incl. booster) at the launch def's drawSize.
Row 2: the missile item (ground / rack) at the item def's drawSize.
Each label: real length, on-screen length in cells, outline thickness relative to V1 (100 % = same as V1).
Grid lines are 1 cell. Output: docs/review/missile_reference.png

  python -I -X utf8 .source/Tools/render_missile_reference.py [mod root]
"""
import glob
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_missile_sizes as sizes  # noqa: E402  (texture measurements and the model table)

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..")
TEX = os.path.join(ROOT, "1.7", "Textures")
sizes.TEX = TEX

# (label, model key in sizes.MODELS, launch def, item def or None, class)
ROWS = [
    ("Stinger", "Stinger", "DMSE_Launch_Stinger", "DMSE_Missile_Stinger", "Light"),
    ("Hellfire", "AdvancedLight", "DMSE_Launch_AdvancedLight", "DMSE_Missile_AdvancedLight", "Light"),
    ("AGM-65", "Maverick", "DMSE_Launch_Maverick", "DMSE_Missile_Maverick", "Light"),
    ("Grison", "Light", "DMSE_Launch_Light", "DMSE_Missile_Grison", "Light"),
    ("Sparrow", "Sparrow", "DMSE_Launch_Sparrow", "DMSE_Missile_Sparrow", "Light"),
    ("Tomahawk", "AdvancedCruise", "DMSE_Launch_AdvancedCruise", "DMSE_Missile_AdvancedCruise", "Light"),
    ("Shahed", "Shahed", "DMSE_Launch_Shahed", "DMSE_Missile_Shahed", "Loitering"),
    ("Termite", "Loiter", "DMSE_Launch_Loiter", "DMSE_Missile_Termite", "Loitering"),
    ("Rheintochter", "LegacyInterceptor", "DMSE_Launch_LegacyInterceptor", "DMSE_Missile_Rheintochter", "Loitering"),
    ("V1", "LegacyLoiter", "DMSE_Launch_LegacyLoiter", "DMSE_Missile_LegacyLoiter", "Loitering"),
    ("YJ", "Yj", "DMSE_Launch_Yj", "DMSE_Missile_Yj", "Medium"),
    ("Cruise", "Cruise", "DMSE_Launch_Cruise", None, "Medium"),
    ("Moskit", "Moskit", "DMSE_Launch_Moskit", "DMSE_Missile_Moskit", "Medium"),
    ("Tochka", "Tochka", "DMSE_Launch_Tochka", "DMSE_Missile_Tochka", "Medium"),
    ("Ballistic", "Ballistic", "DMSE_Launch_Ballistic", None, "Ballistic"),
    ("V2", "LegacyBallistic", "DMSE_Launch_LegacyBallistic", None, "Ballistic"),
    ("Minuteman", "AdvancedBallistic", "DMSE_Launch_AdvancedBallistic", None, "Ballistic"),
]
# Textures drawn but not yet referenced by any def (shown with a note).
PREVIEW_TEXTURES = {}


def def_graphics():
    """defName -> (texPath, drawSize) for every ThingDef in 1.7/Defs."""
    out = {}
    for path in glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True):
        text = open(path, encoding="utf-8").read()
        for m in re.finditer(r'<ThingDef (?:Name|ParentName|Class)[^>]*>.*?\n[ \t]*</ThingDef>', text, re.S):
            block = m.group(0)
            name = re.search(r"<defName>(.*?)</defName>", block)
            g = re.search(r"<graphicData>(.*?)</graphicData>", block, re.S)
            if not name or not g:
                continue
            tex = re.search(r"<texPath>(.*?)</texPath>", g.group(1))
            size = re.search(r"<drawSize>\(?\s*([0-9.]+)", g.group(1))
            if tex and size:
                out[name.group(1)] = (tex.group(1), float(size.group(1)))
    return out


def sprite(tex, draw_size, px_per_cell):
    for suffix in (".png", "_north.png"):
        p = os.path.join(TEX, tex + suffix)
        if os.path.exists(p):
            im = Image.open(p).convert("RGBA")
            break
    s = max(2, int(draw_size * px_per_cell))
    im = im.resize((s, s), Image.LANCZOS)
    return im.crop(im.getbbox())


def main():
    defs = def_graphics()
    PX = 48
    try:
        font = ImageFont.truetype("arial.ttf", 15)
        big = ImageFont.truetype("arial.ttf", 20)
    except OSError:
        font = big = ImageFont.load_default()

    v1_tex, v1_size = defs["DMSE_Launch_LegacyLoiter"]
    v1_outline = sizes.outline_px(v1_tex) / sizes.load(v1_tex).shape[1] * v1_size

    entries = []
    for label, model, launch, item, cls in ROWS:
        tex, size = defs[launch]
        note = ""
        if model in PREVIEW_TEXTURES and os.path.exists(os.path.join(TEX, PREVIEW_TEXTURES[model] + ".png")) and tex != PREVIEW_TEXTURES[model]:
            tex, note = PREVIEW_TEXTURES[model], "new tex, not wired"
        rows = [(tex, size)]
        rows.append(defs[item] if item else None)
        entries.append((label, model, cls, rows, note))

    colw, gap = 132, 26
    max_cells = max(e[3][0][1] * sizes.length_fraction(e[3][0][0]) for e in entries)
    rowh = int(max_cells * PX) + 116
    W = colw * len(entries) + gap * 3 + 20
    H = rowh * 2 + 10
    sheet = Image.new("RGB", (W, H), (150, 140, 120))
    d = ImageDraw.Draw(sheet)
    titles = ["FLIGHT (launch sprite, full missile incl. booster)", "ITEM (on the ground / on a rack)"]
    for r in range(2):
        y0 = r * rowh
        base = y0 + rowh - 8
        d.text((8, y0 + 2), titles[r] + "   grid = 1 cell   outline % = vs V1", fill=(60, 0, 0), font=big)
        for k in range(int(max_cells) + 2):
            d.line([(0, base - k * PX), (W, base - k * PX)], fill=(128, 120, 102))
        x, last = 10, None
        for label, model, cls, rows, note in entries:
            if cls != last:
                if last is not None:
                    x += gap
                d.text((x + 2, y0 + 28), cls, fill=(90, 0, 0), font=font)
                last = cls
            if rows[r] is None:
                d.text((x + 2, y0 + 46), label, fill=(70, 70, 70), font=font)
                d.text((x + 2, y0 + 62), "(no item)", fill=(70, 70, 70), font=font)
                x += colw
                continue
            tex, size = rows[r]
            frac = sizes.length_fraction(tex)
            outline = sizes.outline_px(tex) / sizes.load(tex).shape[1] * size / v1_outline
            im = sprite(tex, size, PX)
            sheet.paste(im, (x + (colw - im.size[0]) // 2, base - im.size[1]), im)
            d.text((x + 2, y0 + 46), label, fill=(0, 0, 0), font=font)
            d.text((x + 2, y0 + 62), "%.1fm  %.2f c" % (sizes.MODELS[model][0], size * frac), fill=(30, 30, 30), font=font)
            d.text((x + 2, y0 + 78), "outline %d%%" % round(outline * 100),
                   fill=(30, 30, 30) if abs(outline - 1) < 0.15 else (140, 20, 0), font=font)
            if note and r == 0:
                d.text((x + 2, y0 + 94), note, fill=(0, 0, 140), font=font)
            x += colw
    out = os.path.join(ROOT, "docs", "review", "missile_reference.png")
    sheet.save(out)
    print("wrote", out, sheet.size)
    for label, model, cls, rows, note in entries:
        tex, size = rows[0]
        print("%-12s flight %-48s drawSize %.2f  screen %.2f c  outline %3.0f%% %s"
              % (label, tex, size, size * sizes.length_fraction(tex),
                 100 * sizes.outline_px(tex) / sizes.load(tex).shape[1] * size / v1_outline, note))


if __name__ == "__main__":
    main()
