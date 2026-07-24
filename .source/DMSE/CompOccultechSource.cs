using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 3x3「Occultech 知識源」的設定。內含有限的可萃取知識儲量；
    /// 由相鄰的萃取器 (<see cref="CompOccultechExtractor"/>) 逐步抽取。
    /// </summary>
    public class CompProperties_OccultechSource : CompProperties
    {
        /// <summary>本源可被萃取的知識點總量（耗盡後即成為空殼）。</summary>
        public float totalReserve = 4000f;

        public CompProperties_OccultechSource()
        {
            compClass = typeof(CompOccultechSource);
        }
    }

    /// <summary>
    /// 有限知識源：提供 <see cref="TryExtract"/> 供萃取器抽取知識點，抽乾後保留為
    /// 可拆除的空殼（依使用者需求「變空」而非直接消失）。
    /// </summary>
    public class CompOccultechSource : ThingComp
    {
        // -1 代表尚未初始化，於 PostSpawnSetup 設為 totalReserve。
        private float reserveRemaining = -1f;

        public CompProperties_OccultechSource Props => (CompProperties_OccultechSource)props;

        public float ReserveRemaining => Mathf.Max(0f, reserveRemaining);

        public float TotalReserve => Props.totalReserve;

        public bool Depleted => reserveRemaining <= 0.0001f;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (reserveRemaining < 0f)
            {
                reserveRemaining = Props.totalReserve;
            }
        }

        /// <summary>
        /// 嘗試抽取至多 <paramref name="requested"/> 點；回傳實際抽出的量（受剩餘儲量限制）。
        /// </summary>
        public float TryExtract(float requested)
        {
            if (requested <= 0f || Depleted)
            {
                return 0f;
            }
            float amount = Mathf.Min(requested, reserveRemaining);
            reserveRemaining -= amount;
            if (reserveRemaining < 0f)
            {
                reserveRemaining = 0f;
            }
            return amount;
        }

        public override string CompInspectStringExtra()
        {
            if (Depleted)
            {
                return "DMSE_Occultech_SourceDepleted".Translate();
            }
            return "DMSE_Occultech_SourceRemaining".Translate(
                ReserveRemaining.ToString("F0"),
                TotalReserve.ToString("F0"));
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            foreach (StatDrawEntry e in base.SpecialDisplayStats())
            {
                yield return e;
            }
            yield return new StatDrawEntry(
                StatCategoryDefOf.Building,
                "DMSE_Occultech_SourceStatLabel".Translate(),
                ReserveRemaining.ToString("F0") + " / " + TotalReserve.ToString("F0"),
                "DMSE_Occultech_SourceStatDesc".Translate(),
                1000);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref reserveRemaining, "reserveRemaining", -1f);
        }
    }
}
