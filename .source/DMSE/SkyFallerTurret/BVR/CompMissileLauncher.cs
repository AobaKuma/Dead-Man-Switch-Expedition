using System.Collections.Generic;
using RimWorld;
using UnityEngine;
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

        /// <summary>每個指定目標連射的攔截彈數；受實際裝填彈數限制。</summary>
        public int interceptorSalvoSize = 1;

        public CompProperties_MissileLauncher()
        {
            compClass = typeof(CompMissileLauncher_Interceptor);
        }
    }

    /// <summary>
    /// 防空攔截彈旋轉發射架：<see cref="CompMissileLauncher"/> 家族中負責地圖內超視距攔截的成員。
    /// 與 <see cref="CompMissileLauncher_Ballistic"/>／<see cref="CompMissileLauncher_Rail"/> 不同，
    /// 世界航跡由 WorldComponent_BVRNetwork 呼叫 FireWorldInterceptor：消耗實體彈、生成純視覺升空彈，
    /// 世界物件接手會合結算；空投與舊波次仍由 MapComponent_BVRCombat 呼叫 FireInterceptor。
    /// </summary>
    public class CompMissileLauncher_Interceptor : CompMissileLauncher
    {
        public CompProperties_MissileLauncher Props => (CompProperties_MissileLauncher)props;

        protected override int LaunchCooldownTicks => Props.reloadCooldownTicks;

        protected override string CooldownScribeKey => "cooldownUntil";

        protected override string CooldownTranslationKey => "DMSE.MissileLauncher.Interceptor.Reloading";

        // 彈藥來源：parent 為 Building_MissileRack（容器），攔截彈以實體 Thing 逐枚存放，
        // 或是由 CompMissileRackStorage 掛在砲塔型平台上（野戰防空砲）。
        private ThingOwner HeldContainer =>
            parent.GetComp<CompMissileRackStorage>()?.HeldContainer
            ?? (parent as IThingHolder)?.GetDirectlyHeldThings();

        // 輕型發射箱與對地發射軌共用彈艙：只取攔截／兩用彈（對地彈不能當攔截彈射掉），
        // 且被打擊任務保留的導彈（StrikeReservation）不取用。
        private Thing LoadedMissile
        {
            get
            {
                ThingOwner owner = HeldContainer;
                if (owner == null || owner.Count == 0) { return null; }
                MapComponent_StrikePlanner planner = StrikeReservation.PlannerFor(parent);
                for (int i = 0; i < owner.Count; i++)
                {
                    if (!MissileRoleExtension.CanIntercept(owner[i])) { continue; }
                    if (planner == null || !planner.IsReserved(owner[i])) { return owner[i]; }
                }
                return null;
            }
        }

        public override bool HasAmmo => LoadedMissile != null;

        /// <summary>
        /// 取得已裝填攔截彈的 MissileConfig，供 BVR 系統讀取戰鬥部加成與彈體攔截係數。
        /// 物品的 CompMissileConfig 優先，其次是 <see cref="MissileItemExtension"/> 宣告的彈體；都沒有回傳 null。
        /// </summary>
        public MissileConfig GetLoadedMissileConfig()
        {
            return MissileItemExtension.ConfigFor(LoadedMissile);
        }

        public bool ReadyToFire(int now)
            => Active && HasAmmo && !OnCooldown(now) && Props.interceptorSkyfaller != null;

        /// <summary>
        /// 發射一枚攔截彈。<paramref name="heading"/> 為來襲方位（升空後轉向飛出地圖）；不給時以
        /// <see cref="MissileProjectileMaker.IncomingBearing"/> 由目標推算。經發射排程錯開生成。
        /// </summary>
        public void FireInterceptor(BVRTarget target, float hitChance, int now, float? heading = null)
        {
            FireInterceptorRounds(target, hitChance, now, heading, 1, out _);
        }

        private int FireInterceptorRounds(BVRTarget target, float hitChance, int now, float? heading, int requestedRounds,
            out int lastSpawnTick)
        {
            lastSpawnTick = now;
            if (Props.interceptorSkyfaller == null || parent.Map == null) { return 0; }

            // 初始升空角沿用建築朝向；具旋轉砲頭的發射架採用砲頭角度。heading 留給升空後的攔截航線。
            heading ??= MissileProjectileMaker.IncomingBearing(target, parent.Position);
            float launchAngle = parent.TryGetComp<CompTurretFacingRenderer>()?.CurRotation ?? parent.Rotation.AsAngle;
            CompTurretFacingRenderer facing = parent.TryGetComp<CompTurretFacingRenderer>();
            MapComponent_StrikePlanner planner = StrikeReservation.PlannerFor(parent);
            ThingOwner owner = HeldContainer;
            List<(Thing missile, ThingDef launchDef, Vector3 launchOffset, bool hasLaunchOffset)> rounds
                = new List<(Thing, ThingDef, Vector3, bool)>();
            int requested = Mathf.Max(1, requestedRounds);
            if (owner != null)
            {
                for (int i = 0; i < owner.Count && rounds.Count < requested; i++)
                {
                    Thing missile = owner[i];
                    if (!MissileRoleExtension.CanIntercept(missile)) { continue; }
                    if (planner != null && planner.IsReserved(missile)) { continue; }
                    Vector3 launchOffset = Vector3.zero;
                    bool hasLaunchOffset = facing != null && facing.TryGetRenderedAmmoOffset(missile, out launchOffset);
                    if (!hasLaunchOffset)
                    {
                        CompMissileRackRenderer rackRenderer = parent.TryGetComp<CompMissileRackRenderer>();
                        hasLaunchOffset = rackRenderer != null && rackRenderer.TryGetRenderedAmmoOffset(missile, out launchOffset);
                    }
                    ThingDef roundLaunchDef = MissileItemExtension.BodyOf(missile)?.launchProjectile ?? Props.interceptorSkyfaller;
                    rounds.Add((missile, roundLaunchDef, launchOffset, hasLaunchOffset));
                }
            }

            int fired = 0;
            foreach (var round in rounds)
            {
                int spawnTick = MapComponent_MissileLaunchQueue.Launch(parent.Map, new PendingMissileLaunch
                {
                    def = round.launchDef,
                    cell = parent.Position,
                    rotation = parent.Rotation,
                    hasLaunchAngle = facing != null,
                    launchAngle = launchAngle,
                    hasLaunchOffset = round.hasLaunchOffset,
                    launchOffset = round.launchOffset,
                    launcher = parent,
                    interceptor = true,
                    targetId = target.id,
                    hitChance = hitChance,
                    hasHeading = heading != null,
                    heading = heading ?? 0f,
                });
                lastSpawnTick = Mathf.Max(lastSpawnTick, spawnTick);
                owner.Remove(round.missile);
                round.missile.Destroy(DestroyMode.Vanish);
                fired++;
            }

            if (fired > 0)
            {
                StartCooldown(now);
                if (target != null && target.id >= 0) { target.interceptorsInFlight += fired; }
            }
            return fired;

        }

        /// <summary>
        /// 試射地圖目標。旋轉發射架先對準本次指派目標，砲頭進入容許角度後才發射；
        /// 避免渲染器追蹤另一個更急迫的波次時，導彈沿著錯的砲頭角度離架。
        /// </summary>
        public bool TryFireInterceptor(BVRTarget target, float hitChance, int now, out int roundsFired, out int lastSpawnTick)
        {
            roundsFired = 0;
            lastSpawnTick = now;
            CompTurretFacingRenderer facing = parent.TryGetComp<CompTurretFacingRenderer>();
            float? heading = MissileProjectileMaker.IncomingBearing(target, parent.Position);
            if (facing != null && heading.HasValue)
            {
                Vector3 aimPoint = parent.DrawPos + MissileMapEntryUtility.HeadingVector(heading.Value) * 10f;
                facing.ForceFaceTarget(aimPoint, 2);
                float tolerance = Mathf.Max(1f, facing.Props.rotationSpeedDegPerTick * 0.5f);
                if (!facing.IsFacing(aimPoint, tolerance)) { return false; }
            }

            roundsFired = FireInterceptorRounds(target, hitChance, now, heading, Props.interceptorSalvoSize, out lastSpawnTick);
            return roundsFired > 0;
        }

        /// <summary>
        /// Consume a real missile and launch a visual-only interceptor; world scheduling owns the roll.
        /// <paramref name="heading"/>: bearing toward the world meeting point, so the missile turns that way after the ascent.
        /// </summary>
        public bool FireWorldInterceptor(int now, float? heading = null)
        {
            if (!ReadyToFire(now) || parent.Map == null) return false;
            FireInterceptor(new BVRTarget { id = -1 }, 0f, now, heading);
            return true;
        }

        protected override string LoadedStatusText => "DMSE.MissileLauncher.Interceptor.Ready".Translate();

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
    }
}
