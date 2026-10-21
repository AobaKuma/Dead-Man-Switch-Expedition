using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Skyfaller → Projectile migration, P1 (docs/Missile_Skyfaller轉Projectile_計畫.md §8.6):
    /// the new <see cref="Projectile_MissileBase"/> must replay the old ascent skyfallers tick for tick.
    /// </summary>
    public static class Tests_MissileFlight
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("F1", "Projectile ascent replays the skyfaller ascent tick for tick (pose, timeline, exhaust inputs)", AscentParity),
            new AcceptanceTest("F2", "Old and new ascent screenshots at the same ticks", AscentScreenshots),
            new AcceptanceTest("F3", "Entry heading follows the world track's last leg and maps to the vanilla caravan edge", WorldHeadings),
            new AcceptanceTest("F4", "Every missile body launches its own projectile with its own texture and launch effect", BodyLaunchProjectiles),
            new AcceptanceTest("F5", "Departures turn to the world heading after the ascent, accelerate on the speed curve and fly out of the map edge", DepartureCruise),
            new AcceptanceTest("F6", "Departure cruise screenshots (east-launched light missile heading north-west)", DepartureScreenshots),
            new AcceptanceTest("F7", "Incoming missiles fly in from the map edge the world heading points at, straight to the target; BVR release keeps the heading", IncomingEntry),
            new AcceptanceTest("F8", "Incoming entry screenshots (cruise missile arriving from the north-east)", IncomingScreenshots),
            new AcceptanceTest("F9", "High-angle shields stop incoming missiles at the contact point (no attribution, shield HP, strike report); ground shields and friendly shields do not", ShieldInterception),
            new AcceptanceTest("F10", "Missile damage is attributed to the launching building, on the same map and across maps", LauncherAttribution),
        };

        // ==================================================================
        //  L3: old saves (docs/Missile_Skyfaller轉Projectile_計畫.md §9)
        // ==================================================================

        /// <summary>Appended after L2 (it reloads the game); run alone with -Only L3 when a third-party mod cuts the full run.</summary>
        public static AcceptanceTest OldSaveTest() =>
            new AcceptanceTest("L3", "Old saves: skyfaller-era incoming missiles (flying and BVR-held) convert to projectiles; old departure and interceptor skyfallers still resolve", OldSaveConversion);

        public static IEnumerable<Wait> OldSaveConversion(TestContext t)
        {
            IntVec3 target = SafeImpactCell;
            MissileConfig config = MissileConfig.NewWithDefaults(LightBody);
            ThingDef incomingDef = LightBody.incomingSkyfaller;
            int fake = WorldComponent_StrikeReports.Current.NewMissionId();
            const int heldTargetId = 999901;

            // A: an incoming missile in flight.
            Projectile_MissileIncoming flying = Incoming(target, 45f, Enemy, null, config);
            flying.worldPhaseDone = true;
            flying.strikeMissionId = fake; flying.strikeWave = 0;
            // B: an incoming missile held by BVR in a far-off wave.
            var held = (Projectile_MissileIncoming)MissileProjectileMaker.MakeIncoming(incomingDef);
            held.config = config.Clone(); held.attacker = Enemy; held.bvrHandled = true; held.worldPhaseDone = true;
            held.strikeMissionId = fake; held.strikeWave = 1;
            var wave = new BVRWave(Find.TickManager.TicksGame + 20000, new List<BVRTarget> { new BVRTarget(held, target, Home, heldTargetId) }) { defenderFaction = Player };
            HomeBVR.Waves.Add(wave);
            // C: an old departure skyfaller carrying its world track (platform fallback def is still a skyfaller).
            PlanetTile dest = TileAtDistance(8f, 12f);
            t.Require(dest.Valid, "a destination tile exists");
            var wo = (ScorerProjectile_WorldObject)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("DMSE_WorldObject_LightMissile"));
            wo.SetFaction(Player); wo.Tile = Home.Tile; wo.destinationTile = dest; wo.config = config.Clone();
            int woId = wo.ID;
            var departure = (ScorerProjectile)MissileProjectileMaker.LaunchDeparture(ThingDef.Named("DMSE_SkyFaller_LightMissile"), Pad.CenterCell, Home, Rot4.East, null, wo);
            // D: an old interceptor skyfaller (visual only).
            var interceptor = (InterceptProjectile)MissileProjectileMaker.LaunchInterceptor(InterceptorSkyfaller, Pad.CenterCell + new IntVec3(4, 0, 0), Home, Rot4.North, null, -1, 0f);
            t.Check(departure.Spawned && interceptor.Spawned && flying.Spawned && !held.Spawned, "four in-flight objects before saving");

            // Save, then rewrite the incoming missiles to the skyfaller-era class, as an old save holds them.
            RememberEnemyForReload();
            Game before = Current.Game;
            GameDataSaveLoader.SaveGame(TestSaveName);
            yield return new WaitFrames(10);
            string path = GenFilePaths.FilePathForSavedGame(TestSaveName);
            string xml = File.ReadAllText(path);
            int rewritten = System.Text.RegularExpressions.Regex.Matches(xml, "Class=\"DMSE\\.Projectile_MissileIncoming\"").Count;
            xml = xml.Replace("Class=\"DMSE.Projectile_MissileIncoming\"", "Class=\"DMSE.MissileIncoming\"");
            File.WriteAllText(path, xml);
            t.Require(rewritten >= 2, $"{rewritten} incoming missiles rewritten to DMSE.MissileIncoming in the save file");

            // The game ticks a while after loading before this routine resumes, so catch the converted missile per tick.
            Projectile_MissileIncoming a = null;
            bool legacySeen = false;
            AcceptanceDriver.Watch(() =>
            {
                if (Current.Game == before || Find.Maps.Count == 0) { return false; }
                legacySeen |= Home.listerThings.AllThings.OfType<MissileIncoming>().Any(x => !x.Destroyed);
                a = Home.listerThings.AllThings.OfType<Projectile_MissileIncoming>().FirstOrDefault(x => x.strikeMissionId == fake && x.strikeWave == 0);
                return a != null;
            });
            GameDataSaveLoader.LoadGame(TestSaveName);
            yield return new WaitUntil(() => Current.Game != before && Current.ProgramState == ProgramState.Playing, 999999);
            yield return new WaitFrames(5);
            yield return new WaitTicks(2);

            // A: converted on its first tick into a projectile on the same target, with its fields.
            t.Check(!Home.listerThings.AllThings.OfType<MissileIncoming>().Any(), "no skyfaller-era incoming missile remains on the map");
            t.Check(a != null && a.Position == target && a.attacker == Enemy && a.config != null && a.config.Valid && a.worldPhaseDone,
                "the flying old missile continues as a projectile on the same target with its config, attacker and strike id");
            t.Note($"legacy missile seen on the map before conversion: {legacySeen}");
            // B: converted inside the BVR wave on load.
            BVRTarget heldTarget = HomeBVR.Waves.SelectMany(w => w.targets).FirstOrDefault(x => x.id == heldTargetId);
            t.Check(heldTarget != null && heldTarget.Payload is Projectile_MissileIncoming p && p.strikeWave == 1 && p.config != null && p.attacker == Enemy,
                $"the BVR-held old missile is a projectile after loading ({heldTarget?.Payload?.GetType().Name ?? "none"})");
            // C and D: the unchanged skyfaller classes still finish their ascent.
            yield return new WaitUntil(() => !Home.listerThings.AllThings.Any(x => x is ScorerProjectile || x is InterceptProjectile), 600);
            WorldObject track = Find.WorldObjects.AllWorldObjects.FirstOrDefault(x => x.ID == woId);
            t.Check(track is ScorerProjectile_WorldObject, "the old departure skyfaller still hands its track to the world");
            t.Check(!Home.listerThings.AllThings.Any(x => x is InterceptProjectile), "the old interceptor skyfaller still leaves the map");

            // Cleanup: no impacts on the test map, no stray track.
            if (track != null && !track.Destroyed) { track.Destroy(); }
            foreach (Projectile_MissileIncoming m in Home.listerThings.AllThings.OfType<Projectile_MissileIncoming>().ToList()) { m.Destroy(DestroyMode.Vanish); }
            HomeBVR.Waves.RemoveAll(w => w.targets.Any(x => x.id == heldTargetId));
            WorldComponent_StrikeReports.Current.Close(fake);
        }

        // ==================================================================
        //  F9: high-angle shields (docs/Missile_Skyfaller轉Projectile_計畫.md §6)
        // ==================================================================

        private static MissileBodyDef LightBody => DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Light");

        private static Thing SpawnShield(string defName, IntVec3 cell, Faction faction)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null) { return null; }
            Thing shield = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            shield.SetFaction(faction);
            GenSpawn.Spawn(shield, cell, Home, Rot4.North, WipeMode.Vanish);
            Track(shield);
            return shield;
        }

        /// <summary>An incoming missile from <paramref name="heading"/> at <paramref name="target"/>, outside BVR.</summary>
        private static Projectile_MissileIncoming Incoming(IntVec3 target, float heading, Faction attacker, Thing launcher, MissileConfig config = null)
        {
            ThingDef def = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Light").incomingSkyfaller;
            var m = (Projectile_MissileIncoming)MissileProjectileMaker.SpawnIncoming(def, target, Home, heading);
            m.bvrHandled = true;
            m.attacker = attacker;
            m.config = config;
            m.SetLauncher(launcher);
            return m;
        }

        private static readonly System.Reflection.FieldInfo InitiatorThingField =
            typeof(BattleLogEntry_ExplosionImpact).GetField("initiatorThing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// Battle-log explosion entries since <paramref name="since"/> whose initiator is <paramref name="instigator"/>'s def.
        /// Vanilla keeps a non-pawn initiator only as its ThingDef (Concerns() matches pawns only), so compare the def.
        /// </summary>
        private static int LoggedExplosionsBy(Thing instigator, int since)
        {
            if (instigator == null) { return 0; }
            return Find.BattleLog.Battles.SelectMany(b => b.Entries).OfType<BattleLogEntry_ExplosionImpact>()
                .Count(e => e.Timestamp >= since && InitiatorThingField?.GetValue(e) as ThingDef == instigator.def);
        }

        /// <summary>
        /// An enemy pawn lying on <paramref name="cell"/> (anaesthetised so it cannot wander off before the impact;
        /// anaesthesia is not violence, so it leaves lastHarmTick alone).
        /// </summary>
        private static Pawn VictimAt(IntVec3 cell, Map map)
        {
            Pawn victim = SpawnFactionPawn(Enemy, cell, map);
            victim.health.AddHediff(HediffDefOf.Anesthetic);
            return victim;
        }

        public static IEnumerable<Wait> ShieldInterception(TestContext t)
        {
            IntVec3 center = SafeImpactCell;
            float heading = 225f; // from the south-west: the whole approach crosses the map, then the shield ring
            Thing shield = SpawnShield("ShieldGeneratorMortar", center, Player);
            if (shield == null)
            {
                t.Note("Royalty's ShieldGeneratorMortar is not loaded; shield interception not tested");
                yield break;
            }
            CompProjectileInterceptor comp = shield.TryGetComp<CompProjectileInterceptor>();
            var waitActive = new WaitUntil(() => comp.Active, 2000);
            yield return waitActive;
            t.Require(!waitActive.TimedOut, $"the high-angle shield is up (radius {comp.Props.radius})");
            int hpBefore = comp.currentHitPoints;

            // 1) Enemy missile without a launcher vs the player's high-angle shield: stopped on the ring.
            int fake = WorldComponent_StrikeReports.Current.NewMissionId();
            WorldComponent_StrikeReports.Current.RecordLaunch(fake, 0, "test", "test");
            Projectile_MissileIncoming enemy = Incoming(center, heading, Enemy, null, new MissileConfig(LightBody));
            enemy.strikeMissionId = fake; enemy.strikeWave = 0;
            var waitHit = new WaitUntil(() => enemy.Destroyed, 1500);
            yield return waitHit;
            t.Check(enemy.ShieldContactCell.IsValid, $"the enemy missile is stopped by the shield (contact {enemy.ShieldContactCell})");
            if (enemy.ShieldContactCell.IsValid)
            {
                float r = enemy.ShieldContactCell.DistanceTo(center);
                t.Check(Mathf.Abs(r - comp.Props.radius) <= 2.5f, $"it explodes on the shield ring ({r:0.#} cells from the projector, radius {comp.Props.radius})");
                Vector3 dir = (enemy.ShieldContactCell - center).ToVector3();
                t.Check(Mathf.Abs(Mathf.DeltaAngle(MissileMapEntryUtility.HeadingOf(dir), heading)) < 10f, "on the side the missile came from");
            }
            if (comp.HitPointsMax > 0)
            {
                t.Check(comp.currentHitPoints < hpBefore || comp.Charging, $"the shield takes the hit ({hpBefore} → {comp.currentHitPoints} HP)");
            }
            else
            {
                // Mech high-angle shields have no hit points; check the shield registered the interception (cone, effecter).
                int lastIntercept = (int)typeof(CompProjectileInterceptor).GetField("lastInterceptTicks",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(comp);
                t.Check(Find.TickManager.TicksGame - lastIntercept < 60, $"the shield registers the interception (no hit points on this shield; last intercept {Find.TickManager.TicksGame - lastIntercept} ticks ago)");
            }
            StrikeReport report = WorldComponent_StrikeReports.Current.ReportFor(fake);
            t.Check(report != null && report.shieldBlocked.Count > 0 && report.shieldBlocked[0] == 1 && report.hit.Sum() == 0,
                "the strike report counts it as stopped by a shield, not a hit");
            WorldComponent_StrikeReports.Current.Close(fake);

            // 2) Player missile (with a player launcher) vs the player's own shield: passes.
            yield return new WaitUntil(() => comp.Active, 2500);
            Thing playerLauncher = SpawnBuilding(LightLauncher, 2, 2);
            Projectile_MissileIncoming own = Incoming(center, heading, Player, playerLauncher);
            TickWatch ownDone = DisarmBeforeImpact(new[] { (Projectile_MissileBase)own });
            yield return new WaitUntil(() => ownDone.Seen, 1500);
            t.Check(!own.ShieldContactCell.IsValid, "the player's own shield lets the player's missile through");

            // 3) The same player missile vs an enemy high-angle shield: stopped, and the explosion is not attributed.
            shield.SetFaction(Enemy);
            yield return new WaitUntil(() => comp.Active, 2500);
            int since = Find.TickManager.TicksGame;
            Projectile_MissileIncoming player = Incoming(center, heading, Player, playerLauncher);
            yield return new WaitUntil(() => player.Destroyed, 1500);
            t.Check(player.ShieldContactCell.IsValid, "an enemy high-angle shield stops a player missile (launcher-based check)");
            t.Check(LoggedExplosionsBy(playerLauncher, since) == 0, "the shield-stopped explosion is not attributed to the launcher");

            // 4) A ground (bullet) shield does not stop missiles.
            shield.Destroy(DestroyMode.Vanish);
            Thing ground = SpawnShield("ShieldGeneratorBullets", center, Player);
            if (ground != null)
            {
                CompProjectileInterceptor gc = ground.TryGetComp<CompProjectileInterceptor>();
                yield return new WaitUntil(() => gc.Active, 2000);
                Projectile_MissileIncoming low = Incoming(center, heading, Enemy, null);
                TickWatch lowDone = DisarmBeforeImpact(new[] { (Projectile_MissileBase)low });
                yield return new WaitUntil(() => lowDone.Seen, 1500);
                t.Check(gc.Active && !low.ShieldContactCell.IsValid, "a ground (bullet) shield does not stop missiles");
            }
        }

        // ==================================================================
        //  F10: launcher attribution (docs/Missile_Skyfaller轉Projectile_計畫.md §7)
        // ==================================================================

        private static Pawn SpawnFactionPawn(Faction faction, IntVec3 cell, Map map)
        {
            PawnKindDef kind = faction.def.basicMemberKind ?? PawnKindDefOf.Villager;
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, faction, mustBeCapableOfViolence: false));
            GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);
            Track(pawn);
            return pawn;
        }

        /// <summary>
        /// The victim is harmed when the blast (after the guidance scatter moved it) reaches its cell; a scattered miss is only noted,
        /// since the battle-log check already proves the attribution.
        /// </summary>
        private static void CheckVictim(TestContext t, Pawn victim, Projectile_MissileIncoming missile, MissileConfig config, int since, string who)
        {
            IntVec3 blast = missile.LastImpactCell;
            float distance = blast.IsValid ? blast.DistanceTo(victim.PositionHeld) : float.MaxValue;
            bool harmed = victim.Dead || victim.mindState.lastHarmTick >= since;
            if (distance <= config.ExplosionRadius - 1f)
            {
                t.Check(harmed, $"{who} inside the blast is harmed ({distance:0.#} cells from {blast}, radius {config.ExplosionRadius:0.#})");
            }
            else
            {
                t.Note($"{who} was {distance:0.#} cells from the scattered blast at {blast} (radius {config.ExplosionRadius:0.#}, scatter {config.Scatter:0.#}); harmed: {harmed}");
            }
        }

        public static IEnumerable<Wait> LauncherAttribution(TestContext t)
        {
            Thing launcher = SpawnBuilding(LightLauncher, 2, 2);
            MissileConfig config = MissileConfig.NewWithDefaults(LightBody);
            t.Require(config != null && config.Valid, "a valid light missile config");

            // Same map: an enemy pawn at the impact point; the explosion names the launcher in the battle log.
            IntVec3 target = SafeImpactCell;
            int since = Find.TickManager.TicksGame;
            Pawn victim = VictimAt(target, Home);
            Projectile_MissileIncoming m = Incoming(target, 45f, Player, launcher, config);
            t.Check(m.Launcher == launcher && m.Instigator == launcher, "the incoming missile carries its launching building");
            yield return new WaitUntil(() => m.Destroyed, 1500);
            t.Check(!m.ShieldContactCell.IsValid, "no shield involved");
            t.Check(LoggedExplosionsBy(launcher, since) > 0, $"the impact is logged against the launching building ({launcher.def.defName})");
            CheckVictim(t, victim, m, config, since, "the enemy pawn at the impact point");

            // Neutral faction hit from the player's launcher: goodwill drops (vanilla damage attribution).
            Faction neutral = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => !f.IsPlayer && !f.HostileTo(Player)
                && f.def.humanlikeFaction && f.def.basicMemberKind != null && f.CanChangeGoodwillFor(Player, -5));
            if (neutral != null)
            {
                IntVec3 neutralCell = target + new IntVec3(-12, 0, 0);
                Pawn bystander = SpawnFactionPawn(neutral, neutralCell, Home);
                int goodwill = neutral.GoodwillWith(Player);
                Projectile_MissileIncoming stray = Incoming(neutralCell, 45f, Player, launcher, config);
                yield return new WaitUntil(() => stray.Destroyed, 1500);
                int after = neutral.GoodwillWith(Player);
                t.Check(after < goodwill || neutral.HostileTo(Player), $"hitting {neutral.Name}'s pawn costs goodwill ({goodwill} → {after})");
                t.Note($"bystander {(bystander.Dead ? "killed" : "harmed")}");
            }
            else
            {
                t.Note("no neutral humanlike faction to test goodwill with");
            }

            // Across maps: the launcher stays on the home map; the missile lands on a generated enemy base map.
            Site site = SpawnMissileBase(6f, 14f);
            t.Require(site != null, "an NPC missile base spawned");
            Map baseMap = MissileWorldUtility.ResolveArrivalMap(site.Tile, site, true);
            t.Require(baseMap != null, "its map is generated");
            GameComponent_MissileEngage.Comp.times.SetOrAdd(baseMap, Find.TickManager.TicksGame + GenDate.TicksPerDay * 2);
            IntVec3 far = CellFinder.RandomClosewalkCellNear(baseMap.Center, baseMap, 20);
            ThingDef def = LightBody.incomingSkyfaller;
            Pawn defender = VictimAt(far, baseMap);
            var remote = (Projectile_MissileIncoming)MissileProjectileMaker.SpawnIncoming(def, far, baseMap, 300f);
            remote.bvrHandled = true; remote.attacker = Player; remote.config = config; remote.SetLauncher(launcher);
            since = Find.TickManager.TicksGame;
            yield return new WaitUntil(() => remote.Destroyed, 1500);
            t.Check(LoggedExplosionsBy(launcher, since) > 0, "a hit on another map is still logged against the home-map launcher");
            CheckVictim(t, defender, remote, config, since, "the enemy pawn on the base map");

            // A destroyed launcher: no instigator rather than a dangling reference.
            Projectile_MissileIncoming orphan = Incoming(target, 45f, Player, launcher, config);
            launcher.Destroy(DestroyMode.Vanish);
            t.Check(orphan.Instigator == null, "once the launcher is gone the missile has no instigator");
            TickWatch orphanDone = DisarmBeforeImpact(new[] { (Projectile_MissileBase)orphan });
            yield return new WaitUntil(() => orphanDone.Seen, 1500);
        }

        // ==================================================================
        //  F7: incoming entry (docs/Missile_Skyfaller轉Projectile_計畫.md §8.4)
        // ==================================================================

        /// <summary>Destroys each missile one tick before it would impact (sampled every tick), so the test pad stays intact.</summary>
        private static TickWatch DisarmBeforeImpact(IEnumerable<Projectile_MissileBase> missiles)
        {
            List<Projectile_MissileBase> list = missiles.ToList();
            return AcceptanceDriver.Watch(() =>
            {
                bool all = true;
                foreach (Projectile_MissileBase m in list)
                {
                    if (m.Destroyed) { continue; }
                    if (m.Spawned && m.TicksToImpact <= 2) { m.Destroy(DestroyMode.Vanish); continue; }
                    all = false;
                }
                return all;
            });
        }

        public static IEnumerable<Wait> IncomingEntry(TestContext t)
        {
            IntVec3 target = Pad.CenterCell;
            var flights = new List<(string name, ThingDef def, Projectile_MissileIncoming missile, float heading)>();
            MissileFlightTrace.Begin();
            TickWatch disarmed;
            try
            {
                foreach (MissileBodyDef body in DefDatabase<MissileBodyDef>.AllDefsListForReading)
                {
                    ThingDef def = body.incomingSkyfaller;
                    if (!t.Check(def != null && typeof(Projectile_MissileIncoming).IsAssignableFrom(def.thingClass),
                        $"{body.defName}: incoming {def?.defName} is a Projectile_MissileIncoming")) { continue; }
                    foreach (float heading in Headings)
                    {
                        var m = (Projectile_MissileIncoming)MissileProjectileMaker.SpawnIncoming(def, target, Home, heading);
                        m.bvrHandled = true; // flight only: no BVR capture
                        flights.Add(($"{def.defName}/{heading:0}°", def, m, heading));
                    }
                }
                // Without a heading the old fall is unchanged: angle -33.7, ticks within ticksToImpactRange.
                ThingDef plainDef = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Cruise").incomingSkyfaller;
                var plain = (Projectile_MissileIncoming)MissileProjectileMaker.SpawnIncoming(plainDef, target, Home);
                plain.bvrHandled = true;
                MissileFlightExtension pf = plainDef.GetModExtension<MissileFlightExtension>();
                t.Check(!plain.HasEntryHeading && Mathf.Approximately(plain.angle, Projectile_MissileBase.DefaultAngle)
                    && plain.TicksToImpact >= pf.ticksToImpactRange.min && plain.TicksToImpact <= pf.ticksToImpactRange.max,
                    $"without a heading the fall is the old one (angle {plain.angle:0.#}, {plain.TicksToImpact} ticks)");

                disarmed = DisarmBeforeImpact(flights.Select(f => (Projectile_MissileBase)f.missile).Append(plain));
                WaitUntil done = new WaitUntil(() => disarmed.Seen, 1500);
                yield return done;
                t.Check(!done.TimedOut, $"all {flights.Count} incoming missiles reached their target");
            }
            finally
            {
                MissileFlightTrace.End();
            }

            int failed = 0;
            var stats = new StringBuilder();
            Vector3 c = target.ToVector3Shifted().WithY(0f);
            foreach (var f in flights)
            {
                List<MissileFlightTrace.Sample> s = MissileFlightTrace.For(f.missile);
                var problems = new List<string>();
                if (s.Count < 3) { problems.Add($"only {s.Count} samples"); }
                else
                {
                    // Starts outside the map and enters through the edge the heading points at.
                    if (MissileDepartureKinematics.InMap(s[0].pos, Home.Size)) { problems.Add($"starts inside the map at {Fmt(s[0].pos)}"); }
                    int enter = s.FindIndex(x => MissileDepartureKinematics.InMap(x.pos, Home.Size));
                    if (enter <= 0) { problems.Add("never enters the map from outside"); }
                    else
                    {
                        Vector3 before = s[enter - 1].pos;
                        Rot4 side = before.x < 0f ? Rot4.West : before.x >= Home.Size.x ? Rot4.East : before.z < 0f ? Rot4.South : Rot4.North;
                        Vector3 edge = MissileMapEntryUtility.EdgePoint(Home.Size, c, f.heading);
                        Rot4 expected = edge.x <= 0.01f ? Rot4.West : edge.x >= Home.Size.x - 0.01f ? Rot4.East : edge.z <= 0.01f ? Rot4.South : Rot4.North;
                        bool corner = Mathf.Min(edge.x, Home.Size.x - edge.x) < 3f && Mathf.Min(edge.z, Home.Size.z - edge.z) < 3f;
                        if (!corner && side != expected) { problems.Add($"enters through the {side} edge, heading points at {expected}"); }
                    }
                    // Straight line from the heading side, nose along the heading, closing in on the target.
                    float worstDir = 0f, worstNose = 0f;
                    for (int k = 0; k < s.Count - 1; k++)
                    {
                        Vector3 back = (s[k].pos - s[k + 1].pos).WithY(0f);
                        if (back.magnitude > 1e-3f) { worstDir = Mathf.Max(worstDir, Mathf.Abs(Mathf.DeltaAngle(MissileMapEntryUtility.HeadingOf(back), f.heading))); }
                        worstNose = Mathf.Max(worstNose, Mathf.Abs(Mathf.DeltaAngle(s[k].rotation, f.heading)));
                        if ((s[k + 1].pos.WithY(0f) - c).magnitude > (s[k].pos.WithY(0f) - c).magnitude + 1e-3f) { problems.Add($"moves away from the target at sample {k}"); break; }
                    }
                    if (worstDir > 0.5f) { problems.Add($"flight direction off the heading by {worstDir:0.##}°"); }
                    if (worstNose > 0.5f) { problems.Add($"nose off the heading by {worstNose:0.##}°"); }
                    float miss = (s[s.Count - 1].pos.WithY(0f) - c).magnitude;
                    if (miss > 4f) { problems.Add($"last sample {miss:0.#} cells from the target"); }
                    int expectTicks = MissileArrivalKinematics.EntryTicks(f.def.GetModExtension<MissileFlightExtension>(), Home.Size, target, f.heading);
                    if (s[0].ticksToImpact != expectTicks - 1) { problems.Add($"fall starts at {s[0].ticksToImpact + 1} ticks, expected {expectTicks}"); }
                    if (stats.Length < 3000) { stats.AppendLine($"{f.name}: {expectTicks} ticks, enters at sample {enter}"); }
                }
                if (problems.Count > 0)
                {
                    failed++;
                    t.Check(false, $"{f.name}: {string.Join(" | ", problems.Take(4))}");
                }
            }
            t.Check(failed == 0, $"{flights.Count - failed}/{flights.Count} incoming missiles: start off-map, enter through the heading's edge, fly straight on the heading, reach the target");
            t.Note(stats.ToString().TrimEnd());

            // BVR hold and release: the missile goes into a terminal wave (no CIWS = released at once) and respawns with its heading.
            ThingDef bvrDef = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Light").incomingSkyfaller;
            var held = (Projectile_MissileIncoming)MissileProjectileMaker.MakeIncoming(bvrDef);
            held.SetEntryHeading(135f);
            held.bvrHandled = true;
            held.worldPhaseDone = true;
            HomeBVR.RegisterTerminal(held, target, Player);
            t.Check(!held.Spawned && HomeBVR.Waves.Any(w => w.targets.Any(x => x.Payload == held)), "BVR holds the unspawned incoming missile");
            // Sampled every tick: at Ultrafast several ticks pass between frames, so count the ticks since the release.
            TickWatch dropped = AcceptanceDriver.Watch(() => held.Spawned);
            var waitDrop = new WaitUntil(() => dropped.Seen, 300);
            yield return waitDrop;
            t.Check(!waitDrop.TimedOut && held.Position == target, "released from the wave, it respawns on its target cell");
            int expect = MissileArrivalKinematics.EntryTicks(bvrDef.GetModExtension<MissileFlightExtension>(), Home.Size, target, 135f);
            int fallen = Find.TickManager.TicksGame - dropped.FirstTick;
            t.Check(held.HasEntryHeading && Mathf.Approximately(held.angle, 135f) && Mathf.Abs(held.TicksToImpact + fallen - expect) <= 1,
                $"the respawned missile still flies in on its heading ({held.angle:0.#}°, {held.TicksToImpact} + {fallen} elapsed vs {expect} ticks)");
            TickWatch heldDone = DisarmBeforeImpact(new[] { (Projectile_MissileBase)held });
            yield return new WaitUntil(() => heldDone.Seen, 600);
        }

        // ==================================================================
        //  F8: screenshots of an arrival from the north-east
        // ==================================================================

        public static IEnumerable<Wait> IncomingScreenshots(TestContext t)
        {
            ThingDef def = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_AdvancedCruise").incomingSkyfaller;
            IntVec3 target = Pad.CenterCell;
            var missile = (Projectile_MissileIncoming)MissileProjectileMaker.SpawnIncoming(def, target, Home, 45f);
            missile.bvrHandled = true;
            TickWatch disarmed = DisarmBeforeImpact(new[] { (Projectile_MissileBase)missile });
            int total = missile.TicksToImpact;
            t.allowPause = true;
            try
            {
                // Falling at ~1.3 cells/tick: 30 ticks out is ~40 cells away, just inside a 40-cell view.
                foreach (int left in new[] { 30, 18, 8 })
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    yield return new WaitUntil(() => missile.Destroyed || missile.TicksToImpact <= left, 900);
                    if (missile.Destroyed) { break; }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.CameraDriver.SetRootPosAndSize(target.ToVector3Shifted() + new Vector3(12f, 0f, 12f), 40f);
                    yield return new WaitFrames(4);
                    t.Screenshot($"cruise_from_NE_{missile.TicksToImpact}_of_{total}");
                }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            yield return new WaitUntil(() => disarmed.Seen, 900);
            t.Check(disarmed.Seen, "the arrival reached its target");
        }

        // ==================================================================
        //  F5: departure cruise (docs/Missile_Skyfaller轉Projectile_計畫.md §8.3)
        // ==================================================================

        private static readonly float[] Headings = { 0f, 60f, 135f, 225f, 300f };

        public static IEnumerable<Wait> DepartureCruise(TestContext t)
        {
            var flights = new List<(string name, MissileBodyDef body, Projectile_MissileLaunch missile, float heading, IntVec3 cell, Rot4 rot)>();
            MissileFlightTrace.Begin();
            try
            {
                int i = 0;
                foreach (MissileBodyDef body in DefDatabase<MissileBodyDef>.AllDefsListForReading)
                {
                    if (body.launchProjectile == null) { continue; }
                    foreach (float heading in Headings)
                    {
                        Rot4 rot = Rotations[i % 4];
                        IntVec3 cell = Pad.CenterCell + new IntVec3((i % 9) - 4, 0, (i / 9 % 9) - 4);
                        var missile = (Projectile_MissileLaunch)MissileProjectileMaker.LaunchDeparture(body.launchProjectile, cell, Home, rot, null, null, heading);
                        flights.Add(($"{body.launchProjectile.defName}/{rot.ToStringHuman()}/{heading:0}°", body, missile, heading, cell, rot));
                        i++;
                    }
                }
                WaitUntil done = new WaitUntil(() => flights.All(f => f.missile.Destroyed), 3000);
                yield return done;
                t.Check(!done.TimedOut, $"all {flights.Count} departures left the map");
            }
            finally
            {
                MissileFlightTrace.End();
            }

            int failed = 0;
            var stats = new StringBuilder();
            foreach (var f in flights)
            {
                List<string> problems = CheckDeparture(f.body.launchProjectile, MissileFlightTrace.For(f.missile), f.heading, f.cell, f.rot, out string summary);
                if (problems.Count > 0)
                {
                    failed++;
                    t.Check(false, $"{f.name}: {string.Join(" | ", problems.Take(4))}");
                }
                else if (stats.Length < 4000)
                {
                    stats.AppendLine($"{f.name}: {summary}");
                }
            }
            t.Check(failed == 0, $"{flights.Count - failed}/{flights.Count} departures: correct exit heading, speed on the speed curve, continuous handoff, exhaust until exit, estimate = actual");
            t.Note(stats.ToString().TrimEnd());
        }

        private static List<string> CheckDeparture(ThingDef def, List<MissileFlightTrace.Sample> s, float heading, IntVec3 cell, Rot4 rot, out string summary)
        {
            var problems = new List<string>();
            summary = "";
            MissileFlightExtension f = def.GetModExtension<MissileFlightExtension>();
            int handoff = s.FindIndex(x => x.cruising);
            if (handoff < 1) { problems.Add($"no handoff to cruise (index {handoff})"); return problems; }
            if (s.Skip(handoff).Any(x => !x.cruising)) { problems.Add("left cruise before exiting"); }

            // Exit: the last pose is outside the map, along the heading.
            Vector3 last = s[s.Count - 1].pos;
            if (MissileDepartureKinematics.InMap(last, Home.Size)) { problems.Add($"last pose {Fmt(last)} still inside the map"); }
            int n = s.Count;
            if (n - handoff > f.turnTicks + 2)
            {
                float exitDir = MissileMapEntryUtility.HeadingOf(s[n - 1].pos - s[n - 2].pos);
                if (Mathf.Abs(Mathf.DeltaAngle(exitDir, heading)) > 0.5f) { problems.Add($"exit direction {exitDir:0.#}° ≠ heading {heading:0}°"); }
                if (Mathf.Abs(Mathf.DeltaAngle(s[n - 1].rotation, heading)) > 0.5f) { problems.Add($"nose {s[n - 1].rotation:0.#}° ≠ heading at exit"); }
            }
            Rot4 exitEdge = last.x < 0f ? Rot4.West : last.x >= Home.Size.x ? Rot4.East : last.z < 0f ? Rot4.South : Rot4.North;
            Vector3 edge = MissileMapEntryUtility.EdgePoint(Home.Size, s[handoff].pos.WithY(0f), heading);
            Rot4 expectedEdge = edge.x <= 0.01f ? Rot4.West : edge.x >= Home.Size.x - 0.01f ? Rot4.East : edge.z <= 0.01f ? Rot4.South : Rot4.North;
            bool corner = Mathf.Min(Mathf.Min(edge.x, Home.Size.x - edge.x), Mathf.Min(edge.z, Home.Size.z - edge.z)) > 0.01f
                || (Mathf.Min(edge.x, Home.Size.x - edge.x) < 8f && Mathf.Min(edge.z, Home.Size.z - edge.z) < 8f);
            if (!corner && exitEdge != expectedEdge) { problems.Add($"exits the {exitEdge} edge, heading points at the {expectedEdge} edge"); }

            // Speed: after the turn, per-tick speed follows speedCurve(progress) / speedCurve(progress at handoff).
            int leave = f.ticksToDiscardInReverse.min;
            float worstRatio = 0f, maxSpeed = 0f, speedAtHandoff = (s[handoff].pos - s[handoff - 1].pos).WithY(0f).magnitude;
            for (int k = handoff + f.turnTicks + 2; k < n - 1; k++)
            {
                float v0 = (s[k].pos - s[k - 1].pos).WithY(0f).magnitude;
                float v1 = (s[k + 1].pos - s[k].pos).WithY(0f).magnitude;
                maxSpeed = Mathf.Max(maxSpeed, v1);
                if (v0 <= f.minCruiseSpeed + 1e-3f || v1 >= f.maxCruiseSpeed - 1e-3f || v0 >= f.maxCruiseSpeed - 1e-3f) { continue; } // clamped
                if (f.speedCurve == null) { worstRatio = Mathf.Max(worstRatio, Mathf.Abs(v1 / v0 - 1f)); continue; }
                float c0 = f.speedCurve.Evaluate((float)s[k].ticksToImpact / leave);
                float c1 = f.speedCurve.Evaluate((float)s[k + 1].ticksToImpact / leave);
                worstRatio = Mathf.Max(worstRatio, Mathf.Abs(v1 / v0 - c1 / c0));
            }
            if (worstRatio > 1e-3f) { problems.Add($"cruise speed departs from the speed curve by {worstRatio:0.####}"); }

            // Continuity: no jump in step length or direction at the handoff.
            float worstJump = 0f, worstTurn = 0f;
            for (int k = Math.Max(1, handoff - 3); k < Math.Min(n - 1, handoff + f.turnTicks + 2); k++)
            {
                Vector3 d0 = (s[k].pos - s[k - 1].pos).WithY(0f), d1 = (s[k + 1].pos - s[k].pos).WithY(0f);
                if (d0.magnitude < 1e-3f || d1.magnitude < 1e-3f) { continue; }
                worstJump = Mathf.Max(worstJump, d1.magnitude / Mathf.Max(d0.magnitude, f.minCruiseSpeed));
                worstTurn = Mathf.Max(worstTurn, Vector3.Angle(d0, d1));
            }
            if (worstJump > 2f) { problems.Add($"step length jumps ×{worstJump:0.##} around the handoff"); }
            if (worstTurn > 30f) { problems.Add($"direction jumps {worstTurn:0.#}° in one tick around the handoff"); }

            // Exhaust stays lit through the cruise while the missile is on the map (after ignition, if it has an effect).
            int ignition = s.FindIndex(x => x.ignited);
            if (ignition >= 0)
            {
                int dark = 0;
                for (int k = Math.Max(handoff, ignition) + 1; k < n - 1; k++) { if (!s[k].exhaust) { dark++; } }
                if (dark > 0) { problems.Add($"exhaust off on {dark} cruise ticks"); }
                float held = s.Skip(Math.Max(handoff, ignition) + 2).Where(x => x.exhaust).Select(x => x.evaluate).DefaultIfEmpty(-1f).Max()
                    - s.Skip(Math.Max(handoff, ignition) + 2).Where(x => x.exhaust).Select(x => x.evaluate).DefaultIfEmpty(-1f).Min();
                if (held > 1e-6f) { problems.Add("exhaust progress keeps moving during the cruise"); }
            }

            // The strike timeline's estimate equals the real flight.
            int estimate = MissileProjectileMaker.DepartureTicksOf(def, cell, Home.Size, rot, heading);
            if (estimate != n) { problems.Add($"estimate {estimate} ticks, actual {n}"); }

            summary = $"handoff at tick {handoff}, exit after {n} ticks via {exitEdge}, speed {speedAtHandoff:0.##}→{maxSpeed:0.##} cells/tick";
            return problems;
        }

        // ==================================================================
        //  F6: screenshots of a turning departure
        // ==================================================================

        public static IEnumerable<Wait> DepartureScreenshots(TestContext t)
        {
            ThingDef def = DefDatabase<MissileBodyDef>.GetNamed("DMSE_MissileBody_Light").launchProjectile;
            IntVec3 cell = Pad.CenterCell;
            int[] at = { 20, 40, 60, 80, 100 };
            t.allowPause = true;
            try
            {
                Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(-10f, 0f, 10f), 40f);
                Thing missile = MissileProjectileMaker.LaunchDeparture(def, cell, Home, Rot4.East, null, null, 315f);
                int start = Find.TickManager.TicksGame;
                foreach (int tick in at)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    yield return new WaitUntil(() => Find.TickManager.TicksGame - start >= tick || missile.Destroyed, 600);
                    if (missile.Destroyed) { break; }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(-10f, 0f, 10f), 40f);
                    yield return new WaitFrames(4);
                    t.Screenshot($"light_E_to_NW_t{Find.TickManager.TicksGame - start}");
                }
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                WaitUntil gone = new WaitUntil(() => missile.Destroyed, 2000);
                yield return gone;
                t.Check(!gone.TimedOut, "the missile flew out of the map");
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // ==================================================================
        //  F4: one launch projectile per body
        // ==================================================================

        /// <summary>Bodies whose launch projectile copies a platform launch skyfaller verbatim: must replay it tick for tick.</summary>
        private static readonly (string body, string platformSkyfaller)[] VerbatimCopies =
        {
            ("DMSE_MissileBody_Ballistic", "DMSE_SkyFaller_BallisticMissile"),
            ("DMSE_MissileBody_Light", "DMSE_SkyFaller_LightMissile"),
            ("DMSE_MissileBody_Loiter", "DMSE_SkyFaller_Loiter"),
            ("DMSE_MissileBody_Cruise", "DMSE_SkyFaller_CruiseMissile"),
        };

        public static IEnumerable<Wait> BodyLaunchProjectiles(TestContext t)
        {
            List<MissileBodyDef> bodies = DefDatabase<MissileBodyDef>.AllDefsListForReading;
            t.Check(bodies.Count >= 16, $"{bodies.Count} missile bodies loaded");
            HashSet<ThingDef> distinct = new HashSet<ThingDef>();
            var launched = new List<(MissileBodyDef body, Projectile_MissileLaunch missile)>();
            var pairs = new List<Pair>();
            MissileFlightTrace.Begin();
            try
            {
                int i = 0;
                foreach (MissileBodyDef body in bodies)
                {
                    ThingDef def = body.launchProjectile;
                    if (!t.Check(def != null && typeof(Projectile_MissileLaunch).IsAssignableFrom(def.thingClass),
                        $"{body.defName} has its own launch projectile ({def?.defName ?? "none"})")) { continue; }
                    distinct.Add(def);
                    Texture tex = def.graphic?.MatSingle?.mainTexture;
                    t.Check(tex != null && tex != BaseContent.BadTex, $"{def.defName} texture {def.graphicData?.texPath} loads");
                    t.Check(def.GetModExtension<MissileFlightExtension>()?.reversed == true, $"{def.defName} flies an ascent");
                    t.Check(def.comps.Any(c => c is CompProperties_EffecterLaunch), $"{def.defName} has a launch effect");

                    // The strike path picks the body's projectile over the platform skyfaller.
                    MissileConfig config = MissileConfig.NewWithDefaults(body);
                    CompMissileLauncher_WorldTargeting anyLauncher = Home.listerThings.AllThings.OfType<ThingWithComps>()
                        .Select(x => x.GetComp<CompMissileLauncher_WorldTargeting>()).FirstOrDefault(c => c != null);
                    if (anyLauncher != null)
                    {
                        t.Check(anyLauncher.LaunchDefFor(config) == def, $"{body.defName}: launchers fire {def.defName}");
                    }

                    IntVec3 cell = new IntVec3(Pad.minX + 3 + (i % 6) * 6, 0, Pad.minZ + 3 + (i / 6) * 10);
                    Thing missile = MissileProjectileMaker.LaunchDeparture(def, cell, Home, Rot4.East, null, null);
                    launched.Add((body, (Projectile_MissileLaunch)missile));
                    i++;
                }

                // Verbatim copies vs the platform skyfallers they replace, on fresh cells.
                int j = 0;
                foreach (var (bodyName, skyfallerName) in VerbatimCopies)
                {
                    ThingDef def = DefDatabase<MissileBodyDef>.GetNamed(bodyName).launchProjectile;
                    ThingDef old = ThingDef.Named(skyfallerName);
                    foreach (Rot4 rot in Rotations)
                    {
                        IntVec3 cell = new IntVec3(Pad.minX + 3 + (j % 8) * 4, 0, Pad.maxZ - 3 - (j / 8) * 6);
                        pairs.Add(new Pair
                        {
                            name = $"{def.defName}/{rot.ToStringHuman()}",
                            oldMissile = SpawnOld(old, cell, rot),
                            newMissile = MissileProjectileMaker.SpawnAscent(def, cell, Home, rot, null),
                        });
                        j++;
                    }
                }
                t.Check(distinct.Count == launched.Count, $"{distinct.Count} distinct launch projectiles for {launched.Count} bodies");

                WaitUntil done = new WaitUntil(() => launched.All(x => x.missile.Destroyed)
                    && pairs.All(p => p.oldMissile.Destroyed && p.newMissile.Destroyed), 1500);
                yield return done;
                t.Check(!done.TimedOut, "every launched missile climbed out of the map");
            }
            finally
            {
                MissileFlightTrace.End();
            }

            foreach (var (body, missile) in launched)
            {
                List<MissileFlightTrace.Sample> trace = MissileFlightTrace.For(missile);
                int ignition = trace.FindIndex(s => s.ignited);
                int exhaust = trace.Count(s => s.exhaust);
                t.Check(ignition >= 0 && exhaust > 0, $"{missile.def.defName}: ignites at tick {ignition}, exhaust on {exhaust} ticks");
            }
            foreach (Pair p in pairs)
            {
                List<string> diffs = ComparePair(MissileFlightTrace.For(p.oldMissile), MissileFlightTrace.For(p.newMissile), out _);
                t.Check(diffs.Count == 0, diffs.Count == 0
                    ? $"{p.name} replays the platform skyfaller tick for tick"
                    : $"{p.name}: {diffs.Count} mismatch(es); first: {string.Join(" | ", diffs.Take(3))}");
            }
        }

        // ==================================================================
        //  F3: world headings (docs/Missile_Skyfaller轉Projectile_計畫.md §5.3)
        // ==================================================================

        public static IEnumerable<Wait> WorldHeadings(TestContext t)
        {
            PlanetTile home = Home.Tile;
            PlanetLayer layer = home.Layer;
            Vector3 end = MissileWorldUtility.TileCenter(home);
            int count = Find.WorldGrid.TilesCount;
            int pairs = 0, tangentFails = 0, edgeFails = 0;
            float worst = 0f;
            Rand.PushState(20261021);
            try
            {
                for (int k = 0; k < 400 && pairs < 120; k++)
                {
                    PlanetTile other = new PlanetTile(Rand.Range(0, count), layer);
                    if (other == home) { continue; }
                    Vector3 start = MissileWorldUtility.TileCenter(other);
                    if (!MissileMapEntryUtility.HasHorizontalHeading(layer, layer, start, end)) { continue; }
                    pairs++;

                    // The world track is Slerp(start, end); its last 0.5 % shows the real arrival direction.
                    float entry = MissileMapEntryUtility.EntryHeading(layer, start, end);
                    Vector3 nearEnd = Vector3.Slerp(start, end, 0.995f);
                    float local = layer.GetHeadingFromTo(end, nearEnd);
                    float delta = Mathf.Abs(Mathf.DeltaAngle(entry, local));
                    worst = Mathf.Max(worst, delta);
                    if (delta > 2f) { tangentFails++; }

                    // Same edge as a vanilla caravan leaving home towards that tile.
                    if (MissileMapEntryUtility.EdgeRot(entry) != Find.WorldGrid.GetRotFromTo(home, other)) { edgeFails++; }
                }
            }
            finally
            {
                Rand.PopState();
            }
            t.Check(pairs >= 50, $"{pairs} tile pairs with a horizontal heading sampled");
            t.Check(tangentFails == 0, $"entry heading matches the track's arrival tangent within 2° ({tangentFails} misses, worst {worst:0.###}°)");
            t.Check(edgeFails == 0, $"entry edge equals PlanetLayer.GetRotFromTo for every pair ({edgeFails} differ)");

            // A rerouted track: the last leg starts mid-flight, so the heading must come from that point, not the launch tile.
            PlanetTile a = TileAtDistance(18f, 22f);
            t.Require(a.Valid, "a tile ~20 tiles away exists");
            Vector3 launch = MissileWorldUtility.TileCenter(a);
            Vector3 legStart = Vector3.Slerp(launch, end, 0.5f);
            legStart = (Quaternion.AngleAxis(25f, end.normalized) * legStart);   // swing the leg start sideways around the target
            float fromLaunch = MissileMapEntryUtility.EntryHeading(layer, launch, end);
            float fromLeg = MissileMapEntryUtility.EntryHeading(layer, legStart, end);
            t.Check(Mathf.Abs(Mathf.DeltaAngle(fromLaunch, fromLeg)) > 10f,
                $"a sideways reroute changes the entry heading ({fromLaunch:0.#}° → {fromLeg:0.#}°)");
            float legLocal = layer.GetHeadingFromTo(end, Vector3.Slerp(legStart, end, 0.995f));
            t.Check(Mathf.Abs(Mathf.DeltaAngle(fromLeg, legLocal)) < 2f, "the rerouted heading matches the last leg's arrival tangent");
            yield break;
        }

        /// <summary>Every ascent skyfaller def that ships (launch and interceptor), with and without launch effects.</summary>
        private static readonly string[] AscentDefs =
        {
            "DMSE_SkyFaller_LightMissile",      // hot launch
            "DMSE_SkyFaller_Loiter",            // hot launch
            "DMSE_SkyFaller_BallisticMissile",  // cold launch, 20-tick eject
            "DMSE_SkyFaller_CruiseMissile",     // cold launch, 40-tick eject
            "DMSE_SkyFaller_Interceptor",       // hot launch
            "DMSE_InterceptProjectile",         // no launch effect
        };

        private static readonly Rot4[] Rotations = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };

        private const float PosTolerance = 1e-4f;
        private const float AngleTolerance = 0.01f;
        private const float ValueTolerance = 1e-6f;

        private static readonly Dictionary<ThingDef, ThingDef> twins = new Dictionary<ThingDef, ThingDef>();

        /// <summary>
        /// A runtime projectile def equivalent to <paramref name="old"/>: same graphic, comps and altitude layer,
        /// its &lt;skyfaller&gt; copied into <see cref="MissileFlightExtension"/>. Not registered in the DefDatabase (never saved).
        /// </summary>
        public static ThingDef ProjectileTwin(ThingDef old)
        {
            if (twins.TryGetValue(old, out ThingDef twin)) { return twin; }
            twin = new ThingDef
            {
                defName = old.defName + "Twin",
                label = old.label,
                thingClass = typeof(Projectile_MissileBase),
                category = ThingCategory.Projectile,
                tickerType = TickerType.Normal,
                altitudeLayer = old.altitudeLayer,
                useHitPoints = false,
                neverMultiSelect = true,
                drawerType = DrawerType.RealtimeOnly,
                size = old.size,
                rotatable = old.rotatable,
                graphicData = old.graphicData,
                graphic = old.graphic,
                comps = old.comps,
                projectile = new ProjectileProperties { flyOverhead = true, speed = 1f, damageDef = DamageDefOf.Bomb },
                modExtensions = new List<DefModExtension> { MissileFlightExtension.FromSkyfaller(old.skyfaller) },
            };
            twins[old] = twin;
            return twin;
        }

        private sealed class Pair
        {
            public string name;
            public Thing oldMissile;
            public Projectile_MissileBase newMissile;
        }

        private static Thing SpawnOld(ThingDef def, IntVec3 cell, Rot4 rot)
        {
            // Same as CompMissileLauncher_WorldTargeting.TryLaunchRound / CompMissileLauncher_Interceptor.FireInterceptor.
            Skyfaller faller = SkyfallerMaker.SpawnSkyfaller(def, cell, Home);
            faller.Rotation = rot;
            faller.angle = rot.AsAngle;
            return faller;
        }

        // ==================================================================
        //  F1: tick-for-tick parity
        // ==================================================================

        public static IEnumerable<Wait> AscentParity(TestContext t)
        {
            List<Pair> pairs = new List<Pair>();
            MissileFlightTrace.Begin();
            try
            {
                int i = 0;
                foreach (string defName in AscentDefs)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    t.Require(def != null && def.skyfaller != null, $"{defName} exists and is a skyfaller");
                    ThingDef twin = ProjectileTwin(def);
                    foreach (Rot4 rot in Rotations)
                    {
                        // Old and new share a cell so map-bound checks (InBounds, ShouldSpawnMotesAt) see the same positions.
                        IntVec3 cell = new IntVec3(Pad.minX + 3 + (i % 6) * 6, 0, Pad.minZ + 3 + (i / 6) * 8);
                        Thing oldMissile = SpawnOld(def, cell, rot);
                        Projectile_MissileBase newMissile = MissileProjectileMaker.SpawnAscent(twin, cell, Home, rot, null);
                        pairs.Add(new Pair { name = $"{defName}/{rot.ToStringHuman()}", oldMissile = oldMissile, newMissile = newMissile });
                        i++;
                    }
                }
                t.Check(pairs.All(p => p.oldMissile.Spawned && p.newMissile.Spawned), $"{pairs.Count} old/new pairs spawned in the same tick");

                WaitUntil done = new WaitUntil(() => pairs.All(p => p.oldMissile.Destroyed && p.newMissile.Destroyed), 1500);
                yield return done;
                t.Check(!done.TimedOut, "every old and new missile left the map");
            }
            finally
            {
                MissileFlightTrace.End();
            }

            StringBuilder tsv = new StringBuilder();
            tsv.AppendLine("pair\tside\ttick\tticksToImpact\tpos.x\tpos.y\tpos.z\trot\tignited\texhaust\tevaluate\tspeed\texPos.x\texPos.z\texRot");
            int failedPairs = 0;
            float worstRenderLag = 0f;
            foreach (Pair p in pairs)
            {
                List<MissileFlightTrace.Sample> a = MissileFlightTrace.For(p.oldMissile);
                List<MissileFlightTrace.Sample> b = MissileFlightTrace.For(p.newMissile);
                AppendTrace(tsv, p.name, "old", a);
                AppendTrace(tsv, p.name, "new", b);

                List<string> diffs = ComparePair(a, b, out float renderLag);
                worstRenderLag = Mathf.Max(worstRenderLag, renderLag);
                if (diffs.Count > 0)
                {
                    failedPairs++;
                    t.Check(false, $"{p.name}: {diffs.Count} mismatch(es); first: {string.Join(" | ", diffs.Take(3))}");
                }
                else
                {
                    int exhaustTicks = b.Count(s => s.exhaust);
                    int ignition = b.FindIndex(s => s.ignited);
                    t.Check(true, $"{p.name}: {b.Count} ticks equal (ignition at tick {ignition}, exhaust on {exhaustTicks} ticks)");
                }
            }
            t.Check(failedPairs == 0, $"all {pairs.Count} pairs match tick for tick");
            t.Note($"Old skyfallers feed the exhaust a pose cached at the last render; its largest lag behind the computed pose was {worstRenderLag:0.###} cells (new projectiles compute it every tick).");

            if (AcceptanceDriver.OutDir != null)
            {
                string file = Path.Combine(AcceptanceDriver.OutDir, "missile_flight_trace.tsv");
                File.WriteAllText(file, tsv.ToString(), Encoding.UTF8);
                t.Note($"Trace written to {file}");
            }
        }

        /// <summary>Returns the mismatches between the old (<paramref name="a"/>) and new (<paramref name="b"/>) traces.</summary>
        private static List<string> ComparePair(List<MissileFlightTrace.Sample> a, List<MissileFlightTrace.Sample> b, out float renderLag)
        {
            List<string> diffs = new List<string>();
            renderLag = 0f;
            if (a.Count != b.Count) { diffs.Add($"tick count old={a.Count} new={b.Count}"); }
            int n = Math.Min(a.Count, b.Count);
            for (int k = 0; k < n; k++)
            {
                MissileFlightTrace.Sample o = a[k], w = b[k];
                string at = $"#{k}";
                if (o.tick != w.tick) { diffs.Add($"{at} game tick {o.tick} vs {w.tick}"); continue; }
                if (!o.hasTimeline || !w.hasTimeline) { diffs.Add($"{at} timeline missing old={o.hasTimeline} new={w.hasTimeline}"); continue; }
                if (o.ticksToImpact != w.ticksToImpact) { diffs.Add($"{at} ticksToImpact {o.ticksToImpact} vs {w.ticksToImpact}"); }
                if ((o.pos - w.pos).magnitude > PosTolerance) { diffs.Add($"{at} pos {Fmt(o.pos)} vs {Fmt(w.pos)}"); }
                if (Mathf.Abs(Mathf.DeltaAngle(o.rotation, w.rotation)) > AngleTolerance) { diffs.Add($"{at} rot {o.rotation:0.###} vs {w.rotation:0.###}"); }
                if (o.ignited != w.ignited) { diffs.Add($"{at} ignited {o.ignited} vs {w.ignited}"); }
                if (o.exhaust != w.exhaust) { diffs.Add($"{at} exhaust {o.exhaust} vs {w.exhaust}"); continue; }
                if (!w.exhaust) { continue; }
                if (Mathf.Abs(o.evaluate - w.evaluate) > ValueTolerance) { diffs.Add($"{at} evaluate {o.evaluate:0.######} vs {w.evaluate:0.######}"); }
                if (Mathf.Abs(o.speed - w.speed) > ValueTolerance) { diffs.Add($"{at} speed {o.speed:0.######} vs {w.speed:0.######}"); }

                // The exhaust runs in CompTick, before this tick advances the timeline: its pose is the previous tick's.
                if (k > 0)
                {
                    MissileFlightTrace.Sample prevNew = b[k - 1], prevOld = a[k - 1];
                    if ((w.exhaustPos - prevNew.pos).magnitude > PosTolerance) { diffs.Add($"{at} new exhaust pos {Fmt(w.exhaustPos)} is not the pose {Fmt(prevNew.pos)}"); }
                    if (Mathf.Abs(Mathf.DeltaAngle(w.exhaustRotation, prevNew.rotation)) > AngleTolerance) { diffs.Add($"{at} new exhaust rot {w.exhaustRotation:0.###} is not the pose {prevNew.rotation:0.###}"); }
                    renderLag = Mathf.Max(renderLag, (o.exhaustPos - prevOld.pos).magnitude);
                }
            }
            return diffs;
        }

        private static string Fmt(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.####},{1:0.####},{2:0.####})", v.x, v.y, v.z);

        private static void AppendTrace(StringBuilder sb, string pair, string side, List<MissileFlightTrace.Sample> samples)
        {
            foreach (MissileFlightTrace.Sample s in samples)
            {
                sb.AppendLine(string.Join("\t", pair, side, s.tick, s.ticksToImpact,
                    F(s.pos.x), F(s.pos.y), F(s.pos.z), F(s.rotation), s.ignited ? 1 : 0, s.exhaust ? 1 : 0,
                    F(s.evaluate), F(s.speed), F(s.exhaustPos.x), F(s.exhaustPos.z), F(s.exhaustRotation)));
            }
        }

        private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

        // ==================================================================
        //  F2: screenshots (visual baseline)
        // ==================================================================

        public static IEnumerable<Wait> AscentScreenshots(TestContext t)
        {
            // Hot launch sideways (z and rotation curves), cold launch upright (eject delay), interceptor sideways.
            var shots = new (string def, Rot4 rot, string tag)[]
            {
                ("DMSE_SkyFaller_LightMissile", Rot4.East, "light_E"),
                ("DMSE_SkyFaller_BallisticMissile", Rot4.North, "ballistic_N"),
                ("DMSE_SkyFaller_CruiseMissile", Rot4.West, "cruise_W"),
                ("DMSE_SkyFaller_Interceptor", Rot4.East, "sam_E"),
            };
            int[] at = { 10, 30, 60 };
            IntVec3 cell = Pad.CenterCell;
            t.allowPause = true;
            try
            {
                foreach (var shot in shots)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(shot.def);
                    foreach (bool useNew in new[] { false, true })
                    {
                        Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(0f, 0f, 4f), 22f);
                        Thing missile = useNew
                            ? MissileProjectileMaker.SpawnAscent(ProjectileTwin(def), cell, Home, shot.rot, null)
                            : SpawnOld(def, cell, shot.rot);
                        int start = Find.TickManager.TicksGame;
                        foreach (int tick in at)
                        {
                            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                            yield return new WaitUntil(() => Find.TickManager.TicksGame - start >= tick, 600);
                            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                            // Re-frame while paused: the camera may still be easing from the previous shot.
                            Find.CameraDriver.SetRootPosAndSize(cell.ToVector3Shifted() + new Vector3(0f, 0f, 4f), 22f);
                            yield return new WaitFrames(4);
                            t.Screenshot($"{shot.tag}_{(useNew ? "new" : "old")}_t{Find.TickManager.TicksGame - start}");
                        }
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                        WaitUntil gone = new WaitUntil(() => missile.Destroyed, 1500);
                        yield return gone;
                        t.Check(!gone.TimedOut, $"{shot.tag} {(useNew ? "new" : "old")} left the map");
                    }
                }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }
    }
}
