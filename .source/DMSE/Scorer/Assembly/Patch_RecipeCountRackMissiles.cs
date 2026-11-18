using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 「維持 X 個」配方把已裝進發射架／儲存架（含野戰防空砲的 comp 彈艙）的導彈算進現有數量。
    ///
    /// 原版 <see cref="RecipeWorkerCounter.CountProducts"/> 有兩條路：
    ///   1) 產品算資源（導彈繼承 ResourceBase）且帳單沒有篩選條件 → 只看資源計數器，也就是倉儲格內的物品，
    ///      架內（unspawned）的導彈一枚都不算；
    ///   2) 其餘情況 → 數地圖上的物品，再加上 <see cref="IHaulSource"/> 的容器（<see cref="Building_MissileRack"/>），
    ///      但 comp 型彈艙（<see cref="CompMissileRackStorage"/>）不是 IHaulSource，同樣漏算。
    /// 指定「只算某個倉庫」時照原版，不加架內數量。
    /// </summary>
    [HarmonyPatch(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountProducts))]
    public static class Patch_RecipeCountRackMissiles
    {
        public static void Postfix(RecipeWorkerCounter __instance, Bill_Production bill, ref int __result)
        {
            RecipeDef recipe = __instance.recipe;
            if (bill?.Map == null || recipe?.products == null || recipe.products.Count == 0) { return; }
            ThingDef def = recipe.products[0].thingDef;
            if (def == null || bill.GetIncludeSlotGroup() != null) { return; }

            IReadOnlyList<IMissileRack> racks = MapComponent_MissileRacks.For(bill.Map)?.Racks;
            if (racks == null || racks.Count == 0) { return; }

            bool resourcePath = UsesResourceCounter(def, bill);
            int extra = 0;
            for (int r = 0; r < racks.Count; r++)
            {
                IMissileRack rack = racks[r];
                Thing building = rack.RackThing;
                if (building == null || !building.Spawned || building.Faction != Faction.OfPlayer) { continue; }
                // 走一般路徑時原版已數過 IHaulSource 的容器，不重複計算。
                if (!resourcePath && building is IHaulSource source
                    && ReferenceEquals(source.GetDirectlyHeldThings(), rack.GetDirectlyHeldThings())) { continue; }
                IReadOnlyList<Thing> held = rack.HeldThings;
                if (held == null) { continue; }
                for (int i = 0; i < held.Count; i++)
                {
                    Thing t = held[i];
                    if (t != null && __instance.CountValidThing(t, bill, def)) { extra += t.stackCount; }
                }
            }
            __result += extra;
        }

        /// <summary>與原版 CountProducts 判斷「只看資源計數器」的條件相同。</summary>
        private static bool UsesResourceCounter(ThingDef def, Bill_Production bill)
            => def.CountAsResource && !bill.includeEquipped
               && (bill.includeTainted || !def.IsApparel || !def.apparel.careIfWornByCorpse)
               && bill.GetIncludeSlotGroup() == null
               && bill.hpRange.min == 0f && bill.hpRange.max == 1f
               && bill.qualityRange.min == QualityCategory.Awful && bill.qualityRange.max == QualityCategory.Legendary
               && !bill.limitToAllowedStuff;
    }
}
