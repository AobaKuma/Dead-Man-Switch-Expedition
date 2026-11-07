using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// 本檔與 MissileFrameworkTypes.cs 同樣預定併入 Fortified：不得引用 DMSE 型別。
namespace Fortified
{
    /// <summary>世界層偵測看到的目標特徵；飛彈行為可在飛行中修改。</summary>
    public struct AirTrackSignature
    {
        public float distanceLevel;
        public int stealthLevel;
        public float speed;
        public float altitude;

        public bool SameAs(in AirTrackSignature o)
            => distanceLevel == o.distanceLevel && stealthLevel == o.stealthLevel && speed == o.speed && altitude == o.altitude;
    }

    /// <summary>
    /// 防空系統可以追蹤的飛行物，不限飛彈。世界物件實作此介面後會自動成為航跡；
    /// 自己無法修改的世界物件（原版或其他 mod）改由 <see cref="AirTrackProvider"/> 提供包裝。
    /// </summary>
    public interface IAirTrack
    {
        /// <summary>世界地圖上的物件，不可為 null；偵測與鎖定狀態依它的 ID 保存。</summary>
        WorldObject Self { get; }

        /// <summary>這條航跡所屬的陣營；防空依它判斷敵我。</summary>
        Faction Attacker { get; }

        PlanetTile Origin { get; }

        /// <summary>目的地；沒有目的地（單純飛越）時為 <see cref="PlanetTile.Invalid"/>。</summary>
        PlanetTile Destination { get; }

        /// <summary>瞄準的據點；沒有時由目的地 tile 推算。</summary>
        MapParent TargetParent => null;

        AirTrackSignature Signature { get; }

        /// <summary>目前的角速度（弧度／tick）。</summary>
        float AngularSpeed { get; }

        /// <summary>速度是否隨時間變化；是的話不能用等速的封閉解求會合。</summary>
        bool VariableSpeed => false;

        /// <summary>距離抵達或飛離的 ticks。防空只在這段時間內求解攔截，超過火控射程不會接戰。</summary>
        int TicksToArrival { get; }

        /// <summary>預測 <paramref name="ticksAhead"/> 後的世界座標；攔截解會反覆呼叫，不可有副作用。</summary>
        Vector3 PositionAt(int ticksAhead);

        /// <summary>預測 <paramref name="ticksAhead"/> 後的飛行高度（公尺）；攔截解與航跡顯示會呼叫，不可有副作用。</summary>
        float AltitudeAt(int ticksAhead) => Signature.altitude;

        /// <summary>機動計數：增加時，飛向舊會合點的在飛攔截彈判定目標脫離。</summary>
        int RouteRevision => 0;

        /// <summary>裝配；非飛彈為 null。</summary>
        IMissileLoadout Loadout => null;

        /// <summary>為 false 時略過 <see cref="ModifyDefense"/>，省下建立查詢的成本。</summary>
        bool ModifiesDefense => false;

        /// <summary>世界層防空對這條航跡的修正（例如自身的電子戰裝置）。</summary>
        void ModifyDefense(AirDefenseQuery query, ref float value) { }

        /// <summary>被攔截命中。怎麼處理由航跡決定：銷毀自己，或只承受傷害繼續飛。</summary>
        void Notify_Intercepted(Faction by);
    }

    /// <summary>高度航跡的顯示換算；純顯示，不影響任何判定。</summary>
    public static class AirTrackDisplay
    {
        private const float MinMeters = 30f, MaxMeters = 150000f, MinLift = 0.15f, MaxLift = 4.5f;

        /// <summary>最後這一段航程畫成俯衝、最前面這一段畫成爬升（模擬只有一個巡航高度，這裡只是讓線條落回地面）。</summary>
        private const float DiveFraction = 0.15f, ClimbFraction = 0.1f;

        /// <summary>高度對應的抬升量（世界單位）。對數縮放：出貨值從 50 公尺到 100 公里，線性的話低空都貼在地上。</summary>
        public static float Lift(float meters)
        {
            float t = Mathf.InverseLerp(Mathf.Log10(MinMeters), Mathf.Log10(MaxMeters), Mathf.Log10(Mathf.Max(1f, meters)));
            return Mathf.Lerp(MinLift, MaxLift, t);
        }

