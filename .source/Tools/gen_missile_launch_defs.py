"""Generate one launch Projectile per missile body (docs/Missile_Skyfaller轉Projectile_計畫.md).

Every MissileBodyDef gets `DMSE_Launch_<Body>` (thingClass DMSE.Projectile_MissileLaunch):
  - graphic: the body's own missile texture (nose up) at its incoming drawSize;
  - flight: the <skyfaller> block of a template launch skyfaller, moved into DMSE.MissileFlightExtension unchanged;
  - effect: hot or cold launch comp with the template's exhaust/smoke curves and a per-body blast scale / eject delay.
The four bodies that already had a dedicated launch skyfaller copy it verbatim (graphic, flight and comp),
so their launches look exactly as before.

Also inserts <launchProjectile> into each body def and writes the label translations.
Idempotent: rerun after changing SPECS.  Usage: python .source/Tools/gen_missile_launch_defs.py [mod root]
"""
import glob
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..")
DEFS = os.path.join(ROOT, "1.7", "Defs")
LANG = os.path.join(ROOT, "1.7", "Languages")
OUT_DEFS = os.path.join(DEFS, "Missile", "DMSE_MissileLaunchProjectiles.xml")
OUT_LANG_NAME = "DMSE_MissileLaunchProjectiles.xml"

# body suffix, template launch skyfaller, texture (None = template's), effect (None = template's comp verbatim)
#   effect: ("hot", launchBlastScale) | ("cold", delayTicks)
SPECS = [
    # Bodies with a dedicated launch skyfaller already: verbatim copies.
    ("Ballistic",         "DMSE_SkyFaller_BallisticMissile", None, None),
    ("Light",             "DMSE_SkyFaller_LightMissile",     None, None),
    ("Loiter",            "DMSE_SkyFaller_Loiter",           None, None),
    ("Cruise",            "DMSE_SkyFaller_CruiseMissile",    None, None),
    # Three-generation bodies: own texture, flight of the closest family, effect by how the real missile leaves its launcher.
    ("LegacyBallistic",   "DMSE_SkyFaller_BallisticMissile", "Things/Missile/V2/skyFaller_incoming",        ("hot", 1.2)),   # V2 lifts off its pad
    ("LegacyLoiter",      "DMSE_SkyFaller_Loiter",           "Things/Missile/V1/skyFaller_incoming",        ("hot", 0.7)),   # V1 off its ramp
    ("Shahed",            "DMSE_SkyFaller_Loiter",           "Things/Missile/Shahed/skyFaller_incoming",    ("hot", 0.5)),   # rail booster
    ("LegacyInterceptor", "DMSE_SkyFaller_Interceptor",      "Things/Missile/Rheintochter/skyFaller",       ("hot", 1.0)),   # boosted SAM
    ("AdvancedLight",     "DMSE_SkyFaller_LightMissile",     "Things/Missile/Hellfire/skyFaller_incoming",  ("hot", 0.75)),  # small rail missile
    ("AdvancedBallistic", "DMSE_SkyFaller_BallisticMissile", "Things/Missile/Minuteman/skyFaller_incoming", ("cold", 20)),   # silo ejection
    ("AdvancedCruise",    "DMSE_SkyFaller_CruiseMissile",    "Things/Missile/Tomahawk/skyFaller_incoming",  ("cold", 40)),   # canister ejection
    ("Stinger",           "DMSE_SkyFaller_Interceptor",      "Things/Weapons/DMSE_Projectile_Stinger",      ("cold", 8)),    # MANPADS soft launch
    ("Maverick",          "DMSE_SkyFaller_LightMissile",     "Things/Missile/AGM65/skyFaller",              ("hot", 0.85)),
    ("Sparrow",           "DMSE_SkyFaller_Interceptor",      "Things/Missile/Aim120/skyFaller",             ("hot", 1.0)),
    ("Moskit",            "DMSE_SkyFaller_CruiseMissile",    "Things/Missile/Moskit/skyFaller",             ("hot", 1.0)),   # booster
    ("Yj",                "DMSE_SkyFaller_CruiseMissile",    "Things/Missile/YJ20/skyFaller",             ("hot", 1.1)),
    ("Tochka",            "DMSE_SkyFaller_BallisticMissile", "Things/Missile/Tochka/skyFaller_incoming",    ("hot", 1.1)),   # off a launcher vehicle
]

