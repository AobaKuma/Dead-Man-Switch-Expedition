#!/usr/bin/env python3
"""Validate the world-BVR content without starting RimWorld (Python standard library only).

Since 2026-11 the air-defense framework (interceptor world objects, tuning, textures, keyed text) lives in
FFF (Fortified-Framework, 1.7/Odyssey); DMSE only ships content that uses it (radars, NPC missile bases).
This script therefore reads both mods.

Usage: check_bvr_world.py [DMSE root] [FFF root]
The FFF root defaults to $FFF_ROOT, then to the sibling folder _Fortified-Framework.
"""
import os
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

try:
    sys.stdout.reconfigure(errors='replace')
except AttributeError:
    pass

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
if len(sys.argv) > 2:
    fff = Path(sys.argv[2]).resolve()
elif os.environ.get('FFF_ROOT'):
    fff = Path(os.environ['FFF_ROOT']).resolve()
else:
    fff = root.parent / '_Fortified-Framework'
if not (fff / 'About/About.xml').is_file():
    print(f'ERROR: FFF not found at {fff} (pass it as the second argument or set FFF_ROOT)')
    sys.exit(2)

# Load folders for 1.7 with Odyssey active (CE folders excluded).
# Early-era content (acoustic mirror, early fire control, V1...) lives in the sibling Early Warfare mod since 2026-11-27.
early = root.parent / '_Dead-Man-Switch-Expedition-EarlyWarfare'
def_dirs = [root / '1.7/Defs', early / '1.7/Defs', fff / '1.6/Defs', fff / '1.7/Defs', fff / '1.7/Odyssey/Defs']
texture_dirs = [root / '1.7/Textures', early / '1.7/Textures', fff / 'Textures', fff / '1.6/Textures', fff / '1.7/Textures',
                fff / '1.7/Odyssey/Textures']
source_dirs = [root / '.source/DMSE', fff / '_Sources/Fortified']
fff_languages = fff / '1.7/Odyssey/Languages'

errors = []
def require(ok, message):
    if not ok:
        errors.append(message)

def rel(path):
    for base in (root, fff):
        try:
            return f'{base.name}/{path.relative_to(base)}'
        except ValueError:
            pass
    return str(path)

defs = {}
for folder in def_dirs:
    for path in folder.rglob('*.xml') if folder.is_dir() else []:
        try:
            for node in ET.parse(path).getroot():
                name = node.findtext('defName')
                if name:
                    defs.setdefault(name, []).append((node, path))
        except ET.ParseError as exc:
            errors.append(f'{rel(path)}: {exc}')

def get(name):
    entries = defs.get(name, [])
    require(len(entries) == 1, f'{name}: expected one definition, found {len(entries)}')
    return entries[0][0] if entries else ET.Element('missing')

def sources():
    for folder in source_dirs:
        for path in folder.rglob('*.cs'):
            if 'obj' not in path.parts and 'bin' not in path.parts:
                yield path.read_text(encoding='utf-8-sig', errors='replace')

classes = set()
used = set()
for text in sources():
    classes.update(re.findall(r'\bclass\s+(\w+)', text))
    used.update(re.findall(r'"(FFF\.BVR\.World\.[\w.]+)"', text))

def find_texture(path):
    for folder in texture_dirs:
        candidate = folder / (path + '.png')
        if candidate.is_file():
            return candidate
    return None

def check_png(texture, name):
    data = texture.read_bytes()
    require(data[:8] == b'\x89PNG\r\n\x1a\n', f'{name}: invalid PNG')
    width, height, depth, color = struct.unpack('>IIBB', data[16:26])
    require(width > 0 and height > 0 and depth == 8 and color == 6, f'{name}: expected 8-bit RGBA PNG')

# Framework world objects (FFF).
world_objects = ('FFF_Interceptor', 'FFF_BlockadeInterceptor')
for name in world_objects:
    world = get(name)
    require(world.findtext('worldObjectClass', '').startswith('Fortified.'), f'{name}: worldObjectClass not in Fortified')
    cls = world.findtext('worldObjectClass', '').rsplit('.', 1)[-1]
    require(cls in classes, f'{name}: class {cls} missing')
    for field in ('texture', 'expandingIconTexture'):
        path = world.findtext(field) or ''
        require(find_texture(path) is not None, f'{name}.{field}: missing texture {path}.png')
