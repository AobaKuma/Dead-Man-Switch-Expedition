"""Mirror the DMSE infrastructure defs that "Expedition: Early Warfare" needs when DMSE is not active.

Early Warfare (sibling folder _Dead-Man-Switch-Expedition-EarlyWarfare) loads 1.7/Standalone only when DMSE is
not active. That folder holds byte-for-byte copies of the DMSE defs listed in MIRRORED_DEFS (same defNames, so
there is never a duplicate: exactly one of the two copies loads), plus the textures, sounds and DefInjected
translations they use. Run this after changing any of those defs in DMSE:

    python .source/Tools/sync_early_standalone.py          # rewrite the mirror
    python .source/Tools/sync_early_standalone.py --check  # exit 1 if the mirror is stale

Adjustments for running without DMSE (research gates, list entries pointing at DMSE-only defs, DMSE.dll classes)
are XML patches in Early Warfare's 1.7/Standalone/Patches, not edits to the mirrored files.
DMSE CE patch operations that target only mirrored defs are copied to 1.7/Standalone/CE/Patches.
"""
import copy
import re
import shutil
import sys
from pathlib import Path

from lxml import etree

ROOT = Path(__file__).resolve().parents[2]
DMSE = ROOT / '1.7'
EARLY = ROOT.parent / '_Dead-Man-Switch-Expedition-EarlyWarfare'
OUT = EARLY / '1.7' / 'Standalone'

# Hard dependencies of Early Warfare on DMSE (parents and required single-value references), found 2026-11-27.
# Add a def here when Early Warfare starts depending on another DMSE def.
MIRRORED_DEFS = [
    'DMSE_BVRCategory',
    'DMSE_ResourceBase',
    'DMSE_BaseBuilding', 'DMSE_BaseDecoration', 'DMSE_BaseStructuralFrame',
    'DMSE_FieldStructure_SmallFrame', 'DMSE_FieldStructure_MediumFrame',
    'DMSE_TiltLauncherBase', 'DMSE_Building_LoiteringLauncher', 'DMSE_Incoming_Loiter', 'DMSE_WorldObject_Loiter',
    'DMSE_MissileBody_Loiter',
    'DMSE_MissileRacks', 'DMSE_LoiteringRack',
    'DMSE_MissileLaunchProjectileBase', 'DMSE_MissileIncomingProjectileBase',
    'DMSE_Launch_Loiter', 'DMSE_Launch_LegacyInterceptor',
    'DMSE_LaunchFx_Pulsejet', 'DMSE_LaunchFx_SolidBooster',
    'DMSE_Missile_Flight',
    'DMSE_WorldObject_IncomingMissile',
    'DMSE_WorldObject_ArtilleryStrike',   # heavy mortar (moved to Early Warfare 2026-11-27)
    # ballistic launch tower, V2 and the strike planning table: DMSE content that Early Warfare also needs on its own
    'DMSE_Building_BallisticRack', 'DMSE_StrikePlanningTable',
    'DMSE_MissileBody_LegacyBallistic', 'DMSE_Launch_LegacyBallistic', 'DMSE_Incoming_LegacyBallisticMissile',
    'DMSE_Namer_LegacyBallistic', 'DMSE_NamerPack_LegacyBallistic',
    'DMSE_LegacyBallistic_Warhead_Amatol', 'DMSE_LegacyBallistic_Warhead_Trialen',
    # tower platform defaults and V2's launch FX
    'DMSE_VerticalLauncherBase', 'DMSE_WorldObject_BallisticMissile',
    'DMSE_Launch_AdvancedBallistic', 'DMSE_Incoming_AdvancedBallisticMissile',
    'DMSE_LaunchFx_SiloColdLaunch', 'DMSE_LaunchFx_LiquidBallistic',
    # shared ballistic parts V2 can use without DMSE research (gated ones stay DMSE-only)
    'DMSE_Ballistic_Guidance_Inertial', 'DMSE_Ballistic_Payload_None', 'DMSE_Ballistic_Payload_Cluster',
    'DMSE_Ballistic_Payload_BunkerBuster', 'DMSE_Ballistic_Warhead_Airburst', 'DMSE_Ballistic_Warhead_TungstenPenetrator',
    'DMSE_Ballistic_Warhead_ClusterBomblet', 'DMSE_Bullet_AirburstFrag',
    'DMSE_MissileBase_GS_ScatterLumps', 'DMSE_MissileBase_GS_ScatterSlag',
    'DMSE_FieldLaunchSite_GS_RackFill', 'DMSE_FieldLaunchSite_GS_Roads',
]

