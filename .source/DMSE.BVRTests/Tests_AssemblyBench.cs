using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Grammar;


static partial class Test
{
    // docs/導彈組裝台與裝配方案_實作計畫.md §8.3, §5.2, §6: bench cost equals print-then-refit, rule-string
    // stripping, and blueprint files round-trip by defName.
    static void AssemblyBench()
    {
        static ThingDef Res(string name) => new ThingDef { defName = name, label = name };
        ThingDef steel = Res("Steel"), chem = Res("Chemfuel"), tant = Res("DMSE_TantastenAlloy"),
            graphene = Res("DMSE_Graphene"), spacer = Res("ComponentSpacer"), industrial = Res("ComponentIndustrial");
        var body = new MissileBodyDef
        {
            defName = "Test_MissileBody_Yj", label = "hypersonic body", designWord = "Hypersonic", refundFraction = 0.5f,
            slots = new List<MissilePartCategory> { MissilePartCategory.Warhead, MissilePartCategory.Guidance,
                MissilePartCategory.Propulsion, MissilePartCategory.Payload }
        };
        MissilePartDef Part(string name, MissilePartCategory cat, string word, bool omit, params ThingDefCountClass[] cost)
            => new MissilePartDef
            {
                defName = name, label = name, category = cat, designWord = word, omitFromDesignName = omit,
                compatibleBodies = new List<MissileBodyDef> { body }, costList = cost.ToList()
            };
        MissilePartDef he = Part("Test_Warhead_HE", MissilePartCategory.Warhead, "HE", false,
            new ThingDefCountClass(steel, 30), new ThingDefCountClass(chem, 20));
        MissilePartDef ap = Part("Test_Warhead_AP", MissilePartCategory.Warhead, "AP", false,
            new ThingDefCountClass(steel, 40), new ThingDefCountClass(tant, 5));
        MissilePartDef antiShip = Part("Test_Guidance_AntiShip", MissilePartCategory.Guidance, "Anti-ship", false,
            new ThingDefCountClass(industrial, 5), new ThingDefCountClass(spacer, 1));
        MissilePartDef standard = Part("Test_Propulsion_Standard", MissilePartCategory.Propulsion, null, true);
        MissilePartDef booster = Part("Test_Propulsion_Booster", MissilePartCategory.Propulsion, "Boost", false,
            new ThingDefCountClass(chem, 40), new ThingDefCountClass(steel, 20));
        MissilePartDef noPayload = Part("Test_Payload_None", MissilePartCategory.Payload, null, true);
        body.defaultParts = new List<MissilePartDef> { he, antiShip, standard, noPayload };
        var item = new ThingDef
        {
            defName = "Test_Missile_Yj", label = "test missile", category = ThingCategory.Item,
            costList = new List<ThingDefCountClass> { new ThingDefCountClass(chem, 270), new ThingDefCountClass(tant, 70),
                new ThingDefCountClass(graphene, 90), new ThingDefCountClass(spacer, 6) },
            comps = new List<CompProperties> { new CompProperties_MissileConfig { defaultBody = body } }
        };
        DefDatabase<ThingDef>.Add(item);
        DefDatabase<MissileBodyDef>.Add(body);
        foreach (MissilePartDef p in new[] { he, ap, antiShip, standard, booster, noPayload }) { DefDatabase<MissilePartDef>.Add(p); }
        // 反查快取是靜態的：清掉，讓它看到剛加入的測試 Def。
        typeof(MissileAssemblyUtility).GetField("cachedDefs", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        typeof(MissileItemUtility).GetField("itemByBody", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
        Check(MissileItemUtility.ItemDefFor(body) == item, "bench finds the item that declares the body");

        MissileConfig defaults = MissileConfig.NewWithDefaults(body);
        Dictionary<ThingDef, int> plain = MissileAssemblyCost.RequiredFor(defaults);
        Check(plain.Count == 4 && plain[chem] == 270 && plain[tant] == 70 && plain[graphene] == 90 && plain[spacer] == 6,
            "default design costs exactly the item recipe");

        MissileConfig target = defaults.Clone();
        target.SetPart(MissilePartCategory.Warhead, ap);
        target.SetPart(MissilePartCategory.Propulsion, booster);
        Dictionary<ThingDef, int> cost = MissileAssemblyCost.RequiredFor(target);
        Check(cost.Count == 5 && cost[chem] == 300 && cost[tant] == 75 && cost[graphene] == 90 && cost[spacer] == 6 && cost[steel] == 45,
            "AP + booster Yj matches the plan's worked example");

        // 與「列印預設彈 → 改裝」逐材料相等
        var refit = new CompMissileConfig { config = defaults.Clone(), pending = target.Clone() };
        var twoStep = new Dictionary<ThingDef, int>();
        void Add(ThingDef d, int n) { twoStep.TryGetValue(d, out int cur); twoStep[d] = cur + n; }
        foreach (ThingDefCountClass c in item.costList) { Add(c.thingDef, c.count); }
        foreach (KeyValuePair<ThingDef, int> kv in refit.TotalRequired()) { Add(kv.Key, kv.Value); }
        foreach (MissilePartDef removed in refit.RemovedParts())
        {
            foreach (ThingDefCountClass c in removed.costList) { Add(c.thingDef, -Mathf.FloorToInt(c.count * body.refundFraction)); }
        }
        Check(twoStep.Where(kv => kv.Value > 0).All(kv => cost.TryGetValue(kv.Key, out int v) && v == kv.Value)
              && cost.All(kv => twoStep.TryGetValue(kv.Key, out int v) && v == kv.Value),
            "bench cost equals print-then-refit, material by material");

        MissileConfig stripped = defaults.Clone();
        stripped.SetPart(MissilePartCategory.Warhead, null);
        Dictionary<ThingDef, int> noWarhead = MissileAssemblyCost.RequiredFor(stripped);
        Check(noWarhead[chem] == 260 && !noWarhead.ContainsKey(steel), "removed default part refunds half, clamped at zero");

        // 規則字串：本組合沒有的符號剔除，空白與分隔符壓縮
        Check(MissileDesignNamer.Strip(defaults, "[warhead] [payload] [body]") == "[warhead] [body]", "omitted payload symbol is stripped");
        Check(MissileDesignNamer.Strip(stripped, "[body]·[warhead][payload]") == "[body]", "dangling separator is trimmed");
        Check(MissileDesignNamer.Strip(target, "[body] [warhead] [propulsion]") == "[body] [warhead] [propulsion]", "present symbols are kept");
        Check(MissileDesignNamer.Strip(defaults, null) == "[body]", "missing template falls back to the body");
        Dictionary<string, string> symbols = MissileDesignNamer.BuildSymbols(target);
        Check(symbols["body"] == "Hypersonic" && symbols["warhead"] == "AP" && symbols["propulsion"] == "Boost"
              && !symbols.ContainsKey("payload"), "symbols use design words and skip omitted parts");
        Check(MissileDesignNamer.IsValidName("YJ AP-Boost·2") && !MissileDesignNamer.IsValidName("bad/name")
              && !MissileDesignNamer.IsValidName(new string('x', 41)), "name validation follows the xenotype editor rules");

        // 各彈體專屬 Namer（docs/導彈組裝台與裝配方案_實作計畫.md §5.6）
        // GrammarResolver 一定會併入 RulePackDefOf.GlobalUtility；測試環境的 DefDatabase 是空的，先補一個。
        var globalUtility = new RulePackDef { defName = "GlobalUtility" };
        Field(globalUtility, "rulePack", new RulePack());
        if (DefDatabase<RulePackDef>.GetNamedSilentFail("GlobalUtility") == null) { DefDatabase<RulePackDef>.Add(globalUtility); }
        // DefOf 的快取可能早就初始化過（值為 null），直接補上。
        typeof(RulePackDefOf).GetField("GlobalUtility", BindingFlags.Public | BindingFlags.Static).SetValue(null, globalUtility);
        var namerPack = new RulePackDef { defName = "Test_NamerPack" };
        var namerRules = new RulePack();
        Field(namerRules, "rulesStrings", new List<string>
        {
            "r_name(p=3)->[designator]-[num] [warhead] [guidance]",
            "r_name(p=2)->[designator]-[num][suffix] [warhead]",
            "r_name(p=1)->[body] [designator]-[num]"
        });
        Field(namerPack, "rulePack", namerRules);
        var namer = new MissileNamerDef
        {
            defName = "Test_Namer", pack = namerPack, designator = "YJ", numberMin = 1, numberMax = 99
        };
        body.namer = namer;
        Check(MissileDesignNamer.NamerFor(target) == namer && MissileDesignNamer.PackFor(target) == namerPack,
            "the body's namer supplies the design-name pack");
        Check(MissileDesignNamer.BuildSymbols(target)["designator"] == "YJ",
            "[designator] is injected from the namer's design word");
        // 代字保持符號（換語言時重新翻譯），只有編號／後綴在抽樣板時烘成文字。
        Check(System.Text.RegularExpressions.Regex.IsMatch(MissileDesignNamer.Bake(namer, "[designator]-[num]"), @"^\[designator\]-[1-9][0-9]?$"),
            "the number is baked into the rule string");
        string bakedRule = MissileDesignNamer.BuildRuleString(target, namerPack);
        Check(!bakedRule.Contains("[num]") && !bakedRule.Contains("[suffix]")
              && System.Text.RegularExpressions.Regex.IsMatch(bakedRule, @"\[designator\]-[1-9][0-9]?"),
            $"the picked template carries a concrete number ({bakedRule})");
        string named = MissileDesignNamer.Generate(target, null, new HashSet<string>(), out string nameRule);
        Check(!named.NullOrEmpty() && named.Contains("YJ-") && !nameRule.Contains("[num]") && MissileDesignNamer.IsValidName(named),
            $"per-body design name ({named} from {nameRule})");
        string renamed = MissileDesignNamer.Generate(target, null, new HashSet<string> { named }, out _);
        Check(renamed != named, $"a colliding designation draws a new number ({named} / {renamed})");
        var padded = new MissileNamerDef
        {
            defName = "Test_Namer_Padded", pack = namerPack, designator = "DF", numberMin = 7, numberMax = 7,
            numberDigits = 2, suffixes = new List<string> { "D" }
        };
        Check(MissileDesignNamer.Bake(padded, "[designator]-[num][suffix]") == "[designator]-07D",
            "numberDigits pads the number and the suffix follows it");
        Check(MissileDesignNamer.Candidates(target, new HashSet<string>()).Count == 3,
            "every template yields one candidate name");
        body.namer = null;
        Check(!MissileDesignNamer.BuildSymbols(target).ContainsKey("designator")
              && MissileDesignNamer.Strip(target, "[designator] [warhead]") == "[warhead]",
            "a body without a namer drops the designator and its separators");

        // 方案檔：以 defName 存讀；缺 Def 時停用並說明原因
        string dir = Path.Combine(Path.GetTempPath(), "dmse-bench-test");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "design.xml");
        MissileDesignBlueprint bp = MissileDesignBlueprint.From(target, "YJ AP", true, "[body] [warhead]");
        bp.ToXml().Save(file);
        MissileDesignBlueprint loaded = MissileDesignBlueprint.FromXml(file);
        MissileConfig back = loaded.ToConfig();
        Check(loaded.Usable && loaded.name == "YJ AP" && loaded.nameLocked && loaded.ruleString == "[body] [warhead]"
              && back.body == body && back.parts.Count == 4 && back.PartFor(MissilePartCategory.Warhead) == ap,
            "blueprint round-trips through its XML file");
        XDocument broken = XDocument.Load(file);
        broken.Root.Element("partDefNames").Add(new XElement("li", "Test_Removed_Part"));
        broken.Save(file);
        MissileDesignBlueprint missing = MissileDesignBlueprint.FromXml(file);
        Check(!missing.Usable && missing.unusableReason.Contains("Test_Removed_Part"), "blueprint with a removed def is disabled with a reason");
        File.Delete(file);
    }
}
