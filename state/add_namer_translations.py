# -*- coding: utf-8 -*-
"""一次性：補上各彈體 Namer 的翻譯（代字、樣板、designWord）。"""
import io
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "1.7/Languages"
TW = ROOT / "ChineseTraditional (繁體中文)/DefInjected"
CN = ROOT / "ChineseSimplified (简体中文)/DefInjected"

DEAD = "DMSE.MissileBodyDef/DMSE_DesignWords.xml"
PART = "DMSE.MissilePartDef/DMSE_DesignWords.xml"

BODY_WORDS = {
    TW: [("DMSE_MissileBody_LegacyBallistic", "老式"), ("DMSE_MissileBody_AdvancedBallistic", "戰略")],
    CN: [("DMSE_MissileBody_LegacyBallistic", "老式"), ("DMSE_MissileBody_AdvancedBallistic", "战略")],
}

PART_WORDS = {
    TW: [
        ("DMSE_Ballistic_Warhead_Thermonuclear", "熱核"),
        ("DMSE_Ballistic_Warhead_Airburst", "空爆"),
        ("DMSE_Ballistic_Warhead_ClusterBomblet", "子母"),
        ("DMSE_Ballistic_Warhead_TungstenPenetrator", "鎢芯"),
        ("DMSE_Ballistic_Guidance_Inertial", "慣性"),
        ("DMSE_Ballistic_Guidance_FireControl", "火控"),
        ("DMSE_Ballistic_Guidance_Laser", "雷射"),
        ("DMSE_Ballistic_Payload_Cluster", "集束"),
        ("DMSE_Ballistic_Payload_BunkerBuster", "反碉堡"),
        ("DMSE_Ballistic_Payload_Antiparticle", "反物質"),
        ("DMSE_AdvBallistic_Warhead_LowYield", "低當量"),
        ("DMSE_AdvBallistic_Guidance_Stellar", "星光"),
        ("DMSE_AdvBallistic_Payload_Penaids", "突防"),
        ("DMSE_AdvBallistic_Payload_MIRV", "多彈頭"),
        ("DMSE_LegacyBallistic_Warhead_Amatol", "阿馬托"),
        ("DMSE_LegacyBallistic_Warhead_Trialen", "特里亞倫"),
    ],
    CN: [
        ("DMSE_Ballistic_Warhead_Thermonuclear", "热核"),
        ("DMSE_Ballistic_Warhead_Airburst", "空爆"),
        ("DMSE_Ballistic_Warhead_ClusterBomblet", "子母"),
        ("DMSE_Ballistic_Warhead_TungstenPenetrator", "钨芯"),
        ("DMSE_Ballistic_Guidance_Inertial", "惯性"),
        ("DMSE_Ballistic_Guidance_FireControl", "火控"),
        ("DMSE_Ballistic_Guidance_Laser", "激光"),
        ("DMSE_Ballistic_Payload_Cluster", "集束"),
        ("DMSE_Ballistic_Payload_BunkerBuster", "反碉堡"),
        ("DMSE_Ballistic_Payload_Antiparticle", "反物质"),
        ("DMSE_AdvBallistic_Warhead_LowYield", "低当量"),
        ("DMSE_AdvBallistic_Guidance_Stellar", "星光"),
        ("DMSE_AdvBallistic_Payload_Penaids", "突防"),
        ("DMSE_AdvBallistic_Payload_MIRV", "多弹头"),
        ("DMSE_LegacyBallistic_Warhead_Amatol", "阿马托"),
        ("DMSE_LegacyBallistic_Warhead_Trialen", "特里亚伦"),
    ],
}

DESIGNATORS = {
    TW: [
        ("DMSE_Namer_Light", "長槍"),
        ("DMSE_Namer_AdvancedLight", "地獄火"),
        ("DMSE_Namer_AdvancedCruise", "戰斧"),
        ("DMSE_Namer_Stinger", "刺針"),
        ("DMSE_Namer_Maverick", "小牛"),
        ("DMSE_Namer_Sparrow", "麻雀"),
        ("DMSE_Namer_Moskit", "白蛉"),
        ("DMSE_Namer_Yj", "鷹擊"),
        ("DMSE_Namer_Tochka", "圓點"),
        ("DMSE_Namer_Shahed", "沙赫德"),
        ("DMSE_Namer_LegacyBallistic", "V2"),
        ("DMSE_Namer_AdvancedBallistic", "民兵"),
    ],
    CN: [
        ("DMSE_Namer_Light", "长枪"),
        ("DMSE_Namer_AdvancedLight", "地狱火"),
        ("DMSE_Namer_AdvancedCruise", "战斧"),
        ("DMSE_Namer_Stinger", "刺针"),
        ("DMSE_Namer_Maverick", "小牛"),
        ("DMSE_Namer_Sparrow", "麻雀"),
        ("DMSE_Namer_Moskit", "白蛉"),
        ("DMSE_Namer_Yj", "鹰击"),
        ("DMSE_Namer_Tochka", "圆点"),
        ("DMSE_Namer_Shahed", "沙赫德"),
        ("DMSE_Namer_LegacyBallistic", "V2"),
        ("DMSE_Namer_AdvancedBallistic", "民兵"),
    ],
}

