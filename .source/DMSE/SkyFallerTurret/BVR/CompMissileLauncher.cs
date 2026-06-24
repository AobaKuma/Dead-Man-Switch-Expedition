using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>階段二：導彈發射裝置。受火控雷達指派，發射中過程攔截彈。</summary>
    public class CompProperties_MissileLauncher : CompProperties
    {
        /// <summary>攔截彈 skyfaller def（thingClass 應為 DMSE.InterceptProjectile）。</summary>
        public ThingDef interceptorSkyfaller;

        /// <summary>兩次發射間的裝填冷卻（ticks）。</summary>
        public int reloadCooldownTicks = 180;

        /// <summary>攔截彈飛行時間（ticks）：占用火力通道直到結算。</summary>
        public int interceptorTravelTicks = 180;

        public CompProperties_MissileLauncher()
        {
            compClass = typeof(CompMissileLauncher);
        }
    }

    public class CompMissileLauncher : CompBVRDevice
    {
        public CompProperties_MissileLauncher Props => (CompProperties_MissileLauncher)props;

        public int cooldownUntil;

        private CompRefuelable Refuelable => parent.GetComp<CompRefuelable>();

        private bool HasAmmo
        {
            get
            {
                CompRefuelable r = Refuelable;
                return r == null || r.Fuel > 0f;
            }
        }

        public override bool Active => base.Active && HasAmmo;

        public bool ReadyToFire(int now)
            => Active && now >= cooldownUntil && Props.interceptorSkyfaller != null;

        public void FireInterceptor(BVRTarget target, float hitChance, int now)
        {
            if (Props.interceptorSkyfaller == null || parent.Map == null) { return; }

            InterceptProjectile p = (InterceptProjectile)SkyfallerMaker.SpawnSkyfaller(
                Props.interceptorSkyfaller, parent.Position, parent.Map);
            p.Rotation = Rot4.Random;
            p.angle = p.Rotation.AsAngle;
            p.targetId = target.id;
            p.hitChance = hitChance;

            cooldownUntil = now + Props.reloadCooldownTicks;

            CompRefuelable r = Refuelable;
            if (r != null) { r.ConsumeFuel(1f); }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_BVRCombat m = Manager;
            if (m != null) { m.launchers.Add(this); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_BVRCombat m = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (m != null) { m.launchers.Remove(this); }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "cooldownUntil", 0);
        }
    }
}
