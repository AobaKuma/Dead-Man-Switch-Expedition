using Fortified;
using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        /// <summary>能攔截的領域（<see cref="CombatDomain"/>）；空 = 只有空中（防空砲不打水下的魚雷）。</summary>
        public List<CombatDomain> domains;

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

        /// <summary>
        /// 是否有足夠燃料再發一發。有 CompRefuelable 時扣它；沒有、但砲有相容層彈匣（CE 的 CompAmmoUser）時
        /// 每發扣 <see cref="RoundsPerShot"/> 發彈匣彈藥；兩者都沒有則視為無限。
        /// </summary>
        private bool HasFuel
        {
            get
            {
                CompRefuelable r = Refuelable;
                if (r != null) { return r.Fuel >= Props.fuelPerShot; }
                if (CombatCompat.HasMagazine(parent)) { return CombatCompat.Active.LoadedRounds(parent) >= RoundsPerShot; }
                return true;
            }
        }

        /// <summary>相容層彈匣每發扣幾發（fuelPerShot 取整，至少 1）。</summary>
        private int RoundsPerShot => System.Math.Max(1, (int)System.Math.Ceiling(Props.fuelPerShot));

        private void ConsumeShot()
        {
            CompRefuelable r = Refuelable;
            if (r != null) { r.ConsumeFuel(Props.fuelPerShot); return; }
            if (CombatCompat.HasMagazine(parent)) { CombatCompat.Active.TryConsumeRounds(parent, RoundsPerShot); }
        }

        // ---- 停火 ----
        // 原版 Building_TurretGun 的「停火」gizmo 只切換私有欄位 holdFire；原版 TryStartShootSomething 以
        // holdFire && CanToggleHoldFire（= 私有的 PlayerControlled）判定是否停火。這裡照抄同一條件，
        // NPC 砲塔或被非殖民者操作時，殘留的停火旗標不會讓它停止攔截。
        private static readonly AccessTools.FieldRef<Building_TurretGun, bool> TurretHoldFireRef = TryHoldFireRef();
        private static readonly Func<Building_TurretGun, bool> TurretPlayerControlled = TryPlayerControlledGetter();
        private static readonly Dictionary<Type, FieldInfo> otherHoldFireFields = new Dictionary<Type, FieldInfo>();

        private static AccessTools.FieldRef<Building_TurretGun, bool> TryHoldFireRef()
        {
            try { return AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire"); }
            catch (Exception e) { Log.Warning($"[DMSE] Building_TurretGun.holdFire not found; hold fire will not stop terminal interception. {e.Message}"); return null; }
        }

        private static Func<Building_TurretGun, bool> TryPlayerControlledGetter()
        {
            try
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(Building_TurretGun), "PlayerControlled");
                return getter != null ? AccessTools.MethodDelegate<Func<Building_TurretGun, bool>>(getter) : null;
            }
            catch { return null; }
        }

        private CompBuildingTurretGun turretGunComp;
        private bool turretGunCompCached;

        /// <summary>
        /// 玩家對此砲塔下了停火：不參與末端攔截。
        /// 原版砲塔讀原版的停火 gizmo；非 Building_TurretGun 的平台讀 <see cref="CompBuildingTurretGun"/> 的停火；
        /// 其他砲塔類別（例如 CE 的 Building_TurretGunCE）讀同名的 holdFire 欄位，只認玩家陣營。
        /// </summary>
        public bool HoldingFire
        {
            get
            {
                if (parent is Building_TurretGun turret)
                {
                    if (TurretHoldFireRef == null || !TurretHoldFireRef(turret)) { return false; }
                    return TurretPlayerControlled != null ? TurretPlayerControlled(turret) : turret.Faction == Faction.OfPlayer;
                }
                if (!turretGunCompCached)
                {
                    turretGunComp = parent?.GetComp<CompBuildingTurretGun>();
                    turretGunCompCached = true;
                }
                if (turretGunComp != null) { return turretGunComp.HoldFire; }
                return parent is Building_Turret && parent.Faction == Faction.OfPlayer && OtherTurretHoldFire(parent);
            }
        }

        private static bool OtherTurretHoldFire(Thing turret)
        {
            Type type = turret.GetType();
            if (!otherHoldFireFields.TryGetValue(type, out FieldInfo field))
            {
                field = AccessTools.Field(type, "holdFire");
                if (field != null && field.FieldType != typeof(bool)) { field = null; }
                otherHoldFireFields[type] = field;
            }
            return field != null && (bool)field.GetValue(turret);
        }

        /// <summary>
        /// 能參與末端攔截：運作中且未停火。停火的 CIWS 對末端階段等同不存在：
        /// 不算進末端窗口（<see cref="MapComponent_BVRCombat.TerminalWindowTicks"/>），中段攔截照常打到落地。
        /// </summary>
        public bool CanEngage => Active && !HoldingFire;

        /// <summary>
        /// 每 tick 由 <see cref="MapComponent_BVRCombat"/> 呼叫一次，管理連射節奏。
        /// <paramref name="terminalWaves"/> 為本 tick 所有處於末端窗口的波次（含其他防禦方的，於此過濾）。
        /// </summary>
        public void TryEngage(List<BVRWave> terminalWaves, int now)
        {
            if (!Active) { return; }

            // 停火：不開新的連射；進行中的連射立即中止、不判定，剩下的砲彈不再發射。
            if (HoldingFire)
            {
                if (burstShotsLeft > 0) { EndBurst(now); }
                return;
            }

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
                if (w.defenderFaction != parent.Faction || !w.targets.Any(t => CombatDomainUtility.Covers(Props.domains, t.domain))) { continue; }
                if (soonest == null || w.tickToImpact < soonest.tickToImpact) { soonest = w; }
            }
            if (soonest == null) { return; }

            burstShotsLeft = Props.shotsPerBurst;
            burstTargetId = soonest.targets.First(t => CombatDomainUtility.Covers(Props.domains, t.domain)).id;
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
            ConsumeShot();

            // 虛擬迫近進度：剛進入末端窗口=0（入場邊緣），即將落地=1（沿航線靠近落點）。
            // 盤旋中的導彈就停在目標上空，迫近進度視為已到位。
            int timeLeft = wave.tickToImpact - now;
            bool loitering = wave.loitering;
            float approach = loitering
                ? 1f
                : (Props.terminalWindowTicks > 0
                    ? Mathf.Clamp01(1f - (float)timeLeft / Props.terminalWindowTicks)
                    : 0f);

            FireVisual(target, approach, loitering);
            burstShotsLeft--;
            nextShotTick = now + Mathf.Max(1, Props.ticksBetweenShots);

            // 連射全部發射完畢後，才做一次攔截判定。
            if (burstShotsLeft <= 0)
            {
                float chance = AirDefenseHooks.MapStat(AirDefenseStat.HitChance, Props.interceptChance, parent.Map, parent.Faction,
                    target.Payload, sensor: parent, eta: timeLeft, terminal: true);
                chance = FinalHitChance(chance, target.Payload);
                bool hit = Rand.Chance(chance);
                AirCombatEvent resolved = AirDefenseHooks.MapEvent(AirCombatEventKind.InterceptResolved, parent.Map, parent.Faction, target.Payload,
                    effector: parent, hitChance: chance, outcome: hit ? AirInterceptOutcome.Hit : AirInterceptOutcome.Miss, terminal: true);
                if (hit)
                {
                    // 攔截表現（末端）：爆心在本輪最後一發的掃射點上空，與曳光彈一致；剛進入窗口時目標還在圖外，
                    // 改為入場邊緣外的虛擬點（不畫爆炸，殘骸淡入，見 InterceptEventData.remote）。須在 SpawnDebris / Discard 之前擷取。
                    IntVec3 debrisCell = MapComponent_InterceptEffects.Trigger(parent.Map,
                        InterceptEventData.From(target, parent.Map, terminal: true, lastAim, approach, Props.interceptSound));
                    if (Rand.Chance(Props.debrisChance))
                    {
                        // 屍體空投落在艙體殘骸的預測落點附近，而不是原本的落點。
                        target.SpawnDebris(parent.Map, debrisCell);
                    }
                    else
                    {
                        target.Discard();
                    }
                    wave.targets.Remove(target);
                }
                AirCombatEvents.Raise(resolved);
                EndBurst(now);
            }
        }

        /// <summary>目標的末端攔截倍率（<see cref="BVRTargetProps.terminalInterceptFactor"/>）；空投艙等沒有擴充的目標為 1。</summary>
        public static float TargetFactor(Thing payload)
            => Mathf.Max(0f, (payload?.def?.GetModExtension<BVRTargetProps>() ?? BVRTargetProps.Default).terminalInterceptFactor);

        /// <summary>
        /// 修正鏈之後的最終命中率：乘上目標倍率（彈道導彈極難被迫近防禦擊落）與模組設定的整體倍率，夾在 0~1。
        /// 放在修正鏈之後，外部加成無法把彈道目標的命中率拉回一般水準。
        /// </summary>
        public static float FinalHitChance(float chance, Thing payload)
        {
            float global = DMSEMod.Settings?.ciwsInterceptMultiplier ?? 1f;
            return Mathf.Clamp01(chance * TargetFactor(payload) * Mathf.Max(0f, global));
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
            CombatCompat.FaceTurret(parent, new LocalTargetInfo(cell));
            // 非 Building_TurretGun 的平台（例如野戰防空砲）由 CompTurretFacingRenderer 畫砲塔頭。
            parent.GetComp<CompTurretFacingRenderer>()?.ForceFaceTarget(cell.ToVector3Shifted());
        }

        /// <summary>
        /// BVR 目標在攔截窗口內被收進容器，position 是落點，不能作為目前空中位置。
        /// 以保留的入場方位重建落點到地圖邊緣的航線，並沿航線迫近、垂直航線掃射。
        /// </summary>
        /// <param name="loitering">盤旋中的導彈滯留在落點上空：不做迫近與提前量，直接對著落點掃射。</param>
        public IntVec3 CalculateAimCell(BVRTarget target, IntVec3 origin, IntVec3 mapSize, float approach, int shotIndex, bool loitering = false)
        {
            Vector3 landing = target.position.ToVector3Shifted();
            float heading = InterceptGeometry.IncomingHeading(target.Payload, landing, origin.ToVector3Shifted(), out _);

            Vector3 direction = MissileMapEntryUtility.HeadingVector(heading);
            Vector3 edge = MissileMapEntryUtility.EdgePoint(mapSize, landing, heading);
            float fraction = loitering
                ? 0f
                : Mathf.Clamp01(Mathf.Lerp(Props.aimHeightFar, Props.aimHeightNear, Mathf.Clamp01(approach)));
            Vector3 aim = landing + (edge - landing) * fraction;
            // 提前量沿導彈飛向落點的方向；掃射沿航線的橫向，不固定在世界 X 軸。
            float lead = loitering ? 0f : Mathf.Min(Mathf.Max(0, Props.aimLeadCells), (aim - landing).magnitude);
            aim -= direction * lead;
            int total = Mathf.Max(1, Props.shotsPerBurst);
            float sweepT = total > 1 ? (float)Mathf.Clamp(shotIndex, 0, total - 1) / (total - 1) : 0.5f;
            float sweep = Mathf.Lerp(-Props.aimSweepCells, Props.aimSweepCells, sweepT);
            aim += new Vector3(direction.z, 0f, -direction.x) * sweep;
            return new IntVec3(Mathf.Clamp(Mathf.FloorToInt(aim.x), 0, mapSize.x - 1), 0,
                Mathf.Clamp(Mathf.FloorToInt(aim.z), 0, mapSize.z - 1));
        }

        /// <summary>朝來襲航線發射曳光攔截彈，砲塔與曳光彈使用同一個掃射瞄準點。</summary>
        /// <param name="approach">虛擬迫近進度（0=入場邊緣，1=航線中段／已到位）。</param>
        /// <param name="loitering">目標正在盤旋：瞄準點固定在盤旋格上空。</param>
        private void FireVisual(BVRTarget target, float approach, bool loitering = false)
        {
            Map map = parent.Map;
            if (map == null) { return; }

            IntVec3 origin = parent.Position;
            IntVec3 skyCell = CalculateAimCell(target, origin, map.Size, approach, Props.shotsPerBurst - burstShotsLeft, loitering);
            LocalTargetInfo skyTarget = new LocalTargetInfo(skyCell);
            lastAim = skyCell; // 供連射期間每 tick 持續朝向。
            parent.GetComp<CompTurretFacingRenderer>()?.ForceFaceTarget(skyCell.ToVector3Shifted());

            // 砲塔轉向天空目標 + 槍口閃光 + 射擊音效。
            float muzzleScale = 9f;
            if (parent is Building_Turret turret)
            {
                // Building_TurretGun 與相容層砲塔（CE 的 Building_TurretGunCE）都是 Building_Turret
                CombatCompat.FaceTurret(parent, skyTarget);
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
