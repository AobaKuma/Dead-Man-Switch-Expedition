#!/usr/bin/env python3
"""Validate missile body/part content and wiring without starting RimWorld (stdlib only).

Checks:
  1. every body that opens slots has at least one compatible part per slot;
  2. every body's defaultParts cover all of its slots, are compatible, and are unique per category;
  3. every thing with CompProperties_MissileConfig has its default parts' costs folded into costList;
  4. every missile item can be stored somewhere (storage filter or NPC autofill candidate);
  5. every BVR target extension declares a sane altitude;
  6. every "Class=DMSE.*" reference exists in .source;
  7. zh-TW and zh-CN DefInjected files have a label for every new part and item.
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
errors = []


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
        for res, count in expected.items():
            require(actual.get(res, 0) >= count,
                    f'{name}: costList lacks default-part materials for {res} ({actual.get(res, 0)} < {count})')

# ---- 4. storage reachability ------------------------------------------------
storable = set()
for path in (root / '1.7/Defs').rglob('*.xml'):
    text = path.read_text(encoding='utf-8-sig')
    for block in re.findall(r'<thingDefs>(.*?)</thingDefs>', text, re.S):
        storable.update(re.findall(r'<li>([\w.]+)</li>', block))
    for block in re.findall(r'<candidates>(.*?)</candidates>', text, re.S):
        storable.update(re.findall(r'<li>([\w.]+)</li>', block))

for name, entries in defs.items():
    for node, _ in entries:
        if tag(node) not in ('ThingDef', 'DMSE.ThingDef'):
            continue
        if node.find("comps/li[@Class='DMSE.CompProperties_MissileConfig']") is None:
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
for name, entries in defs.items():
    for node, _ in entries:
        if tag(node) in ('ThingDef', 'DMSE.ThingDef') and \
                node.find("comps/li[@Class='DMSE.CompProperties_MissileConfig']") is not None:
            expected_keys.add(f'{name}.label')
            expected_keys.add(f'{name}.description')

for language, keys in translated.items():
    missing = sorted(expected_keys - keys)
    require(not missing, f'{language}: missing translation keys {missing}')

if errors:
    print('\n'.join('ERROR: ' + error for error in errors))
    sys.exit(1)
print(f'PASS: {len(bodies)} bodies, {len(parts)} parts, {altitude_defs} BVR target defs, '
      f'{len(expected_keys)} translations in 2 languages.')
