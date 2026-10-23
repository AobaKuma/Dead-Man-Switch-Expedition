using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 世界層防空據點的共同介面：實體 <see cref="WorldObjectComp_AirDefense"/>（NPC 導彈陣地）
    /// 或 <see cref="WorldComponent_SettlementDefense"/> 為一般據點合成的防禦。
    /// </summary>
    public interface IBVRDefense
    {
        bool Available { get; }
        bool RadarOnline { get; }
        /// <summary>通道不足時是否先打雷達截面最大的目標。</summary>
        bool ThreatAssessment { get; }
        int Stock { get; }
        int Channels { get; }
        float DetectionRange(BVRTargetProps target);
        CompProperties_FireControlRadar FireControl { get; }
        MissileConfig Config { get; }
        bool Ready(int now);
        bool TryConsume(int now);
        void Degrade(int now);
    }

    /// <summary>把實體的抽象防空元件包成 <see cref="IBVRDefense"/>。</summary>
    public sealed class WorldAirDefenseAdapter : IBVRDefense
    {
        private readonly WorldObjectComp_AirDefense comp;
        public WorldAirDefenseAdapter(WorldObjectComp_AirDefense comp) { this.comp = comp; }
        public bool Available => comp.Available;
        public bool RadarOnline => comp.RadarOnline;
        public bool ThreatAssessment => comp.Props.threatAssessment;
        public int Stock => comp.Stock;
        public int Channels => comp.Props.channels;
        public float DetectionRange(BVRTargetProps target) => RadarOnline
            ? MissileWorldMotion.DetectionRange(comp.Props.worldDetectionRange, comp.Props.antiStealthLevel, target)
            : 0f;
        public CompProperties_FireControlRadar FireControl => comp.FireControl;
        public MissileConfig Config => comp.Config;
        public bool Ready(int now) => comp.Ready(now);
        public bool TryConsume(int now) => comp.TryConsume(now);
        public void Degrade(int now) => comp.Degrade(now);
    }

    /// <summary>合成防禦的持久狀態（庫存、補充、冷卻、雷達壓制）。</summary>
    public class SettlementDefenseState : IExposable
    {
        public int stock = -1;
        public int lastRestockTick = -1;
        public int cooldownUntil;
        public int radarDownUntil;

        public void ExposeData()
        {
            Scribe_Values.Look(ref stock, "stock", -1);
            Scribe_Values.Look(ref lastRestockTick, "lastRestockTick", -1);
            Scribe_Values.Look(ref cooldownUntil, "cooldownUntil", 0);
            Scribe_Values.Look(ref radarDownUntil, "radarDownUntil", 0);
        }
    }

    /// <summary>
    /// 一般據點自動防空（P4）：沒有專屬防空元件的敵方據點，依陣營科技等級取得一套小型抽象防空。
    /// 只作用於「尚未生成地圖」的據點；地圖一旦生成就交給地圖上的實體裝置。
    /// </summary>
    public class WorldComponent_SettlementDefense : WorldComponent
    {
        private Dictionary<int, SettlementDefenseState> states = new Dictionary<int, SettlementDefenseState>();

        public static WorldComponent_SettlementDefense Current => Find.World?.GetComponent<WorldComponent_SettlementDefense>();

        public WorldComponent_SettlementDefense(World world) : base(world) { }

        /// <summary>是否套用合成防空：敵方、無地圖、沒有專屬防空元件、科技達 Industrial。</summary>
        public static bool Eligible(MapParent parent)
        {
            if (parent == null || parent.Destroyed || parent.HasMap) { return false; }
            Faction faction = parent.Faction;
            if (faction == null || faction.IsPlayer) { return false; }
            if (parent.GetComponent<WorldObjectComp_AirDefense>() != null) { return false; }
            return faction.def != null && faction.def.techLevel >= TechLevel.Industrial;
        }

        public IBVRDefense DefenseFor(MapParent parent)
            => Eligible(parent) ? new SettlementDefense(this, parent) : null;

        public bool HasActiveDefense(MapParent parent)
            => Eligible(parent) && StateOf(parent).stock > 0;

        /// <summary>開發工具：清空合成防空庫存。</summary>
        public void DevDrain(MapParent parent)
        {
            SettlementDefenseState state = StateOf(parent);
            state.stock = 0;
            state.lastRestockTick = Find.TickManager.TicksGame;
        }

        internal SettlementDefenseState StateOf(MapParent parent)
        {
            if (!states.TryGetValue(parent.ID, out SettlementDefenseState state))
            {
                state = new SettlementDefenseState();
                states[parent.ID] = state;
            }
            return state;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref states, "settlementDefenses", LookMode.Value, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                states ??= new Dictionary<int, SettlementDefenseState>();
            }
        }

        private sealed class SettlementDefense : IBVRDefense
        {
            private readonly WorldComponent_SettlementDefense owner;
            private readonly MapParent parent;

            public SettlementDefense(WorldComponent_SettlementDefense owner, MapParent parent)
            {
                this.owner = owner;
                this.parent = parent;
            }

            private TechLevel Tech => parent.Faction?.def?.techLevel ?? TechLevel.Industrial;
            private SettlementDefenseState State => owner.StateOf(parent);

            public bool Available => Eligible(parent);
            public bool RadarOnline => Find.TickManager.TicksGame >= State.radarDownUntil;
            public bool ThreatAssessment => false;
            public int Stock { get { Refresh(); return Mathf.FloorToInt(State.stock); } }
            public int Channels => Tech >= TechLevel.Spacer ? 2 : 1;

            private void Refresh()
            {
                SettlementDefenseState state = State;
                int now = Find.TickManager.TicksGame;
                int max = MaxStock;
                if (state.stock < 0) { state.stock = max; }
                if (state.lastRestockTick >= 0 && Available)
                {
                    state.stock = Mathf.Min(max,
                        state.stock + Mathf.FloorToInt(Mathf.Max(0, now - state.lastRestockTick) * RestockPerDay / GenDate.TicksPerDay));
                }
                state.lastRestockTick = now;
            }

            private int MaxStock => Tech >= TechLevel.Ultra ? 6 : Tech >= TechLevel.Spacer ? 4 : 2;
            private float RestockPerDay => Tech >= TechLevel.Spacer ? 1.5f : 0.5f;

            public float DetectionRange(BVRTargetProps target)
            {
                if (!Available || !RadarOnline) { return 0f; }
                float range = Tech >= TechLevel.Spacer ? SettlementDefenseTuning.SpacerRange : SettlementDefenseTuning.IndustrialRange;
                return MissileWorldMotion.DetectionRange(range, Tech >= TechLevel.Spacer ? 2 : 1, target);
            }

            public CompProperties_FireControlRadar FireControl
                => Tech >= TechLevel.Spacer ? SettlementDefenseTuning.SpacerRadar : SettlementDefenseTuning.IndustrialRadar;

            public MissileConfig Config => SettlementDefenseTuning.Interceptor;

            public bool Ready(int now) => Available && RadarOnline && Stock > 0 && now >= State.cooldownUntil;

            public bool TryConsume(int now)
            {
                if (!Ready(now)) { return false; }
                State.stock -= 1;
                State.cooldownUntil = now + (Tech >= TechLevel.Spacer ? 600 : 900);
                return true;
            }

            public void Degrade(int now)
            {
                State.stock = Mathf.Max(0, State.stock - 2);
                State.radarDownUntil = now + GenDate.TicksPerDay;
                State.cooldownUntil = now + 600;
            }
        }
    }

    /// <summary>合成防空的數值與攔截彈；延遲解析 Def，避免世界生成前的 DefDatabase 存取。</summary>
    public static class SettlementDefenseTuning
    {
        public const float IndustrialRange = 6f;
        public const float SpacerRange = 8f;

        private static CompProperties_FireControlRadar industrialRadar, spacerRadar;
        private static MissileConfig interceptor;

        public static CompProperties_FireControlRadar IndustrialRadar
            => industrialRadar ??= RadarOf("DMSE_EarlyFireControlRadar");

        public static CompProperties_FireControlRadar SpacerRadar
            => spacerRadar ??= RadarOf("DMSE_PhaseArrayRadar");

        public static MissileConfig Interceptor
        {
            get
            {
                if (interceptor == null)
                {
                    MissileBodyDef body = DefDatabase<MissileBodyDef>.GetNamedSilentFail("DMSE_MissileBody_Light");
                    MissilePartDef warhead = DefDatabase<MissilePartDef>.GetNamedSilentFail("DMSE_Light_Warhead_ContinuousRod");
                    interceptor = new MissileConfig(body);
                    if (warhead != null) { interceptor.SetPart(MissilePartCategory.Warhead, warhead); }
                }
                return interceptor;
            }
        }

        private static CompProperties_FireControlRadar RadarOf(string thingDefName)
            => DefDatabase<ThingDef>.GetNamedSilentFail(thingDefName)?.comps?.OfType<CompProperties_FireControlRadar>().FirstOrDefault();
    }
}
