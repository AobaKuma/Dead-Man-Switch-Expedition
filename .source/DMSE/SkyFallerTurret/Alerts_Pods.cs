using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;
using Verse.Noise;

namespace DMSE
{
    public class Alerts_Pods : Alert
    {
        public override string GetLabel()
        {
            Map map = Find.CurrentMap;
            if (map != null && map.GetComponent<MapComponent_InterceptSkyfaller>().Pods is 
                var list && list.Count is 
                int count && list.First().tickToSpawn is int time)
            {
                return "DMS_Alerts_Pods".Translate(count,(time - Find.TickManager.TicksGame).ToStringTicksToPeriod());
            }
            return base.GetLabel();
        }
        public override AlertReport GetReport()
        {
            Map map = Find.CurrentMap;
            if (map != null && map.GetComponent<MapComponent_InterceptSkyfaller>().Pods.Any()) 
            {
                return AlertReport.Active;
            }
            return AlertReport.Inactive;
        }
        protected override void OnClick()
        {
            base.OnClick();
            Map map = Find.CurrentMap;
            if (map != null && map.GetComponent<MapComponent_InterceptSkyfaller>().Pods is var list
                && list.Any())
            {
                CameraJumper.TryJump(list.First().position,map);
            }
        }
    }
}
