# -*- coding: utf-8 -*-
"""
Wire the new FieldStructures textures (ammoStorage / extinguisherUnit / fieldFrame /
fieldHull / fieldSupport / powerUnit) into ThingDefs + DefInjected.

Art convention (verified from canvas sizes, all multiples of 64):
    64 px = 1 cell ; canvas = footprint + 0.5 cell margin on every side
    drawSize = canvas / 64  (south view; east/west get drawSize rotated by the engine)
"""
import io
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EOL = "\r\n"


def read(path):
    with io.open(path, "rb") as f:
        raw = f.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig" if bom else "utf-8")
    return text.replace("\r\n", "\n"), bom


def write(path, text, bom):
    data = text.replace("\n", EOL).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    with io.open(path, "wb") as f:
        f.write(data)


def block(name, label, desc, tex, draw_size, size, hp=None, steel=None):
    lines = [
        "  <ThingDef ParentName=\"DMSE_BaseStructuralFrame\">",
        "    <defName>%s</defName>" % name,
        "    <label>%s</label>" % label,
        "    <description>%s</description>" % desc,
        "    <graphicData>",
        "      <texPath>Things/Building/FieldStructures/%s</texPath>" % tex,
        "      <graphicClass>Graphic_Multi</graphicClass>",
        "      <drawSize>(%s)</drawSize>" % draw_size,
        "      <drawOffset>(0,-0.01,0)</drawOffset>",
        "    </graphicData>",
    ]
    if size is not None:
        lines.append("    <size>(%s)</size>" % size)
    if hp is not None or steel is not None:
        lines.append("    <statBases>")
        if hp is not None:
            lines.append("      <MaxHitPoints>%d</MaxHitPoints>" % hp)
        lines.append("    </statBases>")
        if steel is not None:
            lines.append("    <costList>")
            lines.append("      <Steel>%d</Steel>" % steel)
            lines.append("    </costList>")
    lines += [
        "    <comps>",
        "      <li Class=\"DMSE.CompProperties_BuildingExtraRenderer\">",
        "        <extraGraphic>",
        "          <li>",
        "            <texPath>Things/Building/FieldStructures/%s_top</texPath>" % tex,
        "            <graphicClass>Graphic_Multi</graphicClass>",
        "            <drawSize>(%s)</drawSize>" % draw_size,
        "            <drawOffset>(0,0.01,0)</drawOffset>",
        "          </li>",
        "        </extraGraphic>",
        "      </li>",
        "    </comps>",
        "  </ThingDef>",
        "",
    ]
    return lines


NEW_DEFS = [
    # defName, label, description, texPath stem, drawSize, size, HP, steel
    ("DMSE_FieldStructure_Frame", "field frame",
     "A standard frame for assembling field structures.",
     "fieldFrame", "2,2", "1,1", None, None),
    ("DMSE_FieldStructure_Support", "field support",
     "A load-bearing support for field structures.",
     "fieldSupport", "2,2", "1,1", None, None),
    ("DMSE_FieldStructure_Hull", "field hull section",
     "An armored hull section for field structures.",
     "fieldHull", "2,3", "1,2", 650, 8),
    ("DMSE_FieldStructure_PowerUnit", "field power unit",
     "A compact power unit for field structures.",
     "powerUnit", "2,3", "1,2", 650, 8),
    ("DMSE_FieldStructure_ExtinguisherUnit", "field extinguisher unit",
     "A fire-suppressant unit for field structures.",
     "extinguisherUnit", "2,3", "1,2", 650, 8),
    ("DMSE_FieldStructure_AmmoStorage", "field ammo storage",
     "A sealed ammunition locker for field structures.",
     "ammoStorage", "2,4", "1,3", 975, 12),
]

