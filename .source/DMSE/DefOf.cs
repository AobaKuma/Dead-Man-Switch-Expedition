using RimWorld;
using Verse;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DMSE
{
    [StaticConstructorOnStartup]
    [DefOf]
    public static class DMSE_DefOf
    {
        public static FactionDef DMS_Army;
        public static ThingDef DMSE_VacuumPump;
        public static ThingDef DMSE_NuclearThruster;
        public static WorldObjectDef Ship;
        public static GravshipComponentTypeDef AAA;
        
        // Missile guidance defs
        public static MissileGuidanceDef MissileGuidance_Ballistic;
        public static MissileGuidanceDef MissileGuidance_Radar;
        public static MissileGuidanceDef MissileGuidance_Proportional;
        
        // Warhead defs
        public static MissileWarheadDef MissileWarhead_HighExplosive;
        public static MissileWarheadDef MissileWarhead_Incendiary;
        public static MissileWarheadDef MissileWarhead_EMP;
        public static MissileWarheadDef MissileWarhead_Fragmentation;
    }
}
