using System.Collections.Generic;
using CombatExtended;
using CombatExtended.CombatExtended.Jobs.Utils;
using CombatExtended.Compatibility;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DMSE.CE
{
    /// <summary>啟動時把 CE 相容層掛到 DMSE 的 <see cref="CombatCompat.Active"/>。</summary>
    [StaticConstructorOnStartup]
    public static class CECompatBootstrap
    {
        static CECompatBootstrap()
        {
            CombatCompat.Active = new CECombatCompat();
            Log.Message("[DMSE] Combat Extended compatibility active.");
        }
    }

    /// <summary>
    /// DMSE 的 Combat Extended 相容層。CE 的砲塔是 <see cref="Building_TurretGunCE"/>（不是原版 Building_TurretGun），
    /// 彈藥在 gun 的 <see cref="CompAmmoUser"/> 彈匣裡，砲彈是 <see cref="ProjectileCE"/>；DMSE 本體透過
    /// <see cref="ICombatCompat"/> 存取這些，不直接參考 CE。
    /// </summary>
    public class CECombatCompat : ICombatCompat
    {
        // ── 共用 ──

        /// <summary>建築用的 CE 彈藥 comp：CE 砲塔取 gun 的 CompAmmoUser，其他 Building_Turret 交給 CE 的 TurretRegistry。</summary>
        public static CompAmmoUser AmmoOf(Thing building)
        {
            if (building is Building_TurretGunCE ce) { return ce.CompAmmo; }
            return building?.TryGetComp<CompAmmoUser>();
        }

        private static CompAmmoUser MagazineOf(Thing building)
        {
            CompAmmoUser ammo = AmmoOf(building);
            return ammo != null && ammo.HasMagazine ? ammo : null;
        }

        // ── 砲塔頭 ──

        public bool TryFaceTurret(Thing building, LocalTargetInfo target)
        {
            if (!(building is Building_TurretGunCE ce) || ce.top == null) { return false; }
            ce.top.ForceFaceTarget(target);
            return true;
        }

        public bool TrySetTurretRotation(Thing building, float angleDeg)
        {
            if (!(building is Building_TurretGunCE ce) || ce.top == null) { return false; }
            ce.top.CurRotation = angleDeg;
            return true;
        }

        // ── 彈匣 ──

        public bool HasMagazine(Thing building) => MagazineOf(building) != null;

        public ThingDef LoadedProjectile(Thing building)
        {
            CompAmmoUser ammo = MagazineOf(building);
            if (ammo == null) { return null; }
            // CE 關掉彈藥系統時沒有彈種，彈匣照樣計數，打 verb 預設的投射物
            if (!ammo.UseAmmo) { return ammo.CurMagCount > 0 ? ammo.parent.def.Verbs.FirstOrFallback()?.defaultProjectile : null; }
            return ammo.CurMagCount > 0 ? ammo.CurAmmoProjectile : null;
        }

        public int LoadedRounds(Thing building) => MagazineOf(building)?.CurMagCount ?? 0;

        public bool TryConsumeRounds(Thing building, int rounds)
        {
            CompAmmoUser ammo = MagazineOf(building);
            if (ammo == null || rounds <= 0) { return ammo != null; }
            if (ammo.CurMagCount < rounds) { return false; }
            // 直接改彈匣計數（等同 CompAmmoUser.Notify_ShotFired 的彈匣分支，但不需要 wielder/turret 交叉連結）
            ammo.CurMagCount = ammo.CurMagCount - rounds;
            if (ammo.CurMagCount == 0 && building is Building_TurretGunCE ce && ce.Spawned)
            {
                // 打空了：照 CE 砲塔的規則叫人／自動裝彈機補彈
                ce.TryOrderReload();
            }
            return true;
        }

        public bool TryAutoload(Thing turret, Building_Storage storage)
        {
            CompAmmoUser ammo = MagazineOf(turret);
            if (ammo == null) { return false; }   // 不是 CE 彈匣砲：交回 DMSE 走原版裝填
            if (!ammo.UseAmmo || storage == null || !storage.Spawned) { return true; }

            // 只在空匣、或選定彈種已換掉且匣內是別種彈時補；跟 CE 自動裝彈機一樣不打斷射擊中的砲
            bool wrongType = ammo.CurrentAmmo != ammo.SelectedAmmo;
            if (!ammo.EmptyMagazine && !wrongType) { return true; }
            if (turret is Building_Turret t && t.AttackVerb?.state == VerbState.Bursting) { return true; }

            Thing stack = FindAmmo(storage, ammo);
            if (stack == null) { return true; }

            if (!ammo.EmptyMagazine)
            {
                // 換彈種：先把匣內的退到架旁（CE 的 TryUnload 會把彈放在砲旁）
                if (!ammo.TryUnload(out _, forceUnload: true)) { return true; }
            }
            ammo.SelectedAmmo = (AmmoDef)stack.def;
            ammo.LoadAmmo(stack);
            SoundDefOf.Artillery_ShellLoaded.PlayOneShot(new TargetInfo(turret.Position, turret.Map));
            return true;
        }

        /// <summary>架上可用的彈：優先砲塔選定的彈種，沒有時用同一彈藥組裡的其他彈種。</summary>
        private static Thing FindAmmo(Building_Storage storage, CompAmmoUser ammo)
        {
            AmmoSetDef set = ammo.CurAmmoSet;
            if (set?.ammoTypes == null) { return null; }
            Thing fallback = null;
            Map map = storage.Map;
            List<IntVec3> cells = storage.AllSlotCellsList();
            for (int c = 0; c < cells.Count; c++)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(cells[c]);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (!(t.def is AmmoDef def) || t.stackCount <= 0) { continue; }
                    if (storage.Faction != null && t.IsForbidden(storage.Faction)) { continue; }
                    if (!InSet(set, def)) { continue; }
                    if (def == ammo.SelectedAmmo) { return t; }
                    fallback ??= t;
                }
            }
            return fallback;
        }

        private static bool InSet(AmmoSetDef set, AmmoDef def)
        {
            for (int i = 0; i < set.ammoTypes.Count; i++)
            {
                if (set.ammoTypes[i].ammo == def) { return true; }
            }
            return false;
        }

        // ── 投射物 ──

        /// <summary>同 CE 的 TravelingShell：從地圖外的高處以固定速度飛入（CE 世界砲擊的落彈參數）。</summary>
        private const float ArrivedShotHeight = 200f;
        private const float ArrivedShotSpeed = 55f;

        public bool TryLaunchIncomingShell(ThingDef projectileDef, Map map, IntVec3 entry, IntVec3 target)
        {
            if (projectileDef == null || !typeof(ProjectileCE).IsAssignableFrom(projectileDef.thingClass)) { return false; }
            if (!(projectileDef.projectile is ProjectilePropertiesCE pprops)) { return false; }

            Vector3 source = new Vector3(entry.x, ArrivedShotHeight, entry.z);
            Vector3 targetPos = target.ToVector3Shifted();
            float speed = pprops.isInstant ? pprops.speed : ArrivedShotSpeed;

            ProjectileCE projectile = (ProjectileCE)ThingMaker.MakeThing(projectileDef);
            float shotRotation = pprops.TrajectoryWorker.ShotRotation(pprops, source, targetPos);
            float shotAngle = pprops.TrajectoryWorker.ShotAngle(pprops, source, targetPos, speed);

            projectile.canTargetSelf = false;
            projectile.Position = entry;
            projectile.SpawnSetup(map, false);
            projectile.Launch(null, new Vector2(source.x, source.z), shotAngle, shotRotation, ArrivedShotHeight, speed);
            return true;
        }

        // ── NPC 陣地補給 ──

        public bool TryGetMagazineRefill(Building turret, float refillBelowPercent, float refillToPercent,
            out ThingDef ammoDef, out int count)
        {
            ammoDef = null;
            count = 0;
            CompAmmoUser ammo = MagazineOf(turret);
            if (ammo == null || !ammo.UseAmmo || ammo.MagSize <= 0) { return false; }
            float percent = (float)ammo.CurMagCount / ammo.MagSize;
            if (percent >= refillBelowPercent) { return false; }

            ammoDef = ammo.SelectedAmmo ?? ammo.CurrentAmmo;
            if (ammoDef == null && ammo.CurAmmoSet?.ammoTypes?.Count > 0) { ammoDef = ammo.CurAmmoSet.ammoTypes[0].ammo; }
            if (ammoDef == null) { return false; }

            int target = Mathf.CeilToInt(ammo.MagSize * Mathf.Clamp01(refillToPercent));
            count = Mathf.Max(1, target - ammo.CurMagCount);
            return true;
        }

        public bool MagazineFull(Building turret)
        {
            CompAmmoUser ammo = MagazineOf(turret);
            return ammo == null || ammo.FullMagazine;
        }

        public Job MakeMagazineLoadJob(Pawn pawn, Building turret, Thing ammo)
        {
            CompAmmoUser comp = MagazineOf(turret);
            if (comp == null || !(ammo is AmmoThing) || comp.MissingToFullMagazine <= 0) { return null; }
            if (!(ammo.def is AmmoDef def) || !InSet(comp.CurAmmoSet, def)) { return null; }
            // CE 的 ReloadTurret 裝的是砲塔選定的彈種
            comp.SelectedAmmo = def;
            Job job = JobMaker.MakeJob(CE_JobDefOf.ReloadTurret, turret, ammo);
            job.count = Mathf.Min(ammo.stackCount, comp.MissingToFullMagazine);
            job.playerForced = true;
            return job;
        }

        public bool IsMagazineLoadJob(JobDef def) => def == CE_JobDefOf.ReloadTurret;

        // ── 導彈爆炸與直接傷害 ──

        public bool TryDoMissileExplosion(MissileExplosionArgs args) => MissileExplosionCE.TryDo(args);

        public float MissileDirectArmorPenetration(float vanillaPen, DamageDef damageDef, IReadOnlyList<Def> sources)
            => MissileExplosionCE.DirectPen(vanillaPen, damageDef, sources);

        public bool TurretUsable(Pawn pawn, Building turret)
        {
            CompAmmoUser ammo = AmmoOf(turret);
            if (ammo == null || !ammo.UseAmmo || !ammo.HasMagazine || ammo.CurMagCount > 0) { return true; }
            // 空匣：附近有彈可以裝才去操作（CE 的炮手會自己裝填）
            return turret is Building_Turret t && JobGiverUtils_Reload.MakeReloadJob(pawn, t) != null;
        }
    }
}