# Launch FX profile per body (docs/導彈發射特效_重製計畫.md §5, approved 2026-10-26): written as <fxProfile> into the launch comp.
FX_PROFILES = {
    "Ballistic": "ColdVls",
    "Light": "SolidBooster",
    "Loiter": "Pulsejet",
    "Cruise": "CruiseBooster",
    "LegacyBallistic": "LiquidBallistic",
    "LegacyLoiter": "Pulsejet",
    "Shahed": "SolidBooster",
    "LegacyInterceptor": "SolidBooster",
    "AdvancedLight": "SolidBooster",
    "AdvancedBallistic": "ColdVls",
    "AdvancedCruise": "ColdVls",
    "Stinger": "Manpads",
    "Maverick": "SolidBooster",
    "Sparrow": "SolidBooster",
    "Moskit": "CruiseBooster",
    "Yj": "CruiseBooster",
    "Tochka": "SolidBooster",
}

# Departure turn per flight family (2026-10-26: turn later and gentler). Code defaults: handoffMaxCells 16, maxTurnRate 2 °/tick.
# Ballistic bodies accelerate much harder, so they climb further (22 cells ≈ 44 ticks after ignition) and turn slower.
FLIGHT_OVERRIDES = {
    "DMSE_SkyFaller_BallisticMissile": {"handoffMaxCells": "22", "maxTurnRate": "1.2"},
}

LAUNCH_EFFECT_OVERRIDES = {
    "Shahed": {"burnDurationTicks": "30", "effectScale": "0.35"},
}

SHARED_COMP_TAGS = ["ignitionSound", "ExhaustFleck", "ExhaustCurve", "SmokeFleck", "SmokeCurve", "launchEffectTrigger", "offsetY", "effecterDef"]


def read(path):
    # newline="" keeps CRLF as is, so rewriting a def file only changes the lines we touch.
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def write_generated(path, text):
    """New files use CRLF like the rest of the repository."""
    write(path, text.replace("\r\n", "\n").replace("\n", "\r\n"))


def def_blocks(text, tag):
    for m in re.finditer(r"<%s\b[^>]*>(.*?)</%s>" % (re.escape(tag), re.escape(tag)), text, re.S):
        dn = re.search(r"<defName>(.*?)</defName>", m.group(1))
        if dn:
            yield dn.group(1).strip(), m


def child(block, tag):
    m = re.search(r"<%s>(.*?)</%s>" % (tag, tag), block, re.S)
    return m.group(1) if m else None


def child_raw(block, tag):
    m = re.search(r"<%s\b[^>]*>.*?</%s>|<%s\b[^>]*/>" % (tag, tag, tag), block, re.S)
    return m.group(0) if m else None


def reindent(xml, indent):
    lines = [l.strip() for l in xml.strip().splitlines() if l.strip()]
    out, depth = [], 0
    for l in lines:
        if l.startswith("</"):
            depth -= 1
        out.append(indent + "  " * depth + l)
        opens = re.match(r"<([A-Za-z_][\w.]*)[^>]*>", l)
        if opens and not l.startswith("</") and not l.endswith("/>") and ("</%s>" % opens.group(1)) not in l:
            depth += 1
    return "\n".join(out)


