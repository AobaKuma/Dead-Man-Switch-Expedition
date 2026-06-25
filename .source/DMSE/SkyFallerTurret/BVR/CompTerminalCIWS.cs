using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>階段三：末端攔截砲塔（CIWS）。在末端窗口內自主運作，無需火控導引。</summary>
    public class CompProperties_TerminalCIWS : CompProperties
    {
        /// <summary>末端攔截彈（朝天空發射的曳光彈；命中與否由 interceptChance 決定）。</summary>
        public ThingDef projectile;

        /// <summary>末端攔截窗口（ticks，約 30 秒 = 1800）。剩餘時間進入此區間才啟動。</summary>
        public int terminalWindowTicks = 1800;

        /// <summary>兩次連射之間的冷卻（ticks）。</summary>
        public int cooldownTicks = 60;

        /// <summary>單次連射的發射數。</summary>
        public int shotsPerBurst = 5;

        /// <summary>連射中每發之間隔（ticks），用以呈現快速連射。</summary>
        public int ticksBetweenShots = 6;

        /// <summary>瞄準提前量：朝目標方向的最大水平偏移（格），模擬攔截飛行導彈的提前量。</summary>
        public int aimLeadCells = 4;

        /// <summary>有序掃射半幅（格）：一輪連射由 -aimSweepCells 線性掃到 +aimSweepCells（疊加在提前量上）。</summary>
        public int aimSweepCells = 4;

        /// <summary>瞄準高度（佔砲塔到北邊緣距離的比例）：迫近距離遠時的初始高度（1=邊緣/上）。</summary>
        public float aimHeightFar = 1f;

        /// <summary>瞄準高度比例：迫近距離近時下移到的高度（0.5≈中段）。</summary>
        public float aimHeightNear = 0.5f;

        /// <summary>每發消耗的 Refuelable 燃料量（例如裝甲砲管耐久）；建築無 CompRefuelable 時不消耗。</summary>
        public float fuelPerShot = 1f;

        /// <summary>每輪連射結束後擊落目標的機率（整輪只判定一次）。</summary>
        public float interceptChance = 0.5f;

        /// <summary>擊落成功後生成殘骸（碎片）的機率。碎片僅在末端攔截中可能生成。</summary>
        public float debrisChance = 0.3f;

        /// <summary>攔截成功時的地圖外光效音效（可選；為 null 時用預設）。</summary>
        public SoundDef interceptSound;

        public CompProperties_TerminalCIWS()
        {
            compClass = typeof(CompTerminalCIWS);
        }
    }

    public class CompTerminalCIWS : CompBVRDevice
    {
        public CompProperties_TerminalCIWS Props => (CompProperties_TerminalCIWS)props;

        public int cooldownUntil;
        private int burstShotsLeft;
        private int nextShotTick;
        private int burstTargetId = -1;
        private IntVec3 lastAim = IntVec3.Invalid;

        private CompRefuelable Refuelable => parent.GetComp<CompRefuelable>();

        /// <summary>是否有足夠燃料再發一發（無 CompRefuelable 則視為無限）。</summary>
        private bool HasFuel
        {
            get
            {
                CompRefuelable r = Refuelable;
                return r == null || r.Fuel >= Props.fuelPerShot;
            }
        }

        /// <summary>每 tick 由 <see cref="MapComponent_BVRCombat"/> 呼叫，管理連射節奏。</summary>
        public void TryEngage(BVRWave wave, int now)
        {
            if (!Active) { return; }

            // 連射進行中：每 tick 保持砲管朝向（避免兩發之間閒置漂移），到時間就發下一發。
            if (burstShotsLeft > 0)
            {
                AimTopAt(lastAim);
                if (now >= nextShotTick) { FireBurstShot(wave, now); }
                return;
            }

            // 冷卻完畢、有目標且有燃料：開始新一輪連射。
            if (now >= cooldownUntil && wave.targets.Count > 0 && HasFuel)
            {
                burstShotsLeft = Props.shotsPerBurst;
                burstTargetId = wave.targets[0].id;
                FireBurstShot(wave, now);
            }
        }

        private void FireBurstShot(BVRWave wave, int now)
        {
            // 整輪連射鎖定同一目標；若鎖定目標已消失（被他人攔下或落地），中止本輪、不判定。
            BVRTarget target = wave.targets.Find(t => t.id == burstTargetId);
            if (target == null) { EndBurst(now); return; }

            // 燃料耗盡：中止本輪、不判定。
            if (!HasFuel) { EndBurst(now); return; }
            Refuelable?.ConsumeFuel(Props.fuelPerShot);

            // 虛擬迫近進度：剛進入末端窗口=0（遠/瞄準偏上），即將落地=1（近/瞄準下移）。
            int timeLeft = wave.tickToImpact - now;
            float approach = Props.terminalWindowTicks > 0
                ? Mathf.Clamp01(1f - (float)timeLeft / Props.terminalWindowTicks)
                : 0f;

            FireVisual(target, approach);
            burstShotsLeft--;
            nextShotTick = now + Mathf.Max(1, Props.ticksBetweenShots);

            // 連射全部發射完畢後，才做一次攔截判定。
            if (burstShotsLeft <= 0)
            {
                if (Rand.Chance(Props.interceptChance))
                {
                    // 地圖外攔截光效與音效（末端）。
                    MapComponent_InterceptEffects.Trigger(parent.Map, target.position, terminal: true, Props.interceptSound);
                    if (Rand.Chance(Props.debrisChance))
                    {
                        target.SpawnDebris(parent.Map);
                    }
                    else
                    {
                        target.Discard();
                    }
                    wave.targets.Remove(target);
                }
                EndBurst(now);
            }
        }

        private void EndBurst(int now)
        {
            burstShotsLeft = 0;
            burstTargetId = -1;
            cooldownUntil = now + Props.cooldownTicks;
        }

        /// <summary>讓砲塔頂部持續朝向指定格（連射期間每 tick 呼叫，維持瞄準平順）。</summary>
        private void AimTopAt(IntVec3 cell)
        {
            if (cell.IsValid && parent is Building_TurretGun turret)
            {
                turret.Top.ForceFaceTarget(new LocalTargetInfo(cell));
            }
        }

        /// <summary>朝天空發射一發曳光攔截彈：砲塔轉向、槍口閃光、射擊音效，朝上方射出不傷及殖民地。</summary>
        /// <param name="approach">虛擬迫近進度（0=遠/瞄準偏上，1=近/瞄準下移到中段）。</param>
        private void FireVisual(BVRTarget target, float approach)
        {
            Map map = parent.Map;
            if (map == null) { return; }

            IntVec3 origin = parent.Position;
            // 瞄準高度依虛擬迫近距離：遠時偏上（接近北邊緣），近時下移到約中段（左上 → 左中）。
            int distToEdge = Mathf.Max(1, (map.Size.z - 1) - origin.z);
            float upFrac = Mathf.Lerp(Props.aimHeightFar, Props.aimHeightNear, Mathf.Clamp01(approach));
            int skyZ = Mathf.Clamp(origin.z + Mathf.RoundToInt(distToEdge * upFrac), origin.z + 1, map.Size.z - 1);
            // 提前量：朝目標方向小幅偏移（上限 aimLeadCells），模擬攔截飛行導彈的提前量。
            int lead = Mathf.Clamp(target.position.x - origin.x, -Props.aimLeadCells, Props.aimLeadCells);
            // 有序掃射：依本輪連射進度，砲管由一側線性掃到另一側（第一發至最後一發）。
            int total = Mathf.Max(1, Props.shotsPerBurst);
            int index = Mathf.Clamp(total - burstShotsLeft, 0, total - 1); // 0 = 第一發
            float sweepT = total > 1 ? (float)index / (total - 1) : 0.5f;
            int sweep = Mathf.RoundToInt(Mathf.Lerp(-Props.aimSweepCells, Props.aimSweepCells, sweepT));
            int aimX = Mathf.Clamp(origin.x + lead + sweep, 0, map.Size.x - 1);
            IntVec3 skyCell = new IntVec3(aimX, 0, skyZ);
            LocalTargetInfo skyTarget = new LocalTargetInfo(skyCell);
            lastAim = skyCell; // 供連射期間每 tick 持續朝向。

            // 砲塔轉向天空目標 + 槍口閃光 + 射擊音效。
            float muzzleScale = 9f;
            if (parent is Building_TurretGun turret)
            {
                turret.Top.ForceFaceTarget(skyTarget);
                Verb verb = turret.AttackVerb;
                if (verb != null && verb.verbProps != null)
                {
                    if (verb.verbProps.muzzleFlashScale > 0f) { muzzleScale = verb.verbProps.muzzleFlashScale; }
                    verb.verbProps.soundCast?.PlayOneShot(new TargetInfo(origin, map));
                }
            }
            try { FleckMaker.Static(parent.DrawPos, map, FleckDefOf.ShotFlash, muzzleScale); }
            catch { }

            // 曳光彈（朝天空射出；於地圖邊緣外消失，不傷及殖民地）。
            if (Props.projectile != null)
            {
                try
                {
                    Projectile pro = (Projectile)GenSpawn.Spawn(Props.projectile, origin, map, WipeMode.Vanish);
                    pro.Launch(parent, parent.DrawPos, skyTarget, skyTarget, ProjectileHitFlags.None);
                }
                catch { }
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_BVRCombat m = Manager;
            if (m != null) { m.ciwsTurrets.Add(this); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_BVRCombat m = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (m != null) { m.ciwsTurrets.Remove(this); }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "cooldownUntil", 0);
            Scribe_Values.Look(ref burstShotsLeft, "burstShotsLeft", 0);
            Scribe_Values.Look(ref nextShotTick, "nextShotTick", 0);
            Scribe_Values.Look(ref burstTargetId, "burstTargetId", -1);
        }
    }
}
