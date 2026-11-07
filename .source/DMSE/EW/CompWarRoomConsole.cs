using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    public class CompProperties_WarRoomConsole : CompProperties_Mannable
    {
        public CompProperties_WarRoomConsole() { compClass = typeof(CompWarRoomConsole); }
    }

    /// <summary>
    /// 戰情終端（docs/電子戰建築_實作計畫.md §2）。遙控與交戰限制不需要有人站崗；只有技能加成需要操作員。
    /// 同陣營多座終端的操作員等級疊加（見 <see cref="MapComponent_EWControl.OperatorLevelTotal"/>）。
    /// 操作員直接取 <see cref="CompMannable.ManningPawn"/>（1.7 已公開，不必另外記錄）。
    /// </summary>
    public class CompWarRoomConsole : CompMannable
    {
        private CompPowerTrader power;

        public bool PowerOK => power == null || power.PowerOn;

        /// <summary>目前提供加成的操作員；沒人或終端沒電時為 null，加成立即回 1。</summary>
        public Pawn Operator => parent.Spawned && PowerOK ? ManningPawn : null;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            power = parent.GetComp<CompPowerTrader>();
            MapComponent_EWControl.For(parent.Map)?.consoles.Add(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_EWControl.For(map)?.consoles.Remove(this);
        }

        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn pawn)
        {
            if (parent.Faction != pawn.Faction || !pawn.RaceProps.ToolUser || !pawn.CanReserveAndReach(parent, PathEndMode.InteractionCell, Danger.Deadly)) { yield break; }
            if (!PowerOK)
            {
                yield return new FloatMenuOption("CannotManThing".Translate(parent.LabelShort, parent) + " (" + "NoPower".Translate() + ")", null);
                yield break;
            }
            yield return new FloatMenuOption("OrderManThing".Translate(parent.LabelShort, parent), () =>
            {
                Job job = JobMaker.MakeJob(DMSE_EWJobDefOf.DMSE_OperateWarRoom, parent);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            });
        }

        public override string CompInspectStringExtra()
        {
            Pawn op = Operator;
            string text = op == null
                ? "DMSE.EW.Console.NoOperator".Translate().ToString()
                : "DMSE.EW.Console.Operator".Translate(op.LabelShort).ToString();
            string baseText = base.CompInspectStringExtra();
            return baseText.NullOrEmpty() ? text : baseText + "\n" + text;
        }
    }

    /// <summary>
    /// NPC 守軍沒事做時去操作最近一座本派系、有電且沒人操作的戰情終端。放在職責思考樹的低優先位置
    /// （戰鬥、需求、其他設備之後）；工作定期到期重新評估，敵人出現或有設備空出時就會離開。
    /// </summary>
    public class JobGiver_ManWarRoomConsole : ThinkNode_JobGiver
    {
        public float maxDistFromPoint = 60f;
        public int expiryInterval = 300;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            JobGiver_ManWarRoomConsole copy = (JobGiver_ManWarRoomConsole)base.DeepCopy(resolve);
            copy.maxDistFromPoint = maxDistFromPoint;
            copy.expiryInterval = expiryInterval;
            return copy;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            MapComponent_EWControl control = MapComponent_EWControl.For(pawn.Map);
            if (pawn.Faction == null || control == null || control.consoles.Count == 0) { return null; }
            IntVec3 root = pawn.mindState.duty?.focus.Cell ?? pawn.Position;
            if (!root.IsValid) { root = pawn.Position; }

            Thing best = null;
            float bestDist = float.MaxValue;
            foreach (CompWarRoomConsole console in control.consoles)
            {
                Thing thing = console.parent;
                if (thing.Faction != pawn.Faction || !console.PowerOK) { continue; }
                if (console.MannedNow && console.ManningPawn != pawn) { continue; }
                if (thing.Position.DistanceTo(root) > maxDistFromPoint) { continue; }
                float dist = thing.Position.DistanceToSquared(pawn.Position);
                if (dist >= bestDist || !pawn.CanReserveAndReach(thing, PathEndMode.InteractionCell, Danger.Deadly)) { continue; }
                best = thing;
                bestDist = dist;
            }
            if (best == null) { return null; }
            Job job = JobMaker.MakeJob(DMSE_EWJobDefOf.DMSE_OperateWarRoom, best);
            job.expiryInterval = expiryInterval;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    public class JobDriver_OperateWarRoom : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
            => pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => !(job.GetTarget(TargetIndex.A).Thing.TryGetComp<CompWarRoomConsole>()?.PowerOK ?? false));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil operate = ToilMaker.MakeToil("DMSE_OperateWarRoom");
            operate.tickAction = () =>
            {
                Thing console = operate.actor.CurJob.targetA.Thing;
                console.TryGetComp<CompWarRoomConsole>()?.ManForATick(operate.actor);
                operate.actor.rotationTracker.FaceCell(console.Position);
            };
            operate.handlingFacing = true;
            operate.defaultCompleteMode = ToilCompleteMode.Never;
            yield return operate;
        }
    }

    [DefOf]
    public static class DMSE_EWJobDefOf
    {
        public static JobDef DMSE_OperateWarRoom;
        static DMSE_EWJobDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_EWJobDefOf)); }
    }
}
