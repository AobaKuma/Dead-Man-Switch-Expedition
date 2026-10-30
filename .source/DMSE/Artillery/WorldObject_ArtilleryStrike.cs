using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 超視距火砲打擊的世界地圖航跡物件。
    ///
    /// 工作流程：
    ///   1. <see cref="CompArtilleryStrike"/> 建立本物件，填入目標格、彈藥 def、齊射參數。
    ///   2. 由 <see cref="ScorerProjectile"/> 離場後帶入世界，並在世界地圖上做 Slerp 飛行。
    ///   3. 抵達目的地 tile 後呼叫 <see cref="Arrived"/>：
    ///      a. 確保目標地圖已載入（GetOrGenerateMap）。
    ///      b. 計算邊緣格，透過 <see cref="ArtilleryStrikeUtility.QueueSalvo"/> 排程齊射。
    ///      c. 自毀本物件。
    ///
    /// 與 <see cref="ScorerProjectile_WorldObject"/> 的差異：
    ///   - 抵達時不詢問玩家選目標，直接使用 <see cref="targetCell"/>。
    ///   - 落地物件是 <see cref="Projectile"/>（從邊緣格飛行至目標），而非 Skyfaller 直落。
    /// </summary>
    public class WorldObject_ArtilleryStrike : WorldObject
    {
        // =====================================================================
        //  公開資料（由 CompArtilleryStrike 填入）
        // =====================================================================

        /// <summary>玩家在地圖上選定的目標格（散佈在 SpawnShell 時套用）。</summary>
        public IntVec3 targetCell;

        /// <summary>要生成的 Projectile ThingDef（應繼承 Projectile_Explosive）。</summary>
        public ThingDef projectileDef;

        /// <summary>攻擊方陣營，用於傷害歸因。</summary>
        public Faction attackerFaction;

        /// <summary>散佈半徑（格）。</summary>
        public float scatterRadius;

        /// <summary>齊射發數。</summary>
        public int salvoCount = 1;

        /// <summary>每發之間的間隔 tick。</summary>
        public int salvoIntervalTicks = 60;

        /// <summary>世界航跡速度：每跨一格地表等效距離需要的 tick。</summary>
        public float worldTicksPerTile = 60f;

        /// <summary>雷達導引：每發落彈時改指向目標地圖上的敵方目標（見 <see cref="ArtilleryStrikeUtility.FindGuidedTarget"/>）。</summary>
        public bool guided;

        /// <summary>目的地世界 tile（由 CompArtilleryStrike 在發射時設定）。</summary>
        public PlanetTile destinationTile = PlanetTile.Invalid;

        // =====================================================================
        //  內部狀態
        // =====================================================================

        private PlanetTile initialTile = PlanetTile.Invalid;

        /// <summary>發射地的世界 tile（由 <see cref="PostAdd"/> 設定，供 <see cref="ArtilleryStrikeUtility"/> 計算進入方向）。</summary>
        public PlanetTile InitialTile => initialTile;
        private float      traveledPct;
        private bool       arrived;

        // =====================================================================
        //  世界地圖顯示
        // =====================================================================

        private Vector3 Start
        {
            get
            {
                PlanetTile t = initialTile.Valid ? initialTile : Tile;
                return t.Valid ? Find.WorldGrid.GetTileCenter(t) : Vector3.zero;
            }
        }

        private Vector3 End =>
            destinationTile.Valid ? Find.WorldGrid.GetTileCenter(destinationTile) : Vector3.zero;

        public override Vector3 DrawPos => Vector3.Slerp(Start, End, traveledPct);

        public override bool ExpandingIconFlipHorizontal =>
            GenWorldUI.WorldToUIPosition(Start).x > GenWorldUI.WorldToUIPosition(End).x;

        public override float ExpandingIconRotation
        {
            get
            {
                if (!def.rotateGraphicWhenTraveling) { return base.ExpandingIconRotation; }
                Vector2 s = GenWorldUI.WorldToUIPosition(Start);
                Vector2 e = GenWorldUI.WorldToUIPosition(End);
                float angle = Mathf.Atan2(e.y - s.y, e.x - s.x) * Mathf.Rad2Deg;
                if (angle > 180f) { angle -= 180f; }
                return angle + 90f;
            }
        }

        // =====================================================================
        //  移動速度
        // =====================================================================

        private float TraveledPctStepPerTick
        {
            get
            {
                Vector3 s = Start, e = End;
                if (s == e) { return 1f; }
                float dist = GenMath.SphericalDistance(s.normalized, e.normalized);
                if (dist == 0f) { return 1f; }
                // 與導彈相同的換算（MissileWorldMotion）：角速度 = 1 / (每弧度格數 × 每格 tick)
                float angularSpeed = 1f / Mathf.Max(0.001f,
                    Find.WorldGrid.ApproxDistanceInTiles(1f) * Mathf.Max(1f, worldTicksPerTile));
                return angularSpeed / dist;
            }
        }

        // =====================================================================
        //  生命週期
        // =====================================================================

        public override void PostAdd()
        {
            base.PostAdd();
            initialTile = Tile;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            traveledPct += TraveledPctStepPerTick * delta;
            if (traveledPct >= 1f)
            {
                traveledPct = 1f;
                Arrived();
            }
        }

        // =====================================================================
        //  抵達邏輯
        // =====================================================================

        private void Arrived()
        {
            if (arrived) { return; }
            arrived = true;

            if (!destinationTile.Valid)
            {
                Log.Error("[DMSE Artillery] WorldObject_ArtilleryStrike 抵達時 destinationTile 無效，已跳過。");
                Destroy();
                return;
            }

            // 目標 tile 上沒有地圖或據點 → 落空（不在空格上生成營地地圖）
            Map map = MissileWorldUtility.ResolveArrivalMap(destinationTile, null, allowGenerateMap: true);
            if (map == null)
            {
                Messages.Message("DMSE.Artillery.Missed".Translate(), new GlobalTargetInfo(destinationTile),
                    MessageTypeDefOf.NeutralEvent, historical: false);
                Destroy();
                return;
            }

            // 與導彈抵達相同：砲擊非敵對陣營會使其敵對；登記交戰時間，
            // 否則抵達時才生成、沒有我方人員的地圖會在砲彈落地前被原版移除（Patch_ShouldBeRemoved）。
            if (attackerFaction != null && attackerFaction.IsPlayer &&
                map.Parent?.Faction is Faction f && !f.IsPlayer &&
                f.RelationKindWith(attackerFaction) != FactionRelationKind.Hostile)
            {
                f.TryAffectGoodwillWith(attackerFaction, f.GoodwillToMakeHostile(attackerFaction), true);
            }
            GameComponent_MissileEngage.Comp.times.SetOrAdd(map, Find.TickManager.TicksGame + GenDate.TicksPerDay * 2);

            // 排程所有齊射
            ArtilleryStrikeUtility.QueueSalvo(map, this);

            Destroy();
        }

        // =====================================================================
        //  序列化
        // =====================================================================

        public override void ExposeData()
        {
            base.ExposeData();

            // PlanetTile 以「tileId,layerId」完整序列化（原版已註冊解析器）
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref initialTile,     "initialTile",     PlanetTile.Invalid);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 舊存檔只存 tileId（圖層遺失），只能視為地表
                if (!destinationTile.Valid)
                {
                    int legacyDest = -1;
                    Scribe_Values.Look(ref legacyDest, "destTileId", -1);
                    if (legacyDest >= 0) { destinationTile = new PlanetTile(legacyDest); }
                }
                if (!initialTile.Valid)
                {
                    int legacyInit = -1;
                    Scribe_Values.Look(ref legacyInit, "initTileId", -1);
                    if (legacyInit >= 0) { initialTile = new PlanetTile(legacyInit); }
                }
            }

            Scribe_Values.Look(ref traveledPct,       "traveledPct",       0f);
            Scribe_Values.Look(ref arrived,            "arrived",           false);
            Scribe_Values.Look(ref targetCell,         "targetCell",        default);
            Scribe_Defs.Look(ref projectileDef,        "projectileDef");
            Scribe_References.Look(ref attackerFaction, "attackerFaction");
            Scribe_Values.Look(ref scatterRadius,      "scatterRadius",     0f);
            Scribe_Values.Look(ref salvoCount,         "salvoCount",        1);
            Scribe_Values.Look(ref salvoIntervalTicks, "salvoIntervalTicks", 60);
            Scribe_Values.Look(ref guided,             "guided",            false);
            Scribe_Values.Look(ref worldTicksPerTile,  "worldTicksPerTile", 60f);
        }
    }
}
