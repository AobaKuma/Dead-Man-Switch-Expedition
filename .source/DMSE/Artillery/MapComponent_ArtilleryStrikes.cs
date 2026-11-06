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

        /// <summary>雷達導引：生成時改以當下的敵方目標為中心（找不到則用 <see cref="targetCell"/>）。</summary>
        public bool guided;

        /// <summary>
        /// 效力射彈：以已登錄的校射落點為散佈中心（<see cref="targetCell"/> 已由開火端解析）。
        /// 效力射不再登錄落點；校射彈（guided 或無導引）才會更新落點。
        /// </summary>
        public bool fireForEffect;

        public void ExposeData()
        {
            Scribe_Values.Look(ref fireTick,      "fireTick",      0);
            Scribe_Values.Look(ref targetCell,    "targetCell",    default);
            Scribe_Values.Look(ref entryEdgeCell, "entryEdgeCell", default);
            Scribe_Defs.Look(ref projectileDef,   "projectileDef");
            Scribe_References.Look(ref attackerFaction, "attackerFaction");
            Scribe_Values.Look(ref scatterRadius, "scatterRadius", 0f);
            Scribe_Values.Look(ref guided,        "guided",        false);
            Scribe_Values.Look(ref fireForEffect, "fireForEffect", false);
        }
    }

    // =========================================================================
    //  ArtilleryImpactRegistration — 某陣營在這張地圖上的效力射落點
    // =========================================================================

    /// <summary>
    /// 最後一次校射彈的落點，成為同陣營火砲對這張地圖效力射的散佈中心。
    /// 由 <see cref="ArtilleryStrikeUtility.SpawnShell"/> 在砲彈生成那一 tick 登錄（同陣營覆蓋），
    /// 存活週期跟著地圖：地圖被移除，落點也一併消失（與「目標地圖必須已 spawned」一致）。
    /// </summary>
    public class ArtilleryImpactRegistration : IExposable
    {
        /// <summary>登錄落點的陣營；只有同陣營的砲會共用這個落點。</summary>
        public Faction faction;

        /// <summary>落點格（砲彈實際散佈後的中心）。</summary>
        public IntVec3 cell = IntVec3.Invalid;

        /// <summary>登錄的遊戲 tick（檢視／除錯用）。</summary>
        public int tick = -1;

        public void ExposeData()
        {
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref tick, "tick", -1);
        }
    }

    // =========================================================================
    //  MapComponent_ArtilleryStrikes — 地圖級砲擊排程器
    // =========================================================================

    /// <summary>
    /// 地圖組件，持有所有「已在世界航跡上、即將落地」的砲彈請求。
    /// 由 <see cref="WorldObject_ArtilleryStrike.Arrived"/> 填入，每 tick 輪詢是否到時。
    /// 另外保存各陣營在這張地圖上最後一次校射的落點（效力射的散佈中心，同陣營共用）。
    /// </summary>
    public class MapComponent_ArtilleryStrikes : MapComponent
    {
        private List<ArtilleryShellRequest> pending = new List<ArtilleryShellRequest>();

        /// <summary>各陣營在此地圖上的效力射落點（每陣營最多一筆 = 最後一次校射）。</summary>
        private List<ArtilleryImpactRegistration> impacts = new List<ArtilleryImpactRegistration>();

        public MapComponent_ArtilleryStrikes(Map map) : base(map) { }

        /// <summary>登記一筆砲彈請求（由 WorldObject 在抵達時呼叫）。</summary>
        public void Enqueue(ArtilleryShellRequest req)
        {
            if (req != null) { pending.Add(req); }
        }

        /// <summary>尚未落下的砲彈請求（唯讀；測試用）。</summary>
        public IReadOnlyList<ArtilleryShellRequest> Pending => pending;

        // =====================================================================
        //  效力射落點
        // =====================================================================

        /// <summary>
        /// 登錄一次校射落點（同陣營覆蓋）。由 <see cref="ArtilleryStrikeUtility.SpawnShell"/> 在砲彈生成時呼叫，
        /// 之後同陣營火砲對這張地圖效力射時就以這裡為散佈中心。
        /// </summary>
        public void RegisterImpact(Faction faction, IntVec3 cell)
        {
            if (!cell.IsValid || !cell.InBounds(map)) { return; }
            if (impacts == null) { impacts = new List<ArtilleryImpactRegistration>(); }

            ArtilleryImpactRegistration reg = null;
            for (int i = 0; i < impacts.Count; i++)
            {
                if (impacts[i].faction == faction) { reg = impacts[i]; break; }
            }
            if (reg == null)
            {
                reg = new ArtilleryImpactRegistration { faction = faction };
                impacts.Add(reg);
            }
            reg.cell = cell;
            reg.tick = Find.TickManager.TicksGame;
        }

        /// <summary>取得某陣營在這張地圖上最後一次校射的落點；沒有校射過時回傳 false。</summary>
        public bool TryGetRegisteredImpact(Faction faction, out IntVec3 cell, out int tick)
        {
            cell = IntVec3.Invalid;
            tick = -1;
            if (impacts == null) { return false; }
            for (int i = 0; i < impacts.Count; i++)
            {
                ArtilleryImpactRegistration reg = impacts[i];
                if (reg.faction == faction && reg.cell.IsValid)
                {
                    cell = reg.cell;
                    tick = reg.tick;
                    return true;
                }
            }
            return false;
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
            Scribe_Collections.Look(ref impacts, "artilleryImpactPoints", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pending == null) { pending = new List<ArtilleryShellRequest>(); }
                if (impacts == null) { impacts = new List<ArtilleryImpactRegistration>(); }
            }
        }
    }
}
