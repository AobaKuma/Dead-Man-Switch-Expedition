using RimWorld;
using System.Linq;
using Verse;

namespace DMSE
{
    /// <summary>來襲導彈與 BVR 系統的銜接。</summary>
    public static class MissileBVRUtility
    {
        /// <summary>
        /// 若地圖上有與導彈攻擊方敵對的防禦方雷達，將此（已生成的）導彈交給 BVR 排程攔截：
        /// 先暫時收起（DeSpawn），由 BVR 在攔截窗口內處理；未攔截則於窗口結束自動落地引爆。
        /// 回傳是否成功交給 BVR。
        /// </summary>
        public static bool TryRegister(Thing thing, IntVec3 pos)
        {
            if (!(thing is IIncomingMissile missile) || !thing.Spawned || missile.BvrHandled) { return false; }

            Map map = thing.Map;
            MapComponent_BVRCombat comp = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null) { return false; }

            // Terminal defense does not require a surviving search radar.
            Faction defender = ResolveDefender(comp, missile);
            if (defender == null)
            {
                if (Prefs.DevMode)
                {
                    string atk = missile.Attacker != null ? missile.Attacker.Name : "null";
                    Log.Message($"[DMSE BVR] 導彈未被攔截：找不到對 {atk} 有敵意且具運作中搜索雷達的防禦方"
                        + $"（地圖上搜索雷達數={comp.searchRadars.Count}）。導彈將直接落地。");
                }
                return false;
            }

            missile.BvrHandled = true;
            thing.DeSpawn();
            if (missile.WorldPhaseDone) comp.RegisterTerminal(thing, pos, defender);
            else comp.RegisterIncoming(thing, pos, defender);

            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE BVR] {defender.Name} 攔截來襲導彈"
                    + (missile.Attacker != null ? "（來自 " + missile.Attacker.Name + "）" : "")
                    + $"，目前波次數={comp.Waves.Count}");
            }
            return true;
        }

        /// <summary>
        /// 盤旋飛控：把導彈收進 BVR，建立盤旋波次。與 <see cref="TryRegister"/> 不同，**沒有防禦方也照樣盤旋**：
        /// 導彈在目標上空等待導引頭出現目標，期間只有末端防禦會接戰。回傳是否成功進入盤旋。
        /// </summary>
        public static bool TryRegisterLoitering(Thing thing, IntVec3 pos, MissileConfig config)
        {
            if (!(thing is IIncomingMissile missile) || !thing.Spawned || missile.BvrHandled) { return false; }
            int loiterTicks = config != null ? config.LoiterTicks : 0;
            if (loiterTicks <= 0) { return false; }

            Map map = thing.Map;
            MapComponent_BVRCombat comp = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null) { return false; }

            missile.BvrHandled = true;
            thing.DeSpawn();
            comp.RegisterLoiter(thing, pos, ResolveDefender(comp, missile), loiterTicks, config.LoiterRetryTicks);
            return true;
        }

        /// <summary>負責攔截此導彈的防禦方：世界層已結算的導彈只找有 CIWS 的敵對陣營，其餘找搜索雷達。</summary>
        private static Faction ResolveDefender(MapComponent_BVRCombat comp, IIncomingMissile missile)
        {
            if (!missile.WorldPhaseDone) { return comp.ResolveDefender(missile.Attacker); }
            return comp.ciwsTurrets.Where(c => c.Active && c.parent.Faction != null
                && missile.Attacker != null && c.parent.Faction.HostileTo(missile.Attacker))
                .OrderByDescending(c => c.parent.Faction.IsPlayer).Select(c => c.parent.Faction).FirstOrDefault();
        }
    }
}
