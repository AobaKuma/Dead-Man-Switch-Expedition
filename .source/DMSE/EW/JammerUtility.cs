using Fortified;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>一張地圖上某陣營的干擾圈（同圖多座取最大半徑）。</summary>
    public struct JammerRing
    {
        public MapParent parent;
        public Faction faction;
        public PlanetTile tile;
        public Vector3 center;
        public float range;
    }

    /// <summary>
    /// 世界層進圈判定（§3.2）：每條可被干擾的敵對航跡第一次進圈時擲一次骰，結果存在
    /// <see cref="BVRTrackState"/>，讀檔不重擲。圈內全強度，重疊取最強、不疊加。
    /// </summary>
    public static class JammerUtility
    {
        public static IEnumerable<CompJammer> AllJammers()
        {
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                MapComponent_EWControl control = MapComponent_EWControl.For(maps[i]);
                if (control == null) { continue; }
                foreach (CompJammer jammer in control.jammers) { yield return jammer; }
            }
        }

        public static bool Covers(CompJammer jammer, IBVRTrack track)
            => MissileWorldUtility.SurfaceTilesBetween(MissileWorldUtility.TileCenter(jammer.parent.Map.Tile), track.PositionAt(0))
               <= jammer.Props.worldRange;

        /// <summary>這座干擾機是否會影響這條航跡：運作中、與發射方敵對（同陣營與無陣營都不受影響）、在圈內。</summary>
        public static bool Affects(CompJammer jammer, IBVRTrack track)
            => jammer.CurrentJamPower > 0f && jammer.parent.Faction != null && jammer.parent.Faction.HostileTo(track.Attacker)
               && Covers(jammer, track);

        public static bool Vulnerable(MissileConfig config) => (config?.TargetingGuidance?.JamVulnerability ?? 0f) > 0f;

        /// <summary>掃描前呼叫（§3.2 順序）：已被判定消失的彈不會先被鎖定、白射一枚攔截彈。</summary>
        public static void ProcessTracks(IEnumerable<MissileWorldObject> tracks)
        {
            List<CompJammer> jammers = AllJammers().Where(j => j.CurrentJamPower > 0f).ToList();
            if (jammers.Count == 0) { return; }
            foreach (MissileWorldObject track in tracks.ToList())
            {
                if (track.State.jamRolled || !Vulnerable(track.Config)) { continue; }
                CompJammer strongest = null;
                foreach (CompJammer jammer in jammers)
                {
                    if (Affects(jammer, track) && (strongest == null || jammer.CurrentJamPower > strongest.CurrentJamPower))
                    {
                        strongest = jammer;
                    }
                }
                if (strongest != null) { Roll(track, strongest); }
            }
        }

        public static float BreakChance(CompJammer jammer, MissileConfig config)
            => Mathf.Clamp01(jammer.CurrentJamPower * jammer.Props.jamGuidanceBreakFactor
                * (config?.TargetingGuidance?.JamVulnerability ?? 0f));

        private static void Roll(MissileWorldObject track, CompJammer jammer)
        {
            BVRTrackState state = track.State;
            state.jamRolled = true;
            if (!Rand.Chance(BreakChance(jammer, track.Config))) { return; }
            Faction by = jammer.parent.Faction;
            Map map = jammer.parent.Map;
            if (Rand.Chance(jammer.Props.offMapChance))
            {
                track.Notify_JammedOut(by);
                AirDefenseHooks.WorldEvent(AirCombatEventKind.TrackJammed, track, by, map?.Parent, map, effector: jammer.parent, removed: true);
                return;
            }
            state.jamDenied = true;
            AirDefenseHooks.WorldEvent(AirCombatEventKind.TrackJammed, track, by, map?.Parent, map, effector: jammer.parent);
            if (by == Faction.OfPlayer || track.Attacker == Faction.OfPlayer)
            {
                Messages.Message("DMSE.EW.Jammer.GuidanceDenied".Translate(track.Label), new GlobalTargetInfo(track),
                    by == Faction.OfPlayer ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, historical: false);
            }
        }

        public static int TracksCoveredBy(CompJammer jammer)
        {
            WorldComponent_BVRNetwork network = WorldComponent_BVRNetwork.Current;
            return network == null || jammer.CurrentJamPower <= 0f ? 0 : network.Tracks.Count(t => Affects(jammer, t));
        }

        /// <summary>世界地圖要畫的紅圈。</summary>
        public static IEnumerable<JammerRing> ActiveRings()
        {
            var rings = new Dictionary<(MapParent, Faction), JammerRing>();
            foreach (CompJammer jammer in AllJammers())
            {
                if (jammer.CurrentJamPower <= 0f) { continue; }
                Map map = jammer.parent.Map;
                if (map?.Parent == null || jammer.parent.Faction == null) { continue; }
                var key = (map.Parent, jammer.parent.Faction);
                if (rings.TryGetValue(key, out JammerRing existing) && existing.range >= jammer.Props.worldRange) { continue; }
                rings[key] = new JammerRing
                {
                    parent = map.Parent,
                    faction = jammer.parent.Faction,
                    tile = map.Tile,
                    center = MissileWorldUtility.TileCenter(map.Tile),
                    range = jammer.Props.worldRange,
                };
            }
            return rings.Values;
        }
    }

    /// <summary>
    /// 對敵方雷達的壓制場（ECM 文件 §3）：與飛彈導引共用同一個圓。雷達吃 jamEff（距離衰減、等級差），
    /// 飛彈吃圈內全強度。還沒有 ECCM，所以 eccmLevel 固定 0。jamEff 不存檔，每次掃描重算。
    /// </summary>
    public static class EWEnvironment
    {
        /// <summary>純公式：jamSum = 1 − Π(1 − raw)，再依等級差放大。</summary>
        public static float JamEff(float jamSum, int jamLevel, int eccmLevel = 0)
        {
            int gap = Mathf.Clamp(jamLevel - eccmLevel, -4, 2);
            return Mathf.Clamp01(jamSum * Mathf.Pow(BVRWorldTuningDef.Current.jamLevelScaling, gap));
        }

        /// <summary>防守方 <paramref name="defender"/> 在 <paramref name="center"/> 受到的敵對干擾。</summary>
        public static float JamAt(Vector3 center, Faction defender)
        {
            if (defender == null) { return 0f; }
            float clear = 1f;
            int level = int.MinValue;
            foreach (CompJammer jammer in JammerUtility.AllJammers())
            {
                float power = jammer.CurrentJamPower;
                if (power <= 0f || jammer.parent.Faction == null || !jammer.parent.Faction.HostileTo(defender)) { continue; }
                float distance = MissileWorldUtility.SurfaceTilesBetween(MissileWorldUtility.TileCenter(jammer.parent.Map.Tile), center);
                float raw = power * Mathf.Clamp01(1f - distance / Mathf.Max(0.01f, jammer.Props.worldRange));
                if (raw <= 0f) { continue; }
                clear *= 1f - raw;
                level = Mathf.Max(level, jammer.Props.jamLevel);
            }
            return level == int.MinValue ? 0f : JamEff(1f - clear, level);
        }

        public static float NodeJam(BVRNode node) => node.parent == null ? 0f : JamAt(node.Center, node.faction);

        public static float DetectionFactor(float jamEff)
        {
            if (jamEff <= 0f) { return 1f; }
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            return Mathf.Max(tuning.jamMinDetectionFactor, 1f - tuning.jamDetectionPenalty * jamEff);
        }

        public static float LockFactor(float jamEff) => jamEff <= 0f ? 1f : 1f + BVRWorldTuningDef.Current.jamLockPenalty * jamEff;
    }
}
