using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>War room console, jammer station, long-range radar (docs/電子戰建築_實作計畫.md; ids in the comments).</summary>
    public static class Tests_ElectronicWarfare
    {
        private static ThingDef Console => ThingDef.Named("DMSE_WarRoomConsole");
        private static ThingDef Jammer => ThingDef.Named("DMSE_JammerStation");
        private static ThingDef LongRange => ThingDef.Named("DMSE_OTHWarningRadar");
        private static ThingDef Search => ThingDef.Named("DMSE_SearchRadar");
        private static MapComponent_EWControl Control => MapComponent_EWControl.For(Home);

        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("E1", "EW defs, research chain, jammer power curve", DefsAndResearch),
            new AcceptanceTest("E2", "Long-range radar: resolution, no fire control, warning window, power level (OTH-1..5)", LongRangeRadar),
            new AcceptanceTest("E3", "Power level comp on a radar: full is unchanged, scaled factors, standby (EC-15)", ThrottleOnRadar),
            new AcceptanceTest("E4", "Console: no crew needed, operator skill factors, boundaries (WR-1, WR-2, WR-7)", OperatorBonus),
            new AcceptanceTest("E5", "Console: remote switch, throttle groups, silence and restore (WR-6)", RemoteControl),
            new AcceptanceTest("E6", "ROE on the world layer: hold fire, self-defense, classes, no console (WR-3, WR-4)", RoeWorld),
            new AcceptanceTest("E7", "ROE on the map layer: hold fire and channel cap (WR-3, WR-5)", RoeMap),
            new AcceptanceTest("E8", "Jammer ring: one roll on entry, saved, immune seekers (JM-1..3, JM-9)", JammerRoll),
            new AcceptanceTest("E9", "Jammer outcomes: off-map removal and inertial landing (JM-4..6)", JammerOutcomes),
            new AcceptanceTest("E10", "Jammer ring drawing (JM-7)", JammerRing),
            new AcceptanceTest("E11", "Comms block, map layer untouched, power and stun, ARM target (JM-8, JM-10, JM-11)", JammerCostsAndComms),
            new AcceptanceTest("E12", "Jammer suppresses enemy radars on the world layer (§3.3)", RadarSuppression),
            new AcceptanceTest("E13", "A colonist takes the console by order; the war room tab draws", ConsoleJobAndTab),
        };

        private static void ResetRoeAfterTest()
        {
            MapComponent_EWControl c = Control;
            Override(() => { }, () =>
            {
                c.autoEngage = true;
                c.engagePolicy = EngagePolicy.SelfDefense;
                c.engageNeutral = false;
                c.channelCap = MapComponent_EWControl.ChannelCapUnlimited;
                c.priority = EngagePriority.FirstCome;
                c.holdFireClasses = HoldFireClass.None;
                c.Restore();
            });
        }

        private static bool Near(float a, float b, float tolerance = 0.01f) => Mathf.Abs(a - b) <= tolerance;

        private static BVRTargetProps Props(float distanceLevel, int stealth) => new BVRTargetProps { distanceLevel = distanceLevel, stealthLevel = stealth };

        private static BVRNode PlayerNode() => new BVRNode { parent = Home.Parent, faction = Player, local = HomeBVR };

        private static void ForceRoll(CompJammer jammer, float jamPower, float offMap)
        {
            CompProperties_Jammer p = jammer.Props;
            float oldPower = p.jamPower, oldOffMap = p.offMapChance;
            Override(() => { p.jamPower = jamPower; p.offMapChance = offMap; }, () => { p.jamPower = oldPower; p.offMapChance = oldOffMap; });
        }

        private static WorldObject_IncomingMissile LaunchAtHome(PlanetTile source, MissileConfig config)
        {
            EnsureEnemySafeMark();
            WorldObject_IncomingMissile wo = IncomingMissileUtility.Launch(source, Home.Tile, config, Enemy, CruiseIncoming, null, Home.Parent);
            Track(wo);
            return wo;
        }

        // ------------------------------------------------------------------

        public static IEnumerable<Wait> DefsAndResearch(TestContext t)
        {
            ResearchProjectDef warfare = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_ElectronicWarfare");
            ResearchProjectDef attack = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("DMSE_ElectronicAttack");
            t.Require(warfare != null && attack != null, "both research projects exist");
            t.Check(warfare.baseCost == 3000f && attack.baseCost == 5000f, "research costs 3000 and 5000");
            t.Check(warfare.prerequisites.Any(r => r.defName == "DMSE_RadarNetwork") && attack.prerequisites.Contains(warfare), "research chain radar network → EW → attack");
            t.Check(Console.researchPrerequisites.Contains(warfare) && LongRange.researchPrerequisites.Contains(warfare), "console and long-range radar need electronic warfare");
            t.Check(Jammer.researchPrerequisites.Contains(attack), "jammer needs electronic attack (JM-11)");
            t.Check(LongRange.size == new IntVec2(5, 5) && Console.size == new IntVec2(3, 2) && Jammer.size == new IntVec2(3, 3), "building sizes");
            t.Check(!"DMSE.EW.Tab".Translate().ToString().StartsWith("DMSE."), "keyed strings load");

            Thing jam = SpawnBuilding(Jammer, 2, 2);
            yield return WaitPowered();
            CompPowerThrottle throttle = jam.TryGetComp<CompPowerThrottle>();
            CompPowerTrader power = jam.TryGetComp<CompPowerTrader>();
            float[] expected = { 600f, 1950f, 3300f, 4650f, 6000f };
            for (int s = 0; s <= 4; s++)
            {
                throttle.Step = s;
                t.Check(Near(-power.powerOutputInt, expected[s], 0.5f), $"step {s} draws {expected[s]} W (got {-power.powerOutputInt})");
            }
            throttle.Step = 1;
            yield return new WaitTicks(130);
            t.Check(Near(-power.powerOutputInt, 1950f, 0.5f), "the power level survives over time");
        }

        public static IEnumerable<Wait> LongRangeRadar(TestContext t)
        {
            Thing search = SpawnBuilding(Search, 12, 2);
            int capacityBefore = HomeBVR.Channels.Capacity(Player);
            Thing oth = SpawnBuilding(LongRange, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompSearchRadar radar = oth.TryGetComp<CompSearchRadar>();
            CompSearchRadar searchRadar = search.TryGetComp<CompSearchRadar>();

            // OTH-2
            float loiter = radar.EffectiveWorldRange(Props(1f, 0)), light = radar.EffectiveWorldRange(Props(1.2f, 0));
            float cruise = radar.EffectiveWorldRange(Props(1.5f, 1)), ballistic = radar.EffectiveWorldRange(Props(2f, 2));
            t.Check(Near(loiter, 25f, 0.1f), $"loitering 25.0 (got {loiter:0.00})");
            t.Check(Near(light, 36f, 0.1f), $"light missile 36.0 (got {light:0.00})");
            t.Check(Near(cruise, 22.5f, 0.1f), $"cruise 22.5 (got {cruise:0.00})");
            t.Check(Near(ballistic, 32f, 0.1f), $"ballistic 32.0 (got {ballistic:0.00})");
            t.Check(Near(searchRadar.EffectiveWorldRange(Props(2f, 2)), 9.6f, 0.1f), "search radar sees ballistic at 9.6 (unchanged)");

            // OTH-3
            t.Check(oth.TryGetComp<CompFireControlRadar>() == null, "no fire-control comp");
            t.Check(!HomeBVR.fireControlRadars.Any(f => f.parent == oth), "not registered as fire control");
            t.Check(HomeBVR.Channels.Capacity(Player) == capacityBefore, "channel capacity unchanged");

            // OTH-1: a track both radars would rate differently.
            float cruiseRange = radar.EffectiveWorldRange(BVRTargetProps.Default);
            BVRNode node = PlayerNode();
            WorldObject_IncomingMissile wo = LaunchEnemyMissile(TileAtDistance(19f, 20f));
            float withBoth = node.DetectionRange(wo.TargetProps);
            float searchOnly = searchRadar.EffectiveWorldRange(wo.TargetProps);
            t.Check(withBoth > searchOnly, $"the long-range radar extends node range ({withBoth:0.0} > {searchOnly:0.0})");
            t.Note($"default target range {cruiseRange:0.0}, Moskit {withBoth:0.0}, search radar alone {searchOnly:0.0}");

            // OTH-4
            Thing faller = MissileProjectileMaker.MakeIncoming(ThingDef.Named("DMSE_Incoming_AdvancedBallisticMissile"));
            int windowBoth = HomeBVR.ComputeWarningWindow(faller, 1, Player);
            search.TryGetComp<CompFlickable>().SwitchIsOn = true;
            SetFlick(oth, false);
            yield return new WaitTicks(3);
            int windowSearch = HomeBVR.ComputeWarningWindow(faller, 1, Player);
            SetFlick(oth, true);
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(windowBoth > windowSearch * 2, $"ballistic window much longer with the array ({windowBoth} vs {windowSearch})");

            // Resolution reaches the map layer: a small target gets 1/1.6 of what a large one would per level.
            Thing small = MissileProjectileMaker.MakeIncoming(ThingDef.Named("DMSE_Incoming_Shahed"));
            BVRTargetProps smallProps = small.def.GetModExtension<BVRTargetProps>() ?? BVRTargetProps.Default;
            float resolution = MissileWorldMotion.ResolutionFactor(1.6f, smallProps);
            t.Check(smallProps.distanceLevel >= 1.6f || resolution < 1f, $"small target resolution factor {resolution:0.00}");

            // OTH-5
            CompPowerThrottle throttle = oth.TryGetComp<CompPowerThrottle>();
            throttle.Step = 2;
            t.Check(Near(radar.EffectiveWorldRange(Props(2f, 2)), 32f * 0.7f, 0.1f), "50 % power → range × 0.7");
            t.Check(Near(PowerThrottleUtility.WindowFactor(oth as ThingWithComps), 0.7f), "50 % power → window × 0.7");
            int windowHalf = HomeBVR.ComputeWarningWindow(faller, 1, Player);
            t.Check(windowHalf < windowBoth, $"window shrinks at 50 % ({windowHalf} < {windowBoth})");
            throttle.Step = 0;
            yield return new WaitTicks(2);
            t.Check(!radar.Active && !HomeBVR.searchRadars.Where(r => r.parent == oth).Any(r => r.Active), "standby: radar not active");
            throttle.Step = 4;
        }

        public static IEnumerable<Wait> ThrottleOnRadar(TestContext t)
        {
            Thing radarThing = SpawnBuilding(PhaseArray, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            ThingWithComps radar = (ThingWithComps)radarThing;
            CompFireControlRadar fc = radar.GetComp<CompFireControlRadar>();
            CompSearchRadar sr = radar.GetComp<CompSearchRadar>();
            CompPowerThrottle throttle = radar.GetComp<CompPowerThrottle>();
            t.Require(throttle != null, "phased array has a power level");
            var target = new BVRTarget { stealthLevel = 0, speed = 15f };

            t.Check(throttle.Step == 4, "defaults to full power");
            t.Check(fc.ComputeHitChance(target, 400) == CompFireControlRadar.ComputeHitChance(fc.Props, target, 400), "full power: hit chance identical");
            t.Check(fc.LockOnTicksFor(target) == CompFireControlRadar.LockOnTicksFor(fc.Props, target), "full power: lock time identical");
            t.Check(sr.EffectiveWorldRange(BVRTargetProps.Default) == MissileWorldMotion.DetectionRange(sr.Props.worldDetectionRange, sr.Props.antiStealthLevel, BVRTargetProps.Default), "full power: range identical");
            t.Check(PowerThrottleUtility.SeekerLevel(radar, 2) == 2, "full power: seeker level identical");

            throttle.Step = 1; // 25 %
            t.Check(Near(PowerThrottleUtility.WindowFactor(radar), 0.55f), "25 %: window × 0.55");
            t.Check(Near(PowerThrottleUtility.RangeFactor(radar), 0.55f), "25 %: range × 0.55");
            t.Check(Near(PowerThrottleUtility.HitFactor(radar), 0.7f), "25 %: hit × 0.7");
            t.Check(Near(PowerThrottleUtility.LockFactor(radar), 1.375f), "25 %: lock × 1.375");
            t.Check(PowerThrottleUtility.SeekerLevel(radar, 2) == 1, "25 %: seeker level ceil(2 × 0.25) = 1");
            int cost = Mathf.RoundToInt(CompFireControlRadar.LockOnTicksFor(fc.Props, target) * 1.375f);
            t.Check(fc.LockOnTicksFor(target) == cost, "25 %: instance lock time scales");

            throttle.Step = 0;
            t.Check(!fc.Active && !sr.Active && !fc.ActiveForSeeker, "standby: not active and not seen by seekers");
            t.Check(fc.FreeArtilleryChannels == 0, "standby: no channels");
            throttle.Step = 4;
            t.Check(fc.Active, "back at full power");
        }

        public static IEnumerable<Wait> OperatorBonus(TestContext t)
        {
            ResetRoeAfterTest();
            // Quicktest colonists are random; one with Shooting or Intellectual disabled always reads level 0.
            Pawn pawn = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault(p =>
                !p.skills.GetSkill(SkillDefOf.Shooting).TotallyDisabled && !p.skills.GetSkill(SkillDefOf.Intellectual).TotallyDisabled);
            t.Require(pawn != null, "a colonist who can shoot and research exists");
            ThingWithComps radar = (ThingWithComps)SpawnBuilding(Search, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(12, 2, 1);
            ThingWithComps consoleThing = (ThingWithComps)SpawnBuilding(Console, 20, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompWarRoomConsole console = consoleThing.GetComp<CompWarRoomConsole>();
            CompMissileLauncher_Interceptor sam = launcher.GetComp<CompMissileLauncher_Interceptor>();
            CompSearchRadar sr = radar.GetComp<CompSearchRadar>();
            int baseCooldown = sam.Props.reloadCooldownTicks;
            float baseRange = sr.EffectiveWorldRange(BVRTargetProps.Default);

            // WR-1
            t.Check(!console.MannedNow && EWControlUtility.RadarRangeFactor(radar) == 1f && EWControlUtility.LauncherCooldownFactor(launcher) == 1f, "no operator: both factors 1");
            Control.SetPower(EWDeviceGroup.SearchRadars, false);
            t.Check(!sr.Active, "remote control works with nobody at the console");
            Control.SetPower(EWDeviceGroup.SearchRadars, true);
            yield return WaitPowered(); yield return new WaitTicks(2);

            // WR-2
            SkillRecord intellectual = pawn.skills.GetSkill(SkillDefOf.Intellectual);
            SkillRecord shooting = pawn.skills.GetSkill(SkillDefOf.Shooting);
            int oldI = intellectual.Level, oldS = shooting.Level;
            Override(() => { }, () => { intellectual.Level = oldI; shooting.Level = oldS; });
            intellectual.Level = 12; shooting.Level = 14;
            console.ManForATick(pawn);
            t.Check(console.Operator == pawn, "the manning pawn is the operator");
            t.Check(Near(sr.EffectiveWorldRange(BVRTargetProps.Default), baseRange * 1.08f), "Intellectual 12 → range × 1.08");
            t.Check(Near(EWControlUtility.LauncherCooldownFactor(launcher), 0.8475f, 0.001f), "Shooting 14 → cooldown × 0.85");
            t.Check(sam.CooldownTicksAfterLaunch == Mathf.RoundToInt(baseCooldown / 1.18f), $"launcher cooldown scaled ({sam.CooldownTicksAfterLaunch})");
            intellectual.Level = 0; shooting.Level = 0;
            t.Check(Near(EWControlUtility.RadarRangeFactor(radar), 0.84f), "Intellectual 0 → × 0.84 (floor not hit)");
            t.Check(Near(EWControlUtility.LauncherCooldownFactor(launcher), 1.25f), "Shooting 0 → clamped to × 1.25");
            t.Check(Near(EWControlUtility.ShootingCooldownFactor(20), 1f / 1.36f), "Shooting 20 formula");
            t.Check(Near(EWControlUtility.IntellectualRangeFactor(20), 1.24f), "Intellectual 20 formula");

            // Operators at several consoles add up, capped at 40.
            t.Check(Near(EWControlUtility.IntellectualRangeFactor(40), 1.64f) && Near(EWControlUtility.IntellectualRangeFactor(60), 1.64f), "Intellectual formula tops out at level 40");
            t.Check(Near(EWControlUtility.ShootingCooldownFactor(40), 1f / 1.96f) && Near(EWControlUtility.ShootingCooldownFactor(60), 1f / 1.96f), "Shooting formula tops out at level 40");
            // Temporary operators: earlier tests may have used up the quicktest colonists.
            List<Pawn> crew = new List<Pawn>();
            for (int i = 0; i < 40 && crew.Count < 2; i++)
            {
                Pawn extra = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Player);
                if (extra.skills.GetSkill(SkillDefOf.Intellectual).TotallyDisabled) { extra.Destroy(DestroyMode.Vanish); continue; }
                GenSpawn.Spawn(extra, new IntVec3(Pad.minX + 16 + crew.Count, 0, Pad.minZ + 12), Home);
                crew.Add(extra);
            }
            t.Require(crew.Count == 2, "two temporary operators exist");
            CompWarRoomConsole console2 = ((ThingWithComps)SpawnBuilding(Console, 20, 8)).GetComp<CompWarRoomConsole>();
            CompWarRoomConsole console3 = ((ThingWithComps)SpawnBuilding(Console, 25, 8)).GetComp<CompWarRoomConsole>();
            yield return WaitPowered(); yield return new WaitTicks(2);
            SkillRecord i1 = crew[0].skills.GetSkill(SkillDefOf.Intellectual), i2 = crew[1].skills.GetSkill(SkillDefOf.Intellectual);
            bool d1 = i1.TotallyDisabled, d2 = i2.TotallyDisabled;
            intellectual.Level = 12; i1.Level = 10; i2.Level = 20;
            int expectedTwo = 12 + i1.Level, expectedThree = Mathf.Min(40, expectedTwo + i2.Level);
            console.ManForATick(pawn);
            console2.ManForATick(crew[0]);
            t.Check(Control.OperatorLevelTotal(SkillDefOf.Intellectual, Player) == expectedTwo, $"two operators add up ({Control.OperatorLevelTotal(SkillDefOf.Intellectual, Player)}, expected {expectedTwo})");
            t.Check(Near(EWControlUtility.RadarRangeFactor(radar), EWControlUtility.IntellectualRangeFactor(expectedTwo)), "the summed level drives the radar factor");
            console3.ManForATick(crew[1]);
            intellectual.Level = 20; i1.Level = 20;
            int capped = Control.OperatorLevelTotal(SkillDefOf.Intellectual, Player);
            t.Check(capped == Mathf.Min(40, 20 + i1.Level + i2.Level), $"three operators cap at 40 ({capped}; disabled skills {d1}/{d2})");
            intellectual.Level = 0;
            yield return new WaitTicks(3);
            console2.parent.Destroy(DestroyMode.Vanish);
            console3.parent.Destroy(DestroyMode.Vanish);
            foreach (Pawn extra in crew) { extra.Destroy(DestroyMode.Vanish); }

            // WR-7
            ThingWithComps npcRadar = (ThingWithComps)SpawnBuilding(Search, 26, 2, Enemy);
            t.Check(EWControlUtility.RadarRangeFactor(npcRadar) == 1f, "NPC radar is unaffected");
            yield return new WaitTicks(3);
            t.Check(EWControlUtility.RadarRangeFactor(radar) == 1f, "the bonus ends when nobody mans the console");
            console.ManForATick(pawn);
            consoleThing.Destroy(DestroyMode.Vanish);
            t.Check(EWControlUtility.RadarRangeFactor(radar) == 1f && !Control.RoeActive, "a removed console gives no bonus and no rules");

            // An NPC console buffs its own faction only and never switches on the player's rules.
            ThingWithComps npcConsole = (ThingWithComps)SpawnBuilding(Console, 20, 2, Enemy);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompWarRoomConsole npc = npcConsole.GetComp<CompWarRoomConsole>();
            t.Check(!Control.RoeActive, "an NPC console does not activate the player's rules of engagement");
            t.Check(!new ITab_WarRoomProbe(npcConsole).Visible, "the war room tab stays hidden on an NPC console");
            intellectual.Level = 12;
            npc.ManForATick(pawn);
            if (npc.Operator != null)
            {
                t.Check(Near(EWControlUtility.RadarRangeFactor(npcRadar), 1.08f), "an operator at an NPC console buffs the NPC radar");
                t.Check(EWControlUtility.RadarRangeFactor(radar) == 1f, "an operator at an NPC console does not buff the player's radar");
            }
            else { t.Note("NPC console has no power on the test pad; faction split not exercised here (N10 covers it on a real site)"); }
            npcConsole.Destroy(DestroyMode.Vanish);
        }

        /// <summary>ITab.IsVisible reads the current selection, so select the thing to ask about it.</summary>
        private sealed class ITab_WarRoomProbe
        {
            private readonly Thing thing;
            public ITab_WarRoomProbe(Thing thing) { this.thing = thing; }
            public bool Visible
            {
                get
                {
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
                    bool visible = new ITab_WarRoom().IsVisible;
                    Find.Selector.ClearSelection();
                    return visible;
                }
            }
        }

        public static IEnumerable<Wait> RemoteControl(TestContext t)
        {
            ResetRoeAfterTest();
            ThingWithComps radar = (ThingWithComps)SpawnBuilding(PhaseArray, 2, 2);
            ThingWithComps search = (ThingWithComps)SpawnBuilding(Search, 8, 2);
            ThingWithComps jam = (ThingWithComps)SpawnBuilding(Jammer, 16, 2);
            ThingWithComps enemyRadar = (ThingWithComps)SpawnBuilding(Search, 22, 2, Enemy);
            SpawnBuilding(Console, 28, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompFireControlRadar fc = radar.GetComp<CompFireControlRadar>();
            CompJammer jammer = jam.GetComp<CompJammer>();
            MapComponent_EWControl c = Control;

            c.SetThrottle(EWDeviceGroup.Jammers, 2);
            t.Check(jam.GetComp<CompPowerThrottle>().Step == 2 && radar.GetComp<CompPowerThrottle>().Step == 4, "jammer group only changes jammers");
            c.SetThrottle(EWDeviceGroup.All, 4);
            t.Check(enemyRadar.GetComp<CompPowerThrottle>().Step == 4, "enemy device untouched");
            c.SetThrottle(EWDeviceGroup.All, 1);
            t.Check(enemyRadar.GetComp<CompPowerThrottle>().Step == 4, "enemy device still untouched");
            c.SetThrottle(EWDeviceGroup.All, 4);

            // A device that was already off must stay off after a restore.
            c.SetPower(EWDeviceGroup.SearchRadars, false);
            t.Check(!search.GetComp<CompFlickable>().WantsFlick(), "remote switch keeps the wanted state in sync");
            c.SetPower(EWDeviceGroup.FireControlRadars, true);
            yield return WaitPowered(); yield return new WaitTicks(2);
            bool phasedOn = radar.GetComp<CompFlickable>().SwitchIsOn;

            c.Silence();
            yield return new WaitTicks(2);
            t.Check(c.Silenced, "silence recorded");
            t.Check(!fc.Active && fc.FreeArtilleryChannels == 0, "silence: fire control off, no artillery channels");
            t.Check(!jammer.Active && !c.CommsJammed, "silence: jammer off");
            t.Check(CompBVRDevice.IsOperational(enemyRadar), "silence leaves the enemy radar on");

            c.Restore();
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(!c.Silenced, "restore clears the snapshot");
            t.Check(radar.GetComp<CompFlickable>().SwitchIsOn == phasedOn && fc.Active, "restore turns snapshot devices back on");
            t.Check(jammer.Active, "restore turns the jammer back on");

            // Switching everything back on by hand ends the silence, so the panel stops warning about it.
            c.Silence();
            t.Check(c.Silenced, "silence recorded again");
            c.SetPower(EWDeviceGroup.All, true);
            t.Check(!c.Silenced, "switching the silenced devices on ends the silence");
            yield return WaitPowered(); yield return new WaitTicks(2);

            // Channel limit steps: 0 ... capacity, then unlimited; unlimited counts as the top.
            int unlimited = MapComponent_EWControl.ChannelCapUnlimited;
            t.Check(EWControlUtility.NextChannelCap(unlimited, 4, 1) == unlimited, "+ on unlimited stays unlimited, not 0");
            t.Check(EWControlUtility.NextChannelCap(unlimited, 4, -1) == 4, "- on unlimited drops to the capacity");
            t.Check(EWControlUtility.NextChannelCap(4, 4, 1) == unlimited, "+ at capacity goes to unlimited");
            t.Check(EWControlUtility.NextChannelCap(0, 4, -1) == 0, "- stops at 0");
            t.Check(EWControlUtility.NextChannelCap(0, 0, 1) == unlimited && EWControlUtility.NextChannelCap(unlimited, 0, -1) == 0, "no capacity: only 0 and unlimited");

            // Dead ids are pruned.
            c.Silence();
            jam.Destroy(DestroyMode.Vanish);
            c.Restore();
            yield return WaitPowered();
            t.Check(!c.Silenced && fc.Active, "restore survives a device destroyed during silence");
        }

        public static IEnumerable<Wait> RoeWorld(TestContext t)
        {
            ResetRoeAfterTest();
            SpawnBuilding(PhaseArray, 2, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(12, 2, 4);
            yield return WaitPowered(); yield return new WaitTicks(2);
            PlanetTile source = TileAtDistance(10f, 12f);
            t.Require(source.Valid, "a source tile exists");
            PlanetTile elsewhere = TileAtDistance(13f, 15f);
            BVRNode node = PlayerNode();

            WorldObject_IncomingMissile atUs = LaunchAtHome(source, CruiseConfig());
            EnsureEnemySafeMark();
            WorldObject_IncomingMissile passing = IncomingMissileUtility.Launch(source, elsewhere, CruiseConfig(), Enemy, CruiseIncoming, null, null);
            Track(passing);

            // No console: legacy rules.
            t.Check(node.PolicyAllows(atUs) && node.PolicyAllows(passing), "no console: both hostile tracks may be engaged");

            ThingWithComps consoleThing = (ThingWithComps)SpawnBuilding(Console, 20, 2);
            MapComponent_EWControl c = Control;
            t.Check(c.RoeActive && c.engagePolicy == EngagePolicy.SelfDefense, "a console activates the rules, self-defense by default");

            // WR-4
            t.Check(node.PolicyAllows(atUs), "self-defense engages a track aimed at us");
            t.Check(!node.PolicyAllows(passing), "self-defense ignores a passing track");
            t.Check(node.MayObserve(passing), "a passing track is still observed");
            c.engagePolicy = EngagePolicy.All;
            t.Check(node.PolicyAllows(passing), "engage-all takes the passing track");

            // holdFireClasses
            MissileConfig ballisticConfig = new MissileConfig(DefDatabase<MissileBodyDef>.AllDefs.First(b => b.sizeClass == MissileSizeClass.Ballistic));
            t.Check(EWControlUtility.ClassOf(ballisticConfig, false) == HoldFireClass.Ballistic, "ballistic body classified");
            t.Check(EWControlUtility.ClassOf(null, true) == HoldFireClass.DropPod, "pod classified");
            t.Check(!c.AllowsEngagement(Enemy, Player, HoldFireClass.None) == false, "cruise allowed");
            c.holdFireClasses = HoldFireClass.Ballistic;
            t.Check(!c.AllowsEngagement(Enemy, Player, HoldFireClass.Ballistic), "ballistic held");
            t.Check(c.AllowsEngagement(Enemy, Player, HoldFireClass.Loitering), "others still engaged");
            c.holdFireClasses = HoldFireClass.None;

            // Neutral: a neutral faction is only engaged when allowed.
            Faction neutral = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && !f.HostileTo(Player) && !AirDefenseAlertPolicy.IsFriendly(Player, f));
            if (neutral != null)
            {
                t.Check(!c.AllowsEngagement(neutral, Player, HoldFireClass.None), "neutral held by default");
                c.engageNeutral = true;
                t.Check(c.AllowsEngagement(neutral, Player, HoldFireClass.None), "neutral engaged when allowed");
                c.engageNeutral = false;
            }
            else { t.Note("no neutral faction in this world; neutral checks skipped"); }

            // Priority is the player's version of threat assessment.
            t.Check(!node.ThreatAssessment, "first come by default");
            c.priority = EngagePriority.LargestFirst;
            t.Check(node.ThreatAssessment, "largest-first turns on threat ordering");
            c.priority = EngagePriority.FirstCome;

            // WR-3: the running network holds fire but keeps detecting.
            c.engagePolicy = EngagePolicy.SelfDefense;
            c.autoEngage = false;
            yield return new WaitTicks(150);
            if (!atUs.Destroyed)
            {
                t.Check(atUs.State.detectedByFaction == Player, "hold fire: the track is still detected");
                t.Check(atUs.State.lockUntil < 0 && atUs.State.interceptor == null, "hold fire: no lock and no interceptor");
            }
            c.autoEngage = true;
            yield return new WaitUntil(() => atUs.Destroyed || atUs.State.lockUntil >= 0 || atUs.State.interceptor != null, 600);
            t.Check(atUs.Destroyed || atUs.State.lockUntil >= 0 || atUs.State.interceptor != null, "engagement resumes when allowed again");
            foreach (WorldObject_IncomingMissile wo in new[] { atUs, passing }) { if (!wo.Destroyed) { wo.Destroy(); } }

            // Removing the console falls back to legacy rules.
            consoleThing.Destroy(DestroyMode.Vanish);
            t.Check(!c.RoeActive, "no console, no rules");
        }

        public static IEnumerable<Wait> RoeMap(TestContext t)
        {
            ResetRoeAfterTest();
            SpawnBuilding(PhaseArray, 2, 2);
            SpawnLoadedLauncher(12, 2, 6);
            SpawnBuilding(Console, 20, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            MapComponent_EWControl c = Control;
            MapComponent_BVRCombat bvr = HomeBVR;
            int capacity = bvr.Channels.Capacity(Player);
            t.Require(capacity >= 2, $"at least two channels ({capacity})");

            // WR-3
            c.autoEngage = false;
            t.Require(bvr.RegisterSalvo(CruiseIncoming, 3, CruiseConfig(), Enemy, Player), "salvo registered");
            yield return new WaitTicks(30);
            t.Check(bvr.CountEngaged(Player) == 0, "hold fire: no map locks");
            t.Check(bvr.Waves.Count > 0, "hold fire: the wave is still tracked");

            // WR-5
            c.autoEngage = true;
            c.channelCap = 1;
            int peak = 0;
            yield return new WaitUntil(() =>
            {
                peak = Mathf.Max(peak, bvr.CountEngaged(Player));
                return peak > 1;
            }, 90);
            t.Check(peak == 1, $"channel cap 1: at most one channel is used (peak {peak})");
            BVRNode node = PlayerNode();
            t.Check(node.FreeChannels <= 0, "the world layer sees the cap too");

            c.channelCap = MapComponent_EWControl.ChannelCapUnlimited;
            peak = 0;
            yield return new WaitUntil(() =>
            {
                peak = Mathf.Max(peak, bvr.CountEngaged(Player));
                return peak > 1;
            }, 120);
            t.Check(peak > 1, $"no cap: more channels are used (peak {peak})");

            // holdFire on the map layer: a pod has no missile inside.
            c.holdFireClasses = HoldFireClass.DropPod;
            t.Check(!c.AllowsEngagement(null, Player, EWControlUtility.ClassOf(null, true)), "held pods are not engaged");
            c.holdFireClasses = HoldFireClass.None;
            t.Check(c.AllowsEngagement(null, Player, EWControlUtility.ClassOf(null, true)), "an unknown-source pod over our map is engaged");
        }

        public static IEnumerable<Wait> JammerRoll(TestContext t)
        {
            ResetRoeAfterTest();
            ThingWithComps jam = (ThingWithComps)SpawnBuilding(Jammer, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompJammer jammer = jam.GetComp<CompJammer>();
            CompPowerThrottle throttle = jam.GetComp<CompPowerThrottle>();

            // JM-9
            MissileConfig laser = CruiseConfig("DMSE_Guidance_Laser");
            for (int s = 1; s <= 4; s++)
            {
                throttle.Step = s;
                t.Check(Near(JammerUtility.BreakChance(jammer, laser), 0.15f * s), $"step {s}: break chance {0.15f * s:0.00}");
            }
            throttle.Step = 0;
            t.Check(JammerUtility.BreakChance(jammer, laser) == 0f && !Control.CommsJammed, "step 0: no effect and no comms block");
            throttle.Step = 4;
            yield return WaitPowered(); yield return new WaitTicks(2);

            // Force the outcome: always denied, never off-map.
            ForceRoll(jammer, 1f, 0f);

            // JM-1: outside the ring nothing happens, the first scan inside rolls.
            PlanetTile far = TileAtDistance(10f, 11f);
            t.Require(far.Valid, "a tile outside the ring exists");
            WorldObject_IncomingMissile wo = LaunchAtHome(far, laser);
            yield return new WaitTicks(16);
            t.Check(!wo.State.jamRolled, "outside the ring: no roll");
            float rolledAt = -1f;
            yield return new WaitUntil(() =>
            {
                if (wo.Destroyed) { return true; }
                if (wo.State.jamRolled) { rolledAt = MissileWorldUtility.SurfaceTilesBetween(MissileWorldUtility.TileCenter(Home.Tile), wo.PositionAt(0)); return true; }
                return false;
            }, 3000);
            t.Require(rolledAt > 0f, "the track was rolled on entering the ring");
            t.Check(rolledAt <= 8f && rolledAt >= 7f, $"rolled right at the ring edge ({rolledAt:0.00} tiles)");
            t.Check(wo.State.jamDenied, "guidance denied (forced)");

            // JM-2: only once, and saved.
            wo.State.jamDenied = false;
            yield return new WaitTicks(15 * 5);
            t.Check(!wo.Destroyed && !wo.State.jamDenied, "no second roll while inside the ring");
            wo.State.jamDenied = true;
            string path = Path.Combine(Path.GetTempPath(), "dmse-ew-" + System.Guid.NewGuid() + ".xml");
            BVRTrackState saved = new BVRTrackState { jamRolled = true, jamDenied = true }, loaded = null;
            Scribe.saver.InitSaving(path, "ew");
            Scribe_Deep.Look(ref saved, "state");
            Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path);
            Scribe_Deep.Look(ref loaded, "state");
            Scribe.loader.FinalizeLoading();
            File.Delete(path);
            t.Check(loaded != null && loaded.jamRolled && loaded.jamDenied, "roll results are saved");
            wo.Destroy();

            // JM-3: immune seekers fly through untouched.
            PlanetTile near = TileAtDistance(5f, 6f);
            WorldObject_IncomingMissile arm = LaunchAtHome(near, CruiseConfig("DMSE_Light_Guidance_AntiRadiation"));
            MissileConfig inertial = new MissileConfig(CruiseBody);
            WorldObject_IncomingMissile dumb = LaunchAtHome(near, inertial);
            yield return new WaitTicks(40);
            t.Check(arm.Config.TargetingGuidance is GuidanceType_AntiRadiation, "this config really is anti-radiation");
            t.Check(!arm.Destroyed && !arm.State.jamRolled && !arm.State.jamDenied, "anti-radiation missile is immune");
            t.Check(!dumb.Destroyed && !dumb.State.jamDenied, "inertial / no-seeker missile is immune");
            arm.Destroy(); dumb.Destroy();

            // JM-1: own missiles are never affected.
            WorldObject_IncomingMissile own = IncomingMissileUtility.Launch(near, Home.Tile, laser, Player, CruiseIncoming, null, Home.Parent);
            Track(own);
            yield return new WaitTicks(20);
            t.Check(!own.State.jamRolled, "the jammer's own faction is never affected");
            own.Destroy();
        }

        public static IEnumerable<Wait> JammerOutcomes(TestContext t)
        {
            ResetRoeAfterTest();
            ThingWithComps jam = (ThingWithComps)SpawnBuilding(Jammer, 2, 2);
            Thing phased = SpawnBuilding(PhaseArray, 8, 2);
            Building_MissileRack launcher = SpawnLoadedLauncher(16, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompJammer jammer = jam.GetComp<CompJammer>();
            MissileConfig em = CruiseConfig("DMSE_Guidance_EMSignal");
            PlanetTile near = TileAtDistance(4f, 5f);

            // JM-5
            ForceRoll(jammer, 0f, 0f); // hold rolls until the interceptor is in the air.
            WorldObject_IncomingMissile wo = LaunchAtHome(near, em);
            WorldObject_Interceptor interceptor = (WorldObject_Interceptor)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("DMSE_Interceptor"));
            interceptor.Tile = Home.Tile;
            interceptor.SetFaction(Player);
            interceptor.launchNode = Home.Parent;
            interceptor.launchTick = Find.TickManager.TicksGame;
            interceptor.interceptTick = Find.TickManager.TicksGame + 100000;
            interceptor.start = MissileWorldUtility.TileCenter(Home.Tile);
            interceptor.end = interceptor.start;
            interceptor.target = wo;
            Find.WorldObjects.Add(interceptor);
            wo.State.interceptor = interceptor;
            int maps = Find.Maps.Count;
            int flashes = WorldFlashes.Count;
            jammer.Props.jamPower = 1f; jammer.Props.offMapChance = 1f;
            yield return new WaitUntil(() => wo.Destroyed, 60);
            t.Check(wo.Destroyed, "off-map roll removes the track from the world");
            t.Check(interceptor.Destroyed, "the interceptor in flight is cleared");
            t.Check(Find.Maps.Count == maps, "no map was generated");
            t.Check(WorldFlashes.Count == flashes, "no flash: it did not land anywhere");
            t.Check(HomeBVR.Waves.Count == 0, "nothing was handed to the map BVR");

            // JM-4 / JM-6: both landing paths share the same branch.
            jammer.Props.jamPower = 0f;
            WorldObject_IncomingMissile incoming = LaunchAtHome(near, em);
            ScorerProjectile_WorldObject outgoing = LaunchPlayerMissile(Home.Tile, Home.Parent, CruiseConfig("DMSE_Guidance_EMSignal"));
            MethodInfo arrival = typeof(MissileWorldObject).GetMethod("ArrivalCellFor", BindingFlags.Instance | BindingFlags.NonPublic);
            t.Require(arrival != null, "ArrivalCellFor exists");
            IntVec3 Land(MissileWorldObject m, out bool loiter)
            {
                object[] args = { Home, false };
                IntVec3 cell = (IntVec3)arrival.Invoke(m, args);
                loiter = (bool)args[1];
                return cell;
            }
            GuidanceType.ClearClaims();
            IntVec3 seeker = Land(incoming, out _);
            t.Check(seeker == phased.Position, $"intact seeker locks the biggest emitter ({seeker})");
            GuidanceType.ClearClaims();
            incoming.State.jamDenied = true;
            outgoing.State.jamDenied = true;
            int lockedIn = 0, lockedOut = 0; bool anyLoiter = false;
            for (int i = 0; i < 30; i++)
            {
                if (Land(incoming, out bool l1) == phased.Position) { lockedIn++; }
                if (Land(outgoing, out bool l2) == phased.Position) { lockedOut++; }
                anyLoiter |= l1 || l2;
            }
            t.Check(lockedIn < 5 && lockedOut < 5, $"denied missiles land by inertial spread on both paths ({lockedIn}, {lockedOut} of 30 on the emitter)");
            t.Check(!anyLoiter, "denied missiles do not loiter");
            outgoing.Destroy();

            // A denied missile still lands on the real path without errors.
            yield return new WaitUntil(() => incoming.Destroyed, 2000);
            t.Check(incoming.Destroyed, "denied track reached the map");
        }

        public static IEnumerable<Wait> JammerRing(TestContext t)
        {
            ResetRoeAfterTest();
            ThingWithComps jam = (ThingWithComps)SpawnBuilding(Jammer, 2, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(JammerUtility.ActiveRings().Any(r => r.parent == Home.Parent && Near(r.range, 8f)), "an 8-tile ring is reported for this colony");

            bool oldShow = ShowAllCoverage;
            Override(() => ShowAllCoverage = false, () => ShowAllCoverage = oldShow);
            bool HasRing() => CoverageRings != null && CoverageRings.Keys.Cast<object>().Any(k => k.ToString().Contains("Jammer"));

            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            CameraJumper.TryShowWorld();
            CameraJumper.TryJump(new GlobalTargetInfo(Home.Parent));
            Find.WorldSelector.ClearSelection();
            yield return new WaitFrames(30);
            t.Check(!HasRing(), "coverage display off and nothing selected: no ring");
            Find.WorldSelector.Select(Home.Parent);
            yield return new WaitFrames(180);
            t.Check(HasRing(), "selecting the colony draws the jammer ring");
            t.Screenshot("jammer_ring_selected");
            yield return new WaitFrames(3);
            Find.WorldSelector.ClearSelection();
            ShowAllCoverage = true;
            yield return new WaitFrames(30);
            t.Check(HasRing(), "show-all coverage draws the jammer ring");
            ShowAllCoverage = false;
            jam.GetComp<CompPowerThrottle>().Step = 0;
            Find.WorldSelector.Select(Home.Parent);
            yield return new WaitFrames(30);
            t.Check(!HasRing(), "standby: no ring");
            Find.WorldSelector.ClearSelection();
            CameraJumper.TryHideWorld();
            t.allowPause = false;
        }

        public static IEnumerable<Wait> JammerCostsAndComms(TestContext t)
        {
            ResetRoeAfterTest();
            Thing comms = SpawnBuilding(ThingDef.Named("CommsConsole"), 2, 8);
            ThingWithComps jam = (ThingWithComps)SpawnBuilding(Jammer, 2, 2);
            ThingWithComps jam2 = (ThingWithComps)SpawnBuilding(Jammer, 8, 2);
            ThingWithComps phased = (ThingWithComps)SpawnBuilding(PhaseArray, 14, 2);
            yield return WaitPowered(); yield return new WaitTicks(2);
            Building_CommsConsole console = (Building_CommsConsole)comms;
            CompJammer jammer = jam.GetComp<CompJammer>();
            CompPowerThrottle throttle = jam.GetComp<CompPowerThrottle>();
            CompPowerThrottle throttle2 = jam2.GetComp<CompPowerThrottle>();

            // JM-8
            t.Check(!console.CanUseCommsNow, "a transmitting jammer blocks comms");
            t.Check(new Alert_CommsJammed().GetReport().active, "alert is up");
            t.Check(jammer.CompInspectStringExtra().Contains("DMSE.EW.Jammer.CommsBlocked".Translate().ToString()), "the jammer's inspect string states the cost");
            Pawn pawn = Home.mapPawns.FreeColonistsSpawned.First();
            t.Check(console.GetFloatMenuOptions(pawn).Any(o => o.Disabled), "the comms menu shows a disabled reason, not an error");
            throttle.Step = 0;
            t.Check(!console.CanUseCommsNow, "any other jammer on the map keeps it blocked");
            throttle2.Step = 0;
            t.Check(console.CanUseCommsNow, "all on standby: comms restored");
            throttle.Step = 4;
            t.Check(!console.CanUseCommsNow, "back on: blocked");
            SetFlick(jam, false);
            yield return new WaitTicks(2);
            t.Check(console.CanUseCommsNow, "switched off: comms restored");
            t.Check(jammer.CurrentJamPower == 0f && !jammer.Active, "switched off: no jamming (JM-11)");
            SetFlick(jam, true);
            yield return WaitPowered(); yield return new WaitTicks(2);
            t.Check(!console.CanUseCommsNow, "back on again: blocked");
            jam.TakeDamage(new DamageInfo(DamageDefOf.EMP, 20f));
            yield return new WaitTicks(2);
            t.Check(!jammer.Active && console.CanUseCommsNow, "EMP stun: jamming and comms block both end");

            // JM-10
            MapComponent_BVRCombat bvr = HomeBVR;
            int free = bvr.Channels.Free(Player);
            CompFireControlRadar fc = phased.GetComp<CompFireControlRadar>();
            var target = new BVRTarget { stealthLevel = 0, speed = 15f };
            float hit = fc.ComputeHitChance(target, 400);
            jam.GetComp<CompJammer>();
            t.Check(bvr.Channels.Free(Player) == free, "jammers take no fire-control channel");
            t.Check(hit == CompFireControlRadar.ComputeHitChance(fc.Props, target, 400), "ground jammers do not change map hit chance");

            // ARM: without radars the jammer is the target.
            SetFlick(phased, false);
            throttle2.Step = 4;
            yield return new WaitTicks(3);
            IntVec3 arm = GuidanceType_AntiRadiation.BestRadarCell(Home, Enemy);
            t.Check(arm == jam2.Position, $"anti-radiation picks the transmitting jammer ({arm} vs {jam2.Position})");
            throttle2.Step = 1;
            t.Check(jam2.GetComp<CompJammer>().SeekerLevel == 1, "low power lowers the seeker level");
            throttle2.Step = 0;
            t.Check(GuidanceType_AntiRadiation.BestRadarCell(Home, Enemy) != jam2.Position, "standby is invisible to anti-radiation");
        }

        public static IEnumerable<Wait> ConsoleJobAndTab(TestContext t)
        {
            ResetRoeAfterTest();
            ThingWithComps consoleThing = (ThingWithComps)SpawnBuilding(Console, 4, 6);
            SpawnBuilding(Jammer, 12, 6);
            SpawnBuilding(LongRange, 18, 6);
            yield return WaitPowered(); yield return new WaitTicks(2);
            CompWarRoomConsole console = consoleThing.GetComp<CompWarRoomConsole>();
            Pawn pawn = Home.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.Drafted);
            t.Require(pawn != null, "a colonist exists");

            IntVec3 seat = consoleThing.InteractionCell;
            t.Note($"seat {seat}, standable {seat.Standable(Home)}, things there: {string.Join(", ", seat.GetThingList(Home).Select(x => x.def.defName))}");
            foreach (Pawn c in Home.mapPawns.FreeColonistsSpawned)
            {
                t.Note($"{c.LabelShort} at {c.Position}: job {c.CurJobDef?.defName}, reach {c.CanReach(consoleThing, Verse.AI.PathEndMode.InteractionCell, Danger.Deadly)}, reserve {c.CanReserve(consoleThing)}, roofed-in region {c.GetRoom()?.CellCount}");
            }
            FloatMenuOption order = console.CompFloatMenuOptions(pawn).FirstOrDefault(o => !o.Disabled);
            t.Require(order != null, "the console offers an operate order");
            order.action();
            yield return new WaitUntil(() => console.MannedNow, 2500);
            t.Check(console.MannedNow && console.Operator == pawn, "the ordered colonist reaches the console and mans it");
            t.Check(pawn.CurJobDef == DMSE_EWJobDefOf.DMSE_OperateWarRoom, "the job is the war room job");
            int level = pawn.skills.GetSkill(SkillDefOf.Intellectual).Level;
            t.Check(Near(EWControlUtility.RadarRangeFactor((ThingWithComps)Find.CurrentMap.listerThings.ThingsOfDef(LongRange).First()),
                EWControlUtility.IntellectualRangeFactor(level)), "the job itself drives the operator bonus");

            int errors = LogErrorCount();
            Find.Selector.ClearSelection();
            Find.Selector.Select(consoleThing);
            Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);
            ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType = typeof(ITab_WarRoom);
            yield return new WaitFrames(30);
            t.Screenshot("war_room_tab");
            yield return new WaitFrames(3);
            t.Check(LogErrorCount() == errors, "drawing the tab logs no errors");

            // The other panel states: silence, holding interceptors, and read-only without power.
            MapComponent_EWControl control = Control;
            control.Silence();
            yield return new WaitFrames(30);
            t.Screenshot("war_room_tab_silenced");
            yield return new WaitFrames(3);
            control.Restore();
            control.autoEngage = false;
            control.engagePolicy = EngagePolicy.All;
            control.priority = EngagePriority.LargestFirst;
            control.channelCap = 0;
            control.holdFireClasses = HoldFireClass.DropPod;
            yield return new WaitFrames(30);
            t.Screenshot("war_room_tab_hold");
            yield return new WaitFrames(3);
            control.autoEngage = true;
            control.channelCap = MapComponent_EWControl.ChannelCapUnlimited;
            CompFlickable consoleSwitch = consoleThing.GetComp<CompFlickable>();
            consoleSwitch.DoFlick();
            yield return new WaitTicks(2);
            t.Check(!console.PowerOK, "console switched off for the read-only state");
            yield return new WaitFrames(30);
            t.Screenshot("war_room_tab_no_power");
            yield return new WaitFrames(3);
            consoleSwitch.DoFlick();
            yield return new WaitFrames(3);
            t.Check(LogErrorCount() == errors, "the other panel states log no errors");

            Find.MainTabsRoot.EscapeCurrentTab(false);
            Find.Selector.ClearSelection();
            pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
        }

        private static int LogErrorCount() => Verse.Log.Messages.Count(m => m.type == LogMessageType.Error);

        public static IEnumerable<Wait> RadarSuppression(TestContext t)
        {
            ResetRoeAfterTest();
            // Formula table from the ECM doc.
            t.Check(Near(EWEnvironment.JamEff(0.3f, 2), 1f), "jamSum 0.3, level 2 → jamEff 1");
            t.Check(Near(EWEnvironment.DetectionFactor(1f), 0.4f), "jamEff 1 → detection × 0.4");
            t.Check(Near(EWEnvironment.LockFactor(1f), 1.8f), "jamEff 1 → lock × 1.8");
            t.Check(EWEnvironment.DetectionFactor(0f) == 1f && EWEnvironment.LockFactor(0f) == 1f, "no jam: both factors exactly 1");

            SpawnBuilding(PhaseArray, 2, 2);
            ThingWithComps enemyJam = (ThingWithComps)SpawnBuilding(Jammer, 8, 2, Enemy);
            yield return WaitPowered(); yield return new WaitTicks(16);
            BVRNode node = WorldComponent_BVRNetwork.Current.NodeOf(Home.Parent, Player);
            t.Require(node != null, "player node exists");
            t.Check(Near(node.jamEff, 1f), $"an enemy jammer on our tile suppresses our node (jamEff {node.jamEff:0.00})");
            float raw = PlayerNode().DetectionRange(BVRTargetProps.Default);
            t.Check(Near(node.DetectionRange(BVRTargetProps.Default), raw * 0.4f, 0.05f), "node detection range × 0.4");
            t.Check(Control.CommsJammed, "an enemy jammer on the map blocks comms too (no faction exception)");

            SetFlick(enemyJam, false);
            yield return new WaitTicks(16);
            node = WorldComponent_BVRNetwork.Current.NodeOf(Home.Parent, Player);
            t.Check(node.jamEff == 0f, "jammer off: suppression gone");
        }
    }
}
