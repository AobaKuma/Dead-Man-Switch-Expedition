using RimWorld;
using System.Linq;
using Verse;

namespace DMSE
{
    public class Alerts_Pods : Alert
    {
        // 只提示由玩家防禦的波次（玩家自家基地防空）；玩家進攻敵方基地時，敵方防空不在此提示。
        // 取「最快抵達」的攔截窗口；短時間內的多個可攔截物已於登記時合併到同一波次。
        private static BVRWave SoonestPlayerWave(Map map)
        {
            MapComponent_BVRCombat comp = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null) { return null; }

            BVRWave best = null;
            foreach (BVRWave w in comp.Waves)
            {
                if (w.defenderFaction == null || !w.defenderFaction.IsPlayer) { continue; }
                if (best == null || w.tickToImpact < best.tickToImpact) { best = w; }
            }
            return best;
        }

        public override string GetLabel()
        {
            BVRWave wave = SoonestPlayerWave(Find.CurrentMap);
            if (wave != null)
            {
                return "DMSE.Alert.Pods".Translate(wave.targets.Count,
                    (wave.tickToImpact - Find.TickManager.TicksGame).ToStringTicksToPeriod());
            }
            return base.GetLabel();
        }

        public override AlertReport GetReport()
        {
            return SoonestPlayerWave(Find.CurrentMap) != null ? AlertReport.Active : AlertReport.Inactive;
        }

        protected override void OnClick()
        {
            base.OnClick();
            Map map = Find.CurrentMap;
            BVRWave wave = SoonestPlayerWave(map);
            if (wave != null && wave.targets.Any())
            {
                CameraJumper.TryJump(wave.targets.First().position, map);
            }
        }
    }
}
