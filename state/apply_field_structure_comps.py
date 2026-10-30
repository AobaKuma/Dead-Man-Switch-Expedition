# -*- coding: utf-8 -*-
"""Give the three FieldStructure pieces real behaviour with existing comps.

powerUnit        -> Fortified.CompProperties_SubstationPower (FFF generator, negative W = output)
ammoStorage      -> DMSE.CompProperties_MissileRackStorage + MissileRackExtension + auto-fill
extinguisherUnit -> vanilla CompProperties_ProximityFuse + CompProperties_Explosive (Extinguish)

Only whole <ThingDef> blocks are swapped, so formatting/line endings stay intact.
"""
import io
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EOL = "\r\n"
DEFS = os.path.join(ROOT, "1.7", "Defs", "ThingDefs_Buildings", "DMSE_Gen_Decoration.xml")

MISSILES = ["DMSE_Missile_Grison", "DMSE_Missile_Moskit", "DMSE_Missile_Stinger",
            "DMSE_Missile_Maverick", "DMSE_Missile_Sparrow", "DMSE_Missile_Yj",
            "DMSE_Missile_Tochka", "DMSE_Missile_AdvancedLight",
            "DMSE_Missile_AdvancedCruise", "DMSE_Missile_Shahed"]


def read(path):
    raw = io.open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig" if bom else "utf-8").replace("\r\n", "\n"), bom


def write(path, text, bom):
    data = text.replace("\n", EOL).encode("utf-8")
    if bom:
        data = b"\xef\xbb\xbf" + data
    io.open(path, "wb").write(data)


def top_layer(stem, draw_size):
    return [
        "    <comps>",
        "      <li Class=\"DMSE.CompProperties_BuildingExtraRenderer\">",
        "        <extraGraphic>",
        "          <li>",
        "            <texPath>Things/Building/FieldStructures/%s_top</texPath>" % stem,
        "            <graphicClass>Graphic_Multi</graphicClass>",
        "            <drawSize>(%s)</drawSize>" % draw_size,
        "            <drawOffset>(0,0.01,0)</drawOffset>",
        "          </li>",
        "        </extraGraphic>",
        "      </li>",
    ]


def graphic(stem, draw_size):
    return [
        "    <graphicData>",
        "      <texPath>Things/Building/FieldStructures/%s</texPath>" % stem,
        "      <graphicClass>Graphic_Multi</graphicClass>",
        "      <drawSize>(%s)</drawSize>" % draw_size,
        "      <drawOffset>(0,-0.01,0)</drawOffset>",
        "    </graphicData>",
    ]


POWER = "\n".join(
    ["  <ThingDef ParentName=\"DMSE_BaseStructuralFrame\">",
     "    <defName>DMSE_FieldStructure_PowerUnit</defName>",
     "    <label>field power unit</label>",
     "    <description>A compact power unit for field structures. Supplies 500 W while switched on.</description>"]
    + graphic("powerUnit", "2,3")
    + ["    <size>(1,2)</size>",
       "    <tickerType>Normal</tickerType>",
       "    <statBases>",
       "      <MaxHitPoints>650</MaxHitPoints>",
       "    </statBases>",
       "    <costList>",
       "      <Steel>8</Steel>",
       "    </costList>"]
    + top_layer("powerUnit", "2,3")
    + ["      <!-- FFF 發電 comp：basePowerConsumption 負值 = 發電量（-500 = 500 W）。 -->",
       "      <li Class=\"Fortified.CompProperties_SubstationPower\">",
       "        <basePowerConsumption>-500</basePowerConsumption>",
       "      </li>",
       "    </comps>",
       "  </ThingDef>"]) + "\n"

