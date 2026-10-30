using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 組裝台的生產成本與工作量。兩者都與「在列印台做出預設彈 → 用 ITab 改裝成目標構型」等價：
    /// 組裝台是便利，不是折扣（見 docs/導彈組裝台與裝配方案_實作計畫.md §8.3、§8.4）。
    /// </summary>
    public static class MissileAssemblyCost
    {
        /// <summary>
        /// 一枚的材料 ＝ 物品配方材料 ＋ 新增部件成本 － floor(refundFraction × 移除的預設部件成本)，
        /// 逐材料淨額後夾到 ≥ 0。移除部件的退料與 <see cref="CompMissileConfig.ApplyAssembly"/> 相同。
        /// </summary>
        public static Dictionary<ThingDef, int> RequiredFor(MissileConfig cfg)
        {
            Dictionary<ThingDef, int> req = new Dictionary<ThingDef, int>();
            ThingDef item = MissileItemUtility.ItemDefFor(cfg?.body);
            if (item == null) { return req; }
            Add(req, item.costList, 1);
            MissileConfig defaults = MissileConfig.NewWithDefaults(cfg.body);
            float refundFrac = cfg.body.refundFraction;
            foreach (MissilePartDef p in cfg.parts)
            {
                if (p != null && !defaults.parts.Contains(p)) { Add(req, p.costList, 1); }
            }
            foreach (MissilePartDef p in defaults.parts)
            {
                if (cfg.parts.Contains(p) || p.costList == null) { continue; }
                foreach (ThingDefCountClass c in p.costList)
                {
                    int amt = Mathf.FloorToInt(c.count * refundFrac);
                    if (amt <= 0) { continue; }
                    req.TryGetValue(c.thingDef, out int cur);
                    req[c.thingDef] = cur - amt;
                }
            }
            List<ThingDef> keys = new List<ThingDef>(req.Keys);
            foreach (ThingDef k in keys)
            {
                if (req[k] <= 0) { req.Remove(k); }
            }
            return req;
        }

        /// <summary>一枚的工作量 ＝ 配方工作量 ＋（與預設構型不同時）彈體裝配工作量。</summary>
        public static int WorkFor(MissileConfig cfg)
        {
            ThingDef item = MissileItemUtility.ItemDefFor(cfg?.body);
            if (item == null) { return 0; }
            RecipeDef recipe = MissileItemUtility.RecipeFor(item);
            float work = recipe != null ? recipe.WorkAmountTotal(null) : item.GetStatValueAbstract(StatDefOf.WorkToMake);
            if (!CompMissileConfig.ConfigsEqual(cfg, MissileConfig.NewWithDefaults(cfg.body)))
            {
                work += cfg.body.assembleWorkAmount;
            }
            return Mathf.Max(1, Mathf.RoundToInt(work));
        }

        /// <summary>
        /// 地圖上的現有數量。<see cref="ResourceCounter"/> 只統計列為資源的 def，其餘以 listerThings 加總
        /// （含禁用物品；只當作「庫存」提示，不代表可取用）。
        /// </summary>
        public static int CountOnMap(Map map, ThingDef def)
        {
            if (map == null || def == null) { return 0; }
            if (def.CountAsResource) { return map.resourceCounter.GetCount(def); }
            int n = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++) { n += things[i].stackCount; }
            return n;
        }

        private static void Add(Dictionary<ThingDef, int> d, List<ThingDefCountClass> list, int sign)
        {
            if (list == null) { return; }
            foreach (ThingDefCountClass c in list)
            {
                d.TryGetValue(c.thingDef, out int cur);
                d[c.thingDef] = cur + sign * c.count;
            }
        }
    }
}
