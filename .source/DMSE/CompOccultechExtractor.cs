using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 2x1「Occultech 萃取器（石碑解碼器）」的設定。放置在正面對準資料石碑
    /// (<see cref="CompOccultechSource"/>) 並通電後，會自動、持續地從石碑抽取知識點
    /// 並注入 Occultech 研究樹。
    /// </summary>
    public class CompProperties_OccultechExtractor : CompProperties
    {
        /// <summary>每遊戲日萃取（並注入研究）的知識點數。</summary>
        public float knowledgePerDay = 600f;

        public CompProperties_OccultechExtractor()
        {
            compClass = typeof(CompOccultechExtractor);
        }
    }

    /// <summary>
    /// 自動萃取器：需要電力 + 正面對準未耗盡的石碑 + 尚有研究中的 Occultech 專案。
    /// 三者皆滿足時每 rare-tick 抽取知識點，透過
    /// <see cref="OccultechKnowledgeUtility.AddKnowledge"/> 注入 Restricted 類別（並向上溢流）。
    /// 任一條件不滿足即轉為待機，不抽取石碑儲量、僅耗待機電力。
    /// </summary>
    public class CompOccultechExtractor : ThingComp
    {
        private CompPowerTrader powerComp;
        // 累積未滿 1 點的小數，避免每 tick 都做浮點注入。
        private float buffer;

        public CompProperties_OccultechExtractor Props => (CompProperties_OccultechExtractor)props;

        private float KnowledgePerRareTick =>
            Props.knowledgePerDay * GenTicks.TickRareInterval / GenDate.TicksPerDay;

        private bool PowerOk => powerComp == null || powerComp.PowerOn;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            powerComp = parent.GetComp<CompPowerTrader>();
        }

        /// <summary>
        /// 只掃描本建築「正面」（面向方向 parent.Rotation）緊鄰的那一排格，
        /// 回傳第一個未耗盡的知識源（石碑）；找不到回傳 null。
        /// 換言之：石碑必須正對著解碼器的正面才會生效。
        /// </summary>
        private CompOccultechSource FindSourceInFront()
        {
            if (!parent.Spawned || parent.Map == null)
            {
                return null;
            }

            Map map = parent.Map;
            CellRect rect = parent.OccupiedRect();
            IntVec3 facing = parent.Rotation.FacingCell; // 正面單位方向
            CompOccultechSource fallback = null;

            foreach (IntVec3 cell in rect.Cells)
            {
                IntVec3 ahead = cell + facing;
                // 只取「正面前緣」外一格：ahead 需在建築外且在地圖內。
                if (rect.Contains(ahead) || !ahead.InBounds(map))
                {
                    continue;
                }
                List<Thing> things = ahead.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    CompOccultechSource src = (things[i] as ThingWithComps)?.GetComp<CompOccultechSource>();
                    if (src == null)
                    {
                        continue;
                    }
                    if (!src.Depleted)
                    {
                        return src;
                    }
                    fallback = src; // 記住正面已耗盡的石碑，供狀態顯示用。
                }
            }
            return fallback;
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            if (!parent.Spawned)
            {
                return;
            }

            CompOccultechSource source = FindSourceInFront();
            // 只有在「有電力 + 正面有未耗盡石碑 + 尚有研究中的 Occultech 專案」時才運作。
            bool active = PowerOk && source != null && !source.Depleted
                          && OccultechKnowledgeUtility.HasResearchTarget();

            // 依運作狀態調整耗電（有源在抽=滿載，否則=待機）。
            if (powerComp != null)
            {
                if (active)
                {
                    powerComp.PowerOutput = -powerComp.Props.PowerConsumption;
                }
                else if (powerComp.Props.idlePowerDraw >= 0f)
                {
                    powerComp.PowerOutput = -powerComp.Props.idlePowerDraw;
                }
            }

            if (!active)
            {
                return;
            }

            buffer += KnowledgePerRareTick;
            if (buffer < 1f)
            {
                return;
            }

            float want = Mathf.Floor(buffer);
            float got = source.TryExtract(want);
            buffer -= want;

            if (got > 0f)
            {
                OccultechKnowledgeUtility.AddKnowledge(OccultechKnowledgeUtility.RestrictedCategory, got);
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned)
            {
                return null;
            }

            if (!PowerOk)
            {
                return "DMSE_Occultech_ExtractorNoPower".Translate();
            }

            CompOccultechSource source = FindSourceInFront();
            if (source == null)
            {
                return "DMSE_Occultech_ExtractorNoSource".Translate();
            }
            if (source.Depleted)
            {
                return "DMSE_Occultech_ExtractorSourceDepleted".Translate();
            }
            if (!OccultechKnowledgeUtility.HasResearchTarget())
            {
                return "DMSE_Occultech_ExtractorNoResearch".Translate();
            }

            return "DMSE_Occultech_ExtractorWorking".Translate(
                Props.knowledgePerDay.ToString("F0"),
                source.ReserveRemaining.ToString("F0"));
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref buffer, "occultechExtractBuffer", 0f);
        }
    }
}