EXTINGUISHER = "\n".join(
    ["  <ThingDef ParentName=\"DMSE_BaseStructuralFrame\">",
     "    <defName>DMSE_FieldStructure_ExtinguisherUnit</defName>",
     "    <label>field extinguisher unit</label>",
     "    <description>A fire-suppressant unit for field structures. Detects fire nearby and bursts into extinguishing foam.</description>"]
    + graphic("extinguisherUnit", "2,3")
    + ["    <size>(1,2)</size>",
       "    <tickerType>Normal</tickerType>",
       "    <statBases>",
       "      <MaxHitPoints>650</MaxHitPoints>",
       "    </statBases>",
       "    <costList>",
       "      <Steel>8</Steel>",
       "    </costList>"]
    + top_layer("extinguisherUnit", "2,3")
    + ["      <!-- 原版滅火器建築的兩件套：偵測到火就點引信，炸開時以 Extinguish 傷害滅火並留下滅火泡沫。 -->",
       "      <li Class=\"CompProperties_ProximityFuse\">",
       "        <target>Fire</target>",
       "        <radius>3</radius>",
       "      </li>",
       "      <li Class=\"CompProperties_Explosive\">",
       "        <explosiveRadius>4.9</explosiveRadius>",
       "        <explosiveDamageType>Extinguish</explosiveDamageType>",
       "        <startWickHitPointsPercent>-1</startWickHitPointsPercent>",
       "        <startWickOnDamageTaken>",
       "          <li>Flame</li>",
       "        </startWickOnDamageTaken>",
       "        <postExplosionSpawnThingDef>Filth_FireFoam</postExplosionSpawnThingDef>",
       "        <postExplosionSpawnChance>1</postExplosionSpawnChance>",
       "        <postExplosionSpawnThingCount>1</postExplosionSpawnThingCount>",
       "        <applyDamageToExplosionCellsNeighbors>true</applyDamageToExplosionCellsNeighbors>",
       "        <explosionEffect>ExtinguisherExplosion</explosionEffect>",
       "        <explosionSound>Explosion_FirefoamPopper</explosionSound>",
       "      </li>",
       "    </comps>",
       "  </ThingDef>"]) + "\n"

AMMO = "\n".join(
    ["  <ThingDef ParentName=\"DMSE_BaseStructuralFrame\">",
     "    <defName>DMSE_FieldStructure_AmmoStorage</defName>",
     "    <label>field ammo storage</label>",
     "    <description>A sealed ammunition locker for field structures. Holds up to eight missiles.</description>"]
    + graphic("ammoStorage", "2,4")
    + ["    <size>(1,3)</size>",
       "    <statBases>",
       "      <MaxHitPoints>975</MaxHitPoints>",
       "    </statBases>",
       "    <costList>",
       "      <Steel>12</Steel>",
       "    </costList>",
       "    <building>",
       "      <ignoreStoredThingsBeauty>true</ignoreStoredThingsBeauty>",
       "      <!-- 只收實體導彈（與組裝台的出廠暫存同一份清單）。 -->",
       "      <fixedStorageSettings>",
       "        <filter>",
       "          <thingDefs>"]
    + ["            <li>%s</li>" % m for m in MISSILES]
    + ["          </thingDefs>",
       "        </filter>",
       "      </fixedStorageSettings>",
       "      <defaultStorageSettings>",
       "        <priority>Normal</priority>",
       "        <filter>",
       "          <thingDefs>"]
    + ["            <li>%s</li>" % m for m in MISSILES]
    + ["          </thingDefs>",
       "        </filter>",
       "      </defaultStorageSettings>",
       "    </building>",
       "    <!-- 容量 8 發；敵方站點生成時自動裝填（攔截彈），可當 NPC 導彈基地的彈藥庫。 -->",
       "    <modExtensions>",
       "      <li Class=\"DMSE.MissileRackExtension\">",
       "        <capacity>8</capacity>",
       "        <renderStoredBody>false</renderStoredBody>",
       "      </li>",
       "      <li Class=\"DMSE.MissileRackAutoFillExtension\">",
       "        <enemyFactionOnly>true</enemyFactionOnly>",
       "        <fillRules>",
       "          <li>",
       "            <minTechLevel>Industrial</minTechLevel>",
       "            <candidates>",
       "              <li>DMSE_InterceptorMissile</li>",
       "            </candidates>",
       "          </li>",
       "        </fillRules>",
       "      </li>",
       "    </modExtensions>",
       "    <inspectorTabs>",
       "      <li>RimWorld.ITab_Storage</li>",
       "      <li>DMSE.ITab_MissileInventory</li>",
       "    </inspectorTabs>"]
    + top_layer("ammoStorage", "2,4")
    + ["      <li Class=\"DMSE.CompProperties_MissileRackStorage\" />",
       "    </comps>",
       "  </ThingDef>"]) + "\n"


