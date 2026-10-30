# -*- coding: utf-8 -*-
"""Validate the new FieldStructure defs against the actual texture files."""
import glob
import io
import os
import sys
import xml.etree.ElementTree as ET

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEX = os.path.join(ROOT, "1.7", "Textures")
DEFS = os.path.join(ROOT, "1.7", "Defs", "ThingDefs_Buildings", "DMSE_Gen_Decoration.xml")
LANGDIR = os.path.join(ROOT, "1.7", "Languages")
PREFIX = "DMSE_FieldStructure_"
NEW = ["Frame", "Support", "Hull", "PowerUnit", "ExtinguisherUnit", "AmmoStorage"]

errors = []


def tex_path_to_file(tex_path):
    """RimWorld resolves Things/Building/X -> Textures/Things/Building/X.png (or folder variant)."""
    base = os.path.join(TEX, *tex_path.split("/"))
    if os.path.isfile(base + ".png"):
        return base + ".png"
    return None


# ---- 1. well-formed XML ------------------------------------------------------
for p in [DEFS] + glob.glob(os.path.join(LANGDIR, "*", "DefInjected", "ThingDef",
                                         "DMSE_Gen_Decoration.xml")):
    try:
        ET.parse(p)
    except Exception as e:  # noqa: BLE001
        errors.append("XML parse failed %s: %s" % (p, e))

tree = ET.parse(DEFS)
root = tree.getroot()

# ---- 2. unique defNames across the whole 1.7 Defs tree ----------------------
all_names = {}
for p in glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True):
    try:
        t = ET.parse(p)
    except Exception:
        continue
    for el in t.getroot().iter():
        n = el.find("defName")
        if n is not None and (n.text or "").strip():
            all_names.setdefault(n.text.strip(), []).append(os.path.relpath(p, ROOT))
for name, files in all_names.items():
    if len(files) > 1 and (name.startswith(PREFIX) or name.startswith("DMSE_StructuralFrame")
                           or name.startswith("DMSE_BaseStructuralFrame")):
        errors.append("duplicate defName %s in %s" % (name, files))

# ---- 3. new defs: textures + drawSize --------------------------------------
print("%-38s %-10s %-10s %-12s %s" % ("defName", "size", "drawSize", "canvas/64", "directions"))
for d in root.findall("ThingDef"):
    dn = d.findtext("defName")
    if not dn or not dn.startswith(PREFIX):
        continue
    gd = d.find("graphicData")
    tex = gd.findtext("texPath")
    draw_size = gd.findtext("drawSize")
    size = d.findtext("size")
    ds = [int(v) for v in draw_size.strip("()").split(",")]

    stem = os.path.basename(tex)
    folder = os.path.dirname(tex)
    present = []
    for suf in ("south", "north", "east", "west"):
        if tex_path_to_file(os.path.join(folder, "%s_%s" % (stem, suf))):
            present.append(suf)
    if not present:
        errors.append("%s: no directional texture found for %s" % (dn, tex))
    png = tex_path_to_file(os.path.join(folder, "%s_%s" % (stem, present[0])))
    canvas = Image.open(png).size
    cells = (canvas[0] / 64.0, canvas[1] / 64.0)

    # for the south/north view the drawSize must equal the canvas in cells
    if ("south" in present or "north" in present) and ds != [int(cells[0]), int(cells[1])]:
        errors.append("%s: drawSize %s != canvas %s cells" % (dn, ds, cells))

    # top layer must exist and use the same drawSize
    comp = d.find("comps/li/extraGraphic/li")
    ttop = comp.findtext("texPath") if comp is not None else None
    if not ttop or not all(tex_path_to_file(os.path.join(os.path.dirname(ttop),
                                                         "%s_%s" % (os.path.basename(ttop), s)))
                           for s in present):
        errors.append("%s: top layer missing matching direction(s) %s" % (dn, present))
    elif comp.findtext("drawSize").strip("()") != draw_size.strip("()"):
        errors.append("%s: top drawSize %s != %s" % (dn, comp.findtext("drawSize"), draw_size))

    print("%-38s %-10s %-10s %-12s %s" % (dn, size, draw_size, "%dx%d" % cells, ",".join(present)))

    # footprint must be canvas - 1 cell on each axis
    fw, fh = [int(v) for v in size.strip("()").split(",")]
    if (fw, fh) != (int(cells[0]) - 1, int(cells[1]) - 1):
        errors.append("%s: size %s vs canvas %s" % (dn, size, cells))

# ---- 4. every FieldStructures png is referenced -----------------------------
DIRS = ("_south", "_north", "_east", "_west")
referenced = set()
defs_txt = "".join(io.open(p, encoding="utf-8").read() for p in
                   glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True))
