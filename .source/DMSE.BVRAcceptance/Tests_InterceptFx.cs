using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Intercept debris FX, P2 (docs/攔截殘骸特效_實作與技術美術計畫.md §5): the airburst is drawn from the
    /// effects component's own fleck system at the on-route burst point, also beyond the map edge.
    /// Mostly screenshots for the art review; the checks only cover what the code can see.
    /// </summary>
    public static class Tests_InterceptFx
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("I1", "Intercept airburst screenshots: terminal/midcourse/drop pod at several headings, early and late", BurstScreenshots),
            new AcceptanceTest("I2", "Fleck render A/B: intercept smoke def via own system vs vanilla system vs vanilla dust puff", RenderAB),
            new AcceptanceTest("I3", "Major debris chunks: spawn with a hulk, save/load mid-flight, land or cull in time, damage only with the setting (roofs stop it), trail screenshots", MajorDebris),
            new AcceptanceTest("I4", "Drop-pod debris profile: no fireball, pod counts and tint, hulk draws the pod texture; missiles keep the missile profile", DropPodProfile),
            new AcceptanceTest("I5", "Intercept FX draw above fog of war: own materials between fog and the edge clipper, vanilla untouched, burst over fog screenshots", RenderQueueDiag),
            new AcceptanceTest("I6", "Intercept FX budget: density setting scales fragment counts; a 40-burst salvo stays under the live caps (frame time logged)", BudgetAndDensity),
            new AcceptanceTest("I7", "Smoke instancing A/B on the same 40-burst salvo (same seeds, same map): frame time with Mote instancing on vs off", SalvoInstancingAB),
            new AcceptanceTest("I8", "Remote terminal intercepts: approach-0 bursts from N/E/S/W are virtual points beyond the entry edge with no airburst; every chunk reaches the screen and fragments fade in; edge screenshots", RemoteTerminal),
        };

        // Far terminal intercepts (2026-10-27): the CIWS hits the target before it is over the map, so the burst is a virtual point
        // beyond the entry edge. No airburst is drawn; only debris that will reach the screen is thrown, fading in from the edge band.
        public static IEnumerable<Wait> RemoteTerminal(TestContext t)
        {
            Map map = Home;
            InterceptDebrisPhysics p = InterceptDebrisPhysics.Default;
            ThingDef missileDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
            int[] shotAges = { 30, 60, 90 };
            t.allowPause = true;
            try
            {
                foreach (float heading in new[] { 0f, 90f, 180f, 270f })
                {
                    string tag = "remote_from_" + "NESW"[(int)(heading / 90f)];
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                    yield return new WaitUntil(() => LiveFlecks(map) == 0 && (Debris(map)?.Count ?? 0) == 0 && Chunks(map).Count == 0, 900);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;

                    InterceptEventData data = InterceptEventData.Build(null, map.Center, map.Size, map.Center, true, IntVec3.Invalid, 0f, null,
                        Gen.HashCombineInt(Find.TickManager.TicksGame, (int)heading), p, heading);
                    data.payloadKind = InterceptPayloadKind.Missile;
                    data.payloadDef = missileDef;
                    data.targetSpeed = 0.8f;
                    Vector3 edge = MissileMapEntryUtility.EdgePoint(map.Size, map.Center.ToVector3Shifted(), heading);
                    float beyond = Vector3.Dot(data.burstGround - edge, MissileMapEntryUtility.HeadingVector(heading));
                    t.Note($"{tag}: burstGround=({data.burstGround.x:0.#}, {data.burstGround.z:0.#}) height={data.burstHeight:0.#} beyond edge {beyond:0.#}");
                    t.Check(data.remote && beyond >= -0.01f, $"{tag}: approach-0 terminal intercept is a virtual point beyond the entry edge");

                    MapComponent_InterceptEffects.Trigger(map, data);
                    t.Check(LiveFlecks(map) == 0, $"{tag}: no airburst flecks ({LiveFlecks(map)})");
                    InterceptDebrisFlecks debris = Debris(map);
                    List<InterceptDebrisChunk> chunks = Chunks(map);
                    t.Check(debris.Count > 0 && chunks.Count > 0, $"{tag}: debris that reaches the screen is thrown ({debris.Count} fragments, {chunks.Count} chunks)");
                    t.Check(chunks.All(ch => ch.remote) && debris.Pieces.All(f => f.remote && f.fleck.instanceColor.a <= 0f),
                        $"{tag}: remote debris starts transparent");

                    // Every chunk is seen (fade > 0) before it lands or is culled; fragments fade in to at least remoteMinFade.
                    var ids = new HashSet<int>(chunks.Select(ch => ch.thingIDNumber));
                    var seen = new HashSet<int>();
                    float maxFragmentAlpha = 0f;
                    int shot = 0;
                    int start = Find.TickManager.TicksGame;
                    while ((Chunks(map).Count > 0 || debris.Count > 0) && Find.TickManager.TicksGame - start < p.maxLifeTicks + 30)
                    {
                        foreach (InterceptDebrisChunk ch in Chunks(map))
                        {
                            if (InterceptGeometry.EdgeFade(map.Size, ch.state.DrawPos(p), p) > 0f) { seen.Add(ch.thingIDNumber); }
                        }
                        foreach (InterceptDebrisFlecks.Piece piece in debris.Pieces) { maxFragmentAlpha = Mathf.Max(maxFragmentAlpha, piece.fleck.instanceColor.a); }
                        if (shot < shotAges.Length && Find.TickManager.TicksGame - start >= shotAges[shot])
                        {
                            // Camera parked inside the entry edge: where the player sees the debris come in.
                            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                            Find.CameraDriver.SetRootPosAndSize(MissileMapEntryUtility.InsetFromEdge(map.Size, edge, 14f), 28f);
                            CloseLogWindow();
                            yield return new WaitFrames(6);
                            t.Screenshot($"{tag}_t{shotAges[shot]}");
                            shot++;
                            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                        }
                        yield return new WaitTicks(1);
                    }
                    t.Check(Chunks(map).Count == 0 && debris.Count == 0, $"{tag}: remote debris lands or is culled in time");
                    t.Check(ids.All(seen.Contains), $"{tag}: every remote chunk reaches the screen before it leaves ({seen.Count}/{ids.Count})");
                    t.Check(maxFragmentAlpha >= p.remoteMinFade, $"{tag}: fragments fade in ({maxFragmentAlpha:0.##})");
                }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // Drop-pod profile (P5): no fireball, fewer/heavier pod-tinted chunks, the hulk draws the pod's own texture,
        // and the missile profile still applies to missiles.
        public static IEnumerable<Wait> DropPodProfile(TestContext t)
        {
            Map map = Home;
            ThingDef podDef = ThingDefOf.DropPodIncoming;
            ThingDef missileDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
            InterceptDebrisProfileDef podProfile = DefDatabase<InterceptDebrisProfileDef>.GetNamed("DMSE_InterceptDebris_DropPod");

            var probe = new InterceptEventData { payloadKind = InterceptPayloadKind.DropPod, payloadDef = podDef };
            t.Check(InterceptDebrisProfileDef.For(probe) == podProfile, "drop pods use the drop-pod debris profile");
            probe = new InterceptEventData { payloadKind = InterceptPayloadKind.Missile, payloadDef = missileDef };
            t.Check(InterceptDebrisProfileDef.For(probe).defName == "DMSE_InterceptDebris_Missile", "missiles use the missile debris profile");

            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            yield return new WaitUntil(() => LiveFlecks(map) == 0 && (Debris(map)?.Count ?? 0) == 0 && Chunks(map).Count == 0, 900);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            InterceptDebrisChunk hulk = null;
            for (int attempt = 0; attempt < 6 && hulk == null; attempt++)
            {
                // Space the bursts out: from the third burst within 6 ticks the salvo throttle drops the hulk.
                if (attempt > 0) { yield return new WaitTicks(7); }
                InterceptEventData data = InterceptEventData.Build(null, map.Center, map.Size, map.Center, true, IntVec3.Invalid, 0.5f, null,
                    Gen.HashCombineInt(Find.TickManager.TicksGame, 31 + attempt), InterceptDebrisPhysics.Default, 135f);
                data.payloadKind = InterceptPayloadKind.DropPod;
                data.payloadDef = podDef;
                int chunksBefore = Chunks(map).Count;
                int minorBefore = Debris(map)?.Count ?? 0;
                IntVec3 cell = MapComponent_InterceptEffects.Trigger(map, data);
                List<InterceptDebrisChunk> spawned = Chunks(map).Skip(chunksBefore).ToList();
                int minor = (Debris(map)?.Count ?? 0) - minorBefore;
                if (attempt == 0)
                {
                    var system = Canvas(map).StaticFlecksOrNull;
                    t.Check(!system.EnumerateFlecks().OfType<FleckStatic>().Any(f => f.def == DMSE_DefOf.DMSE_InterceptFireball || f.def == DMSE_DefOf.DMSE_InterceptFireGlow),
                        "a drop-pod burst has no fireball");
                    t.Check(minor >= podProfile.minorCount.min && minor <= podProfile.minorCount.max, $"drop pod threw {minor} small fragments (profile {podProfile.minorCount})");
                    int plain = spawned.Count(c => !c.hulk);
                    t.Check(plain >= podProfile.majorCount.min && plain <= podProfile.majorCount.max, $"drop pod threw {plain} chunks (profile {podProfile.majorCount})");
                    t.Check(spawned.Where(c => !c.hulk).All(c => c.tint == podProfile.majorTint), "drop-pod chunks use the profile tint");
                    t.Check(cell.IsValid && cell.InBounds(map), $"a drop-pod burst predicts a corpse landing cell ({cell})");
                }
                hulk = spawned.FirstOrDefault(c => c.hulk);
            }
            t.Check(hulk != null && hulk.sourceDef == podDef && hulk.tint == podProfile.hulkTint, "a drop-pod hulk draws the pod's texture with the pod tint");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }

        // P6: intercept FX draw above fog of war (own materials, queue between fog and the map-edge clipper);
        // vanilla materials stay untouched; a real burst over a fogged cell is screenshotted.
        public static IEnumerable<Wait> RenderQueueDiag(TestContext t)
        {
            Map map = Home;
            int fogQ = MatBases.FogOfWar.renderQueue;
            int clipQ = MapEdgeClipDrawer.ClipMat.renderQueue;
            t.Note($"renderQueue: FogOfWar={fogQ} ClipMat={clipQ} LightOverlay={MatBases.LightOverlay?.renderQueue} "
                + $"shader Mote={ShaderDatabase.Mote.renderQueue} MoteGlow={ShaderDatabase.MoteGlow.renderQueue} Transparent={ShaderDatabase.Transparent.renderQueue}");

            var queues = new List<string>();
            bool allAbove = true;
            foreach (FleckDef def in DefDatabase<FleckDef>.AllDefsListForReading.Where(d => d.HasModExtension<InterceptFxMaterial>()))
            {
                var mats = new List<Material>();
                if (def.graphicData != null) { mats.Add(def.graphicData.Graphic.MatSingle); }
                if (def.randomGraphics != null) { mats.AddRange(def.randomGraphics.Select(g => g.Graphic.MatSingle)); }
                foreach (Material m in mats) { allAbove &= m.renderQueue > fogQ && m.renderQueue < clipQ; }
                queues.Add($"{def.defName}={string.Join("/", mats.Select(m => m.renderQueue).Distinct())}");
            }
            t.Note("fleck queues: " + string.Join(", ", queues));
            t.Check(queues.Count >= 7 && allAbove, "every intercept fleck material draws above fog and below the map-edge clipper");
            Material chunkMat = InterceptFxMaterials.AboveFog(ThingDefOf.DropPodIncoming.graphic.MatSingle, 3);
            t.Check(chunkMat.renderQueue > fogQ && chunkMat.renderQueue < clipQ && ThingDefOf.DropPodIncoming.graphic.MatSingle.renderQueue < fogQ,
                "hulk materials are clones above fog; the incoming thing's own material is unchanged");
            t.Check(FleckDefOf.ExplosionFlash.graphicData.Graphic.MatSingle.renderQueue < fogQ, "vanilla fleck materials stay below fog");

            IntVec3 fogCell = IntVec3.Invalid;
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.x > 30 && c.x < map.Size.x - 30 && c.z > 40 && c.z < map.Size.z - 30 && map.fogGrid.IsFogged(c)
                    && map.fogGrid.IsFogged(c + new IntVec3(6, 0, 0)) && map.fogGrid.IsFogged(c + new IntVec3(-6, 0, 0))) { fogCell = c; break; }
            }
            t.Note($"fogged probe cell: {fogCell}");
            if (!fogCell.IsValid) { yield break; }

            // Heading 0 terminal burst: draw position = aim + height, so aim 14 cells south of the fogged cell (approach 0.5 → height 14).
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            yield return new WaitUntil(() => LiveFlecks(map) == 0 && (Debris(map)?.Count ?? 0) == 0 && Chunks(map).Count == 0, 900);
            IntVec3 aim = fogCell + new IntVec3(0, 0, -14);
            InterceptEventData data = InterceptEventData.Build(null, aim, map.Size, map.Center, true, aim, 0.5f, null,
                Find.TickManager.TicksGame, InterceptDebrisPhysics.Default, 0f);
            data.payloadKind = InterceptPayloadKind.Missile;
            data.payloadDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
            data.targetSpeed = 0.8f;
            Vector3 burst = data.BurstDrawPos(InterceptDebrisPhysics.Default);
            t.Note($"burst over fog: drawPos ({burst.x:0.#}, {burst.z:0.#}) fogged={map.fogGrid.IsFogged(burst.ToIntVec3())}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            MapComponent_InterceptEffects.Trigger(map, data);
            t.allowPause = true;
            try
            {
                foreach (int age in new[] { 4, 30 })
                {
                    int at = Find.TickManager.TicksGame + (age == 4 ? 4 : 26);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    yield return new WaitUntil(() => Find.TickManager.TicksGame >= at, 300);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    Find.CameraDriver.SetRootPosAndSize(burst.WithY(0f) + new Vector3(0f, 0f, -6f), 24f);
                    CloseLogWindow();
                    yield return new WaitFrames(6);
                    t.Screenshot($"burst_over_fog_t{age}");
                }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // P6 budget: density scales counts; a big salvo stays under the live caps; frame time is logged for the record.
        public static IEnumerable<Wait> BudgetAndDensity(TestContext t)
        {
            Map map = Home;
            int density = Settings.interceptFxDensity;
            Override(() => { }, () => Settings.interceptFxDensity = density);
            ThingDef missileDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
            InterceptEventData Burst(int i, float heading)
            {
                InterceptEventData d = InterceptEventData.Build(null, map.Center, map.Size, map.Center, true, IntVec3.Invalid, 0.5f, null,
                    Gen.HashCombineInt(Find.TickManager.TicksGame, i), InterceptDebrisPhysics.Default, heading);
                d.payloadKind = InterceptPayloadKind.Missile;
                d.payloadDef = missileDef;
                d.targetSpeed = 0.8f;
                return d;
            }

            // Density: the same seeds at Low and High.
            var minorBy = new Dictionary<int, int>();
            foreach (int level in new[] { 0, 2 })
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                yield return new WaitUntil(() => (Debris(map)?.Count ?? 0) == 0 && Chunks(map).Count == 0 && LiveFlecks(map) == 0, 900);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                Settings.interceptFxDensity = level;
                int total = 0;
                for (int i = 0; i < 4; i++)
                {
                    int before = Debris(map)?.Count ?? 0;
                    MapComponent_InterceptEffects.Trigger(map, Burst(1000 + i, 90f * i));
                    total += (Debris(map)?.Count ?? 0) - before;
                    yield return new WaitTicks(7);
                }
                minorBy[level] = total;
            }
            t.Note($"small fragments for 4 bursts: Low {minorBy[0]}, High {minorBy[2]}");
            t.Check(minorBy[0] < minorBy[2] && minorBy[0] <= Mathf.CeilToInt(minorBy[2] * 0.5f),
                "low density throws far fewer fragments than high");

            // Salvo: 40 bursts over 40 ticks at High density; live counts stay under the caps.
            Settings.interceptFxDensity = 2;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            yield return new WaitUntil(() => (Debris(map)?.Count ?? 0) == 0 && Chunks(map).Count == 0 && LiveFlecks(map) == 0, 900);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            float idle = 0f;
            for (int f = 0; f < 60; f++) { idle += Time.unscaledDeltaTime; yield return new WaitFrames(1); }
            int maxChunks = 0, maxMinor = 0;
            float busy = 0f;
            int frames = 0;
            for (int i = 0; i < 40; i++)
            {
                MapComponent_InterceptEffects.Trigger(map, Burst(2000 + i, (i * 37) % 360));
                maxChunks = Mathf.Max(maxChunks, Chunks(map).Count);
                maxMinor = Mathf.Max(maxMinor, Debris(map)?.Count ?? 0);
                int next = Find.TickManager.TicksGame + 1;
                while (Find.TickManager.TicksGame < next) { busy += Time.unscaledDeltaTime; frames++; yield return new WaitFrames(1); }
            }
            // Trails pile up after the salvo: measure the next 120 frames too.
            float after = 0f;
            for (int f = 0; f < 120; f++) { after += Time.unscaledDeltaTime; yield return new WaitFrames(1); }
            t.Note($"salvo of 40: peak chunks {maxChunks}, peak fragments {maxMinor}; mean frame {idle / 60f * 1000f:0.0} ms idle, "
                + $"{busy / Mathf.Max(1, frames) * 1000f:0.0} ms during the salvo ({frames} frames), {after / 120f * 1000f:0.0} ms over the next 120 frames");
            t.Check(maxChunks <= InterceptFxBudget.MaxLiveChunks, $"live chunks never exceed the cap ({maxChunks} <= {InterceptFxBudget.MaxLiveChunks})");
            t.Check(maxMinor <= InterceptFxBudget.MaxLiveMinor, $"live fragments stay under the cap ({maxMinor} <= {InterceptFxBudget.MaxLiveMinor})");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            yield return new WaitUntil(() => Chunks(map).Count == 0, 900);
        }

        // L1 (docs/導彈發射特效_重製計畫.md): the same seeded salvo twice in one session, smoke/trail/fireball instancing on, then off.
        // GraphicData.renderInstanced is read at draw time, so it can be toggled live. Frame time is valid here (unpaused).
        public static IEnumerable<Wait> SalvoInstancingAB(TestContext t)
        {
            Map map = Home;
            ThingDef missileDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
            FleckDef[] smokeDefs = { DMSE_DefOf.DMSE_InterceptSmokeBall, DMSE_DefOf.DMSE_DebrisTrailPuff, DMSE_DefOf.DMSE_InterceptFireball };
            var graphics = smokeDefs.SelectMany(d => d.randomGraphics ?? new List<GraphicData>()).ToList();
            t.Check(graphics.Count > 0 && graphics.All(g => g.renderInstanced), "shipped smoke/trail/fireball defs are instanced");
            int density = Settings.interceptFxDensity;
            Override(() => Settings.interceptFxDensity = 2, () => Settings.interceptFxDensity = density);
            // Warm-up pass (shader/texture uploads), then three interleaved ON/OFF pairs; compare medians.
            var during = new Dictionary<bool, List<float>> { [true] = new List<float>(), [false] = new List<float>() };
            var cpu = new Dictionary<bool, List<float>> { [true] = new List<float>(), [false] = new List<float>() };
            FxCanvas canvas = Canvas(map);
            try
            {
                bool?[] passes = { null, true, false, true, false, true, false };
                foreach (bool? pass in passes)
                {
                    bool instanced = pass ?? true;
                    foreach (GraphicData g in graphics) { g.renderInstanced = instanced; }
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                    yield return new WaitUntil(() => (Debris(map)?.Count ?? 0) == 0 && Chunks(map).Count == 0 && LiveFlecks(map) == 0, 1200);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    yield return new WaitFrames(30);
                    double cpu0 = canvas.drawCpuMs;
                    int draws0 = canvas.drawCount;
                    float busy = 0f;
                    int frames = 0;
                    for (int i = 0; i < 40; i++)
                    {
                        InterceptEventData d = InterceptEventData.Build(null, map.Center, map.Size, map.Center, true, IntVec3.Invalid, 0.5f, null,
                            Gen.HashCombineInt(777, i), InterceptDebrisPhysics.Default, (i * 37) % 360);
                        d.payloadKind = InterceptPayloadKind.Missile;
                        d.payloadDef = missileDef;
                        d.targetSpeed = 0.8f;
                        MapComponent_InterceptEffects.Trigger(map, d);
                        int next = Find.TickManager.TicksGame + 1;
                        while (Find.TickManager.TicksGame < next) { busy += Time.unscaledDeltaTime; frames++; yield return new WaitFrames(1); }
                    }
                    // Keep drawing the lingering smoke for another 60 frames.
                    for (int f = 0; f < 60; f++) { busy += Time.unscaledDeltaTime; frames++; yield return new WaitFrames(1); }
                    float frameMs = busy / Mathf.Max(1, frames) * 1000f;
                    float drawMs = (float)((canvas.drawCpuMs - cpu0) / Mathf.Max(1, canvas.drawCount - draws0));
                    t.Note($"{(pass == null ? "warm-up" : instanced ? "instancing ON " : "instancing OFF")}: frame {frameMs:0.0} ms, canvas draw CPU {drawMs:0.00} ms/frame ({frames} frames)");
                    if (pass != null) { during[instanced].Add(frameMs); cpu[instanced].Add(drawMs); }
                }
            }
            finally
            {
                foreach (GraphicData g in graphics) { g.renderInstanced = true; }
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            float Median(List<float> xs) { var o = xs.OrderBy(x => x).ToList(); return o.Count == 0 ? 0f : o[o.Count / 2]; }
            t.Note($"median frame: ON {Median(during[true]):0.0} ms, OFF {Median(during[false]):0.0} ms; median canvas draw CPU: ON {Median(cpu[true]):0.00} ms, OFF {Median(cpu[false]):0.00} ms");
            var results = new Dictionary<bool, float> { [true] = Median(during[true]), [false] = Median(during[false]) };
            t.Check(results.ContainsKey(true) && results.ContainsKey(false), "both variants measured");
        }

        private static PlayerConfigSettings Settings => LoadedModManager.GetMod<DMSEMod>().GetSettings<PlayerConfigSettings>();

        private static List<InterceptDebrisChunk> Chunks(Map map)
            => map.listerThings.ThingsOfDef(DMSE_DefOf.DMSE_InterceptDebrisChunk).OfType<InterceptDebrisChunk>().ToList();

        /// <summary>A chunk that lands on <paramref name="cell"/> on the next tick.</summary>
        private static InterceptDebrisChunk DropChunkOn(Map map, IntVec3 cell)
        {
            var s = new DebrisState { ground = cell.ToVector3Shifted().WithY(0f), height = 0.05f, verticalSpeed = -0.2f, mass = 2f };
            return InterceptDebrisChunk.SpawnChunk(map, s, 1f, false, null);
        }

        public static IEnumerable<Wait> MajorDebris(TestContext t)
        {
            Map map = Home;
            IntVec3 landing = map.Center;
            ThingDef missileDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
            bool damageSetting = Settings.interceptDebrisDamage;
            Override(() => Settings.interceptDebrisDamage = false, () => Settings.interceptDebrisDamage = damageSetting);

            // Start with an empty sky: chunks from earlier tests (I1's salvo) would skew counts and the camera framing.
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            yield return new WaitUntil(() => Chunks(map).Count == 0 && (Debris(map)?.Count ?? 0) == 0, 900);

            // 1. A terminal missile burst from the east spawns chunks; try a few seeds until one throws a hulk.
            InterceptDebrisChunk hulk = null;
            int filthBefore = map.listerThings.ThingsOfDef(DMSE_DefOf.DMSE_Filth_MetalScrap).Count;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            for (int attempt = 0; attempt < 10 && hulk == null; attempt++)
            {
                // Space the bursts out: from the third burst within 6 ticks the salvo throttle drops the hulk.
                if (attempt > 0) { yield return new WaitTicks(7); }
                InterceptEventData data = InterceptEventData.Build(null, landing, map.Size, map.Center, true, IntVec3.Invalid, 0.3f, null,
                    Gen.HashCombineInt(Find.TickManager.TicksGame, attempt), InterceptDebrisPhysics.Default, 90f);
                data.payloadKind = InterceptPayloadKind.Missile;
                data.payloadDef = missileDef;
                data.targetSpeed = 1f;
                MapComponent_InterceptEffects.Trigger(map, data);
                hulk = Chunks(map).FirstOrDefault(c => c.hulk);
            }
            List<InterceptDebrisChunk> chunks = Chunks(map);
            t.Check(chunks.Count >= 2, $"bursts spawned {chunks.Count} major chunks");
            t.Check(hulk != null && hulk.sourceDef == missileDef, "a missile burst throws a hulk that draws the missile's own texture");
            t.Check(chunks.All(c => c.Position.InBounds(map)), "chunk positions are inside the map");
            Vector3 meanV = chunks.Aggregate(Vector3.zero, (acc, c) => acc + c.state.velocity);
            t.Check(meanV.x < 0f, $"chunks from an eastern entry fly west (mean vx {meanV.x / Mathf.Max(1, chunks.Count):0.###})");

            // 2. Save a chunk mid-flight and load a copy: the flight state survives.
            yield return new WaitTicks(10);
            InterceptDebrisChunk probe = Chunks(map).FirstOrDefault();
            if (t.Check(probe != null, "a chunk is still in flight after 10 ticks"))
            {
                string path = Path.Combine(Path.GetTempPath(), "dmse_intercept_chunk.xml");
                Thing saved = probe;
                Scribe.saver.InitSaving(path, "chunk");
                Scribe_Deep.Look(ref saved, "thing");
                Scribe.saver.FinalizeSaving();
                Thing loaded = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref loaded, "thing");
                Scribe.loader.FinalizeLoading();
                File.Delete(path);
                var copy = loaded as InterceptDebrisChunk;
                t.Check(copy != null && (copy.state.ground - probe.state.ground).sqrMagnitude < 1e-6f
                    && Mathf.Abs(copy.state.height - probe.state.height) < 1e-4f
                    && (copy.state.velocity - probe.state.velocity).sqrMagnitude < 1e-8f
                    && copy.state.age == probe.state.age && copy.hulk == probe.hulk && copy.sourceDef == probe.sourceDef,
                    "chunk flight state round-trips through save/load");
            }

            // 3. Screenshots of the trails, then everything lands or is culled within the lifetime; nothing damaged.
            t.allowPause = true;
            try
            {
                // Frame the flying chunks (the burst sits far east of the landing cell at approach 0.3).
                Vector3 look = map.Center.ToVector3Shifted();
                foreach (int at in new[] { 15, 35, 60 })
                {
                    int until = Find.TickManager.TicksGame + (at == 15 ? 5 : at == 35 ? 20 : 25);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    yield return new WaitUntil(() => Find.TickManager.TicksGame >= until, 300);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    List<InterceptDebrisChunk> flying = Chunks(map);
                    if (flying.Count > 0)
                    {
                        look = flying.Aggregate(Vector3.zero, (acc, c) => acc + c.DrawPos) / flying.Count;
                        look.y = 0f;
                    }
                    t.Note($"chunks_t{at}: {flying.Count} in flight, camera ({look.x:0.#}, {look.z:0.#})");
                    Find.CameraDriver.SetRootPosAndSize(look, 26f);
                    CloseLogWindow();
                    yield return new WaitFrames(6);
                    t.Screenshot($"chunks_t{at}");
                }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            var gone = new WaitUntil(() => Chunks(map).Count == 0, InterceptDebrisPhysics.Default.maxLifeTicks + 60);
            yield return gone;
            t.Check(!gone.TimedOut, "every chunk landed or was culled within its lifetime");
            int filthAfter = map.listerThings.ThingsOfDef(DMSE_DefOf.DMSE_Filth_MetalScrap).Count;
            t.Note($"metal scrap filth {filthBefore} -> {filthAfter}");
            t.Check(filthAfter > filthBefore, "landing chunks leave metal scrap");

            // 4. Damage only with the setting; roofs stop it.
            Thing wall = SpawnBuilding(ThingDefOf.Wall, 2, 2);
            Thing roofedWall = SpawnBuilding(ThingDefOf.Wall, 6, 2);
            IntVec3 roofCell = roofedWall.Position;
            Override(() => map.roofGrid.SetRoof(roofCell, RoofDefOf.RoofConstructed), () => map.roofGrid.SetRoof(roofCell, null));
            int hp0 = wall.HitPoints;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            DropChunkOn(map, wall.Position);
            yield return new WaitTicks(3);
            t.Check(wall.HitPoints == hp0, "setting off: a landing chunk does no damage");
            Settings.interceptDebrisDamage = true;
            DropChunkOn(map, wall.Position);
            int roofHp0 = roofedWall.HitPoints;
            DropChunkOn(map, roofCell);
            yield return new WaitTicks(3);
            var staticSettings = typeof(DMSEMod).GetField("Settings", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            t.Note($"damage diag: wall destroyed={wall.Destroyed} roofed={wall.Position.Roofed(map)} sameSettings={ReferenceEquals(staticSettings, Settings)} "
                + $"staticOn={(staticSettings as PlayerConfigSettings)?.interceptDebrisDamage} chunksLeft={Chunks(map).Count} "
                + $"cell=[{string.Join(", ", wall.Position.GetThingList(map).Select(x => x.def.defName))}]");
            t.Check(wall.HitPoints < hp0, $"setting on: a landing chunk damages the wall ({hp0} -> {wall.HitPoints})");
            t.Check(roofedWall.HitPoints == roofHp0, "setting on: a roof stops the chunk");
            Settings.interceptDebrisDamage = false;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }

        // Left to right, 12 cells apart: own system (grey), own system (white), vanilla map.flecks (same def), vanilla DustPuff, own system glow.
        // 2026-10-26 result: only the white-tinted smoke shows; the dark-grey tint is invisible in both systems, so the own system draws
        // correctly and dark smoke needs a dark texture, not a dark instanceColor. (The glow has already faded at t30.)
        public static IEnumerable<Wait> RenderAB(TestContext t)
        {
            Map map = Home;
            IntVec3 c = map.Center;
            Vector3 origin = c.ToVector3Shifted() + new Vector3(-24f, 0f, 0f);
            var comp = map.GetComponent<MapComponent_InterceptEffects>();
            FxCanvas canvas = comp.Canvas;
            t.Require(canvas != null, "intercept effects draw on the shared FX canvas");
            t.allowPause = true;
            try
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                canvas.Emit(DMSE_DefOf.DMSE_InterceptSmokeBall, origin, 6f, 0f, new Color(0.4f, 0.4f, 0.4f, 0.9f));
                canvas.Emit(DMSE_DefOf.DMSE_InterceptSmokeBall, origin + new Vector3(12f, 0f, 0f), 6f, 0f, Color.white);
                map.flecks.CreateFleck(new FleckCreationData
                {
                    def = DMSE_DefOf.DMSE_InterceptSmokeBall, spawnPosition = origin + new Vector3(24f, 0f, 0f), scale = 6f,
                    instanceColor = new Color(0.4f, 0.4f, 0.4f, 0.9f), ageTicksOverride = -1
                });
                FleckMaker.ThrowDustPuffThick(origin + new Vector3(36f, 0f, 0f), map, 6f, Color.grey);
                canvas.Emit(DMSE_DefOf.DMSE_InterceptFireGlow, origin + new Vector3(48f, 0f, 0f), 6f, 0f, Color.white);
                int at = Find.TickManager.TicksGame + 30;
                yield return new WaitUntil(() => Find.TickManager.TicksGame >= at, 300);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Find.CameraDriver.SetRootPosAndSize(c.ToVector3Shifted(), 30f);
                CloseLogWindow();
                yield return new WaitFrames(6);
                t.Screenshot("render_ab_t30");
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        private struct Case
        {
            public string tag;
            public float heading;
            public bool terminal;
            public float approach;
            public InterceptPayloadKind kind;
        }

        private static readonly Case[] Cases =
        {
            // Approach 0 is a remote intercept now (no airburst; covered by I8). East at 0.25 is the earliest on-map burst.
            new Case { tag = "terminal_early_from_E", heading = 90f, terminal = true, approach = 0.25f, kind = InterceptPayloadKind.Missile },
            new Case { tag = "terminal_near_from_S", heading = 180f, terminal = true, approach = 1f, kind = InterceptPayloadKind.Missile },
            new Case { tag = "terminal_mid_from_W", heading = 270f, terminal = true, approach = 0.5f, kind = InterceptPayloadKind.Missile },
            new Case { tag = "midcourse_from_N", heading = 0f, terminal = false, approach = 0f, kind = InterceptPayloadKind.Missile },
            new Case { tag = "midcourse_from_E", heading = 90f, terminal = false, approach = 0f, kind = InterceptPayloadKind.Missile },
            new Case { tag = "droppod_from_SE", heading = 135f, terminal = true, approach = 0.5f, kind = InterceptPayloadKind.DropPod },
        };

        /// <summary>The shared FX canvas the intercept effects draw on (docs/導彈發射特效_重製計畫.md L1).</summary>
        private static FxCanvas Canvas(Map map) => map.GetComponent<MapComponent_InterceptEffects>().Canvas;

        /// <summary>The debug log window auto-opens on other mods' startup errors and covers the screenshots.</summary>
        private static void CloseLogWindow() => Find.WindowStack.TryRemove(typeof(EditWindow_Log), doCloseSound: false);

        private static InterceptDebrisFlecks Debris(Map map) => Canvas(map).Debris;

        private static int LiveFlecks(Map map)
        {
            FleckSystem system = Canvas(map).StaticFlecksOrNull;
            return system == null ? 0 : system.EnumerateFlecks().Count();
        }

        public static IEnumerable<Wait> BurstScreenshots(TestContext t)
        {
            Map map = Home;
            t.Require(map == Find.CurrentMap, "home map is the current map");
            t.Require(Canvas(map) != null, "effects component has a shared FX canvas");
            IntVec3 landing = map.Center;
            t.allowPause = true;
            try
            {
                foreach (Case c in Cases)
                {
                    // Let the previous burst's smoke clear so each shot shows one burst.
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                    yield return new WaitUntil(() => LiveFlecks(map) == 0 && (Debris(map)?.Count ?? 0) == 0, 900);

                    InterceptEventData data = InterceptEventData.Build(null, landing, map.Size, map.Center, c.terminal, IntVec3.Invalid,
                        c.approach, null, Gen.HashCombineInt(Find.TickManager.TicksGame, (int)c.heading), InterceptDebrisPhysics.Default, c.heading);
                    data.payloadKind = c.kind;
                    Vector3 burst = data.BurstDrawPos(InterceptDebrisPhysics.Default);
                    float offMap = InterceptGeometry.DistanceOutside(map.Size, data.burstGround);
                    t.Note($"{c.tag}: burstGround=({data.burstGround.x:0.#}, {data.burstGround.z:0.#}) height={data.burstHeight:0.#} "
                        + $"drawPos=({burst.x:0.#}, {burst.z:0.#}) groundOffMap={offMap:0.#}");
                    // Vanilla's edge clipper hides anything outside the map, so the drawn burst must stay inside the margin.
                    float m = InterceptDebrisPhysics.Default.drawEdgeInset - 0.01f;
                    t.Check(burst.x >= m && burst.x <= map.Size.x - m && burst.z >= m && burst.z <= map.Size.z - m,
                        $"{c.tag}: burst is drawn inside the map margin");

                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    MapComponent_InterceptEffects.Trigger(map, data);
                    int created = LiveFlecks(map);
                    if (c.tag == "terminal_near_from_S")
                    {
                        // Diagnostics for the art review: what each fleck looks like.
                        var system = Canvas(map).StaticFlecksOrNull;
                        foreach (IFleck f in system.EnumerateFlecks())
                        {
                            if (f is FleckStatic s)
                            {
                                t.Note($"  fleck {s.def.defName}: pos=({s.DrawPos.x:0.#},{s.DrawPos.y:0.##},{s.DrawPos.z:0.#}) alpha={s.Alpha:0.##} "
                                    + $"scale={s.ExactScale.x:0.#} color={s.instanceColor} shader={s.def.graphicData?.shaderType?.defName} "
                                    + $"instanced={s.def.graphicData?.renderInstanced}");
                            }
                        }
                    }
                    int expectedMin = c.kind == InterceptPayloadKind.DropPod ? 1 + 4 + 6 : 1 + 2 + 4 + 6;
                    t.Check(created >= expectedMin, $"{c.tag}: burst created {created} flecks (>= {expectedMin})");
                    InterceptDebrisFlecks debris = Debris(map);
                    t.Check(debris != null && debris.Count >= 6, $"{c.tag}: burst threw {debris?.Count ?? 0} debris fragments");

                    // Early: flash and fireballs. Late: the smoke cloud on its own.
                    foreach (int age in new[] { 3, 20, 45 })
                    {
                        int at = Find.TickManager.TicksGame + age;
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                        yield return new WaitUntil(() => Find.TickManager.TicksGame >= at, 300);
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        Find.CameraDriver.SetRootPosAndSize(burst, 28f);
                        CloseLogWindow();
                        yield return new WaitFrames(6);
                        t.Screenshot($"{c.tag}_t{age}");
                    }

                    // Midcourse: also what a player sees with the camera parked on the entry edge.
                    if (!c.terminal)
                    {
                        Vector3 edge = MissileMapEntryUtility.EdgePoint(map.Size, landing.ToVector3Shifted(), c.heading);
                        Find.CameraDriver.SetRootPosAndSize(MissileMapEntryUtility.InsetFromEdge(map.Size, edge, 10f), 28f);
                        CloseLogWindow();
                        yield return new WaitFrames(6);
                        t.Screenshot($"{c.tag}_edge_view_t45");
                    }
                }

                // Salvo: 8 headings in one tick (thinned from the third burst on).
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                yield return new WaitUntil(() => LiveFlecks(map) == 0 && (Debris(map)?.Count ?? 0) == 0, 900);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                for (int i = 0; i < 8; i++)
                {
                    InterceptEventData d = InterceptEventData.Build(null, landing, map.Size, map.Center, true, IntVec3.Invalid,
                        0.5f, null, Gen.HashCombineInt(Find.TickManager.TicksGame, i), InterceptDebrisPhysics.Default, i * 45f);
                    MapComponent_InterceptEffects.Trigger(map, d);
                }
                int salvo = Find.TickManager.TicksGame + 3;
                yield return new WaitUntil(() => Find.TickManager.TicksGame >= salvo, 300);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Find.CameraDriver.SetRootPosAndSize(landing.ToVector3Shifted(), 70f);
                CloseLogWindow();
                yield return new WaitFrames(6);
                t.Screenshot("salvo_8_headings_t3");
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }
    }
}
