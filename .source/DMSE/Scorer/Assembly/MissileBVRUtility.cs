using RimWorld;
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
        public static bool TryRegister(MissileIncoming missile, IntVec3 pos)
        {
            if (missile == null || !missile.Spawned || missile.bvrHandled) { return false; }

            Map map = missile.Map;
            MapComponent_BVRCombat comp = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null) { return false; }

            Faction defender = comp.ResolveDefender(missile.attacker);
            if (defender == null)
            {
                if (Prefs.DevMode)
                {
                    string atk = missile.attacker != null ? missile.attacker.Name : "null";
                    Log.Message($"[DMSE BVR] 導彈未被攔截：找不到對 {atk} 有敵意且具運作中搜索雷達的防禦方"
                        + $"（地圖上搜索雷達數={comp.searchRadars.Count}）。導彈將直接落地。");
                }
                return false;
            }

            missile.bvrHandled = true;
            missile.DeSpawn();
            comp.RegisterIncoming(missile, pos, defender);

            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE BVR] {defender.Name} 攔截來襲導彈"
                    + (missile.attacker != null ? "（來自 " + missile.attacker.Name + "）" : "")
                    + $"，目前波次數={comp.Waves.Count}");
            }
            return true;
        }
    }
}
