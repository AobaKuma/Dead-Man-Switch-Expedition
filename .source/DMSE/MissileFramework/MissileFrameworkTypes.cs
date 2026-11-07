using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// 本資料夾預定併入 Fortified：namespace 已用最終名稱，不得引用 DMSE 型別。
// 型別搬進 Fortified.dll 後，XML 的 Class="Fortified.X" 與存檔的 Class 屬性都不必改。
namespace Fortified
{
    /// <summary>一枚飛彈的裝配：行為擴充只透過此介面讀取，不需要知道裝配系統的具體型別。</summary>
    public interface IMissileLoadout
    {
        /// <summary>載荷容量係數 N，效果強度依此縮放。</summary>
        float PayloadCapacity { get; }

        bool PenetratesThickRoof { get; }

        /// <summary>裝配帶來的所有行為設定；順序必須穩定（存檔依序對應）。</summary>
        IEnumerable<MissileBehaviorProps> BehaviorProps { get; }
    }

    /// <summary>世界層飛行中的飛彈，<see cref="MissileBehavior"/> 的宿主。</summary>
    public interface IMissileTrack : IAirTrack
    {
        /// <summary>整條航線的進度（0–1），含先前航段；終端更新時可能回退。</summary>
        float RouteProgress { get; }

        /// <summary>宣告一次機動：飛向舊會合點的在飛攔截彈判定目標脫離。</summary>
        void NotifyManoeuvre();
    }

    /// <summary>導引頭選目標時可取得的全部資訊。</summary>
    public sealed class MissileSeekContext
    {
        public IMissileLoadout Loadout { get; set; }
        public float PayloadCapacity { get; set; }
        public Map Map { get; set; }
        public Faction Attacker { get; set; }
        public bool CanPenetrateThickRoof { get; set; }

        /// <summary>進入地圖的方位；盤旋重新搜尋或跨圖層時為 null。</summary>
        public float? ApproachHeading { get; set; }

        /// <summary>發出這次搜尋的航跡；已進入地圖（盤旋）或只是查詢時為 null。</summary>
        public IMissileTrack Track { get; set; }

        /// <summary>非 null 表示實際鎖定：同一鍵的導引頭會分散目標並登記；null 表示只查詢。</summary>
        public object ClaimKey { get; set; }

        /// <summary>純查詢用的情境：不穿透厚岩頂、不登記鎖定。</summary>
        public static MissileSeekContext Query(Map map, Faction attacker)
            => new MissileSeekContext { Map = map, Attacker = attacker };
    }

    /// <summary>引爆時可取得的全部資訊。</summary>
    public sealed class MissileImpactContext
    {
        public IMissileLoadout Loadout { get; set; }
        public float PayloadCapacity { get; set; }
        public IntVec3 Center { get; set; }
        public Map Map { get; set; }
        public Faction Attacker { get; set; }

        /// <summary>傷害歸因；被攔截等情況為 null。</summary>
        public Thing Instigator { get; set; }

        /// <summary>進入地圖的方位；沒有水平方位時為 null。</summary>
        public float? ApproachHeading { get; set; }
    }

    /// <summary>
    /// 行為的 XML 設定（共用、無狀態），掛在彈體或部件的 behaviors 下。
    /// 只需要落點、引爆階段的行為可以只覆寫這裡的方法；飛行中要保存狀態時指定 <see cref="behaviorClass"/>。
    /// </summary>
    public class MissileBehaviorProps
    {
        public System.Type behaviorClass;

        /// <summary>導引頭選定目標後可修改結果；設成 <see cref="IntVec3.Invalid"/> 表示放棄這個目標。</summary>
        public virtual void ModifySeekResult(MissileSeekContext context, ref IntVec3 cell) { }

        /// <summary>戰鬥部與載荷效果之後呼叫；在厚岩層表面引爆時不會呼叫。</summary>
        public virtual void PostDetonate(MissileImpactContext context) { }

        /// <summary>裝了這個行為的飛彈被防空瞄準時（兩層都會呼叫）；例如電子戰艙降低命中率。不可有副作用。</summary>
        public virtual void ModifyDefenseAsTarget(AirDefenseQuery query, ref float value) { }

        /// <summary>裝了這個行為的飛彈作為攔截彈時（兩層都會呼叫）；例如導引頭提高命中率。不可有副作用。</summary>
        public virtual void ModifyDefenseAsInterceptor(AirDefenseQuery query, ref float value) { }