        /// <summary>爬升與俯衝對抬升量的係數（0–1）。<paramref name="progressFromStart"/> 不知道時傳 1。</summary>
        public static float Ramp(float progressFromStart, float fractionLeft)
            => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progressFromStart / ClimbFraction)) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(fractionLeft / DiveFraction));

        /// <summary>線寬係數：世界相機由上往下看，抬升不易察覺，飛得高的線畫得粗一點（離相機較近）。</summary>
        public static float WidthFactor(float meters) => Mathf.Lerp(0.7f, 1.6f, Mathf.InverseLerp(MinLift, MaxLift, Lift(meters)));

        /// <summary>把世界座標沿地表法線抬高。</summary>
        public static Vector3 Raised(Vector3 point, float lift) => point + point.normalized * lift;

        /// <summary>航跡目前位置的抬升量。世界物件的 DrawPos 用它，圖示才會畫在航跡線上。</summary>
        public static float CurrentLift(IAirTrack track)
        {
            float progress = track is IMissileTrack missile ? missile.RouteProgress : 1f;
            float left = track is IMissileTrack ? 1f - progress : 1f;
            return Lift(track.AltitudeAt(0)) * Ramp(progress, left);
        }

        /// <summary>高度分段：0 低空、1 中空、2 高空、3 彈道。</summary>
        public static int Band(float meters) => meters < 500f ? 0 : meters < 5000f ? 1 : meters < 20000f ? 2 : 3;
    }

    /// <summary>防空流程的哪一層。</summary>
    public enum AirCombatLayer { World, Map }

    /// <summary>可被修正的防空數值。</summary>
    public enum AirDefenseStat
    {
        /// <summary>偵測能力。世界層是探測半徑（地表格），地圖層是預警窗口（ticks）。</summary>
        Detection,

        /// <summary>火控鎖定時間（ticks）。</summary>
        LockTicks,

        /// <summary>單次攔截的命中率（0–1）。</summary>
        HitChance,
    }

    /// <summary>一次防空數值計算的情境；只有該層與該數值用得到的欄位會有值。</summary>
    public sealed class AirDefenseQuery
    {
        public AirDefenseStat Stat { get; set; }
        public AirCombatLayer Layer { get; set; }
        public Faction Defender { get; set; }
        public Faction Attacker { get; set; }
        public AirTrackSignature Signature { get; set; }

        /// <summary>防禦方據點；地圖層是地圖的 Parent。</summary>
        public MapParent Site { get; set; }

        /// <summary>防禦方地圖；沒有地圖的抽象據點為 null。</summary>
        public Map Map { get; set; }

        /// <summary>世界層航跡；查詢涵蓋範圍（不針對特定目標）或地圖層時為 null。</summary>
        public IAirTrack Track { get; set; }

        /// <summary>世界層目標物件；封鎖攔截的載具沒有 <see cref="Track"/>，只有這個。</summary>
        public WorldObject TargetObject { get; set; }

        /// <summary>地圖層被收起的來襲物（導彈或空投艙）。</summary>
        public Thing TargetThing { get; set; }

        public IMissileLoadout TargetLoadout { get; set; }
        public IMissileLoadout InterceptorLoadout { get; set; }

        /// <summary>提供這個數值的裝置（雷達、CIWS、射手）；抽象據點為 null。</summary>
        public Thing Sensor { get; set; }

        /// <summary>目標距離抵達的 ticks；未知時為 0。</summary>
        public int EtaTicks { get; set; }

        /// <summary>地圖層的末端攔截（CIWS）。</summary>
        public bool Terminal { get; set; }
    }

    /// <summary>
    /// 防空數值修正：地圖層與世界層共用。可在 <see cref="AirDefenseExtensionDef"/> 宣告，
    /// 或用 <see cref="AirDefensePipeline.Add"/> 註冊。不可有副作用，介面顯示與排序也會呼叫。
    /// </summary>
    public abstract class AirDefenseModifier
    {
        /// <summary>數字小的先套用；相同時依註冊順序。</summary>
        public int order;

        public abstract void Modify(AirDefenseQuery query, ref float value);

        public virtual IEnumerable<string> ConfigErrors(Def parent) { yield break; }
    }

    /// <summary>
    /// 不必寫 C# 的修正：value = value × factor + offset。
    /// <c>&lt;li Class="Fortified.AirDefenseModifier_Factor"&gt;</c>
    /// </summary>
    public class AirDefenseModifier_Factor : AirDefenseModifier
    {
        public AirDefenseStat stat;
        public float factor = 1f;
        public float offset;
        public bool worldOnly;
        public bool mapOnly;

        public override void Modify(AirDefenseQuery query, ref float value)
        {
            if (query.Stat != stat || (worldOnly && query.Layer != AirCombatLayer.World) || (mapOnly && query.Layer != AirCombatLayer.Map)) { return; }
            value = value * factor + offset;
        }

        public override IEnumerable<string> ConfigErrors(Def parent)
        {
            if (worldOnly && mapOnly) { yield return $"{parent.defName}: a modifier cannot be both worldOnly and mapOnly"; }
        }
    }

    /// <summary>
    /// 修正鏈：全域修正 → 目標裝配 → 攔截彈裝配 → 航跡自身。
    /// 單一修正丟例外時只跳過它，並記錄一次錯誤，不會中斷戰鬥流程。
    /// </summary>
    public static class AirDefensePipeline
    {
        private static readonly List<AirDefenseModifier> modifiers = new List<AirDefenseModifier>();

        public static void Add(AirDefenseModifier modifier)
        {
            if (modifier == null || modifiers.Contains(modifier)) { return; }
            int index = modifiers.Count;
            while (index > 0 && modifiers[index - 1].order > modifier.order) { index--; }
            modifiers.Insert(index, modifier);
        }

        public static void Remove(AirDefenseModifier modifier) => modifiers.Remove(modifier);

        /// <summary>有沒有任何修正可能作用；沒有時呼叫端可以跳過建立查詢。</summary>
        public static bool MayModify(IAirTrack track = null, IMissileLoadout target = null, IMissileLoadout interceptor = null)
        {
            AirDefenseExtensions.EnsureLoaded();
            return modifiers.Count > 0 || track?.ModifiesDefense == true || HasBehaviors(target) || HasBehaviors(interceptor);
        }

        private static bool HasBehaviors(IMissileLoadout loadout)
        {
            if (loadout == null) { return false; }
            foreach (MissileBehaviorProps props in loadout.BehaviorProps)
            {
                if (props != null) { return true; }
            }
            return false;
        }

        public static float Evaluate(AirDefenseQuery query, float value)
        {
            AirDefenseExtensions.EnsureLoaded();
            for (int i = 0; i < modifiers.Count; i++)
            {
                AirDefenseModifier modifier = modifiers[i];
                Guarded(modifier, ref value, query, static (AirDefenseModifier m, AirDefenseQuery q, ref float v) => m.Modify(q, ref v));
            }
            if (query.TargetLoadout != null)
            {
                foreach (MissileBehaviorProps props in query.TargetLoadout.BehaviorProps)
                {
                    if (props != null) { Guarded(props, ref value, query, static (MissileBehaviorProps p, AirDefenseQuery q, ref float v) => p.ModifyDefenseAsTarget(q, ref v)); }
                }
            }
            if (query.InterceptorLoadout != null)
            {
                foreach (MissileBehaviorProps props in query.InterceptorLoadout.BehaviorProps)
                {
                    if (props != null) { Guarded(props, ref value, query, static (MissileBehaviorProps p, AirDefenseQuery q, ref float v) => p.ModifyDefenseAsInterceptor(q, ref v)); }
                }
            }
            if (query.Track?.ModifiesDefense == true)
            {
                Guarded(query.Track, ref value, query, static (IAirTrack t, AirDefenseQuery q, ref float v) => t.ModifyDefense(q, ref v));
            }
            return Limit(query.Stat, value);
        }

        private delegate void Step<T>(T source, AirDefenseQuery query, ref float value);

        private static void Guarded<T>(T source, ref float value, AirDefenseQuery query, Step<T> step)
        {
            float result = value;
            try
            {
                step(source, query, ref result);
                if (!float.IsNaN(result)) { value = result; }
            }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Fortified] air defense modifier {source.GetType()} threw: {e}", source.GetType().GetHashCode() ^ 0x41444d);
            }
        }

        private static float Limit(AirDefenseStat stat, float value)
        {
            switch (stat)
            {
                case AirDefenseStat.HitChance: return Mathf.Clamp01(value);
                case AirDefenseStat.LockTicks: return Mathf.Max(1f, value);
                default: return Mathf.Max(0f, value);
            }
        }
    }

    /// <summary>一次世界層接戰的情境。<see cref="Track"/> 為 null 表示目標是沒有航跡的載具（封鎖攔截）。</summary>
    public sealed class AirEngagement
    {
        public IAirTrack Track { get; set; }
        public WorldObject TargetObject { get; set; }
        public Faction Defender { get; set; }
        public MapParent Site { get; set; }
        public Map Map { get; set; }

        /// <summary>防禦方據點的世界座標。</summary>
        public Vector3 Origin { get; set; }

        /// <summary>目標與據點的地表距離（格）。</summary>
        public float DistanceTiles { get; set; }

        /// <summary>目標距離抵達的 ticks。</summary>
        public int EtaTicks { get; set; }

        /// <summary>目標目前的飛行高度（公尺）。</summary>
        public float Altitude { get; set; }
    }

    /// <summary>效果器這一發的參數。</summary>
    public sealed class AirEffectorShot
    {
        /// <summary>攔截物的角速度（弧度／tick）；0 或負值 = 瞬間命中（雷射），不產生世界物件。</summary>
        public float AngularSpeed { get; set; }

        /// <summary>
        /// 不用火控（<see cref="IAirEffector.UsesFireControl"/> 為 false）時的基礎命中率。
        /// 用火控時基礎命中率來自火控雷達，這個值不使用。
        /// </summary>
        public float BaseHitChance { get; set; } = 1f;

        public float HitFactor { get; set; } = 1f;
        public float HitBonus { get; set; }

        /// <summary>命中判定後，對附近其他航跡的額外擲骰次數（空爆）。</summary>
        public int ExtraRolls { get; set; }

        /// <summary>這一發的裝配；非飛彈為 null。修正鏈會呼叫它的 <see cref="MissileBehaviorProps.ModifyDefenseAsInterceptor"/>。</summary>
        public IMissileLoadout Loadout { get; set; }

        /// <summary>
        /// 攔截物的世界物件；null = 預設攔截彈。worldObjectClass 必須是防空網路認得的攔截物類別，
        /// 這個欄位用來換外觀與名稱，不是換飛行或結算方式。
        /// </summary>
        public WorldObjectDef ShotDef { get; set; }
    }

    /// <summary>
    /// 能接戰世界層航跡的武器：攔截彈發射架、抽象據點的防空庫存，或其他 mod 的雷射與電磁砲。
    /// 防空節點只透過這個介面使用武器。
    /// <para>
    /// <see cref="UsesFireControl"/> 為 true：走完整流程（火控鎖定、佔火力通道、受火控射程與高度限制），由節點挑一具開火。
    /// 為 false：點防禦式，不鎖定、不佔通道，只要節點陣營看得到且政策允許，每次掃描都會詢問能否開火。
    /// </para>
    /// 註冊見 <see cref="MapComponent_AirEffectors"/>。
    /// </summary>
    public interface IAirEffector
    {
        /// <summary>武器所在的 Thing；陣營由它決定。抽象據點的庫存為 null。</summary>
        Thing Source { get; }

        bool UsesFireControl { get; }

        /// <summary>現在或冷卻後能開火。為 false 時防空不會開始鎖定，已有的鎖定也會放掉。</summary>
        bool Armed { get; }

        /// <summary>冷卻結束的 tick；節點優先選最閒置的武器。</summary>
        int ReadyTick { get; }

        /// <summary>這一刻能開火（彈藥、冷卻、電力）。</summary>
        bool CanFire(int now);

        /// <summary>能否打這個目標（射程、高度等）。不可有副作用。</summary>
        bool CanEngage(AirEngagement engagement);

        /// <summary>這一發的參數。不可有副作用，開火前可能查詢。</summary>
        AirEffectorShot PrepareShot(AirEngagement engagement);

        /// <summary>
        /// 開火並扣除彈藥與冷卻；回傳 false 表示這次沒有發射。
        /// <paramref name="meetingPoint"/> 是預計會合的世界座標，瞬間命中時是目標目前的位置。
        /// </summary>
        bool TryFire(AirEngagement engagement, int now, Vector3 meetingPoint);
    }

    /// <summary>
    /// 地圖上的 <see cref="IAirEffector"/> 註冊表：在 Comp 生成時註冊、移除時取消。不存檔，讀檔時 Comp 會重新註冊。
    /// 註冊後，只要同陣營在這張地圖有防空節點（搜索或火控雷達）就會被使用。
    /// </summary>
    public class MapComponent_AirEffectors : MapComponent
    {
        private readonly List<IAirEffector> effectors = new List<IAirEffector>();

        public MapComponent_AirEffectors(Map map) : base(map) { }

        public IReadOnlyList<IAirEffector> Effectors => effectors;

        public void Register(IAirEffector effector)
        {
            if (effector != null && !effectors.Contains(effector)) { effectors.Add(effector); }
        }

        public void Deregister(IAirEffector effector) => effectors.Remove(effector);
    }

    /// <summary>導彈陣地選目標時的情境。</summary>
    public sealed class StrikeTargetQuery
    {
        public MapParent Source { get; set; }
        public Faction Attacker { get; set; }
        public IMissileLoadout Loadout { get; set; }

        /// <summary>射程與跨圖層檢查；收集後呼叫端還會再過濾一次，Worker 可以先用它省下工作。</summary>
        public System.Predicate<MapParent> InRange { get; set; }
    }

    /// <summary>
    /// 陣營的導彈陣地怎麼選目標。由陣營準則指定，XML 用 <c>Class=</c> 換掉。
    /// 只要收集候選；射程、目標本身與已摧毀的據點由呼叫端過濾。
    /// </summary>
    public abstract class StrikeTargetWorker
    {
        public abstract void CollectTargets(StrikeTargetQuery query, List<MapParent> into);

        /// <summary>從候選裡挑一個；預設隨機。</summary>
        public virtual MapParent Choose(StrikeTargetQuery query, List<MapParent> candidates) => candidates.RandomElement();
    }

    /// <summary>只打玩家已生成地圖的據點（預設，與加入準則前相同）。</summary>
    public class StrikeTargetWorker_Players : StrikeTargetWorker
    {
        public override void CollectTargets(StrikeTargetQuery query, List<MapParent> into)
        {
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is MapParent mp && mp.HasMap && mp.Faction != null && mp.Faction.IsPlayer) { into.Add(mp); }
            }
        }
    }

    /// <summary>
    /// 打敵對陣營的據點，可包含其他 NPC：設在陣營準則上就會出現 NPC 互打。
    /// <c>&lt;targetWorker Class="Fortified.StrikeTargetWorker_Hostile"&gt;</c>
    /// </summary>
    public class StrikeTargetWorker_Hostile : StrikeTargetWorker
    {
        public bool includePlayer = true;
        public bool includeNpc = true;

        /// <summary>只打已生成地圖的據點；false 時也打抽象據點（抵達時不會生成地圖，只有反輻射彈能壓制它的防空）。</summary>
        public bool requireMap = true;

        public override void CollectTargets(StrikeTargetQuery query, List<MapParent> into)
        {
            Faction attacker = query.Attacker;
            if (attacker == null) { return; }
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (!(wo is MapParent mp) || mp.Faction == null || mp.Faction == attacker) { continue; }
                if (requireMap && !mp.HasMap) { continue; }
                if (mp.Faction.IsPlayer ? !includePlayer : !includeNpc) { continue; }
                if (!mp.Faction.HostileTo(attacker)) { continue; }
                into.Add(mp);
            }
        }
    }

    public enum AirCombatEventKind
    {
        /// <summary>某陣營第一次偵測到目標（世界層每陣營一次；地圖層在目標進入波次時）。</summary>
        TrackDetected,
        LockStarted,
        InterceptorLaunched,

        /// <summary>一次攔截結算，看 <see cref="AirCombatEvent.Outcome"/>。</summary>
        InterceptResolved,

        /// <summary>沒被攔截，抵達目標。</summary>
        TrackArrived,

        /// <summary>導引被干擾機打掉。<see cref="AirCombatEvent.Removed"/> = 直接落在地圖外。</summary>
        TrackJammed,
    }

    public enum AirInterceptOutcome
    {
        None,
        Hit,
        Miss,

        /// <summary>目標已改變航線或已消失。</summary>
        Evaded,

        /// <summary>飛行中雙方變成友軍，攔截彈安全解除。</summary>
        Safed,
    }

    /// <summary>
    /// 防空事件。事件當下目標可能已被移除，但這裡的資料仍可讀取；
    /// 地圖層的 <see cref="TargetThing"/> 在命中後可能已銷毀，請先看 <c>Destroyed</c>。
    /// </summary>
    public sealed class AirCombatEvent
    {
        public AirCombatEventKind Kind { get; set; }
        public AirCombatLayer Layer { get; set; }
        public Faction Defender { get; set; }
        public Faction Attacker { get; set; }
        public MapParent Site { get; set; }
        public Map Map { get; set; }
        public IAirTrack Track { get; set; }
        public WorldObject TargetObject { get; set; }
        public Thing TargetThing { get; set; }
        public AirTrackSignature Signature { get; set; }
        public IMissileLoadout TargetLoadout { get; set; }
        public IMissileLoadout InterceptorLoadout { get; set; }

        /// <summary>發射裝置、雷達或 CIWS；抽象據點為 null。</summary>
        public Thing Effector { get; set; }

        public float HitChance { get; set; }
        public AirInterceptOutcome Outcome { get; set; }

        /// <summary>空爆波及的額外擲骰，不是主要目標。</summary>
        public bool Secondary { get; set; }

        public bool Terminal { get; set; }
        public bool Removed { get; set; }
    }

    /// <summary>防空事件流。處理者丟例外時只跳過它，不影響其他處理者與戰鬥流程。</summary>
    public static class AirCombatEvents
    {
        /// <summary>所有防空事件；依 <see cref="AirCombatEvent.Kind"/> 分辨。不存檔，讀檔後不必重新訂閱。</summary>
        public static event Action<AirCombatEvent> Raised;

        private static readonly List<AirCombatListener> listeners = new List<AirCombatListener>();

        public static void AddListener(AirCombatListener listener)
        {
            if (listener != null && !listeners.Contains(listener)) { listeners.Add(listener); }
        }

        public static void RemoveListener(AirCombatListener listener) => listeners.Remove(listener);

        /// <summary>沒有處理者時回傳 false，呼叫端可以跳過建立事件。</summary>
        public static bool HasListeners
        {
            get
            {
                AirDefenseExtensions.EnsureLoaded();
                return Raised != null || listeners.Count > 0;
            }
        }

        public static void Raise(AirCombatEvent e)
        {
            if (e == null) { return; }
            AirDefenseExtensions.EnsureLoaded();
            for (int i = 0; i < listeners.Count; i++)
            {
                AirCombatListener listener = listeners[i];
                try { listener.Notify(e); }
                catch (Exception ex) { LogHandlerError(listener.GetType(), ex); }
            }
            Action<AirCombatEvent> handlers = Raised;
            if (handlers == null) { return; }
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action<AirCombatEvent>)handler)(e); }
                catch (Exception ex) { LogHandlerError(handler.Method.DeclaringType, ex); }
            }
        }

        private static void LogHandlerError(Type type, Exception e)
            => Log.ErrorOnce($"[Fortified] air combat event handler {type} threw: {e}", (type?.GetHashCode() ?? 0) ^ 0x414345);
    }

    /// <summary>XML 宣告的事件處理者。</summary>
    public abstract class AirCombatListener
    {
        public abstract void Notify(AirCombatEvent e);
    }

    /// <summary>
    /// 把無法自行實作 <see cref="IAirTrack"/> 的世界物件變成航跡。每次掃描都會呼叫，應該只回傳正在飛的物件；
    /// 不再回傳的物件，其偵測與鎖定狀態會被丟棄。
    /// </summary>
    public abstract class AirTrackProvider
    {
        public abstract void CollectTracks(List<IAirTrack> into);
    }

    /// <summary>
    /// 免寫啟動程式碼的註冊方式：把修正、事件處理者與航跡來源寫在 XML，遊戲載入時生效。
    /// </summary>
    public class AirDefenseExtensionDef : Def
    {
        public List<AirDefenseModifier> modifiers;
        public List<AirCombatListener> listeners;
        public List<AirTrackProvider> trackProviders;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) { yield return error; }
            if (modifiers.NullOrEmpty() && listeners.NullOrEmpty() && trackProviders.NullOrEmpty())
            {
                yield return $"{defName}: declares no modifiers, listeners or track providers";
            }
            if (modifiers != null)
            {
                foreach (AirDefenseModifier modifier in modifiers)
                {
                    if (modifier == null) { yield return $"{defName}: null modifier"; continue; }
                    foreach (string error in modifier.ConfigErrors(this)) { yield return error; }
                }
            }
        }
    }

    /// <summary>載入 <see cref="AirDefenseExtensionDef"/>，並保存航跡來源。</summary>
    public static class AirDefenseExtensions
    {
        private static bool loaded;
        private static readonly List<AirTrackProvider> providers = new List<AirTrackProvider>();
        private static readonly List<IAirTrack> none = new List<IAirTrack>();

        internal static void EnsureLoaded()
        {
            if (loaded) { return; }
            loaded = true;
            foreach (AirDefenseExtensionDef def in DefDatabase<AirDefenseExtensionDef>.AllDefsListForReading) { Register(def); }
        }

        /// <summary>套用一個 Def 的內容。資料庫裡的 Def 會自動套用，不必呼叫。</summary>
        public static void Register(AirDefenseExtensionDef def)
        {
            if (def == null) { return; }
            if (def.modifiers != null)
            {
                foreach (AirDefenseModifier modifier in def.modifiers) { AirDefensePipeline.Add(modifier); }
            }
            if (def.listeners != null)
            {
                foreach (AirCombatListener listener in def.listeners) { AirCombatEvents.AddListener(listener); }
            }
            if (def.trackProviders != null)
            {
                foreach (AirTrackProvider provider in def.trackProviders) { AddProvider(provider); }
            }
        }

        public static void AddProvider(AirTrackProvider provider)
        {
            if (provider != null && !providers.Contains(provider)) { providers.Add(provider); }
        }

        public static void RemoveProvider(AirTrackProvider provider) => providers.Remove(provider);

        /// <summary>所有航跡來源本次提供的航跡。</summary>
        public static List<IAirTrack> CollectProvidedTracks()
        {
            EnsureLoaded();
            if (providers.Count == 0) { return none; }
            List<IAirTrack> collected = new List<IAirTrack>();
            for (int i = 0; i < providers.Count; i++)
            {
                try { providers[i].CollectTracks(collected); }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[Fortified] air track provider {providers[i].GetType()} threw: {e}", providers[i].GetType().GetHashCode() ^ 0x415450);
                }
            }
            return collected;
        }
    }
}