def replace_def(text, def_name, new_block):
    marker = "<defName>%s</defName>" % def_name
    i = text.index(marker)
    start = text.rindex("  <ThingDef ", 0, i)
    end = text.index("\n  </ThingDef>\n", i) + len("\n  </ThingDef>\n")
    return text[:start] + new_block + text[end:]


LANG_FILES = [
    (os.path.join(ROOT, "1.7", "Languages", "ChineseSimplified (简体中文)",
                  "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml"),
     {"DMSE_FieldStructure_PowerUnit": "为野战结构供电的紧凑型电力单元，开启时输出 500 W。",
      "DMSE_FieldStructure_ExtinguisherUnit": "为野战结构配备的灭火单元，侦测到附近失火时会喷出灭火泡沫。",
      "DMSE_FieldStructure_AmmoStorage": "为野战结构储存导弹的密封弹药库，可存放 8 枚导弹。"}),
    (os.path.join(ROOT, "1.7", "Languages", "ChineseTraditional (繁體中文)",
                  "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml"),
     {"DMSE_FieldStructure_PowerUnit": "為野戰結構供電的緊湊型電力單元，開啟時輸出 500 W。",
      "DMSE_FieldStructure_ExtinguisherUnit": "為野戰結構配備的滅火單元，偵測到附近失火時會噴出滅火泡沫。",
      "DMSE_FieldStructure_AmmoStorage": "為野戰結構儲存導彈的密封彈藥庫，可存放 8 枚導彈。"}),
]

NEW_EN = {
    "DMSE_FieldStructure_PowerUnit":
        "A compact power unit for field structures. Supplies 500 W while switched on.",
    "DMSE_FieldStructure_ExtinguisherUnit":
        "A fire-suppressant unit for field structures. Detects fire nearby and bursts into extinguishing foam.",
    "DMSE_FieldStructure_AmmoStorage":
        "A sealed ammunition locker for field structures. Holds up to eight missiles.",
}


def patch_lang():
    for path, entries in LANG_FILES:
        text, bom = read(path)
        for name, desc in entries.items():
            open_tag = "<%s.description>" % name
            close_tag = "</%s.description>" % name
            i = text.index(open_tag)
            line_start = text.rindex("\n", 0, i) + 1          # start of the description line
            line_end = text.index("\n", i) + 1                # just past the description line
            prev_start = text.rindex("\n", 0, line_start - 1) + 1   # start of the EN comment line
            prev = text[prev_start:line_start]
            if not prev.strip().startswith("<!-- EN:"):
                raise SystemExit("comment line missing above %s in %s" % (name, path))
            new_lines = ("  <!-- EN: " + NEW_EN[name] + " -->\n"
                         + "  <%s>%s</%s>\n" % (open_tag.strip("<>"), desc, close_tag.strip("</>")))
            text = text[:prev_start] + new_lines + text[line_end:]
        write(path, text, bom)
        print("lang patched:", path.encode("ascii", "replace").decode())


if __name__ == "__main__":
    text, bom = read(DEFS)
    if "Fortified.CompProperties_SubstationPower" in text:
        print("already patched")
        raise SystemExit(0)
    for name, block in (("DMSE_FieldStructure_PowerUnit", POWER),
                        ("DMSE_FieldStructure_ExtinguisherUnit", EXTINGUISHER),
                        ("DMSE_FieldStructure_AmmoStorage", AMMO)):
        text = replace_def(text, name, block)
    write(DEFS, text, bom)
    print("defs patched")
    patch_lang()
