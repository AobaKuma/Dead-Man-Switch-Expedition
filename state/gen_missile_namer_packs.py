# -*- coding: utf-8 -*-
"""產生各彈體的設計名稱樣板包（英文預設 + zh-TW／zh-CN 翻譯）。

每個彈體一組自己的 r_name 樣板：型號代字＋編號當骨架，部件詞只挑該彈體真正會有的槽位，
語序與連接符依彈種（反艦／對地／防空／彈道／巡飛）分別設計。
"""
import io
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def rule(weight, text):
    return f'r_name(p={weight})->{text}' if weight else f'r_name->{text}'


# 每個彈體：defName、說明、樣板（每條 (weight, template)）
PACKS = [
    ("Light", "輕型對地／通用彈體：戰頭＋導引＋酬載，短編號。", [
        (3, "[designator]-[num] [warhead] [guidance]"),
        (2, "[designator]-[num] [warhead] [payload]"),
        (1, "[body] [designator]-[num]"),
        (1, "[designator]-[num]"),
    ]),
    ("AdvancedLight", "先進輕型多用途彈體（地獄火級）：三位數編號，偏重戰頭型號。", [
        (3, "[designator]-[num] [warhead] [guidance]"),
        (2, "[designator]-[num] [warhead] [payload]"),
        (2, "[designator]-[num] [guidance]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("AdvancedCruise", "先進巡航彈體（戰斧級）：導引頭與推進是型號重點，語序導引優先。", [
        (3, "[designator]-[num] [guidance] [warhead]"),
        (2, "[designator]-[num][suffix] [guidance]"),
        (2, "[designator]-[num] [warhead] [propulsion]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("Stinger", "輕型防空彈體（刺針級）：只有戰頭＋導引，名稱短。", [
        (3, "[designator]-[num] [guidance]"),
        (2, "[designator]-[num] [warhead] [guidance]"),
        (1, "[body] [designator]-[num]"),
        (1, "[designator]-[num]"),
    ]),
    ("Maverick", "輕型對地彈體（小牛級）：酬載可選，型號常帶酬載字。", [
        (3, "[designator]-[num] [warhead] [guidance]"),
        (2, "[designator]-[num] [payload]"),
        (1, "[body] [designator]-[num]"),
        (1, "[designator]-[num]"),
    ]),
    ("Sparrow", "先進防空彈體（麻雀級）：半主動族，後綴字母是型號的一部分。", [
        (3, "[designator]-[num][suffix] [guidance]"),
        (2, "[designator]-[num][suffix] [warhead] [guidance]"),
        (2, "[designator]-[num] [warhead] [payload]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("Moskit", "中型反艦彈體（白蛉級）：戰頭＋導引是主體，推進放次要語序。", [
        (3, "[designator]-[num] [warhead] [guidance]"),
        (2, "[designator]-[num][suffix] [guidance] [propulsion]"),
        (1, "[designator]-[num] [warhead] [propulsion]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("Yj", "中型高超音速反艦彈體（鷹擊級）：導引／推進與戰頭並列。", [
        (3, "[designator]-[num] [warhead] [guidance]"),
        (2, "[designator]-[num] [guidance] [propulsion]"),
        (2, "[designator]-[num][suffix] [warhead] [payload]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("Tochka", "中型戰術彈體（圓點級）：戰頭為型號主體，酬載／推進當後綴詞。", [
        (3, "[designator]-[num][suffix] [warhead]"),
        (2, "[designator]-[num] [warhead] [guidance]"),
        (1, "[designator]-[num] [payload] [propulsion]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("LegacyBallistic", "老式彈道彈體（V2 級）：沒有精密導引，戰頭就是型號。", [
        (3, "[designator]-[num] [warhead]"),
        (2, "[designator]-[num] [warhead] [payload]"),
        (1, "[body] [designator]-[num]"),
        (1, "[designator]-[num]"),
    ]),
    ("AdvancedBallistic", "先進彈道彈體（民兵級）：戰頭＋酬載（多彈頭／突防）為主。", [
        (3, "[designator]-[num] [warhead] [payload]"),
        (2, "[designator]-[num] [guidance] [warhead]"),
        (1, "[designator]-[num] [payload]"),
        (1, "[body] [designator]-[num]"),
    ]),
    ("Shahed", "巡飛彈體（沙赫德級）：只有導引＋酬載，名稱短的無人機型號。", [
        (3, "[designator]-[num] [guidance]"),
        (2, "[designator]-[num] [payload]"),
        (2, "[body] [designator]-[num]"),
        (1, "[designator]-[num]"),
    ]),
]

# 中文樣板：同一組骨架，連接符改用「·」，語序照中文習慣（部件詞連寫）。
CN = {
    "Light": [
        (3, "[designator]-[num]·[warhead][guidance]"),
        (2, "[designator]-[num]·[warhead][payload]"),
        (1, "[body]·[designator]-[num]"),
        (1, "[designator]-[num]"),
    ],
    "AdvancedLight": [
        (3, "[designator]-[num]·[warhead][guidance]"),
        (2, "[designator]-[num]·[warhead][payload]"),
        (2, "[designator]-[num]·[guidance]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "AdvancedCruise": [
        (3, "[designator]-[num]·[guidance][warhead]"),
        (2, "[designator]-[num][suffix]·[guidance]"),
        (2, "[designator]-[num]·[warhead][propulsion]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "Stinger": [
        (3, "[designator]-[num]·[guidance]"),
        (2, "[designator]-[num]·[warhead][guidance]"),
        (1, "[body]·[designator]-[num]"),
        (1, "[designator]-[num]"),
    ],
    "Maverick": [
        (3, "[designator]-[num]·[warhead][guidance]"),
        (2, "[designator]-[num]·[payload]"),
        (1, "[body]·[designator]-[num]"),
        (1, "[designator]-[num]"),
    ],
    "Sparrow": [
        (3, "[designator]-[num][suffix]·[guidance]"),
        (2, "[designator]-[num][suffix]·[warhead][guidance]"),
        (2, "[designator]-[num]·[warhead][payload]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "Moskit": [
        (3, "[designator]-[num]·[warhead][guidance]"),
        (2, "[designator]-[num][suffix]·[guidance][propulsion]"),
        (1, "[designator]-[num]·[warhead][propulsion]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "Yj": [
        (3, "[designator]-[num]·[warhead][guidance]"),
        (2, "[designator]-[num]·[guidance][propulsion]"),
        (2, "[designator]-[num][suffix]·[warhead][payload]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "Tochka": [
        (3, "[designator]-[num][suffix]·[warhead]"),
        (2, "[designator]-[num]·[warhead][guidance]"),
        (1, "[designator]-[num]·[payload][propulsion]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "LegacyBallistic": [
        (3, "[designator]-[num]·[warhead]"),
        (2, "[designator]-[num]·[warhead][payload]"),
        (1, "[body]·[designator]-[num]"),
        (1, "[designator]-[num]"),
    ],
    "AdvancedBallistic": [
        (3, "[designator]-[num]·[warhead][payload]"),
        (2, "[designator]-[num]·[guidance][warhead]"),
        (1, "[designator]-[num]·[payload]"),
        (1, "[body]·[designator]-[num]"),
    ],
    "Shahed": [
        (3, "[designator]-[num]·[guidance]"),
        (2, "[designator]-[num]·[payload]"),
        (2, "[body]·[designator]-[num]"),
        (1, "[designator]-[num]"),
    ],
}

GENERIC_CN = [
    (3, "[body]·[warhead]"),
    (2, "[warhead][guidance][body]"),
    (0, "[guidance][body]"),
    (0, "[body]·[warhead][payload]"),
    (0, "[guidance][propulsion][body]"),
]

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<Defs>

  <!-- ==================================================================
       導彈設計名稱樣板（r_name）
       Design-name templates.
       ==================================================================
       樣板由 MissileDesignNamer 依彈體挑選：每個可裝配彈體在 <DMSE.MissileNamerDef> 的
       pack 欄位指定自己的包（DMSE_NamerPack_*），沒指定的彈體用通用 DMSE_NamerMissileDesign。

       符號：
         [body] [warhead] [guidance] [propulsion] [payload]  由程式以 Rule_String 注入（值是 Def 的 designWord）
         [designator]  型號代字（Namer 的 designator 欄位，可翻譯）
         [num] [suffix] 型號編號與後綴；抽定樣板時就換成文字寫進規則字串，改部件不會重抽

       本組合沒有的槽位符號會先被剔除，所以同一組樣板能服務有／無酬載的構型；
       型號類樣板因此不重複列部件組合，而是靠 [designator]-[num] 當骨架。
       每個彈體一組自己的樣板（語序依彈種：反艦偏戰頭＋導引、巡航偏導引、
       輕型防空短、彈道以戰頭／酬載為主、巡飛只有導引／酬載）。
       同一 keyword 多條＝加權候選，用 p= 調權重。check_missile_content.py 第 13／15 條檢查。
       ================================================================== -->

  <!-- 通用樣板：沒有綁 Namer 的彈體（只有部件詞、沒有型號代字）。 -->
  <RulePackDef>
    <defName>DMSE_NamerMissileDesign</defName>
    <rulePack>
      <rulesStrings>
        <li>r_name(p=3)->[body] [warhead]</li>
        <li>r_name(p=2)->[body] [warhead] [guidance]</li>
        <li>r_name->[body]-[guidance]</li>
        <li>r_name->[warhead] [payload] [body]</li>
        <li>r_name->[guidance] [propulsion] [body]</li>
      </rulesStrings>
    </rulePack>
  </RulePackDef>
"""


def emit_defs():
    out = [HEADER]
    for name, comment, rules in PACKS:
        out.append(f"""
  <!-- {name}：{comment} -->
  <RulePackDef>
    <defName>DMSE_NamerPack_{name}</defName>
    <rulePack>
      <rulesStrings>""")
        for weight, text in rules:
            out.append(f"        <li>{rule(weight, text)}</li>")
        out.append("""      </rulesStrings>
    </rulePack>
  </RulePackDef>
""")
    out.append("\n</Defs>\n")
    return "".join(out)


def emit_language(rules_by_pack):
    out = ['<?xml version="1.0" encoding="utf-8"?>\n<LanguageData>\n']
    out.append("\n  <!-- 通用樣板（沒有綁 Namer 的彈體）。 -->\n")
    out.append("  <DMSE_NamerMissileDesign.rulePack.rulesStrings>\n")
    for weight, text in GENERIC_CN:
        out.append(f"    <li>{rule(weight, text)}</li>\n")
    out.append("  </DMSE_NamerMissileDesign.rulePack.rulesStrings>\n")
    for name, _, _ in PACKS:
        out.append(f"\n  <!-- {name} -->\n")
        out.append(f"  <DMSE_NamerPack_{name}.rulePack.rulesStrings>\n")
        for weight, text in rules_by_pack[name]:
            out.append(f"    <li>{rule(weight, text)}</li>\n")
        out.append(f"  </DMSE_NamerPack_{name}.rulePack.rulesStrings>\n")
    out.append("\n</LanguageData>\n")
    return "".join(out)


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with io.open(path, "w", encoding="utf-8-sig", newline="\r\n") as handle:
        handle.write(text)
    print("wrote", os.path.relpath(path, ROOT), len(text))


if __name__ == "__main__":
    write(os.path.join(ROOT, "1.7", "Defs", "RulePacks", "DMSE_Namer_MissileDesign.xml"), emit_defs())
    for folder in ("ChineseTraditional (繁體中文)", "ChineseSimplified (简体中文)"):
        write(os.path.join(ROOT, "1.7", "Languages", folder, "DefInjected", "RulePackDef",
                           "DMSE_Namer_MissileDesign.xml"), emit_language(CN))
