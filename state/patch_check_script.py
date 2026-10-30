# -*- coding: utf-8 -*-
"""一次性：更新 check_missile_content.py 的命名相關檢查（第 12、13 條，新增第 15 條）。"""
from pathlib import Path

path = Path(__file__).resolve().parents[1] / ".source/Tools/check_missile_content.py"
text = path.read_text(encoding="utf-8")

# ---- 檔頭 docstring ----
old_doc = """ 12. every designWord has zh-TW and zh-CN translations, and the design-name rule pack is translated;
 13. every design-name template (English and translations) uses only [body] [warhead] [guidance] [propulsion]
     [payload] and contains [body];
 14. every producible missile lists the assembly bench in its durability-decay shelterBuildings.
"""
new_doc = """ 12. every designWord and every namer designator has zh-TW and zh-CN translations;
 13. every design-name template (English and translations) uses only the known symbols and has [body] or [designator];
 14. every producible missile lists the assembly bench in its durability-decay shelterBuildings;
 15. every customizable body has its own DMSE.MissileNamerDef (existing pack, translated, slots it opens),
     every namer is referenced by a body, and no fixed body declares one.
"""
assert old_doc in text
text = text.replace(old_doc, new_doc)

# ---- 第 12、13 條整段換掉 ----
start = text.index("# ---- 12. design words translated")
end = text.index("# ---- 14. bench shelters its output")
new_block = '''# ---- 12. design words and namer designators translated ----------------------
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
    symbols = set(re.findall(r'\\[(\\w+)\\]', rule))
    require(symbols <= DESIGN_SYMBOLS,
            f'{where}: {pack} template {rule!r} uses unknown symbols {sorted(symbols - DESIGN_SYMBOLS)}')
    require('body' in symbols or 'designator' in symbols,
            f'{where}: {pack} template {rule!r} has neither [body] nor [designator]')
for name in design_packs:
    rules = [li.text or '' for li in packs[name].findall('rulePack/rulesStrings/li')]
    require(any(r.startswith('r_name') for r in rules), f'{name}: no r_name templates')

'''
text = text[:start] + new_block + text[end:]

# ---- 新增第 15 條（放在第 14 條之後、總結之前）----
anchor = "if errors:\n"
new15 = '''# ---- 15. every customizable body has its own namer --------------------------
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
            used = set(re.findall(r'\\[(\\w+)\\]', rule)) - {'body', 'designator', 'num', 'suffix'}
            require(used <= slots, f'{pack}: template {rule!r} uses slots {sorted(used - slots)} that {name} does not open')

used_namers = {body.findtext('namer') for body in bodies.values() if body.findtext('namer')}
for name in namers:
    require(name in used_namers, f'{name}: not referenced by any MissileBodyDef')
    low, high = namers[name].findtext('numberMin'), namers[name].findtext('numberMax')
    if low is not None or high is not None:
        require((low or '1').isdigit() and (high or '99').isdigit(),
                f'{name}: numberMin/numberMax must be integers')
        require(int(low or 1) <= int(high or 99), f'{name}: numberMin {low} > numberMax {high}')

'''
assert anchor in text
text = text.replace(anchor, new15 + anchor, 1)

# ---- 總結字串 ----
old_summary = "f'{len(expected_keys)} translations in 2 languages, {len(producible)} bench-producible missiles, '\n      f'{len(templates)} design-name templates.')"
new_summary = "f'{len(expected_keys)} translations in 2 languages, {len(producible)} bench-producible missiles, '\n      f'{len(namers)} missile namers, {len(templates)} design-name templates.')"
assert old_summary in text
text = text.replace(old_summary, new_summary)

path.write_text(text, encoding="utf-8")
print("check_missile_content.py updated")
