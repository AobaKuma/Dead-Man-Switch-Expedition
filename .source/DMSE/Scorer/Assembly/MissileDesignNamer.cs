using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.Grammar;

namespace DMSE
{
    /// <summary>
    /// 導彈設計名稱：規則字串（ruleString）→ <see cref="Verse.Grammar"/> → 名稱。
    /// 樣板依彈體而定：<see cref="MissileBodyDef.namer"/> 指定的包；沒綁定時用通用 <c>DMSE_NamerMissileDesign</c>。
    /// 每槽一個符號（[body] [warhead] [guidance] [propulsion] [payload]），值是 Def 的 designWord
    /// （null 退回 label；部件標了 omitFromDesignName 就不出現）；型號類 Namer 另外提供
    /// [designator]（代字，可翻譯）與 [num]／[suffix]（編號與後綴，抽樣板時就換成實際文字）。
    /// 解析時用獨立 root keyword <c>design_name</c>，不 Include 規則包，避免注入的規則變成 r_name 的額外候選。
    /// </summary>
    public static class MissileDesignNamer
    {
        public const int MaxNameLength = 40;

        /// <summary>名稱輸入過濾（同異種人編輯器，另允許中文間隔號）。</summary>
        public static readonly Regex ValidSymbolRegex = new Regex(@"^[\p{L}0-9 '\-·]*$");

        private const string RootKeyword = "design_name";
        private const string BodyKeyword = "body";
        private const string DesignatorKeyword = "designator";
        private const string DesignatorToken = "[designator]";
        private const string NumberToken = "[num]";
        private const string SuffixToken = "[suffix]";
        private const string DefaultPackName = "DMSE_NamerMissileDesign";
        private const int NumberAttempts = 8;

        /// <summary>[num]／[suffix] 在抽樣板時就換成文字；殘留（例如沒有 Namer）視為不存在。</summary>
        private static readonly string[] BakedTokens = { NumberToken, SuffixToken };

        /// <summary>固定順序：與 body.slots 宣告順序無關，同一組合永遠組出同一條規則字串。</summary>
        private static readonly MissilePartCategory[] SlotOrder =
        {
            MissilePartCategory.Warhead, MissilePartCategory.Guidance,
            MissilePartCategory.Propulsion, MissilePartCategory.Payload
        };

        /// <summary>通用樣板包（沒有綁定 Namer 的彈體用）。</summary>
        public static RulePackDef Pack => DefDatabase<RulePackDef>.GetNamedSilentFail(DefaultPackName);

        /// <summary>此構型所屬彈體的 Namer；沒綁定時為 null（通用樣板、沒有型號代字）。</summary>
        public static MissileNamerDef NamerFor(MissileConfig cfg) => cfg?.body?.namer;

        /// <summary>此構型使用的樣板包：彈體 Namer 的包，退回通用包。</summary>
        public static RulePackDef PackFor(MissileConfig cfg) => NamerFor(cfg)?.pack ?? Pack;

        public static string Key(MissilePartCategory cat) => cat.ToString().ToLowerInvariant();

        private static string Word(Def d, string designWord) => designWord ?? d.label;

        /// <summary>本次組合的符號表：keyword → 詞。標了 omitFromDesignName 的部件不放（等同空槽）。</summary>
        public static Dictionary<string, string> BuildSymbols(MissileConfig cfg)
        {
            Dictionary<string, string> syms = new Dictionary<string, string>();
            if (cfg?.body == null) { return syms; }
            syms[BodyKeyword] = Word(cfg.body, cfg.body.designWord);
            string designator = NamerFor(cfg)?.Designator;
            if (!designator.NullOrEmpty()) { syms[DesignatorKeyword] = designator; }
            foreach (MissilePartCategory cat in SlotOrder)
            {
                MissilePartDef p = cfg.PartFor(cat);
                if (p == null || p.omitFromDesignName) { continue; }
                syms[Key(cat)] = Word(p, p.designWord);
            }
            return syms;
        }

        /// <summary>規則包內所有 r_name 樣板（原始字串）。</summary>
        public static List<Rule_String> Templates(RulePackDef pack)
        {
            if (pack == null) { return new List<Rule_String>(); }
            return pack.RulesPlusIncludes.OfType<Rule_String>().Where(r => r.keyword == "r_name").ToList();
        }

        /// <summary>
        /// 把樣板中的 [num]／[suffix] 換成實際文字（型號編號與後綴）。
        /// 這一步在「抽定樣板」時做一次，之後沿用同一條規則字串，改部件不會重抽編號。
        /// </summary>
        public static string Bake(MissileNamerDef namer, string template)
        {
            if (template.NullOrEmpty()) { return template; }
            string rule = template;
            if (rule.Contains(NumberToken))
            {
                rule = rule.Replace(NumberToken, namer != null ? namer.Number() : string.Empty);
            }
            if (rule.Contains(SuffixToken))
            {
                rule = rule.Replace(SuffixToken, namer?.Suffix() ?? string.Empty);
            }
            return rule;
        }

