using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 抵達落點的導彈 Projectile（取代 <see cref="MissileIncoming"/> skyfaller；docs/Missile_Skyfaller轉Projectile_計畫.md §8.4）。
    ///
    /// 墜落沿用原版 Skyfaller 公式（<see cref="Projectile_MissileBase"/> 的正向時間軸）。有進入方位
    /// （<see cref="SetEntryHeading"/>，世界航跡最後一段在目標的方位）時，angle 設成該方位、起始 ticksToImpact 取成起點在地圖邊緣外
    /// （<see cref="MissileArrivalKinematics.EntryTicks"/>），所以彈體從對應的地圖邊緣飛入、直線飛向落點。
    ///
    /// 生成方式不經 Launch（BVR 收起後會以 TryDrop 重新生成，SpawnSetup 每次都重算墜落），SpawnSetup 直接設 landed 停用原版直線運動。
    /// 首 tick 交給 BVR、落地引爆與波次打擊回報與舊 MissileIncoming 相同。
    /// </summary>
    public class Projectile_MissileIncoming : Projectile_MissileBase, IIncomingMissile
    {
        public MissileConfig config;
        public Faction attacker;
        public bool bvrHandled;
        public bool worldPhaseDone;
        public int strikeMissionId = -1;
        public int strikeWave = -1;
        private bool strikeReported;
        private bool checkedBvr;

        private bool hasEntryHeading;
        private float entryHeading;

        public MissileConfig Config { get => config; set => config = value; }
        public Faction Attacker { get => attacker; set => attacker = value; }
        public bool BvrHandled { get => bvrHandled; set => bvrHandled = value; }
        public bool WorldPhaseDone { get => worldPhaseDone; set => worldPhaseDone = value; }
        public int StrikeMissionId { get => strikeMissionId; set => strikeMissionId = value; }
        public int StrikeWave { get => strikeWave; set => strikeWave = value; }

        public bool HasEntryHeading => hasEntryHeading;
        public float EntryHeading => entryHeading;

        public override string Label => MissileImpactUtility.StripSuffix(def.label);

        /// <summary>
        /// 設定發射建築（傷害歸因；docs/Missile_Skyfaller轉Projectile_計畫.md §7）。只接受仍存在且有陣營的建築：
        /// 原版護盾以 Launcher 的陣營判斷敵我，沒有陣營的 Launcher 會讓它出錯。
        /// </summary>
        public void SetLauncher(Thing thing)
        {
            launcher = thing != null && !thing.Destroyed && thing.Faction != null ? thing : null;
        }

        /// <summary>落地爆炸的 instigator：仍存在的發射建築；沒有時為 null。</summary>
        public Thing Instigator => launcher != null && !launcher.Destroyed ? launcher : null;

        /// <summary>撞上護盾時扣的護盾 HP（原版 CheckIntercept 讀 DamageAmount）：導彈的實際彈頭傷害。</summary>
        public override int DamageAmount => config != null && config.Valid ? config.DamageAmount : base.DamageAmount;

        /// <summary>設定進入方位（0 = 北、順時針）：彈體從這個方向的地圖邊緣飛入。須在生成前呼叫。</summary>
        public void SetEntryHeading(float heading)
        {
            hasEntryHeading = true;
            entryHeading = heading;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (respawningAfterLoad) { return; }
            landed = true; // 不經 Launch：停用原版直線運動、出界銷毀與自由攔截（見基底說明）。
            hasHitRoof = false;
            hasImpacted = false;
            if (hasEntryHeading)
            {
                angle = MissileMapEntryUtility.SkyfallerAngleForHeading(entryHeading);
                ticksToImpact = ticksToImpactMax = MissileArrivalKinematics.EntryTicks(Flight, map.Size, Position, entryHeading);
            }
        }

        protected override void Tick()
        {
            // 首次 tick 時（此時呼叫端已設好 attacker/config）嘗試交給 BVR 攔截。
            if (!checkedBvr)
            {
                checkedBvr = true;
                if (!bvrHandled)
                {
                    MissileBVRUtility.TryRegister(this, Position);
                    if (!Spawned) { return; } // 已被 BVR 收起。
                }
            }
            Vector3 before = DrawPos;
            base.Tick();

            // 高角護盾：以畫面位置的移動線段檢查（§6）。原版 EMP 判定讀 DamageDef，所以帶上彈頭的傷害型別。
            if (Spawned && !hasImpacted)
            {
                damageDefOverride = config != null && config.Valid ? config.DamageDef : null;
                if (MissileShieldUtility.TryIntercept(this, before, DrawPos, out IntVec3 contact))
                {
                    InterceptedByShield(contact);
                }
            }
        }

        /// <summary>被高角護盾擋下：在碰觸點完整引爆（不套用命中偏移、不歸因），波次打擊記為護盾擋下。</summary>
        private void InterceptedByShield(IntVec3 contact)
        {
            hasImpacted = true;
            if (!strikeReported && strikeMissionId >= 0)
            {
                strikeReported = true;
                WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, StrikeOutcome.ShieldBlocked);
            }
            Map map = Map;
            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE] {LabelCap} 被高角護盾擋下，於 {contact} 引爆（原落點 {Position}）。");
            }
            ShieldContactCell = contact;
            MissileImpactUtility.Detonate(config, contact, map, attacker, instigator: null, applyScatter: false);
            Destroy();
        }

        /// <summary>落地引爆的實際爆心（套用導引的命中偏移後）；尚未落地為 Invalid。</summary>
        public IntVec3 LastImpactCell { get; private set; } = IntVec3.Invalid;

        /// <summary>被護盾擋下時的碰觸格（驗收測試用）；未被擋下為 Invalid。</summary>
        public IntVec3 ShieldContactCell { get; private set; } = IntVec3.Invalid;

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (!strikeReported && strikeMissionId >= 0)
            {
                strikeReported = true;
                WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, StrikeOutcome.TerminalKilled);
            }
            base.Destroy(mode);
        }

        protected override void MissileImpact()
        {
            if (!strikeReported && strikeMissionId >= 0)
            {
                strikeReported = true;
                WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, StrikeOutcome.Hit);
            }
            Map map = Map;
            IntVec3 position = Position;
            LastImpactCell = MissileImpactUtility.Detonate(config, position, map, attacker, Instigator); // 傷害歸因於發射建築

            // 同 Skyfaller.Impact 的落地表現：揚塵、鏡頭震動、落地音效。
            MissileFlightExtension f = Flight;
            CellRect rect = this.OccupiedRect();
            for (int i = 0; i < rect.Area * f.motesPerCell; i++)
            {
                FleckMaker.ThrowDustPuff(rect.RandomVector3, map, 2f);
            }
            if (f.cameraShake > 0f && map == Find.CurrentMap)
            {
                Find.CameraDriver.shaker.DoShake(f.cameraShake);
            }
            f.impactSound?.PlayOneShot(SoundInfo.InMap(new TargetInfo(position, map)));
            Destroy();
        }

        public override void ExposeData()
        {
            // 已摧毀的發射建築不存參照（讀檔會找不到）；Projectile 本身以 "launcher" 鍵存它。
            if (Scribe.mode == LoadSaveMode.Saving && launcher != null && launcher.Destroyed) { launcher = null; }
            base.ExposeData();
            Scribe_Deep.Look(ref config, "config");
            Scribe_References.Look(ref attacker, "attacker");
            Scribe_Values.Look(ref bvrHandled, "bvrHandled", false);
            Scribe_Values.Look(ref worldPhaseDone, "worldPhaseDone", false);
            Scribe_Values.Look(ref strikeMissionId, "strikeMissionId", -1);
            Scribe_Values.Look(ref strikeWave, "strikeWave", -1);
            Scribe_Values.Look(ref strikeReported, "strikeReported", false);
            Scribe_Values.Look(ref checkedBvr, "checkedBvr", false);
            Scribe_Values.Look(ref hasEntryHeading, "hasEntryHeading", false);
            Scribe_Values.Look(ref entryHeading, "entryHeading", 0f);
        }
    }
}
