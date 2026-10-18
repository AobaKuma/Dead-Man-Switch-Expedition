using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class BVRNode
    {
        public MapParent parent;
        public Faction faction;
        public MapComponent_BVRCombat local;
        public WorldObjectComp_AirDefense abstractDefense;
        public PlanetTile Tile => parent.Tile; // Moving ships must use the current tile.
        public Vector3 Center => MissileWorldUtility.TileCenter(Tile);
        public float DetectionRange(BVRTargetProps target)
        {
            if (abstractDefense != null)
                return MissileWorldMotion.DetectionRange(abstractDefense.Props.worldDetectionRange, abstractDefense.Props.antiStealthLevel, target);
            float range = 0;
            foreach (var r in local.searchRadars)
                if (r.Active && r.parent.Faction == faction)
                    range = Mathf.Max(range, MissileWorldMotion.DetectionRange(r.Props.worldDetectionRange, r.Props.antiStealthLevel, target));
            return range;
        }
        /// <summary>
        /// Threat assessment: when channels are short, engage the largest radar signature first (decoy payloads exploit it).
        /// Abstract nodes use their XML flag; map nodes of non-player factions from the Spacer tech level up.
        /// </summary>
        public bool ThreatAssessment => abstractDefense != null ? abstractDefense.Props.threatAssessment
            : faction != null && !faction.IsPlayer && faction.def.techLevel >= TechLevel.Spacer;
        /// <summary>Any interceptor that can fire now or after reloading; locking without one would only hold a channel.</summary>
        public bool HasInterceptorAmmo => local != null ? local.HasInterceptorAmmo(faction)
            : abstractDefense.Available && abstractDefense.Stock > 0;
        public int FreeChannels => local != null ? local.Channels.Free(faction)
            : Mathf.Max(0, abstractDefense.Props.channels - (WorldComponent_BVRNetwork.Current?.CountEngaged(parent, faction) ?? 0));
        public CompFireControlRadar BestRadar(BVRTarget target, int eta) => local?.fireControlRadars
            .Where(fc => fc.Active && fc.parent.Faction == faction && eta <= fc.Props.maxRangeTicks)
            .OrderByDescending(fc => fc.ComputeHitChance(target, eta)).FirstOrDefault();
        public CompMissileLauncher_Interceptor ReadyLauncher(int now) => local?.launchers
            .Where(l => l.parent.Faction == faction && l.ReadyToFire(now))
            .OrderBy(l => l.CooldownUntil).FirstOrDefault();
    }
}