all_pngs = glob.glob(os.path.join(TEX, "Things", "Building", "FieldStructures", "*.png"))
for x in all_pngs:
    stem = os.path.basename(x)[:-4]
    base = stem
    for d in DIRS:
        if stem.endswith(d):
            base = stem[: -len(d)]
            break
    if "FieldStructures/" + base not in defs_txt:
        referenced.add(stem)
orphans = sorted(referenced)
if orphans:
    errors.append("unreferenced textures: %s" % orphans)

# ---- 5. DefInjected coverage ------------------------------------------------
for lang in sorted(os.listdir(LANGDIR)):
    if lang.lower().startswith("english"):
        continue  # English lives in the Defs themselves
    p = os.path.join(LANGDIR, lang, "DefInjected", "ThingDef", "DMSE_Gen_Decoration.xml")
    if not os.path.isfile(p):
        errors.append("missing DefInjected for %s" % lang)
        continue
    txt = io.open(p, encoding="utf-8-sig").read()
    for name in NEW:
        dn = PREFIX + name
        if "<%s.label>" % dn not in txt or "<%s.description>" % dn not in txt:
            errors.append("%s: %s.label/description missing" % (lang, dn))


# ---- 6. behaviour wiring checks (added with the comp pass) -------------------
print("\n[wiring]")
CORE = r"D:\Steam\steamapps\common\RimWorld\Data"
cmp_root = ET.parse(DEFS).getroot()

# 6a. comps that tick must not sit on a Never-tick building
for d in cmp_root.findall("ThingDef"):
    dn = d.findtext("defName") or ""
    if not dn.startswith(PREFIX):
        continue
    classes = [li.get("Class") for li in d.findall("comps/li")]
    ticker = d.findtext("tickerType")
    needs_tick = [c for c in classes if c in ("CompProperties_ProximityFuse", "CompProperties_Explosive",
                                              "Fortified.CompProperties_SubstationPower")]
    print("%-38s ticker=%-7s comps=%s" % (dn, ticker, classes))
    if needs_tick and ticker != "Normal":
        errors.append("%s: %s needs tickerType Normal (has %s)" % (dn, needs_tick, ticker))
    if "CompProperties_ProximityFuse" in classes and "CompProperties_Explosive" not in classes:
        errors.append("%s: ProximityFuse without CompExplosive -> NRE" % dn)
    if "CompProperties_Usable" in classes:
        usable = d.find("comps/li[@Class='CompProperties_Usable']")
        if usable is None or usable.find("useJob") is None:
            errors.append("%s: CompProperties_Usable without useJob is inert" % dn)
        if "CompProperties_UseEffect" not in classes:
            errors.append("%s: CompProperties_Usable without CompUseEffect does nothing" % dn)
        else:
            ue = d.find("comps/li[@Class='CompProperties_UseEffect']")
            if ue is None or not (ue.findtext("compClass") or "").strip():
                errors.append("%s: CompProperties_UseEffect without compClass" % dn)

# 6b. def references used by the new defs must exist (mod defs or core)
need = set()
for d in cmp_root.findall("ThingDef"):
    if not (d.findtext("defName") or "").startswith(PREFIX):
        continue
    for li in d.iter():
        if li.tag == "li" and li.text and li.text.strip() and " " not in li.text.strip():
            pass
    for td in d.iter("thingDefs"):
        for li in td.findall("li"):
            need.add(li.text.strip())
    for cand in d.iter("candidates"):
        for li in cand.findall("li"):
            need.add(li.text.strip())
    for tag in ("target", "postExplosionSpawnThingDef"):
        v = d.find("comps/li/" + tag)
        if v is None:
            for li in d.findall("comps/li"):
                vv = li.find(tag)
                if vv is not None and vv.text:
                    need.add(vv.text.strip())
        elif v.text:
            need.add(v.text.strip())

mod_defs = ""
for p in glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True):
    mod_defs += io.open(p, encoding="utf-8").read()


def core_has(name):
    for p in glob.glob(os.path.join(CORE, "**", "*.xml"), recursive=True):
        try:
            if ("<defName>%s</defName>" % name) in io.open(p, encoding="utf-8", errors="ignore").read():
                return True
        except OSError:
            continue
    return False


for name in sorted(need):
    ok = ("<defName>%s</defName>" % name) in mod_defs or core_has(name)
    print("  ref %-28s %s" % (name, "ok" if ok else "MISSING"))
    if not ok:
        errors.append("missing def reference: %s" % name)

print()
if errors:
    print("FAILED (%d)" % len(errors))
    for e in errors:
        print(" -", e)
    sys.exit(1)
print("ALL CHECKS PASSED: XML parses, defNames unique, textures/drawSize/size consistent, "
      "language entries present, comps wired with correct tickers and existing def refs.")
