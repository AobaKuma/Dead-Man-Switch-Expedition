using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>一枚等待生成的導彈（發射邏輯已完成，只差畫面上的升空彈）。</summary>
    public class PendingMissileLaunch : IExposable
    {
        public int tick;
        public ThingDef def;
        public IntVec3 cell;
        public Rot4 rotation;
        public Thing launcher;

        // 對地離場
        public WorldObject worldObject;

        // 攔截
        public bool interceptor;
        public int targetId = -1;
        public float hitChance;

        public bool hasHeading;
        public float heading;

        public float? Heading => hasHeading ? heading : (float?)null;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref rotation, "rotation");
            if (Scribe.mode == LoadSaveMode.Saving && launcher != null && launcher.Destroyed) { launcher = null; }
            Scribe_References.Look(ref launcher, "launcher");
            Scribe_Deep.Look(ref worldObject, "worldObject");
            Scribe_Values.Look(ref interceptor, "interceptor", false);
            Scribe_Values.Look(ref targetId, "targetId", -1);
            Scribe_Values.Look(ref hitChance, "hitChance", 0f);
            Scribe_Values.Look(ref hasHeading, "hasHeading", false);
            Scribe_Values.Look(ref heading, "heading", 0f);
        }
    }

    /// <summary>
    /// 導彈發射排程：同一張地圖的升空彈至少相隔 <see cref="SpacingTicks"/> 生成，不會在同一 tick 一起竄出
    /// （多座發射架同波齊射、ripple 續射、多具 SAM 同時接戰、多選建築一起按發射）。
    ///
    /// 只錯開「畫面上的升空彈」：彈藥消耗、冷卻、BVR 通道、世界航跡物件的建立都在發射當下完成；
    /// 對地彈的世界航跡在升空彈飛出地圖時才加入世界，所以會跟著延後同樣的 tick（打擊時間軸以
    /// <see cref="ScorerProjectile_WorldObject.launchSpawnTick"/> 計入）。發射後焰在升空彈實際生成時才觸發。
    /// 等待中的發射隨地圖存檔。
    /// </summary>
    public class MapComponent_MissileLaunchQueue : MapComponent
    {
        /// <summary>同一張地圖兩枚升空彈的最小間隔（tick）。</summary>
        public const int SpacingTicks = 6;

        private int lastSlotTick = -100000;
        private List<PendingMissileLaunch> pending = new List<PendingMissileLaunch>();

        public MapComponent_MissileLaunchQueue(Map map) : base(map) { }

        public static MapComponent_MissileLaunchQueue For(Map map) => map?.GetComponent<MapComponent_MissileLaunchQueue>();

        /// <summary>經排程發射（地圖沒有排程元件時立刻生成）；回傳實際生成 tick。</summary>
        public static int Launch(Map map, PendingMissileLaunch launch)
        {
            MapComponent_MissileLaunchQueue queue = For(map);
            if (queue != null)
            {
                queue.Request(launch, out int spawnTick);
                return spawnTick;
            }
            queue = new MapComponent_MissileLaunchQueue(map);
            launch.tick = Find.TickManager.TicksGame;
            queue.Spawn(launch);
            return launch.tick;
        }

        /// <summary>等待生成的對地離場彈數。</summary>
        public int PendingDepartures => pending.Count(p => !p.interceptor);
        public int PendingCount => pending.Count;

        /// <summary>下一個可用的生成 tick（不保留）。</summary>
        public int NextSlot(int now) => Mathf.Max(now, lastSlotTick + SpacingTicks);

        /// <summary>排入一枚導彈：時段空著就立刻生成並回傳升空彈，否則排到下一個時段、回傳 null。回傳的 tick 為實際生成時刻。</summary>
        public Thing Request(PendingMissileLaunch launch, out int spawnTick)
        {
            int now = Find.TickManager.TicksGame;
            spawnTick = NextSlot(now);
            lastSlotTick = spawnTick;
            launch.tick = spawnTick;
            if (spawnTick <= now) { return Spawn(launch); }
            pending.Add(launch);
            return null;
        }

        public override void MapComponentTick()
        {
            if (pending.Count == 0) { return; }
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingMissileLaunch launch = pending[i];
                if (launch.tick > now) { continue; }
                pending.RemoveAt(i);
                i--;
                Spawn(launch);
            }
        }

        private Thing Spawn(PendingMissileLaunch launch)
        {
            if (launch.def == null) { return null; }
            Thing launcher = launch.launcher != null && !launch.launcher.Destroyed ? launch.launcher : null;
            if (launcher is ThingWithComps building && building.Spawned && building.Map == map)
            {
                CompLaunchBackblast.TryTrigger(building);
            }
            IntVec3 cell = launch.cell.InBounds(map) ? launch.cell : launch.cell.ClampInsideMap(map);
            return launch.interceptor
                ? MissileProjectileMaker.LaunchInterceptor(launch.def, cell, map, launch.rotation, launcher, launch.targetId, launch.hitChance, launch.Heading)
                : MissileProjectileMaker.LaunchDeparture(launch.def, cell, map, launch.rotation, launcher, launch.worldObject, launch.Heading);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref lastSlotTick, "lastSlotTick", -100000);
            Scribe_Collections.Look(ref pending, "pending", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pending == null) { pending = new List<PendingMissileLaunch>(); }
        }
    }
}
