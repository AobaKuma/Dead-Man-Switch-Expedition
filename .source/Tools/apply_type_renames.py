#!/usr/bin/env python3
"""Rewrite a mod's XML with a Fortified.DefRenameDef table (Python standard library only).

Usage:
    python apply_type_renames.py TABLE.xml MOD_ROOT [MOD_ROOT ...] [--write] [--folders 1.7 ...]

Def XML resolves Class attributes before DefRenameDef is loaded, so old type names
in XML are never redirected at runtime (docs/轉移船與BVR移植FFF_可行性評估.md §7.2).
This script rewrites them, plus renamed defNames, using the same table that the
game uses for savegames:

  typeRenames  Class="Old.Type", <thingClass>Old.Type</thingClass>, <li>Old.Type</li>, ...
               (whole attribute value or whole element text only)
  renames      <defName>Old</defName>, references whose whole text is Old,
               defName="Old" / defName='Old' inside patch XPaths, and DefInjected keys
               <Old.label> in Languages/*/DefInjected/<defType>/.

Without --write it only reports what would change. C# files are not edited; their
remaining references to old type names are counted so they can be fixed in the IDE.
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path


def load_table(path):
    root = ET.parse(path).getroot()
    types, defs = {}, defaultdict(dict)
    for li in root.iter('li'):
        old_type, new_type = li.findtext('oldTypeName'), li.findtext('newType')
        if old_type and new_type:
            types[old_type.strip()] = new_type.strip()
        old_def, new_def, def_type = li.findtext('oldDefName'), li.findtext('newDefName'), li.findtext('defType')
        if old_def and new_def and def_type:
            defs[def_type.strip()][old_def.strip()] = new_def.strip()
    return types, defs


def mod_def_tags(mods, types):
    """defName -> set of Def type short names that use it in the given mods (tags mapped through typeRenames)."""
    tags = defaultdict(set)
    for mod in mods:
        for path in mod.rglob('*.xml'):
            p = path.as_posix()
            if '/Defs/' not in p or '/1.6/' in p:
                continue
            try:
                root = ET.parse(path).getroot()
            except (ET.ParseError, UnicodeDecodeError):
                continue
            for node in root:
                name = node.findtext('defName')
                if name:
                    tag = types.get(node.tag, node.tag)
                    tags[name.strip()].add(tag.split('.')[-1])
    return tags


def chained(mapping):
    """Old names that are also new names would chain at runtime (A->B, B->C); the game looks up once."""
    return sorted(k for k, v in mapping.items() if v in mapping)


def rewrite_xml(text, types, def_names):
    changes = []

    def sub(pattern, repl_fn, s):
        return re.sub(pattern, repl_fn, s)

    # Whole attribute values: Class="Old.Type"
    def attr(m):
        quote, value = m.group(1), m.group(2)
        if value in types:
            changes.append(f'Class="{value}" -> {types[value]}')
            return f'Class={quote}{types[value]}{quote}'
        return m.group(0)
    text = sub(r'\bClass=(["\'])([^"\']*)\1', attr, text)

    # Whole element text equal to an old type name or old defName
    def elem(m):
        value = m.group(2).strip()
        if value in types:
            changes.append(f'<{m.group(1)}>{value} -> {types[value]}')
            return m.group(0).replace(value, types[value], 1)
        if value in def_names:
            changes.append(f'<{m.group(1)}>{value} -> {def_names[value]}')
            return m.group(0).replace(value, def_names[value], 1)
        return m.group(0)
    text = sub(r'<([\w.]+)(?:\s[^>]*)?>([^<>]+)</\1>', elem, text)

    # defName="Old" inside XPath expressions
    def xpath(m):
        value = m.group(2)
        if value in def_names:
            changes.append(f'defName={value} -> {def_names[value]}')
            return f'defName={m.group(1)}{def_names[value]}{m.group(1)}'
        return m.group(0)
    text = sub(r'defName\s*=\s*(["\'])([^"\']+)\1', xpath, text)

    # Def element tags that are type names: <DMSE.MissileBodyDef> ... </DMSE.MissileBodyDef>
    def tag(m):
        value = m.group(2)
        if value in types:
            changes.append(f'<{value}> tag -> {types[value]}')
            return m.group(1) + types[value]
        return m.group(0)
    text = sub(r'(</?)([A-Za-z_]\w*(?:\.\w+)+)(?=[\s/>])', tag, text)

    # Type names inside XPath expressions: Defs/DMSE.MissileBodyDef[defName="..."]
    def xpath_types(m):
        body = m.group(2)
        new_body = re.sub(r'(?<![\w.])([A-Za-z_]\w*(?:\.\w+)+)(?![\w.])',
                          lambda t: types.get(t.group(1), t.group(1)), body)
        if new_body != body:
            changes.append('xpath type name')
        return m.group(1) + new_body + m.group(3)
    text = sub(r'(<xpath>)(.*?)(</xpath>)', xpath_types, text)
    return text, changes


def renamed_injected_folder(rel, types):
    """Languages/<lang>/DefInjected/<Old.Type>/file.xml -> folder named after the new type, or None."""
    m = re.search(r'(/DefInjected/)([^/]+)(/)', '/' + rel)
    if m and m.group(2) in types:
        return ('/' + rel).replace(m.group(0), m.group(1) + types[m.group(2)] + m.group(3), 1)[1:]
    return None


def rewrite_defs_injected(text, def_names):
    changes = []

    def key(m):
        old = m.group(2)
        if old in def_names:
            changes.append(f'<{old}.{m.group(3)}> -> {def_names[old]}')
            return f'{m.group(1)}{def_names[old]}.{m.group(3)}'
        return m.group(0)
    text = re.sub(r'(</?)([A-Za-z_]\w*)\.([\w.]+)(?=[\s>])', key, text)
    return text, changes


def main():
    # Windows consoles (cp950) cannot print every path, e.g. "ChineseSimplified (简体中文)"; never crash on output.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(errors='replace')
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('table', type=Path)
    ap.add_argument('mods', type=Path, nargs='+')
    ap.add_argument('--write', action='store_true', help='write the changes (default: report only)')
    ap.add_argument('--folders', nargs='*', default=None,
                    help='only these subfolders of each mod root (default: everything except 1.6, .git and obj/bin)')
    args = ap.parse_args()

    types, defs = load_table(args.table)
    all_def_names = {}
    for mapping in defs.values():
        all_def_names.update(mapping)
    problems = chained(types) + chained(all_def_names)
    for name in problems:
        print(f'CHAIN {name} is both an old and a new name; point every entry at the final name', file=sys.stderr)
    if problems:
        return 2
    # A defName shared by several Def types where only some are renamed (e.g. a ConceptDef and a ResearchProjectDef)
    # cannot be rewritten by text: a plain reference does not say which Def it means. Leave those for hand edits.
    tags_in_mods = mod_def_tags([m.resolve() for m in args.mods], types)
    renamed_types = defaultdict(set)
    for def_type, mapping in defs.items():
        for old in mapping:
            renamed_types[old].add(def_type.split('.')[-1])
    ambiguous = {name for name in all_def_names if tags_in_mods.get(name, set()) - renamed_types[name]}
    for name in sorted(ambiguous):
        others = ', '.join(sorted(tags_in_mods[name] - renamed_types[name]))
        print(f'MANUAL {name}: also a {others} that is not renamed; its references are left unchanged', file=sys.stderr)
    all_def_names = {k: v for k, v in all_def_names.items() if k not in ambiguous}
    print(f'table: {len(types)} type renames, {len(all_def_names)} defName renames ({len(ambiguous)} left for hand edits)')

    total_files = total_changes = 0
    cs_leftovers = defaultdict(int)
    for mod in args.mods:
        mod = mod.resolve()
        roots = [mod / f for f in args.folders] if args.folders else [mod]
        for root in roots:
            for path in sorted(root.rglob('*')):
                rel = path.relative_to(mod).as_posix()
                if not path.is_file() or rel.startswith(('1.6/', '.git/')) or '/obj/' in '/' + rel or '/bin/' in '/' + rel:
                    continue
                if path.suffix == '.cs':
                    src = path.read_text(encoding='utf-8-sig', errors='replace')
                    for old in types:
                        if old in src:
                            cs_leftovers[old] += 1
                    continue
                if path.suffix != '.xml':
                    continue
                raw = path.read_bytes()
                bom = raw.startswith(b'\xef\xbb\xbf')
                try:
                    text = raw.decode('utf-8-sig')
                except UnicodeDecodeError:
                    print(f'skip (not UTF-8): {rel}', file=sys.stderr)
                    continue
                if 'DefRenameDef' in text:
                    continue  # the rename tables themselves list the old names on purpose
                new_text, changes = rewrite_xml(text, types, all_def_names)
                injected = re.search(r'/Languages/[^/]+/DefInjected/([^/]+)/', '/' + rel)
                if injected:
                    folder_type = types.get(injected.group(1), injected.group(1))
                    for def_type, mapping in defs.items():
                        if def_type == folder_type or def_type.split('.')[-1] == folder_type.split('.')[-1]:
                            new_text, more = rewrite_defs_injected(new_text, mapping)
                            changes += more
                moved_to = renamed_injected_folder(rel, types)
                if moved_to:
                    changes.append(f'move to {moved_to}')
                if not changes:
                    continue
                total_files += 1
                total_changes += len(changes)
                print(f'{mod.name}/{rel}: {len(changes)}')
                for c in changes[:8]:
                    print(f'    {c}')
                if len(changes) > 8:
                    print(f'    ... {len(changes) - 8} more')
                if args.write:
                    out = new_text.encode('utf-8')
                    target = mod / moved_to if moved_to else path
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes((b'\xef\xbb\xbf' if bom else b'') + out)
                    if moved_to:
                        path.unlink()

    mode = 'wrote' if args.write else 'would change'
    print(f'{mode} {total_changes} references in {total_files} XML files')
    if cs_leftovers:
        print(f'C#: {len(cs_leftovers)} old type names still written out in .cs files (fix with IDE rename):')
        for old, n in sorted(cs_leftovers.items(), key=lambda kv: -kv[1])[:20]:
            print(f'    {old}: {n} file(s)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