HEADER = ('<?xml version="1.0" encoding="utf-8"?>\n<Defs>\n\n'
          '  <!-- Mirrored from DMSE 1.7/Defs/{rel} by DMSE .source/Tools/sync_early_standalone.py. Do not edit by hand.\n'
          '       Loaded only when DMSE is not active (see LoadFolders.xml).\n'
          '       由 DMSE 的 .source/Tools/sync_early_standalone.py 自 1.7/Defs/{rel} 複製，請勿手改；只在 DMSE 未啟用時載入。 -->\n\n')
TEX_SUFFIXES = ['', '_north', '_east', '_south', '_west', '_northm', '_eastm', '_southm', '_westm', '_m']


def read(path):
    return path.read_text(encoding='utf-8-sig')


def find_block(lines, name):
    by_def = re.compile(r'<defName>\s*%s\s*</defName>' % re.escape(name))
    by_name = re.compile(r'^(\s*)<([A-Za-z0-9_.]+)[^>]*\sName="%s"' % re.escape(name))
    hits = [i for i, l in enumerate(lines) if by_def.search(l) or by_name.search(l)]
    if len(hits) != 1:
        return None
    i = hits[0]
    m = by_name.search(lines[i])
    if m:
        start, indent, tag = i, m.group(1), m.group(2)
    else:
        depth = len(lines[i]) - len(lines[i].lstrip())
        start = i - 1
        while True:
            m = re.match(r'^(\s*)<([A-Za-z0-9_.]+)(\s|>|$)', lines[start])
            if m and len(m.group(1)) < depth and not lines[start].lstrip().startswith('<!--'):
                break
            start -= 1
        indent, tag = m.group(1), m.group(2)
    end = start
    if not lines[start].rstrip().endswith('/>'):
        while not re.match(r'^%s</%s>' % (re.escape(indent), re.escape(tag)), lines[end]):
            end += 1
    c = start - 1
    if c >= 0 and lines[c].strip().endswith('-->'):
        while '<!--' not in lines[c]:
            c -= 1
        start = c
    return start, end


