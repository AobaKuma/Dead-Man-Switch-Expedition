using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_MissileConfig : CompProperties
    {
        /// <summary>預設彈體；為 null 時取第一個 MissileBodyDef。</summary>
        public MissileBodyDef defaultBody;

        public CompProperties_MissileConfig()
        {
            compClass = typeof(CompMissileConfig);
        }

        /// <summary>
        /// 資訊卡：實物顯示已套用的構型，物品 def 顯示出廠預設構型。
        /// 只在 props 這裡產生（原版 thing 卡也會走 def 的 SpecialDisplayStats），comp 不再重複。
        /// </summary>
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            foreach (StatDrawEntry e in base.SpecialDisplayStats(req)) { yield return e; }
            MissileConfig config = null;
            CompMissileConfig comp = req.Thing?.TryGetComp<CompMissileConfig>();
            if (comp != null)
            {
                comp.EnsureInit();
                config = comp.config;
            }
            if (config?.body == null)
            {
                config = MissileConfig.NewWithDefaults(defaultBody ?? DefDatabase<MissileBodyDef>.AllDefsListForReading.FirstOrDefault());
            }
            foreach (StatDrawEntry e in MissileInfoCardUtility.MissileStats(config, req.Def as ThingDef ?? req.Thing?.def)) { yield return e; }
        }
    }

    /// <summary>
    /// 保存導彈的裝配設定。
    /// config = 已套用（影響發射效果）；pending = 玩家於 ITab 設定的目標。
    /// 兩者不同則需要小人搬運資源裝配；裝配完成後 config = pending。
    /// </summary>
    public class CompMissileConfig : ThingComp, IMissileAssemblyTarget
    {
        public MissileConfig config = new MissileConfig();   // 已套用
        public MissileConfig pending = new MissileConfig();  // 待裝配目標

        /// <summary>
        /// 玩家已按下「確認裝配」按鈕。
        /// WorkGiver 僅在此旗標為 true 且 NeedsAssembly 時才派工。
        /// 換部件時自動清除，需玩家重新確認。
        /// </summary>
        public bool assemblyConfirmed = false;

        // 已搬運到此導彈、尚未消耗的資源。
        public Dictionary<ThingDef, int> delivered = new Dictionary<ThingDef, int>();

        /// <summary>
        /// 彈體預設零件是否已播種。只在「建立」時播種一次（<see cref="PostPostMake"/>）。
        /// 序列化預設 true：舊存檔讀入後視為已播種（不會白送零件），
        /// 玩家刻意拆空的導彈也不會在開啟 ITab 時自己長回來。
        /// </summary>
        public bool defaultsSeeded = true;

        // 施工進度：workDone 只對 workDoneFor（開工當下的 pending）有效；
        // 玩家在 ITab 改了目標構型，舊進度自動作廢，不必在每個改動點重置。
        private float workDone;
        private MissileConfig workDoneFor;

        /// <summary>
        /// 設計名稱（組裝台出廠時寫入）。只在目前 config 等於命名當下的構型（designNameFor）時顯示，
        /// 改裝後自動失效。
        /// </summary>
        private string designName;
        private MissileConfig designNameFor;

        /// <summary>目前有效的設計名稱；改裝過或沒有命名時為 null。</summary>
        public string DesignName
            => !designName.NullOrEmpty() && designNameFor != null && ConfigsEqual(designNameFor, config) ? designName : null;

        public void SetDesignName(string name)
        {
            EnsureInit();
            designName = name.NullOrEmpty() ? null : name;
            designNameFor = designName != null ? config.Clone() : null;
        }

        public CompProperties_MissileConfig Props => (CompProperties_MissileConfig)props;

        public void EnsureInit()
        {
            if (config == null) { config = new MissileConfig(); }
            if (config.body == null)
            {
                config.body = Props.defaultBody ?? DefDatabase<MissileBodyDef>.AllDefsListForReading.FirstOrDefault();
            }
            if (pending == null) { pending = new MissileConfig(); }
            if (pending.body == null) { pending.CopyFrom(config); }
            if (delivered == null) { delivered = new Dictionary<ThingDef, int>(); }

            // 建立時播種預設零件：建造價格已包含它們的材料，所以 config 與 pending 同步、
            // NeedsAssembly 為 false → 一造好就能發射。之後重新裝配才需再搬料。
            if (!defaultsSeeded)
            {
                defaultsSeeded = true;
                if (config.parts == null || config.parts.Count == 0)
                {
                    MissileConfig seeded = MissileConfig.NewWithDefaults(config.body);
                    config.parts = seeded.parts;
                    pending.body = config.body;
                    pending.parts = new List<MissilePartDef>(seeded.parts);
                }
            }
        }

        // ---- 裝配狀態 ----
        public bool NeedsAssembly
        {
            get
            {
                EnsureInit();
                return !ConfigsEqual(config, pending);
            }
        }

        public List<MissilePartDef> AddedParts()
            => pending.parts.Where(p => p != null && !config.parts.Contains(p)).ToList();

        public List<MissilePartDef> RemovedParts()
            => config.parts.Where(p => p != null && !pending.parts.Contains(p)).ToList();

        /// <summary>裝配 pending 所需的全部資源（新增部件的成本總和）。</summary>
        public Dictionary<ThingDef, int> TotalRequired()
        {
            Dictionary<ThingDef, int> d = new Dictionary<ThingDef, int>();
            foreach (MissilePartDef part in AddedParts())
            {
                if (part.costList == null) { continue; }
                foreach (ThingDefCountClass c in part.costList)
                {
                    d.TryGetValue(c.thingDef, out int cur);
                    d[c.thingDef] = cur + c.count;
                }
            }
            return d;
        }

        /// <summary>尚需搬運的資源（總需求扣除已搬運）。</summary>
        public Dictionary<ThingDef, int> StillNeeded()
        {
            Dictionary<ThingDef, int> need = TotalRequired();
            Dictionary<ThingDef, int> result = new Dictionary<ThingDef, int>();
            foreach (KeyValuePair<ThingDef, int> kv in need)
            {
                delivered.TryGetValue(kv.Key, out int got);
                int remain = kv.Value - got;
                if (remain > 0) { result[kv.Key] = remain; }
            }
            return result;
        }

        public bool ResourcesComplete => StillNeeded().Count == 0;

        // ---- IMissileAssemblyTarget ----
        public Thing Parent => parent;

        public bool WantsWork => assemblyConfirmed && NeedsAssembly;

        public int WorkAmount
        {
            get
            {
                EnsureInit();
                return pending?.body != null ? pending.body.assembleWorkAmount : 600;
            }
        }

        public float WorkDone
        {
            get
            {
                EnsureInit();
                return workDoneFor != null && ConfigsEqual(workDoneFor, pending) ? workDone : 0f;
            }
            set
            {
                EnsureInit();
                workDone = value;
                workDoneFor = value > 0f ? pending.Clone() : null;
            }
        }

        /// <summary>改裝只吃小人速度：物品、儲存架、發射井都不是工作台，沒有設施可連。</summary>
        public float WorkSpeedFor(Pawn pawn) => MissileAssemblyUtility.PawnWorkSpeed(pawn);

        public bool CanFinishNow => true;

        public void Deposit(ThingDef def, int count)
        {
            EnsureInit();
            delivered.TryGetValue(def, out int cur);
            delivered[def] = cur + count;
        }

        /// <summary>完成裝配：消耗需求資源、退還移除部件（部分）與多餘搬運，套用 pending。</summary>
        public void ApplyAssembly()
        {
            EnsureInit();
            Dictionary<ThingDef, int> required = TotalRequired();

            // 退還移除部件的部分資源。
            float refundFrac = pending.body != null ? pending.body.refundFraction : 0.5f;
            Dictionary<ThingDef, int> refund = new Dictionary<ThingDef, int>();
            foreach (MissilePartDef removed in RemovedParts())
            {
                if (removed.costList == null) { continue; }
                foreach (ThingDefCountClass c in removed.costList)
                {
                    int amt = Mathf.FloorToInt(c.count * refundFrac);
                    if (amt <= 0) { continue; }
                    refund.TryGetValue(c.thingDef, out int cur);
                    refund[c.thingDef] = cur + amt;
                }
            }

            // 多搬運的資源原樣退還。
            foreach (KeyValuePair<ThingDef, int> kv in delivered)
            {
                required.TryGetValue(kv.Key, out int req);
                int extra = kv.Value - req;
                if (extra > 0)
                {
                    refund.TryGetValue(kv.Key, out int cur);
                    refund[kv.Key] = cur + extra;
                }
            }

            SpawnResources(refund);

            delivered.Clear();
            config = pending.Clone();
            assemblyConfirmed = false;
            WorkDone = 0f;
        }

        /// <summary>
        /// 上帝模式免費裝配：直接套用 pending，不消耗資源；已搬運的資源原樣退回。
        /// 移除的部件不退還（否則反覆裝上、拆下就能憑空產生資源）。
        /// </summary>
        public void ApplyAssemblyFree()
        {
            EnsureInit();
            SpawnResources(delivered);
            delivered.Clear();
            config = pending.Clone();
            assemblyConfirmed = false;
            WorkDone = 0f;
        }

        /// <summary>pending 已回到與 config 相同卻仍有搬運資源時，把資源退回（取消裝配）。</summary>
        public void RefundDeliveredIfIdle()
        {
            EnsureInit();
            if (!NeedsAssembly && delivered.Count > 0)
            {
                SpawnResources(delivered);
                delivered.Clear();
            }
        }

        private void SpawnResources(Dictionary<ThingDef, int> things)
        {
            if (things == null || things.Count == 0) { return; }
            if (!MissileAssemblyUtility.TryGetSpawnLocation(this, out Map map, out IntVec3 pos)) { return; }
            foreach (KeyValuePair<ThingDef, int> kv in things)
            {
                int remaining = kv.Value;
                while (remaining > 0)
                {
                    int stack = Mathf.Min(remaining, kv.Key.stackLimit);
                    Thing t = ThingMaker.MakeThing(kv.Key);
                    t.stackCount = stack;
                    GenPlace.TryPlaceThing(t, pos, map, ThingPlaceMode.Near);
                    remaining -= stack;
                }
            }
        }

        internal static bool ConfigsEqual(MissileConfig a, MissileConfig b)
        {
            if (a == null || b == null) { return a == b; }
            if (a.body != b.body) { return false; }
            if (a.parts.Count != b.parts.Count) { return false; }
            for (int i = 0; i < a.parts.Count; i++)
            {
                if (!b.parts.Contains(a.parts[i])) { return false; }
            }
            return true;
        }

        public override void PostPostMake()
        {
            base.PostPostMake();
            // 播種只在建立時發生；PostExposeData 讀檔不會重播（欄位預設 true）。
            defaultsSeeded = false;
            EnsureInit();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureInit();
        }

        public override string CompInspectStringExtra()
        {
            EnsureInit();
            if (config.body == null) { return null; }

            MissilePartDef warhead = config.PartFor(MissilePartCategory.Warhead);
            string s = "DMSE.Missile.Inspect".Translate(
                config.body.LabelCap,
                warhead != null ? warhead.LabelCap : (TaggedString)"DMSE.Missile.None".Translate(),
                config.ExplosionRadius.ToString("0.#"),
                config.DamageAmount);

            if (NeedsAssembly)
            {
                Dictionary<ThingDef, int> need = StillNeeded();
                if (need.Count > 0)
                {
                    string list = string.Join(", ", need.Select(kv => kv.Key.label + " x" + kv.Value));
                    s += "\n" + "DMSE.Missile.NeedResources".Translate(list);
                }
                else
                {
                    s += "\n" + "DMSE.Missile.ReadyToAssemble".Translate();
                }
            }
            return s;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref config, "config");
            Scribe_Deep.Look(ref pending, "pending");
            Scribe_Collections.Look(ref delivered, "delivered", LookMode.Def, LookMode.Value);
            Scribe_Values.Look(ref assemblyConfirmed, "assemblyConfirmed", false);
            Scribe_Values.Look(ref defaultsSeeded, "defaultsSeeded", true);
            Scribe_Values.Look(ref workDone, "workDone", 0f);
            Scribe_Deep.Look(ref workDoneFor, "workDoneFor");
            Scribe_Values.Look(ref designName, "designName");
            Scribe_Deep.Look(ref designNameFor, "designNameFor");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (config == null) { config = new MissileConfig(); }
                if (pending == null) { pending = new MissileConfig(); }
                if (delivered == null) { delivered = new Dictionary<ThingDef, int>(); }
            }
        }
    }
}
