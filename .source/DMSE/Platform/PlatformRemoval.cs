using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 掛在太空平台地形（DMS 軌道平台、輕型平台，以及原版的軌道平台、機械體平台）上，
    /// 使其可用「拆除平台」指令拆回太空虛空並回收材料。
    /// </summary>
    public class RemovablePlatformExtension : DefModExtension
    {
        /// <summary>拆除後回收的材料；未指定時以地形的 costList × refundFraction 計算。</summary>
        public List<ThingDefCountClass> leavings;

        public float refundFraction = 0.5f;

        /// <summary>每項回收材料的產出機率（真空岩地面比照採礦的碎石掉落率）。</summary>
        public float leavingsChance = 1f;

        /// <summary>拆除工作量；未指定（≤ 0）時取地形 WorkToBuild 的一半，最少 600。</summary>
        public float workAmount = -1f;
    }

    /// <summary>
    /// 真空岩的地面（Vacstone_Rough / _RoughHewn / _Smooth）是 TerrainDefGenerator_Stone 在 XML patch 之後產生的隱含 Def，
    /// 無法用 patch 掛 modExtensions，啟動時在這裡補上：拆除比照採礦，25% 機率得到一塊真空岩碎石。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class VacstoneRemovalSetup
    {
        static VacstoneRemovalSetup()
        {
            ThingDef rock = ThingDefOf.Vacstone;
            if (rock?.building == null)
            {
                return;
            }
            ThingDef chunk = rock.building.mineableThing;
            float chance = rock.building.mineableDropChance;
            TerrainDef rough = rock.building.naturalTerrain;
            TerrainDef hewn = rock.building.leaveTerrain;
            foreach (TerrainDef terrain in new[] { rough, hewn, rough?.smoothedTerrain, hewn?.smoothedTerrain })
            {
                if (terrain == null || terrain.HasModExtension<RemovablePlatformExtension>())
                {
                    continue;
                }
                if (terrain.modExtensions == null)
                {
                    terrain.modExtensions = new List<DefModExtension>();
                }
                RemovablePlatformExtension ext = new RemovablePlatformExtension { workAmount = 1500f, leavingsChance = chance };
                if (chunk != null)
                {
                    ext.leavings = new List<ThingDefCountClass> { new ThingDefCountClass(chunk, 1) };
                }
                terrain.modExtensions.Add(ext);
            }
        }
    }

    public static class PlatformRemovalUtility
    {
        public static RemovablePlatformExtension ExtensionOf(TerrainDef terrain)
        {
            return terrain?.GetModExtension<RemovablePlatformExtension>();
        }

        public static float WorkAmount(TerrainDef terrain)
        {
            RemovablePlatformExtension ext = ExtensionOf(terrain);
            if (ext != null && ext.workAmount > 0f)
            {
                return ext.workAmount;
            }
            return Mathf.Max(600f, terrain.GetStatValueAbstract(StatDefOf.WorkToBuild) * 0.5f);
        }

        public static List<ThingDefCountClass> Leavings(TerrainDef terrain)
        {
            List<ThingDefCountClass> result = new List<ThingDefCountClass>();
            RemovablePlatformExtension ext = ExtensionOf(terrain);
            if (ext == null)
            {
                return result;
            }
            if (ext.leavings != null)
            {
                result.AddRange(ext.leavings);
                return result;
            }
            if (terrain.CostList != null)
            {
                for (int i = 0; i < terrain.CostList.Count; i++)
                {
                    int count = GenMath.RoundRandom(terrain.CostList[i].count * ext.refundFraction);
                    if (count > 0)
                    {
                        result.Add(new ThingDefCountClass(terrain.CostList[i].thingDef, count));
                    }
                }
            }
            return result;
        }

        /// <summary>這格的平台目前能不能拆；不是可拆平台時 reason 為 null（不顯示訊息）。</summary>
        public static bool CanRemoveAt(IntVec3 c, Map map, out string reason)
        {
            reason = null;
            if (!c.InBounds(map))
            {
                return false;
            }
            TerrainGrid tg = map.terrainGrid;
            if (ExtensionOf(tg.BaseTerrainAt(c)) == null)
            {
                return false;
            }
            if (ExtensionOf(tg.TopTerrainAt(c)) == null || tg.FoundationAt(c) != null || tg.TempTerrainAt(c) != null)
            {
                reason = "DMSE.Platform.FloorOnTop".Translate();
                return false;
            }
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame)
                {
                    reason = "DMSE.Platform.Blocked".Translate(t.LabelShort);
                    return false;
                }
            }
            return true;
        }

        /// <summary>拆除時施工者站的位置：相鄰、可站立、可到達，且不是另一格待拆的平台。</summary>
        public static IntVec3 FindStandCell(Pawn pawn, IntVec3 c)
        {
            Map map = pawn.Map;
            IntVec3 best = IntVec3.Invalid;
            float bestDist = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 cell = c + GenAdj.AdjacentCells[i];
                if (!cell.InBounds(map) || !cell.Standable(map) || map.designationManager.DesignationAt(cell, DMSE_DefOf.DMSE_RemovePlatform) != null)
                {
                    continue;
                }
                if (!pawn.CanReach(cell, Verse.AI.PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }
                float dist = (cell - pawn.Position).LengthHorizontalSquared;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = cell;
                }
            }
            return best;
        }

        public static bool AnyPawnAt(IntVec3 c, Map map)
        {
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>拆除：格上的物品移到附近、回收材料、地形改回太空虛空。</summary>
        public static void Remove(IntVec3 c, Map map, bool doLeavings)
        {
            TerrainDef terrain = map.terrainGrid.TopTerrainAt(c);
            // 太空可行走後也算 Standable；物品與回收材料仍放在實地上，不要飄到虛空
            System.Predicate<IntVec3> elsewhere = cell => cell != c && cell.Standable(map) && cell.GetTerrain(map) != TerrainDefOf.Space;
            List<Thing> things = c.GetThingList(map).ListFullCopy();
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Destroyed || t is Pawn)
                {
                    continue;
                }
                if (t.def.category == ThingCategory.Item)
                {
                    t.DeSpawn();
                    if (!GenPlace.TryPlaceThing(t, c, map, ThingPlaceMode.Near, null, elsewhere))
                    {
                        t.Destroy();
                    }
                }
                else if (t.def.destroyable)
                {
                    t.Destroy();
                }
            }
            if (doLeavings)
            {
                List<ThingDefCountClass> leavings = Leavings(terrain);
                float chance = ExtensionOf(terrain)?.leavingsChance ?? 1f;
                for (int i = 0; i < leavings.Count; i++)
                {
                    if (!Rand.Chance(chance))
                    {
                        continue;
                    }
                    Thing thing = ThingMaker.MakeThing(leavings[i].thingDef);
                    thing.stackCount = leavings[i].count;
                    GenPlace.TryPlaceThing(thing, c, map, ThingPlaceMode.Near, null, elsewhere);
                }
            }
            map.terrainGrid.SetTerrain(c, TerrainDefOf.Space);
            FilthMaker.RemoveAllFilth(c, map);
        }
    }
}
