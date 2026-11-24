using RimWorld;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 誘餌彈體設定（<see cref="MissileBodyDef.decoy"/>；null = 不是誘餌）。見 docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §7。
    /// 誘餌的雷達特徵與外觀在釋放時取自它模仿的彈頭（<see cref="MissileWorldObject.mimicBody"/>），所以同一種誘餌
    /// 跟著 MIRV 時像重返載具、跟著單彈頭時像整枚導彈。
    /// </summary>
    public class DecoyProps
    {
        /// <summary>釋放時找不到可模仿的彈頭時用的外觀。</summary>
        public MissileBodyDef mimicBody;

        /// <summary>擬真等級（0–3），越高越難被識別（P4）。</summary>
        public int fidelity = 1;

        /// <summary>抵達已有地圖的目標時進入地圖層的機率；0 = 一定在大氣層燒毀（輕型誘餌）。</summary>
        public float reentrySurvivalChance;

        /// <summary>落地不爆炸，只有揚塵與撞擊聲。</summary>
        public bool inertImpact = true;
    }

    public static class DecoyImpactUtility
    {
        /// <summary>惰性誘餌落地：揚塵、碎屑與金屬撞擊聲，沒有傷害、戰鬥部、載荷或天光。</summary>
        public static void InertImpact(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map)) { return; }
            for (int i = 0; i < 4; i++)
            {
                FleckMaker.ThrowDustPuffThick(cell.ToVector3Shifted() + Gen.RandomHorizontalVector(0.8f), map, Rand.Range(1.5f, 2.5f),
                    new UnityEngine.Color(0.55f, 0.5f, 0.45f));
            }
            FilthMaker.TryMakeFilth(cell, map, ThingDefOf.Filth_RubbleRock);
            SoundDefOf.MetalHitImportant.PlayOneShot(new TargetInfo(cell, map));
        }
    }
}
