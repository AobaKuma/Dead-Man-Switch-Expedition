using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 攔截敵對空投的生成：若地圖上有運作中的搜索雷達，將 incoming skyfaller 收進
    /// <see cref="MapComponent_BVRCombat"/> 排程攔截，而非讓它直接落地。
    /// </summary>
    [HarmonyPatch(typeof(SkyfallerMaker), nameof(SkyfallerMaker.SpawnSkyfaller),
        new Type[] { typeof(ThingDef), typeof(Thing), typeof(IntVec3), typeof(Map) })]
    public class Patch_MakeDropPodAt
    {
        [HarmonyPostfix]
        public static void postfix(Skyfaller __result, Thing innerThing, IntVec3 pos, Map map)
        {
            if (__result == null || map == null) { return; }
            if (__result is InterceptProjectile) { return; } // 別攔截自己的攔截彈。

            Faction dropFaction = BVRDetection.GetDropFaction(innerThing);
            if (dropFaction == null) { return; } // 內容物無可判定陣營，不介入。

            MapComponent_BVRCombat comp = map.GetComponent<MapComponent_BVRCombat>();
            if (comp == null) { return; }

            // 找出有資格攔截此空投的防禦方（擁有雷達且與空投敵對的陣營）。
            Faction defender = comp.ResolveDefender(dropFaction);
            if (defender == null)
            {
                return; // 沒有能對抗它的防禦方雷達，照常落地。
            }

            __result.DeSpawn();
            comp.RegisterIncoming(__result, pos, defender);

            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE BVR] {defender.Name} 攔截 {dropFaction.Name} 的空投：{innerThing.LabelCap} @ {pos}");
            }
        }
    }

    /// <summary>目標識別輔助。</summary>
    public static class BVRDetection
    {
        /// <summary>取得空投內容物（pawn，含 ActiveTransporter 內）中第一個有陣營的 pawn 的陣營；無則回傳 null。</summary>
        public static Faction GetDropFaction(Thing innerThing)
        {
            foreach (Pawn pawn in PawnsIn(innerThing))
            {
                if (pawn.Faction != null)
                {
                    return pawn.Faction;
                }
            }
            return null;
        }

        /// <summary>
        /// 空投內容物中的 pawn：innerThing 本身，或 ActiveTransporter 艙內／穿梭機 CompTransporter 內的所有 pawn。
        /// 艙內可能同時裝著多個物品（例如圍攻的補給艙），不能用 ActiveTransporterInfo.SingleContainedThing（多於一件會報錯）。
        /// </summary>
        public static IEnumerable<Pawn> PawnsIn(Thing innerThing)
        {
            if (innerThing is Pawn pawn)
            {
                yield return pawn;
                yield break;
            }
            // 其他 mod 自己的載具（例如自帶容器的突擊艇）
            if (innerThing is IBVRPayloadHolder holder)
            {
                foreach (Pawn aboard in holder.PawnsAboard)
                {
                    if (aboard != null) { yield return aboard; }
                }
                yield break;
            }
            ThingOwner contents = (innerThing as ActiveTransporter)?.Contents?.innerContainer
                ?? innerThing.TryGetComp<CompTransporter>()?.innerContainer; // 穿梭機
            if (contents == null) { yield break; }
            for (int i = 0; i < contents.Count; i++)
            {
                if (contents[i] is Pawn contained)
                {
                    yield return contained;
                }
            }
        }
    }

    /// <summary>
    /// 空投／來襲物的內容物不是 <see cref="ActiveTransporter"/> 或 <see cref="CompTransporter"/> 時，由它自己列出載著的 pawn：
    /// 地圖層 BVR 用來判定陣營（決定誰攔截）與擊落時處理艇上人員（<see cref="BVRDetection.PawnsIn"/>）。
    /// </summary>
    public interface IBVRPayloadHolder
    {
        IEnumerable<Pawn> PawnsAboard { get; }
    }

    /// <summary>
    /// 地圖層來襲物在警報裡顯示的名稱（<see cref="BVRTarget.DisplayLabel"/>）：例如聲納在遠處只聽得到「未知接觸」，靠近才分得出是什麼。
    /// 回傳 null 或空字串用預設名稱。
    /// </summary>
    public interface IBVRContactLabel
    {
        string ContactLabel(BVRTarget target, int ticksToImpact);
    }

    /// <summary>
    /// 來襲物被攔截成功時，由它自己決定結果（例如水下的「沉沒」），不用預設的殘骸空投艙。
    /// 實作在來襲物的內容物上（<see cref="BVRTarget.Payload"/> 的 skyfaller 裡第一件）。回傳 true 表示已處理，預設流程不再執行；
    /// 處理時自己呼叫 <see cref="BVRTarget.HandleInterceptedPawns"/> 與 <see cref="BVRTarget.Discard"/>。
    /// </summary>
    public interface IInterceptResolver
    {
        bool TryResolveIntercept(BVRTarget target, Map map, IntVec3 debrisCell);
    }
}
