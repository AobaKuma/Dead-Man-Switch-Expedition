using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>Faction doctrines loaded from the shipped XML, and the world target worker with real factions.</summary>
    public static class Tests_MissileDoctrine
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("AG1", "Shipped doctrines reproduce the former tech-level settlement defense and threat assessment", ShippedDefaults),
            new AcceptanceTest("AG2", "A doctrine's hostile worker picks enemy NPC maps; the default picks only player maps", TargetWorkers),
        };

        public static IEnumerable<Wait> ShippedDefaults(TestContext t)
        {
            MissileDoctrineDef Doctrine(TechLevel tech) => MissileDoctrine.For(new FactionDef { techLevel = tech }, DefDatabase<MissileDoctrineDef>.AllDefsListForReading);
            t.Check(Doctrine(TechLevel.Neolithic) == null && Doctrine(TechLevel.Medieval) == null, "below industrial there is no doctrine, so no synthetic air defense");
            MissileDoctrineDef industrial = Doctrine(TechLevel.Industrial), spacer = Doctrine(TechLevel.Spacer), ultra = Doctrine(TechLevel.Ultra), archotech = Doctrine(TechLevel.Archotech);
            t.Require(industrial != null && spacer != null && ultra != null, "industrial, spacer and ultra have defaults");
            t.Check(archotech == ultra, "archotech uses the ultra doctrine");
            foreach (MissileDoctrineDef d in DefDatabase<MissileDoctrineDef>.AllDefsListForReading)
            {
                t.Check(!d.ConfigErrors().Any(), $"{d.defName} has no config errors");
            }

            // The values that used to be hard-coded in SettlementDefense.
            SettlementAirDefenseProps i = industrial.settlementDefense, s = spacer.settlementDefense, u = ultra.settlementDefense;
            t.Check(i.detectionRange == 6f && i.antiStealthLevel == 1 && i.channels == 1 && i.maxStock == 2 && i.restockPerDay == 0.5f && i.cooldownTicks == 900,
                "industrial: range 6, anti-stealth 1, one channel, 2 stock, 0.5 a day, 900-tick cooldown");
            t.Check(s.detectionRange == 8f && s.antiStealthLevel == 2 && s.channels == 2 && s.maxStock == 4 && s.restockPerDay == 1.5f && s.cooldownTicks == 600,
                "spacer: range 8, anti-stealth 2, two channels, 4 stock, 1.5 a day, 600-tick cooldown");
            t.Check(u.maxStock == 6 && u.channels == 2 && u.cooldownTicks == 600, "ultra: 6 stock, otherwise spacer");
            t.Check(i.FireControl == EarlyFireControl.GetCompProperties<CompProperties_FireControlRadar>()
                && s.FireControl == PhaseArray.GetCompProperties<CompProperties_FireControlRadar>(), "industrial uses the early radar, spacer the phased array");
            t.Check(new[] { i, s, u }.All(x => x.Interceptor?.body?.defName == "DMSE_MissileBody_Light"
                && x.Interceptor.PartFor(MissilePartCategory.Warhead)?.defName == "DMSE_Light_Warhead_ContinuousRod"), "every level fires the light continuous-rod interceptor");
            t.Check(!i.threatAssessment && !s.threatAssessment && !u.threatAssessment, "synthetic defense never ranked by threat");
            t.Check(!industrial.threatAssessment && spacer.threatAssessment && ultra.threatAssessment, "map nodes rank by threat from spacer up");
            t.Check(new[] { industrial, spacer, ultra }.All(d => d.TargetWorker is StrikeTargetWorker_Players), "shipped doctrines only strike player maps");

            // A real settlement still gets its synthetic defense through the doctrine.
            Settlement settlement = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction != null && !x.Faction.IsPlayer && !x.HasMap
                && x.Faction.def.techLevel >= TechLevel.Industrial && x.GetComponent<WorldObjectComp_AirDefense>() == null);
            if (settlement != null)
            {
                IBVRDefense synthetic = WorldComponent_SettlementDefense.Current.DefenseFor(settlement);
                SettlementAirDefenseProps expected = MissileDoctrine.For(settlement.Faction).settlementDefense;
                t.Check(synthetic != null && synthetic.Channels == expected.channels && synthetic.FireControl == expected.FireControl && synthetic.Config == expected.Interceptor,
                    $"{settlement.Label} ({settlement.Faction.def.techLevel}) defends with its doctrine");
            }
            else { t.Note("no unvisited industrial-or-higher NPC settlement on this world"); }
            yield break;
        }

        public static IEnumerable<Wait> TargetWorkers(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            Faction rival = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => !f.IsPlayer && f != site.Faction && !f.def.hidden && f.def.humanlikeFaction);
            t.Require(rival != null, "another NPC faction exists");
            FactionRelationKind oldKind = site.Faction.RelationKindWith(rival);
            Override(() => site.Faction.SetRelationDirect(rival, FactionRelationKind.Hostile, false), () => site.Faction.SetRelationDirect(rival, oldKind, false));

            Site enemyCamp = SpawnMissileBase(10f, 18f);
            t.Require(enemyCamp != null && enemyCamp != site, "a second site exists");
            enemyCamp.SetFaction(rival);
            Map campMap = SpawnSiteMap(enemyCamp);
            t.Require(campMap != null, "the rival site has a map");
            yield return new WaitTicks(5);

            MissileConfig config = site.GetComponent<WorldObjectComp_MissileBase>().BuildFireConfig();
            List<MapParent> Collect(StrikeTargetWorker worker)
            {
                var list = new List<MapParent>();
                worker.CollectTargets(new StrikeTargetQuery { Source = site, Attacker = site.Faction, Loadout = config }, list);
                return list;
            }
            List<MapParent> players = Collect(new StrikeTargetWorker_Players());
            t.Check(players.Contains(Home.Parent) && !players.Contains(enemyCamp), "the default worker picks the player's map only");
            List<MapParent> hostile = Collect(new StrikeTargetWorker_Hostile());
            t.Check(hostile.Contains(enemyCamp) && hostile.Contains(Home.Parent), "the hostile worker adds the enemy NPC map");
            List<MapParent> npcOnly = Collect(new StrikeTargetWorker_Hostile { includePlayer = false });
            t.Check(npcOnly.Contains(enemyCamp) && !npcOnly.Contains(Home.Parent), "a worker can leave the player out");
            List<MapParent> unseen = Collect(new StrikeTargetWorker_Hostile { includePlayer = false, requireMap = true });
            t.Check(unseen.All(p => p.HasMap), "requireMap keeps abstract sites out");

            // Through the doctrine, as the base itself asks: the full query also drops out-of-range and own sites.
            FactionDef def = site.Faction.def;
            var ext = new MissileDoctrineExtension { doctrine = new MissileDoctrineDef { defName = "DMSE_Test_HostileDoctrine", targetWorker = new StrikeTargetWorker_Hostile { includePlayer = false } } };
            def.modExtensions ??= new List<DefModExtension>();
            def.modExtensions.Insert(0, ext);
            try
            {
                List<MapParent> viaDoctrine = MissileDoctrine.Targets(site, site.Faction, config, mp => true);
                t.Check(viaDoctrine.Contains(enemyCamp) && !viaDoctrine.Contains(Home.Parent) && !viaDoctrine.Contains(site), "the faction's doctrine decides, without any patch");
                t.Check(MissileDoctrine.Targets(site, site.Faction, config, mp => false).Count == 0, "the range check still applies");
            }
            finally { def.modExtensions.Remove(ext); }
            t.Check(MissileDoctrine.Targets(site, site.Faction, config, mp => true).All(p => p.Faction.IsPlayer), "without the extension the faction is back to its default");
        }
    }
}
