#!/usr/bin/env python3
"""Draft the FFF DefRenameDef type table for the DMSE -> FFF move (Python standard library only).

docs/轉移船與BVR移植FFF_可行性評估.md §7.4 is the source of the naming rules.

Usage:
    python gen_fff_rename_table.py [--out FILE] [--report FILE] [--downstream MOD_ROOT ...]

The script scans the C# types in the migration scope, classifies each one
(saved in savegames / referenced from XML / C# only), proposes a new name, and
writes a Fortified.DefRenameDef XML draft. Re-running it keeps every <newType>
that was edited by hand in an existing --out file, so the draft can be revised
and regenerated safely.
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / '.source' / 'DMSE'
NEW_NAMESPACE = 'Fortified'

# Migration scope (doc §3.1, §4, §5); everything else in DMSE stays.
SCOPE = [
    'CelestialTransfer', 'Platform', 'SkyFallerTurret', 'MissileFramework', 'MissileProjectile', 'MissileFx',
    'Scorer', 'EW', 'Artillery', 'MissileBase', 'SeaPlatform', 'Renameable', 'Integration', 'Common',
    'Utilities/CombatCompat.cs', 'Utilities/DMSE_Concepts.cs',
    'Building_MissileRack.cs', 'CompMissileLauncher.cs', 'CompMissileRackRenderer.cs', 'CompMissileBodyRenderer.cs',
]
# Hellfire impact craters are DMSE content (doc §2.1).
EXCLUDE = ['CelestialTransfer/Hellfire']

# Hand-picked names from doc §7.4. Rules below cover the rest.
EXPLICIT = {
    'CompPropertiesScorer': 'CompProperties_FueledMissileLauncher',
    'CompMissileLauncher_Scorer': 'CompMissileLauncher_Fueled',
    'MissileWorldObject': 'WorldObject_Missile',
    'ScorerProjectile_WorldObject': 'WorldObject_OutgoingMissile',
    'Alerts_Exist': 'Alert_StrikeMapExpiry',
    'Alerts_Pods': 'Alert_IncomingAirThreat',
    'Alerts_PlayerMissiles': 'Alert_PlayerMissilesInFlight',
    'Alerts_BVRStatus': 'Alert_AirDefenseStatus',
    'GameComponent_MissileEngage': 'GameComponent_StrikeMapTimers',
    'CompProperties_MissileBase': 'WorldObjectCompProperties_MissileBase',
    'CompProperties_WorldAirDefense': 'WorldObjectCompProperties_AirDefense',
    'BVRTargetProps': 'AirTargetExtension',
    'InterceptFxMaterial': 'InterceptFxMaterialExtension',
    'ModExtension_LaserDesignator': 'LaserDesignatorExtension',
    'ModExtension_TurretTraverse': 'TurretTraverseExtension',
    'MissileRackUtil': 'MissileRackUtility',
    'CompBVRDevice': 'CompAirDefenseDevice',
    'MapComponent_BVRCombat': 'MapComponent_AirDefense',
    'WorldComponent_BVRNetwork': 'WorldComponent_AirDefenseNetwork',
    'BVRWorldTuningDef': 'AirDefenseTuningDef',
    'BVRNode': 'AirDefenseNode',
    'BVRTarget': 'AirDefenseTarget',
    'BVRWave': 'AirDefenseWave',
    'BVRTrackState': 'AirTrackState',
    'BVRExternalTrack': 'ExternalAirTrack',
    'IBVRTrack': 'IAirDefenseTrack',
    'IBVRDefense': 'IAirDefense',
    'IBVRContactLabel': 'IAirContactLabel',
    'IBVRPayloadHolder': 'IAirPayloadHolder',
    'BVRDetection': 'AirDefenseDetectionUtility',
    'MissileBVRUtility': 'MissileAirDefenseUtility',
    'BVRChannelLedger': 'FireChannelLedger',
    'BVRInterceptSolver': 'InterceptSolver',
    'BVRWorldDrawer': 'AirDefenseWorldDrawer',
    'BVRDefenseEffector': 'AirDefenseEffector',
    'DMSE_Concepts': 'ConceptTeachUtility',
    # DefOf classes merge into FFF's existing catch-all FFF_DefOf (it already mixes JobDef, DutyDef, StatCategoryDef).
    'DMSE_MissileJobDefOf': 'FFF_DefOf',
    'DMSE_BallisticJobDefOf': 'FFF_DefOf',
    'DMSE_StrikeDefOf': 'FFF_DefOf',
    'DMSE_ManPortableSAMDefOf': 'FFF_DefOf',
    'DMSE_EWJobDefOf': 'FFF_DefOf',
    'DMSE_MissileCrewDutyDefOf': 'FFF_DefOf',
    'DMSE_KeyBindingDefOf': 'FFF_DefOf',
    'MissileStatCategoryDefOf': 'FFF_DefOf',
    'DMSE_FrameworkDefOf': 'FFF_DefOf',
}
MERGE_TARGETS = {'FFF_DefOf'}

# Framework Def renames (doc §3.3, §7.3): DMSE_ prefix -> FFF_ prefix.
DEF_OLD_PREFIX, DEF_NEW_PREFIX = 'DMSE_', 'FFF_'
# Content defaults that the framework code names directly. They become data (tuning Def fields) instead of
# being renamed into FFF (doc §3.3), so they are listed as TODO and left out of <renames>.
CONTENT_LEAKS = {'DMSE_Incoming_Moskit', 'DMSE_WorldObject_IncomingMissile', 'DMSE_Missile_Shot',
                 'DMSE_LaserDesignatorMark', 'DMSE_RadarNetwork', 'DMSE_Incoming_Loiter', 'DMSE_Incoming_LightMissile',
                 'DMSE_Incoming_AdvancedBallisticMissile', 'DMSE_Occultech_TerminalCruise'}
# Framework Defs that no C# names directly but framework XML needs (doc §4.4 sea platform), as (defName, XML tag).
# Player-buildable Defs (the deck TerrainDef DMSE_SeaPlatform and the ThingDef DMSE_SeaPlatformPillar, both in the
# Odyssey build menu without research) stay in DMSE as content: FFF Defs load for every FFF user, so FFF only ships
# abstract parents for them.
EXTRA_FRAMEWORK_DEFS = [
    ('DMSE_OpenAir', 'TerrainAffordanceDef'), ('DMSE_OpenAir', 'TerrainDef'),
    ('DMSE_SeaPlatformPillarBase', 'TerrainAffordanceDef'),
    ('DMSE_SeaPlatform', 'MapGeneratorDef'), ('DMSE_SeaPlatform', 'GenStepDef'),
    ('DMSE_SeaPlatformPillar', 'ShaderDef'), ('DMSE_SeaPlatformPillar', 'ShaderTypeDef'),
    ('DMSE_SeaPlatformSea', 'ShaderDef'), ('DMSE_SeaPlatformSea', 'ShaderTypeDef'),
]
# Files whose def references are dev-only and do not make a Def part of the framework.
DEF_SCAN_SKIP = re.compile(r'(^|/)DebugActions_[^/]*\.cs$|(^|/)Dev/')
# Kept as-is on purpose (doc §7.4 "不改的").
KEEP = {'MissileIncoming', 'TravelingObject', 'WorldObject_Transfer', 'WorldObject_ImpactGravship', 'MapComponent_Ship',
        'TransferTakeoffCutscene'}

# Types whose instances end up in savegames (base chain contains one of these).
SAVED_BASES = {
    'ThingComp', 'Thing', 'ThingWithComps', 'Building', 'Building_TurretGun', 'Building_Storage', 'Skyfaller', 'Projectile',
    'Filth', 'Mote', 'MapComponent', 'CustomMapComponent', 'WorldComponent', 'GameComponent', 'WorldObject', 'MapParent',
    'WorldObjectComp', 'IExposable', 'LordJob', 'LordToil', 'JobDriver', 'GameCondition', 'Hediff', 'HediffComp',
    'IThingHolder', 'ILoadReferenceable',
}

TYPE_RE = re.compile(
    r'\b(?:(?:public|internal|private|protected|static|abstract|sealed|partial|readonly|unsafe|new)\s+)*'
    r'(class|struct|interface|enum|record)\s+([A-Za-z_]\w*)(?:\s*<[^>{]*>)?\s*(?::\s*([^{;]+?))?\s*(?:where\b[^{]*)?\{')
NS_RE = re.compile(r'\bnamespace\s+([\w.]+)')


def strip_code(src):
    """Remove comments, strings and chars, keeping braces and line structure."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith('//', i):
            j = src.find('\n', i)
            i = n if j < 0 else j
        elif src.startswith('/*', i):
            j = src.find('*/', i + 2)
            i = n if j < 0 else j + 2
        elif c == '@' and i + 1 < n and src[i + 1] == '"':
            j = i + 2
            while j < n and not (src[j] == '"' and (j + 1 >= n or src[j + 1] != '"')):
                j += 2 if src[j] == '"' else 1
            out.append('""')
            i = j + 1
        elif c == '$' and i + 1 < n and src[i + 1] == '"' or c == '"':
            j = i + (2 if c == '$' else 1)
            while j < n and src[j] != '"':
                j += 2 if src[j] == '\\' else 1
            out.append('""')
            i = j + 1
        elif c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            out.append("' '")
            i = j + 1
        else:
            out.append(c)
            i += 1
    return ''.join(out)


