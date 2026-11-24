using System.Collections.Generic;
using Fortified;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一組要釋放的子彈（<see cref="MissileBehaviorProps_ReleaseChildren.children"/> 的一項）。
    /// </summary>
    public class MissileReleaseGroup
    {
        public MirvRole role = MirvRole.Warhead;

        /// <summary>子彈彈體；null = 與母彈相同。</summary>
        public MissileBodyDef body;

        public int count = 1;

        /// <summary>明確指定的子彈部件；有值時不繼承、也不用預設部件。</summary>
        public List<MissilePartDef> parts;

        /// <summary>
        /// 從母彈已裝部件複製的類別（例如戰鬥部、導引），不檢查 <see cref="MissilePartDef.compatibleBodies"/>。
        /// 載荷（Payload）一律不繼承，避免子彈再分裂。
        /// </summary>
        public List<MissilePartCategory> inheritCategories;

        public MissileConfig MakeConfig(MissileConfig parentConfig)
        {
            MissileBodyDef childBody = body ?? parentConfig?.body;
            if (childBody == null) { return null; }
            if (parts != null)
            {
                MissileConfig explicitConfig = new MissileConfig(childBody);
                explicitConfig.parts.AddRange(parts);
                return explicitConfig;
            }
            MissileConfig config = MissileConfig.NewWithDefaults(childBody);
            if (inheritCategories != null && parentConfig != null)
            {
                foreach (MissilePartCategory category in inheritCategories)
                {
                    if (category == MissilePartCategory.Payload) { continue; }
                    MissilePartDef part = parentConfig.PartFor(category);
                    if (part != null) { config.SetPart(category, part); }
                }
            }
            return config;
        }
    }

    /// <summary>
    /// 彈頭匯流排（post-boost vehicle）：飛到釋放點後依序放出分導重返載具與誘餌，各自成為獨立航跡；
    /// 放完後母彈退場（<see cref="retireParent"/>）。依賴裝配系統的彈體與部件，所以跟 <see cref="MissileBodyDef"/> 一起遷移。
    /// 見 docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §5。
    /// <c>&lt;li Class="DMSE.MissileBehaviorProps_ReleaseChildren"&gt;</c>
    /// </summary>
    public class MissileBehaviorProps_ReleaseChildren : MissileBehaviorProps
    {
        /// <summary>抵達前多少 ticks 開始釋放；&gt;0 時優先於 <see cref="atProgress"/>。</summary>
        public int ticksBeforeArrival;

        /// <summary>沒有設定 <see cref="ticksBeforeArrival"/> 時，在整條航線的哪個進度開始釋放（0–1）。</summary>
        public float atProgress = 0.5f;

        /// <summary>航線進度下限：短程發射不會一升空就分導。</summary>
        public float minProgress = 0.25f;

        /// <summary>相鄰兩枚子彈的釋放間隔（ticks）。</summary>
        public int releaseIntervalTicks = 20;

        /// <summary>打亂釋放順序（彈頭與誘餌交錯，防空無法從抵達順序分辨）。</summary>
        public bool shuffleOrder = true;

        /// <summary>放完後銷毀母彈（分導）；false = 母彈繼續飛，作為群組裡的真彈頭（純誘餌載荷）。</summary>
        public bool retireParent;

        public List<MissileReleaseGroup> children;

        // ---- 單組簡寫（舊介面）：children 為空時轉成一組 role = Warhead ----
        public int count = 1;
        /// <summary>子彈彈體；null = 與母彈相同。</summary>
        public MissileBodyDef childBody;
        /// <summary>子彈部件；null = 使用子彈彈體的預設部件。</summary>
        public List<MissilePartDef> childParts;

        public MissileBehaviorProps_ReleaseChildren() { behaviorClass = typeof(MissileBehavior_ReleaseChildren); }

        private List<MissileReleaseGroup> resolvedGroups;
        public List<MissileReleaseGroup> Groups
        {
            get
            {
                if (resolvedGroups != null) { return resolvedGroups; }
                if (!children.NullOrEmpty()) { return resolvedGroups = children; }
                return resolvedGroups = new List<MissileReleaseGroup>
                {
                    new MissileReleaseGroup { role = MirvRole.Warhead, body = childBody, count = count, parts = childParts }
                };
            }
        }

        public int TotalCount
        {
            get
            {
                int total = 0;
                foreach (MissileReleaseGroup g in Groups) { total += System.Math.Max(0, g.count); }
                return total;
            }
        }

        public int CountOf(MirvRole role)
        {
            int total = 0;
            foreach (MissileReleaseGroup g in Groups) { if (g.role == role) { total += System.Math.Max(0, g.count); } }
            return total;
        }

        public override IEnumerable<string> ConfigErrors(Def parent)
        {
            foreach (string error in base.ConfigErrors(parent)) { yield return error; }
            if (TotalCount < 1) { yield return $"{parent.defName}: release count must be at least 1"; }
            if (ticksBeforeArrival <= 0 && (atProgress <= 0f || atProgress >= 1f)) { yield return $"{parent.defName}: atProgress must be between 0 and 1"; }
            if (minProgress < 0f || minProgress >= 1f) { yield return $"{parent.defName}: minProgress must be in [0, 1)"; }
            if (releaseIntervalTicks < 1) { yield return $"{parent.defName}: releaseIntervalTicks must be at least 1"; }
            foreach (MissileReleaseGroup g in Groups)
            {
                if (g == null) { yield return $"{parent.defName}: null release group"; continue; }
                if (g.role == MirvRole.None) { yield return $"{parent.defName}: release group role must be Warhead or Decoy"; }
                if (g.body == null && parent is MissileBodyDef)
                {
                    yield return $"{parent.defName}: a body releasing children of its own body would recurse; set the child body";
                }
                if (g.inheritCategories != null && g.inheritCategories.Contains(MissilePartCategory.Payload))
                {
                    yield return $"{parent.defName}: release groups never inherit the Payload slot (it would release again)";
                }
            }
        }
    }

    public class MissileBehavior_ReleaseChildren : MissileBehavior
    {
        /// <summary>開始釋放的 tick；-1 = 尚未開始。</summary>
        private int releaseStartTick = -1;
        private int releasedCount;
        private bool finished;
        /// <summary>釋放順序：每項是 <see cref="MissileBehaviorProps_ReleaseChildren.Groups"/> 的索引。</summary>
        private List<int> queue;

        private MissileBehaviorProps_ReleaseChildren Props => (MissileBehaviorProps_ReleaseChildren)props;

        public bool Started => releaseStartTick >= 0;
        public bool Finished => finished;

        private bool Due()
        {
            float progress = track.RouteProgress;
            if (progress >= 1f) { return true; } // 已到目的地：一定要放（短程發射）
            if (progress < Props.minProgress) { return false; }
            return Props.ticksBeforeArrival > 0
                ? track.TicksToArrival <= Props.ticksBeforeArrival
                : progress >= Props.atProgress;
        }

        public override void FlightTick(int elapsedTicks)
        {
            if (finished) { return; }
            if (!(track is MissileWorldObject parent)) { return; }
            int now = Find.TickManager.TicksGame;
            if (!Started)
            {
                if (!Due()) { return; }
                releaseStartTick = now;
                BuildQueue(parent);
                if (parent.mirvGroupId < 0) { parent.mirvGroupId = parent.ID; }
                if (!Props.retireParent) { parent.mirvRole = MirvRole.Warhead; }
                WorldComponent_MirvGroups.Current?.Create(parent, ExpectedWarheads, Props.CountOf(MirvRole.Decoy));
                if (Prefs.DevMode)
                {
                    Log.Message($"[DMSE MIRV] {parent.Label} #{parent.ID} 開始釋放 {queue.Count} 枚（彈頭 {Props.CountOf(MirvRole.Warhead)}、誘餌 {Props.CountOf(MirvRole.Decoy)}），剩 {parent.TicksToArrival} ticks 抵達");
                }
            }

            int interval = System.Math.Max(1, Props.releaseIntervalTicks);
            int remaining = queue.Count - releasedCount;
            // 照間隔應放出的數量；來不及在抵達前放完時一次放完。
            int due = (now - releaseStartTick) / interval + 1;
            if (parent.TicksToArrival <= remaining * interval) { due = queue.Count; }
            while (releasedCount < queue.Count && releasedCount < due)
            {
                Release(parent, Props.Groups[queue[releasedCount]]);
                releasedCount++;
            }
            if (releasedCount < queue.Count) { return; }

            finished = true;
            if (Prefs.DevMode)
            {
                Log.Message($"[DMSE MIRV] {parent.Label} #{parent.ID} 釋放完畢" + (Props.retireParent ? "，母彈退場" : "，母彈繼續飛"));
            }
            if (Props.retireParent && !parent.Destroyed)
            {
                // 分離閃光只給看得到這條航跡的玩家（自己的彈或已被偵測），不洩漏未偵測航跡的位置。
                if (parent.VisibleToPlayer) { WorldComponent_BVRNetwork.Current?.AddFlash(parent.DrawPos); }
                parent.Destroy();
            }
        }

        /// <summary>群組裡的彈頭總數：放出的彈頭，加上不退場時母彈自己。</summary>
        private int ExpectedWarheads => Props.CountOf(MirvRole.Warhead) + (Props.retireParent ? 0 : 1);

        private void Release(MissileWorldObject parent, MissileReleaseGroup group)
        {
            MissileConfig config = group.MakeConfig(parent.config);
            // 彈頭都帶打擊任務 id（警示依任務分組）；戰果由分導群組彙整後一次回報（計畫 §6.4），子彈自己不回報。
            if (group.role != MirvRole.Decoy)
            {
                parent.SpawnChild(config, group.role, parent.mirvGroupId, inheritsRole: true);
                return;
            }
            // 誘餌模仿同組彈頭：有放出的彈頭就像重返載具，否則像繼續飛的母彈（計畫 §5.6）。
            MissileBodyDef warheadBody = null;
            foreach (MissileReleaseGroup g in Props.Groups)
            {
                if (g.role == MirvRole.Warhead) { warheadBody = g.body ?? parent.config?.body; break; }
            }
            MissileBodyDef mimic = warheadBody ?? parent.config?.body;
            ThingDef mimicIncoming = warheadBody != null ? warheadBody.incomingSkyfaller : parent.IncomingDef;
            // 誘餌也帶打擊任務 id：玩家飛彈警示與 NPC 警戒把它們和彈頭算在同一波（誘餌自己不回報戰果）。
            parent.SpawnChild(config, MirvRole.Decoy, parent.mirvGroupId, inheritsRole: true, mimic: mimic, mimicIncomingDef: mimicIncoming);
        }

        /// <summary>還沒放出的彈頭數（匯流排在放完前被毀時記入群組）。</summary>
        private int UnreleasedWarheads
        {
            get
            {
                if (!Started || finished || queue == null) { return 0; }
                int n = 0;
                for (int i = releasedCount; i < queue.Count; i++)
                {
                    if (Props.Groups[queue[i]].role == MirvRole.Warhead) { n++; }
                }
                return n;
            }
        }

        public override void OnIntercepted(Faction by)
            => WorldComponent_MirvGroups.RecordUnreleased((track as MissileWorldObject)?.mirvGroupId ?? -1, UnreleasedWarheads, StrikeOutcome.Intercepted);

        public override void OnJammedOut(Faction by)
            => WorldComponent_MirvGroups.RecordUnreleased((track as MissileWorldObject)?.mirvGroupId ?? -1, UnreleasedWarheads, StrikeOutcome.Missed);

        private void BuildQueue(MissileWorldObject parent)
        {
            queue = new List<int>();
            List<MissileReleaseGroup> groups = Props.Groups;
            for (int g = 0; g < groups.Count; g++)
            {
                for (int i = 0; i < groups[g].count; i++) { queue.Add(g); }
            }
            if (Props.shuffleOrder && queue.Count > 1)
            {
                Rand.PushState(Gen.HashCombineInt(parent.ID, releaseStartTick));
                try { queue.Shuffle(); }
                finally { Rand.PopState(); }
            }
        }

        /// <summary>攻擊方檢視面板：釋放進度或預定時機。</summary>
        public string InspectLine()
        {
            int warheads = Props.CountOf(MirvRole.Warhead);
            int decoys = Props.CountOf(MirvRole.Decoy);
            if (finished) { return null; }
            if (Started)
            {
                return "DMSE.Mirv.Releasing".Translate(releasedCount, queue?.Count ?? Props.TotalCount);
            }
            int ticks = Props.ticksBeforeArrival > 0
                ? System.Math.Max(0, track.TicksToArrival - Props.ticksBeforeArrival)
                : -1;
            string when = ticks >= 0 ? ticks.ToStringTicksToPeriod() : Props.atProgress.ToStringPercent();
            return "DMSE.Mirv.WillRelease".Translate(warheads, decoys, when);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref releaseStartTick, "releaseStartTick", -1);
            Scribe_Values.Look(ref releasedCount, "releasedCount", 0);
            Scribe_Values.Look(ref finished, "finished", false);
            Scribe_Collections.Look(ref queue, "queue", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && releaseStartTick >= 0 && queue == null)
            {
                // 佇列遺失（不該發生）：視為已放完，避免重複釋放。
                finished = true;
            }
        }
    }
}
