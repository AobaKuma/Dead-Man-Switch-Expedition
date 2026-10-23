using System.Linq;
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

        public bool UsesAlertRules => alertStage > 0;

        /// <summary>移動中的船每次掃描都要用當前 tile。</summary>
        public PlanetTile Tile => parent.Tile;
        public Vector3 Center => MissileWorldUtility.TileCenter(Tile);
        public bool IsLocalMap => local != null;

        /// <summary>目前實際的探測半徑（已套用警戒階段）。</summary>
        public float DetectionRange(BVRTargetProps target) => EquipmentDetectionRange(target) * rangeFactor;

        /// <summary>設備正常有效探測半徑（不看警戒階段；仍含反隱身與雷達壓制）。</summary>
        public float EquipmentDetectionRange(BVRTargetProps target)
        {
            if (defense != null) { return defense.DetectionRange(target); }

            float range = 0;
            foreach (CompSearchRadar r in local.searchRadars)
            {
                if (r.Active && r.parent.Faction == faction)
                {
                    range = Mathf.Max(range,
                        MissileWorldMotion.DetectionRange(r.Props.worldDetectionRange, r.Props.antiStealthLevel, target));
                }
            }
            return range;
        }

        /// <summary>本節點自己的感測器是否看見這條航跡（不含資料鏈）。</summary>
        public bool CanSee(IBVRTrack track)
        {
            float range = DetectionRange(track.TargetProps);
            return range > 0f && MissileWorldUtility.SurfaceTilesBetween(Center, track.PositionAt(0)) <= range;
        }

        public bool DestinationIs(IBVRTrack track) => track.Destination == Tile;

        /// <summary>區域防禦的接戰條件：航跡以本節點為目的地，或（開啟時）正飛越本節點的覆蓋範圍。</summary>
        public bool CanEngage(IBVRTrack track)
            => DestinationIs(track)
               || (BVRWorldTuningDef.Current.areaDefenseEnabled && CanSee(track));

        /// <summary>
        /// 威脅評估：通道不足時先接戰雷達截面最大的航跡。抽象節點看 XML 旗標；
        /// 非玩家的 Spacer 以上地圖節點預設開啟。
        /// </summary>
        public bool ThreatAssessment => defense != null
            ? defense.ThreatAssessment
            : faction != null && !faction.IsPlayer && faction.def.techLevel >= TechLevel.Spacer;

        /// <summary>有攔截彈可用（現在或冷卻後）。</summary>
        public bool HasInterceptorAmmo => local != null
            ? local.HasInterceptorAmmo(faction)
            : defense != null && defense.Available && defense.Stock > 0;

        public int FreeChannels => local != null
            ? local.Channels.Free(faction)
            : Mathf.Max(0, (defense?.Channels ?? 0) - (WorldComponent_BVRNetwork.Current?.CountEngaged(parent, faction) ?? 0));

        public CompFireControlRadar BestRadar(BVRTarget target, int eta) => local?.fireControlRadars
            .Where(fc => fc.Active && fc.parent.Faction == faction && eta <= fc.Props.maxRangeTicks)
            .OrderByDescending(fc => fc.ComputeHitChance(target, eta)).FirstOrDefault();

        public CompMissileLauncher_Interceptor ReadyLauncher(int now) => local?.launchers
            .Where(l => l.parent.Faction == faction && l.ReadyToFire(now))
            .OrderBy(l => l.CooldownUntil).FirstOrDefault();

        /// <summary>有可用火控：地圖上運作中的火控雷達，或抽象節點的火控設定。</summary>
        public bool HasFireControl => local != null
            ? local.fireControlRadars.Any(fc => fc.Active && fc.parent.Faction == faction)
            : defense?.FireControl != null;

        /// <summary>
        /// 這個節點依自己的規則是否會接戰此航跡（不含射程、火控與彈藥）：
        /// 警戒節點看階段政策，其餘沿用只打敵對。
        /// </summary>
        public bool PolicyAllows(IBVRTrack track) => UsesAlertRules
            ? AirDefenseAlertPolicy.MayEngage(alertStage, faction, track.Attacker, WorldComponent_FactionAirDefenseAlert.TargetSite(track)?.Faction)
            : AirDefenseAlertPolicy.LegacyMayEngage(faction, track.Attacker);

        /// <summary>這個節點是否會為此航跡產生／更新情報。</summary>
        public bool MayObserve(IBVRTrack track) => UsesAlertRules
            ? AirDefenseAlertPolicy.MayObserve(faction, track.Attacker)
            : AirDefenseAlertPolicy.LegacyMayEngage(faction, track.Attacker);

        public CompProperties_FireControlRadar DefenseFireControl => defense?.FireControl;
        public MissileConfig DefenseConfig => defense?.Config;
        public bool DefenseReady(int now) => defense != null && defense.Ready(now);
        public bool TryConsumeDefense(int now) => defense != null && defense.TryConsume(now);
    }
}