# 型號樣板（中文）：A 四槽、B 三槽、C 輕型防空、D 巡飛彈。
PACKS_ZH = {
    "A": [
        "r_name(p=3)->[designator]-[num]·[warhead][guidance]",
        "r_name(p=2)->[designator]-[num][suffix]·[warhead]",
        "r_name(p=2)->[designator]-[num]·[guidance][propulsion]",
        "r_name(p=1)->[body]·[designator]-[num]",
        "r_name(p=1)->[designator]-[num]·[payload]",
    ],
    "B": [
        "r_name(p=3)->[designator]-[num]·[warhead][guidance]",
        "r_name(p=2)->[designator]-[num][suffix]·[warhead]",
        "r_name(p=2)->[body]·[designator]-[num]",
        "r_name(p=1)->[designator]-[num]·[guidance]",
    ],
    "C": [
        "r_name(p=3)->[designator]-[num]·[warhead][guidance]",
        "r_name(p=2)->[body]·[designator]-[num]",
        "r_name(p=1)->[designator]-[num]·[guidance]",
        "r_name(p=1)->[designator]-[num]",
    ],
    "D": [
        "r_name(p=3)->[designator]-[num]·[guidance]",
        "r_name(p=2)->[body]·[designator]-[num]",
        "r_name(p=1)->[designator]-[num]·[payload]",
        "r_name(p=1)->[designator]-[num]",
    ],
}

PACK_OF_BODY = {
    "Yj": "A", "Moskit": "A", "Tochka": "A", "AdvancedCruise": "A",
    "Light": "B", "AdvancedLight": "B", "LegacyBallistic": "B",
    "AdvancedBallistic": "B", "Maverick": "B", "Sparrow": "B",
    "Stinger": "C", "Shahed": "D",
}

GUI_ZH = {"A": "四槽彈體", "B": "戰頭＋導引＋酬載", "C": "戰頭＋導引（輕型防空）", "D": "導引＋酬載（巡飛彈）"}


def insert_before_close(path, lines):
    text = path.read_text(encoding="utf-8-sig")
    if "</LanguageData>" not in text:
        raise SystemExit(f"no </LanguageData> in {path}")
    payload = "\n".join(lines)
    text = text.replace("</LanguageData>", payload + "\n</LanguageData>")
    path.write_text(text, encoding="utf-8-sig")


for base in (TW, CN):
    insert_before_close(base / DEAD, [f"  <{n}.designWord>{w}</{n}.designWord>" for n, w in BODY_WORDS[base]])
    insert_before_close(base / PART, [f"  <{n}.designWord>{w}</{n}.designWord>" for n, w in PART_WORDS[base]])

    # ---- 設計名稱代字 ----
    outdir = base / "DMSE.MissileNamerDef"
    outdir.mkdir(exist_ok=True)
    tag = "繁體" if base == TW else "簡體"
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        "<LanguageData>",
        f"  <!-- 各彈體設計名稱的型號代字（DMSE.MissileNamerDef.designator，{tag}）。 -->",
    ]
    for name, word in DESIGNATORS[base]:
        lines.append(f"  <{name}.designator>{word}</{name}.designator>")
    lines += ["</LanguageData>", ""]
    (outdir / "DMSE_MissileNamers.xml").write_text("\n".join(lines), encoding="utf-8-sig")

    # ---- 型號樣板 ----
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        "<LanguageData>",
        "",
        "  <!-- 通用樣板（沒有綁 Namer 的彈體）。 -->",
        "  <DMSE_NamerMissileDesign.rulePack.rulesStrings>",
    ]
    for rule in [
        "r_name(p=3)->[body]·[warhead]",
        "r_name(p=2)->[warhead][guidance][body]",
        "r_name->[guidance][body]",
        "r_name->[body]·[warhead][payload]",
        "r_name->[guidance][propulsion][body]",
    ]:
        lines.append(f"    <li>{rule}</li>")
    lines.append("  </DMSE_NamerMissileDesign.rulePack.rulesStrings>")
    for body, key in PACK_OF_BODY.items():
        lines.append("")
        lines.append(f"  <!-- {body}：{GUI_ZH[key]} -->")
        lines.append(f"  <DMSE_NamerPack_{body}.rulePack.rulesStrings>")
        for rule in PACKS_ZH[key]:
            lines.append(f"    <li>{rule}</li>")
        lines.append(f"  </DMSE_NamerPack_{body}.rulePack.rulesStrings>")
    lines += ["", "</LanguageData>", ""]
    (base / "RulePackDef/DMSE_Namer_MissileDesign.xml").write_text("\n".join(lines), encoding="utf-8-sig")

print("translations written")
