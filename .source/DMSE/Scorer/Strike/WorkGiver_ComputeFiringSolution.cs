using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    [DefOf]
    public static class DMSE_StrikeDefOf
    {
        public static JobDef DMSE_ComputeFiringSolution;
        public static RulePackDef DMSE_NamerStrikeMission;

        static DMSE_StrikeDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_StrikeDefOf));
        }
    }

    /// <summary>
    /// 人工版打擊規劃桌的彈道計算（docs/BVR_波次打擊規劃.md §8-2）：工作類型「研究」，
    /// 掃描本地圖仍有未解導彈的人工版指揮台。右鍵指揮台可強制指派（Scanner 自動提供）。
    /// </summary>
    public class WorkGiver_ComputeFiringSolution : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.InteractionCell;

        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        private static IEnumerable<CompStrikeConsole> Pending(Map map)
        {
            MapComponent_StrikePlanner planner = map?.GetComponent<MapComponent_StrikePlanner>();
            return planner == null ? Enumerable.Empty<CompStrikeConsole>() : planner.consoles.Where(c => c.NeedsManualComputation);
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return Pending(pawn.Map).Select(c => (Thing)c.parent);
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !Pending(pawn.Map).Any();

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompStrikeConsole console = t.TryGetComp<CompStrikeConsole>();
            if (console == null || !console.NeedsManualComputation || t.Faction != pawn.Faction) { return null; }
            if (!console.Active)
            {
                JobFailReason.Is("DMSE.BVR.DeviceInactive".Translate());
                return null;
            }
            if (t.IsForbidden(pawn) || t.IsBurning() || !pawn.CanReserve(t, 1, -1, null, forced)) { return null; }
            return JobMaker.MakeJob(DMSE_StrikeDefOf.DMSE_ComputeFiringSolution, t);
        }
    }

    /// <summary>
    /// 在打擊規劃桌計算彈道（比照原版研究）：每 tick 工作量 = 研究速度，給智識經驗；
    /// 進度存在任務上，中斷後可續算；全部解完即結束。
    /// </summary>
    public class JobDriver_ComputeFiringSolution : JobDriver
    {
        private const TargetIndex ConsoleInd = TargetIndex.A;

        private CompStrikeConsole Console => job.GetTarget(ConsoleInd).Thing?.TryGetComp<CompStrikeConsole>();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(ConsoleInd), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(ConsoleInd);
            this.FailOnBurningImmobile(ConsoleInd);
            yield return Toils_Goto.GotoThing(ConsoleInd, PathEndMode.InteractionCell);

            Toil compute = ToilMaker.MakeToil("ComputeFiringSolution");
            compute.tickIntervalAction = delegate (int delta)
            {
                CompStrikeConsole console = Console;
                if (console == null) { return; }
                Pawn actor = compute.actor;
                console.AddComputationWork(actor.GetStatValue(StatDefOf.ResearchSpeed) * delta);
                actor.skills?.Learn(SkillDefOf.Intellectual, 0.1f * delta);
                actor.GainComfortFromCellIfPossible(delta, chairsOnly: true);
                if (!console.NeedsManualComputation) { ReadyForNextToil(); }
            };
            compute.FailOn(() => Console == null || !Console.NeedsManualComputation || !Console.Active);
            compute.FailOnCannotTouch(ConsoleInd, PathEndMode.InteractionCell);
            compute.WithEffect(EffecterDefOf.Research, ConsoleInd);
            compute.WithProgressBar(ConsoleInd, () => Console?.ComputeProgress ?? 0f);
            compute.defaultCompleteMode = ToilCompleteMode.Delay;
            compute.defaultDuration = 4000;
            compute.activeSkill = () => SkillDefOf.Intellectual;
            yield return compute;
            yield return Toils_General.Wait(2);
        }
    }

    /// <summary>有打擊任務的下一波現在就能整波發射（docs/BVR_波次打擊規劃.md §10-4）。</summary>
    public class Alert_StrikeWaveReady : Alert
    {
        private readonly List<Thing> culprits = new List<Thing>();
        private readonly List<string> lines = new List<string>();

        public Alert_StrikeWaveReady()
        {
            defaultLabel = "DMSE.Strike.Alert.WaveReady".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override AlertReport GetReport()
        {
            culprits.Clear();
            lines.Clear();
            if (DMSEMod.Settings != null && !DMSEMod.Settings.strikeWaveReadyAlert) { return AlertReport.Inactive; }
            foreach (Map map in Find.Maps)
            {
                MapComponent_StrikePlanner planner = map.GetComponent<MapComponent_StrikePlanner>();
                if (planner == null) { continue; }
                foreach (CompStrikeConsole console in planner.consoles)
                {
                    if (console.parent.Faction != Faction.OfPlayer || console.mission == null) { continue; }
                    int next = console.mission.NextWaveIndex;
                    if (next < 0 || console.CannotFireReason(next, partial: false) != null) { continue; }
                    culprits.Add(console.parent);
                    lines.Add("DMSE.Strike.Alert.WaveReadyLine".Translate(console.mission.Label, next + 1, console.mission.TargetLabel));
                }
            }
            return AlertReport.CulpritsAre(culprits);
        }

        public override TaggedString GetExplanation()
        {
            return "DMSE.Strike.Alert.WaveReadyDesc".Translate() + "\n\n" + string.Join("\n", lines);
        }
    }

    /// <summary>有人工版任務在等彈道計算，但該地圖沒有能做研究工作的殖民者。</summary>
    public class Alert_StrikeComputationPending : Alert
    {
        private readonly List<Thing> culprits = new List<Thing>();

        public Alert_StrikeComputationPending()
        {
            defaultLabel = "DMSE.Strike.Alert.ComputationPending".Translate();
            defaultExplanation = "DMSE.Strike.Alert.ComputationPendingDesc".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override AlertReport GetReport()
        {
            culprits.Clear();
            foreach (Map map in Find.Maps)
            {
                MapComponent_StrikePlanner planner = map.GetComponent<MapComponent_StrikePlanner>();
                if (planner == null || planner.consoles.Count == 0) { continue; }
                List<CompStrikeConsole> waiting = planner.consoles
                    .Where(c => c.parent.Faction == Faction.OfPlayer && c.NeedsManualComputation).ToList();
                if (waiting.Count > 0 && !StrikeComputation.Researchers(map).Any()) { culprits.AddRange(waiting.Select(c => (Thing)c.parent)); }
            }
            return AlertReport.CulpritsAre(culprits);
        }
    }
}
