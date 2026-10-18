using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>One faction pool shared by artillery, local waves and world tracks.</summary>
    public class BVRChannelLedger
    {
        private readonly MapComponent_BVRCombat owner;
        private readonly Map map;
        public BVRChannelLedger(MapComponent_BVRCombat owner, Map map) { this.owner = owner; this.map = map; }
        public int Capacity(Faction faction) => owner.fireControlRadars.Where(fc => fc.Active && fc.parent.Faction == faction)
            .Sum(fc => fc.Props.maxTargets);
        public int Artillery(Faction faction) => owner.fireControlRadars.Where(fc => fc.parent.Faction == faction)
            .Sum(fc => fc.OccupiedArtilleryChannels);
        public int Local(Faction faction) => owner.CountEngaged(faction);
        public int World(Faction faction) => WorldComponent_BVRNetwork.Current?.CountEngaged(map.Parent, faction) ?? 0;
        public int Occupied(Faction faction) => Artillery(faction) + Local(faction) + World(faction);
        public int Free(Faction faction) => System.Math.Max(0, Capacity(faction) - Occupied(faction));
    }
}
