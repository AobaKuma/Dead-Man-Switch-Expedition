using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.ProfilerUI;
using Waylith.Profiling;
using Waylith.Profiling.Instrumentation;

namespace DMSE.BVRAcceptance
{
    /// <summary>
    /// Performance capture with the game's own frame profiler (debug action "Frame profiler"), switched on by
    /// <c>-dmse-bvr-profile</c>. Every DMSE method is instrumented; each frame the newest profiler snapshots are read
    /// and added up per acceptance test, so every test doubles as a workload. Writes perf_sections.tsv (one row per
    /// test), perf_entries.tsv (DMSE entry points: time including everything they call) and perf_self.tsv
    /// (time spent in each DMSE method itself).
    /// </summary>
    public static class PerfProfile
    {
        public const string ArgKey = "dmse-bvr-profile";

        private sealed class Stat
        {
            public double us;        // total (entries) or self (self table) microseconds
            public long calls;
            public double tickUs;    // part of us spent below TickManager.DoSingleTick
            public float maxFrameUs; // worst single frame
        }

        private sealed class Section
        {
            public string name;
            public int frames, ticks;
            public double frameUs, tickUs, dmseUs, dmseTickUs;
            public float maxFrameUs, maxDmseFrameUs;
            public long allocBytes;
            public int gen0, gen1, gen2;
            public readonly Dictionary<int, Stat> entries = new Dictionary<int, Stat>();
            public readonly Dictionary<int, Stat> self = new Dictionary<int, Stat>();
        }

        private static bool? requested;
        private static bool running;
        private static int lastOrdinal = -1;
        private static int lastTick = -1;
        private static Section current;
        private static readonly List<Section> sections = new List<Section>();
        private static readonly Dictionary<int, float> frameScratch = new Dictionary<int, float>();

        // Per path id: 0 = unknown, otherwise flags.
        private const byte Known = 1, Dmse = 2, DmseAbove = 4, UnderTick = 8, TickAnchor = 16;
        private static byte[] pathFlags = new byte[4096];
        private static readonly Dictionary<int, byte> labelKinds = new Dictionary<int, byte>(); // 0 other, 1 DMSE, 2 tick anchor

        public static bool Requested
        {
            get
            {
                if (requested == null) { requested = GenCommandLine.CommandLineArgPassed(ArgKey); }
                return requested.Value;
            }
        }

        public static void Begin()
        {
            if (!Requested || running) { return; }
            ProfilerPrefsData prefs = ProfilerPrefs.Current; // in memory only; never saved from here
            prefs.instrumentationTargets = new List<string>
            {
                "DMSE!*::*",
                "Assembly-CSharp!Verse.TickManager::DoSingleTick",
            };
            prefs.instrumentation = true;
            prefs.mainThreadOnly = true;
            prefs.allocTracking = false;
            prefs.autoMuteTrivial = true;
            prefs.historyCapacity = 2000;
            FrameProfiler.SetEnabled(true);
            ProfilerPrefs.Apply();
            running = true;
            AcceptanceDriver.Log($"[perf] frame profiler on: instrumentation supported={ProfilerInstrumentation.IsSupported} "
                + $"enabled={ProfilerInstrumentation.IsEnabled} matched={ProfilerInstrumentation.MatchedMethodCount} "
                + $"instrumented={ProfilerInstrumentation.InstrumentedMethodCount}");
            StartSection("(startup)");
        }

        public static void StartSection(string name)
        {
            if (!running) { return; }
            Sample();
            current = sections.Find(x => x.name == name);
            if (current == null)
            {
                current = new Section { name = name };
                sections.Add(current);
            }
            lastTick = Find.TickManager.TicksGame;
        }

        /// <summary>Read every profiler frame finished since the last call. Call once per rendered frame.</summary>
        public static void Sample()
        {
            if (!running || current == null) { return; }
            int now = Find.TickManager.TicksGame;
            if (lastTick >= 0 && now > lastTick) { current.ticks += now - lastTick; }
            lastTick = now;
            try
            {
                using (FrameProfiler.BeginRead())
                {
                    int newest = -1;
                    for (int offset = 0; offset < 2000; offset++)
                    {
                        if (!FrameProfiler.TryGetHistorySnapshot(offset, out FrameProfiler.FrameSnapshot snap)) { break; }
                        int ordinal = snap.captureOrdinal;
                        if (ordinal <= lastOrdinal) { break; }
                        if (ordinal > newest) { newest = ordinal; }
                        if (snap.valid) { Add(current, snap); }
                    }
                    if (newest > lastOrdinal) { lastOrdinal = newest; }
                }
            }
            catch (Exception ex)
            {
                AcceptanceDriver.Log("[perf] sample failed: " + ex.Message);
            }
        }

