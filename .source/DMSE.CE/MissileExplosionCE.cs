using System.Collections.Generic;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE.CE
{
    /// <summary>
    /// 導彈部件（MissileBodyDef／MissilePartDef）上的 CE 爆炸設定，由 1.7/CE 的 XML 補丁加到 modExtensions。
    /// 主爆炸依序讀戰鬥部、彈體；戰鬥部效果讀戰鬥部；酬載效果讀酬載。每個欄位取第一個有設定的部件，
    /// 都沒有設定時用 <see cref="MissileExplosionCE"/> 的自動值（與 CE 砲彈同一套刻度）。
    /// </summary>
    public class ModExtension_CEExplosion : DefModExtension
    {
        /// <summary>爆炸的鈍擊穿甲（MPa）；-1 = CE 的傷害類型預設值（Bomb 為 62）。</summary>
        public float armorPenetration = -1f;

        /// <summary>乘在爆炸穿甲上的倍率；-1 = 未設定（1）。</summary>
        public float armorPenetrationFactor = -1f;

        /// <summary>直接傷害（刀刃、鎢針、燒灼）的 CE 穿甲：銳器為 mm RHA、鈍擊為 MPa；-1 = 由原版值換算。</summary>
        public float directArmorPenetration = -1f;

        /// <summary>明確指定的 CE 彈片（例如 Fragment_Large／Fragment_Small）；設定後取代自動彈片。空清單 = 不丟彈片。</summary>
        public List<ThingDefCountClass> fragments;

        /// <summary>自動彈片數的倍率；-1 = 未設定（1），0 = 不丟彈片。</summary>
        public float fragmentScale = -1f;

        /// <summary>彈片初速倍率；-1 = 未設定（1）。</summary>
        public float fragSpeedFactor = -1f;
    }

    /// <summary>導彈爆炸的 CE 實作：ExplosionCE（CE 穿甲）＋CE 彈片。</summary>
    public static class MissileExplosionCE
    {
        // 自動彈片：依 CE 155 mm 榴彈（546 傷害 → 40 大＋80 小）的比例換算，並設上限（彈片是真實投射物）。
        private const float DamagePerLargeFragment = 13.65f;
        private const float DamagePerSmallFragment = 6.8f;
        private const int MaxLargeFragments = 60;
        private const int MaxSmallFragments = 120;

        // 直接傷害沒有 CE 設定時，由原版刻度換算（原版 0.6 的刀刃 ≈ 15 mm RHA）。
        private const float SharpPenPerVanilla = 25f;
        private const float BluntPenPerVanilla = 40f;

        private static ThingDef fragmentLarge;
        private static ThingDef fragmentSmall;
        private static bool fragmentDefsResolved;

        private static ModExtension_CEExplosion ExtOf(Def def) => def?.GetModExtension<ModExtension_CEExplosion>();

        /// <summary>依來源順序取第一個「有設定」的值。</summary>
        private static float First(IReadOnlyList<Def> sources, System.Func<ModExtension_CEExplosion, float> get, float fallback)
        {
            if (sources == null) { return fallback; }
            for (int i = 0; i < sources.Count; i++)
            {
                ModExtension_CEExplosion ext = ExtOf(sources[i]);
                if (ext == null) { continue; }
                float v = get(ext);
                if (v >= 0f) { return v; }
            }
            return fallback;
        }

        private static List<ThingDefCountClass> ExplicitFragments(IReadOnlyList<Def> sources)
        {
            if (sources == null) { return null; }
            for (int i = 0; i < sources.Count; i++)
            {
                ModExtension_CEExplosion ext = ExtOf(sources[i]);
                if (ext?.fragments != null) { return ext.fragments; }
            }
            return null;
        }

        /// <summary>會產生彈片的爆炸：爆破類（Bomb、BombSuper 與 CE／其他模組的 *Bomb* 傷害）。EMP、湮滅、燃燒等不丟彈片。</summary>
        private static bool Fragmenting(DamageDef def)
            => def != null && (def == DamageDefOf.Bomb || def.defName.Contains("Bomb"));

        public static bool TryDo(MissileExplosionArgs args)
        {
            if (args?.map == null || args.damageDef == null || !args.center.InBounds(args.map)) { return false; }

            int damage = args.damageAmount >= 0 ? args.damageAmount : args.damageDef.defaultDamage;
            if (damage < 0) { damage = 1; }

            float pen = First(args.sources, e => e.armorPenetration, args.damageDef.defaultArmorPenetration);
            pen *= First(args.sources, e => e.armorPenetrationFactor, 1f);

            GenExplosionCE.DoExplosion(args.center, args.map, args.radius, args.damageDef, args.instigator,
                damage, pen);

            ThrowFragments(args, damage);
            return true;
        }

        private static void ThrowFragments(MissileExplosionArgs args, int damage)
        {
            // 倍率（例如溫壓戰鬥部 0）同時作用在明確指定的彈片（彈體的實際彈片數）與自動彈片上。
            float scale = First(args.sources, e => e.fragmentScale, 1f);
            if (scale <= 0f) { return; }
            List<ThingDefCountClass> explicitFrags = ExplicitFragments(args.sources);
            List<ThingDefCountClass> frags;
            if (explicitFrags != null)
            {
                // 只有爆破類爆炸才用彈體的彈片（同一具彈體的 EMP、湮滅酬載不丟）；複製一份再縮放，不改 def 上的清單。
                if (!Fragmenting(args.damageDef)) { return; }
                frags = new List<ThingDefCountClass>();
                foreach (ThingDefCountClass f in explicitFrags)
                {
                    int n = Mathf.RoundToInt(f.count * scale);
                    if (f.thingDef != null && n > 0) { frags.Add(new ThingDefCountClass(f.thingDef, n)); }
                }
            }
            else
            {
                if (!Fragmenting(args.damageDef)) { return; }
                ResolveFragmentDefs();
                if (fragmentLarge == null || fragmentSmall == null) { return; }
                int large = Mathf.Min(MaxLargeFragments, Mathf.RoundToInt(damage / DamagePerLargeFragment * scale));
                int small = Mathf.Min(MaxSmallFragments, Mathf.RoundToInt(damage / DamagePerSmallFragment * scale));
                frags = new List<ThingDefCountClass>();
                if (large > 0) { frags.Add(new ThingDefCountClass(fragmentLarge, large)); }
                if (small > 0) { frags.Add(new ThingDefCountClass(fragmentSmall, small)); }
            }
            if (frags.Count == 0) { return; }

            // CE 的 CompFragments.Throw 不需要真的掛在物件上（parent 為 null 時從地面高度、360° 丟出）。
            CompFragments comp = new CompFragments
            {
                props = new CompProperties_Fragments
                {
                    fragments = frags,
                    fragSpeedFactor = First(args.sources, e => e.fragSpeedFactor, 1f),
                },
            };
            comp.Throw(args.center.ToVector3Shifted(), args.map, args.instigator);
        }

        private static void ResolveFragmentDefs()
        {
            if (fragmentDefsResolved) { return; }
            fragmentLarge = DefDatabase<ThingDef>.GetNamedSilentFail("Fragment_Large");
            fragmentSmall = DefDatabase<ThingDef>.GetNamedSilentFail("Fragment_Small");
            fragmentDefsResolved = true;
        }

        public static float DirectPen(float vanillaPen, DamageDef damageDef, IReadOnlyList<Def> sources)
        {
            float set = First(sources, e => e.directArmorPenetration, -1f);
            if (set >= 0f) { return set; }
            if (damageDef?.armorCategory == DamageArmorCategoryDefOf.Sharp) { return vanillaPen * SharpPenPerVanilla; }
            if (damageDef?.armorCategory?.defName == "Blunt") { return vanillaPen * BluntPenPerVanilla; }
            return vanillaPen;
        }
    }
}
