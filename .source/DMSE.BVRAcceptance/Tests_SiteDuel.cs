using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Fortified;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Two NPC launch sites of mutually hostile factions fire at each other through the real game loop:
    /// map generation, power, crew, fire channels, departure, world flight, detection, alert stages, locks,
    /// interceptors, terminal defense, seekers, impact, resupply, jamming, site destruction and save/load.
    ///
    /// Sites pick targets through their faction's doctrine (DMSE.MissileDoctrineDef). These tests give both factions a doctrine
    /// whose target worker returns the paired site, the same way a mod would turn on NPC-versus-NPC fighting in XML.
    /// Everything after target selection is production code. Harmony is only used to record errors.
    /// Ids start with T so the shipped radar values and intercept altitudes apply (see AcceptanceDriver).
    /// </summary>
    public static class Tests_SiteDuel
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("TD1", "Two field sites exchange fire: launch, detect, alert, lock, intercept, arrive, damage, no errors", MutualExchange),
            new AcceptanceTest("TD2", "One-way strike on a disarmed site, then destruction: no friendly fire, site removed, attacker ceases fire", StrikeToDestruction),
            new AcceptanceTest("TD3", "A site without a map: its world salvo meets real radars, its abstract defense meets real missiles, no map generated", WorldLayerSite),
            new AcceptanceTest("TD4", "A jammer at one site suppresses the enemy radar and missiles, and leaves its own side's alone", JammerAtOneSite),
        };

        /// <summary>Reloads the game, so it runs after L3 where a third-party crash on a second reload costs only this test.</summary>
        public static AcceptanceTest ReloadTest()
            => new AcceptanceTest("TD5", "Save and load in the middle of the exchange: tracks, channels and hostility survive, the fight goes on", SaveLoadMidFight);

        // ==================================================================
        //  Hooks: target redirect and recording
        // ==================================================================

        /// <summary>Source site id → target site id. Ids survive a reload; object references do not.</summary>
        private static readonly Dictionary<int, int> pairs = new Dictionary<int, int>();
        private static readonly List<Rec> events = new List<Rec>();
        private static readonly List<LaunchRec> launches = new List<LaunchRec>();
        private static readonly HashSet<int> seenTracks = new HashSet<int>();
        private static readonly List<string> errors = new List<string>();
        private static Harmony harmony;
        private static string outPrefix;

        private sealed class Rec
        {
            public int tick, defender, attacker, site;
            public AirCombatEventKind kind;
            public AirCombatLayer layer;
            public AirInterceptOutcome outcome;
            public bool terminal, secondary, removed, realEffector;
            public float hitChance;
        }

        private sealed class LaunchRec
        {
            public int tick, attacker;
            public PlanetTile from, to;
            public string body;
            public bool vulnerable;
        }

        private static int Id(Faction f) => f?.loadID ?? -1;

        private static void Hook()
        {
            if (harmony != null) { return; }
            harmony = new Harmony("DMSE.BVRAcceptance.SiteDuel");
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(Tests_SiteDuel), nameof(LogErrorPrefix)));
            AirCombatEvents.Raised += OnCombatEvent;
        }

        private static void Unhook()
        {
            AirCombatEvents.Raised -= OnCombatEvent;
            harmony?.UnpatchAll(harmony.Id);
            harmony = null;
            pairs.Clear();
            foreach ((FactionDef def, DefModExtension ext) in installed) { def.modExtensions?.Remove(ext); }
            installed.Clear();
        }

        private static void ResetRecording(string testId)
        {
            events.Clear();
            launches.Clear();
            seenTracks.Clear();
            errors.Clear();
            AcceptanceDriver.Watch(ScanNewTracks);
            outPrefix = Path.Combine(AcceptanceDriver.OutDir, "site_duel_" + testId);
            File.WriteAllText(outPrefix + "_timeline.tsv", "tick\tside\tstored\tbuildings\thp\tcrew\tchannels\tengaged\tstage\tthreat\twaves\ttracksOut\n");
            File.WriteAllText(outPrefix + "_events.tsv", "tick\tkind\tlayer\tdefender\tattacker\tsite\toutcome\tterminal\tsecondary\tremoved\teffector\thitChance\n");
        }

        /// <summary>Faction defs given a test doctrine, with the extension to remove afterwards.</summary>
        private static readonly List<(FactionDef def, DefModExtension ext)> installed = new List<(FactionDef, DefModExtension)>();

        /// <summary>
        /// Paired sites fire only at each other; any other site of the same faction def keeps the default (player maps),
        /// because the def may be shared with factions outside the test.
        /// </summary>
        public class DuelTargetWorker : StrikeTargetWorker
        {
            private static readonly StrikeTargetWorker fallback = new StrikeTargetWorker_Players();

            public override void CollectTargets(StrikeTargetQuery query, List<MapParent> into)
            {
                if (query.Source == null || !pairs.TryGetValue(query.Source.ID, out int targetId))
                {
                    fallback.CollectTargets(query, into);
                    return;
                }
                MapParent target = Find.WorldObjects.AllWorldObjects.OfType<MapParent>().FirstOrDefault(p => p.ID == targetId);
                if (target != null) { into.Add(target); }
            }
        }

        /// <summary>Give the faction a doctrine with the duel worker; its other settings stay those it already had.</summary>
        private static void InstallDoctrine(Faction faction)
        {
            FactionDef def = faction.def;
            if (installed.Any(x => x.def == def)) { return; }
            MissileDoctrineDef current = MissileDoctrine.For(faction);
            var doctrine = new MissileDoctrineDef
            {
                defName = "DMSE_Test_DuelDoctrine_" + def.defName,
                settlementDefense = current?.settlementDefense,
                threatAssessment = current?.threatAssessment ?? false,
                targetWorker = new DuelTargetWorker(),
            };
            var ext = new MissileDoctrineExtension { doctrine = doctrine };
            def.modExtensions ??= new List<DefModExtension>();
            def.modExtensions.Insert(0, ext);
            installed.Add((def, ext));
        }

        /// <summary>
        /// Launches are counted as new world tracks, checked every tick. A postfix on IncomingMissileUtility.Make is not reliable:
        /// in a full run it recorded nothing while missiles flew, probably because hot callers had inlined it by then.
        /// A missile counts once it has left its map. Never true: it only uses the per-tick watch.
        /// </summary>
        private static bool ScanNewTracks()
        {
            foreach (WorldObject o in Find.WorldObjects.AllWorldObjects)
            {
                if (!(o is WorldObject_IncomingMissile m) || !seenTracks.Add(m.ID)) { continue; }
                launches.Add(new LaunchRec
                {
                    tick = Find.TickManager.TicksGame,
                    attacker = Id(m.Faction),
                    from = m.Origin.Valid ? m.Origin : m.Tile,
                    to = m.destinationTile,
                    body = m.config?.body?.defName ?? "?",
                    vulnerable = JammerUtility.Vulnerable(m.config),
                });
            }
            return false;
        }

        public static void LogErrorPrefix(string text)
        {
            if (errors.Count < 200) { errors.Add(text); }
        }

        private static void OnCombatEvent(AirCombatEvent e)
        {
            Rec r = new Rec
            {
                tick = Find.TickManager.TicksGame,
                kind = e.Kind,
                layer = e.Layer,
                defender = Id(e.Defender),
                attacker = Id(e.Attacker),
                site = e.Site?.ID ?? -1,
                outcome = e.Outcome,
                terminal = e.Terminal,
                secondary = e.Secondary,
                removed = e.Removed,
                realEffector = e.Effector != null,
                hitChance = e.HitChance,
            };
            events.Add(r);
            try
            {
                File.AppendAllText(outPrefix + "_events.tsv",
                    $"{r.tick}\t{r.kind}\t{r.layer}\t{r.defender}\t{r.attacker}\t{r.site}\t{r.outcome}\t{r.terminal}\t{r.secondary}\t{r.removed}\t{r.realEffector}\t{r.hitChance:0.00}\n");
            }
            catch (Exception) { }
        }

        private static int CountEv(AirCombatEventKind kind, Side defender, Func<Rec, bool> extra = null)
            => events.Count(e => e.kind == kind && e.defender == Id(defender.faction) && (extra == null || extra(e)));

        private static int CountLaunches(Side attacker) => launches.Count(l => l.attacker == Id(attacker.faction));

        private static void CheckNoErrors(TestContext t)
        {
            t.Check(errors.Count == 0, errors.Count == 0 ? "no errors logged during the fight"
                : $"{errors.Count} errors logged during the fight, first: {string.Join(" || ", errors.Distinct().Take(4).Select(FirstLine))}");
        }

        private static string FirstLine(string s)
        {
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        // ==================================================================
        //  Sides
        // ==================================================================

        private static readonly FieldInfo EngagedChannels = typeof(MapComponent_MissileBase)
            .GetField("engagedChannels", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Side
        {
            public string name;
            public Faction faction;
            public Site site;
            public int startStored, startBuildings, startCrew, startChannels;
            public float startHp;
            public int minStored = int.MaxValue, peakEngaged, peakStage;
            public float peakThreat;
            public bool sawLoadJob, sawManned, sawResupply;

            public Map Map => site.Map;
            public bool HasMap => site != null && !site.Destroyed && site.HasMap;
            public MapComponent_BVRCombat Combat => Map.GetComponent<MapComponent_BVRCombat>();
            public MapComponent_MissileBase Base => Map.GetComponent<MapComponent_MissileBase>();
            public AirDefenseSiteAlert Alert => WorldComponent_FactionAirDefenseAlert.Current.StateFor(site);

            public List<Building> Buildings => Map.listerBuildings.allBuildingsNonColonist.Where(b => b.Faction == faction && !b.Destroyed).ToList();
            public int Stored => MissileBaseRackSupplyUtility.RacksOf(Map, faction).Sum(r => r.StoredCount);
            public float Hp => Buildings.Sum(b => (float)b.HitPoints);
            public int Crew => Map.mapPawns.SpawnedPawnsInFaction(faction).Count(p => p.RaceProps.Humanlike && !p.Dead && !p.Downed);
            public int Channels => Combat.fireControlRadars.Where(fc => fc.Active && fc.parent.Faction == faction).Sum(fc => fc.Props.maxTargets);
            public int Engaged => ((System.Collections.IList)EngagedChannels.GetValue(Base)).Cast<WorldObject_IncomingMissile>().Count(m => m != null && !m.Destroyed);
            public bool Armed => Combat.HasInterceptorAmmo(faction);
            public bool SearchUp => Combat.searchRadars.Any(r => r.Active && r.parent.Faction == faction);
            public bool FireControlUp => Combat.fireControlRadars.Any(fc => fc.Active && fc.parent.Faction == faction);
            public int Ciws => Combat.ciwsTurrets.Count(c => c.parent.Faction == faction);
            public int InterceptorLaunchers => Combat.launchers.Count(l => l.parent.Faction == faction);

            /// <summary>Tracks of this side flying at <paramref name="to"/>.</summary>
            public int TracksAt(Side to) => Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>()
                .Count(m => !m.Destroyed && m.Faction == faction && m.destinationTile == to.site.Tile);

            public void TakeStartLevels()
            {
                startStored = Stored;
                startBuildings = Buildings.Count;
                startHp = Hp;
                startCrew = Crew;
                startChannels = Channels;
            }

            public void Sample(Side enemy)
            {
                if (!HasMap) { return; }
                int stored = Stored, engaged = Engaged;
                minStored = Mathf.Min(minStored, stored);
                peakEngaged = Mathf.Max(peakEngaged, engaged);
                AirDefenseSiteAlert alert = Alert;
                peakStage = Mathf.Max(peakStage, alert?.stage ?? 0);
                peakThreat = Mathf.Max(peakThreat, alert?.Threat ?? 0f);
                List<Pawn> crew = Map.mapPawns.SpawnedPawnsInFaction(faction);
                sawLoadJob |= crew.Any(p => p.CurJobDef == DMSE_MissileJobDefOf.DMSE_LoadMissileLauncher);
                sawManned |= Buildings.Any(b => b.TryGetComp<CompMannable>()?.MannedNow == true);
                sawResupply |= Map.listerThings.AllThings.Any(x => x is Skyfaller && x.def == ThingDefOf.DropPodIncoming);
                File.AppendAllText(outPrefix + "_timeline.tsv",
                    $"{Find.TickManager.TicksGame}\t{name}\t{stored}\t{Buildings.Count}\t{Hp:0}\t{Crew}\t{Channels}\t{engaged}\t{alert?.stage ?? 0}\t{alert?.Threat ?? 0f:0.0}\t{Combat.Waves.Count}\t{TracksAt(enemy)}\n");
            }
        }

        private static void Describe(TestContext t, Side s)
        {
            string racks = string.Join(", ", MissileBaseRackSupplyUtility.RacksOf(s.Map, s.faction)
                .GroupBy(r => r.RackThing.def.defName).Select(g => $"{g.Key}×{g.Count()} ({g.Sum(r => r.StoredCount)})"));
            t.Note($"{s.name}: {s.faction.Name} ({s.faction.def.techLevel}) tile {s.site.Tile}, {s.Buildings.Count} buildings, {s.Crew} crew, "
                + $"search {s.Combat.searchRadars.Count}, fire control {s.Combat.fireControlRadars.Count}, interceptor launchers {s.InterceptorLaunchers}, CIWS {s.Ciws}; racks: {racks}");
        }

        // ==================================================================
        //  Setup
        // ==================================================================

        private static bool PickFactions(out Faction a, out Faction b)
        {
            List<Faction> usable = Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.defeated && !f.Hidden && f.def.humanlikeFaction)
                .OrderBy(f => Mathf.Abs((int)f.def.techLevel - (int)TechLevel.Industrial)).ToList();
            a = usable.ElementAtOrDefault(0);
            b = usable.ElementAtOrDefault(1);
            return a != null && b != null;
        }

        /// <summary>Hostile both ways for this test only; the previous relation is restored in cleanup.</summary>
        private static void MakeHostile(Faction a, Faction b)
        {
            FactionRelation ab = a.RelationWith(b), ba = b.RelationWith(a);
            FactionRelationKind oldAb = ab.kind, oldBa = ba.kind;
            int goodAb = ab.baseGoodwill, goodBa = ba.baseGoodwill;
            Override(() =>
            {
                ab.kind = ba.kind = FactionRelationKind.Hostile;
                ab.baseGoodwill = ba.baseGoodwill = -100;
            }, () =>
            {
                ab.kind = oldAb; ba.kind = oldBa;
                ab.baseGoodwill = goodAb; ba.baseGoodwill = goodBa;
            });
        }

        /// <summary>
        /// Two free buildable tiles <paramref name="minSep"/>–<paramref name="maxSep"/> apart, as far as this world allows
        /// from other settlements, whose area defense would otherwise join the fight (a third-party shot is still possible).
        /// </summary>
        private static bool PickTiles(float minSep, float maxSep, out PlanetTile a, out PlanetTile b)
        {
            a = b = PlanetTile.Invalid;
            PlanetTile home = Home.Tile;
            List<Vector3> others = Find.WorldObjects.AllWorldObjects.OfType<MapParent>()
                .Where(p => p.Faction != null && !p.Faction.IsPlayer).Select(p => MissileWorldUtility.TileCenter(p.Tile)).ToList();
            float Score(PlanetTile t)
            {
                Vector3 c = MissileWorldUtility.TileCenter(t);
                return others.Count == 0 ? 999f : others.Min(o => MissileWorldUtility.SurfaceTilesBetween(c, o));
            }
            bool Free(PlanetTile t)
            {
                Tile info = Find.WorldGrid[t];
                return info.PrimaryBiome.canBuildBase && info.hilliness != Hilliness.Impassable
                    && !Find.WorldObjects.AnyWorldObjectAt(t) && !Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t);
            }

            // The tile farthest from everyone can be an island with no neighbor in range, so try the candidates in order.
            List<(PlanetTile tile, float score)> free = new List<(PlanetTile, float)>();
            int count = Find.WorldGrid.TilesCount;
            for (int i = 0; i < count; i++)
            {
                PlanetTile t = new PlanetTile(i, home.Layer);
                if (Free(t)) { free.Add((t, Score(t))); }
            }
            free.Sort((x, y) => y.score.CompareTo(x.score));
            int tried = 0;
            for (int i = 0; i < free.Count && tried < 200; i += 10)
            {
                PlanetTile first = free[i].tile;
                if (MissileWorldUtility.RangeDistance(home, first) < 20f) { continue; }
                tried++;
                foreach ((PlanetTile tile, float _) in free)
                {
                    if (tile == first) { continue; }
                    float d = MissileWorldUtility.RangeDistance(first, tile);
                    if (d < minSep || d > maxSep || MissileWorldUtility.RangeDistance(home, tile) < 20f) { continue; }
                    a = first;
                    b = tile;   // the list is sorted, so this is the best-placed partner
                    return true;
                }
            }
            return false;
        }

        private static Site MakeSite(Faction faction, PlanetTile tile)
        {
            Site site = (Site)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("DMSE_FieldLaunchSite"));
            site.Tile = tile;
            site.SetFaction(faction);
            const float points = 1200f;
            site.AddPart(new SitePart(site, DefDatabase<SitePartDef>.GetNamed("DMSE_FieldLaunchSitePart"),
                new SitePartParams { points = points, threatPoints = points }));
            Find.WorldObjects.Add(site);
            Track(site);
            return site;
        }

        /// <summary>The highest field-site tier: two fire-control radars, several launch positions, CIWS.</summary>
        private static Map GenerateMap(Site site)
        {
            Difficulty difficulty = Find.Storyteller.difficulty;
            float saved = difficulty.threatScale;
            difficulty.threatScale = 1.5f;
            try
            {
                Map map = MissileWorldUtility.ResolveArrivalMap(site.Tile, site, true);
                KeepMapAlive(map);
                return map;
            }
            finally { difficulty.threatScale = saved; }
        }

        private static void Setup(TestContext t, string testId, float minSep, float maxSep, bool mapForB, out Side a, out Side b)
        {
            ResetRecording(testId);
            Override(Hook, Unhook);
            EnsureNpcWorldNetwork();
            t.Require(PickFactions(out Faction fa, out Faction fb), "two humanlike NPC factions exist");
            MakeHostile(fa, fb);
            InstallDoctrine(fa);
            InstallDoctrine(fb);
            t.Check(fa.HostileTo(fb) && fb.HostileTo(fa), $"{fa.Name} and {fb.Name} are hostile to each other");
            t.Require(PickTiles(minSep, maxSep, out PlanetTile ta, out PlanetTile tb), $"two free tiles {minSep}–{maxSep} tiles apart");
            a = new Side { name = "A", faction = fa, site = MakeSite(fa, ta) };
            b = new Side { name = "B", faction = fb, site = MakeSite(fb, tb) };
            t.Note($"sites {MissileWorldUtility.RangeDistance(ta, tb):0.0} tiles apart, {MissileWorldUtility.RangeDistance(Home.Tile, ta):0} from home");
            pairs[a.site.ID] = b.site.ID;
            pairs[b.site.ID] = a.site.ID;
            t.Require(GenerateMap(a.site) != null, "site A map generated");
            if (mapForB)
            {
                t.Require(GenerateMap(b.site) != null, "site B map generated");
            }
        }

        private static Wait WaitOperational(Side s) => new WaitUntil(() => s.SearchUp && s.FireControlUp, 6000);

        /// <summary>Generated racks must reach a working fire-control chain; a site that cannot get there fails the test here.</summary>
        private static IEnumerable<Wait> BringUp(TestContext t, Side s)
        {
            WaitUntil up = (WaitUntil)WaitOperational(s);
            yield return up;
            t.Check(!up.TimedOut, $"{s.name}: search and fire-control radars come online (search {s.SearchUp}, fire control {s.FireControlUp})");
            s.TakeStartLevels();
            Describe(t, s);
        }

        private static void KeepAlive(params Side[] sides)
        {
            foreach (Side s in sides) { if (s.HasMap) { KeepMapAlive(s.Map); } }
        }

        // ==================================================================
        //  TD1: mutual exchange
        // ==================================================================

        public static IEnumerable<Wait> MutualExchange(TestContext t)
        {
            Setup(t, "TD1", 8f, 11f, true, out Side a, out Side b);
            t.Check(a.Alert?.baseline == AirDefenseAlertTuningDef.Current.baselineMilitary && b.Alert?.baseline == AirDefenseAlertTuningDef.Current.baselineMilitary,
                "both sites start at the military alert baseline");
            foreach (Wait w in BringUp(t, a)) { yield return w; }
            foreach (Wait w in BringUp(t, b)) { yield return w; }
            t.Check(a.startStored > 0 && b.startStored > 0, $"both sites start with missiles ({a.startStored} / {b.startStored})");
            t.Check(a.startChannels > 0 && b.startChannels > 0, $"both sites have fire channels ({a.startChannels} / {b.startChannels})");

            // Tracks live a few hundred ticks; a frame at ultrafast is enough, but do not miss the first one.
            TickWatch trackAB = AcceptanceDriver.Watch(() => a.TracksAt(b) > 0);
            TickWatch trackBA = AcceptanceDriver.Watch(() => b.TracksAt(a) > 0);

            int lettersBefore = LetterCount("DMSE.Missile.IncomingLetter") + LetterCount("DMSE.BVR.World.DetectedLetter");
            int start = Find.TickManager.TicksGame;
            // Past the two-day rack resupply: the racks run dry in a few hours, so this also covers the pods.
            int minTicks = GenDate.TicksPerDay * 5 / 2, maxTicks = GenDate.TicksPerDay * 4;
            bool shot = false;
            TickWatch podA = AcceptanceDriver.Watch(() => a.HasMap && a.Map.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming).Any(x => a.Stored < a.startStored));
            TickWatch podB = AcceptanceDriver.Watch(() => b.HasMap && b.Map.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming).Any(x => b.Stored < b.startStored));
            bool Milestones() =>
                CountLaunches(a) > 0 && CountLaunches(b) > 0
                && CountEv(AirCombatEventKind.InterceptorLaunched, a) > 0 && CountEv(AirCombatEventKind.InterceptorLaunched, b) > 0
                && CountEv(AirCombatEventKind.InterceptResolved, a) > 0 && CountEv(AirCombatEventKind.InterceptResolved, b) > 0
                && CountEv(AirCombatEventKind.TrackArrived, a) > 0 && CountEv(AirCombatEventKind.TrackArrived, b) > 0;

            while (Find.TickManager.TicksGame - start < maxTicks)
            {
                KeepAlive(a, b);
                a.Sample(b);
                b.Sample(a);
                if (!shot && a.TracksAt(b) > 0 && b.TracksAt(a) > 0)
                {
                    shot = true;
                    foreach (Wait w in GlobeShot(t, a, "both_tracks")) { yield return w; }
                }
                if (Find.TickManager.TicksGame - start >= minTicks && Milestones()) { break; }
                yield return new WaitTicks(250);
            }
            int elapsed = Find.TickManager.TicksGame - start;
            t.Note($"fought {elapsed} ticks ({elapsed / (float)GenDate.TicksPerDay:0.00} days); launches A {CountLaunches(a)}, B {CountLaunches(b)}");
            foreach (var g in events.GroupBy(e => (e.kind, e.layer, e.outcome, e.defender, e.terminal)).OrderBy(g => g.Key.kind))
            {
                t.Note($"  {g.Key.kind} {g.Key.layer} {g.Key.outcome} defender {g.Key.defender}{(g.Key.terminal ? " terminal" : "")}: {g.Count()}");
            }

            // ── Launch chain ──
            foreach ((Side me, Side enemy, TickWatch track) in new[] { (a, b, trackAB), (b, a, trackBA) })
            {
                t.Check(CountLaunches(me) > 0, $"{me.name} launches at {enemy.name} ({CountLaunches(me)})");
                t.Check(me.minStored < me.startStored, $"{me.name}: launches come out of its racks ({me.startStored} → lowest {me.minStored})");
                t.Check(track.Seen, $"{me.name}: the missile leaves the map and becomes a world track");
                t.Check(me.peakEngaged <= Mathf.Max(me.startChannels, 1), $"{me.name}: in-flight missiles stay within its fire channels (peak {me.peakEngaged} of {me.startChannels})");
            }
            t.Check(launches.All(l => l.to == a.site.Tile || l.to == b.site.Tile), "every launch went to the other site");
            t.Check(launches.All(l => l.from == a.site.Tile || l.from == b.site.Tile), "every launch came from one of the two sites");
            t.Note($"bodies: {string.Join(", ", launches.GroupBy(l => l.body).Select(g => $"{g.Key}×{g.Count()}"))}");

            // ── Air defense chain, per defender ──
            foreach ((Side me, Side enemy) in new[] { (a, b), (b, a) })
            {
                int attacker = Id(enemy.faction);
                t.Check(CountEv(AirCombatEventKind.TrackDetected, me, e => e.attacker == attacker) > 0, $"{me.name} detects {enemy.name}'s missiles");
                t.Check(CountEv(AirCombatEventKind.LockStarted, me, e => e.attacker == attacker) > 0, $"{me.name} locks {enemy.name}'s missiles");
                t.Check(CountEv(AirCombatEventKind.InterceptorLaunched, me, e => e.attacker == attacker && e.realEffector) > 0, $"{me.name} fires an interceptor from a real launcher");
                t.Check(CountEv(AirCombatEventKind.InterceptResolved, me, e => e.attacker == attacker) > 0, $"{me.name}'s intercepts resolve");
                t.Check(me.peakStage >= (int)AirDefenseStage.Passive, $"{me.name}: alert stage held or rose (peak {me.peakStage})");
                // Peak, not the end value: pressure decays within two days and the racks run dry long before the fight ends.
                t.Check(me.peakThreat > 0f, $"{me.name}: the attack is booked as pressure (peak {me.peakThreat:0.0})");
                t.Note($"{me.name}: stage {me.Alert?.stage}, threat {me.Alert?.Threat:0.0}; stored {me.startStored} → {me.Stored}, buildings {me.startBuildings} → {me.Buildings.Count}, HP {me.startHp:0} → {me.Hp:0}, crew {me.startCrew} → {me.Crew}; manned seen {me.sawManned}, carry job seen {me.sawLoadJob}, resupply pod seen {me.sawResupply}");
            }

            // ── Arrival, terminal, damage ──
            int arrivalsA = CountEv(AirCombatEventKind.TrackArrived, a), arrivalsB = CountEv(AirCombatEventKind.TrackArrived, b);
            t.Check(arrivalsA + arrivalsB > 0, $"missiles get through to a site (A {arrivalsA}, B {arrivalsB})");
            bool anyTerminal = events.Any(e => e.kind == AirCombatEventKind.InterceptResolved && e.terminal);
            if (a.Ciws + b.Ciws > 0) { t.Check(anyTerminal, $"terminal defense engaged an arrival (CIWS A {a.Ciws}, B {b.Ciws})"); }
            bool damaged = a.Hp < a.startHp || b.Hp < b.startHp || a.Buildings.Count < a.startBuildings || b.Buildings.Count < b.startBuildings;
            t.Check(damaged, $"impacts damage a site (A HP {a.startHp:0} → {a.Hp:0}, B HP {b.startHp:0} → {b.Hp:0})");

            // ── Resupply: racks that ran dry get pods while the crew lives ──
            foreach ((Side me, TickWatch pod) in new[] { (a, podA), (b, podB) })
            {
                t.Note($"{me.name}: stored lowest {me.minStored}, now {me.Stored}, crew {me.Crew}, supply pod seen {pod.Seen}, launches {CountLaunches(me)}");
                if (me.Crew > 0 && MissileBaseRackSupplyUtility.RacksOf(me.Map, me.faction).Any(r => r.StoredCount == 0))
                {
                    t.Check(pod.Seen, $"{me.name}: pods come for its empty racks");
                }
            }

            // ── Side effects ──
            t.Check(LetterCount("DMSE.Missile.IncomingLetter") + LetterCount("DMSE.BVR.World.DetectedLetter") == lettersBefore,
                "a war between two NPC sites sends the player no incoming or detection letter");
            t.Check(!Find.WorldObjects.AllWorldObjects.OfType<WorldObject_IncomingMissile>().Any(m => !m.Destroyed && m.destinationTile == Home.Tile), "nothing flies at the player's colony");
            CheckNoErrors(t);

            foreach (Side s in new[] { a, b })
            {
                Thing hub = s.Buildings.OrderBy(x => x.HitPoints / (float)Mathf.Max(1, x.MaxHitPoints)).FirstOrDefault();
                if (hub == null) { continue; }
                CameraJumper.TryJump(new GlobalTargetInfo(hub.Position, s.Map));
                yield return new WaitFrames(5);
                Find.CameraDriver.SetRootPosAndSize(hub.Position.ToVector3Shifted(), 60f);
                yield return new WaitFrames(60);
                t.Screenshot("site_" + s.name);
                yield return new WaitFrames(5);
            }
        }

        /// <summary>The globe shows tracks only while paused and after about 180 frames (see the acceptance notes).</summary>
        private static IEnumerable<Wait> GlobeShot(TestContext t, Side s, string name)
        {
            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            // The fixture's "non-destroyable thing" noise auto-opens the debug log over the shot.
            foreach (Window window in Find.WindowStack.Windows.Where(w => w.GetType().Name == "EditWindow_Log").ToList())
            {
                Find.WindowStack.TryRemove(window, doCloseSound: false);
            }
            CameraJumper.TryJump(new GlobalTargetInfo(s.site));
            yield return new WaitFrames(180);
            t.Screenshot(name);
            yield return new WaitFrames(5);
            CameraJumper.TryHideWorld();
            t.allowPause = false;
        }

        // ==================================================================
        //  TD2: strike a disarmed site, then destroy it
        // ==================================================================

        public static IEnumerable<Wait> StrikeToDestruction(TestContext t)
        {
            Setup(t, "TD2", 8f, 11f, true, out Side a, out Side b);
            // Nobody has a target while the radars come up: otherwise the first wave leaves on the 500-tick check before B is disarmed.
            int targetOfA = pairs[a.site.ID], targetOfB = pairs[b.site.ID];
            pairs[a.site.ID] = pairs[b.site.ID] = -1;
            foreach (Wait w in BringUp(t, a)) { yield return w; }
            foreach (Wait w in BringUp(t, b)) { yield return w; }

            // B cannot shoot back or defend: no fire control, no CIWS, so everything A fires must reach the map.
            foreach (CompFireControlRadar fc in b.Combat.fireControlRadars.Where(x => x.parent.Faction == b.faction).ToList()) { fc.parent.Destroy(DestroyMode.Vanish); }
            foreach (CompTerminalCIWS c in b.Combat.ciwsTurrets.Where(x => x.parent.Faction == b.faction).ToList()) { c.parent.Destroy(DestroyMode.Vanish); }
            foreach (Building turret in b.Buildings.Where(x => x is Building_Turret).ToList()) { turret.Destroy(DestroyMode.Vanish); }
            yield return new WaitTicks(5);
            b.TakeStartLevels();
            a.TakeStartLevels();
            t.Check(b.Channels == 0, "B has no fire channels left");
            t.Check(CountLaunches(a) + CountLaunches(b) == 0, "no launch before the sides have targets");
            // B keeps its target and its missiles: only the missing fire channels may stop it.
            pairs[a.site.ID] = targetOfA;
            pairs[b.site.ID] = targetOfB;

            int start = Find.TickManager.TicksGame;
            bool Damaged() => b.Hp < b.startHp || b.Buildings.Count < b.startBuildings;
            while (Find.TickManager.TicksGame - start < GenDate.TicksPerDay * 3 && !(Damaged() && CountEv(AirCombatEventKind.TrackArrived, b) >= 2))
            {
                KeepAlive(a, b);
                a.Sample(b);
                b.Sample(a);
                yield return new WaitTicks(250);
            }
            t.Note($"after {Find.TickManager.TicksGame - start} ticks: A launched {CountLaunches(a)}, arrivals {CountEv(AirCombatEventKind.TrackArrived, b)}; B HP {b.startHp:0} → {b.Hp:0}, buildings {b.startBuildings} → {b.Buildings.Count}, crew {b.startCrew} → {b.Crew}");
            t.Check(CountLaunches(a) > 0, "A launches at B");
            t.Check(CountLaunches(b) == 0, "B without fire control does not launch");
            t.Check(CountEv(AirCombatEventKind.TrackArrived, b) > 0, "A's missiles arrive at B");
            t.Check(Damaged(), $"the undefended site takes damage (HP {b.startHp:0} → {b.Hp:0})");
            t.Check(a.Buildings.Count == a.startBuildings && a.Hp >= a.startHp * 0.99f, $"the attacker's own site is untouched (HP {a.startHp:0} → {a.Hp:0})");
            t.Check((b.Alert?.Threat ?? 0f) > 0f, $"the hits raise B's alert pressure ({b.Alert?.Threat ?? 0f:0.0})");
            t.Screenshot("strike_damage");

            // ── Objectives: every radar and world launcher gone, and nobody ever looked at this map ──
            MapParent bParent = b.site;
            t.Check(Find.CurrentMap == Home, "site B's map is off screen");
            t.Check(b.Base.ShowsObjectives, "an unobserved site still registers its objectives");

            // A missile in flight when the site goes away must end quietly.
            MissileConfig cfg = a.site.GetComponent<WorldObjectComp_MissileBase>().BuildFireConfig();
            WorldObject_IncomingMissile inFlight = IncomingMissileUtility.Launch(a.site.Tile, b.site.Tile, cfg, a.faction, target: b.site);
            t.Require(inFlight != null, "a last missile is flying at B");
            int ticksLeft = inFlight.TicksToArrival;

            int destroyedLetters = LetterCount("DMSE.MissileBase.Destroyed");
            foreach (Building x in b.Base.RemainingRadars.Concat(b.Base.RemainingLaunchers).ToList()) { if (!x.Destroyed) { x.Kill(); } }
            var waitGone = new WaitUntil(() => { KeepAlive(a); return bParent.Destroyed; }, 1100);
            yield return waitGone;
            t.Check(!waitGone.TimedOut, "with no objective left the site is destroyed, though its map is off screen");
            t.Check(bParent.Destroyed, "the destroyed site is gone from the world");
            t.Check(!Find.Maps.Any(m => m.Parent == bParent), "its map is removed");
            t.Check(LetterCount("DMSE.MissileBase.Destroyed") == destroyedLetters + 1, "one letter reports it");
            t.Check(Find.CurrentMap == Home, "the player's view stays where it was");
            yield return new WaitFrames(5);

            var waitMissile = new WaitUntil(() => inFlight.Destroyed, ticksLeft + 300);
            yield return waitMissile;
            t.Check(!waitMissile.TimedOut, "the missile flying at the gone site ends");

            // ── With its only target gone, A stops ──
            // Launches, not stored rounds: a crew member carrying a round to a launcher also lowers the rack total.
            int launched = CountLaunches(a);
            int stored = a.Stored;
            yield return new WaitTicks(1500);
            KeepAlive(a);
            t.Note($"A stored {stored} → {a.Stored} while holding fire");
            t.Check(CountLaunches(a) == launched, $"A ceases fire without a target (launches {launched} → {CountLaunches(a)})");
            t.Check(!a.site.Destroyed && a.HasMap, "the surviving site stands");
            CheckNoErrors(t);
        }

        // ==================================================================
        //  TD3: one site without a map (abstract layer)
        // ==================================================================

        public static IEnumerable<Wait> WorldLayerSite(TestContext t)
        {
            // The field site's abstract detection range is 10 tiles.
            Setup(t, "TD3", 6f, 9f, false, out Side a, out Side b);
            foreach (Wait w in BringUp(t, a)) { yield return w; }
            WorldObjectComp_AirDefense abstractDefense = b.site.GetComponent<WorldObjectComp_AirDefense>();
            WorldObjectComp_MissileBase worldFire = b.site.GetComponent<WorldObjectComp_MissileBase>();
            t.Require(abstractDefense?.Available == true, "site B has an abstract air defense");
            t.Check(!b.site.HasMap, "site B has no map");
            int stockStart = abstractDefense.Stock;

            // ── B's world layer fires; A's real radars and launchers answer ──
            int salvos = 0;
            for (int i = 0; i < 3; i++)
            {
                salvos += worldFire.TryFireAtPlayerColonies();
                KeepAlive(a);
                yield return new WaitTicks(1200);
            }
            t.Check(salvos > 0, $"the world layer of B fires at A's map ({salvos} missiles in 3 salvos)");
            int waitStart = Find.TickManager.TicksGame;
            int b2a() => launches.Count(l => l.attacker == Id(b.faction));

            // ── A fires back from its map at the mapless site ──
            int aStart = Find.TickManager.TicksGame;
            while (Find.TickManager.TicksGame - aStart < GenDate.TicksPerDay * 2
                && !(abstractDefense.Stock < stockStart && CountLaunches(a) > 0 && a.TracksAt(b) == 0))
            {
                KeepAlive(a);
                a.Sample(b);
                yield return new WaitTicks(250);
            }

            t.Note($"launches A {CountLaunches(a)}, B {b2a()}; B stock {stockStart} → {abstractDefense.Stock}; A stored {a.startStored} → {a.Stored}");
            foreach (var g in events.GroupBy(e => (e.kind, e.layer, e.outcome, e.defender)).OrderBy(g => g.Key.kind))
            {
                t.Note($"  {g.Key.kind} {g.Key.layer} {g.Key.outcome} defender {g.Key.defender}: {g.Count()}");
            }

            int attackerB = Id(b.faction), attackerA = Id(a.faction);
            t.Check(CountEv(AirCombatEventKind.TrackDetected, a, e => e.attacker == attackerB) > 0, "A's real radar detects B's world-layer missiles");
            t.Check(CountEv(AirCombatEventKind.InterceptorLaunched, a, e => e.attacker == attackerB && e.realEffector) > 0, "A answers from a real launcher");
            t.Check(CountLaunches(a) > 0, "A launches at the mapless site");
            t.Check(CountEv(AirCombatEventKind.TrackDetected, b, e => e.attacker == attackerA) > 0, "B's abstract node detects A's missiles");
            t.Check(CountEv(AirCombatEventKind.InterceptorLaunched, b, e => e.attacker == attackerA && !e.realEffector) > 0, "B's abstract node fires, with no building behind it");
            t.Check(abstractDefense.Stock < stockStart, $"each abstract shot costs stock ({stockStart} → {abstractDefense.Stock})");
            t.Check(!b.site.HasMap && !b.site.Destroyed, "missiles reaching a mapless site generate no map and leave the site standing");
            t.Check(a.TracksAt(b) == 0, "no missile is left hanging over the mapless site");
            CheckNoErrors(t);
        }

        // ==================================================================
        //  TD4: jammer at one site
        // ==================================================================

        public static IEnumerable<Wait> JammerAtOneSite(TestContext t)
        {
            // Inside the jammer's 8-tile ring, so A's own radar is suppressed as well as its missiles.
            Setup(t, "TD4", 5f, 7f, true, out Side a, out Side b);
            foreach (Wait w in BringUp(t, a)) { yield return w; }
            foreach (Wait w in BringUp(t, b)) { yield return w; }

            ThingDef jammerDef = ThingDef.Named("DMSE_JammerStation");
            CompProperties_Jammer props = jammerDef.GetCompProperties<CompProperties_Jammer>();
            float oldBreak = props.jamGuidanceBreakFactor, oldOffMap = props.offMapChance;
            Override(() => { props.jamGuidanceBreakFactor = 100f; props.offMapChance = 1f; },
                () => { props.jamGuidanceBreakFactor = oldBreak; props.offMapChance = oldOffMap; });

            Thing jam = SpawnPowered(b.Map, jammerDef, b.faction, b.Combat.searchRadars.First().parent.Position);
            t.Require(jam != null, "a jammer station stands at B");
            CompJammer jammer = jam.TryGetComp<CompJammer>();
            var waitJam = new WaitUntil(() => jammer.CurrentJamPower > 0f, 1500);
            yield return waitJam;
            t.Require(!waitJam.TimedOut, $"the jammer runs (power {jammer.CurrentJamPower:0.00})");

            WorldComponent_BVRNetwork network = WorldComponent_BVRNetwork.Current;
            var waitScan = new WaitUntil(() => network.NodeOf(a.site, a.faction)?.jamEff > 0f, 600);
            yield return waitScan;
            float jamA = network.NodeOf(a.site, a.faction)?.jamEff ?? 0f;
            float jamB = network.NodeOf(b.site, b.faction)?.jamEff ?? 0f;
            t.Check(jamA > 0f, $"the enemy radar inside the ring is suppressed (jamEff {jamA:0.00})");
            t.Check(jamB == 0f, "the jammer's own side is not suppressed");

            int start = Find.TickManager.TicksGame;
            int JammedA() => events.Count(e => e.kind == AirCombatEventKind.TrackJammed && e.attacker == Id(a.faction));
            bool Vulnerable(Side s) => launches.Any(l => l.attacker == Id(s.faction) && l.vulnerable);
            while (Find.TickManager.TicksGame - start < GenDate.TicksPerDay * 3 && !(JammedA() > 0 && CountLaunches(b) > 0))
            {
                KeepAlive(a, b);
                a.Sample(b);
                b.Sample(a);
                yield return new WaitTicks(250);
            }

            t.Note($"launches A {CountLaunches(a)} (vulnerable {launches.Count(l => l.attacker == Id(a.faction) && l.vulnerable)}), B {CountLaunches(b)}; jammed A {JammedA()}");
            t.Check(CountLaunches(a) > 0, "A launches into the ring");
            if (Vulnerable(a)) { t.Check(JammedA() > 0, "A's jammable missiles lose guidance in the ring"); }
            else { t.Note("A only fired seekers immune to the jammer: nothing to jam."); }
            t.Check(events.All(e => e.kind != AirCombatEventKind.TrackJammed || e.attacker != Id(b.faction)), "B's own missiles are never jammed by B's jammer");
            t.Check(CountLaunches(b) > 0, "B keeps firing while jamming");
            CheckNoErrors(t);
        }

        /// <summary>Consumers on a generated map need a net: a conduit first, the station next to it.</summary>
        private static Thing SpawnPowered(Map map, ThingDef def, Faction faction, IntVec3 near)
        {
            bool Clear(IntVec3 c) => GenAdj.OccupiedRect(c, Rot4.North, def.size).ExpandedBy(2).Cells
                .All(x => x.InBounds(map) && x.Standable(map) && x.GetEdifice(map) == null);
            if (!CellFinder.TryFindRandomCellNear(near, map, 40, Clear, out IntVec3 cell)) { return null; }
            IntVec3 conduitCell = GenAdj.OccupiedRect(cell, Rot4.North, def.size).ExpandedBy(1).EdgeCells.First();
            Thing conduit = ThingMaker.MakeThing(ThingDefOf.PowerConduit);
            conduit.SetFaction(faction);
            GenSpawn.Spawn(conduit, conduitCell, map, WipeMode.Vanish);
            Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(faction);
            GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
            return thing;
        }

        // ==================================================================
        //  TD5: save and load mid-fight
        // ==================================================================

        public static IEnumerable<Wait> SaveLoadMidFight(TestContext t)
        {
            Setup(t, "TD5", 8f, 11f, true, out Side a, out Side b);
            foreach (Wait w in BringUp(t, a)) { yield return w; }
            foreach (Wait w in BringUp(t, b)) { yield return w; }

            int siteA = a.site.ID, siteB = b.site.ID, factionA = a.faction.loadID, factionB = b.faction.loadID;
            int start = Find.TickManager.TicksGame;
            bool Flying() => a.TracksAt(b) + b.TracksAt(a) > 0;
            var waitFlying = new WaitUntil(() => { KeepAlive(a, b); return Flying(); }, GenDate.TicksPerDay);
            yield return waitFlying;
            t.Require(!waitFlying.TimedOut, "a missile is in flight between the sites");

            t.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            int tracksBefore = a.TracksAt(b) + b.TracksAt(a);
            int engagedBefore = a.Engaged + b.Engaged;
            int interceptorsBefore = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>().Count();
            int storedBefore = a.Stored + b.Stored;
            t.Note($"saved after {Find.TickManager.TicksGame - start} ticks with {tracksBefore} tracks, {engagedBefore} booked channels, {interceptorsBefore} interceptors, {storedBefore} stored");

            RememberEnemyForReload();
            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(TestSaveName);
            yield return new WaitFrames(10);
            GameDataSaveLoader.LoadGame(TestSaveName);
            yield return new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
            yield return new WaitFrames(5);
            t.allowPause = false;

            // The fixture dropped its cleanup list on reload; the patches stayed in this process.
            Override(() => { }, Unhook);

            Site Resolve(int id) => Find.WorldObjects.AllWorldObjects.OfType<Site>().FirstOrDefault(s => s.ID == id);
            Faction FactionById(int id) => Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.loadID == id);
            a = new Side { name = "A", faction = FactionById(factionA), site = Resolve(siteA) };
            b = new Side { name = "B", faction = FactionById(factionB), site = Resolve(siteB) };
            t.Require(a.site != null && b.site != null, "both sites survive the reload");
            t.Require(a.HasMap && b.HasMap, "both site maps survive the reload");
            Track(a.site);
            Track(b.site);
            KeepAlive(a, b);
            t.Check(a.faction.HostileTo(b.faction), "the factions are still hostile");

            int tracksAfter = a.TracksAt(b) + b.TracksAt(a);
            int interceptorsAfter = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>().Count();
            t.Note($"loaded with {tracksAfter} tracks, {a.Engaged + b.Engaged} booked channels, {interceptorsAfter} interceptors, {a.Stored + b.Stored} stored");
            // A booked channel can belong to a missile still climbing out of the map, so booked ≥ tracks before the save;
            // about 70 ticks pass before this point, and those missiles become tracks without any new launch.
            t.Check(tracksAfter <= engagedBefore, $"no missile appears from nowhere ({engagedBefore} booked before → {tracksAfter} tracks)");
            t.Check(a.Engaged + b.Engaged >= tracksAfter, $"every track in the air still books its channel ({tracksAfter} tracks, {a.Engaged + b.Engaged} booked)");
            t.Check(a.Stored + b.Stored <= storedBefore, $"no missile is duplicated by the reload ({storedBefore} → {a.Stored + b.Stored})");
            yield return new WaitTicks(600);
            foreach (Side s in new[] { a, b })
            {
                object cached = typeof(MapComponent_MissileBase).GetField("isMissileBaseMapCached", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(s.Base);
                t.Check(cached is true, $"{s.name}: the map is still recognised as a missile site after the reload (cached {cached ?? "null"})");
            }

            // ── The fight goes on ──
            int launchesBefore = launches.Count, eventsBefore = events.Count;
            int goOn = Find.TickManager.TicksGame;
            while (Find.TickManager.TicksGame - goOn < GenDate.TicksPerDay && !(launches.Count > launchesBefore && events.Count > eventsBefore))
            {
                KeepAlive(a, b);
                a.Sample(b);
                b.Sample(a);
                yield return new WaitTicks(250);
            }
            t.Check(events.Count > eventsBefore, $"air defense keeps working after the reload ({events.Count - eventsBefore} events)");
            t.Check(launches.Count > launchesBefore, $"the sites keep firing after the reload ({launches.Count - launchesBefore} launches)");
            CheckNoErrors(t);
        }
    }
}
