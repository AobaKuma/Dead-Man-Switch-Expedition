using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 超視距火砲的「離場投射物」。
    ///
    /// 工作流程：
    ///   1. <see cref="CompArtilleryStrike.LaunchToTile"/> 生成本物件，設定所有目標參數，
    ///      再用 <see cref="Launch"/> 朝地圖對應方位的邊緣格發射。
    ///   2. 投射物沿弧線飛向邊緣格（<see cref="ProjectileDef.flyOverhead"/> = true）。
    ///   3. 抵達邊緣格後 <see cref="Impact"/> 被呼叫：
    ///      a. 以本物件攜帶的資料建立 <see cref="WorldObject_ArtilleryStrike"/>。
    ///      b. 將 WorldObject 加入世界地圖，開始 Slerp 飛行至目標 tile。
    ///      c. 本物件以 Vanish 模式銷毀（無爆炸）。
    ///
    /// 為何不用 Skyfaller：Skyfaller 動畫從建築正上方升起，視覺上不像砲擊。
    /// 本類別使用標準 Projectile 弧線，視覺上更貼近真實砲彈出膛後飛向地平線。
    ///
    /// 存檔安全性：所有 WorldObject 所需資料序列化於本物件，
    /// 即使在飛行中存檔也能正確還原。
    /// </summary>
    public class Projectile_ArtilleryDeparture : Projectile
    {
        // =====================================================================
        //  建立 WorldObject 所需的序列化資料
        //  由 CompArtilleryStrike.LaunchToTile 在 Launch() 前設定
        // =====================================================================

        /// <summary>目標 tile ID（世界地圖）。-1 = 未設定。</summary>
        public int destTileId = -1;

        /// <summary>目標地圖上的落彈格（地圖中心，散佈在 SpawnShell 時套用）。</summary>
        public IntVec3 artilleryTargetCell;

        /// <summary>落地所使用的 Projectile ThingDef。</summary>
        public ThingDef artilleryProjectileDef;

        /// <summary>攻擊方陣營，用於傷害歸因。</summary>
        public Faction artilleryAttackerFaction;

        /// <summary>散佈半徑（格）。</summary>
        public float artilleryScatterRadius;

        /// <summary>齊射發數。</summary>
        public int artillerySalvoCount = 1;

        /// <summary>齊射各發間隔（tick）。</summary>
        public int artillerySalvoIntervalTicks = 60;

        /// <summary>世界地圖物件的 def（用於重建 WorldObject）。</summary>
        public WorldObjectDef artilleryWorldObjectDef;

        // =====================================================================
        //  存檔
        // =====================================================================

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref destTileId,              "destTileId",              -1);
            Scribe_Values.Look(ref artilleryTargetCell,     "artilleryTargetCell",     default);
            Scribe_Defs.Look(ref artilleryProjectileDef,    "artilleryProjectileDef");
            Scribe_References.Look(ref artilleryAttackerFaction, "artilleryAttackerFaction");
            Scribe_Values.Look(ref artilleryScatterRadius,  "artilleryScatterRadius",  0f);
            Scribe_Values.Look(ref artillerySalvoCount,     "artillerySalvoCount",     1);
            Scribe_Values.Look(ref artillerySalvoIntervalTicks, "artillerySalvoIntervalTicks", 60);
            Scribe_Defs.Look(ref artilleryWorldObjectDef,   "artilleryWorldObjectDef");
        }

        // =====================================================================
        //  抵達邊緣格
        // =====================================================================

        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            // 驗證必要資料
            if (destTileId < 0 || artilleryWorldObjectDef == null || artilleryProjectileDef == null)
            {
                Log.Error("[DMSE Artillery] Projectile_ArtilleryDeparture 抵達邊緣時缺少必要資料，已取消。");
                Destroy(DestroyMode.Vanish);
                return;
            }

            // 建立世界地圖航跡物件
            WorldObject_ArtilleryStrike wo =
                (WorldObject_ArtilleryStrike)WorldObjectMaker.MakeWorldObject(artilleryWorldObjectDef);
            wo.targetCell         = artilleryTargetCell;
            wo.projectileDef      = artilleryProjectileDef;
            wo.attackerFaction    = artilleryAttackerFaction;
            wo.scatterRadius      = artilleryScatterRadius;
            wo.salvoCount         = artillerySalvoCount;
            wo.salvoIntervalTicks = artillerySalvoIntervalTicks;

            // 出發 tile = 本投射物所在地圖的世界 tile
            wo.Tile            = Map.Tile;
            wo.destinationTile = new PlanetTile(destTileId);

            if (artilleryAttackerFaction != null)
            {
                wo.SetFaction(artilleryAttackerFaction);
            }

            Find.WorldObjects.Add(wo);

            // 無爆炸，Vanish 銷毀
            Destroy(DestroyMode.Vanish);
        }
    }
}
