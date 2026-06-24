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
    }

    /// <summary>
    /// 保存導彈的裝配設定。
    /// config = 已套用（影響發射效果）；pending = 玩家於 ITab 設定的目標。
    /// 兩者不同則需要小人搬運資源裝配；裝配完成後 config = pending。
    /// </summary>
    public class CompMissileConfig : ThingComp
    {
        public MissileConfig config = new MissileConfig();   // 已套用
        public MissileConfig pending = new MissileConfig();  // 待裝配目標

        // 已搬運到此導彈、尚未消耗的資源。
        public Dictionary<ThingDef, int> delivered = new Dictionary<ThingDef, int>();

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
            if (parent == null || !parent.Spawned || parent.Map == null) { return; }
            foreach (KeyValuePair<ThingDef, int> kv in things)
            {
                int remaining = kv.Value;
                while (remaining > 0)
                {
                    int stack = Mathf.Min(remaining, kv.Key.stackLimit);
                    Thing t = ThingMaker.MakeThing(kv.Key);
                    t.stackCount = stack;
                    GenPlace.TryPlaceThing(t, parent.Position, parent.Map, ThingPlaceMode.Near);
                    remaining -= stack;
                }
            }
        }

        private static bool ConfigsEqual(MissileConfig a, MissileConfig b)
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
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (config == null) { config = new MissileConfig(); }
                if (pending == null) { pending = new MissileConfig(); }
                if (delivered == null) { delivered = new Dictionary<ThingDef, int>(); }
            }
        }
    }
}
