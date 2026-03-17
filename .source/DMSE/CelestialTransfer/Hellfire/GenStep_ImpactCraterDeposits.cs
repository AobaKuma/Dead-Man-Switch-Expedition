using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class GenStep_ImpactCraterDeposits : GenStep
    {
        public override int SeedPart => 1408723119;

        // 可由 XML 覆寫
        public FloatRange coreBand = new FloatRange(0f, 0.22f);
        public FloatRange rimBand = new FloatRange(0.22f, 0.42f);
        public FloatRange ejectaBand = new FloatRange(0.42f, 0.78f);

        public float coreComponentsChance = 0.17f;
        public float coreSteelChance = 0.12f;
        public float rimComponentsChance = 0.09f;
        public float rimSteelChance = 0.20f;
        public float ejectaComponentsChance = 0.03f;
        public float ejectaSteelChance = 0.08f;

        public float rimSlagChance = 0.07f;
        public float ejectaSlagChance = 0.04f;

        public float vacstoneScarChanceCore = 0.28f;
        public float vacstoneScarChanceRim = 0.16f;
        public float vacstoneScarChanceEjecta = 0.08f;

        public float coreObsidianChance = 0.56f;
        public float rimObsidianChance = 0.22f;
        public float ejectaObsidianChance = 0.14f;

        public override void Generate(Map map, GenStepParams parms)
        {
            var steelDef = ThingDefOf.MineableSteel;
            var mechDef = DefDatabase<ThingDef>.GetNamedSilentFail("MineableComponentsIndustrial");
            var obsidianMineableDef = DefDatabase<ThingDef>.GetNamedSilentFail("MineableObsidian");
            var slagDef = ThingDefOf.ChunkSlagSteel;
            var vacstoneTerrain = DefDatabase<TerrainDef>.GetNamedSilentFail("Vacstone_Rough");

            if (steelDef == null || mechDef == null || slagDef == null || vacstoneTerrain == null)
            {
                return;
            }

            IntVec3 center = map.Center;
            float maxR = Mathf.Max(center.x, center.z) - 2f;
            if (maxR <= 8f) return;

            Rand.PushState(Gen.HashCombineInt(map.Tile, SeedPart));
            try
            {
                foreach (IntVec3 c in map.AllCells)
                {
                    float r = (c - center).LengthHorizontal / maxR; // 0..~1
                    if (r > ejectaBand.max) continue;

                    bool inCore = r >= coreBand.min && r < coreBand.max;
                    bool inRim = r >= rimBand.min && r < rimBand.max;
                    bool inEjecta = r >= ejectaBand.min && r < ejectaBand.max;

                    if (!inCore && !inRim && !inEjecta) continue;

                    // 1) 真空岩疤痕（地表）
                    if (!c.Fogged(map) && c.InBounds(map))
                    {
                        float scarChance = inCore ? vacstoneScarChanceCore : (inRim ? vacstoneScarChanceRim : vacstoneScarChanceEjecta);
                        if (Rand.Chance(scarChance) && c.GetTerrain(map) != vacstoneTerrain)
                        {
                            map.terrainGrid.SetTerrain(c, vacstoneTerrain);
                        }
                    }

                    // 2) 埋壓礦（只替換自然岩）
                    Building edifice = c.GetEdifice(map);
                    if (edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock)
                    {
                        float compChance = inCore ? coreComponentsChance : (inRim ? rimComponentsChance : ejectaComponentsChance);
                        float steelChance = inCore ? coreSteelChance : (inRim ? rimSteelChance : ejectaSteelChance);
                        float obsChance = inCore ? coreObsidianChance : (inRim ? rimObsidianChance : ejectaObsidianChance);

                        float roll = Rand.Value;
                        if (roll < compChance)
                        {
                            edifice.Destroy();
                            GenSpawn.Spawn(mechDef, c, map);
                        }
                        else if (roll < compChance + steelChance)
                        {
                            edifice.Destroy();
                            GenSpawn.Spawn(steelDef, c, map);
                        }
                        else if (roll < compChance + steelChance + obsChance)
                        {
                            edifice.Destroy();
                            GenSpawn.Spawn(obsidianMineableDef, c, map);
                        }
                    }
                    else
                    {
                        // 3) 鋼渣散佈（坑壁多、外圈次之）
                        float slagChance = inRim ? rimSlagChance : (inEjecta ? ejectaSlagChance : 0f);
                        if (slagChance > 0f && Rand.Chance(slagChance) && c.Walkable(map) && c.GetFirstThing(map, slagDef) == null)
                        {
                            GenSpawn.Spawn(slagDef, c, map);
                        }
                    }
                }
            }
            finally
            {
                Rand.PopState();
            }
        }
    }
}