def in_scope(rel):
    if any(rel == e or rel.startswith(e + '/') for e in EXCLUDE):
        return False
    return any(rel == s or rel.startswith(s + '/') for s in SCOPE)


def scan_types():
    """Return {full_name: info}; nested types use '+' like the CLR and savegames."""
    types = {}
    for path in sorted(SRC.rglob('*.cs')):
        rel = path.relative_to(SRC).as_posix()
        if '/obj/' in '/' + rel or '/bin/' in '/' + rel or not in_scope(rel):
            continue
        code = strip_code(path.read_text(encoding='utf-8-sig'))
        ns_match = NS_RE.search(code)
        namespace = ns_match.group(1) if ns_match else ''
        # Walk braces; a type match opens a scope that records the type name.
        stack = []  # entries: type name or None
        pos = 0
        matches = {m.end() - 1: m for m in TYPE_RE.finditer(code)}
        while pos < len(code):
            ch = code[pos]
            if ch == '{':
                m = matches.get(pos)
                if m:
                    outer = [s for s in stack if s]
                    name = m.group(2)
                    full = namespace + '.' + '+'.join(outer + [name]) if namespace else '+'.join(outer + [name])
                    bases = [re.sub(r'<.*', '', b).strip() for b in (m.group(3) or '').split(',') if b.strip()]
                    bases = [b.split('.')[-1] for b in bases]
                    types[full] = {'kind': m.group(1), 'name': name, 'namespace': namespace, 'outer': outer,
                                   'bases': bases, 'file': rel}
                    stack.append(name)
                else:
                    stack.append(None)
            elif ch == '}':
                if stack:
                    stack.pop()
            pos += 1
    return types


