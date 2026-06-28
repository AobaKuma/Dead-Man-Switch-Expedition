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

        // 彈藥來源：parent 為 Building_MissileRack（容器），攔截彈以實體 Thing 逐枚存放，
        // 與 Scorer 的 CompMissileRailLauncher 相同；發射時取出一枚並摧毀（取代舊 CompRefuelable 抽象燃料）。
        private ThingOwner HeldContainer => (parent as IThingHolder)?.GetDirectlyHeldThings();

        private Thing LoadedMissile
        {
            get
            {
                ThingOwner owner = HeldContainer;
                return owner != null && owner.Count > 0 ? owner[0] : null;
            }
        }

        private bool HasAmmo => LoadedMissile != null;

        /// <summary>
        /// 取得已裝填攔截彈的 MissileConfig，供 BVR 系統讀取戰鬥部加成。
        /// 裝填物無 CompMissileConfig 時回傳 null。
        /// </summary>
        public MissileConfig GetLoadedMissileConfig()
        {
            Thing missile = LoadedMissile;
            if (missile == null) { return null; }
            CompMissileConfig comp = missile.TryGetComp<CompMissileConfig>();
            return comp?.config;
        }

        public override bool Active => base.Active && HasAmmo;

        public bool ReadyToFire(int now)
            => Active && now >= cooldownUntil && Props.interceptorSkyfaller != null;

        public void FireInterceptor(BVRTarget target, float hitChance, int now)
        {
            if (Props.interceptorSkyfaller == null || parent.Map == null) { return; }

            // 取出一枚實體攔截彈作為彈藥；無彈則不發射。
            Thing missile = LoadedMissile;
            if (missile == null) { return; }

            InterceptProjectile p = (InterceptProjectile)SkyfallerMaker.SpawnSkyfaller(
                Props.interceptorSkyfaller, parent.Position, parent.Map);
            // 發射方向與發射器建築朝向一致（同巡航導彈）。
            p.Rotation = parent.Rotation;
            p.angle = p.Rotation.AsAngle;
            p.targetId = target.id;
            p.hitChance = hitChance;

            cooldownUntil = now + Props.reloadCooldownTicks;

            // 消耗實體彈藥（與 CompMissileRailLauncher 相同）。
            HeldContainer?.Remove(missile);
            missile.Destroy(DestroyMode.Vanish);
        }

        public override string CompInspectStringExtra()
        {
            int remaining = cooldownUntil - Find.TickManager.TicksGame;
            if (remaining > 0)
            {
                return "DMSE.SAM.Reloading".Translate(remaining.ToStringTicksToPeriod());
            }
            return "DMSE.SAM.Ready".Translate();
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
