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
        public MissileWorldObject target;
        public MapParent launchNode;
        public PlanetTile targetDestination = PlanetTile.Invalid;
        public float hitChance, payloadCapacity;
        public MissilePartDef warhead;
        public override Vector3 DrawPos => Vector3.Slerp(start, end, Mathf.Clamp01(
            (float)(Find.TickManager.TicksGame - launchTick) / Mathf.Max(1, interceptTick - launchTick)));
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
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // A splash/other defense can remove the target before this interceptor's due tick.
                // Never write a dangling reference to a world object omitted from the save.
                MissileWorldObject liveTarget = target != null && !target.Destroyed ? target : null;
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