        public virtual IEnumerable<string> ConfigErrors(Def parent)
        {
            if (behaviorClass != null && !typeof(MissileBehavior).IsAssignableFrom(behaviorClass))
            {
                yield return $"{parent.defName}: behaviorClass {behaviorClass} is not a {nameof(MissileBehavior)}";
            }
        }

        public MissileBehavior CreateBehavior(IMissileTrack track)
        {
            if (behaviorClass == null) { return null; }
            MissileBehavior behavior = (MissileBehavior)System.Activator.CreateInstance(behaviorClass);
            behavior.props = this;
            behavior.track = track;
            return behavior;
        }
    }

    /// <summary>單條航跡上的行為實例（可存檔）。生命週期到航跡進入目標地圖為止。</summary>
    public abstract class MissileBehavior : IExposable
    {
        public MissileBehaviorProps props;
        public IMissileTrack track;

        /// <summary>航跡加入世界時呼叫一次（讀檔不會再呼叫）。</summary>
        public virtual void PostLaunch() { }

        /// <summary>飛行推進後呼叫；可銷毀航跡，之後的行為不再執行。</summary>
        public virtual void FlightTick(int elapsedTicks) { }

        /// <summary>是否會改變飛行速度。為 true 時 ETA 與攔截解改走逐段積分。</summary>
        public virtual bool HasSpeedProfile => false;

        /// <summary>在指定航線進度的速度倍率。必須是進度的純函式，預測未來位置時也會呼叫。</summary>
        public virtual float SpeedFactorAt(float routeProgress) => 1f;

        public virtual void ModifySignature(ref AirTrackSignature signature) { }

        /// <summary>在指定航線進度的飛行高度。必須是進度的純函式；會改變高度的行為要同時覆寫這裡，攔截與航跡顯示才預測得到。</summary>
        public virtual void ModifyAltitudeAt(float routeProgress, ref float altitude) { }

        public virtual void PostTerminalUpdate() { }
        public virtual void OnIntercepted(Faction by) { }
        public virtual void OnJammedOut(Faction by) { }

        /// <summary>抵達時呼叫；回傳 true 表示已自行結算，不再走一般抵達流程。</summary>
        public virtual bool PreArrive() => false;

        public virtual void ExposeData() { }
    }

    /// <summary>一條航跡的行為集合：分派、彙總與存檔。</summary>
    public sealed class MissileBehaviorSet
    {
        public static readonly MissileBehaviorSet Empty = new MissileBehaviorSet(new List<MissileBehavior>());

        private readonly List<MissileBehavior> behaviors;
        public bool HasSpeedProfile { get; }
        public int Count => behaviors.Count;

        private MissileBehaviorSet(List<MissileBehavior> behaviors)
        {
            this.behaviors = behaviors;
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (behaviors[i].HasSpeedProfile) { HasSpeedProfile = true; }
            }
        }

        public static MissileBehaviorSet Create(IMissileLoadout loadout, IMissileTrack track)
        {
            if (loadout == null) { return Empty; }
            List<MissileBehavior> list = null;
            foreach (MissileBehaviorProps props in loadout.BehaviorProps)
            {
                MissileBehavior behavior = props?.CreateBehavior(track);
                if (behavior != null) { (list ??= new List<MissileBehavior>()).Add(behavior); }
            }
            return list == null ? Empty : new MissileBehaviorSet(list);
        }

        private bool Gone(IMissileTrack track) => track.Self != null && track.Self.Destroyed;

        public void PostLaunch() { foreach (MissileBehavior b in behaviors) { b.PostLaunch(); } }

        public void FlightTick(int elapsed)
        {
            foreach (MissileBehavior b in behaviors)
            {
                if (Gone(b.track)) { return; }
                b.FlightTick(elapsed);
            }
        }

        public float SpeedFactorAt(float routeProgress)
        {
            float factor = 1f;
            foreach (MissileBehavior b in behaviors) { factor *= b.SpeedFactorAt(routeProgress); }
            return Mathf.Max(0.01f, factor);
        }

        public void ModifySignature(ref AirTrackSignature signature)
        {
            foreach (MissileBehavior b in behaviors) { b.ModifySignature(ref signature); }
        }

        public void ModifyAltitudeAt(float routeProgress, ref float altitude)
        {
            foreach (MissileBehavior b in behaviors) { b.ModifyAltitudeAt(routeProgress, ref altitude); }
        }

        public void PostTerminalUpdate() { foreach (MissileBehavior b in behaviors) { b.PostTerminalUpdate(); } }
        public void OnIntercepted(Faction by) { foreach (MissileBehavior b in behaviors) { b.OnIntercepted(by); } }
        public void OnJammedOut(Faction by) { foreach (MissileBehavior b in behaviors) { b.OnJammedOut(by); } }

