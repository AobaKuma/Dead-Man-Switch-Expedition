using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈世界航跡的共用基底：玩家發射的 <see cref="ScorerProjectile_WorldObject"/>
    /// 與來襲的 <see cref="WorldObject_IncomingMissile"/>。
    ///
    /// 終端更新（逃逸區）：發射時鎖定 <see cref="targetParent"/>；目標換了 tile 時，
    /// 具導引能力的導彈可從當下位置改飛新位置，次數上限由彈體決定
    /// （<see cref="MissileConfig.TerminalUpdates"/>），且改飛後的總航程不得超過射程。
    /// 次數用完或燃料不足時沿最後位置飛行，抵達時目標不在就落空。
    /// </summary>
    public abstract class MissileWorldObject : WorldObject, IBVRTrack
    {
        public MissileConfig config;
        public PlanetTile destinationTile = PlanetTile.Invalid;

        /// <summary>發射時鎖定的目標；null 表示打擊固定 tile。</summary>
        public MapParent targetParent;

        protected PlanetTile initialTile = PlanetTile.Invalid;
        protected float traveledPct;
        protected bool arrived;

        // 目前航段；終端更新後從當下位置重新起算
        private bool hasLegStart;
        private Vector3 legStart;
        private float legLength = -1f;   // 本航段消耗的射程（地表格，含跨圖層成本）；-1 = 依起訖 tile 計算
        private float rangeSpent;        // 先前航段已消耗的射程
        private int terminalUpdatesUsed;

        private int ticksUntilTerminalCheck;

        /// <summary>抵達時若目標存在但尚未生成地圖（例：敵方據點），是否生成其地圖。</summary>
        protected abstract bool AllowGenerateTargetMap { get; }

        /// <summary>抵達。<paramref name="map"/> 為 null 表示落空；實作需自行 <see cref="WorldObject.Destroy"/>。</summary>
        protected abstract void OnArrived(Map map);

        public int RouteRevision => terminalUpdatesUsed;

        public int TerminalUpdatesRemaining
        {
            get
            {
                int total = config != null ? config.TerminalUpdates : 0;
                return Mathf.Max(0, total - terminalUpdatesUsed);
            }
        }

        protected Vector3 Start
        {
            get
            {
                if (hasLegStart) { return legStart; }
                return MissileWorldUtility.TileCenter(initialTile.Valid ? initialTile : Tile);
            }
        }

        protected Vector3 End => MissileWorldUtility.TileCenter(destinationTile);

        public override Vector3 DrawPos => PositionAt(0);

        public override bool ExpandingIconFlipHorizontal
            => GenWorldUI.WorldToUIPosition(Start).x > GenWorldUI.WorldToUIPosition(End).x;

        public override float ExpandingIconRotation
        {
            get
            {
                if (!def.rotateGraphicWhenTraveling) { return base.ExpandingIconRotation; }
                Vector2 a = GenWorldUI.WorldToUIPosition(Start);
                Vector2 b = GenWorldUI.WorldToUIPosition(End);
                float num = Mathf.Atan2(b.y - a.y, b.x - a.x) * 57.29578f;
                if (num > 180f) { num -= 180f; }
                return num + 90f;
            }
        }

        private int lastMotionTick = -1;
        private BVRTrackState bvrState = new BVRTrackState();
        public WorldObject Self => this;
        public Faction Attacker => Faction;
        public PlanetTile Origin => initialTile;
        public PlanetTile Destination => destinationTile;
        public MissileConfig Config => config;
        public BVRTrackState State => bvrState ??= new BVRTrackState();
        public virtual ThingDef IncomingDef => config?.body?.incomingSkyfaller;
        public BVRTargetProps TargetProps => IncomingDef?.GetModExtension<BVRTargetProps>() ?? BVRTargetProps.Default;
        public float AngularSpeed => MissileWorldMotion.AngularSpeed(config);
        private float TraveledPctStepPerTick => AngularSpeed / Mathf.Max(0.0000001f,
            GenMath.SphericalDistance(Start.normalized, End.normalized));
        private float CurrentProgress => Mathf.Clamp01(traveledPct + TraveledPctStepPerTick *
            (lastMotionTick < 0 ? 0 : Mathf.Max(0, Find.TickManager.TicksGame - lastMotionTick)));
        public int TicksToArrival => Mathf.Max(0, Mathf.CeilToInt((1f - CurrentProgress) / TraveledPctStepPerTick));
        public Vector3 PositionAt(int ticksAhead) => Vector3.Slerp(Start, End,
            Mathf.Clamp01(CurrentProgress + Mathf.Max(0, ticksAhead) * TraveledPctStepPerTick));
        public override Color ExpandingIconColor => State.detectedByFaction == Faction.OfPlayer
            ? Color.red : Faction?.Color ?? Color.white;
        public void Notify_Intercepted(Faction by)
        {
            if (Destroyed) return;
            if (by == Faction.OfPlayer || Attacker == Faction.OfPlayer)
                Messages.Message("DMSE.BVR.World.Intercepted".Translate(Label, by?.Name ?? "?"),
                    new GlobalTargetInfo(destinationTile), by == Faction.OfPlayer ? MessageTypeDefOf.PositiveEvent
                        : MessageTypeDefOf.NegativeEvent, historical: false);
            WorldComponent_BVRNetwork.Current?.AddFlash(PositionAt(0));
            Destroy();
        }

        private float CurrentLegLength
        {
            get
            {
                if (legLength >= 0f) { return legLength; }
                return MissileWorldUtility.RangeDistance(initialTile.Valid ? initialTile : Tile, destinationTile);
            }
        }

        public override void PostAdd()
        {
            base.PostAdd();
            initialTile = Tile;
            lastMotionTick = Find.TickManager.TicksGame;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (arrived) { return; }

            int now = Find.TickManager.TicksGame;
            int elapsed = lastMotionTick < 0 ? delta : Mathf.Max(0, now - lastMotionTick);
            // Commit elapsed movement before a terminal update starts a new leg.
            traveledPct = Mathf.Clamp01(traveledPct + TraveledPctStepPerTick * elapsed);
            lastMotionTick = now;
            ticksUntilTerminalCheck -= elapsed;
            if (ticksUntilTerminalCheck <= 0)
            {
                ticksUntilTerminalCheck = MissileWorldUtility.TerminalCheckIntervalTicks;
                TryTerminalUpdate();
            }

            if (traveledPct >= 1f)
            {
                traveledPct = 1f;
                arrived = true;
                OnArrived(MissileWorldUtility.ResolveArrivalMap(destinationTile, targetParent, AllowGenerateTargetMap));
            }
        }

        /// <summary>目標換了 tile 時嘗試改飛；不符合條件就維持原航線（之後落空）。</summary>
        private void TryTerminalUpdate()
        {
            if (targetParent == null || targetParent.Destroyed) { return; }

            PlanetTile targetTile = targetParent.Tile;
            if (!targetTile.Valid || targetTile == destinationTile) { return; }
            if (TerminalUpdatesRemaining <= 0) { return; }

            Vector3 here = Vector3.Slerp(Start, End, traveledPct);
            float spent = rangeSpent + CurrentLegLength * traveledPct;
            float newLeg = MissileWorldUtility.SurfaceTilesBetween(here, MissileWorldUtility.TileCenter(targetTile));
            if (!MissileWorldUtility.SameLayer(destinationTile, targetTile))
            {
                newLeg += MissileWorldUtility.CrossLayerRangeCost;
            }
            int range = config != null ? config.Range : 0;
            if (range > 0 && spent + newLeg > range) { return; }

            rangeSpent = spent;
            legStart = here;
            hasLegStart = true;
            legLength = newLeg;
            destinationTile = targetTile;
            traveledPct = 0f;
            terminalUpdatesUsed++;

            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE Missile] 終端更新 → {targetTile}（剩餘 {TerminalUpdatesRemaining} 次，已用射程 {spent:0.#}/{(range > 0 ? range.ToString() : "∞")}）");
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            if (config != null && config.HasTerminalGuidance)
            {
                if (!text.NullOrEmpty()) { text += "\n"; }
                text += "DMSE.Missile.TerminalUpdatesRemaining".Translate(TerminalUpdatesRemaining, config.TerminalUpdates);
            }
            if (!text.NullOrEmpty()) text += "\n";
            text += "DMSE.BVR.World.MissileType".Translate(config?.body?.LabelCap ?? Label) + "\n";
            string destination = targetParent?.Label ?? Find.WorldObjects.MapParentAt(destinationTile)?.Label;
            text += "DMSE.BVR.World.TrackInspect".Translate(destination ?? destinationTile.ToString(),
                TicksToArrival.ToStringTicksToPeriod(), State.StatusKey.Translate(), State.interceptAttempts);
            return text;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref initialTile, "initialTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref traveledPct, "traveledPct", 0f);
            Scribe_Values.Look(ref arrived, "arrived", false);
            Scribe_Values.Look(ref lastMotionTick, "lastMotionTick", -1);
            Scribe_Deep.Look(ref bvrState, "bvrState");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                bvrState ??= new BVRTrackState();
                if (lastMotionTick < 0) lastMotionTick = Find.TickManager.TicksGame;
            }
            Scribe_Deep.Look(ref config, "config");
            Scribe_References.Look(ref targetParent, "targetParent");
            Scribe_Values.Look(ref hasLegStart, "hasLegStart", false);
            Scribe_Values.Look(ref legStart, "legStart");
            Scribe_Values.Look(ref legLength, "legLength", -1f);
            Scribe_Values.Look(ref rangeSpent, "rangeSpent", 0f);
            Scribe_Values.Look(ref terminalUpdatesUsed, "terminalUpdatesUsed", 0);
        }
    }
}