def build():
    """Return {relative output path: bytes or source Path}."""
    files = {}
    found = {}
    for path in sorted((DMSE / 'Defs').rglob('*.xml')):
        lines = read(path).split('\n')
        for name in MIRRORED_DEFS:
            block = find_block(lines, name)
            if block:
                found.setdefault(name, []).append((path.relative_to(DMSE / 'Defs').as_posix(), block, lines))
    problems = [f'{n}: found {len(found.get(n, []))} times' for n in MIRRORED_DEFS if len(found.get(n, [])) != 1]
    if problems:
        sys.exit('ERROR: ' + '; '.join(problems))

    by_file = {}
    for name in MIRRORED_DEFS:
        rel, (s, e), lines = found[name][0]
        by_file.setdefault(rel, []).append((s, e, lines))
    mirrored_text = []
    for rel, blocks in sorted(by_file.items()):
        blocks.sort(key=lambda b: b[0])
        body = '\n\n'.join('\n'.join(lines[s:e + 1]) for s, e, lines in blocks)
        mirrored_text.append(body)
        files[f'Defs/{rel}'] = (HEADER.format(rel=rel) + body + '\n\n</Defs>\n').encode('utf-8-sig')
    text = '\n'.join(mirrored_text)
    # Early Warfare's own defs may also use DMSE textures (e.g. shared turret bases); mirror those too.
    early_text = '\n'.join(read(p) for p in sorted((EARLY / '1.7' / 'Defs').rglob('*.xml')))

    def in_early(p):
        tex = EARLY / '1.7' / 'Textures' / p
        return tex.is_dir() or any((tex.parent / (tex.name + s + '.png')).is_file() for s in TEX_SUFFIXES)

    # textures and sounds referenced by the mirrored defs and by Early Warfare's defs
    path_re = r'>\s*([A-Za-z0-9_\-]+(?:/[A-Za-z0-9_\-]+)+)\s*<'
    paths = set(re.findall(path_re, text)) | {p for p in re.findall(path_re, early_text) if not in_early(p)}
    for p in sorted(paths):
        tex = DMSE / 'Textures' / p
        candidates = [tex.parent / (tex.name + s + '.png') for s in TEX_SUFFIXES]
        # a folder only matters for Graphic_Random-style paths that have no file of their own
        if tex.is_dir() and not any(c.is_file() for c in candidates):
            candidates += sorted(tex.iterdir())
        for c in candidates:
            if c.is_file():
                files['Textures/' + c.relative_to(DMSE / 'Textures').as_posix()] = c
        snd = ROOT / 'Sounds' / p
        for c in [snd.parent / (snd.name + ext) for ext in ('.ogg', '.wav', '.mp3')] + (sorted(snd.iterdir()) if snd.is_dir() else []):
            if c.is_file():
                files['Sounds/' + c.relative_to(ROOT / 'Sounds').as_posix()] = c

    # CE patch operations that target only mirrored defs (loaded with CE active and DMSE not active)
    ce_ops = []
    names_in = re.compile(r'defName\s*=\s*"([^"]+)"')
    for path in sorted((DMSE / 'CE' / 'Patches').rglob('*.xml')):
        tree = etree.parse(str(path))
        for op in tree.iter():
            if not isinstance(op.tag, str) or op.find('xpath') is None:
                continue
            refs = set(names_in.findall(op.findtext('xpath') or ''))
            hit = refs & set(MIRRORED_DEFS)
            if not hit:
                continue
            if refs - set(MIRRORED_DEFS):
                sys.exit(f'ERROR: {path.name}:{op.sourceline} patches mirrored {sorted(hit)} together with '
                         f'{sorted(refs - set(MIRRORED_DEFS))}; split that xpath in DMSE first')
            prev = op.getprevious()
            if prev is not None and not isinstance(prev.tag, str):
                ce_ops.append('\t\t\t' + etree.tostring(prev, encoding='unicode').strip())
            op = copy.deepcopy(op)
            op.tag = 'li'
            op.tail = None
            ce_ops.append(f'\t\t\t<!-- {path.relative_to(DMSE / "CE").as_posix()} -->')
            ce_ops.append('\t\t\t' + etree.tostring(op, encoding='unicode').strip())
    if ce_ops:
        files['CE/Patches/DMSE_Mirrored_CE.xml'] = (
            '<?xml version="1.0" encoding="utf-8"?>\n<Patch>\n\n'
            '\t<!-- Mirrored from DMSE 1.7/CE/Patches by .source/Tools/sync_early_standalone.py. Do not edit by hand.\n'
            '\t     Loaded only when Combat Extended is active and DMSE is not. -->\n'
            '\t<Operation Class="PatchOperationSequence">\n\t\t<operations>\n'
            + '\n'.join(ce_ops) + '\n\t\t</operations>\n\t</Operation>\n\n</Patch>\n').encode('utf-8-sig')

    # DefInjected translations of the mirrored defs (one file per language and def type)
    key = re.compile(r'^\s*<([A-Za-z0-9_]+)\.')
    names = set(MIRRORED_DEFS)
    for lang_dir in sorted((DMSE / 'Languages').iterdir()):
        for type_dir in sorted((lang_dir / 'DefInjected').glob('*')) if (lang_dir / 'DefInjected').is_dir() else []:
            picked = []
            for path in sorted(type_dir.glob('*.xml')):
                lines = read(path).split('\n')
                i = 0
                while i < len(lines):
                    m = key.match(lines[i])
                    if m and m.group(1) in names:
                        if i > 0 and lines[i - 1].strip().startswith('<!-- EN:'):
                            picked.append(lines[i - 1])
                        tag = lines[i].strip()[1:].split('>')[0]
                        j = i
                        while '</' + tag + '>' not in lines[j]:
                            j += 1
                        picked.extend(lines[i:j + 1])
                        i = j + 1
                        continue
                    i += 1
            if picked:
                rel = f'Languages/{lang_dir.name}/DefInjected/{type_dir.name}/DMSE_Mirrored.xml'
                files[rel] = ('<?xml version="1.0" encoding="utf-8"?>\n<LanguageData>\n\n'
                              '  <!-- Mirrored from DMSE by .source/Tools/sync_early_standalone.py. Do not edit by hand. -->\n'
                              + '\n'.join(picked) + '\n\n</LanguageData>\n').encode('utf-8-sig')
    return files


def content(v):
    return v.read_bytes() if isinstance(v, Path) else v


def main():
    check = '--check' in sys.argv
    if not EARLY.is_dir():
        sys.exit(f'ERROR: Early Warfare not found at {EARLY}')
    want = build()
    managed = ('Defs', 'Textures', 'Sounds', 'Languages', 'CE')
    have = {p.relative_to(OUT).as_posix() for d in managed if (OUT / d).is_dir()
            for p in (OUT / d).rglob('*') if p.is_file()}
    stale = sorted(p for p in want if not (OUT / p).is_file() or (OUT / p).read_bytes() != content(want[p]))
    extra = sorted(have - set(want))
    if check:
        for p in stale:
            print('STALE', p)
        for p in extra:
            print('EXTRA', p)
        print(f'{len(want)} mirrored files, {len(stale)} stale, {len(extra)} extra')
        sys.exit(1 if stale or extra else 0)
    for p in stale:
        dst = OUT / p
        dst.parent.mkdir(parents=True, exist_ok=True)
        v = want[p]
        if isinstance(v, Path):
            shutil.copy2(v, dst)
        else:
            dst.write_bytes(v)
        print('write', p)
    for p in extra:
        (OUT / p).unlink()
        print('delete', p)
    print(f'{len(want)} mirrored files ({len(stale)} written, {len(extra)} deleted)')


if __name__ == '__main__':
    main()
