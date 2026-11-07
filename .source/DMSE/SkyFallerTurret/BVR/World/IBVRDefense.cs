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

    /// <summary>抽象據點的防空庫存作為武器：沒有實體，陣營由節點決定。</summary>
    public sealed class BVRDefenseEffector : Fortified.IAirEffector
    {
        private readonly IBVRDefense defense;
        public BVRDefenseEffector(IBVRDefense defense) { this.defense = defense; }
        public Thing Source => null;
        public bool UsesFireControl => true;
        public bool Armed => defense.Available && defense.Stock > 0;
        public int ReadyTick => 0;
        public bool CanFire(int now) => defense.Ready(now);
        public bool CanEngage(Fortified.AirEngagement engagement) => true;
        public Fortified.AirEffectorShot PrepareShot(Fortified.AirEngagement engagement) => AirDefenseHooks.ShotFrom(defense.Config);
        public bool TryFire(Fortified.AirEngagement engagement, int now, Vector3 meetingPoint) => defense.TryConsume(now);
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

        /// <summary>是否套用合成防空：敵方、無地圖、沒有專屬防空元件，且陣營準則有合成防空。</summary>
        public static bool Eligible(MapParent parent)
        {
            if (parent == null || parent.Destroyed || parent.HasMap) { return false; }
            Faction faction = parent.Faction;
            if (faction == null || faction.IsPlayer) { return false; }
            if (parent.GetComponent<WorldObjectComp_AirDefense>() != null) { return false; }
            return PropsFor(faction) != null;
        }

        /// <summary>陣營準則的合成防空設定；沒有時回傳 null。</summary>
        public static SettlementAirDefenseProps PropsFor(Faction faction) => MissileDoctrine.For(faction)?.settlementDefense;

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

            private SettlementAirDefenseProps Props => PropsFor(parent.Faction);
            private SettlementDefenseState State => owner.StateOf(parent);

            public bool Available => Eligible(parent);
            public bool RadarOnline => Find.TickManager.TicksGame >= State.radarDownUntil;
            public bool ThreatAssessment => Props?.threatAssessment ?? false;
            public int Stock { get { Refresh(); return Mathf.FloorToInt(State.stock); } }
            public int Channels => Props?.channels ?? 1;

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

            private int MaxStock => Props?.maxStock ?? 0;
            private float RestockPerDay => Props?.restockPerDay ?? 0f;

            public float DetectionRange(BVRTargetProps target)
            {
                SettlementAirDefenseProps props = Props;
                if (props == null || !Available || !RadarOnline) { return 0f; }
                return MissileWorldMotion.DetectionRange(props.detectionRange, props.antiStealthLevel, target);
            }

            public CompProperties_FireControlRadar FireControl => Props?.FireControl;

            public MissileConfig Config => Props?.Interceptor;

            public bool Ready(int now) => Available && RadarOnline && Stock > 0 && now >= State.cooldownUntil;

            public bool TryConsume(int now)
            {
                if (!Ready(now)) { return false; }
                State.stock -= 1;
                State.cooldownUntil = now + (Props?.cooldownTicks ?? 900);
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
}