        public bool PreArrive()
        {
            foreach (MissileBehavior b in behaviors)
            {
                if (b.PreArrive()) { return true; }
            }
            return false;
        }

        /// <summary>依序存讀每個行為的狀態。節點不存在時（舊存檔、新增部件）維持預設值。</summary>
        public void ExposeData()
        {
            for (int i = 0; i < behaviors.Count; i++)
            {
                if (Scribe.EnterNode($"{behaviors[i].GetType().Name}_{i}"))
                {
                    try { behaviors[i].ExposeData(); }
                    finally { Scribe.ExitNode(); }
                }
            }
        }
    }

    /// <summary>飛行剖面的一段。</summary>
    public class MissileFlightPhase
    {
        /// <summary>這一段持續到整條航線的哪個進度（0–1）。</summary>
        public float untilProgress = 1f;
        public float speedFactor = 1f;
        /// <summary>這一段的飛行高度（公尺）；負值 = 不改變。</summary>
        public float altitude = -1f;
        public float signatureFactor = 1f;
        public int stealthOffset;
        /// <summary>進入這一段時視為機動，使在飛攔截彈脫靶。</summary>
        public bool evasiveEntry;
    }

    /// <summary>
    /// 分段飛行剖面（爬升、巡航、俯衝）：依航線進度改變速度與偵測特徵。
    /// <c>&lt;li Class="Fortified.MissileBehaviorProps_FlightProfile"&gt;</c>
    /// </summary>
    public class MissileBehaviorProps_FlightProfile : MissileBehaviorProps
    {
        public List<MissileFlightPhase> phases = new List<MissileFlightPhase>();

        public MissileBehaviorProps_FlightProfile() { behaviorClass = typeof(MissileBehavior_FlightProfile); }

        public int PhaseIndexAt(float routeProgress)
        {
            for (int i = 0; i < phases.Count; i++)
            {
                if (routeProgress < phases[i].untilProgress) { return i; }
            }
            return phases.Count - 1;
        }

        public override IEnumerable<string> ConfigErrors(Def parent)
        {
            foreach (string error in base.ConfigErrors(parent)) { yield return error; }
            if (phases.NullOrEmpty()) { yield return $"{parent.defName}: flight profile has no phases"; yield break; }
            for (int i = 0; i < phases.Count; i++)
            {
                if (phases[i].speedFactor <= 0f) { yield return $"{parent.defName}: flight phase {i} needs speedFactor > 0"; }
                if (i > 0 && phases[i].untilProgress <= phases[i - 1].untilProgress)
                {
                    yield return $"{parent.defName}: flight phases must have increasing untilProgress";
                }
            }
        }
    }

    public class MissileBehavior_FlightProfile : MissileBehavior
    {
        private int lastPhase = -1;

        private MissileBehaviorProps_FlightProfile Props => (MissileBehaviorProps_FlightProfile)props;
        private MissileFlightPhase Phase(float progress)
            => Props.phases.NullOrEmpty() ? null : Props.phases[Props.PhaseIndexAt(progress)];

        public override bool HasSpeedProfile => !Props.phases.NullOrEmpty();
        public override float SpeedFactorAt(float routeProgress) => Phase(routeProgress)?.speedFactor ?? 1f;

        public override void FlightTick(int elapsedTicks)
        {
            if (Props.phases.NullOrEmpty()) { return; }
            int phase = Props.PhaseIndexAt(track.RouteProgress);
            if (phase == lastPhase) { return; }
            bool entered = lastPhase >= 0;
            lastPhase = phase;
            if (entered && Props.phases[phase].evasiveEntry) { track.NotifyManoeuvre(); }
        }

        public override void ModifySignature(ref AirTrackSignature signature)
        {
            MissileFlightPhase phase = Phase(track.RouteProgress);
            if (phase == null) { return; }
            signature.distanceLevel *= phase.signatureFactor;
            signature.stealthLevel += phase.stealthOffset;
            if (phase.altitude >= 0f) { signature.altitude = phase.altitude; }
        }

        public override void ModifyAltitudeAt(float routeProgress, ref float altitude)
        {
            MissileFlightPhase phase = Phase(routeProgress);
            if (phase != null && phase.altitude >= 0f) { altitude = phase.altitude; }
        }

        public override void ExposeData() => Scribe_Values.Look(ref lastPhase, "lastPhase", -1);
    }
}