def load_defs():
    things, bodies = {}, {}
    files = {}
    for path in glob.glob(os.path.join(DEFS, "**", "*.xml"), recursive=True):
        if os.path.abspath(path) == os.path.abspath(OUT_DEFS):
            continue
        text = read(path)
        for dn, m in def_blocks(text, "ThingDef"):
            things[dn] = m.group(1)
        for dn, m in def_blocks(text, "DMSE.MissileBodyDef"):
            bodies[dn] = m.group(1)
            files[dn] = path
    return things, bodies, files


def comp_xml(template, effect):
    comps = child(template, "comps") or ""
    li = re.search(r'<li Class="DMSE\.CompProperties_Effecter(Hot|Cold)Launch">(.*?)</li>\s*$', comps.strip(), re.S)
    if li is None:
        return None
    if effect is None:
        return '<li Class="DMSE.CompProperties_Effecter%sLaunch">\n%s\n</li>' % (li.group(1), li.group(2))
    shared = [child_raw(li.group(2), t) for t in SHARED_COMP_TAGS]
    kind, value = effect
    if kind == "hot":
        head = '<li Class="DMSE.CompProperties_EffecterHotLaunch">\n<launchBlastScale>%s</launchBlastScale>' % value
    else:
        head = '<li Class="DMSE.CompProperties_EffecterColdLaunch">\n<delayTicks>%d</delayTicks>' % value
    return head + "\n" + "\n".join(s for s in shared if s) + "\n</li>"


def graphic_xml(template, tex, draw_size):
    g = child(template, "graphicData")
    if tex is not None:
        g = re.sub(r"<texPath>.*?</texPath>", "<texPath>%s</texPath>" % tex, g)
    if draw_size is not None:
        g = re.sub(r"<drawSize>.*?</drawSize>", "<drawSize>%s</drawSize>" % draw_size, g)
    return "<graphicData>%s</graphicData>" % g


def translations(lang_dir):
    found = {}
    for path in glob.glob(os.path.join(lang_dir, "DefInjected", "ThingDef", "*.xml")):
        if os.path.basename(path) == OUT_LANG_NAME:
            continue
        for m in re.finditer(r"<([\w]+)\.label>(.*?)</\1\.label>", read(path)):
            found[m.group(1)] = m.group(2)
    return found


def convert_incoming_defs():
    """Old incoming skyfallers (ParentName SkyfallerBase, thingClass DMSE.MissileIncoming) become
    DMSE.Projectile_MissileIncoming in place: same defName, <skyfaller> moved into DMSE.MissileFlightExtension."""
    converted = 0
    for path in glob.glob(os.path.join(DEFS, "**", "*.xml"), recursive=True):
        text = read(path)
        nl = "\r\n" if "\r\n" in text else "\n"
        new_text = text
        for m in re.finditer(r'<ThingDef ParentName="SkyfallerBase">.*?</ThingDef>', text, re.S):
            block = m.group(0)
            if "<thingClass>DMSE.MissileIncoming</thingClass>" not in block:
                continue
            sky = re.search(r"([ \t]*)<skyfaller>(.*?)</skyfaller>[ \t]*\r?\n", block, re.S)
            if sky is None:
                continue
            indent = sky.group(1)
            inner = sky.group(2)
            flight = (indent + "  " + '<li Class="DMSE.MissileFlightExtension">'
                      + inner.rstrip() + nl + indent + "  </li>" + nl)
            new = block.replace('<ThingDef ParentName="SkyfallerBase">', '<ThingDef ParentName="DMSE_MissileIncomingProjectileBase">', 1)
            new = new.replace("<thingClass>DMSE.MissileIncoming</thingClass>", "<thingClass>DMSE.Projectile_MissileIncoming</thingClass>", 1)
            new = new.replace(sky.group(0), "", 1)
            if "<modExtensions>" in new:
                new = re.sub(r"(<modExtensions>[ \t]*\r?\n)", lambda mm: mm.group(1) + flight, new, count=1)
            else:
                new = new.replace("</ThingDef>", indent + "<modExtensions>" + nl + flight + indent + "</modExtensions>" + nl + indent[:-2] + "</ThingDef>", 1)
            new_text = new_text.replace(block, new, 1)
            converted += 1
        if new_text != text:
            write(path, new_text)
    return converted


