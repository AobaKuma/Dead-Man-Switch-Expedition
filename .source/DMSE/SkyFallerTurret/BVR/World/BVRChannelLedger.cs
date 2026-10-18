using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>One faction pool shared by artillery, local waves, world tracks and strike guidance.</summary>
    public class BVRChannelLedger
    {
        private readonly MapComponent_BVRCombat owner;
        private readonly Map map;
        private MapComponent_StrikePlanner planner;
        public BVRChannelLedger(MapComponent_BVRCombat owner, Map map) { this.owner = owner; this.map = map; }
        private MapComponent_StrikePlanner Planner => planner ??= map?.GetComponent<MapComponent_StrikePlanner>();
        public int Capacity(Faction faction) => owner.fireControlRadars.Where(fc => fc.Active && fc.parent.Faction == faction)
            .Sum(fc => fc.Props.maxTargets);
        public int Artillery(Faction faction) => owner.fireControlRadars.Where(fc => fc.parent.Faction == faction)
            .Sum(fc => fc.OccupiedArtilleryChannels);
        public int Local(Faction faction) => owner.CountEngaged(faction);
        public int World(Faction faction) => WorldComponent_BVRNetwork.Current?.CountEngaged(map.Parent, faction) ?? 0;
        /// <summary>Strike missiles queued to launch or still in their guidance period (docs/BVR_波次打擊規劃.md §6).</summary>
        public int Strike(Faction faction) => Planner?.GuidanceOccupied(faction) ?? 0;
        public int Occupied(Faction faction) => Artillery(faction) + Local(faction) + World(faction) + Strike(faction);
        public int Free(Faction faction) => System.Math.Max(0, Capacity(faction) - Occupied(faction));
        /// <summary>Channels of active radars that can guide strike missiles.</summary>
        public int StrikeCapacity(Faction faction) => owner.fireControlRadars
            .Where(fc => fc.Active && fc.parent.Faction == faction && fc.Props.supportsStrikeGuidance).Sum(fc => fc.Props.maxTargets);
        /// <summary>Channels a strike wave may book now: the shared free pool, limited to strike-capable radars.</summary>
        public int StrikeFree(Faction faction) => System.Math.Max(0,
            System.Math.Min(Free(faction), StrikeCapacity(faction) - Strike(faction)));
    }
}
