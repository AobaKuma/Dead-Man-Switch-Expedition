using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 單一彈體的設計名稱規則（<see cref="MissileDesignNamer"/> 的資料面）：
    /// 型號代字、編號範圍、後綴與樣板包。一個彈體綁一個 Namer（<see cref="MissileBodyDef.namer"/>）；
    /// 沒綁的彈體沿用通用樣板 <c>DMSE_NamerMissileDesign</c>，只由部件詞組成、沒有型號代字。
    ///
    /// 樣板可用符號：<c>[body] [warhead] [guidance] [propulsion] [payload]</c>
    /// 與型號類的三個 <c>[designator] [num] [suffix]</c>。
    /// [designator] 由 Def 的 designator 欄位注入（可翻譯）；[num]／[suffix] 在抽定樣板的當下
    /// 就換成實際文字（成為規則字串的一部分），所以之後改部件只換詞、不換編號。
    /// </summary>
    public class MissileNamerDef : Def
    {
        /// <summary>此型號的 r_name 樣板包；null 時用通用的 DMSE_NamerMissileDesign。</summary>
        public RulePackDef pack;

        /// <summary>
        /// 型號代字（樣板中的 [designator]），例如英文 YJ、中文「鷹擊」。
        /// 空 = 樣板中的 [designator]（連同附著的分隔符）整段剔除。
        /// </summary>
        [MustTranslate]
        public string designator;

        /// <summary>[num] 的編號下界（含）。</summary>
        public int numberMin = 1;

        /// <summary>[num] 的編號上界（含）；小於 numberMin 時只取 numberMin。</summary>
        public int numberMax = 99;

        /// <summary>&gt;0 時編號左補零到這個位數（例如 2 → 07）。</summary>
        public int numberDigits = 0;

        /// <summary>[suffix] 的候選後綴（如 D／M／U）；空 = 樣板中的 [suffix] 整段剔除。</summary>
        public List<string> suffixes;

        /// <summary>可注入符號表的代字；未設定時為 null（等同該符號不存在）。</summary>
        public string Designator => designator.NullOrEmpty() ? null : designator;

        /// <summary>抽一個型號編號，依 <see cref="numberDigits"/> 補零。</summary>
        public string Number()
        {
            int n = Rand.RangeInclusive(numberMin, numberMax < numberMin ? numberMin : numberMax);
            return numberDigits > 0 ? n.ToString("D" + numberDigits) : n.ToString();
        }

        /// <summary>抽一個後綴；沒有後綴清單時為 null。</summary>
        public string Suffix() => suffixes.NullOrEmpty() ? null : suffixes.RandomElement();
    }
}
