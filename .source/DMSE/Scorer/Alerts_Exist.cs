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
    public class Alerts_Exist : Alert
    {
        public override string GetLabel()
        {
            Map map = Find.CurrentMap;
            if (map != null && GameComponent_MissileEngage.Comp.times.TryGetValue(map) is int time)
            {
                return "DMS_Exist".Translate((time - Find.TickManager.TicksGame)
                    .ToStringTicksToPeriod(true));
            }
            return base.GetLabel();
        }
        public override AlertReport GetReport()
        {
            Map map = Find.CurrentMap;
            if (map != null && GameComponent_MissileEngage.Comp.times.ContainsKey(map)) 
            {
                return AlertReport.Active;
            }
            return AlertReport.Inactive;
        }
    }
}
