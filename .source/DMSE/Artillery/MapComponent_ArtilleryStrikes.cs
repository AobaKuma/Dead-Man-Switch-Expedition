using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    // =========================================================================
    //  ArtilleryShellRequest — 單發砲彈的排程資料
    // =========================================================================

    /// <summary>
    /// 一筆待落地砲彈的完整描述，由 <see cref="MapComponent_ArtilleryStrikes"/> 持有並序列化。
    /// 當 <see cref="fireTick"/> 到達時，<see cref="ArtilleryStrikeUtility.SpawnShell"/> 生成實際的 <see cref="Projectile"/>。
    /// </summary>
    public class ArtilleryShellRequest : IExposable
    {
        /// <summary>應生成投射物的遊戲 tick。</summary>
        public int fireTick;

        /// <summary>玩家選定的目標格（散佈前）。</summary>
        public IntVec3 targetCell;

        /// <summary>砲彈從地圖哪個邊緣格進入，在 <see cref="WorldObject_ArtilleryStrike.Arrived"/> 時預先計算。</summary>
        public IntVec3 entryEdgeCell;

        /// <summary>要生成的 <see cref="Projectile"/> ThingDef（需繼承 <c>Projectile_Explosive</c>）。</summary>
        public ThingDef projectileDef;

        /// <summary>攻擊方陣營，傳入 <c>Projectile.launcher</c> 用於傷害歸因。</summary>
        public Faction attackerFaction;

        /// <summary>以目標格為圓心的散佈半徑（格數）。0 = 精準。</summary>
        public float scatterRadius;

        public void ExposeData()
        {
            Scribe_Values.Look(ref fireTick,      "fireTick",      0);
            Scribe_Values.Look(ref targetCell,    "targetCell",    default);
            Scribe_Values.Look(ref entryEdgeCell, "entryEdgeCell", default);
            Scribe_Defs.Look(ref projectileDef,   "projectileDef");
            Scribe_References.Look(ref attackerFaction, "attackerFaction");
            Scribe_Values.Look(ref scatterRadius, "scatterRadius", 0f);
        }
    }

    // =========================================================================
    //  MapComponent_ArtilleryStrikes — 地圖級砲擊排程器
    // =========================================================================

    /// <summary>
    /// 地圖組件，持有所有「已在世界航跡上、即將落地」的砲彈請求。
    /// 由 <see cref="WorldObject_ArtilleryStrike.Arrived"/> 填入，每 tick 輪詢是否到時。
    /// </summary>
    public class MapComponent_ArtilleryStrikes : MapComponent
    {
        private List<ArtilleryShellRequest> pending = new List<ArtilleryShellRequest>();

        public MapComponent_ArtilleryStrikes(Map map) : base(map) { }

        /// <summary>登記一筆砲彈請求（由 WorldObject 在抵達時呼叫）。</summary>
        public void Enqueue(ArtilleryShellRequest req)
        {
            if (req != null) { pending.Add(req); }
        }

        public override void MapComponentTick()
        {
            if (pending.Count == 0) { return; }

            int now = Find.TickManager.TicksGame;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (now >= pending[i].fireTick)
                {
                    ArtilleryStrikeUtility.SpawnShell(pending[i], map);
                    pending.RemoveAt(i);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref pending, "artilleryPending", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pending == null) { pending = new List<ArtilleryShellRequest>(); }
            }
        }
    }
}