        private static void Add(Section s, FrameProfiler.FrameSnapshot snap)
        {
            s.frames++;
            s.frameUs += snap.totalMs * 1000.0;
            if (snap.totalMs * 1000f > s.maxFrameUs) { s.maxFrameUs = snap.totalMs * 1000f; }
            s.allocBytes += snap.allocatedBytes;
            s.gen0 += snap.gen0Collections; s.gen1 += snap.gen1Collections; s.gen2 += snap.gen2Collections;

            frameScratch.Clear();
            float dmseFrame = 0f;
            ProfilerPathTable.Entry[] table = ProfilerPathTable.Entries;
            int tableCount = ProfilerPathTable.Count;
            for (int i = 0; i < snap.sectionCount; i++)
            {
                FrameProfiler.ThreadSection sec = snap.sections[i];
                int end = Math.Min(sec.firstPathEntry + sec.pathEntryCount, snap.pathEntryCount);
                for (int k = sec.firstPathEntry; k < end; k++)
                {
                    FrameProfiler.PathEntry e = snap.pathEntries[k];
                    if ((uint)e.pathId >= (uint)tableCount) { continue; }
                    byte f = Flags(e.pathId, table);
                    if ((f & TickAnchor) != 0 && (f & UnderTick) == 0) { s.tickUs += e.totalMicroseconds; }
                    if ((f & Dmse) == 0) { continue; }
                    int label = table[e.pathId].labelId.Value;
                    bool underTick = (f & UnderTick) != 0;

                    Stat self = Get(s.self, label);
                    self.us += e.selfMicroseconds; self.calls += e.calls;
                    if (underTick) { self.tickUs += e.selfMicroseconds; }

                    if ((f & DmseAbove) != 0) { continue; } // counted in its DMSE caller's total
                    Stat entry = Get(s.entries, label);
                    entry.us += e.totalMicroseconds; entry.calls += e.calls;
                    if (underTick) { entry.tickUs += e.totalMicroseconds; s.dmseTickUs += e.totalMicroseconds; }
                    s.dmseUs += e.totalMicroseconds;
                    dmseFrame += e.totalMicroseconds;
                    frameScratch.TryGetValue(label, out float sum);
                    frameScratch[label] = sum + e.totalMicroseconds;
                }
            }
            if (dmseFrame > s.maxDmseFrameUs) { s.maxDmseFrameUs = dmseFrame; }
            foreach (KeyValuePair<int, float> pair in frameScratch)
            {
                Stat entry = s.entries[pair.Key];
                if (pair.Value > entry.maxFrameUs) { entry.maxFrameUs = pair.Value; }
            }
        }

        private static Stat Get(Dictionary<int, Stat> map, int label)
        {
            if (!map.TryGetValue(label, out Stat stat)) { stat = new Stat(); map[label] = stat; }
            return stat;
        }

        private static byte Flags(int pathId, ProfilerPathTable.Entry[] table)
        {
            if (pathId >= pathFlags.Length) { Array.Resize(ref pathFlags, Math.Max(pathId + 1, pathFlags.Length * 2)); }
            byte f = pathFlags[pathId];
            if (f != 0) { return f; }
            f = Known;
            int kind = LabelKind(table[pathId].labelId);
            if (kind == 1) { f |= Dmse; }
            if (kind == 2) { f |= TickAnchor; }
            int parent = table[pathId].parentPathId;
            if (parent > 0 && parent < table.Length)
            {
                byte pf = Flags(parent, table);
                if ((pf & (Dmse | DmseAbove)) != 0) { f |= DmseAbove; }
                if ((pf & (TickAnchor | UnderTick)) != 0) { f |= UnderTick; }
            }
            pathFlags[pathId] = f;
            return f;
        }

