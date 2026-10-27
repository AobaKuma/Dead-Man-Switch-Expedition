"""Recipe materials by equipment generation (docs/三代裝備體系_實作計畫.md §0, 2026-10-27).

Legacy (T0):   vanilla steel + industrial components.
Middle (T1):   DMS tungsten steel (Tungsteel) + stamped components (ComponentStamped).
Advanced (T2): Expedition tantasten alloy + graphene + advanced components (ComponentSpacer).
Chemfuel stays as propellant. Each recipe keeps its market value: everything that is not chemfuel is split into
"structure" (metals, cloth) and "electronics" (components) and re-expressed in the tier's materials. A missile's
default parts are bundled into this (their materials used to be listed separately; the content checker no longer
requires them).

  python -I -X utf8 .source/Tools/tier_missile_costs.py [mod root]          print the table
  python -I -X utf8 .source/Tools/tier_missile_costs.py [mod root] --apply  write the costLists
"""
import glob
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else os.path.join(os.path.dirname(__file__), "..", "..")
APPLY = "--apply" in sys.argv

VALUE = {"Steel": 1.9, "Chemfuel": 2.3, "Cloth": 1.5, "Plasteel": 9, "ComponentIndustrial": 32, "ComponentSpacer": 200,
         "ComponentStamped": 60, "Tungsteel": 8, "DMSE_TantastenAlloy": 30, "DMSE_Graphene": 10, "DMSE_Nitrogallium": 75}
ELECTRONICS = {"ComponentIndustrial", "ComponentSpacer", "ComponentStamped"}

TIERS = {
    "T0": ["DMSE_Missile_LegacyLoiter", "DMSE_Missile_Rheintochter", "DMSE_Missile_Shahed",
           "DMSE_AcousticEarlyWarningRadar", "DMSE_EarlyFireControlRadar"],
    "T1": ["DMSE_Missile_Termite", "DMSE_Missile_Stinger", "DMSE_Missile_Maverick", "DMSE_Missile_Grison", "DMSE_Missile_Moskit",
           "DMSE_Missile_Tochka", "DMSE_InterceptorMissile", "DMSE_SearchRadar", "DMSE_FireControlRadar"],
    "T2": ["DMSE_Missile_Sparrow", "DMSE_Missile_AdvancedLight", "DMSE_Missile_Yj", "DMSE_Missile_AdvancedCruise",
           "DMSE_PhaseArrayRadar"],
}


def nice(x):
    """Round structure amounts: to 5 above 50, to 1 below."""
    return max(1, int(round(x / 5.0) * 5)) if x >= 50 else max(1, int(round(x)))


def convert(tier, cost):
    structure = sum(VALUE[k] * v for k, v in cost.items() if k not in ELECTRONICS and k != "Chemfuel")
    electronics = sum(VALUE[k] * v for k, v in cost.items() if k in ELECTRONICS)
    out = {}
    if cost.get("Chemfuel"):
        out["Chemfuel"] = cost["Chemfuel"]
    if tier == "T0":
        out["Steel"] = nice(structure / VALUE["Steel"])
        if electronics:
            out["ComponentIndustrial"] = max(1, round(electronics / VALUE["ComponentIndustrial"]))
    elif tier == "T1":
        out["Tungsteel"] = nice(structure / VALUE["Tungsteel"])
        if electronics:
            out["ComponentStamped"] = max(1, round(electronics / VALUE["ComponentStamped"]))
    else:
        out["DMSE_TantastenAlloy"] = nice(0.7 * structure / VALUE["DMSE_TantastenAlloy"])
        out["DMSE_Graphene"] = nice(0.3 * structure / VALUE["DMSE_Graphene"])
        if electronics:
            out["ComponentSpacer"] = max(1, round(electronics / VALUE["ComponentSpacer"]))
    return out


def value(cost):
    return sum(VALUE[k] * v for k, v in cost.items())


def main():
    tier_of = {d: t for t, ds in TIERS.items() for d in ds}
    seen = set()
    for path in sorted(glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True)):
        with open(path, encoding="utf-8", newline="") as f:
            text = f.read()

        def fix(m):
            block = m.group(0)
            name = re.search(r"<defName>(.*?)</defName>", block)
            if not name or name.group(1) not in tier_of:
                return block
            n = name.group(1)
            cl = re.search(r"([ \t]*)<costList>(.*?)</costList>", block, re.S)
            old = {k: int(v) for k, v in re.findall(r"<(\w+)>(\d+)</\1>", cl.group(2))}
            unknown = set(old) - set(VALUE)
            assert not unknown, (n, unknown)
            new = convert(tier_of[n], old)
            seen.add(n)
            print("%-2s %-32s %6.0f -> %6.0f  %s  =>  %s" % (tier_of[n], n, value(old), value(new),
                  " ".join("%s:%d" % (k, v) for k, v in old.items()), " ".join("%s:%d" % (k, v) for k, v in new.items())))
            if not APPLY:
                return block
            nl = "\r\n" if "\r\n" in block else "\n"
            ind = cl.group(1)
            body = "".join("%s  <%s>%d</%s>%s" % (ind, k, v, k, nl) for k, v in new.items())
            block = block[:cl.start()] + ind + "<costList>" + nl + body + ind + "</costList>" + block[cl.end():]
            # Comments that listed the default parts' materials are stale now.
            block = re.sub(r"<!-- 含預設零件：[^>]*?-->", "<!-- 預設零件已折算在下方材料內（2026-10-27 材料分代：%s）。 -->" % tier_of[n], block)
            return block

        new_text = re.sub(r'<ThingDef (?:Name|ParentName|Class)[^>]*>.*?\n[ \t]*</ThingDef>', fix, text, flags=re.S)
        if APPLY and new_text != text:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(new_text)
    missing = set(tier_of) - seen
    assert not missing, missing


if __name__ == "__main__":
    main()