DEFS = os.path.join(ROOT, "1.7", "Defs", "ThingDefs_Buildings", "DMSE_Gen_Decoration.xml")
LANG = [
    (os.path.join(ROOT, "1.7", "Languages", "ChineseSimplified (简体中文)",
                  "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml"),
     {
         "DMSE_FieldStructure_Frame": ("野战框架", "用于拼装野战结构的标准框架。"),
         "DMSE_FieldStructure_Support": ("野战支撑架", "用于支撑野战结构的承重支架。"),
         "DMSE_FieldStructure_Hull": ("野战外壳段", "用于包覆野战结构的装甲外壳段。"),
         "DMSE_FieldStructure_PowerUnit": ("野战电力单元", "为野战结构供电的紧凑型电力单元。"),
         "DMSE_FieldStructure_ExtinguisherUnit": ("野战灭火单元", "为野战结构配备的灭火单元。"),
         "DMSE_FieldStructure_AmmoStorage": ("野战弹药库", "为野战结构储存弹药的密封弹药库。"),
     }),
    (os.path.join(ROOT, "1.7", "Languages", "ChineseTraditional (繁體中文)",
                  "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml"),
     {
         "DMSE_FieldStructure_Frame": ("野戰框架", "用於拼裝野戰結構的標準框架。"),
         "DMSE_FieldStructure_Support": ("野戰支撐架", "用於支撐野戰結構的承重支架。"),
         "DMSE_FieldStructure_Hull": ("野戰外殼段", "用於包覆野戰結構的裝甲外殼段。"),
         "DMSE_FieldStructure_PowerUnit": ("野戰電力單元", "為野戰結構供電的緊湊型電力單元。"),
         "DMSE_FieldStructure_ExtinguisherUnit": ("野戰滅火單元", "為野戰結構配備的滅火單元。"),
         "DMSE_FieldStructure_AmmoStorage": ("野戰彈藥庫", "為野戰結構儲存彈藥的密封彈藥庫。"),
     }),
]


def patch_defs():
    text, bom = read(DEFS)
    if "DMSE_FieldStructure_Frame" in text:
        print("already patched: DMSE_Gen_Decoration.xml")
        return

    # 1) drop the duplicated drawOffset node on the two medium top layers
    dup = ("            <drawSize>(3,2)</drawSize>\n"
           "            <drawOffset>(0,0,0)</drawOffset>\n"
           "            <drawOffset>(0,0.01,0)</drawOffset>\n")
    fixed = ("            <drawSize>(3,2)</drawSize>\n"
             "            <drawOffset>(0,0.01,0)</drawOffset>\n")
    assert text.count(dup) == 2, "expected 2 duplicated drawOffset nodes, got %d" % text.count(dup)
    text = text.replace(dup, fixed)

    # 2) insert the new field-structure pieces right before the lathedeco def
    anchor = ("  <ThingDef ParentName=\"DMSE_BaseDecoration\">\n"
              "    <defName>DMSE_lathedeco</defName>")
    assert text.count(anchor) == 1, "lathedeco anchor not unique"

    lines = [
        "  <!-- ==================== 野戰結構模組件（雙層貼圖）==================== -->",
        "  <!-- 貼圖：64px = 1 格；畫布 = 佔地 + 四周各 0.5 格，故 drawSize = 畫布像素 / 64 -->",
        "  <!-- 缺少的朝向（如 _west）由 Graphic_Multi 自動鏡像，不必補圖 -->",
        "",
    ]
    for args in NEW_DEFS:
        lines += block(*args)
    text = text.replace(anchor, "\n".join(lines) + anchor)

    write(DEFS, text, bom)
    print("patched", os.path.relpath(DEFS, ROOT))


def patch_lang():
    for path, entries in LANG:
        text, bom = read(path)
        if "DMSE_FieldStructure_Frame" in text:
            print("already patched:", path.encode("ascii", "replace").decode())
            continue
        # anchor on the last existing field-structure entry
        marker = "  <DMSE_FieldStructure_MediumSection.description>"
        idx = text.index(marker)
        end = text.index("\n", idx)
        tail = text[end + 1:]
        assert "</LanguageData>" in tail

        lines = [""]
        for name, (label, desc) in entries.items():
            # keep the same EN-comment convention as the rest of the file
            en_label = dict((d[0], d[1]) for d in NEW_DEFS)
            lines.append("  <!-- EN: %s -->" % en_label[name])
            lines.append("  <%s.label>%s</%s.label>" % (name, label, name))
            en_desc = dict((d[0], d[2]) for d in NEW_DEFS)[name]
            lines.append("  <!-- EN: %s -->" % en_desc)
            lines.append("  <%s.description>%s</%s.description>" % (name, desc, name))
            lines.append("")
        insert = "\n".join(lines)
        text = text[:end + 1] + insert + text[end + 1:]
        write(path, text, bom)
        print("patched", path.encode("ascii", "replace").decode())


if __name__ == "__main__":
    patch_defs()
    patch_lang()
