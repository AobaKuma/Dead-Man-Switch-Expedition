using RimWorld;
using System.Linq;
using Verse;

namespace DMSE
{
    public class Alerts_Pods : Alert
    {
        // 只提示由玩家防禦的波次（玩家自家基地防空）；玩家進攻敵方基地時，敵方防空不在此提示。
        private static BVRWave FirstPlayerWave(Map map)
        {
            MapComponent_BVRCombat comp = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null) { return null; }
            return comp.Waves.FirstOrDefault(w => w.defenderFaction != null && w.defenderFaction.IsPlayer);
        }

        public override string GetLabel()
        {
            BVRWave wave = FirstPlayerWave(Find.CurrentMap);
            if (wave != null)
            {
                return "DMSE.Alert.Pods".Translate(wave.targets.Count,
                    (wave.tickToImpact - Find.TickManager.TicksGame).ToStringTicksToPeriod());
            }
            return base.GetLabel();
        }

        public override AlertReport GetReport()
        {
            return FirstPlayerWave(Find.CurrentMap) != null ? AlertReport.Active : AlertReport.Inactive;
        }

        protected override void OnClick()
        {
            base.OnClick();
            Map map = Find.CurrentMap;
            BVRWave wave = FirstPlayerWave(map);
            if (wave != null && wave.targets.Any())
            {
                CameraJumper.TryJump(wave.targets.First().position, map);
            }
        }
    }
}
