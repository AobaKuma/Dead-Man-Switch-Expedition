using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 抵達落點的導彈（舊 skyfaller 版，已由 <see cref="Projectile_MissileIncoming"/> 取代）。
    /// 保留給舊存檔：落地 def 已改為 Projectile，讀檔時若遇到飛行中或被 BVR 收起的舊導彈，
    /// 轉換成新 Projectile 繼續（<see cref="ConvertToProjectile"/>；docs/Missile_Skyfaller轉Projectile_計畫.md §9）。
    /// </summary>
    public class MissileIncoming : Skyfaller, IIncomingMissile
    {
        public MissileConfig config;

        /// <summary>發射此導彈的陣營（來襲方）。供攔截/識別用。</summary>
        public Faction attacker;

        /// <summary>是否已交由 BVR 處理（避免重複登記攔截）。</summary>
        public bool bvrHandled;
        public bool worldPhaseDone;

        /// <summary>波次打擊的任務 id 與波次（-1 = 非波次打擊）；落地記為命中，未落地就被銷毀記為末端擊落。</summary>
        public int strikeMissionId = -1;
        public int strikeWave = -1;
        private bool strikeReported;

        public MissileConfig Config { get => config; set => config = value; }
        public Faction Attacker { get => attacker; set => attacker = value; }
        public bool BvrHandled { get => bvrHandled; set => bvrHandled = value; }
        public bool WorldPhaseDone { get => worldPhaseDone; set => worldPhaseDone = value; }
        public int StrikeMissionId { get => strikeMissionId; set => strikeMissionId = value; }
        public int StrikeWave { get => strikeWave; set => strikeWave = value; }

        /// <summary>只顯示 def.label 且剔除括號後綴（如「cruise missile (incoming)」→「cruise missile」）。</summary>
        public override string Label => MissileImpactUtility.StripSuffix(def.label);

        private bool checkedBvr;

        /// <summary>落地 def 已不是 skyfaller（已轉為 Projectile）：這個物件來自舊存檔，需要轉換。</summary>
        private bool Obsolete => def.skyfaller == null;

        /// <summary>
        /// 建立帶相同設定的新 <see cref="Projectile_MissileIncoming"/>（未生成；同一 def）。
        /// 舊存檔沒有世界航跡方位，轉換後照原本垂直墜落。
        /// </summary>
        public Projectile_MissileIncoming ConvertToProjectile()
        {
            if (!typeof(Projectile_MissileIncoming).IsAssignableFrom(def.thingClass)) { return null; }
            Projectile_MissileIncoming p = (Projectile_MissileIncoming)ThingMaker.MakeThing(def);
            p.config = config;
            p.attacker = attacker;
            p.bvrHandled = bvrHandled;
            p.worldPhaseDone = worldPhaseDone;
            p.strikeMissionId = strikeReported ? -1 : strikeMissionId;
            p.strikeWave = strikeWave;
            return p;
        }

        public override Vector3 DrawPos => Obsolete ? Position.ToVector3Shifted() : base.DrawPos;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (Obsolete) { return; }
            base.DrawAt(drawLoc, flip);
        }

        protected override void Tick()
        {
            if (Obsolete)
            {
                // 舊存檔：以新 Projectile 在同一落點重新墜落（不再登記 BVR：讀檔前已處理過）。
                Map map = Map;
                IntVec3 cell = Position;
                Projectile_MissileIncoming replacement = ConvertToProjectile();
                strikeReported = true; // 回報交給替代物件
                Destroy(DestroyMode.Vanish);
                if (replacement != null)
                {
                    replacement.bvrHandled = true;
                    GenSpawn.Spawn(replacement, cell, map);
                }
                return;
            }

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
            base.Tick();
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (!strikeReported && strikeMissionId >= 0)
            {
                strikeReported = true;
                WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, StrikeOutcome.TerminalKilled);
            }
            base.Destroy(mode);
        }

        protected override void Impact()
        {
            if (!strikeReported && strikeMissionId >= 0)
            {
                strikeReported = true;
                WorldComponent_StrikeReports.Report(strikeMissionId, strikeWave, StrikeOutcome.Hit);
            }
            MissileImpactUtility.Detonate(config, Position, Map, attacker);
            base.Impact();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref config, "config");
            Scribe_References.Look(ref attacker, "attacker");
            Scribe_Values.Look(ref bvrHandled, "bvrHandled", false);
            Scribe_Values.Look(ref worldPhaseDone, "worldPhaseDone", false);
            Scribe_Values.Look(ref strikeMissionId, "strikeMissionId", -1);
            Scribe_Values.Look(ref strikeWave, "strikeWave", -1);
            Scribe_Values.Look(ref strikeReported, "strikeReported", false);
            Scribe_Values.Look(ref checkedBvr, "checkedBvr", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && innerContainer == null)
            {
                // 存檔缺 innerContainer 節點時 Scribe_Deep 會把它讀成 null，Skyfaller.Destroy 會因此出錯（轉換流程要先 Destroy）。
                innerContainer = new ThingOwner<Thing>(this);
            }
        }
    }
}
