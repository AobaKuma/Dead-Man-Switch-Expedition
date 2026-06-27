using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 當 parent 未被存放於指定的庇護建築內時，緩慢扣除其耐久 (HitPoints)。
    /// 主要用於導彈：放在發射器/彈藥庫等容器建築內可安全保存；
    /// 散落在外的彈體則會逐漸劣化，耐久歸零後依設定摧毀。
    ///
    /// 庇護判定（任一成立即視為受保護）：
    ///   1. checkContainer：parent 被裝入某 IThingHolder，且其鏈上存在 def ∈ shelterBuildings 的 Building。
    ///   2. checkCell：parent 已生成 (Spawned)，且其佔用格上存在 def ∈ shelterBuildings 的 Building
    ///      （涵蓋會讓物品保持 Spawned 的儲存類建築，如貨架/開放式彈架）。
    ///
    /// 注意：此 Comp 仰賴 parent 會 tick。一般 item (ResourceBase) 預設 tickerType=Never，
    /// 因此須在對應 ThingDef 設定 &lt;tickerType&gt;Rare&lt;/tickerType&gt;（本 Comp 使用 CompTickRare）。
    /// </summary>
    public class CompProperties_DurabilityDecay : CompProperties
    {
        /// <summary>未受庇護時，每遊戲日損失的耐久 (HitPoints)。</summary>
        public float hitPointsPerDay = 5f;

        /// <summary>可庇護 parent 的建築 def 清單；清單為空時 parent 永遠視為未受庇護。</summary>
        public List<ThingDef> shelterBuildings = new List<ThingDef>();

        /// <summary>是否沿 ParentHolder 鏈尋找容器建築（裝入發射器/儲存櫃等）。</summary>
        public bool checkContainer = true;

        /// <summary>是否檢查 parent 佔用格上的建築（貨架/彈架等會保持物品 Spawned 的儲存）。</summary>
        public bool checkCell = true;

        /// <summary>是否在 parent 位於屋頂下時也視為受庇護（與 shelterBuildings 為 OR 關係）。</summary>
        public bool shelteredWhenRoofed = false;

        /// <summary>耐久降到下限時是否摧毀 parent。</summary>
        public bool destroyWhenDepleted = true;

        /// <summary>耐久下限；扣到此值（含）以下即視為耗盡。預設 0。</summary>
        public int minHitPoints = 0;

        /// <summary>摧毀時使用的 DestroyMode（Vanish 不留殘骸，類似自然劣化）。</summary>
        public DestroyMode destroyMode = DestroyMode.Vanish;

        /// <summary>摧毀時是否於訊息列提示。</summary>
        public bool sendDestroyMessage = true;

        /// <summary>摧毀提示用的翻譯 key，格式參數 {0} = parent 標籤。</summary>
        public string destroyMessageKey = "DMSE_MissileDecayedAway";

        public CompProperties_DurabilityDecay()
        {
            compClass = typeof(CompDurabilityDecay);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }

            if (parentDef != null && !parentDef.useHitPoints)
            {
                yield return "CompDurabilityDecay 需要 parent useHitPoints=true。";
            }
            if (parentDef != null && parentDef.tickerType == TickerType.Never)
            {
                yield return "CompDurabilityDecay 需要 tickerType 非 Never（建議 Rare），否則不會 tick。";
            }
        }
    }

    public class CompDurabilityDecay : ThingComp
    {
        // 累積未滿 1 點的耐久損失（HitPoints 為整數，需緩衝）。
        private float decayBuffer;

        public CompProperties_DurabilityDecay Props => (CompProperties_DurabilityDecay)props;

        // CompTickRare 每 GenTicks.TickRareInterval (250) tick 觸發一次。
        private float HitPointsPerRareTick =>
            Props.hitPointsPerDay * GenTicks.TickRareInterval / GenDate.TicksPerDay;

        public bool IsSheltered
        {
            get
            {
                if (Props.shelteredWhenRoofed && parent.Spawned &&
                    parent.Map != null && parent.Position.Roofed(parent.Map))
                {
                    return true;
                }

                List<ThingDef> shelters = Props.shelterBuildings;
                if (shelters == null || shelters.Count == 0)
                {
                    return false;
                }

                // 1) 容器鏈：沿 ParentHolder 往上找庇護建築。
                if (Props.checkContainer)
                {
                    IThingHolder holder = parent.ParentHolder;
                    while (holder != null)
                    {
                        if (holder is Building b && shelters.Contains(b.def))
                        {
                            return true;
                        }
                        holder = holder.ParentHolder;
                    }
                }

                // 2) 佔用格：尋找與 parent 重疊的庇護建築（貨架/彈架等）。
                if (Props.checkCell && parent.Spawned && parent.Map != null)
                {
                    Map map = parent.Map;
                    foreach (IntVec3 cell in parent.OccupiedRect())
                    {
                        if (!cell.InBounds(map))
                        {
                            continue;
                        }
                        List<Thing> things = cell.GetThingList(map);
                        for (int i = 0; i < things.Count; i++)
                        {
                            if (things[i] is Building bb && shelters.Contains(bb.def))
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();

            if (!parent.Spawned || parent.Destroyed)
            {
                return;
            }
            if (!parent.def.useHitPoints)
            {
                return;
            }
            if (IsSheltered)
            {
                return;
            }

            decayBuffer += HitPointsPerRareTick;
            if (decayBuffer < 1f)
            {
                return;
            }

            int loss = Mathf.FloorToInt(decayBuffer);
            decayBuffer -= loss;

            int newHp = parent.HitPoints - loss;
            if (newHp <= Props.minHitPoints)
            {
                if (Props.destroyWhenDepleted)
                {
                    IntVec3 pos = parent.Position;
                    Map map = parent.Map;
                    parent.HitPoints = Mathf.Max(0, Props.minHitPoints);

                    if (Props.sendDestroyMessage && !Props.destroyMessageKey.NullOrEmpty())
                    {
                        Messages.Message(
                            Props.destroyMessageKey.Translate(parent.Label),
                            new TargetInfo(pos, map),
                            MessageTypeDefOf.NegativeEvent);
                    }
                    parent.Destroy(Props.destroyMode);
                }
                else
                {
                    parent.HitPoints = Mathf.Max(1, Props.minHitPoints);
                }
                return;
            }

            parent.HitPoints = newHp;
        }

        public override string CompInspectStringExtra()
        {
            if (!parent.def.useHitPoints)
            {
                return null;
            }
            if (IsSheltered)
            {
                return "DMSE_MissileDecaySheltered".Translate();
            }
            return "DMSE_MissileDecayUnsheltered".Translate(Props.hitPointsPerDay.ToString("0.#"));
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref decayBuffer, "decayBuffer", 0f);
        }
    }
}
