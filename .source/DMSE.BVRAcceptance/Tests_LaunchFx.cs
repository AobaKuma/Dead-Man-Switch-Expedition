using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Missile launch FX remake (docs/導彈發射特效_重製計畫.md). V0 is the L0 engine check: can our own Transparent
    /// smoke material be GPU-instanced through DrawBatch, and what does it save versus one DrawMesh per puff?
    /// </summary>
    public static class Tests_LaunchFx
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("V0", "Launch FX L0: 3000 smoke quads per variant (Transparent, Transparent instanced, Mote instanced): frame time and per-instance colour", InstancingAB),
            new AcceptanceTest("V1", "Launch FX L2: plume registers, ramps to full thrust and is drawn; old vs new solid-booster launch at the same ticks", PlumeOldVsNew),
            new AcceptanceTest("V2", "Launch FX L3: cold vertical launch leaves a continuous smoke column that lingers after the missile leaves", SmokeColumn),
            new AcceptanceTest("V3", "Launch FX L4: hot ground cloud at ignition, cold ejection puff before ignition, booster separation drops debris", OneOffBursts),
            new AcceptanceTest("V4", "Launch FX L5: every launch body uses its profile; one screenshot each at the same tick", AllBodies),
            new AcceptanceTest("V5", "Launch FX L6: 12-missile ripple stays under the smoke cap; canvas draw CPU logged", RippleBudget),
            new AcceptanceTest("V6", "Lighting diag: smoke tinted 100/60/35 % on Mote (instanced) vs Transparent; sky colour logged", TintDiag),
            new AcceptanceTest("V8", "Temporary missile size tuner: items on the ground, a loaded rack and the flight sprites; a slider change resizes them live", SizeTuner),
            new AcceptanceTest("V7", "Smoke and debris follow ambient light: darker at midnight than at noon, brighter next to a lamp; launch and intercept screenshots day and night", AmbientLight),
        };

        private const int Cols = 60, Rows = 50;
        private const int MeasureFrames = 90;

        private struct Variant
        {
            public string tag;
            public Material mat;
            public bool instanced;
        }

        public static IEnumerable<Wait> InstancingAB(TestContext t)
        {
            Map map = Home;
            Texture2D tex = ContentFinder<Texture2D>.Get("Things/Effects/InterceptDebris/SmokeMid/SmokeMid_a");
            t.Require(tex != null, "smoke texture loads");
            // Own copies, so enabling instancing here never touches the shipped materials.
            var variants = new[]
            {
                new Variant { tag = "transparent", mat = new Material(MaterialPool.MatFrom(tex, ShaderDatabase.Transparent, Color.white)), instanced = false },
                new Variant { tag = "transparent_instanced", mat = new Material(MaterialPool.MatFrom(tex, ShaderDatabase.Transparent, Color.white)), instanced = true },
                new Variant { tag = "mote_instanced", mat = new Material(MaterialPool.MatFrom(tex, ShaderDatabase.Mote, Color.white)), instanced = true },
            };
            foreach (Variant v in variants) { v.mat.renderQueue = 3177; }

            Vector3 origin = map.Center.ToVector3Shifted() + new Vector3(-Cols * 0.6f, 0f, -Rows * 0.6f);
            float y = AltitudeLayer.MetaOverlays.AltitudeFor(1f);
            var matrices = new List<Matrix4x4>(Cols * Rows);
            var colors = new List<Color>(Cols * Rows);
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    var pos = new Vector3(origin.x + c * 1.2f, y, origin.z + r * 1.2f);
                    matrices.Add(Matrix4x4.TRS(pos, Quaternion.AngleAxis((r * 37 + c * 11) % 360, Vector3.up), new Vector3(2.5f, 1f, 2.5f)));
                    // Upper half red-tinted: if per-instance colour is lost under instancing, the whole grid comes out one colour.
                    colors.Add(r >= Rows / 2 ? new Color(1f, 0.3f, 0.3f, 0.9f) : new Color(1f, 1f, 1f, 0.9f));
                }
            }

            var batch = new DrawBatch();
            t.allowPause = true;
            try
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Find.CameraDriver.SetRootPosAndSize(map.Center.ToVector3Shifted(), 40f);
                CloseLog();
                yield return new WaitFrames(10);

                float idle = 0f;
                for (int f = 0; f < MeasureFrames; f++) { idle += Time.unscaledDeltaTime; yield return new WaitFrames(1); }
                t.Note($"idle: {idle / MeasureFrames * 1000f:0.00} ms/frame");

                var results = new Dictionary<string, float>();
                foreach (Variant v in variants)
                {
                    float sum = 0f;
                    double cpu = 0;
                    var watch = new Stopwatch();
                    for (int f = 0; f < MeasureFrames + 20; f++)
                    {
                        // CPU side only (submit + flush): the window is throttled while paused in the harness, so frame time is not usable.
                        watch.Restart();
                        for (int i = 0; i < matrices.Count; i++)
                        {
                            batch.DrawMesh(MeshPool.plane10, matrices[i], v.mat, 0, colors[i], v.instanced);
                        }
                        batch.Flush(true);
                        watch.Stop();
                        if (f >= 20) { cpu += watch.Elapsed.TotalMilliseconds; }
                        if (f == 60)
                        {
                            CloseLog();
                            t.Screenshot("instancing_" + v.tag);
                        }
                        if (f >= 20) { sum += Time.unscaledDeltaTime; }
                        yield return new WaitFrames(1);
                    }
                    results[v.tag] = sum / MeasureFrames * 1000f;
                    t.Note($"{v.tag}: CPU submit+flush {cpu / MeasureFrames:0.00} ms/frame, frame {results[v.tag]:0.00} ms for {matrices.Count} quads "
                        + $"(enableInstancing={v.mat.enableInstancing}, supportsInstancing={SystemInfo.supportsInstancing})");
                }
                t.Check(results["transparent"] > 0f && results["transparent_instanced"] > 0f, "all variants measured");
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // ==================================================================
        //  V1–V5 helpers
        // ==================================================================

        private static MapComponent_MissileFx Fx(Map map) => MapComponent_MissileFx.For(map);

        private static ThingDef LaunchDef(string suffix) => DefDatabase<ThingDef>.GetNamed("DMSE_Launch_" + suffix);

        private static CompEffecterLaunch LaunchComp(Thing missile) => (missile as ThingWithComps)?.GetComp<CompEffecterLaunch>();

        /// <summary>Launch a departure (no world object) from <paramref name="cell"/>, heading north-west so the cruise leg crosses the map.</summary>
        private static Thing Launch(string suffix, IntVec3 cell, float heading = 330f)
            => MissileProjectileMaker.LaunchDeparture(LaunchDef(suffix), cell, Home, Rot4.North, null, null, heading);

        private static WaitUntil WaitClearSky(Map map)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            return new WaitUntil(() => Fx(map).Canvas.Smoke.Count == 0 && Fx(map).Active.Count == 0, 3000);
        }

        private static IEnumerable<Wait> Shot(TestContext t, Vector3 look, float size, string name)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.CameraDriver.SetRootPosAndSize(look, size);
            CloseLog();
            yield return new WaitFrames(6);
            t.Screenshot(name);
        }

        private static IEnumerable<Wait> RunTo(int start, int tick)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            yield return new WaitUntil(() => Find.TickManager.TicksGame - start >= tick, 1200);
        }

        // ==================================================================
        //  V1: plume (L2), old vs new at the same ticks
        // ==================================================================

        public static IEnumerable<Wait> PlumeOldVsNew(TestContext t)
        {
            Map map = Home;
            ThingDef def = LaunchDef("LegacyInterceptor");
            var props = def.GetCompProperties<CompProperties_EffecterLaunch>();
            t.Require(props?.fxProfile != null && props.fxProfile.defName == "DMSE_LaunchFx_SolidBooster", "LegacyInterceptor uses the solid-booster profile");
            yield return WaitClearSky(map);
            IntVec3 cell = Pad.CenterCell;
            Vector3 look = cell.ToVector3Shifted() + new Vector3(-6f, 0f, 14f);
            LaunchFxProfileDef profile = props.fxProfile;
            t.allowPause = true;
            try
            {
                foreach (bool useNew in new[] { false, true })
                {
                    props.fxProfile = useNew ? profile : null;
                    yield return WaitClearSky(map);
                    Thing missile = Launch("LegacyInterceptor", cell);
                    int start = Find.TickManager.TicksGame;
                    foreach (int at in new[] { 5, 30, 60 })
                    {
                        foreach (Wait w in RunTo(start, at)) { yield return w; }
                        if (missile.Destroyed) { break; }
                        if (useNew && at == 30)
                        {
                            LaunchFxEmitter fx = LaunchComp(missile)?.LaunchFx;
                            t.Check(fx != null && Fx(map).Active.Contains(fx), "the launch registered a plume emitter");
                            t.Check(fx != null && fx.Thrust > 0.99f, $"the plume is at full thrust 30 ticks after a hot launch ({fx?.Thrust:0.##})");
                        }
                        foreach (Wait w in Shot(t, look, 22f, $"plume_{(useNew ? "new" : "old")}_t{at}")) { yield return w; }
                    }
                    if (!missile.Destroyed) { missile.Destroy(); }
                }
            }
            finally
            {
                props.fxProfile = profile;
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            t.Check(Fx(map).Active.Count == 0, "destroyed missiles unregister their plume");
        }

        // ==================================================================
        //  V2: smoke column (L3)
        // ==================================================================

        public static IEnumerable<Wait> SmokeColumn(TestContext t)
        {
            Map map = Home;
            yield return WaitClearSky(map);
            IntVec3 cell = Pad.CenterCell;
            Thing missile = Launch("AdvancedCruise", cell, 0f);
            LaunchFxEmitter fx = null;
            int start = Find.TickManager.TicksGame;
            t.allowPause = true;
            try
            {
                foreach (int at in new[] { 60, 120 })
                {
                    foreach (Wait w in RunTo(start, at)) { yield return w; }
                    fx = LaunchComp(missile)?.LaunchFx ?? fx;
                    foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(0f, 0f, 16f), 26f, $"column_t{at}")) { yield return w; }
                }
                t.Require(fx != null, "the cold launch has a launch FX emitter");
                t.Note($"column: {fx.puffsEmitted} puffs, max gap {fx.maxStreamGap:0.##} cells, max gap/size {fx.maxGapToSize:0.##}, live smoke {Fx(map).Canvas.Smoke.Count}");
                t.Check(fx.puffsEmitted >= 40, $"the column emitted {fx.puffsEmitted} puffs");
                t.Check(fx.maxGapToSize < 0.8f, $"neighbouring puffs overlap: max gap / size {fx.maxGapToSize:0.##} < 0.8");
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                var gone = new WaitUntil(() => missile.Destroyed, 1500);
                yield return gone;
                int left = Find.TickManager.TicksGame;
                foreach (Wait w in RunTo(left, 240)) { yield return w; }
                t.Check(Fx(map).Canvas.Smoke.Count > 0, $"the column still hangs in the air 4 s after the missile left ({Fx(map).Canvas.Smoke.Count} puffs)");
                foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(0f, 0f, 16f), 26f, "column_after_leave_4s")) { yield return w; }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // ==================================================================
        //  V3: one-off bursts (L4)
        // ==================================================================

        public static IEnumerable<Wait> OneOffBursts(TestContext t)
        {
            Map map = Home;
            IntVec3 cell = Pad.CenterCell;
            t.allowPause = true;
            try
            {
                // Hot: ground cloud at ignition.
                yield return WaitClearSky(map);
                Thing hot = Launch("LegacyInterceptor", cell);
                int start = Find.TickManager.TicksGame;
                foreach (Wait w in RunTo(start, 3)) { yield return w; }
                LaunchFxEmitter hotFx = LaunchComp(hot)?.LaunchFx;
                t.Check(hotFx != null && hotFx.burstPuffs >= 6, $"hot launch threw a ground cloud ({hotFx?.burstPuffs} puffs)");
                foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(0f, 0f, 4f), 18f, "hot_ground_cloud_t3")) { yield return w; }
                foreach (Wait w in RunTo(start, 20)) { yield return w; }
                foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(0f, 0f, 6f), 18f, "hot_ground_cloud_t20")) { yield return w; }
                if (!hot.Destroyed) { hot.Destroy(); }

                // Cold: ejection puff before ignition, ignition later in the air.
                yield return WaitClearSky(map);
                Thing cold = Launch("AdvancedBallistic", cell);
                int delay = LaunchComp(cold).Props.delayTicks;
                start = Find.TickManager.TicksGame;
                foreach (Wait w in RunTo(start, 3)) { yield return w; }
                LaunchFxEmitter coldFx = LaunchComp(cold)?.LaunchFx;
                t.Check(coldFx != null && coldFx.burstPuffs > 0 && coldFx.ignitionTick < 0 && coldFx.Thrust == 0f,
                    $"cold launch: ejection puff ({coldFx?.burstPuffs}) and no flame before ignition (delay {delay})");
                foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(0f, 0f, 4f), 18f, "cold_ejection_t3")) { yield return w; }
                foreach (Wait w in RunTo(start, delay + 8)) { yield return w; }
                t.Check(coldFx.ignitionTick >= 0 && coldFx.Thrust > 0.5f, $"cold launch ignites after the delay (thrust {coldFx.Thrust:0.##})");
                foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(0f, 0f, 8f), 18f, $"cold_air_ignition_t{delay + 8}")) { yield return w; }
                if (!cold.Destroyed) { cold.Destroy(); }

                // Booster separation drops debris.
                yield return WaitClearSky(map);
                int chunksBefore = map.listerThings.ThingsOfDef(DMSE_DefOf.DMSE_InterceptDebrisChunk).Count;
                Thing cruise = Launch("Moskit", cell);
                LaunchFxEmitter cruiseFx = null;
                int booster = LaunchDef("Moskit").GetCompProperties<CompProperties_EffecterLaunch>().fxProfile.boosterTicks;
                int cruiseDelay = LaunchComp(cruise).Props.delayTicks;
                start = Find.TickManager.TicksGame;
                foreach (Wait w in RunTo(start, cruiseDelay + booster + 15)) { yield return w; }
                cruiseFx = LaunchComp(cruise)?.LaunchFx;
                int chunksAfter = map.listerThings.ThingsOfDef(DMSE_DefOf.DMSE_InterceptDebrisChunk).Count;
                t.Check(cruiseFx != null && cruiseFx.debrisSpawned >= 1 && chunksAfter > chunksBefore,
                    $"booster separation dropped {cruiseFx?.debrisSpawned} debris chunk(s)");
                t.Check(cruiseFx != null && cruiseFx.Thrust < 0.2f, $"after burnout the cruise missile runs on its sustainer ({cruiseFx?.Thrust:0.##})");
                foreach (Wait w in Shot(t, (cruiseFx?.Nozzle ?? cell.ToVector3Shifted()) + new Vector3(0f, 0f, -4f), 20f, "cruise_booster_separation")) { yield return w; }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // ==================================================================
        //  V4: every launch body (L5)
        // ==================================================================

        public static IEnumerable<Wait> AllBodies(TestContext t)
        {
            Map map = Home;
            IntVec3 cell = Pad.CenterCell;
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.defName.StartsWith("DMSE_Launch_")).OrderBy(d => d.defName).ToList();
            t.Check(defs.Count >= 15, $"{defs.Count} launch defs");
            var missing = defs.Where(d => d.GetCompProperties<CompProperties_EffecterLaunch>()?.fxProfile == null).Select(d => d.defName).ToList();
            t.Check(missing.Count == 0, "every launch def has a launch FX profile" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
            t.allowPause = true;
            try
            {
                foreach (ThingDef def in defs)
                {
                    yield return WaitClearSky(map);
                    Thing missile = MissileProjectileMaker.LaunchDeparture(def, cell, map, Rot4.North, null, null, 330f);
                    int start = Find.TickManager.TicksGame;
                    int delay = LaunchComp(missile)?.Props.delayTicks ?? 0;
                    foreach (Wait w in RunTo(start, 35 + (LaunchComp(missile) is CompEffecterColdLaunch ? delay : 0))) { yield return w; }
                    LaunchFxEmitter fx = LaunchComp(missile)?.LaunchFx;
                    t.Check(fx != null, $"{def.defName}: launch FX running ({def.GetCompProperties<CompProperties_EffecterLaunch>()?.fxProfile?.defName})");
                    foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(-4f, 0f, 12f), 22f, "body_" + def.defName.Replace("DMSE_Launch_", ""))) { yield return w; }
                    if (!missile.Destroyed) { missile.Destroy(); }
                }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        // ==================================================================
        //  V5: ripple budget (L6)
        // ==================================================================

        public static IEnumerable<Wait> RippleBudget(TestContext t)
        {
            Map map = Home;
            var settings = LoadedModManager.GetMod<DMSEMod>().GetSettings<PlayerConfigSettings>();
            int density = settings.interceptFxDensity;
            Override(() => { }, () => settings.interceptFxDensity = density);
            foreach (int level in new[] { 1, 2 })
            {
                settings.interceptFxDensity = level;
                yield return WaitClearSky(map);
                FxCanvas canvas = Fx(map).Canvas;
                double cpu0 = canvas.drawCpuMs;
                int draws0 = canvas.drawCount;
                int peak = 0;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                var missiles = new List<Thing>();
                for (int i = 0; i < 12; i++)
                {
                    missiles.Add(Launch("Light", Pad.CenterCell + new IntVec3((i % 4) * 3 - 4, 0, (i / 4) * 3 - 3), 300f + i * 5f));
                    int next = Find.TickManager.TicksGame + 30;
                    while (Find.TickManager.TicksGame < next) { peak = Mathf.Max(peak, canvas.Smoke.Count); yield return new WaitFrames(1); }
                }
                int until = Find.TickManager.TicksGame + 240;
                while (Find.TickManager.TicksGame < until) { peak = Mathf.Max(peak, canvas.Smoke.Count); yield return new WaitFrames(1); }
                double cpu = (canvas.drawCpuMs - cpu0) / Mathf.Max(1, canvas.drawCount - draws0);
                t.Note($"density {level}: 12-missile ripple peak smoke {peak} (cap {LaunchFxBudget.SmokeCap}), canvas draw CPU {cpu:0.00} ms/frame over {canvas.drawCount - draws0} frames");
                t.Check(peak <= LaunchFxBudget.SmokeCap + LaunchFxBudget.MaxPuffsPerTick * 12, $"density {level}: smoke stays at the cap ({peak})");
                foreach (Thing m in missiles) { if (!m.Destroyed) { m.Destroy(); } }
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }

        // Does a dark tint darken smoke on each shader (needed for lighting by multiplying the colour), or does it vanish?
        public static IEnumerable<Wait> TintDiag(TestContext t)
        {
            Map map = Home;
            t.Note($"sky: glow {map.skyManager.CurSkyGlow:0.##}, sky colour {map.skyManager.CurSky.colors.sky}, overlay colour {MatBases.LightOverlay.color}");
            FleckDef transparent = new FleckDef
            {
                defName = "DMSE_Diag_SmokeTransparent", fleckSystemClass = typeof(FleckSystemStatic), altitudeLayer = AltitudeLayer.MetaOverlays,
                altitudeLayerIncOffset = 1f, solidTime = 60f, fadeOutTime = 1f,
                graphicData = new GraphicData { texPath = "Things/Effects/InterceptDebris/SmokeMid/SmokeMid_a", graphicClass = typeof(Graphic_Fleck), shaderType = ShaderTypeDefOf.Transparent }
            };
            FxCanvas canvas = Fx(map).Canvas;
            Vector3 origin = Pad.CenterCell.ToVector3Shifted() + new Vector3(-12f, 0f, 0f);
            float[] tints = { 1f, 0.6f, 0.35f };
            for (int i = 0; i < tints.Length; i++)
            {
                Color c = new Color(tints[i], tints[i], tints[i], 0.9f);
                canvas.Emit(DMSE_DefOf.DMSE_LaunchSmokeMid, origin + new Vector3(8f * i, 0f, 4f), 5f, 0f, c);
                canvas.Emit(transparent, origin + new Vector3(8f * i, 0f, -4f), 5f, 0f, c);
            }
            t.allowPause = true;
            try
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                yield return new WaitTicks(5);
                foreach (Wait w in Shot(t, Pad.CenterCell.ToVector3Shifted(), 16f, "tint_diag")) { yield return w; }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            t.Check(true, "diagnostic screenshot taken (top row Mote instanced, bottom row Transparent; tints 100/60/35 % left to right)");
        }

        /// <summary>Jump the clock forward to the given local hour (debug tick setter; the sky follows on the next frame).</summary>
        private static void JumpToHour(Map map, int hour)
        {
            int now = GenLocalDate.HourOfDay(map);
            int hours = ((hour - now) % 24 + 24) % 24;
            if (hours == 0) { return; }
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + hours * GenDate.TicksPerHour);
        }

        private static float Luma(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;

        // Smoke and debris are drawn after vanilla's lighting overlay (to stay above fog), so FxLighting multiplies the light in.
        public static IEnumerable<Wait> AmbientLight(TestContext t)
        {
            Map map = Home;
            IntVec3 cell = Pad.CenterCell;
            var lumas = new Dictionary<int, float>();
            t.allowPause = true;
            try
            {
                foreach (int hour in new[] { 12, 0 })
                {
                    yield return WaitClearSky(map);
                    JumpToHour(map, hour);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    yield return new WaitFrames(10);
                    Color light = FxLighting.LightAt(map, cell.ToVector3Shifted());
                    lumas[hour] = Luma(light);
                    t.Note($"hour {hour}: sky glow {map.skyManager.CurSkyGlow:0.##}, sky {map.skyManager.CurSky.colors.sky}, light at pad {light}");

                    Thing missile = Launch("LegacyInterceptor", cell);
                    InterceptEventData d = InterceptEventData.Build(null, cell + new IntVec3(14, 0, 0), map.Size, map.Center, true,
                        cell + new IntVec3(14, 0, 4), 0.5f, null, 4242 + hour, InterceptDebrisPhysics.Default, 90f);
                    d.payloadKind = InterceptPayloadKind.Missile;
                    d.payloadDef = DefDatabase<ThingDef>.GetNamed("DMSE_Incoming_LegacyBallisticMissile");
                    d.targetSpeed = 0.8f;
                    MapComponent_InterceptEffects.Trigger(map, d);
                    int start = Find.TickManager.TicksGame;
                    foreach (Wait w in RunTo(start, 45)) { yield return w; }
                    foreach (Wait w in Shot(t, cell.ToVector3Shifted() + new Vector3(6f, 0f, 10f), 24f, $"light_hour{hour:00}")) { yield return w; }
                    if (!missile.Destroyed) { missile.Destroy(); }
                }
                t.Check(lumas[0] < lumas[12] * 0.8f, $"midnight light ({lumas[0]:0.##}) is clearly darker than noon ({lumas[12]:0.##})");

                // A lamp lights nearby smoke at night.
                Thing lamp = SpawnBuilding(DefDatabase<ThingDef>.GetNamed("TorchLamp"), 2, 2);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                yield return new WaitTicks(30);
                yield return new WaitFrames(5);
                float near = Luma(FxLighting.LightAt(map, lamp.Position.ToVector3Shifted() + new Vector3(1f, 0f, 0f)));
                float far = Luma(FxLighting.LightAt(map, lamp.Position.ToVector3Shifted() + new Vector3(30f, 0f, 30f)));
                t.Note($"night light next to a torch lamp {near:0.##}, 30 cells away {far:0.##}");
                t.Check(near > far + 0.1f, "a lamp brightens smoke next to it at night");
                FxCanvas canvas = Fx(map).Canvas;
                var spec = new SmokePuffSpec
                {
                    def = DMSE_DefOf.DMSE_LaunchSmokeLight, pos = lamp.Position.ToVector3Shifted() + new Vector3(1f, 0f, 1f), size = 4f, lifeTicks = 300,
                    color = Color.white, hotColor = Color.white, drag = 0.1f
                };
                canvas.Smoke.Spawn(spec);
                spec.pos = lamp.Position.ToVector3Shifted() + new Vector3(12f, 0f, 1f);
                canvas.Smoke.Spawn(spec);
                yield return new WaitTicks(10);
                foreach (Wait w in Shot(t, lamp.Position.ToVector3Shifted() + new Vector3(6f, 0f, 1f), 14f, "light_lamp_night")) { yield return w; }
            }
            finally
            {
                t.allowPause = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
        }

        private static void CloseLog() => Find.WindowStack.TryRemove(typeof(EditWindow_Log), doCloseSound: false);
    
        // ==================================================================
        //  V8: temporary missile size tuner (.source/DMSE/Dev/MissileSizeTuner.cs)
        // ==================================================================

        public static IEnumerable<Wait> SizeTuner(TestContext t)
        {
            Map map = Home;
            IntVec3 origin = Pad.CenterCell + new IntVec3(-14, 0, -6);
            int items = 0, x = 0;
            foreach (ThingDef def in MissileSizeTuner.MissileItems)
            {
                Thing item = GenSpawn.Spawn(ThingMaker.MakeThing(def), origin + new IntVec3(x, 0, 0), map);
                spawnedForCleanup.Add(item);
                x += Mathf.Max(1, Mathf.CeilToInt(def.graphicData.drawSize.x * 0.4f));
                items++;
            }
            t.Check(items >= 10, $"{items} missile items placed on the ground");

            Thing rackThing = SpawnBuilding(DefDatabase<ThingDef>.GetNamed("DMSE_Building_LoiteringLauncher"), 2, 16);
            IMissileRack rack = MissileRackUtil.Of(rackThing);
            ThingDef loaded = MissileSizeTuner.MissileItems.FirstOrDefault(d => rack != null && rack.Accepts(ThingMaker.MakeThing(d)));
            if (rack != null && loaded != null)
            {
                while (!rack.Full)
                {
                    Thing m = ThingMaker.MakeThing(loaded);
                    if (!rack.Accepts(m) || !rack.GetDirectlyHeldThings().TryAdd(m)) { break; }
                }
            }
            t.Check(rack != null && rack.StoredCount > 0, $"rack loaded with {rack?.StoredCount ?? 0}× {loaded?.defName}");

            MapComponent_MissileSizePreview preview = map.GetComponent<MapComponent_MissileSizePreview>();
            preview.anchor = origin + new IntVec3(0, 0, 8);
            Find.WindowStack.Add(new Dialog_MissileSizeTuner());
            yield return new WaitFrames(10);
            t.Check(Find.WindowStack.IsOpen<Dialog_MissileSizeTuner>(), "tuner window open");
            foreach (Wait w in Shot(t, origin.ToVector3Shifted() + new Vector3(16f, 0f, 8f), 24f, "size_tuner_before")) { yield return w; }

            // A slider change: the item on the ground, the missiles on the rack and the flight sprite resize at once.
            MissileSizeEntry itemEntry = MissileSizeTuner.Entries.First(e => e.item && e.key == loaded?.defName);
            MissileSizeEntry flightEntry = MissileSizeTuner.Entries.First(e => !e.item);
            float before = itemEntry.size;
            itemEntry.size = before + 1.5f; itemEntry.Apply();
            flightEntry.size += 1.5f; flightEntry.Apply();
            Thing onGround = map.listerThings.ThingsOfDef(loaded).FirstOrDefault();
            t.Check(onGround != null && Mathf.Abs(onGround.Graphic.drawSize.x - (before + 1.5f)) < 0.01f, $"item on the ground redrawn at {onGround?.Graphic.drawSize.x:0.0}");
            t.Check(rack.HeldThings.All(h => Mathf.Abs(h.Graphic.drawSize.x - (before + 1.5f)) < 0.01f), "missiles on the rack redrawn at the new size");
            t.Check(flightEntry.defs.All(d => Mathf.Abs(d.graphic.drawSize.x - flightEntry.size) < 0.01f), $"{flightEntry.key}: all {flightEntry.defs.Count} flight defs resized");
            t.Check(MissileSizeTuner.Export().Contains("*changed"), "export marks the changed rows");
            yield return new WaitFrames(5);
            foreach (Wait w in Shot(t, origin.ToVector3Shifted() + new Vector3(16f, 0f, 8f), 24f, "size_tuner_after")) { yield return w; }

            foreach (MissileSizeEntry e in MissileSizeTuner.Entries) { e.size = e.originalSize; e.offsetZ = e.originalOffsetZ; e.Apply(); }
            preview.anchor = IntVec3.Invalid;
            Find.WindowStack.TryRemove(typeof(Dialog_MissileSizeTuner));
            foreach (Thing th in spawnedForCleanup) { if (!th.Destroyed) { th.Destroy(); } }
            spawnedForCleanup.Clear();
        }

        private static readonly List<Thing> spawnedForCleanup = new List<Thing>();
    }
}
