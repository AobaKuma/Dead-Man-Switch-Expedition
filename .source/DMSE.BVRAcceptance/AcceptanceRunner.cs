using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE.BVRAcceptance
{
    // ======================================================================
    //  Waits (yielded by test scripts)
    // ======================================================================

    public abstract class Wait
    {
        public abstract bool Done { get; }
    }

    public sealed class WaitTicks : Wait
    {
        private readonly int until;
        public WaitTicks(int ticks) { until = Find.TickManager.TicksGame + Math.Max(1, ticks); }
        public override bool Done => Find.TickManager.TicksGame >= until;
    }

    public sealed class WaitFrames : Wait
    {
        private readonly int until;
        public WaitFrames(int frames) { until = Time.frameCount + Math.Max(1, frames); }
        public override bool Done => Time.frameCount >= until;
    }

    /// <summary>Waits on real time (works while the game is paused).</summary>
    public sealed class WaitRealtime : Wait
    {
        private readonly Func<bool> condition;
        private readonly float until;
        public WaitRealtime(Func<bool> condition, float seconds)
        {
            this.condition = condition;
            until = Time.realtimeSinceStartup + seconds;
        }
        public override bool Done => condition() || Time.realtimeSinceStartup >= until;
    }

    /// <summary>Waits until a condition holds or a tick timeout passes; check <see cref="TimedOut"/> afterwards.</summary>
    public sealed class WaitUntil : Wait
    {
        private readonly Func<bool> condition;
        private readonly int timeoutTick;
        public bool TimedOut { get; private set; }
        public WaitUntil(Func<bool> condition, int timeoutTicks)
        {
            this.condition = condition;
            timeoutTick = Find.TickManager.TicksGame + timeoutTicks;
        }
        public override bool Done
        {
            get
            {
                if (condition()) { return true; }
                if (Find.TickManager.TicksGame >= timeoutTick) { TimedOut = true; return true; }
                return false;
            }
        }
    }

    /// <summary>Aborts the current test with a failure.</summary>
    public sealed class AcceptanceAbort : Exception
    {
        public AcceptanceAbort(string message) : base(message) { }
    }

    // ======================================================================
    //  Test definition / context
    // ======================================================================

    public sealed class AcceptanceTest
    {
        public string id;
        public string title;
        public Func<TestContext, IEnumerable<Wait>> body;
        public AcceptanceTest(string id, string title, Func<TestContext, IEnumerable<Wait>> body)
        {
            this.id = id; this.title = title; this.body = body;
        }
    }

    public sealed class TestContext
    {
        public readonly AcceptanceTest test;
        public readonly List<string> failures = new List<string>();
        public readonly List<string> notes = new List<string>();
        public int checks;

        /// <summary>While true the driver does not force the game to keep running (TV-guidance pause checks).</summary>
        public bool allowPause;

        public TestContext(AcceptanceTest test) { this.test = test; }

        public bool Check(bool condition, string what)
        {
            checks++;
            if (!condition) { failures.Add(what); AcceptanceDriver.Log($"    FAIL: {what}"); }
            else { AcceptanceDriver.Log($"    ok:   {what}"); }
            return condition;
        }

        public void Require(bool condition, string what)
        {
            if (!Check(condition, what)) { throw new AcceptanceAbort(what); }
        }

        public void Note(string text)
        {
            notes.Add(text);
            AcceptanceDriver.Log($"    note: {text}");
        }

        public void Screenshot(string name)
        {
            AcceptanceDriver.Screenshot(test.id + "_" + name);
        }
    }

    // ======================================================================
    //  GameComponent hook (auto-instantiated by RimWorld for every game)
    // ======================================================================

    public class GameComponent_BVRAcceptance : GameComponent
    {
        public GameComponent_BVRAcceptance(Game game) { }

        public override void GameComponentUpdate()
        {
            AcceptanceDriver.Update();
        }

        public override void GameComponentTick()
        {
            AcceptanceDriver.Tick();
        }
    }

    /// <summary>Records the first game tick at which a condition holds (sampled every tick, not every frame).</summary>
    public sealed class TickWatch
    {
        public readonly Func<bool> condition;
        public int FirstTick = -1;
        public TickWatch(Func<bool> condition) { this.condition = condition; }
        public bool Seen => FirstTick >= 0;
    }

    // ======================================================================
    //  Driver
    // ======================================================================

    public static class AcceptanceDriver
    {
        public const string ArgKey = "dmse-bvr-acceptance";
        /// <summary>Optional comma-separated test ids to run (e.g. <c>-dmse-bvr-only=S3,W6</c>).</summary>
        public const string OnlyArgKey = "dmse-bvr-only";

        private static bool? enabled;
        private static string outDir;
        private static StreamWriter writer;

        private static List<AcceptanceTest> tests;
        private static int index = -1;
        private static TestContext ctx;
        private static IEnumerator<Wait> routine;
        private static Wait wait;
        private static Game lastGame;
        private static int settleFrames;
        private static bool finished;
        private static float quitAt = -1f;
        private static readonly List<(string id, string title, bool pass, string detail)> results
            = new List<(string, string, bool, string)>();
        private static readonly List<string> screenshots = new List<string>();

        public static bool Enabled
        {
            get
            {
                if (enabled == null)
                {
                    enabled = GenCommandLine.TryGetCommandLineArg(ArgKey, out outDir) && !outDir.NullOrEmpty();
                }
                return enabled.Value;
            }
        }

        public static void Log(string line)
        {
            string stamped = Current.Game != null && Find.TickManager != null
                ? $"[t={Find.TickManager.TicksGame,7}] {line}" : line;
            Verse.Log.Message("[DMSE-ACCEPT] " + stamped);
            try
            {
                if (writer == null)
                {
                    Directory.CreateDirectory(outDir);
                    writer = new StreamWriter(Path.Combine(outDir, "bvr_acceptance_log.txt"), append: false, Encoding.UTF8);
                    writer.AutoFlush = true;
                }
                writer.WriteLine(stamped);
            }
            catch (Exception) { }
        }

        private static readonly List<TickWatch> watches = new List<TickWatch>();

        /// <summary>Watch a condition every game tick until the current test ends.</summary>
        public static TickWatch Watch(Func<bool> condition)
        {
            TickWatch w = new TickWatch(condition);
            watches.Add(w);
            return w;
        }

        public static void Tick()
        {
            if (!Enabled || watches.Count == 0) { return; }
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < watches.Count; i++)
            {
                TickWatch w = watches[i];
                if (w.FirstTick >= 0) { continue; }
                try { if (w.condition()) { w.FirstTick = now; } }
                catch (Exception) { }
            }
        }

        public static void Screenshot(string name)
        {
            string file = "DMSE_BVR_" + name;
            ScreenshotTaker.TakeNonSteamShot(file);
            screenshots.Add(file + ".png");
            Log($"    screenshot: {file}.png");
        }

        public static void Update()
        {
            if (!Enabled || finished)
            {
                if (finished && quitAt > 0f && Time.realtimeSinceStartup >= quitAt)
                {
                    quitAt = -1f;
                    Root.Shutdown();
                }
                return;
            }
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) { return; }
            if (LongEventHandler.AnyEventNowOrWaiting) { return; }

            if (Current.Game != lastGame)
            {
                lastGame = Current.Game;
                settleFrames = 120;
                Log($"Game ready (map {Find.CurrentMap.uniqueID}, tile {Find.CurrentMap.Tile}).");
                if (tests == null)
                {
                    BVRFixture.PrepareGameOnce();
                    tests = AcceptanceSuite.Build();
                    if (GenCommandLine.TryGetCommandLineArg(OnlyArgKey, out string only) && !only.NullOrEmpty())
                    {
                        HashSet<string> ids = new HashSet<string>(only.Split(',').Select(s => s.Trim()));
                        tests = tests.Where(x => ids.Contains(x.id)).ToList();
                    }
                    Log($"Running {tests.Count} acceptance tests.");
                }
                else
                {
                    // A test reloaded the game; let it continue from where it left off.
                    BVRFixture.OnGameReloaded();
                }
            }
            if (settleFrames > 0) { settleFrames--; return; }

            CloseBlockingWindows();
            if (ctx == null || !ctx.allowPause)
            {
                if (Find.TickManager.CurTimeSpeed != TimeSpeed.Ultrafast) { Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast; }
            }

            if (wait != null && !wait.Done) { return; }
            wait = null;

            if (routine == null)
            {
                index++;
                if (index >= tests.Count) { Finish(); return; }
                AcceptanceTest test = tests[index];
                ctx = new TestContext(test);
                Log("");
                Log($"=== {test.id}: {test.title}");
                try
                {
                    routine = test.body(ctx).GetEnumerator();
                }
                catch (Exception ex)
                {
                    EndTest(ex);
                    return;
                }
            }

            try
            {
                if (routine.MoveNext())
                {
                    wait = routine.Current;
                }
                else
                {
                    EndTest(null);
                }
            }
            catch (Exception ex)
            {
                EndTest(ex);
            }
        }

        private static void EndTest(Exception ex)
        {
            if (ex is AcceptanceAbort abort)
            {
                Log($"    ABORT: {abort.Message}");
            }
            else if (ex != null)
            {
                ctx.failures.Add("exception: " + ex.GetType().Name + ": " + ex.Message);
                Log($"    EXCEPTION: {ex}");
            }
            bool pass = ctx.failures.Count == 0 && ctx.checks > 0;
            if (ctx.checks == 0 && ex == null) { ctx.failures.Add("no checks ran"); }
            string detail = pass ? $"{ctx.checks} checks" : string.Join(" | ", ctx.failures);
            results.Add((ctx.test.id, ctx.test.title, pass, detail));
            Log($"--- {ctx.test.id}: {(pass ? "PASS" : "FAIL")} ({detail})");

            try { BVRFixture.CleanupAfterTest(); }
            catch (Exception cleanupEx) { Log($"    cleanup exception: {cleanupEx}"); }

            watches.Clear();
            routine = null;
            wait = null;
            ctx = null;
        }

        private static void Finish()
        {
            finished = true;
            int passed = results.Count(r => r.pass);
            Log("");
            Log($"=== SUMMARY: {passed}/{results.Count} passed");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("id\tresult\ttitle\tdetail");
            foreach (var r in results)
            {
                Log($"{(r.pass ? "PASS" : "FAIL")}  {r.id}  {r.title}  -- {r.detail}");
                sb.AppendLine($"{r.id}\t{(r.pass ? "PASS" : "FAIL")}\t{r.title}\t{r.detail}");
            }
            try
            {
                File.WriteAllText(Path.Combine(outDir, "bvr_acceptance_results.tsv"), sb.ToString(), Encoding.UTF8);
                File.WriteAllLines(Path.Combine(outDir, "bvr_acceptance_screenshots.txt"),
                    screenshots.Select(s => Path.Combine(GenFilePaths.ScreenshotFolderPath, s)));
                File.WriteAllText(Path.Combine(outDir, "bvr_acceptance_done.txt"), DateTime.Now.ToString("o"));
            }
            catch (Exception ex) { Log("could not write results: " + ex); }
            BVRFixture.DeleteTestSave();
            // Give the last screenshot a moment to flush, then quit.
            quitAt = Time.realtimeSinceStartup + 3f;
        }

        /// <summary>Close dialogs that would block input (scenario intro, mod popups); keep everything else.</summary>
        private static void CloseBlockingWindows()
        {
            List<Window> windows = Find.WindowStack.Windows.ToList();
            foreach (Window w in windows)
            {
                if (w.layer == WindowLayer.Dialog || w.layer == WindowLayer.SubSuper || w.layer == WindowLayer.Super)
                {
                    if (w is Dialog_MessageBox || w is Dialog_NodeTree || w.forcePause || w.absorbInputAroundWindow)
                    {
                        string detail = w is Dialog_MessageBox box ? " [" + box.text + "]" : string.Empty;
                        Log($"closing window {w.GetType().Name}{detail}");
                        Find.WindowStack.TryRemove(w, doCloseSound: false);
                    }
                }
            }
        }
    }
}
