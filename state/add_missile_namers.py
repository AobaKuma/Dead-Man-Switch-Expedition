# -*- coding: utf-8 -*-
"""一次性：把 <namer>（必要時連 <designWord>）插進各彈體 Def。"""
import io, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "1.7/Defs/Missile"

# (檔案, 彈體 defName, namer defName, 要補的 designWord 或 None)
TARGETS = [
    ("DMSE_LightMissile.xml", "DMSE_MissileBody_Light", "DMSE_Namer_Light", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_LegacyBallistic", "DMSE_Namer_LegacyBallistic", "Legacy"),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_AdvancedLight", "DMSE_Namer_AdvancedLight", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_AdvancedBallistic", "DMSE_Namer_AdvancedBallistic", "Strategic"),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_AdvancedCruise", "DMSE_Namer_AdvancedCruise", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_Stinger", "DMSE_Namer_Stinger", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_Maverick", "DMSE_Namer_Maverick", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_Sparrow", "DMSE_Namer_Sparrow", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_Moskit", "DMSE_Namer_Moskit", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_Yj", "DMSE_Namer_Yj", None),
    ("DMSE_MissileGenerationBodies.xml", "DMSE_MissileBody_Tochka", "DMSE_Namer_Tochka", None),
    ("DMSE_Shahed.xml", "DMSE_MissileBody_Shahed", "DMSE_Namer_Shahed", None),
]

by_file = {}
for fname, body, namer, word in TARGETS:
    by_file.setdefault(fname, []).append((body, namer, word))

fail = 0
for fname, entries in by_file.items():
    path = ROOT / fname
    text = path.read_text(encoding="utf-8-sig")
    lines = text.split("\n")
    offset = 0
    for body, namer, word in entries:
        start = next(i for i, l in enumerate(lines) if f"<defName>{body}</defName>" in l)
        end = next(i for i in range(start + 1, len(lines)) if "</DMSE.MissileBodyDef>" in lines[i])
        block = lines[start:end]
        has_namer = any("<namer>" in l for l in block)
        has_word = any("<designWord>" in l for l in block)
        if has_namer:
            print(f"skip {body}: already has <namer>")
            continue
        if has_word:
            anchor = next(i for i, l in enumerate(block) if l.strip().startswith("<designWord>"))
        else:
            anchor = next(i for i, l in enumerate(block) if l.strip().startswith("<label>"))
        insert = []
        if word and not has_word:
            insert.append(f"    <designWord>{word}</designWord>")
        insert.append(f"    <namer>{namer}</namer>")
        lines[start + anchor + 1:start + anchor + 1] = insert
        print(f"{body}: +{len(insert)} line(s) after index {start + anchor}")
    path.write_text("\n".join(lines), encoding="utf-8-sig")

sys.exit(1 if fail else 0)
