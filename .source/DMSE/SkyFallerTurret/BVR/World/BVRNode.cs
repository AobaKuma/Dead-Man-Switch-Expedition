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

        /// <summary>移動中的船每次掃描都要用當前 tile。</summary>
        public PlanetTile Tile => parent.Tile;
        public Vector3 Center => MissileWorldUtility.TileCenter(Tile);
        public bool IsLocalMap => local != null;

        public float DetectionRange(BVRTargetProps target)
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

        public CompProperties_FireControlRadar DefenseFireControl => defense?.FireControl;
        public MissileConfig DefenseConfig => defense?.Config;
        public bool DefenseReady(int now) => defense != null && defense.Ready(now);
        public bool TryConsumeDefense(int now) => defense != null && defense.TryConsume(now);
    }
}
