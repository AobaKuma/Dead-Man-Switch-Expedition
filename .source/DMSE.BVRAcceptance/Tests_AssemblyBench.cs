using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// 導彈組裝台（docs/導彈組裝台與裝配方案_實作計畫.md）：設施與資訊卡、小人實際搬料施工、出廠去向、
    /// 取消退料、改裝吃小人速度、方案庫存讀、窗口各狀態繪製。
    /// </summary>
    public static class Tests_AssemblyBench
    {
        private static ThingDef Bench => ThingDef.Named("DMSE_MissileAssemblyBench");
        private static ThingDef ToolCabinet => ThingDef.Named("ToolCabinet");
        private static ThingDef StingerItem => ThingDef.Named("DMSE_Missile_Stinger");
        private static MissileBodyDef StingerBody => DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Stinger");

        private static int CountOnMap(ThingDef def) => Home.listerThings.ThingsOfDef(def).Sum(x => x.stackCount);

        private static void SpawnStack(ThingDef def, int count, IntVec3 near)
        {
            while (count > 0)
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = Mathf.Min(count, def.stackLimit);
                count -= t.stackCount;
                GenPlace.TryPlaceThing(t, near, Home, ThingPlaceMode.Near);
            }
        }

        public static IEnumerable<Wait> BenchFacilitiesAndInfoCard(TestContext t)
        {
            Thing bench = SpawnBuilding(Bench, 2, 2);
            CompMissileAssemblyBench comp = bench.TryGetComp<CompMissileAssemblyBench>();
            t.Require(comp != null && comp.Rack != null, "the bench has its order comp and output rack");
            t.Check(comp.Rack.MaxStored == 2, "the output rack holds two missiles");
            t.Check(MissileItemUtility.ProducibleBodies.Count == 10, $"ten bodies are producible ({MissileItemUtility.ProducibleBodies.Count})");
            yield return new WaitTicks(2);

            StatDef speedStat = StatDefOf.WorkTableWorkSpeedFactor;
            t.Check(speedStat.Worker.ShouldShowFor(StatRequest.For(bench)), "the info card shows the bench's work speed");
            Thing launcher = SpawnBuilding(LightLauncher, 14, 2);
            t.Check(!speedStat.Worker.ShouldShowFor(StatRequest.For(launcher)), "other DMSE buildings still hide it");

            float bare = comp.TableSpeed;
            bool outdoors = StatPart_WorkTableOutdoors.Applies(bench);
            t.Check(outdoors, "the test pad is outdoors");
            t.Check(Mathf.Abs(bare - 0.8f * (StatPart_WorkTableTemperature.Applies(bench) ? 0.7f : 1f)) < 0.01f,
                $"outdoor (and temperature) penalties apply to the bench ({bare:0.###})");
            string explanation = speedStat.Worker.GetExplanationFull(StatRequest.For(bench), speedStat.toStringNumberSense, bare);
            t.Check(explanation.Contains("0.8") || explanation.Contains("80"), "the info-card explanation lists the outdoor factor");

            SpawnBuilding(ToolCabinet, 2, 6);
            SpawnBuilding(ToolCabinet, 4, 6);
            SpawnBuilding(ToolCabinet, 2, 7);
            yield return new WaitTicks(2);
            int linked = bench.TryGetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading.Count;
            float withCabinets = comp.TableSpeed;
            t.Check(linked == 2, $"only two tool cabinets link ({linked})");
            t.Check(Mathf.Abs(withCabinets / bare - 1.12f) < 0.01f, $"two tool cabinets add 12% ({bare:0.###} -> {withCabinets:0.###})");

            Pawn pawn = Home.mapPawns.FreeColonistsSpawned.First();
            t.Check(Mathf.Abs(comp.WorkSpeedFor(pawn) - pawn.GetStatValue(StatDefOf.GeneralLaborSpeed) * withCabinets) < 0.001f,
                "bench work speed = colonist labor speed x bench speed");
            Thing loose = GenSpawn.Spawn(ThingMaker.MakeThing(StingerItem), Pad.CenterCell, Home);
            CompMissileConfig looseCfg = loose.TryGetComp<CompMissileConfig>();
            t.Check(Mathf.Abs(looseCfg.WorkSpeedFor(pawn) - pawn.GetStatValue(StatDefOf.GeneralLaborSpeed)) < 0.001f,
                "a refit goes at the colonist's labor speed");
            looseCfg.WorkDone = 100f;
            looseCfg.pending.SetPart(MissilePartCategory.Warhead, Part("DMSE_Light_Warhead_Incendiary"));
            t.Check(looseCfg.WorkDone == 0f, "changing the refit target discards earlier progress");
            loose.Destroy();
        }

        public static IEnumerable<Wait> BenchProducesWithColonist(TestContext t)
        {
            Thing bench = SpawnBuilding(Bench, 2, 2);
            CompMissileAssemblyBench comp = bench.TryGetComp<CompMissileAssemblyBench>();
            ThingDef item = MissileItemUtility.ItemDefFor(StingerBody);
            t.Require(item == StingerItem, "the stinger body maps to its item");
            RecipeDef recipe = MissileItemUtility.RecipeFor(item);
            t.Require(recipe != null, "the stinger recipe is found");
            foreach (ResearchProjectDef r in new[] { recipe.researchPrerequisite }.Concat(recipe.researchPrerequisites ?? new List<ResearchProjectDef>())
                         .Where(r => r != null && !r.IsFinished))
            {
                Find.ResearchManager.FinishProject(r, false, null, false);
            }
            foreach (MissilePartDef p in DefDatabase<MissilePartDef>.AllDefsListForReading.Where(p => p.CompatibleWith(StingerBody)))
            {
                foreach (ResearchProjectDef r in p.researchPrerequisites ?? new List<ResearchProjectDef>())
                {
                    if (!r.IsFinished) { Find.ResearchManager.FinishProject(r, false, null, false); }
                }
            }
            t.Require(MissileItemUtility.CanProduce(StingerBody, out string why), $"the stinger is producible ({why})");

            MissileConfig cfg = MissileConfig.NewWithDefaults(StingerBody);
            MissilePartDef rod = Part("DMSE_Light_Warhead_ContinuousRod");
            cfg.SetPart(MissilePartCategory.Warhead, rod);
            string name = MissileDesignNamer.Generate(cfg, null, MissileDesignNamer.UsedNames(Home), out string rule);
            t.Check(MissileDesignNamer.IsValidName(name) && !rule.NullOrEmpty(), $"a valid design name is generated ({name} from {rule})");
            string designator = MissileDesignNamer.NamerFor(cfg)?.Designator;
            t.Check(!designator.NullOrEmpty() && name.Contains(designator + "-"),
                $"the body's own namer supplies the designation prefix ({name} / {designator})");
            Dictionary<ThingDef, int> need = MissileAssemblyCost.RequiredFor(cfg);
            foreach (KeyValuePair<ThingDef, int> kv in need) { SpawnStack(kv.Key, kv.Value * 2, bench.Position + new IntVec3(4, 0, 0)); }
            Dictionary<ThingDef, int> before = need.Keys.ToDictionary(d => d, CountOnMap);

            comp.PlaceOrder(cfg, name, false, rule, 1, BenchOutputMode.KeepOnBench);
            t.Require(comp.OrderActive && comp.WantsWork, "the order is active");
            t.Check(MissileAssemblyUtility.GetAssemblyTarget(bench) == comp, "the bench is the assembly target while it has an order");

            Pawn pawn = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
            t.Require(pawn != null, "a colonist can craft");
            var giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("DMSE_AssembleMissile").Worker;
            t.Check(giver.PotentialWorkThingsGlobal(pawn).Contains(bench), "the assembly work giver scans the bench");
            bool sawDeliver = false, sawAssemble = false;
            var produced = new WaitUntil(() =>
            {
                if (!comp.OrderActive) { return true; }
                if (pawn.Drafted) { pawn.drafter.Drafted = false; }
                JobDef cur = pawn.CurJobDef;
                sawDeliver |= cur == DMSE_MissileJobDefOf.DMSE_DeliverMissileResource;
                sawAssemble |= cur == DMSE_MissileJobDefOf.DMSE_AssembleMissile;
                if (cur != DMSE_MissileJobDefOf.DMSE_DeliverMissileResource && cur != DMSE_MissileJobDefOf.DMSE_AssembleMissile
                    && giver.HasJobOnThing(pawn, bench, true))
                {
                    Job job = giver.JobOnThing(pawn, bench, true);
                    if (job != null) { pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                }
                return false;
            }, 60000);
            yield return produced;
            if (produced.TimedOut)
            {
                string still = string.Join(", ", comp.StillNeeded().Select(kv => kv.Key.defName + " x" + kv.Value));
                t.Note($"timeout: pawn job {pawn.CurJobDef?.defName ?? "none"} at {pawn.Position}, drafted {pawn.Drafted}, "
                    + $"still needed [{still}], work {comp.WorkDone:0}/{comp.WorkAmount}, can finish {comp.CanFinishNow}, "
                    + $"has job {giver.HasJobOnThing(pawn, bench, true)} ({JobFailReason.Reason ?? "-"})");
            }
            t.Require(!produced.TimedOut, "the colonist hauls the materials and assembles the missile");
            t.Check(sawDeliver && sawAssemble, "both the delivery and the assembly job ran");

            Thing missile = comp.Rack.HeldThings.FirstOrDefault();
            t.Require(missile != null && missile.def == StingerItem, "the finished missile waits on the bench");
            CompMissileConfig made = missile.TryGetComp<CompMissileConfig>();
            t.Check(!made.NeedsAssembly && made.config.PartFor(MissilePartCategory.Warhead) == rod, "the missile leaves the bench fully assembled");
            t.Check(made.DesignName == name && missile.LabelNoCount.Contains(name), $"the missile carries its design name ({missile.LabelNoCount})");
            foreach (KeyValuePair<ThingDef, int> kv in need)
            {
                t.Check(CountOnMap(kv.Key) == before[kv.Key] - kv.Value, $"{kv.Key.defName}: exactly one missile's worth is used");
            }
            t.Check(missile.TryGetComp<CompDurabilityDecay>()?.IsSheltered == true, "the bench shelters its output from decay");
            t.Check(WorkGiver_UnloadMissileRack.FirstDisallowed(comp.Rack) == null, "kept missiles are not unloaded");

            // 改裝後設計名稱失效
            made.pending.SetPart(MissilePartCategory.Warhead, Part("DMSE_Light_Warhead_HE"));
            made.ApplyAssemblyFree();
            t.Check(made.DesignName == null, "refitting the missile drops its design name");
        }

        public static IEnumerable<Wait> BenchOutputCancelAndDesigns(TestContext t)
        {
            Thing bench = SpawnBuilding(Bench, 2, 2);
            CompMissileAssemblyBench comp = bench.TryGetComp<CompMissileAssemblyBench>();
            MissileConfig cfg = MissileConfig.NewWithDefaults(StingerBody);
            yield return new WaitTicks(2);

            // 取消訂單：已到料全數退回
            comp.PlaceOrder(cfg, "Test SAM", true, "[body]", 3, BenchOutputMode.KeepOnBench);
            ThingDef chem = ThingDefOf.Chemfuel;
            int chemBefore = CountOnMap(chem);
            comp.Deposit(chem, 12);
            comp.CancelOrder();
            t.Check(!comp.OrderActive && comp.Delivered.Count == 0 && comp.draft != null, "cancelling keeps the draft and clears deliveries");
            yield return new WaitTicks(2);
            t.Check(CountOnMap(chem) == chemBefore + 12, "delivered materials come back next to the bench");

            // 送往倉儲：上帝模式即時完成兩枚（容量 2），第三枚留為訂單
            bool oldGod = DebugSettings.godMode;
            try
            {
                DebugSettings.godMode = true;
                LoadedModManager.GetMod<DMSEMod>().GetSettings<PlayerConfigSettings>().godModeInstantAssembly = true;
                comp.PlaceOrder(cfg, "Test SAM", true, "[body]", 3, BenchOutputMode.SendToStorage);
                t.Check(comp.Rack.StoredCount == 2 && comp.OrderActive && comp.remaining == 1,
                    "instant assembly fills the output rack and keeps the rest of the order");
                t.Check(!comp.CanFinishNow && comp.OutputBlocked, "a full output rack blocks finishing");
                Thing first = comp.Rack.HeldThings[0];
                t.Check(comp.IsOutbound(first) && WorkGiver_UnloadMissileRack.FirstDisallowed(comp.Rack) == first,
                    "send-to-storage missiles are offered to the unload work giver");
                comp.CancelOrder();
            }
            finally
            {
                DebugSettings.godMode = oldGod;
            }

            // 方案庫：存、重讀、刪
            string designName = "Acceptance Test Design";
            MissileDesignBlueprint old = MissileDesignBlueprintDatabase.FindByName(designName);
            if (old != null) { MissileDesignBlueprintDatabase.Delete(old); }
            MissileConfig rodCfg = cfg.Clone();
            rodCfg.SetPart(MissilePartCategory.Warhead, Part("DMSE_Light_Warhead_ContinuousRod"));
            t.Require(MissileDesignBlueprintDatabase.Save(MissileDesignBlueprint.From(rodCfg, designName, true, "[body] [warhead]"), out string err),
                $"the design is saved ({err})");
            MissileDesignBlueprintDatabase.Reload();
            MissileDesignBlueprint saved = MissileDesignBlueprintDatabase.FindByName(designName);
            t.Require(saved != null && System.IO.File.Exists(saved.filePath), "the design file is in the config folder");
            MissileConfig back = saved.ToConfig();
            t.Check(back != null && back.body == StingerBody && back.PartFor(MissilePartCategory.Warhead) == rodCfg.PartFor(MissilePartCategory.Warhead),
                "the saved design reads back");
            t.Check(MissileDesignBlueprintDatabase.Delete(saved) && MissileDesignBlueprintDatabase.FindByName(designName) == null,
                "the design can be deleted");

            // 窗口：空狀態、配置中、訂單進行中
            comp.SetDraft(null, null, false, null);
            var empty = new Dialog_MissileAssemblyBench(comp) { forcePause = false, absorbInputAroundWindow = false };
            Find.WindowStack.Add(empty);
            yield return new WaitFrames(10);
            t.Screenshot("bench_empty");
            yield return new WaitFrames(3);
            empty.Close(doCloseSound: false);
            yield return new WaitFrames(2);

            comp.SetDraft(rodCfg, "YJ Test", false, "[body] [warhead]");
            var configuring = new Dialog_MissileAssemblyBench(comp) { forcePause = false, absorbInputAroundWindow = false };
            Find.WindowStack.Add(configuring);
            yield return new WaitFrames(10);
            t.Screenshot("bench_configuring");
            yield return new WaitFrames(3);
            configuring.Close(doCloseSound: false);
            yield return new WaitFrames(2);
            t.Check(comp.draft != null && comp.draft.PartFor(MissilePartCategory.Warhead) == rodCfg.PartFor(MissilePartCategory.Warhead),
                "closing the window keeps the draft on the bench");

            comp.PlaceOrder(rodCfg, "YJ Test", true, "[body] [warhead]", 2, BenchOutputMode.KeepOnBench);
            comp.Deposit(ThingDefOf.Chemfuel, 10);
            var ordered = new Dialog_MissileAssemblyBench(comp) { forcePause = false, absorbInputAroundWindow = false };
            Find.WindowStack.Add(ordered);
            yield return new WaitFrames(10);
            t.Screenshot("bench_ordered");
            yield return new WaitFrames(3);
            ordered.Close(doCloseSound: false);

            MissileDesignBlueprintDatabase.Save(MissileDesignBlueprint.From(rodCfg, designName, true, "[body] [warhead]"), out _);
            var list = new Dialog_MissileDesignList(null) { forcePause = false, absorbInputAroundWindow = false };
            Find.WindowStack.Add(list);
            yield return new WaitFrames(10);
            t.Screenshot("design_list");
            yield return new WaitFrames(3);
            list.Close(doCloseSound: false);
            MissileDesignBlueprint shown = MissileDesignBlueprintDatabase.FindByName(designName);
            if (shown != null) { MissileDesignBlueprintDatabase.Delete(shown); }
            comp.CancelOrder();
            t.Check(comp.CompInspectStringExtra() == null || !comp.CompInspectStringExtra().Contains("Order"), "no order text after cancelling");
        }
    }
}