        private static int LabelKind(ProfilerLabelId id)
        {
            if (!id.IsValid) { return 0; }
            if (labelKinds.TryGetValue(id.Value, out byte kind)) { return kind; }
            string label = FrameProfiler.GetLabel(id);
            kind = 0;
            if (label.StartsWith("Auto:", StringComparison.Ordinal))
            {
                kind = label.EndsWith("TickManager.DoSingleTick", StringComparison.Ordinal) ? (byte)2 : (byte)1;
            }
            labelKinds[id.Value] = kind;
            return kind;
        }

        public static void Write(string outDir)
        {
            if (!running) { return; }
            Sample();
            CultureInfo inv = CultureInfo.InvariantCulture;
            string F(double v) => v.ToString("0.####", inv);
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("section\tticks\tframes\tframe_ms\ttick_ms\ttick_ms_per_tick\tdmse_ms\tdmse_tick_ms\tdmse_tick_ms_per_tick\tdmse_share_of_tick\tdmse_draw_ms_per_frame\tmax_frame_ms\tmax_dmse_frame_ms\talloc_mb\tgen0\tgen1\tgen2");
                StringBuilder eb = new StringBuilder();
                eb.AppendLine("section\tlabel\ttotal_ms\ttick_ms\tms_per_tick\tdraw_ms_per_frame\tcalls\tcalls_per_tick\tmax_frame_ms");
                StringBuilder fb = new StringBuilder();
                fb.AppendLine("section\tlabel\tself_ms\ttick_self_ms\tself_ms_per_tick\tcalls\tcalls_per_tick");
                foreach (Section s in sections)
                {
                    if (s.frames == 0) { continue; }
                    double ticks = Math.Max(1, s.ticks);
                    double drawUs = s.dmseUs - s.dmseTickUs;
                    sb.AppendLine(string.Join("\t", s.name, s.ticks.ToString(inv), s.frames.ToString(inv), F(s.frameUs / 1000), F(s.tickUs / 1000),
                        F(s.tickUs / 1000 / ticks), F(s.dmseUs / 1000), F(s.dmseTickUs / 1000), F(s.dmseTickUs / 1000 / ticks),
                        F(s.tickUs > 0 ? s.dmseTickUs / s.tickUs : 0), F(drawUs / 1000 / s.frames), F(s.maxFrameUs / 1000), F(s.maxDmseFrameUs / 1000),
                        F(s.allocBytes / 1048576.0), s.gen0.ToString(inv), s.gen1.ToString(inv), s.gen2.ToString(inv)));
                    foreach (KeyValuePair<int, Stat> pair in s.entries.OrderByDescending(p => p.Value.us))
                    {
                        Stat e = pair.Value;
                        if (e.us < 50) { continue; }
                        eb.AppendLine(string.Join("\t", s.name, FrameProfiler.GetLabel(new ProfilerLabelId(pair.Key)), F(e.us / 1000), F(e.tickUs / 1000),
                            F(e.tickUs / 1000 / ticks), F((e.us - e.tickUs) / 1000 / s.frames), e.calls.ToString(inv), F(e.calls / ticks), F(e.maxFrameUs / 1000)));
                    }
                    foreach (KeyValuePair<int, Stat> pair in s.self.OrderByDescending(p => p.Value.us))
                    {
                        Stat e = pair.Value;
                        if (e.us < 50) { continue; }
                        fb.AppendLine(string.Join("\t", s.name, FrameProfiler.GetLabel(new ProfilerLabelId(pair.Key)), F(e.us / 1000), F(e.tickUs / 1000),
                            F(e.tickUs / 1000 / ticks), e.calls.ToString(inv), F(e.calls / ticks)));
                    }
                }
                File.WriteAllText(Path.Combine(outDir, "perf_sections.tsv"), sb.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(outDir, "perf_entries.tsv"), eb.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(outDir, "perf_self.tsv"), fb.ToString(), Encoding.UTF8);
                AcceptanceDriver.Log($"[perf] wrote {sections.Count} sections; instrumented={ProfilerInstrumentation.InstrumentedMethodCount} "
                    + $"muted={ProfilerInstrumentation.MutedMethodCount} auto-muted labels={FrameProfiler.AutoMutedLabelCount} "
                    + $"scope overhead={FrameProfiler.ScopeOverheadNanoseconds:0.#} ns");
            }
            catch (Exception ex)
            {
                AcceptanceDriver.Log("[perf] could not write report: " + ex);
            }
        }
    }

    /// <summary>Steady-state workloads that only run with <c>-dmse-bvr-profile</c> (ids PF0–PF3).</summary>
    public static class PerfTests
    {
        public static List<AcceptanceTest> Build()
        {
            return new List<AcceptanceTest>
            {
                new AcceptanceTest("PF0", "Perf baseline: colony with no DMSE things, idle", Idle),
                new AcceptanceTest("PF1", "Perf: one of every DMSE building on the pad, idle", EveryBuilding),
                new AcceptanceTest("PF2", "Perf: NPC missile base on the world map, nothing in flight", WorldSiteIdle),
                new AcceptanceTest("PF3", "Perf: twelve enemy cruise missiles against a defended colony", Saturation),
            };
        }

        private static IEnumerable<Wait> Idle(TestContext t)
        {
            int start = Find.TickManager.TicksGame;
            yield return new WaitTicks(2500);
            t.Check(Find.TickManager.TicksGame - start >= 2500, "idle baseline ran 2500 ticks");
        }

        private static IEnumerable<Wait> EveryBuilding(TestContext t)
        {
            CellRect pad = BVRFixture.Pad;
            int x = 1, z = 1, rowHeight = 0, placed = 0, failed = 0, skipped = 0;
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.defName.StartsWith("DMSE_") && d.category == ThingCategory.Building && d.building != null
                    && !d.IsBlueprint && !d.IsFrame && d.thingClass != null)
                .OrderByDescending(d => d.size.z).ThenBy(d => d.defName).ToList();
            foreach (ThingDef def in defs)
            {
                int w = def.size.x, h = def.size.z;
                if (w > 12 || h > 12) { skipped++; continue; }
                if (x + w + 1 > pad.Width) { x = 1; z += rowHeight + 1; rowHeight = 0; }
                if (z + h + 1 > pad.Height) { skipped++; continue; }
                try
                {
                    BVRFixture.SpawnBuilding(def, x + (w - 1) / 2, z + (h - 1) / 2);
                    placed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    t.Note($"could not spawn {def.defName}: {ex.GetType().Name}: {ex.Message}");
                }
                x += w + 1;
                rowHeight = Math.Max(rowHeight, h);
            }
            t.Note($"spawned {placed} of {defs.Count} DMSE building defs ({failed} failed, {skipped} did not fit)");
            yield return BVRFixture.WaitPowered();
            yield return new WaitTicks(2500);
            t.Check(placed > 0, "DMSE buildings stood idle for 2500 ticks");
        }

        private static IEnumerable<Wait> WorldSiteIdle(TestContext t)
        {
            Site site = BVRFixture.SpawnMissileBase(20f, 60f);
            t.Require(site != null, "an NPC missile base exists on the world map");
            yield return new WaitTicks(2500);
            t.Check(!site.Destroyed, "the base stood for 2500 ticks");
        }

        private static IEnumerable<Wait> Saturation(TestContext t)
        {
            BVRFixture.SpawnBuilding(BVRFixture.PhaseArray, 2, 2);
            for (int i = 0; i < 4; i++) { BVRFixture.SpawnLoadedLauncher(10 + i * 5, 2, 6); }
            BVRFixture.SpawnBuilding(BVRFixture.WingBack, 10, 12);
            BVRFixture.SpawnBuilding(BVRFixture.WingBack, 18, 12);
            yield return BVRFixture.WaitPowered();
            yield return new WaitTicks(2);
            PlanetTile source = BVRFixture.TileAtDistance(24f, 30f);
            t.Require(source.Valid, "a launch tile 24-30 tiles out exists");
            List<WorldObject> missiles = new List<WorldObject>();
            for (int i = 0; i < 12; i++)
            {
                missiles.Add(BVRFixture.LaunchEnemyMissile(source));
                yield return new WaitTicks(20);
            }
            WaitUntil gone = new WaitUntil(() => missiles.All(m => m.Destroyed)
                && !BVRFixture.Home.listerThings.AllThings.Any(x => x is Skyfaller || x is Projectile), 12000);
            yield return gone;
            t.Note(gone.TimedOut ? "raid still in the air after 12000 ticks" : "raid resolved");
            yield return new WaitTicks(300);
            t.Check(missiles.Count == 12, "twelve missiles were launched at the colony");
        }
    }
}
