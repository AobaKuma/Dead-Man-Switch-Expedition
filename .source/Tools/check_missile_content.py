#!/usr/bin/env python3
"""Validate missile body/part content and wiring without starting RimWorld (stdlib only).

Checks:
  1. every body that opens slots has at least one compatible part per slot;
  2. every body's defaultParts cover all of its slots, are compatible, and are unique per category;
  3. every thing with CompProperties_MissileConfig costs at least as much (market value) as its default parts
     (materials follow the equipment tier since 2026-10-27, so the parts' own materials need not appear);
  4. every missile item can be stored somewhere (storage filter or NPC autofill candidate);
  5. every BVR target extension declares a sane altitude;
  6. every "Class=DMSE.*" reference exists in .source;
  7. zh-TW and zh-CN DefInjected files have a label for every new part and item;
  8. every missile in a launch platform's fixed storage filter has a body the platform's
     sizeClass / extraSizeClasses and launchMode accept (MissileTaxonomy.PlatformAccepts);
  9. every body and item reference (defaultBody, MissileItemExtension.body, defaultParts,
     incomingSkyfaller, researchPrerequisites) resolves;
 10. every body has its own launchProjectile (DMSE.Projectile_MissileLaunch, reversed MissileFlightExtension,
     existing texture, hot/cold launch effect, translated label).
 11. the missile assembly bench's fixed and default storage filters both equal the set of producible missiles
     (items whose defaultBody is customizeLocation=Item), and none of those items is made from stuff;
 12. every designWord and every namer designator has zh-TW and zh-CN translations;
 13. every design-name template (English and translations) uses only the known symbols and has [body] or [designator];
 14. every producible missile lists the assembly bench in its durability-decay shelterBuildings;
 15. every customizable body has its own DMSE.MissileNamerDef (existing pack, translated, slots it opens),
     every namer is referenced by a body, and no fixed body declares one.
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
errors = []


# Market values of recipe materials (vanilla Core, Dead Man Switch, this mod); used by check 3.
MATERIAL_VALUE = {'Steel': 1.9, 'Chemfuel': 2.3, 'Cloth': 1.5, 'Plasteel': 9, 'ComponentIndustrial': 32,
                  'ComponentSpacer': 200, 'ComponentStamped': 60, 'Tungsteel': 8, 'DMSE_TantastenAlloy': 30,
                  'DMSE_Graphene': 10, 'DMSE_Nitrogallium': 75}


def require(ok, message):
    if not ok:
        errors.append(message)


def parse(path):
    return ET.parse(path).getroot()


defs = {}
for path in (root / '1.7/Defs').rglob('*.xml'):
    try:
        for node in parse(path):
            name = node.findtext('defName')
            if name:
                defs.setdefault(name, []).append((node, path))
    except ET.ParseError as exc:
        errors.append(f'{path.relative_to(root)}: {exc}')


def get(name):
    entries = defs.get(name, [])
    if len(entries) != 1:
        errors.append(f'{name}: expected one definition, found {len(entries)}')
        return None
    return entries[0][0]


def tag(node):
    return node.tag if node is not None else ''


def part_costs(part):
    costs = {}
    for item in part.findall('costList'):
        for res in item:
            costs[res.tag] = costs.get(res.tag, 0) + int(float(res.text or 0))
    return costs


def body_slots(body):
    return [s.text for s in body.findall('slots/li') if s.text]


bodies = {name: node for name, entries in defs.items()
          for node, _ in entries if tag(node) == 'DMSE.MissileBodyDef'}
parts = {name: node for name, entries in defs.items()
         for node, _ in entries if tag(node) == 'DMSE.MissilePartDef'}
things = {name: node for name, entries in defs.items()
          for node, _ in entries if tag(node) in ('ThingDef', 'DMSE.ThingDef')}


def item_body(node):
    """Body a missile item declares: CompMissileConfig.defaultBody, else MissileItemExtension.body."""
    body = node.findtext("comps/li[@Class='DMSE.CompProperties_MissileConfig']/defaultBody")
    if body:
        return body
    return node.findtext("modExtensions/li[@Class='DMSE.MissileItemExtension']/body")

# ---- 1. slot coverage -------------------------------------------------------
for name, body in bodies.items():
    slots = body_slots(body)
    if not slots:
        continue
    for slot in slots:
        found = []
        for pname, part in parts.items():
            if part.findtext('category') != slot:
                continue
            compat = [c.text for c in part.findall('compatibleBodies/li')]
            if not compat or name in compat:
                found.append(pname)
        require(found, f'{name}: slot {slot} has no compatible part')

# ---- 2. default parts ------------------------------------------------------
# Only item-form bodies carry defaults; launcher-assembled bodies (customizeLocation=Launcher,
# no item form) are built empty and paid for at assembly time.
for name, body in bodies.items():
    slots = body_slots(body)
    defaults = [p.text for p in body.findall('defaultParts/li') if p.text]
    if not slots:
        require(not defaults, f'{name}: has defaultParts but no slots')
        continue
    if body.findtext('customizeLocation') == 'Launcher':
        require(not defaults, f'{name}: launcher-assembled body must not ship default parts')
        continue
    require(defaults, f'{name}: has slots but no defaultParts')
    seen = set()
    for pname in defaults:
        part = parts.get(pname)
        require(part is not None, f'{name}: default part {pname} is not a MissilePartDef')
        if part is None:
            continue
        category = part.findtext('category')
        require(category in slots, f'{name}: default part {pname} is {category}, not an open slot')
        require(category not in seen, f'{name}: two default parts for {category}')
        seen.add(category)
        compat = [c.text for c in part.findall('compatibleBodies/li')]
        require(not compat or name in compat, f'{name}: default part {pname} is incompatible')
    require(slots and seen == set(slots), f'{name}: defaultParts do not cover {sorted(set(slots) - seen)}')

# ---- 3. build cost includes default parts -----------------------------------
for name, entries in defs.items():
    for node, path in entries:
        if tag(node) not in ('ThingDef', 'DMSE.ThingDef'):
            continue
        config = node.find("comps/li[@Class='DMSE.CompProperties_MissileConfig']")
        if config is None:
            continue
        body = bodies.get(config.findtext('defaultBody'))
        if body is None:
            errors.append(f'{name}: defaultBody missing')
            continue
        expected = {}
        for pname in [p.text for p in body.findall('defaultParts/li') if p.text]:
            part = parts.get(pname)
            if part is None:
                continue
            for res, count in part_costs(part).items():
                expected[res] = expected.get(res, 0) + count
        actual = {}
        for item in node.findall('costList'):
            for res in item:
                actual[res.tag] = actual.get(res.tag, 0) + int(float(res.text or 0))
        # Default parts are bundled into the tier materials (.source/Tools/tier_missile_costs.py): compare value, not materials.
        unknown = sorted((set(expected) | set(actual)) - set(MATERIAL_VALUE))
        require(not unknown, f'{name}: no market value known for {unknown} (add it to MATERIAL_VALUE)')
        cost_value = sum(MATERIAL_VALUE.get(r, 0) * c for r, c in actual.items())
        parts_value = sum(MATERIAL_VALUE.get(r, 0) * c for r, c in expected.items())
        require(cost_value >= parts_value,
                f'{name}: costList is worth {cost_value:.0f}, less than its default parts ({parts_value:.0f})')

# ---- 4. storage reachability ------------------------------------------------
storable = set()
for path in (root / '1.7/Defs').rglob('*.xml'):
    text = path.read_text(encoding='utf-8-sig')
    for block in re.findall(r'<thingDefs>(.*?)</thingDefs>', text, re.S):
        storable.update(re.findall(r'<li>([\w.]+)</li>', block))
    for block in re.findall(r'<candidates>(.*?)</candidates>', text, re.S):
        storable.update(re.findall(r'<li>([\w.]+)</li>', block))

for name, node in things.items():
    if item_body(node) is None:
        continue
    # Buildings (launchers) hold their own round; only item missiles need a storage home.
    if node.findtext('stackLimit') is None:
        continue
    require(name in storable, f'{name}: no storage filter or autofill candidate accepts this missile')

# ---- 5. altitude ------------------------------------------------------------
altitude_defs = 0
for name, entries in defs.items():
    for node, path in entries:
        ext = node.find("modExtensions/li[@Class='DMSE.BVRTargetProps']")
        if ext is None:
            continue
        altitude_defs += 1
        raw = ext.findtext('altitude')
        require(raw is not None, f'{name}: BVRTargetProps lacks altitude')
        if raw is not None:
            value = float(raw)
            require(50 <= value <= 200000, f'{name}: altitude {value} out of range')

# ---- 6. class references ----------------------------------------------------
classes = set()
for path in (root / '.source/DMSE').rglob('*.cs'):
    if 'obj' in path.parts or 'bin' in path.parts:
        continue
    classes.update(re.findall(r'\bclass\s+(\w+)', path.read_text(encoding='utf-8-sig')))
for path in (root / '1.7/Defs').rglob('*.xml'):
    for cls in re.findall(r'Class="DMSE\.(\w+)"', path.read_text(encoding='utf-8-sig')):
        require(cls in classes, f'{path.relative_to(root)}: DMSE.{cls} has no class in .source')

# ---- 7. translations --------------------------------------------------------
languages = ['ChineseTraditional (繁體中文)', 'ChineseSimplified (简体中文)']
translated = {}
for language in languages:
    folder = root / '1.7/Languages' / language / 'DefInjected'
    keys = set()
    for path in folder.rglob('*.xml'):
        try:
            keys.update(node.tag for node in parse(path))
        except ET.ParseError as exc:
            errors.append(f'{path.relative_to(root)}: {exc}')
    translated[language] = keys

expected_keys = set()
for name in parts:
    expected_keys.add(f'{name}.label')
    expected_keys.add(f'{name}.description')
for name in bodies:
    expected_keys.add(f'{name}.label')
    expected_keys.add(f'{name}.description')
for name, node in things.items():
    is_platform = node.find("modExtensions/li[@Class='DMSE.MissileLauncherExtension']") is not None
    if item_body(node) is not None or is_platform:
        expected_keys.add(f'{name}.label')
        expected_keys.add(f'{name}.description')

for language, keys in translated.items():
    missing = sorted(expected_keys - keys)
    require(not missing, f'{language}: missing translation keys {missing}')

# ---- 8. platform filters vs size class --------------------------------------
SIZE_ORDER = ['Loitering', 'Light', 'Medium', 'Ballistic']
for name, node in things.items():
    ext = node.find("modExtensions/li[@Class='DMSE.MissileLauncherExtension']")
    if ext is None:
        continue
    accepted = {ext.findtext('sizeClass') or 'Medium'}
    accepted.update(li.text for li in ext.findall('extraSizeClasses/li') if li.text)
    mode = ext.findtext('launchMode') or 'Tilt'
    require(mode in ('Tilt', 'Vertical'), f'{name}: platform launchMode must be Tilt or Vertical, not {mode}')
    for size in accepted:
        require(size in SIZE_ORDER, f'{name}: unknown size class {size}')
    for li in node.findall('building/fixedStorageSettings/filter/thingDefs/li'):
        item = things.get(li.text)
        if item is None:
            errors.append(f'{name}: storage filter lists unknown ThingDef {li.text}')
            continue
        body_name = item_body(item)
        if body_name is None:
            continue  # generic rounds without a body (e.g. the interceptor missile)
        body = bodies.get(body_name)
        if body is None:
            continue  # reported by rule 9
        size = body.findtext('sizeClass') or 'Medium'
        body_mode = body.findtext('launchMode') or 'Both'
        require(size in accepted,
                f'{name}: holds {li.text} ({size}) but accepts only {sorted(accepted)}; add extraSizeClasses')
        require(body_mode in ('Both', mode),
                f'{name}: holds {li.text} (launchMode {body_mode}) on a {mode} platform')

# ---- 9. references resolve --------------------------------------------------
# RimWorld rejects ThingDef names that end in a digit ("ends with a numerical digit" config error).
for name in things:
    require(not name[-1].isdigit(), f'{name}: ThingDef defName must not end with a digit')
research = {name for name, entries in defs.items() for node, _ in entries if tag(node) == 'ResearchProjectDef'}
for name, node in things.items():
    body = item_body(node)
    if body is not None:
        require(body in bodies, f'{name}: missile body {body} does not exist')
for name, body in bodies.items():
    incoming = body.findtext('incomingSkyfaller')
    require(incoming in things, f'{name}: incomingSkyfaller {incoming} does not exist')
    if incoming in things:
        # docs/Missile_Skyfaller轉Projectile_計畫.md §8.4: incoming missiles are Projectiles that fall (not reversed).
        require(things[incoming].findtext('thingClass') == 'DMSE.Projectile_MissileIncoming',
                f'{incoming}: thingClass must be DMSE.Projectile_MissileIncoming')
        fall = things[incoming].find("modExtensions/li[@Class='DMSE.MissileFlightExtension']")
        require(fall is not None, f'{incoming}: DMSE.MissileFlightExtension missing (run .source/Tools/gen_missile_launch_defs.py)')
        if fall is not None:
            require((fall.findtext('reversed') or 'false').strip().lower() != 'true', f'{incoming}: flight must not be reversed')
        require(things[incoming].find('skyfaller') is None, f'{incoming}: leftover <skyfaller> block')
    for li in body.findall('researchPrerequisites/li'):
        require(li.text in research, f'{name}: research {li.text} does not exist')
for name, part in parts.items():
    for li in part.findall('compatibleBodies/li'):
        require(li.text in bodies, f'{name}: compatible body {li.text} does not exist')
    for li in part.findall('researchPrerequisites/li'):
        require(li.text in research, f'{name}: research {li.text} does not exist')

# ---- 10. every body launches its own Projectile ------------------------------
# docs/Missile_Skyfaller轉Projectile_計畫.md: launch look and exhaust follow the body, not the platform.
abstract_things = {}
for path in (root / '1.7/Defs').rglob('*.xml'):
    try:
        for node in parse(path):
            if node.tag == 'ThingDef' and node.get('Name'):
                abstract_things[node.get('Name')] = node
    except ET.ParseError:
        pass  # reported above


def inherited(node, path):
    """findtext through the ParentName chain (Name="..." abstract parents included)."""
    seen = set()
    while node is not None:
        value = node.findtext(path)
        if value is not None:
            return value
        parent = node.get('ParentName')
        if not parent or parent in seen:
            return None
        seen.add(parent)
        node = abstract_things.get(parent)
    return None


texture_roots = [root / '1.7/Textures', root / 'Textures']
launch_classes = ('DMSE.CompProperties_EffecterHotLaunch', 'DMSE.CompProperties_EffecterColdLaunch')
for name, body in bodies.items():
    launch = body.findtext('launchProjectile')
    require(launch is not None, f'{name}: launchProjectile missing (run .source/Tools/gen_missile_launch_defs.py)')
    if launch is None:
        continue
    node = things.get(launch)
    require(node is not None, f'{name}: launchProjectile {launch} does not exist')
    if node is None:
        continue
    require(inherited(node, 'thingClass') == 'DMSE.Projectile_MissileLaunch',
            f'{launch}: thingClass must be DMSE.Projectile_MissileLaunch')
    require(inherited(node, 'category') == 'Projectile', f'{launch}: category must be Projectile')
    flight = node.find("modExtensions/li[@Class='DMSE.MissileFlightExtension']")
    require(flight is not None, f'{launch}: DMSE.MissileFlightExtension missing')
    if flight is not None:
        require((flight.findtext('reversed') or '').strip().lower() == 'true', f'{launch}: flight must be reversed (ascent)')
        discard = (flight.findtext('ticksToDiscardInReverse') or '0').split('~')[0].strip()
        require(discard.isdigit() and int(discard) > 0, f'{launch}: ticksToDiscardInReverse must be > 0')
    tex = node.findtext('graphicData/texPath')
    require(tex is not None and any((r / (tex + '.png')).exists() for r in texture_roots),
            f'{launch}: texture {tex} not found')
    require(any(node.find(f"comps/li[@Class='{c}']") is not None for c in launch_classes),
            f'{launch}: needs a hot or cold launch effect comp')
    for language in languages:
        require(f'{launch}.label' in translated[language], f'{language}: missing {launch}.label')

# ---- 11. assembly bench produces every customizable item --------------------
# docs/導彈組裝台與裝配方案_實作計畫.md §4.5: the bench's storage lists must match MissileItemUtility.ProducibleBodies.
BENCH = 'DMSE_MissileAssemblyBench'
producible = set()
for name, node in things.items():
    body_name = node.findtext("comps/li[@Class='DMSE.CompProperties_MissileConfig']/defaultBody")
    body = bodies.get(body_name) if body_name else None
    if body is not None and (body.findtext('customizeLocation') or 'Item') == 'Item':
        producible.add(name)
        require(node.find('stuffCategories') is None, f'{name}: producible at the assembly bench but made from stuff')
bench = things.get(BENCH)
require(bench is not None, f'{BENCH} is missing')
if bench is not None:
    for which in ('fixedStorageSettings', 'defaultStorageSettings'):
        listed = {li.text for li in bench.findall(f'building/{which}/filter/thingDefs/li')}
        require(listed == producible, f'{BENCH} {which}: missing {sorted(producible - listed)}, extra {sorted(listed - producible)}')

# ---- 12. design words and namer designators translated ----------------------
for name, node in list(parts.items()) + list(bodies.items()):
    if node.findtext('designWord'):
        for language in languages:
            require(f'{name}.designWord' in translated[language], f'{language}: missing {name}.designWord')

namers = {name: node for name, entries in defs.items()
          for node, _ in entries if tag(node) == 'DMSE.MissileNamerDef'}
packs = {name: node for name, entries in defs.items()
         for node, _ in entries if tag(node) == 'RulePackDef'}
require('DMSE_NamerMissileDesign' in packs, 'DMSE_NamerMissileDesign rule pack is missing')

for name in namers:
    for language in languages:
        require(f'{name}.designator' in translated[language], f'{language}: missing {name}.designator')
for name, node in namers.items():
    pack = node.findtext('pack')
    if pack:
        for language in languages:
            require(f'{pack}.rulePack.rulesStrings' in translated[language],
                    f'{language}: {pack} rule pack is not translated')

# ---- 13. design-name templates use known symbols ----------------------------
DESIGN_SYMBOLS = {'body', 'warhead', 'guidance', 'propulsion', 'payload', 'designator', 'num', 'suffix'}
design_packs = {name for name in packs if name.startswith('DMSE_NamerPack_')} | {'DMSE_NamerMissileDesign'}
templates = []
for name in design_packs:
    templates += [('Defs', name, li.text or '') for li in packs[name].findall('rulePack/rulesStrings/li')]
for language in languages:
    for path in (root / '1.7/Languages' / language / 'DefInjected/RulePackDef').glob('*.xml'):
        for node in parse(path):
            pack = node.tag.split('.')[0]
            if node.tag.endswith('.rulePack.rulesStrings') and pack in design_packs:
                templates += [(language, pack, li.text or '') for li in node.findall('li')]
for where, pack, rule in templates:
    if not rule.startswith('r_name'):
        continue
    symbols = set(re.findall(r'\[(\w+)\]', rule))
    require(symbols <= DESIGN_SYMBOLS,
            f'{where}: {pack} template {rule!r} uses unknown symbols {sorted(symbols - DESIGN_SYMBOLS)}')
    require('body' in symbols or 'designator' in symbols,
            f'{where}: {pack} template {rule!r} has neither [body] nor [designator]')
for name in design_packs:
    rules = [li.text or '' for li in packs[name].findall('rulePack/rulesStrings/li')]
    require(any(r.startswith('r_name') for r in rules), f'{name}: no r_name templates')

# ---- 14. bench shelters its output ------------------------------------------
for name in producible:
    shelters = {li.text for li in things[name].findall("comps/li[@Class='DMSE.CompProperties_DurabilityDecay']/shelterBuildings/li")}
    require(BENCH in shelters, f'{name}: {BENCH} missing from shelterBuildings (finished missiles would decay on the bench)')

# ---- 15. every customizable body has its own namer --------------------------
for name, body in bodies.items():
    customize = body.findtext('customizeLocation') or 'Item'
    namer_name = body.findtext('namer')
    if customize == 'None':
        require(namer_name is None, f'{name}: not customizable, so it must not declare a namer')
        continue
    require(namer_name is not None, f'{name}: customizable body has no <namer> (names would fall back to the generic pack)')
    if namer_name is None:
        continue
    require(namer_name in namers, f'{name}: namer {namer_name} is not a DMSE.MissileNamerDef')
    if namer_name not in namers:
        continue
    pack = namers[namer_name].findtext('pack')
    require(pack in packs, f'{namer_name}: pack {pack} is not a RulePackDef')
    if pack in packs:
        slots = {slot.lower() for slot in body_slots(body)}
        for rule in (li.text or '' for li in packs[pack].findall('rulePack/rulesStrings/li')):
            if not rule.startswith('r_name'):
                continue
            used = set(re.findall(r'\[(\w+)\]', rule)) - {'body', 'designator', 'num', 'suffix'}
            require(used <= slots, f'{pack}: template {rule!r} uses slots {sorted(used - slots)} that {name} does not open')

used_namers = {body.findtext('namer') for body in bodies.values() if body.findtext('namer')}
for name in namers:
    require(name in used_namers, f'{name}: not referenced by any MissileBodyDef')
    low, high = namers[name].findtext('numberMin'), namers[name].findtext('numberMax')
    if low is not None or high is not None:
        require((low or '1').isdigit() and (high or '99').isdigit(),
                f'{name}: numberMin/numberMax must be integers')
        require(int(low or 1) <= int(high or 99), f'{name}: numberMin {low} > numberMax {high}')

if errors:
    print('\n'.join('ERROR: ' + error for error in errors))
    sys.exit(1)
print(f'PASS: {len(bodies)} bodies, {len(parts)} parts, {altitude_defs} BVR target defs, '
      f'{len(expected_keys)} translations in 2 languages, {len(producible)} bench-producible missiles, '
      f'{len(namers)} missile namers, {len(templates)} design-name templates.')