def base_closure(types, by_short, full):
    seen, todo = set(), list(types[full]['bases'])
    while todo:
        b = todo.pop()
        if b in seen:
            continue
        seen.add(b)
        for f in by_short.get(b, []):
            todo.extend(types[f]['bases'])
    return seen


def xml_references(roots):
    """Full type names written in Def/Patch XML (Class attributes and element text)."""
    refs = defaultdict(set)
    pat = re.compile(r'\b(DMSE(?:\.\w+)+)')
    for root in roots:
        for path in root.rglob('*.xml'):
            p = path.as_posix()
            if '/Languages/' in p or '/1.6/' in p or '/obj/' in p:
                continue
            try:
                text = path.read_text(encoding='utf-8-sig')
            except (UnicodeDecodeError, OSError):
                continue
            for m in pat.finditer(text):
                refs[m.group(1)].add(path.relative_to(root.parent).as_posix())
    return refs


def propose(info, closure):
    name = info['name']
    if name in KEEP:
        return name, ''
    if name in EXPLICIT:
        return EXPLICIT[name], 'doc §7.4'
    new, why = name, []
    if name.startswith('Alerts_'):
        new = 'Alert_' + name[len('Alerts_'):]
        why.append('Alert_ singular')
    if 'WorldObjectCompProperties' in closure and not new.startswith('WorldObjectCompProperties_'):
        new = 'WorldObjectCompProperties_' + re.sub(r'^CompProperties_?', '', new)
        why.append('WorldObjectCompProperties_ prefix')
    elif 'CompProperties' in closure and new.startswith('CompProperties') and not new.startswith('CompProperties_'):
        new = 'CompProperties_' + new[len('CompProperties'):]
        why.append('CompProperties_ prefix')
    if 'DefModExtension' in closure and not new.endswith('Extension'):
        core = re.sub(r'^ModExtension_', '', new)
        core = re.sub(r'Props$', '', core)
        new = core + 'Extension'
        why.append('Extension suffix')
    if re.search(r'Util$', new):
        new = new + 'ity'
        why.append('no abbreviation')
    if new.startswith('DMSE_') and new.endswith('DefOf'):
        why.append('TODO merge into FFF_*DefOf')
    elif new.startswith('DMSE_'):
        new = new[len('DMSE_'):]
        why.append('drop DMSE_ prefix')
    if 'BVR' in new and name not in EXPLICIT and not name.startswith('Patch_'):
        why.append('TODO contains BVR')
    return new, ', '.join(why)


