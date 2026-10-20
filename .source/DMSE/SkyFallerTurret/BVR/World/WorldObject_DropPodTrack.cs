using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 空投虛擬航跡（P4）：把已在地圖內結算的空投（<see cref="Skyfaller"/>）在世界層也表示成一條航跡，
    /// 讓世界雷達／攔截彈能提前接戰。真正的載荷仍留在目標地圖的波次裡（<see cref="BVRTarget"/>），
    /// 這個世界物件只是它的「幽靈」：被攔截時同步移除地圖波次目標，抵達時自行消失。
    ///
    /// 由 <see cref="BVRWorldTuningDef.podsUseWorldTracks"/> 開啟；預設關閉，避免改變既有空投流程。
    /// </summary>
    public class WorldObject_DropPodTrack : MissileWorldObject
    {
        public int spawnTick;
        public int arrivalTick;
        public Vector3 startPos;
        public Vector3 endPos;
        public PlanetTile targetTile = PlanetTile.Invalid;
        public Map targetMap;
        public int waveTargetId = -1;
        public ThingDef incomingDef;

        public override ThingDef IncomingDef => incomingDef;
        public override PlanetTile Destination => targetTile;
        protected override bool AllowGenerateTargetMap => false;

        public override float AngularSpeed
            => arrivalTick <= spawnTick ? 0f
                : GenMath.SphericalDistance(startPos.normalized, endPos.normalized) / (arrivalTick - spawnTick);

        public override int TicksToArrival => Mathf.Max(0, arrivalTick - Find.TickManager.TicksGame);

        public override Vector3 PositionAt(int ticksAhead)
        {
            int total = Mathf.Max(1, arrivalTick - spawnTick);
            float pct = Mathf.Clamp01((Find.TickManager.TicksGame - spawnTick + Mathf.Max(0, ticksAhead)) / (float)total);
            return Vector3.Slerp(startPos, endPos, pct);
        }

        public override Vector3 DrawPos => PositionAt(0);

        protected override void TickInterval(int delta)
        {
            int now = Find.TickManager.TicksGame;
            if (targetMap == null || targetMap.Disposed) { Destroy(); return; }

            MapComponent_BVRCombat bvr = targetMap.GetComponent<MapComponent_BVRCombat>();
            if (bvr == null || bvr.GetTarget(waveTargetId) == null)
            {
                // 地圖波次已被攔截或已落地：幽靈航跡沒有意義了。
                Destroy();
                return;
            }
            if (now >= arrivalTick)
            {
                arrived = true;
                Destroy();
            }
        }

        protected override void OnArrived(Map map) => Destroy();

        /// <summary>被世界層攔截：同步移除地圖波次裡的同一個目標（不落地、不留殘骸）。</summary>
        protected override void OnIntercepted(Faction by)
        {
            if (targetMap == null || targetMap.Disposed) { return; }
            MapComponent_BVRCombat bvr = targetMap.GetComponent<MapComponent_BVRCombat>();
            if (bvr != null && bvr.GetTarget(waveTargetId) != null) { bvr.RemoveTarget(waveTargetId); }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref spawnTick, "podSpawnTick");
            Scribe_Values.Look(ref arrivalTick, "podArrivalTick");
            Scribe_Values.Look(ref startPos, "podStartPos");
            Scribe_Values.Look(ref endPos, "podEndPos");
            Scribe_Values.Look(ref targetTile, "podTargetTile", PlanetTile.Invalid);
            Scribe_References.Look(ref targetMap, "podTargetMap");
            Scribe_Values.Look(ref waveTargetId, "podWaveTargetId", -1);
            Scribe_Defs.Look(ref incomingDef, "podIncomingDef");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                arrived = false;
            }
        }
    }
}
