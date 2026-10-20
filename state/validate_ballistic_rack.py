"""One-off validation for the ballistic missile launch rack (def, comps, refs, texture, lang)."""
import glob
import re
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors = []


def fail(msg):
    errors.append(msg)


# ---- collect every def in 1.7/Defs -----------------------------------------
defs = {}
for path in (ROOT / '1.7/Defs').rglob('*.xml'):
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        fail(f'{path.name}: {exc}')
        continue
    for node in root:
        name = node.findtext('defName')
        if name:
            defs.setdefault(name, []).append((node, path))

rack_defs = defs.get('DMSE_Building_BallisticRack', [])
if len(rack_defs) != 1:
    fail(f'DMSE_Building_BallisticRack: expected exactly 1 def, found {len(rack_defs)}')
rack, rack_path = rack_defs[0]

# ---- shape ---------------------------------------------------------------
if rack.tag != 'ThingDef':
    fail(f'wrong def class: {rack.tag}')
if rack.get('ParentName') != 'DMSE_VerticalLauncherBase':
    fail('must inherit DMSE_VerticalLauncherBase (vertical launch, no gravship)')
if rack.findtext('size') != '(2,2)':
    fail(f'size should be (2,2), got {rack.findtext("size")}')

# ---- comp classes exist in the C# sources ---------------------------------
sources = ''
for path in (ROOT / '.source/DMSE').rglob('*.cs'):
    if 'obj' in path.parts or 'bin' in path.parts:
        continue
    sources += path.read_text(encoding='utf-8-sig')
classes = set(re.findall(r'\bclass\s+(\w+)', sources))
for comp in rack.findall('comps/li'):
    cls = comp.get('Class')
    if cls and cls.startswith('DMSE.'):
        simple = cls.split('.')[-1]
        if simple not in classes:
            fail(f'comp class not found in sources: {cls}')

# ---- def references ------------------------------------------------------
refs = []
for tag in ('skyfaller', 'skyfallerIncoming', 'worldObjectDef', 'defaultBody'):
    for node in rack.iter(tag):
        if node.text:
            refs.append((tag, node.text.strip()))
for li in rack.find('researchPrerequisites') if rack.find('researchPrerequisites') is not None else []:
    refs.append(('research', (li.text or '').strip()))
for li in rack.find('costList') if rack.find('costList') is not None else []:
    refs.append(('cost', li.tag))
refs.append(('statBases', 'MaxHitPoints'))
# vanilla resources have no def in this mod's XML, so they are allowed by name
VANILLA = {'Steel', 'ComponentIndustrial', 'ComponentSpacer', 'ComponentStamped',
           'Plasteel', 'Chemfuel', 'Silver', 'Gold', 'Uranium'}
for kind, name in refs:
    if not name:
        continue
    if kind in ('statBases',) or (kind == 'cost' and name in VANILLA):
        continue
    if name not in defs:
        fail(f'{kind} ref not found: {name}')

# ---- texture -------------------------------------------------------------
tex = ROOT / '1.7/Textures' / ((rack.findtext('graphicData/texPath') or '') + '.png')
if not tex.is_file():
    fail(f'missing texture: {tex}')
else:
    data = tex.read_bytes()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        fail('texture is not a PNG')
    else:
        width, height, depth, color = struct.unpack('>IIBB', data[16:26])
        if color != 6 or depth != 8:
            fail(f'texture must be 8-bit RGBA, got depth={depth} color={color}')
        if (width, height) != (512, 512):
            fail(f'texture should be 512x512, got {width}x{height}')

# ---- language ------------------------------------------------------------
for lang in ('ChineseTraditional (繁體中文)', 'ChineseSimplified (简体中文)'):
    path = ROOT / '1.7/Languages' / lang / 'DefInjected/ThingDef/DMSE_BallisticRack.xml'
    if not path.is_file():
        fail(f'missing language file: {path}')
        continue
    keys = {n.tag for n in ET.parse(path).getroot()}
    need = {'DMSE_Building_BallisticRack.label', 'DMSE_Building_BallisticRack.description'}
    if keys != need:
        fail(f'{lang}: keys {sorted(keys)} != {sorted(need)}')

if errors:
    print('\n'.join('ERROR: ' + e for e in errors))
    sys.exit(1)
print('PASS: ballistic rack def, comp classes, def references, 512x512 RGBA texture, 2 language files.')