for name in ('Interceptor', 'Coverage'):
    texture = find_texture(f'UI/Fortified/BVR/{name}')
    if texture is not None:
        check_png(texture, name)
    else:
        errors.append(f'Missing UI/Fortified/BVR/{name}.png')

tuning = get('FFF_BVRWorldTuning')
require(tuning.tag == 'Fortified.AirDefenseTuningDef', f'Wrong tuning def class {tuning.tag}')
for field in ('ticksPerTileAtSpeed1', 'interceptorSpeedMultiplier', 'stealthDetectFactor', 'scanIntervalTicks'):
    require(float(tuning.findtext(field, '0')) > 0, f'{field} must be positive')
require(float(tuning.findtext('minInterceptMarginTicks', '-1')) >= 0, 'Negative intercept margin')
require(float(tuning.findtext('airburstRadiusTiles', '-1')) >= 0, 'Negative airburst radius')

# DMSE content that uses the framework.
for radar, expected in [('DMSE_AcousticEarlyWarningRadar', 8), ('DMSE_PhaseArrayRadar', 12)]:
    node = get(radar).find("comps/li[@Class='Fortified.CompProperties_SearchRadar']")
    require(node is not None and float(node.findtext('worldDetectionRange', '0')) == expected,
            f'{radar}: world radius should be {expected}')

base = get('DMSE_MissileBase').find("comps/li[@Class='Fortified.WorldObjectCompProperties_AirDefense']")
require(base is not None, 'Missile base lacks abstract defense')
if base is not None:
    require('WorldObjectCompProperties_AirDefense' in classes, 'Abstract defense class missing')
    radar = get(base.findtext('fireControlDef'))
    require(radar.find("comps/li[@Class='Fortified.CompProperties_FireControlRadar']") is not None,
            'Invalid NPC fire-control reference')
    body = get(base.findtext('interceptorBody'))
    warhead = get(base.findtext('interceptorWarhead'))
    require(body.tag == 'Fortified.MissileBodyDef', 'NPC interceptor body has wrong type')
    require(warhead.tag == 'Fortified.MissilePartDef', 'NPC warhead has wrong type')
    require(warhead.find('warheadEffect') is not None, 'NPC warhead lacks effect')
    require(base.findtext('interceptorBody') in [n.text for n in warhead.findall('compatibleBodies/li')],
            'NPC warhead incompatible with body')

# FFF translations: keyed text used in code, and the injected world-object labels.
expected = None
languages = ['English', 'ChineseTraditional (繁體中文)', 'ChineseSimplified (简体中文)']
for language in languages:
    folder = fff_languages / language
    keys = {}
    for path in sorted((folder / 'Keyed').glob('*.xml')):
        for node in ET.parse(path).getroot():
            if node.tag in keys:
                errors.append(f'{language}: duplicate key {node.tag}')
            keys[node.tag] = node.text or ''
    world_keys = {k: v for k, v in keys.items() if k.startswith('FFF.BVR.World.')}
    require(not used - keys.keys(), f'{language}: missing keys {sorted(used - keys.keys())}')
    if expected is None:
        expected = world_keys
    else:
        require(world_keys.keys() == expected.keys(), f'{language}: different FFF.BVR.World key set '
                f'(missing {sorted(expected.keys() - world_keys.keys())}, extra {sorted(world_keys.keys() - expected.keys())})')
        for key in world_keys.keys() & expected.keys():
            require(sorted(re.findall(r'\{\d+\}', world_keys[key])) == sorted(re.findall(r'\{\d+\}', expected[key])),
                    f'{language}: mismatched format placeholders in {key}')
    if language != 'English':
        injected = set()
        for path in (folder / 'DefInjected/WorldObjectDef').glob('*.xml'):
            injected.update(node.tag for node in ET.parse(path).getroot())
        for name in world_objects:
            for field in ('label', 'description'):
                require(f'{name}.{field}' in injected, f'{language}: missing def translation {name}.{field}')

if errors:
    print('\n'.join('ERROR: ' + error for error in errors))
    sys.exit(1)
print(f'PASS: world BVR defs (DMSE + FFF), body/warhead references, radar ranges, 2 RGBA textures '
      f'and {len(used)} keys in {len(languages)} languages.')
