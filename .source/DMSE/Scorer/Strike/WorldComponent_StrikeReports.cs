using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>波次打擊導彈的結局。</summary>
    public enum StrikeOutcome
    {
        Launched,
        /// <summary>在世界層被敵方防空攔截。</summary>
        Intercepted,
        /// <summary>抵達時目標已不在（落空）。</summary>
        Missed,
        /// <summary>抵達目標地圖（之後還會命中或被末端防空擊落）。</summary>
        Arrived,
        Hit,
        /// <summary>抵達後在地圖內被擊落（末端 CIWS 等）。</summary>
        TerminalKilled,
    }

    /// <summary>一個打擊任務的戰果統計（逐波）。</summary>
    public class StrikeReport : IExposable
    {
        public int missionId;
        /// <summary>任務名稱（發射時記下，改名時更新）；空白時用「打擊任務 #id」。</summary>
        public string missionLabel;
        public string targetLabel;
        public string originLabel;
        public bool closed;
        public int lastEventTick;
        public List<int> launched = new List<int>();
        public List<int> intercepted = new List<int>();
        public List<int> missed = new List<int>();
        public List<int> arrived = new List<int>();
        public List<int> hit = new List<int>();
        public List<int> terminalKilled = new List<int>();

        private IEnumerable<List<int>> Lists => new[] { launched, intercepted, missed, arrived, hit, terminalKilled };

        public void Record(int wave, StrikeOutcome outcome)
        {
            if (wave < 0) { wave = 0; }
            foreach (List<int> list in Lists) { while (list.Count <= wave) { list.Add(0); } }
            switch (outcome)
            {
                case StrikeOutcome.Launched: launched[wave]++; break;
                case StrikeOutcome.Intercepted: intercepted[wave]++; break;
                case StrikeOutcome.Missed: missed[wave]++; break;
                case StrikeOutcome.Arrived: arrived[wave]++; break;
                case StrikeOutcome.Hit: hit[wave]++; break;
                case StrikeOutcome.TerminalKilled: terminalKilled[wave]++; break;
            }
        }

        private static int Sum(List<int> list) => list.Sum();

        /// <summary>已有最終結局（攔截、落空、命中、末端擊落）的枚數。</summary>
        public int Resolved => Sum(intercepted) + Sum(missed) + Sum(hit) + Sum(terminalKilled);

        /// <summary>已抵達但還沒有命中／擊落結果的枚數。</summary>
        public int ArrivedPending => System.Math.Max(0, Sum(arrived) - Sum(hit) - Sum(terminalKilled));

        public int Unresolved => System.Math.Max(0, Sum(launched) - Resolved);

        public string Label => missionLabel.NullOrEmpty() ? StrikeMission.DefaultLabel(missionId) : missionLabel;

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("DMSE.Strike.Report.Header".Translate(Label, targetLabel ?? "?", originLabel ?? "?"));
            for (int w = 0; w < launched.Count; w++)
            {
                if (launched[w] == 0) { continue; }
                sb.AppendLine();
                sb.Append("DMSE.Strike.Report.Wave".Translate(w + 1, launched[w], intercepted[w], hit[w], terminalKilled[w], missed[w]));
            }
            int unknown = Unresolved;
            if (unknown > 0)
            {
                sb.AppendLine();
                sb.Append("DMSE.Strike.Report.Unknown".Translate(unknown));
            }
            return sb.ToString();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref missionId, "missionId");
            Scribe_Values.Look(ref missionLabel, "missionLabel");
            Scribe_Values.Look(ref targetLabel, "targetLabel");
            Scribe_Values.Look(ref originLabel, "originLabel");
            Scribe_Values.Look(ref closed, "closed");
            Scribe_Values.Look(ref lastEventTick, "lastEventTick");
            Scribe_Collections.Look(ref launched, "launched", LookMode.Value);
            Scribe_Collections.Look(ref intercepted, "intercepted", LookMode.Value);
            Scribe_Collections.Look(ref missed, "missed", LookMode.Value);
            Scribe_Collections.Look(ref arrived, "arrived", LookMode.Value);
            Scribe_Collections.Look(ref hit, "hit", LookMode.Value);
            Scribe_Collections.Look(ref terminalKilled, "terminalKilled", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                launched ??= new List<int>(); intercepted ??= new List<int>(); missed ??= new List<int>();
                arrived ??= new List<int>(); hit ??= new List<int>(); terminalKilled ??= new List<int>();
            }
        }
    }

    /// <summary>
    /// 波次打擊的全域帳（docs/BVR_波次打擊規劃.md §10-4）：配發全域唯一的任務 id，並統計每個任務各波導彈的結局。
    /// 任務結束（全部發射、略過或取消）且每枚導彈都有結局後發一封戰果信；已抵達但遲遲沒有命中結果的
    /// （例如地圖被移除）在最後一次事件後 3000 ticks 以「結果不明」結算。
    /// </summary>
    public class WorldComponent_StrikeReports : WorldComponent
    {
        public const int StaleTicks = 3000;

        private int nextMissionId = 1000;
        private List<StrikeReport> reports = new List<StrikeReport>();

        public static WorldComponent_StrikeReports Current => Find.World?.GetComponent<WorldComponent_StrikeReports>();

        public WorldComponent_StrikeReports(World world) : base(world) { }

        public int NewMissionId() => nextMissionId++;

        public StrikeReport ReportFor(int missionId) => reports.FirstOrDefault(r => r.missionId == missionId);

        private StrikeReport GetOrCreate(int missionId)
        {
            StrikeReport report = ReportFor(missionId);
            if (report == null)
            {
                report = new StrikeReport { missionId = missionId };
                reports.Add(report);
            }
            return report;
        }

        /// <summary>任務的稱呼：有戰果帳（第一枚發射後）就用帳上的名稱。供只知道任務 id 的航跡使用。</summary>
        public static string MissionLabel(int missionId) => Current?.ReportFor(missionId)?.Label ?? StrikeMission.DefaultLabel(missionId);

        /// <summary>第一枚發射時建立戰果帳。</summary>
        public void RecordLaunch(int missionId, int wave, string targetLabel, string originLabel, string missionLabel = null)
        {
            StrikeReport report = GetOrCreate(missionId);
            if (!missionLabel.NullOrEmpty()) { report.missionLabel = missionLabel; }
            report.targetLabel ??= targetLabel;
            report.originLabel ??= originLabel;
            Record(missionId, wave, StrikeOutcome.Launched);
        }

        public static void Report(int missionId, int wave, StrikeOutcome outcome)
        {
            if (missionId < 0) { return; }
            Current?.Record(missionId, wave, outcome);
        }

        public void Record(int missionId, int wave, StrikeOutcome outcome)
        {
            StrikeReport report = outcome == StrikeOutcome.Launched ? GetOrCreate(missionId) : ReportFor(missionId);
            if (report == null) { return; }
            report.Record(wave, outcome);
            report.lastEventTick = Find.TickManager.TicksGame;
        }

        /// <summary>任務不會再發射（完成或取消）：之後等所有導彈有結局再發戰果信。</summary>
        public void Close(int missionId)
        {
            StrikeReport report = ReportFor(missionId);
            if (report == null) { return; }
            report.closed = true;
            report.lastEventTick = Find.TickManager.TicksGame;
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int now = Find.TickManager.TicksGame;
            if (now % 250 != 0 || reports.Count == 0) { return; }
            foreach (StrikeReport report in reports.ToList())
            {
                if (!report.closed) { continue; }
                bool done = report.Unresolved == 0
                    || (report.Unresolved == report.ArrivedPending && now - report.lastEventTick >= StaleTicks);
                if (!done) { continue; }
                reports.Remove(report);
                Find.LetterStack.ReceiveLetter("DMSE.Strike.Report.Label".Translate(report.Label), report.Summary(),
                    LetterDefOf.NeutralEvent);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextMissionId, "nextMissionId", 1000);
            Scribe_Collections.Look(ref reports, "strikeReports", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { reports ??= new List<StrikeReport>(); }
        }
    }
}