def category(types, by_short, full, refs):
    closure = base_closure(types, by_short, full)
    old_full = full
    if closure & SAVED_BASES:
        return 'saved', closure
    if old_full in refs or types[full]['kind'] == 'class' and (closure & {
            'CompProperties', 'Def', 'DefModExtension', 'WorldObjectCompProperties', 'GenStep', 'SitePartWorker',
            'IncidentWorker', 'WorkGiver', 'WorkGiver_Scanner', 'PlaceWorker', 'SectionLayer', 'WorldGenStep',
            'WorldDrawLayer', 'MapDrawLayer', 'Alert', 'Alert_Critical', 'Designator', 'Verb', 'ITab', 'StatPart',
            'ScenPart', 'WeatherEvent', 'ThinkNode', 'JobGiver', 'LordToilData'}):
        return 'xml', closure
    return 'csharp', closure


def load_manual(path):
    """Keep hand edits: oldTypeName -> newType from an existing draft."""
    manual = {}
    if not path.exists():
        return manual
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        sys.exit(f'{path}: {exc}')
    for li in root.iter('li'):
        old, new = li.findtext('oldTypeName'), li.findtext('newType')
        if old and new:
            manual[old.strip()] = new.strip()
    return manual


def def_index(root):
    """defName -> [(XML tag, file), ...] for every Def in a mod's Defs (the same defName can exist per Def type)."""
    index = defaultdict(list)
    for folder in ('1.7/Defs', '1.6/Defs', 'Defs'):
        base = root / folder
        if not base.exists():
            continue
        for path in base.rglob('*.xml'):
            try:
                tree = ET.parse(path).getroot()
            except ET.ParseError:
                continue
            for node in tree:
                name = node.findtext('defName')
                if name:
                    index[name.strip()].append((node.tag, path.relative_to(root).as_posix()))
        if folder == '1.7/Defs' and index:
            break
    return index


# Only work givers and jobs: a SitePartDef/IncidentDef whose worker moves is still DMS content (the site itself).
WORKER_FIELDS = ('giverClass', 'driverClass')


def worker_defs(root):
    """(defName, tag, field, class) for Defs in 1.7/Defs whose worker/driver class is a DMSE type."""
    out = []
    for path in (root / '1.7' / 'Defs').rglob('*.xml'):
        try:
            tree = ET.parse(path).getroot()
        except ET.ParseError:
            continue
        for node in tree:
            name = node.findtext('defName')
            if not name:
                continue
            for field in WORKER_FIELDS:
                cls = node.findtext(field)
                if cls and cls.strip().startswith('DMSE.'):
                    out.append((name.strip(), node.tag, field, cls.strip()))
    return out


