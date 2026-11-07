using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class WorldObject_Interceptor : WorldObject
    {
        public int launchTick, interceptTick;
        public int targetRevision;
        public Vector3 start, end;
        /// <summary>目標航跡的世界物件；<see cref="WorldComponent_BVRNetwork.TrackOf"/> 取得航跡。</summary>
        public WorldObject target;
        public MapParent launchNode;
        public PlanetTile targetDestination = PlanetTile.Invalid;
        public float hitChance, payloadCapacity;
        public MissilePartDef warhead;

        /// <summary>佔一條火力通道直到結算；點防禦式武器的攔截物不佔。</summary>
        public bool usesChannel = true;

        /// <summary>命中判定後的額外擲骰；負值 = 舊存檔，依 <see cref="warhead"/> 計算。</summary>
        public int extraRolls = -1;

        public int ExtraRolls => extraRolls >= 0 ? extraRolls : warhead?.warheadEffect?.ExtraInterceptRolls(payloadCapacity) ?? 0;
        private float FlightFraction => Mathf.Clamp01((float)(Find.TickManager.TicksGame - launchTick) / Mathf.Max(1, interceptTick - launchTick));

        /// <summary>地表上的位置；<see cref="DrawPos"/> 再抬到目前高度。</summary>
        public Vector3 GroundPos => Vector3.Slerp(start, end, FlightFraction);

        /// <summary>會合點的抬升量。目標不是玩家知道的航跡時為 0，不從攔截彈洩漏目標高度。</summary>
        public float MeetLift
        {
            get
            {
                IBVRTrack track = WorldComponent_BVRNetwork.Current?.TrackOf(target);
                if (track == null || track.Self.Destroyed || !BVRWorldDrawer.KnownToPlayer(track)) { return 0f; }
                return AirTrackDisplay.Lift(track.AltitudeAt(Mathf.Max(0, interceptTick - Find.TickManager.TicksGame)));
            }
        }

        public float CurrentLift => MeetLift * FlightFraction;
        public override Vector3 DrawPos => AirTrackDisplay.Raised(GroundPos, CurrentLift);
        public override bool ExpandingIconFlipHorizontal
            => GenWorldUI.WorldToUIPosition(start).x > GenWorldUI.WorldToUIPosition(end).x;
        public override float ExpandingIconRotation
        {
            get
            {
                Vector2 a = GenWorldUI.WorldToUIPosition(start);
                Vector2 b = GenWorldUI.WorldToUIPosition(end);
                return Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg + 90f;
            }
        }
        public override string GetInspectString() => "DMSE.BVR.World.InterceptorInspect".Translate(
            target?.Label ?? "?", Mathf.Max(0, interceptTick - Find.TickManager.TicksGame).ToStringTicksToPeriod());
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref launchTick, "launchTick");
            Scribe_Values.Look(ref targetRevision, "targetRevision");
            Scribe_Values.Look(ref interceptTick, "interceptTick");
            Scribe_Values.Look(ref start, "start");
            Scribe_Values.Look(ref end, "end");
            Scribe_Values.Look(ref targetDestination, "targetDestination", PlanetTile.Invalid);
            Scribe_Values.Look(ref hitChance, "hitChance");
            Scribe_Values.Look(ref payloadCapacity, "payloadCapacity");
            Scribe_Defs.Look(ref warhead, "warhead");
            Scribe_Values.Look(ref usesChannel, "usesChannel", true);
            Scribe_Values.Look(ref extraRolls, "extraRolls", -1);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // A splash/other defense can remove the target before this interceptor's due tick.
                // Never write a dangling reference to a world object omitted from the save.
                WorldObject liveTarget = target != null && !target.Destroyed ? target : null;
                MapParent liveNode = launchNode != null && !launchNode.Destroyed ? launchNode : null;
                Scribe_References.Look(ref liveTarget, "target");
                Scribe_References.Look(ref liveNode, "launchNode");
            }
            else
            {
                Scribe_References.Look(ref target, "target");
                Scribe_References.Look(ref launchNode, "launchNode");
            }
        }
    }
}