def main():
    converted = convert_incoming_defs()
    if converted:
        print("incoming defs converted to Projectile: %d" % converted)
    things, bodies, body_files = load_defs()
    parts = ['<?xml version="1.0" encoding="utf-8"?>',
             "<!-- Generated by .source/Tools/gen_missile_launch_defs.py: edit SPECS there, not this file. -->",
             "<Defs>",
             "",
             "  <!-- Launch Projectile shared by every missile body (docs/Missile_Skyfaller轉Projectile_計畫.md):",
             "       same draw layer and offscreen drawing as the launch skyfallers it replaces. -->",
             '  <ThingDef Name="DMSE_MissileLaunchProjectileBase" Abstract="True">',
             "    <thingClass>DMSE.Projectile_MissileLaunch</thingClass>",
             "    <category>Projectile</category>",
             "    <tickerType>Normal</tickerType>",
             "    <altitudeLayer>Skyfaller</altitudeLayer>",
             "    <drawerType>RealtimeOnly</drawerType>",
             "    <drawOffscreen>true</drawOffscreen>",
             "    <useHitPoints>false</useHitPoints>",
             "    <neverMultiSelect>true</neverMultiSelect>",
             "    <size>(1,1)</size>",
             "    <projectile>",
             "      <flyOverhead>true</flyOverhead>",
             "      <speed>1</speed>",
             "      <damageDef>Bomb</damageDef>",
             "    </projectile>",
             "  </ThingDef>",
             "",
             "  <!-- Incoming missile Projectile (plan §8.4): converted in place from the old SkyfallerBase incoming defs",
             "       by convert_incoming_defs(); flies in from the map edge along the world track's last leg. -->",
             '  <ThingDef Name="DMSE_MissileIncomingProjectileBase" Abstract="True">',
             "    <thingClass>DMSE.Projectile_MissileIncoming</thingClass>",
             "    <category>Projectile</category>",
             "    <tickerType>Normal</tickerType>",
             "    <altitudeLayer>Skyfaller</altitudeLayer>",
             "    <drawerType>RealtimeOnly</drawerType>",
             "    <drawOffscreen>true</drawOffscreen>",
             "    <useHitPoints>false</useHitPoints>",
             "    <neverMultiSelect>true</neverMultiSelect>",
             "    <size>(1,1)</size>",
             "    <projectile>",
             "      <flyOverhead>true</flyOverhead>",
             "      <speed>1</speed>",
             "      <damageDef>Bomb</damageDef>",
             "    </projectile>",
             "  </ThingDef>",
             ""]
    labels = {}
    missing = []
    for suffix, template_name, tex, effect in SPECS:
        body_name = "DMSE_MissileBody_" + suffix
        body = bodies.get(body_name)
        template = things.get(template_name)
        if body is None or template is None:
            missing.append(body_name if body is None else template_name)
            continue
        incoming_name = (child(body, "incomingSkyfaller") or "").strip()
        incoming = things.get(incoming_name, "")
        incoming_label = (child(incoming, "label") or body_name).strip()
        label = incoming_label.replace("(incoming)", "(launching)")
        draw_size = None if tex is None else (child(child(incoming, "graphicData") or "", "drawSize") or "").strip() or None
        name = "DMSE_Launch_" + suffix
        labels[name] = incoming_name
        flight = child(template, "skyfaller")
        flight = flight.rstrip() + "".join("\n<%s>%s</%s>" % (k, v, k) for k, v in FLIGHT_OVERRIDES.get(template_name, {}).items()) + "\n"
        comp = comp_xml(template, effect)
        if comp:
            extra = "".join("<%s>%s</%s>\n" % (key, value, key)
                            for key, value in LAUNCH_EFFECT_OVERRIDES.get(suffix, {}).items())
            if suffix in FX_PROFILES:
                extra += "<fxProfile>DMSE_LaunchFx_%s</fxProfile>\n" % FX_PROFILES[suffix]
            comp = comp.removesuffix("</li>") + extra + "</li>"
        note = ("copy of %s" % template_name) if tex is None else ("%s flight, %s" % (template_name, "%s launch %s" % effect if effect else "template launch"))
        parts.append("  <!-- %s: %s -->" % (body_name, note))
        parts.append('  <ThingDef ParentName="DMSE_MissileLaunchProjectileBase">')
        parts.append("    <defName>%s</defName>" % name)
        parts.append("    <label>%s</label>" % label)
        parts.append(reindent(graphic_xml(template, tex, draw_size), "    "))
        parts.append("    <modExtensions>")
        parts.append(reindent('<li Class="DMSE.MissileFlightExtension">%s</li>' % flight, "      "))
        parts.append("    </modExtensions>")
        if comp:
            parts.append("    <comps>")
            parts.append(reindent(comp, "      "))
            parts.append("    </comps>")
        parts.append("  </ThingDef>")
        parts.append("")
    parts.append("</Defs>")
    write_generated(OUT_DEFS, "\n".join(parts) + "\n")

    # <launchProjectile> on each body (right after <incomingSkyfaller>).
    unspecified = sorted(set(bodies) - {"DMSE_MissileBody_" + s[0] for s in SPECS})
    for suffix, _, _, _ in SPECS:
        body_name = "DMSE_MissileBody_" + suffix
        path = body_files.get(body_name)
        if path is None:
            continue
        text = read(path)
        for dn, m in def_blocks(text, "DMSE.MissileBodyDef"):
            if dn != body_name:
                continue
            block = m.group(0)
            want = "DMSE_Launch_" + suffix
            if "<launchProjectile>" in block:
                new = re.sub(r"<launchProjectile>.*?</launchProjectile>", "<launchProjectile>%s</launchProjectile>" % want, block)
            else:
                nl = "\r\n" if "\r\n" in text else "\n"
                new = re.sub(r"(\n([ \t]*)<incomingSkyfaller>.*?</incomingSkyfaller>)",
                             lambda mm: mm.group(1) + nl + mm.group(2) + "<launchProjectile>%s</launchProjectile>" % want, block, count=1)
            if new != block:
                text = text.replace(block, new)
                write(path, text)
            break

    # Labels: "<incoming label> → launching" in each language that translates the incoming def.
    for lang in os.listdir(LANG):
        lang_dir = os.path.join(LANG, lang)
        if not os.path.isdir(os.path.join(lang_dir, "DefInjected")):
            continue
        known = translations(lang_dir)
        rows = []
        for name, incoming_name in labels.items():
            src = known.get(incoming_name)
            if src is None:
                continue
            rows.append("  <%s.label>%s</%s.label>" % (name, src.replace("（來襲）", "（升空）").replace("（来袭）", "（升空）"), name))
        if rows:
            write_generated(os.path.join(lang_dir, "DefInjected", "ThingDef", OUT_LANG_NAME),
                  '<?xml version="1.0" encoding="utf-8"?>\n<!-- Generated by .source/Tools/gen_missile_launch_defs.py -->\n<LanguageData>\n'
                  + "\n".join(rows) + "\n</LanguageData>\n")

    print("launch projectiles: %d written to %s" % (len(SPECS) - len(missing), os.path.relpath(OUT_DEFS, ROOT)))
    if missing:
        print("MISSING:", ", ".join(missing))
    if unspecified:
        print("BODIES WITHOUT A SPEC:", ", ".join(unspecified))
    return 1 if missing or unspecified else 0


if __name__ == "__main__":
    sys.exit(main())