def tag_matches(tag, cs_type):
    """Does a Def's XML tag match the C# type that references it (JobDef, Fortified.X, DMSE.X)?"""
    return cs_type is None or tag.split('.')[-1] == cs_type.split('.')[-1]


def scan_def_refs():
    """Defs that the migrated code needs by name: {defName: (C# def type or None, set of files, how)}."""
    refs = {}

    def add(name, def_type, rel, how):
        entry = refs.setdefault(name, [def_type, set(), set()])
        if def_type and not entry[0]:
            entry[0] = def_type
        entry[1].add(rel)
        entry[2].add(how)

    dmse_defof = {}
    defof_src = (SRC / 'DefOf.cs').read_text(encoding='utf-8-sig')
    for m in re.finditer(r'public static (\w+)\s+(\w+)\s*;', defof_src):
        dmse_defof[m.group(2)] = m.group(1)

    for path in sorted(SRC.rglob('*.cs')):
        rel = path.relative_to(SRC).as_posix()
        if '/obj/' in '/' + rel or not in_scope(rel) or DEF_SCAN_SKIP.search(rel):
            continue
        raw = re.sub(r'//.*', '', path.read_text(encoding='utf-8-sig'))
        raw = re.sub(r'/\*.*?\*/', '', raw, flags=re.S)
        if '[DefOf]' in raw:
            for m in re.finditer(r'public static (\w+Def)\s+(' + DEF_OLD_PREFIX + r'\w+)\s*;', raw):
                add(m.group(2), m.group(1), rel, 'DefOf field')
        for m in re.finditer(r'\bDMSE_DefOf\.(\w+)', raw):
            add(m.group(1), dmse_defof.get(m.group(1)), rel, 'DMSE_DefOf')
        for m in re.finditer(r'DefDatabase<(\w+)>\.GetNamed(?:SilentFail)?\(\s*"(' + DEF_OLD_PREFIX + r'\w+)"', raw):
            add(m.group(2), m.group(1), rel, 'GetNamed')
        # Constants carry no type; DMSE_Concepts only names ConceptDefs (passed to Teach), and a few names
        # (e.g. DMSE_ElectronicWarfare) are both a ConceptDef and a ResearchProjectDef.
        const_type = 'ConceptDef' if rel.endswith('DMSE_Concepts.cs') else None
        for m in re.finditer(r'\bconst string \w+\s*=\s*"(' + DEF_OLD_PREFIX + r'\w+)"', raw):
            add(m.group(1), const_type, rel, 'const')
    return refs


def def_type_name(tag, type_rows):
    """XML tag of a Def -> the type name a DefRename.defType must use after the move."""
    if '.' in tag:
        return type_rows.get(tag, tag)
    for old, new in type_rows.items():
        if old.split('.')[-1] == tag:
            return new
    return tag


def load_manual_defs(path):
    manual = {}
    if not path.exists():
        return manual
    for li in ET.parse(path).getroot().iter('li'):
        old, new = li.findtext('oldDefName'), li.findtext('newDefName')
        if old and new:
            manual[old.strip()] = new.strip()
    return manual


