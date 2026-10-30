using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_MissileAssemblyBench : CompProperties
    {
        /// <summary>一張訂單最多幾枚。</summary>
        public int maxBatch = 99;

        public CompProperties_MissileAssemblyBench()
        {
            compClass = typeof(CompMissileAssemblyBench);
        }
    }

    /// <summary>出廠後成品的去向。</summary>
    public enum BenchOutputMode
    {
        /// <summary>留在台上的容器；發射平台裝填直接來拉（<see cref="WorkGiver_LoadMissileLauncher.FindSourceRack"/>）。</summary>
        KeepOnBench,
        /// <summary>出廠後交給 <see cref="WorkGiver_UnloadMissileRack"/> 取出，再由原版搬運送進倉儲。</summary>
        SendToStorage
    }

    /// <summary>
    /// 導彈組裝台：從零生產導彈。訂單＝構型（draft）＋數量；一次收一枚份的料、做一枚、出廠一枚。
    /// 搬料與施工共用改裝的 Job（<see cref="IMissileAssemblyTarget"/>）；成品放進同一建築的
    /// <see cref="CompMissileRackStorage"/>（出廠暫存）。
    /// 狀態：空閒（draft == null）→ 配置中（!ordered）→ 收料中／待施工／出廠阻塞（ordered）。
    /// </summary>
    public class CompMissileAssemblyBench : ThingComp, IMissileAssemblyTarget
    {
        // 訂單
        public MissileConfig draft;
        public string designName;
        public bool nameLocked;
        public string ruleString;
        public bool ordered;
        public int remaining;
        /// <summary>本張訂單已出廠的數量（顯示「第 n / N 枚」用）。</summary>
        public int producedThisOrder;
        public BenchOutputMode outputMode = BenchOutputMode.KeepOnBench;

        private Dictionary<ThingDef, int> delivered = new Dictionary<ThingDef, int>();
        private float workDone;
        // SendToStorage 模式下尚未被取出的成品。
        private List<Thing> outbound = new List<Thing>();

        public CompProperties_MissileAssemblyBench Props => (CompProperties_MissileAssemblyBench)props;

        public CompMissileRackStorage Rack => parent.TryGetComp<CompMissileRackStorage>();

        public IReadOnlyDictionary<ThingDef, int> Delivered => delivered;

        // ---------------- 狀態 ----------------

        public bool HasDraft => draft?.body != null;

        /// <summary>收料中／待施工／出廠阻塞：訂單凍結，窗口只能看、改數量或取消。</summary>
        public bool OrderActive => ordered && HasDraft && remaining > 0;

        public bool IsOutbound(Thing t) => t != null && outbound.Contains(t);

        /// <summary>出廠暫存已滿（料齊也不會施工）。</summary>
        public bool OutputBlocked => Rack == null || Rack.Full;

        // ---------------- IMissileAssemblyTarget ----------------

        public Thing Parent => parent;

        public bool WantsWork => OrderActive;

        /// <summary>一枚份的總需求。</summary>
        public Dictionary<ThingDef, int> RequiredPerUnit()
            => HasDraft ? MissileAssemblyCost.RequiredFor(draft) : new Dictionary<ThingDef, int>();

        public Dictionary<ThingDef, int> StillNeeded()
        {
            Dictionary<ThingDef, int> result = new Dictionary<ThingDef, int>();
            if (!OrderActive) { return result; }
            foreach (KeyValuePair<ThingDef, int> kv in RequiredPerUnit())
            {
                delivered.TryGetValue(kv.Key, out int got);
                if (kv.Value - got > 0) { result[kv.Key] = kv.Value - got; }
            }
            return result;
        }

        public bool ResourcesComplete => StillNeeded().Count == 0;

        public void Deposit(ThingDef def, int count)
        {
            delivered.TryGetValue(def, out int cur);
            delivered[def] = cur + count;
        }

        public int WorkAmount => HasDraft ? MissileAssemblyCost.WorkFor(draft) : 1;

        public float WorkDone
        {
            get => workDone;
            set => workDone = Mathf.Max(0f, value);
        }

        /// <summary>小人速度 × 台速（工具櫃、機械櫃、戶外、溫度都在 WorkTableWorkSpeedFactor 裡）。</summary>
        public float WorkSpeedFor(Pawn pawn) => MissileAssemblyUtility.PawnWorkSpeed(pawn) * TableSpeed;

        public float TableSpeed => parent.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor);

        public bool CanFinishNow => Rack != null && !Rack.Full;

        /// <summary>產出一枚：出廠即完成彈（config 與 pending 同為 draft），消耗一枚份的料。</summary>
        public void ApplyAssembly()
        {
            if (!OrderActive) { return; }
            if (ProduceOne(consume: true) && remaining <= 0)
            {
                Messages.Message("DMSE.Bench.OrderDone".Translate(DisplayName), parent, MessageTypeDefOf.PositiveEvent);
            }
        }

        private bool ProduceOne(bool consume)
        {
            ThingDef itemDef = MissileItemUtility.ItemDefFor(draft.body);
            if (itemDef == null)
            {
                Log.ErrorOnce("[DMSE] assembly bench order has no item def: " + draft.body.defName, 0x0D5E0001);
                CancelOrder();
                return false;
            }
            CompMissileRackStorage rack = Rack;
            if (rack == null || rack.Full) { return false; }

            Thing missile = ThingMaker.MakeThing(itemDef);   // PostPostMake 已播種預設零件
            CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
            if (cfg != null)
            {
                cfg.EnsureInit();
                cfg.config.CopyFrom(draft);
                cfg.pending.CopyFrom(draft);
                cfg.assemblyConfirmed = false;
                cfg.SetDesignName(designName);
            }
            if (!rack.HeldContainer.TryAdd(missile))
            {
                missile.Destroy();
                return false;
            }
            if (outputMode == BenchOutputMode.SendToStorage) { outbound.Add(missile); }

            if (consume) { ConsumeOneUnit(); }
            workDone = 0f;
            producedThisOrder++;
            remaining--;
            if (remaining <= 0)
            {
                ordered = false;
                remaining = 0;
                RefundDelivered();   // 多搬的料原樣退回
            }
            return true;
        }

        private void ConsumeOneUnit()
        {
            foreach (KeyValuePair<ThingDef, int> kv in RequiredPerUnit())
            {
                delivered.TryGetValue(kv.Key, out int got);
                int left = got - kv.Value;
                if (left > 0) { delivered[kv.Key] = left; } else { delivered.Remove(kv.Key); }
            }
        }

        // ---------------- 訂單操作（窗口呼叫） ----------------

        /// <summary>寫回配置中的草稿（不下單）。</summary>
        public void SetDraft(MissileConfig cfg, string name, bool locked, string rule)
        {
            if (OrderActive) { return; }
            draft = cfg?.Clone();
            designName = name;
            nameLocked = locked;
            ruleString = rule;
        }

        public void PlaceOrder(MissileConfig cfg, string name, bool locked, string rule, int count, BenchOutputMode mode)
        {
            if (OrderActive || cfg?.body == null) { return; }
            SetDraft(cfg, name, locked, rule);
            outputMode = mode;
            remaining = Mathf.Clamp(count, 1, Props.maxBatch);
            producedThisOrder = 0;
            ordered = true;
            workDone = 0f;
            if (MissileAssemblyUtility.GodModeInstant)
            {
                // 上帝模式即時裝配：不耗料、不派工，能放幾枚就做幾枚，餘數保留為訂單。
                while (OrderActive && ProduceOne(consume: false)) { }
            }
        }

        /// <summary>訂單進行中只能改數量（至少 1）。</summary>
        public void SetRemaining(int count)
        {
            if (!OrderActive) { return; }
            remaining = Mathf.Clamp(count, 1, Props.maxBatch);
        }

        public void SetOutputMode(BenchOutputMode mode) => outputMode = mode;

        /// <summary>取消訂單：已到料 100% 退回台旁，進度歸零，回到配置中（保留草稿）。</summary>
        public void CancelOrder()
        {
            ordered = false;
            remaining = 0;
            workDone = 0f;
            RefundDelivered();
        }

        public string DisplayName
            => !designName.NullOrEmpty() ? designName : draft?.body != null ? draft.body.LabelCap.ToString() : parent.LabelShortCap;

        private void RefundDelivered(float fraction = 1f)
        {
            if (delivered.Count == 0) { return; }
            if (parent.Spawned)
            {
                foreach (KeyValuePair<ThingDef, int> kv in delivered)
                {
                    int remainingCount = Mathf.FloorToInt(kv.Value * fraction);
                    while (remainingCount > 0)
                    {
                        int stack = Mathf.Min(remainingCount, kv.Key.stackLimit);
                        Thing t = ThingMaker.MakeThing(kv.Key);
                        t.stackCount = stack;
                        GenPlace.TryPlaceThing(t, parent.Position, parent.Map, ThingPlaceMode.Near);
                        remainingCount -= stack;
                    }
                }
            }
            delivered.Clear();
        }

        // ---------------- 生命週期 ----------------

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // 讀檔後訂單已作廢（例如彈體 Def 被移除）卻仍有已到料：退回台旁。
            if (!OrderActive && delivered.Count > 0) { RefundDelivered(); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            if (mode != DestroyMode.WillReplace && delivered.Count > 0)
            {
                // 已到料就地掉落；被摧毀時依原版慣例只剩一半。
                float frac = mode == DestroyMode.KillFinalize ? 0.5f : 1f;
                IntVec3 pos = parent.Position;
                foreach (KeyValuePair<ThingDef, int> kv in delivered)
                {
                    int n = Mathf.FloorToInt(kv.Value * frac);
                    while (n > 0)
                    {
                        int stack = Mathf.Min(n, kv.Key.stackLimit);
                        Thing t = ThingMaker.MakeThing(kv.Key);
                        t.stackCount = stack;
                        GenPlace.TryPlaceThing(t, pos, map, ThingPlaceMode.Near);
                        n -= stack;
                    }
                }
                delivered.Clear();
                ordered = false;
                remaining = 0;
            }
            base.PostDeSpawn(map, mode);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer) { yield break; }
            yield return new Command_Action
            {
                defaultLabel = "DMSE.Bench.Open".Translate(),
                defaultDesc = "DMSE.Bench.OpenDesc".Translate(),
                icon = parent.def.uiIcon,
                action = () => Find.WindowStack.Add(new Dialog_MissileAssemblyBench(this))
            };
            if (OrderActive)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Bench.CancelOrder".Translate(),
                    defaultDesc = "DMSE.Bench.CancelOrderDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                    action = CancelOrder
                };
            }
        }

        public override string CompInspectStringExtra()
        {
            StringBuilder sb = new StringBuilder();
            if (OrderActive)
            {
                sb.Append("DMSE.Bench.InspectOrder".Translate(DisplayName, remaining));
                Dictionary<ThingDef, int> need = StillNeeded();
                if (need.Count > 0)
                {
                    sb.AppendLine();
                    sb.Append("DMSE.Missile.NeedResources".Translate(string.Join(", ", need.Select(kv => kv.Key.label + " x" + kv.Value))));
                }
                else if (!CanFinishNow)
                {
                    sb.AppendLine();
                    sb.Append("DMSE.Bench.OutputFull".Translate());
                }
                if (workDone > 0f)
                {
                    sb.AppendLine();
                    sb.Append("DMSE.Bench.InspectProgress".Translate((workDone / Mathf.Max(1f, WorkAmount)).ToStringPercent()));
                }
            }
            float speed = TableSpeed;
            if (!Mathf.Approximately(speed, 1f))
            {
                if (sb.Length > 0) { sb.AppendLine(); }
                sb.Append(StatDefOf.WorkTableWorkSpeedFactor.LabelCap + ": " + speed.ToStringPercent());
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Deep.Look(ref draft, "draft");
            Scribe_Values.Look(ref designName, "designName");
            Scribe_Values.Look(ref nameLocked, "nameLocked", false);
            Scribe_Values.Look(ref ruleString, "ruleString");
            Scribe_Values.Look(ref ordered, "ordered", false);
            Scribe_Values.Look(ref remaining, "remaining", 0);
            Scribe_Values.Look(ref producedThisOrder, "producedThisOrder", 0);
            Scribe_Values.Look(ref outputMode, "outputMode", BenchOutputMode.KeepOnBench);
            Scribe_Collections.Look(ref delivered, "delivered", LookMode.Def, LookMode.Value);
            Scribe_Values.Look(ref workDone, "workDone", 0f);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                IReadOnlyList<Thing> held = Rack?.HeldThings;
                outbound.RemoveAll(t => t == null || held == null || !held.Contains(t));
            }
            Scribe_Collections.Look(ref outbound, "outbound", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (delivered == null) { delivered = new Dictionary<ThingDef, int>(); }
                if (outbound == null) { outbound = new List<Thing>(); }
                outbound.RemoveAll(t => t == null);
                if (draft != null && draft.body == null)
                {
                    // 彈體 Def 已被移除：訂單作廢並退料。
                    draft = null;
                    ordered = false;
                    remaining = 0;
                }
            }
        }
    }
}
