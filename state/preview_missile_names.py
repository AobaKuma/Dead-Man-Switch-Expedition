# -*- coding: utf-8 -*-
"""預覽每個彈體用自己 Namer 生成的名稱（模擬 MissileDesignNamer）。

只做這幾件事（與 C# 相同順序）：抽樣板 → 把 [num]／[suffix] 換成文字 → 剔除空槽符號
→ 壓縮分隔符 → 以 designWord 代入 [body]／[warhead]／… 與 [designator]。
"""
import glob
import io
import os
import random
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SLOTS = ("warhead", "guidance", "propulsion", "payload")


def load(path):
    return io.open(path, encoding="utf-8-sig").read()


def elements(text, tag):
    return re.findall(rf"<{re.escape(tag)}>(.*?)</{re.escape(tag)}>", text, re.S)


def child(block, tag):
    m = re.search(rf"<{tag}>(.*?)</{tag}>", block, re.S)
    return m.group(1).strip() if m else None


def injected(folder, name):
    out = {}
    for path in glob.glob(os.path.join(ROOT, "1.7", "Languages", folder, "DefInjected", "**", "*.xml"), recursive=True):
        for m in re.finditer(r"<([\w.]+)>([^<]*)</\1>", load(path)):
            out[m.group(1)] = m.group(2)
    return out


def defs_of(tag):
    out = {}
    for path in glob.glob(os.path.join(ROOT, "1.7", "Defs", "**", "*.xml"), recursive=True):
        text = load(path)
        for node in elements(text, tag):
            name = child(node, "defName")
            if name:
                out[name] = node
    return out


def strip(rule, syms):
    for slot in SLOTS:
        if slot not in syms:
            rule = rule.replace(f"[{slot}]", "")
    rule = re.sub(r"「\s*」|『\s*』|\(\s*\)|\[\s*\]", "", rule)
    rule = re.sub(r"\s{2,}", " ", rule)
    rule = re.sub(r"(\s*[·\-]\s*){2,}", "·", rule)
    return re.sub(r"^[\s\-·]+|[\s\-·]+$", "", rule) or "[body]"


def preview(folder, label, count=3):
    bodies, parts = defs_of("DMSE.MissileBodyDef"), defs_of("DMSE.MissilePartDef")
    namers = defs_of("DMSE.MissileNamerDef")
    packs = defs_of("RulePackDef")
    words = injected(folder, label)
    random.seed(7)
    print(f"\n== {label} ==")
    for body_name, body in bodies.items():
        namer_name = child(body, "namer")
        if not namer_name:
            continue
        namer = namers[namer_name]
        slots = [s for s in elements(child(body, "slots") or "", "li")]
        defaults = [s for s in elements(child(body, "defaultParts") or "", "li")]
        syms = {"body": words.get(f"{body_name}.designWord") or child(body, "designWord") or body_name}
        for part_name in defaults:
            part = parts[part_name]
            if child(part, "omitFromDesignName") == "true":
                continue
            word = words.get(f"{part_name}.designWord") or child(part, "designWord") or child(part, "label")
            syms[child(part, "category").lower()] = word
        designator = words.get(f"{namer_name}.designator") or child(namer, "designator")
        syms["designator"] = designator
        pack = child(namer, "pack")
        rules = [li for li in elements(child(packs[pack], "rulePack") or "", "li")]
        lo, hi = int(child(namer, "numberMin")), int(child(namer, "numberMax"))
        digits = int(child(namer, "numberDigits") or 0)
        suffixes = elements(child(namer, "suffixes") or "", "li")
        names = []
        for _ in range(count):
            raw = random.choice(rules)
            rule = raw.split("->", 1)[1]
            number = str(random.randint(lo, hi))
            if digits:
                number = number.zfill(digits)
            rule = rule.replace("[num]", number)
            rule = rule.replace("[suffix]", random.choice(suffixes) if suffixes else "")
            rule = strip(rule, syms)
            name = rule
            for key, value in syms.items():
                name = name.replace(f"[{key}]", value)
            names.append(name)
        print(f"  {body_name.replace('DMSE_MissileBody_', ''):18s} {designator:10s} {names}")


if __name__ == "__main__":
    preview("ChineseTraditional (繁體中文)", "zh-TW")
    preview("", "en")
