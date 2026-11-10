using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 世界層的防空節點：地圖上的實體雷達（<see cref="local"/>），或抽象據點（<see cref="defense"/>：
    /// NPC 導彈陣地的元件，或一般據點的合成防空）。
    ///
    /// P4 區域防禦：節點可以接戰「飛越自己覆蓋範圍」的航跡，不再要求航跡以自己為目的地；
    /// 目的地節點仍是首選（點防禦），其餘由最近的可用節點接手。
    /// </summary>
    public class BVRNode
    {
        public MapParent parent;
        public Faction faction;
        public MapComponent_BVRCombat local;
        public IBVRDefense defense;

        /// <summary>
        /// NPC 防空警戒階段（1–5）；0 = 不適用警戒規則（玩家節點，或警戒系統關閉），沿用舊的只打敵對規則。
        /// 由 <see cref="WorldComponent_BVRNetwork"/> 建立節點時填入。
        /// </summary>
        public int alertStage;

        /// <summary>警戒階段對探測半徑的係數（第一階段 0、第二階段觀測半徑、雷達暖機中 0）。</summary>
        public float rangeFactor = 1f;

        /// <summary>敵方干擾機對本節點的壓制（0–1）；不存檔，建立節點時重算（見 <see cref="EWEnvironment"/>）。</summary>
        public float jamEff;

        public bool UsesAlertRules => alertStage > 0;

        /// <summary>玩家節點要遵守的戰情終端規則；沒有終端或不是地圖節點時為 null（走舊規則）。</summary>
        private MapComponent_EWControl Roe => local != null ? MapComponent_EWControl.ActiveRoe(local.map, faction) : null;

        /// <summary>移動中的船每次掃描都要用當前 tile。</summary>
        public PlanetTile Tile => parent.Tile;
        public Vector3 Center => MissileWorldUtility.TileCenter(Tile);
        public bool IsLocalMap => local != null;

        /// <summary>目前實際的探測半徑（已套用警戒階段）。</summary>
        public float DetectionRange(BVRTargetProps target, IBVRTrack track = null) => EquipmentDetectionRange(target, track) * rangeFactor;

        /// <summary>設備正常有效探測半徑（不看警戒階段；仍含反隱身與雷達壓制）。</summary>
        public float EquipmentDetectionRange(BVRTargetProps target, IBVRTrack track = null)
            => AirDefenseHooks.WorldStat(AirDefenseStat.Detection, RawDetectionRange(target), this, track, target);

        private float RawDetectionRange(BVRTargetProps target)
        {
            float jam = EWEnvironment.DetectionFactor(jamEff);
            if (defense != null) { return defense.DetectionRange(target) * jam; }

            float range = 0;
            foreach (CompSearchRadar r in local.searchRadars)
            {
                if (r.Active && r.parent.Faction == faction)
                {
                    range = Mathf.Max(range, r.EffectiveWorldRange(target));
                }
            }
            return range * jam;
        }

        /// <summary>本節點自己的感測器是否看見這條航跡（不含資料鏈）。</summary>
        public bool CanSee(IBVRTrack track)
        {
            float range = DetectionRange(track.TargetProps, track);
            return range > 0f && MissileWorldUtility.SurfaceTilesBetween(Center, track.PositionAt(0)) <= range;
        }

        public bool DestinationIs(IBVRTrack track) => track.Destination == Tile;

        /// <summary>區域防禦的接戰條件：航跡以本節點為目的地，或（開啟時）正飛越本節點的覆蓋範圍。</summary>
        public bool CanEngage(IBVRTrack track)
            => DestinationIs(track)
               || (BVRWorldTuningDef.Current.areaDefenseEnabled && CanSee(track));

        /// <summary>
        /// 威脅評估：通道不足時先接戰雷達截面最大的航跡。抽象節點看自己的設定；
        /// 玩家地圖節點看戰情終端，NPC 地圖節點看陣營準則。
        /// </summary>
        public bool ThreatAssessment => defense != null
            ? defense.ThreatAssessment
            : faction != null && (faction.IsPlayer
                ? Roe?.priority == EngagePriority.LargestFirst
                : MissileDoctrine.For(faction)?.threatAssessment == true);

        private BVRDefenseEffector defenseEffector;

        /// <summary>本節點的所有武器：抽象據點的庫存，或地圖上屬於本陣營的發射架與註冊的效果器。</summary>
        public IEnumerable<IAirEffector> Effectors
        {
            get
            {
                if (defense != null) { yield return defenseEffector ??= new BVRDefenseEffector(defense); }
                if (local == null) { yield break; }
                foreach (CompMissileLauncher_Interceptor launcher in local.launchers)
                {
                    if (launcher.parent.Faction == faction) { yield return launcher; }
                }
                MapComponent_AirEffectors registered = local.map?.GetComponent<MapComponent_AirEffectors>();
                if (registered == null) { yield break; }
                foreach (IAirEffector effector in registered.Effectors)
                {
                    if (effector.Source?.Faction == faction) { yield return effector; }
                }
            }
        }

        /// <summary>有走火控流程的武器可用（現在或冷卻後）。</summary>
        public bool HasInterceptorAmmo => Effectors.Any(e => e.UsesFireControl && e.Armed);

        /// <summary>有能打這個領域的火控武器可用（現在或冷卻後）。</summary>
        public bool HasInterceptorAmmoFor(CombatDomain domain) => Effectors.Any(e => e.UsesFireControl && e.Armed && CombatDomainUtility.Engages(e, domain));

        /// <summary>有點防禦式的武器：不走火控，每次掃描各自判斷。</summary>
        public bool HasAutonomousEffector => Effectors.Any(e => !e.UsesFireControl);

        /// <summary>現在能打這個目標的火控武器，最閒置的優先；沒有時回傳 null。</summary>
        public IAirEffector ReadyEffector(int now, AirEngagement engagement)
        {
            IAirEffector best = null;
            foreach (IAirEffector effector in Effectors)
            {
                if (!effector.UsesFireControl || !effector.CanFire(now) || !effector.CanEngage(engagement)) { continue; }
                if (best == null || effector.ReadyTick < best.ReadyTick) { best = effector; }
            }
            return best;
        }

        public AirEngagement EngagementFor(IBVRTrack track, WorldObject targetObject, Vector3 position, int eta, float altitude) => new AirEngagement
        {
            Track = AirDefenseHooks.Public(track),
            TargetObject = targetObject,
            Defender = faction,
            Site = parent,
            Map = local?.map,
            Origin = Center,
            DistanceTiles = MissileWorldUtility.SurfaceTilesBetween(Center, position),
            EtaTicks = eta,
            Altitude = altitude,
            Domain = (byte)CombatDomainUtility.Of(track),
        };

        public int FreeChannels => local != null
            ? Mathf.Min(local.Channels.Free(faction), Roe?.RoeChannelsLeft(faction) ?? int.MaxValue)
            : Mathf.Max(0, (defense?.Channels ?? 0) - (WorldComponent_BVRNetwork.Current?.CountEngaged(parent, faction) ?? 0));

        public CompFireControlRadar BestRadar(BVRTarget target, int eta) => local?.fireControlRadars
            .Where(fc => fc.Active && fc.parent.Faction == faction && eta <= fc.Props.maxRangeTicks
                && fc.Props.InAltitudeEnvelope(target.altitude) && fc.Props.Covers(target.domain))
            .OrderByDescending(fc => fc.ComputeHitChance(target, eta)).FirstOrDefault();

        /// <summary>有可用火控：地圖上運作中的火控雷達，或抽象節點的火控設定。</summary>
        public bool HasFireControl => local != null
            ? local.fireControlRadars.Any(fc => fc.Active && fc.parent.Faction == faction)
            : defense?.FireControl != null;

        /// <summary>有火控能導引這個領域（<see cref="CombatDomain"/>）。不行時這個節點完全不接戰，交給其他節點。</summary>
        public bool CoversDomain(CombatDomain domain)
        {
            if (local == null) { return defense?.FireControl?.Covers(domain) == true; }
            foreach (CompFireControlRadar fc in local.fireControlRadars)
            {
                if (fc.Active && fc.parent.Faction == faction && fc.Props.Covers(domain)) { return true; }
            }
            return false;
        }

        /// <summary>有火控能打到這個高度。打不到時交給其他節點，不佔住接戰權。</summary>
        public bool CanReachAltitude(float altitude)
        {
            if (local == null) { return defense?.FireControl?.InAltitudeEnvelope(altitude) == true; }
            foreach (CompFireControlRadar fc in local.fireControlRadars)
            {
                if (fc.Active && fc.parent.Faction == faction && fc.Props.InAltitudeEnvelope(altitude)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// 這個節點依自己的規則是否會接戰此航跡（不含射程、火控與彈藥）：
        /// 警戒節點看階段政策，其餘沿用只打敵對。
        /// </summary>
        public bool PolicyAllows(IBVRTrack track) => UsesAlertRules
            ? AirDefenseAlertPolicy.MayEngage(alertStage, faction, track.Attacker, WorldComponent_FactionAirDefenseAlert.TargetSite(track)?.Faction)
            : Roe?.AllowsTrack(track) ?? AirDefenseAlertPolicy.LegacyMayEngage(faction, track.Attacker);

        /// <summary>這個節點是否會為此航跡產生／更新情報。交戰限制不影響偵測；開放中立時也要看得到中立航跡。</summary>
        public bool MayObserve(IBVRTrack track) => UsesAlertRules
            ? AirDefenseAlertPolicy.MayObserve(faction, track.Attacker)
            : AirDefenseAlertPolicy.LegacyMayEngage(faction, track.Attacker)
              || (Roe?.engageNeutral == true && AirDefenseAlertPolicy.IsNonAlly(faction, track.Attacker));

        public CompProperties_FireControlRadar DefenseFireControl => defense?.FireControl;
    }
}