        /// <summary>依權重抽一條樣板、換上編號，剔除本組合沒有的符號，壓縮空白與首尾分隔符。</summary>
        public static string BuildRuleString(MissileConfig cfg, RulePackDef pack)
        {
            List<Rule_String> templates = Templates(pack ?? PackFor(cfg));
            string raw = templates.Count > 0 ? templates.RandomElementByWeight(r => r.BaseSelectionWeight).Generate() : null;
            return Strip(cfg, Bake(NamerFor(cfg), raw));
        }

        /// <summary>把樣板中本組合沒有的槽位（含型號代字）整段剔除。</summary>
        public static string Strip(MissileConfig cfg, string template)
        {
            if (cfg?.body == null) { return null; }
            if (template.NullOrEmpty()) { return "[body]"; }
            Dictionary<string, string> syms = BuildSymbols(cfg);
            string rule = template;
            foreach (MissilePartCategory cat in SlotOrder)
            {
                if (!syms.ContainsKey(Key(cat))) { rule = rule.Replace("[" + Key(cat) + "]", string.Empty); }
            }
            if (!syms.ContainsKey(DesignatorKeyword)) { rule = rule.Replace(DesignatorToken, string.Empty); }
            foreach (string token in BakedTokens) { rule = rule.Replace(token, string.Empty); }
            rule = Regex.Replace(rule, @"「\s*」|『\s*』|\(\s*\)|\[\s*\]", string.Empty);
            rule = Regex.Replace(rule, @"\s{2,}", " ");
            rule = Regex.Replace(rule, @"(\s*[·\-]\s*){2,}", "·");
            rule = Regex.Replace(rule, @"^[\s\-·]+|[\s\-·]+$", string.Empty);
            return rule.NullOrEmpty() ? "[body]" : rule;
        }

        /// <summary>
        /// 以規則字串產生名稱；<paramref name="forcedRuleString"/> 為 null 時重抽樣板（含重新抽編號）。
        /// 名稱與 <paramref name="used"/> 重複時先重抽編號，抽不到才自動加編號（「… 2」）。
        /// </summary>
        public static string Generate(MissileConfig cfg, string forcedRuleString, ICollection<string> used, out string ruleString)
        {
            ruleString = null;
            if (cfg?.body == null) { return null; }
            MissileNamerDef namer = NamerFor(cfg);
            if (!forcedRuleString.NullOrEmpty())
            {
                ruleString = Strip(cfg, Bake(namer, forcedRuleString));
                return Resolve(cfg, ruleString, used, true);
            }
            for (int i = 0; i < NumberAttempts; i++)
            {
                ruleString = BuildRuleString(cfg, PackFor(cfg));
                string fresh = Resolve(cfg, ruleString, null, false);
                if (fresh.NullOrEmpty() || used == null || !used.Contains(fresh)) { return fresh; }
            }
            return Resolve(cfg, ruleString, used, true);
        }

        private static string Resolve(MissileConfig cfg, string ruleString, ICollection<string> used, bool appendNumberIfUsed)
        {
            GrammarRequest req = default;
            foreach (KeyValuePair<string, string> kv in BuildSymbols(cfg))
            {
                req.Rules.Add(new Rule_String(kv.Key, kv.Value));
            }
            req.Rules.Add(new Rule_String(RootKeyword, ruleString));
            string name = NameGenerator.GenerateName(req,
                n => !n.NullOrEmpty() && n.Length <= MaxNameLength && (used == null || !used.Contains(n)),
                appendNumberIfNameUsed: appendNumberIfUsed, rootKeyword: RootKeyword);
            return name.Length > MaxNameLength ? name.Substring(0, MaxNameLength).TrimEnd() : name;
        }

        /// <summary>每條樣板各產生一個名稱（去重），供「候選」選單使用。</summary>
        public static List<KeyValuePair<string, string>> Candidates(MissileConfig cfg, ICollection<string> used)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            HashSet<string> taken = used != null ? new HashSet<string>(used) : new HashSet<string>();
            foreach (Rule_String t in Templates(PackFor(cfg)))
            {
                string name = Generate(cfg, t.Generate(), taken, out string rule);
                if (name.NullOrEmpty() || result.Any(kv => kv.Key == name)) { continue; }
                result.Add(new KeyValuePair<string, string>(name, rule));
                taken.Add(name);
            }
            return result;
        }

        /// <summary>目前已用的設計名稱：方案庫 ∪ 地圖上導彈的有效設計名稱。</summary>
        public static HashSet<string> UsedNames(Map map, string except = null)
        {
            HashSet<string> used = new HashSet<string>();
            foreach (MissileDesignBlueprint bp in MissileDesignBlueprintDatabase.All)
            {
                if (!bp.name.NullOrEmpty()) { used.Add(bp.name); }
            }
            if (map != null)
            {
                foreach (ThingDef d in MissileAssemblyUtility.MissileDefs)
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(d))
                    {
                        string n = t.TryGetComp<CompMissileConfig>()?.DesignName;
                        if (n != null) { used.Add(n); }
                    }
                }
            }
            if (except != null) { used.Remove(except); }
            return used;
        }

        public static bool IsValidName(string name)
            => !name.NullOrEmpty() && name.Trim().Length > 0 && name.Length <= MaxNameLength && ValidSymbolRegex.IsMatch(name);
    }
}
