# -*- coding: utf-8 -*-
"""Follow-up tweaks:
  1. extinguisherUnit: vanilla one-shot popper -> manual use (CompProperties_Usable +
     Fortified.CompUseEffect_Extinguish), no longer ticks or destroys itself.
  2. powerUnit: 500 W -> 1500 W.
"""
import io
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EOL = "\r\n"
DEFS = os.path.join(ROOT, "1.7", "Defs", "ThingDefs_Buildings", "DMSE_Gen_Decoration.xml")

LANG = [
    (os.path.join(ROOT, "1.7", "Languages", "ChineseSimplified (简体中文)",
                  "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml"),
     "为野战结构供电的紧凑型电力单元，开启时输出 1500 W。",
     "为野战结构配备的灭火单元，任何殖民者都能触发，在 4.9 格半径内喷洒灭火泡沫。"),
    (os.path.join(ROOT, "1.7", "Languages", "ChineseTraditional (繁體中文)",
                  "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml"),
     "為野戰結構供電的緊湊型電力單元，開啟時輸出 1500 W。",
     "為野戰結構配備的滅火單元，任何殖民者都能觸發，在 4.9 格半徑內噴灑滅火泡沫。"),
]

EN_POWER_DESC = "A compact power unit for field structures. Supplies 1500 W while switched on."
EN_EXT_DESC = ("A fire-suppressant unit for field structures. Can be triggered by a colonist "
               "to spray extinguishing foam over a 4.9-cell radius.")

EXT_BLOCK = """  <ThingDef ParentName="DMSE_BaseStructuralFrame">
    <defName>DMSE_FieldStructure_ExtinguisherUnit</defName>
    <label>field extinguisher unit</label>
    <description>{desc}</description>
    <graphicData>
      <texPath>Things/Building/FieldStructures/extinguisherUnit</texPath>
      <graphicClass>Graphic_Multi</graphicClass>
      <drawSize>(2,3)</drawSize>
      <drawOffset>(0,-0.01,0)</drawOffset>
    </graphicData>
    <size>(1,2)</size>
    <statBases>
      <MaxHitPoints>650</MaxHitPoints>
    </statBases>
    <costList>
      <Steel>8</Steel>
    </costList>
    <comps>
      <li Class="DMSE.CompProperties_BuildingExtraRenderer">
        <extraGraphic>
          <li>
            <texPath>Things/Building/FieldStructures/extinguisherUnit_top</texPath>
            <graphicClass>Graphic_Multi</graphicClass>
            <drawSize>(2,3)</drawSize>
            <drawOffset>(0,0.01,0)</drawOffset>
          </li>
        </extraGraphic>
      </li>
      <!-- 手動滅火：小人右鍵使用 → CompUsable.UsedBy 呼叫同一個 def 上的 CompUseEffect_Extinguish
           （FFF，半徑 4.9、留下滅火泡沫），建築本身不受損、可重複使用。 -->
      <li Class="CompProperties_Usable">
        <useJob>TriggerObject</useJob>
        <useLabel>Trigger {0_label}</useLabel>
        <useDuration>90</useDuration>
      </li>
      <li Class="CompProperties_UseEffect">
        <compClass>Fortified.CompUseEffect_Extinguish</compClass>
      </li>
    </comps>
  </ThingDef>
"""


def read(path):
    raw = io.open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig" if bom else "utf-8").replace("\r\n", "\n"), bom


def write(path, text, bom):
    data = text.replace("\n", EOL).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    io.open(path, "wb").write(data)


def replace_def(text, def_name, new_block):
    marker = "<defName>%s</defName>" % def_name
    i = text.index(marker)
    start = text.rindex("  <ThingDef ", 0, i)
    end = text.index("\n  </ThingDef>\n", i) + len("\n  </ThingDef>\n")
    return text[:start] + new_block + text[end:]


def main():
    text, bom = read(DEFS)

    # power: 500 W -> 1500 W
    assert text.count("<basePowerConsumption>-500</basePowerConsumption>") == 1
    text = text.replace("<basePowerConsumption>-500</basePowerConsumption>",
                        "<basePowerConsumption>-1500</basePowerConsumption>")
    assert text.count("Supplies 500 W while switched on.") == 1
    text = text.replace("Supplies 500 W while switched on.", "Supplies 1500 W while switched on.")
    text = text.replace("basePowerConsumption 負值 = 發電量（-500 = 500 W）",
                        "basePowerConsumption 負值 = 發電量（-1500 = 1500 W）")

    # extinguisher: manual use instead of one-shot popper
    old_desc = ("A fire-suppressant unit for field structures. Detects fire nearby and bursts "
                "into extinguishing foam.")
    assert old_desc in text
    block = EXT_BLOCK.replace("{desc}", EN_EXT_DESC)
    text = replace_def(text, "DMSE_FieldStructure_ExtinguisherUnit", block)

    write(DEFS, text, bom)
    print("defs patched")

    for path, power_desc, ext_desc in LANG:
        t, b = read(path)
        assert "Supplies 500 W while switched on." in t
        t = t.replace("Supplies 500 W while switched on.", "Supplies 1500 W while switched on.")
        assert "Detects fire nearby and bursts into extinguishing foam." in t
        t = t.replace("Detects fire nearby and bursts into extinguishing foam.",
                      "Can be triggered by a colonist to spray extinguishing foam over a 4.9-cell radius.")
        # replace the zh description element bodies
        for name, new in (("DMSE_FieldStructure_PowerUnit", power_desc),
                          ("DMSE_FieldStructure_ExtinguisherUnit", ext_desc)):
            open_tag = "<%s.description>" % name
            close_tag = "</%s.description>" % name
            i = t.index(open_tag)
            j = t.index(close_tag, i)
            t = t[:i] + open_tag + new + t[j:]
        write(path, t, b)
        print("lang patched:", path.encode("ascii", "replace").decode())


if __name__ == "__main__":
    main()
