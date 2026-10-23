import os, re
import xml.etree.ElementTree as ET

ROOT = r"D:/Steam/steamapps/common/RimWorld/Mods/_Dead-Man-Switch-Expedition"
CE   = r"D:/Steam/steamapps/workshop/content/294100/2890901044"
DMS  = r"D:/Steam/steamapps/common/RimWorld/Mods/Dead-Man-Switch"
CORE = r"D:/Steam/steamapps/common/RimWorld/Data/Core"

AMMO = os.path.join(ROOT, "1.7/CE/Defs/Ammo/DMSE_37x250mmFlak.xml")
PATCH = os.path.join(ROOT, "1.7/CE/Patches/Buildings/GenerationTurrets_CE.xml")
TURRETS = os.path.join(ROOT, "1.7/Defs/ThingDefs_Buildings/DMSE_GenerationTurrets.xml")


def collect(*dirs):
    names = set()
    for d in dirs:
        for dp, _, fns in os.walk(d):
            for fn in fns:
                if not fn.endswith(".xml"):
                    continue
                try:
                    data = open(os.path.join(dp, fn), "r", encoding="utf-8-sig").read()
                except Exception:
                    continue
                for m in re.finditer(r"<defName>([^<]+)</defName>", data):
                    names.add(m.group(1).strip())
    return names


known = collect(os.path.join(ROOT, "1.7"), CE, DMS, CORE)
print("known defNames:", len(known))

mine = ET.parse(AMMO).getroot()
own = {d.text.strip() for d in mine.iter("defName")}
refs = set()
for ammo in mine.iter("ammoTypes"):
    for child in ammo:
        refs.add(child.tag)                 # ammo item
        if child.text:
            refs.add(child.text.strip())    # projectile
for tag in ("cookOffProjectile", "similarTo", "parent", "thingDefs", "products", "li"):
    for e in mine.iter(tag):
        if e.text and e.text.strip():
            refs.add(e.text.strip())
        for c in e:
            refs.add(c.tag)
            if c.text and c.text.strip():
                refs.add(c.text.strip())
refs -= own
missing = sorted(r for r in refs if r not in known)
print("referenced:", len(refs), "missing:", missing if missing else "none")

print("\n-- ammo set wiring --")
for ammo in mine.iter("ammoTypes"):
    for child in ammo:
        print("  ", child.tag, "->", child.text.strip(),
              "" if (child.tag in own and child.text.strip() in own) else "  <-- CHECK")

print("\n-- patch ops --")
patch = ET.parse(PATCH).getroot()
by_name = {}
for td in ET.parse(TURRETS).getroot().iter("ThingDef"):
    dn = td.find("defName")
    if dn is not None:
        by_name[dn.text.strip()] = td
for op in patch.iter("Operation"):
    if op.get("Class", "").endswith("PatchOperationSequence"):
        continue
    xp = op.find("xpath")
    dn = op.find("defName")
    label = dn.text.strip() if dn is not None else (" ".join(xp.text.split()) if xp is not None else "?")
    if dn is not None:
        present = dn.text.strip() in by_name
    else:
        present = all(m in by_name for m in re.findall(r'defName="([^"]+)"', xp.text))
    print("  ", "OK " if present else "MISSING", op.get("Class"), "|", label)
