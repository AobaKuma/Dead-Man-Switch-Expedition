#!/usr/bin/env python3
"""Validate the world-BVR content without starting RimWorld (Python standard library only)."""
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
errors = []
def require(ok, message):
    if not ok:
        errors.append(message)

defs = {}
for path in (root / '1.7/Defs').rglob('*.xml'):
    try:
        for node in ET.parse(path).getroot():
            name = node.findtext('defName')
            if name:
                defs.setdefault(name, []).append((node, path))
    except ET.ParseError as exc:
        errors.append(f'{path.relative_to(root)}: {exc}')

def get(name):
    entries = defs.get(name, [])
    require(len(entries) == 1, f'{name}: expected one definition, found {len(entries)}')
    return entries[0][0] if entries else ET.Element('missing')

classes = set()
for path in (root / '.source/DMSE').rglob('*.cs'):
    if 'obj' in path.parts or 'bin' in path.parts:
        continue
    classes.update(re.findall(r'\bclass\s+(\w+)', path.read_text(encoding='utf-8-sig')))

world = get('DMSE_Interceptor')
require(world.findtext('worldObjectClass') == 'DMSE.WorldObject_Interceptor', 'Wrong interceptor worldObjectClass')
require('WorldObject_Interceptor' in classes, 'Interceptor class missing')
for field in ('texture', 'expandingIconTexture'):
    texture = root / '1.7/Textures' / ((world.findtext(field) or '') + '.png')
    require(texture.is_file(), f'{field}: missing texture {texture}')
for name in ('Interceptor', 'Coverage'):
    texture = root / f'1.7/Textures/UI/DMSE/BVR/{name}.png'
    if texture.is_file():
        data = texture.read_bytes()
        require(data[:8] == b'\x89PNG\r\n\x1a\n', f'{name}: invalid PNG')
        width, height, depth, color = struct.unpack('>IIBB', data[16:26])
        require(width > 0 and height > 0 and depth == 8 and color == 6, f'{name}: expected 8-bit RGBA PNG')
    else:
        errors.append(f'Missing {texture}')

tuning = get('DMSE_BVRWorldTuning')
require(tuning.tag == 'DMSE.BVRWorldTuningDef', 'Wrong tuning def class')
for field in ('ticksPerTileAtSpeed1', 'interceptorSpeedMultiplier', 'stealthDetectFactor', 'scanIntervalTicks'):
    require(float(tuning.findtext(field, '0')) > 0, f'{field} must be positive')
require(float(tuning.findtext('minInterceptMarginTicks', '-1')) >= 0, 'Negative intercept margin')
require(float(tuning.findtext('airburstRadiusTiles', '-1')) >= 0, 'Negative airburst radius')
for radar, expected in [('DMSE_AcousticEarlyWarningRadar', 8), ('DMSE_PhaseArrayRadar', 12)]:
    node = get(radar).find("comps/li[@Class='DMSE.CompProperties_SearchRadar']")
    require(node is not None and float(node.findtext('worldDetectionRange', '0')) == expected, f'{radar}: missing world radius')

base = get('DMSE_MissileBase').find("comps/li[@Class='DMSE.CompProperties_WorldAirDefense']")
require(base is not None, 'Missile base lacks abstract defense')
if base is not None:
    require('CompProperties_WorldAirDefense' in classes, 'Abstract defense class missing')
    radar = get(base.findtext('fireControlDef'))
    require(radar.find("comps/li[@Class='DMSE.CompProperties_FireControlRadar']") is not None, 'Invalid NPC fire-control reference')
    body = get(base.findtext('interceptorBody'))
    warhead = get(base.findtext('interceptorWarhead'))
    require(body.tag == 'DMSE.MissileBodyDef', 'NPC interceptor body has wrong type')
    require(warhead.tag == 'DMSE.MissilePartDef', 'NPC warhead has wrong type')
    require(warhead.find('warheadEffect') is not None, 'NPC warhead lacks effect')
    require(base.findtext('interceptorBody') in [n.text for n in warhead.findall('compatibleBodies/li')], 'NPC warhead incompatible with body')

used = set()
for path in (root / '.source/DMSE').rglob('*.cs'):
    if 'obj' not in path.parts and 'bin' not in path.parts:
        used.update(re.findall(r'"(DMSE\.BVR\.World\.[\w.]+)"', path.read_text(encoding='utf-8-sig')))
expected = None
for language in ['English', 'ChineseTraditional (繁體中文)', 'ChineseSimplified (简体中文)']:
    folder = root / '1.7/Languages' / language
    translations = ET.parse(folder / 'Keyed/DMSE_BVRWorld.xml').getroot()
    keys = {node.tag: node.text or '' for node in translations}
    require(len(keys) == len(translations), f'{language}: duplicate translation keys')
    require(not used - keys.keys(), f'{language}: missing keys {sorted(used - keys.keys())}')
    if expected is None:
        expected = keys
    else:
        require(keys.keys() == expected.keys(), f'{language}: different translation key set')
        for key in keys.keys() & expected.keys():
            require(sorted(re.findall(r'\{\d+\}', keys[key])) == sorted(re.findall(r'\{\d+\}', expected[key])),
                    f'{language}: mismatched format placeholders in {key}')
    injected = ET.parse(folder / 'DefInjected/WorldObjectDef/DMSE_BVRWorld.xml').getroot()
    require({n.tag for n in injected} == {'DMSE_Interceptor.label', 'DMSE_Interceptor.description'}, f'{language}: missing def translation')

if errors:
    print('\n'.join('ERROR: ' + error for error in errors))
    sys.exit(1)
print(f'PASS: world BVR defs, body/warhead references, radar ranges, 2 RGBA textures and {len(used)} keys in 3 languages.')
