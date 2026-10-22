using RimWorld;
using System.Collections.Generic;
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

        /// <summary>來襲航線上從落點到入場邊緣的距離比例：遠時的瞄準位置（1=邊緣）。</summary>
        public float aimHeightFar = 1f;

        /// <summary>迫近時的航線距離比例（0.5=落點與入場邊緣的中段）。</summary>
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

        /// <summary>
        /// 每 tick 由 <see cref="MapComponent_BVRCombat"/> 呼叫一次，管理連射節奏。
        /// <paramref name="terminalWaves"/> 為本 tick 所有處於末端窗口的波次（含其他防禦方的，於此過濾）。
        /// </summary>
        public void TryEngage(List<BVRWave> terminalWaves, int now)
        {
            if (!Active) { return; }

            // 連射進行中：每 tick 保持砲管朝向（避免兩發之間閒置漂移），到時間就發下一發。
            if (burstShotsLeft > 0)
            {
                AimTopAt(lastAim);
                if (now >= nextShotTick) { FireBurstShot(terminalWaves, now); }
                return;
            }

            if (now < cooldownUntil || !HasFuel) { return; }

            // 冷卻完畢且有燃料：對最早落地的己方末端波次開始新一輪連射。
            BVRWave soonest = null;
            for (int i = 0; i < terminalWaves.Count; i++)
            {
                BVRWave w = terminalWaves[i];
                if (w.defenderFaction != parent.Faction || w.targets.Count == 0) { continue; }
                if (soonest == null || w.tickToImpact < soonest.tickToImpact) { soonest = w; }
            }
            if (soonest == null) { return; }

            burstShotsLeft = Props.shotsPerBurst;
            burstTargetId = soonest.targets[0].id;
            FireBurstShot(terminalWaves, now);
        }

        /// <summary>在己方末端波次中找出本輪連射鎖定的目標及其所屬波次；找不到回傳 null。</summary>
        private BVRTarget FindBurstTarget(List<BVRWave> terminalWaves, out BVRWave wave)
        {
            for (int i = 0; i < terminalWaves.Count; i++)
            {
                BVRWave w = terminalWaves[i];
                if (w.defenderFaction != parent.Faction) { continue; }
                for (int j = 0; j < w.targets.Count; j++)
                {
                    if (w.targets[j].id == burstTargetId)
                    {
                        wave = w;
                        return w.targets[j];
                    }
                }
            }
            wave = null;
            return null;
        }

        private void FireBurstShot(List<BVRWave> terminalWaves, int now)
        {
            // 整輪連射鎖定同一目標；若鎖定目標已消失（被他人攔下或落地），中止本輪、不判定。
            BVRTarget target = FindBurstTarget(terminalWaves, out BVRWave wave);
            if (target == null) { EndBurst(now); return; }

            // 燃料耗盡：中止本輪、不判定。
            if (!HasFuel) { EndBurst(now); return; }
            Refuelable?.ConsumeFuel(Props.fuelPerShot);

            // 虛擬迫近進度：剛進入末端窗口=0（入場邊緣），即將落地=1（沿航線靠近落點）。
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
            if (!cell.IsValid) { return; }
            if (parent is Building_TurretGun turret)
            {
                turret.Top.ForceFaceTarget(new LocalTargetInfo(cell));
            }
            // 非 Building_TurretGun 的平台（例如野戰防空砲）由 CompTurretFacingRenderer 畫砲塔頭。
            parent.GetComp<CompTurretFacingRenderer>()?.ForceFaceTarget(cell.ToVector3Shifted());
        }

        /// <summary>
        /// BVR 目標在攔截窗口內被收進容器，position 是落點，不能作為目前空中位置。
        /// 以保留的入場方位重建落點到地圖邊緣的航線，並沿航線迫近、垂直航線掃射。
        /// </summary>
        public IntVec3 CalculateAimCell(BVRTarget target, IntVec3 origin, IntVec3 mapSize, float approach, int shotIndex)
        {
            Vector3 landing = target.position.ToVector3Shifted();
            Thing payload = target.Payload;
            float heading;
            if (payload is Projectile_MissileIncoming incoming && incoming.HasEntryHeading)
                heading = incoming.EntryHeading;
            else if (payload is Projectile_MissileBase missile)
                heading = missile.angle;
            else if (payload is Skyfaller skyfaller)
                heading = skyfaller.angle;
            else
                heading = MissileMapEntryUtility.HeadingOf(landing - origin.ToVector3Shifted());

            Vector3 direction = MissileMapEntryUtility.HeadingVector(heading);
            Vector3 edge = MissileMapEntryUtility.EdgePoint(mapSize, landing, heading);
            float fraction = Mathf.Clamp01(Mathf.Lerp(Props.aimHeightFar, Props.aimHeightNear, Mathf.Clamp01(approach)));
            Vector3 aim = landing + (edge - landing) * fraction;
            // 提前量沿導彈飛向落點的方向；掃射沿航線的橫向，不固定在世界 X 軸。
            float lead = Mathf.Min(Mathf.Max(0, Props.aimLeadCells), (aim - landing).magnitude);
            aim -= direction * lead;
            int total = Mathf.Max(1, Props.shotsPerBurst);
            float sweepT = total > 1 ? (float)Mathf.Clamp(shotIndex, 0, total - 1) / (total - 1) : 0.5f;
            float sweep = Mathf.Lerp(-Props.aimSweepCells, Props.aimSweepCells, sweepT);
            aim += new Vector3(direction.z, 0f, -direction.x) * sweep;
            return new IntVec3(Mathf.Clamp(Mathf.FloorToInt(aim.x), 0, mapSize.x - 1), 0,
                Mathf.Clamp(Mathf.FloorToInt(aim.z), 0, mapSize.z - 1));
        }

        /// <summary>朝來襲航線發射曳光攔截彈，砲塔與曳光彈使用同一個掃射瞄準點。</summary>
        /// <param name="approach">虛擬迫近進度（0=入場邊緣，1=航線中段）。</param>
        private void FireVisual(BVRTarget target, float approach)
        {
            Map map = parent.Map;
            if (map == null) { return; }

            IntVec3 origin = parent.Position;
            IntVec3 skyCell = CalculateAimCell(target, origin, map.Size, approach, Props.shotsPerBurst - burstShotsLeft);
            LocalTargetInfo skyTarget = new LocalTargetInfo(skyCell);
            lastAim = skyCell; // 供連射期間每 tick 持續朝向。
            parent.GetComp<CompTurretFacingRenderer>()?.ForceFaceTarget(skyCell.ToVector3Shifted());

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