def main():
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(errors='replace')
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--out', type=Path, default=ROOT / '.source' / 'Tools' / 'fff_type_renames.xml')
    ap.add_argument('--report', type=Path, default=None, help='also write a TSV with every scanned type')
    ap.add_argument('--def-files', type=Path, default=ROOT / '.source' / 'Tools' / 'fff_def_files.tsv',
                    help='TSV marking each 1.7/Defs file as framework / mixed / content')
    ap.add_argument('--downstream', type=Path, nargs='*', default=[], help='other mod roots whose XML counts as references')
    args = ap.parse_args()

    types = scan_types()
    by_short = defaultdict(list)
    for full, info in types.items():
        by_short[info['name']].append(full)
    refs = xml_references([ROOT / '1.7'] + [p.resolve() for p in args.downstream])
    manual = load_manual(args.out)

    rows, collisions = [], defaultdict(list)
    for full in sorted(types):
        info = types[full]
        cat, closure = category(types, by_short, full, refs)
        new_short, why = propose(info, closure)
        new_outer = [EXPLICIT.get(o, o) for o in info['outer']]
        new_full = NEW_NAMESPACE + '.' + '+'.join(new_outer + [new_short])
        if full in manual:
            if manual[full] != new_full:
                why = (why + ', ' if why else '') + 'hand-edited'
            new_full = manual[full]
        collisions[new_full].append(full)
        rows.append((cat, full, new_full, why, info['file'], sorted(refs.get(full, []))))

    dupes = {k: v for k, v in collisions.items() if len(v) > 1 and k.split('.')[-1] not in MERGE_TARGETS}
    for new_full, olds in sorted(dupes.items()):
        print(f'COLLISION {new_full} <- {", ".join(olds)}', file=sys.stderr)

    # Entries: saved and XML types always (namespace changes); C#-only types only when the short name changes.
    order = {'saved': 0, 'xml': 1, 'csharp': 2}
    entries = [r for r in rows if r[0] != 'csharp' or r[1].split('.')[-1] != r[2].split('.')[-1]]
    entries = [r for r in entries if r[1] != r[2]]
    entries.sort(key=lambda r: (order[r[0]], r[1]))

    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<!-- Draft generated by .source/Tools/gen_fff_rename_table.py. Edit <newType> by hand; regenerating keeps the edits.',
        '     saved = appears in savegames (required); xml = referenced from Def XML; csharp = renamed C#-only type (reference for downstream code).',
        '     Entries marked TODO need a decision before the table is used. -->',
        '<Defs>',
        '  <Fortified.DefRenameDef>',
        '    <defName>DMSE_TypeRenames</defName>',
        '    <typeRenames>',
    ]
    current = None
    for cat, old, new, why, file, xml_files in entries:
        if cat != current:
            lines.append(f'      <!-- ===== {cat} ===== -->')
            current = cat
        note = f'{file}' + (f'; {why}' if why else '') + (f'; XML: {len(xml_files)} file(s)' if xml_files else '')
        lines.append(f'      <li><oldTypeName>{old}</oldTypeName><newType>{new}</newType></li> <!-- {note} -->')
    lines.append('    </typeRenames>')

    # ---- framework Def renames ----
    type_map = {r[1]: r[2] for r in rows}
    index = def_index(ROOT)
    fff_index = def_index(ROOT.parent / '_Fortified-Framework') if (ROOT.parent / '_Fortified-Framework').exists() else {}
    manual_defs = load_manual_defs(args.out)
    def_rows, def_todo = [], []
    wanted = {name: (cs_type, files, how) for name, (cs_type, files, how) in scan_def_refs().items()}
    for name, tag in EXTRA_FRAMEWORK_DEFS:
        wanted.setdefault(name + '\0' + tag, (tag, {'framework XML (doc §4.4)'}, {'extra'}))
    # Defs whose worker class moves to FFF belong to the framework too (WorkGiverDef for a moved WorkGiver, ...).
    for name, tag, field, cls in worker_defs(ROOT):
        if cls in type_map and name.startswith(DEF_OLD_PREFIX) and name not in CONTENT_LEAKS:
            entry = wanted.get(name)
            if entry is None:
                wanted[name] = (tag, {f'{field} {cls.split(".")[-1]}'}, {'worker'})
            elif entry[0] and entry[0].split('.')[-1] != tag.split('.')[-1]:
                # Same defName, another Def type: list it under its own type.
                wanted[name + '\0' + tag] = (tag, {f'{field} {cls.split(".")[-1]}'}, {'worker'})
    for key, (cs_type, files, how) in sorted(wanted.items()):
        name = key.split('\0')[0]
        files_note = ', '.join(sorted(f.split('/')[-1] for f in files))
        if name in CONTENT_LEAKS:
            def_todo.append(f'{name}: content named in framework code ({files_note}); make it a tuning/Def field (doc §3.3)')
            continue
        if not name.startswith(DEF_OLD_PREFIX):
            def_todo.append(f'{name}: {files_note} names a Def of another mod (DMS core); make it data (doc §3.3)')
            continue
        matches = [(tag, f) for tag, f in index.get(name, []) if tag_matches(tag, cs_type)]
        if not matches:
            if how != {'const'}:  # constants that are not defNames (slate keys, config folders) are fine
                def_todo.append(f'{name}: referenced by {files_note} but no matching Def in 1.7/Defs')
            continue
        if cs_type is None and how != {'extra'} and len(matches) > 1:
            def_todo.append(f'{name}: {files_note} names it without a type and {len(matches)} Def types use that name; '
                            'give the code a typed reference')
            continue
        new_name = manual_defs.get(name, DEF_NEW_PREFIX + name[len(DEF_OLD_PREFIX):])
        if new_name in fff_index:
            def_todo.append(f'{name} -> {new_name}: FFF already has a Def with that name')
        for tag, def_file in matches:
            def_rows.append((def_type_name(tag, type_map), name, new_name, def_file, files_note))
    lines.append('    <renames>')
    for def_type, old, new, def_file, files_note in sorted(def_rows):
        lines.append(f'      <li><defType>{def_type}</defType><oldDefName>{old}</oldDefName><newDefName>{new}</newDefName></li>'
                     f' <!-- {def_file}; used by {files_note} -->')
    lines.append('    </renames>')

    # ---- which Def files move to FFF ----
    # framework = every Def in the file is in <renames>; mixed = some; content = none. Abstract parents are not counted.
    framework_keys = {(r[1], r[0].split('.')[-1]) for r in def_rows}
    per_file = defaultdict(lambda: [0, 0, []])
    for name, def_entries in index.items():
        for tag, def_file in def_entries:
            def_type = def_type_name(tag, type_map).split('.')[-1]
            stats = per_file[def_file]
            stats[1] += 1
            if (name, def_type) in framework_keys:
                stats[0] += 1
                stats[2].append(name)
    with args.def_files.open('w', encoding='utf-8') as f:
        f.write('kind\tframework_defs\ttotal_defs\tfile\tframework_def_names\n')
        for def_file, (fw, total, names) in sorted(per_file.items(), key=lambda kv: (kv[1][0] == 0, kv[1][0] != kv[1][1], kv[0])):
            kind = 'framework' if fw == total else 'mixed' if fw else 'content'
            f.write(f'{kind}\t{fw}\t{total}\t{def_file}\t{", ".join(sorted(names))}\n')
    kinds = defaultdict(int)
    for fw, total, _ in per_file.values():
        kinds['framework' if fw == total else 'mixed' if fw else 'content'] += 1
    print(f'Def files: framework {kinds["framework"]}, mixed {kinds["mixed"]}, content {kinds["content"]} -> {args.def_files}')
    lines += ['  </Fortified.DefRenameDef>', '</Defs>', '']
    args.out.write_text('\n'.join(lines), encoding='utf-8')

    if args.report:
        with args.report.open('w', encoding='utf-8') as f:
            f.write('category\told\tnew\tnote\tfile\txml_refs\n')
            for cat, old, new, why, file, xml_files in sorted(rows, key=lambda r: (order[r[0]], r[1])):
                f.write(f'{cat}\t{old}\t{new}\t{why}\t{file}\t{len(xml_files)}\n')

    counts = defaultdict(int)
    for r in rows:
        counts[r[0]] += 1
    renamed = sum(1 for r in rows if r[1].split('.')[-1] != r[2].split('.')[-1])
    todo_rows = [r for r in rows if 'TODO' in r[3]]
    for r in todo_rows:
        print(f'TODO {r[1]}: {r[3]}', file=sys.stderr)
    for t in def_todo:
        print(f'TODO def {t}', file=sys.stderr)
    todo = len(todo_rows) + len(def_todo)
    print(f'framework Def renames: {len(def_rows)}')
    print(f'{len(rows)} types: saved {counts["saved"]}, xml {counts["xml"]}, csharp {counts["csharp"]}; '
          f'short name changes {renamed}; entries {len(entries)}; TODO {todo}; collisions {len(dupes)}')
    print(f'wrote {args.out}')
    return 1 if dupes else 0


if __name__ == '__main__':
    sys.exit(main())
