# -*- coding: utf-8 -*-
"""一次性：補上彈道系部件的 designWord，並把「無酬載」設為命名時略過。"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "1.7/Defs/Missile"

WORDS = {
    "DMSE_BallisticMissile.xml": [
        ("DMSE_Ballistic_Warhead_Thermonuclear", "Thermonuclear"),
        ("DMSE_Ballistic_Warhead_Airburst", "Airburst"),
        ("DMSE_Ballistic_Warhead_ClusterBomblet", "Bomblet"),
        ("DMSE_Ballistic_Warhead_TungstenPenetrator", "Tungsten"),
        ("DMSE_Ballistic_Guidance_Inertial", "Inertial"),
        ("DMSE_Ballistic_Guidance_FireControl", "FireControl"),
        ("DMSE_Ballistic_Guidance_Laser", "Laser"),
        ("DMSE_Ballistic_Payload_Cluster", "Cluster"),
        ("DMSE_Ballistic_Payload_BunkerBuster", "Bunker"),
        ("DMSE_Ballistic_Payload_Antiparticle", "Antiparticle"),
    ],
    "DMSE_MissileParts_BallisticGenerations.xml": [
        ("DMSE_AdvBallistic_Warhead_LowYield", "Low Yield"),
        ("DMSE_AdvBallistic_Guidance_Stellar", "Stellar"),
        ("DMSE_AdvBallistic_Payload_Penaids", "PenAids"),
        ("DMSE_AdvBallistic_Payload_MIRV", "MIRV"),
        ("DMSE_LegacyBallistic_Warhead_Amatol", "Amatol"),
        ("DMSE_LegacyBallistic_Warhead_Trialen", "Trialen"),
    ],
}

OMIT = {"DMSE_BallisticMissile.xml": ["DMSE_Ballistic_Payload_None"]}

for fname, entries in WORDS.items():
    path = ROOT / fname
    lines = path.read_text(encoding="utf-8-sig").split("\n")
    for def_name, word in entries:
        start = next(i for i, l in enumerate(lines) if f"<defName>{def_name}</defName>" in l)
        end = next(i for i in range(start + 1, len(lines)) if "</DMSE.MissilePartDef>" in lines[i])
        block = lines[start:end]
        if any("<designWord>" in l for l in block):
            print(f"skip {def_name}: already has designWord")
            continue
        anchor = next(i for i, l in enumerate(block) if l.strip().startswith("<label>"))
        lines[start + anchor + 1:start + anchor + 1] = [f"    <designWord>{word}</designWord>"]
        print(f"{def_name}: +designWord {word}")
    path.write_text("\n".join(lines), encoding="utf-8-sig")

for fname, names in OMIT.items():
    path = ROOT / fname
    lines = path.read_text(encoding="utf-8-sig").split("\n")
    for def_name in names:
        start = next(i for i, l in enumerate(lines) if f"<defName>{def_name}</defName>" in l)
        end = next(i for i in range(start + 1, len(lines)) if "</DMSE.MissilePartDef>" in lines[i])
        if any("<omitFromDesignName>" in l for l in lines[start:end]):
            print(f"skip {def_name}: already omitted")
            continue
        anchor = next(i for i in range(start, end) if lines[i].strip().startswith("<category>"))
        lines[anchor + 1:anchor + 1] = ["    <omitFromDesignName>true</omitFromDesignName>"]
        print(f"{def_name}: +omitFromDesignName")
    path.write_text("\n".join(lines), encoding="utf-8-sig")

sys.exit(0)
