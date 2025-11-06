using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace DMSE
{
    public class GenStep_Prefab : GenStep
    {
        public override int SeedPart => 1145;

        public override void Generate(Map map, GenStepParams parms)
        {
            PrefabUtility.SpawnPrefab(this.prefab,map,map.Center - new IntVec3(1,0,1),Rot4.North);
        }

        public PrefabDef prefab;
    }
}
