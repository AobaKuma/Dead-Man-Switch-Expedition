using System;
using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using static DMSE.BVRAcceptance.BVRFixture;

namespace DMSE.BVRAcceptance
{
    /// <summary>The shape a third-party weapon mod would ship; it only knows the framework interface.</summary>
    public class TestEffector : IAirEffector
    {
        public Thing source;
        public bool fireControl;
        public float rangeTiles = 1000f, hitChance = 1f, angularSpeed;
        public int cooldownTicks = 300, cooldownUntil, shots;
        public float lastDistance = -1f;
        public AirEngagement lastEngagement;

        public Thing Source => source;
        public bool UsesFireControl => fireControl;
        public bool Armed => source != null && source.Spawned;
        public int ReadyTick => cooldownUntil;
        public bool CanFire(int now) => Armed && now >= cooldownUntil;
        public bool CanEngage(AirEngagement engagement) => engagement.DistanceTiles <= rangeTiles;
        public AirEffectorShot PrepareShot(AirEngagement engagement) => new AirEffectorShot { AngularSpeed = angularSpeed, BaseHitChance = hitChance };
        public bool TryFire(AirEngagement engagement, int now, Vector3 meetingPoint)
        {
            shots++;
            cooldownUntil = now + cooldownTicks;
            lastDistance = engagement.DistanceTiles;
            lastEngagement = engagement;
            return true;
        }
    }

    /// <summary>Weapons behind IAirEffector on the real world scan: point defense without fire control, and a third-party fire-control weapon.</summary>
    public static class Tests_AirEffector
    {
        public static List<AcceptanceTest> Tests() => new List<AcceptanceTest>
        {
            new AcceptanceTest("AF1", "A registered laser shoots down a track inside its range, without a lock, a channel or an interceptor", InstantLaser),
            new AcceptanceTest("AF2", "A missed point-defense weapon keeps its cooldown; another faction's weapon stays silent", CooldownAndFaction),
            new AcceptanceTest("AF3", "A third-party fire-control weapon goes through lock, channel and a flying interceptor", FireControlWeapon),
            new AcceptanceTest("AF4", "Blockade interception fires through the node's weapon", BlockadeThroughEffector),
        };

        private static MapComponent_AirEffectors Registry => Home.GetComponent<MapComponent_AirEffectors>();
        private static int Interceptors => Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>().Count(i => !i.Destroyed);

        public static IEnumerable<Wait> InstantLaser(TestContext t)
        {
            Thing radar = SpawnBuilding(PhaseArray, 10, 10);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            t.Require(Registry != null, "the map has an effector registry");
            var laser = new TestEffector { source = radar, rangeTiles = 8f };
            List<AirCombatEvent> seen = new List<AirCombatEvent>();
            Action<AirCombatEvent> record = seen.Add;
            Registry.Register(laser);
            AirCombatEvents.Raised += record;
            try
            {
                PlanetTile source = TileAtDistance(19f, 21f);
                t.Require(source.Valid, "a tile ~20 tiles away exists");
                WorldObject_IncomingMissile missile = LaunchEnemyMissile(source);
                int interceptorsBefore = Interceptors;
                int capacity = HomeBVR.Channels.Free(Player);
                var waitShot = new WaitUntil(() => missile.Destroyed, missile.TicksToArrival);
                yield return waitShot;
                t.Require(!waitShot.TimedOut, "the missile is shot down before arrival");
                t.Note($"fired at {laser.lastDistance:0.0} tiles");
                t.Check(laser.shots == 1, $"one shot was enough ({laser.shots})");
                t.Check(laser.lastDistance > 0f && laser.lastDistance <= 8f, $"it waited for its own range ({laser.lastDistance:0.0} of 8)");
                t.Check(laser.lastEngagement.Track == missile && laser.lastEngagement.Defender == Player && laser.lastEngagement.Site == Home.Parent
                    && laser.lastEngagement.Map == Home, "the engagement names the track, side, site and map");
                t.Check(Interceptors == interceptorsBefore, "no interceptor was created");
                t.Check(HomeBVR.Channels.Free(Player) == capacity, "no fire-control channel is held");
                List<AirCombatEventKind> kinds = seen.Where(e => e.Track == missile).Select(e => e.Kind).ToList();
                t.Note("events: " + string.Join(", ", kinds));
                t.Check(!kinds.Contains(AirCombatEventKind.LockStarted), "no lock was taken");
                AirCombatEvent launched = seen.First(e => e.Track == missile && e.Kind == AirCombatEventKind.InterceptorLaunched);
                t.Check(launched.Effector == radar && launched.Defender == Player, "the launch event names the weapon");
                AirCombatEvent resolved = seen.Last(e => e.Track == missile);
                t.Check(resolved.Kind == AirCombatEventKind.InterceptResolved && resolved.Outcome == AirInterceptOutcome.Hit && resolved.HitChance == 1f, "the result is reported");
            }
            finally
            {
                AirCombatEvents.Raised -= record;
                Registry.Deregister(laser);
            }
        }

        public static IEnumerable<Wait> CooldownAndFaction(TestContext t)
        {
            Thing radar = SpawnBuilding(PhaseArray, 10, 10);
            Thing enemyGun = SpawnBuilding(PhaseArray, 16, 10, Enemy);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            var dud = new TestEffector { source = radar, hitChance = 0f, cooldownTicks = 300 };
            var foreign = new TestEffector { source = enemyGun };
            Registry.Register(dud);
            Registry.Register(foreign);
            try
            {
                PlanetTile source = TileAtDistance(19f, 21f);
                t.Require(source.Valid, "a tile ~20 tiles away exists");
                WorldObject_IncomingMissile missile = LaunchEnemyMissile(source);
                int start = -1;
                var waitFirst = new WaitUntil(() => dud.shots > 0, missile.TicksToArrival);
                yield return waitFirst;
                t.Require(!waitFirst.TimedOut, "the weapon fires");
                start = Find.TickManager.TicksGame;
                t.Check(!missile.Destroyed, "a certain miss leaves the missile flying");
                var waitSecond = new WaitUntil(() => dud.shots > 1 || missile.Destroyed, 400);
                yield return waitSecond;
                t.Require(dud.shots == 2, $"it fires again after its cooldown ({dud.shots})");
                int gap = Find.TickManager.TicksGame - start;
                t.Check(gap >= 285 && gap <= 330, $"the cooldown is respected ({gap} ticks, 300 set)");
                t.Check(missile.State.interceptAttempts >= 2, "the attempts are counted");
                t.Check(foreign.shots == 0, "an enemy weapon on the map does not shoot its own side's missile");
                missile.Destroy();
            }
            finally
            {
                Registry.Deregister(dud);
                Registry.Deregister(foreign);
            }
        }

        public static IEnumerable<Wait> BlockadeThroughEffector(TestContext t)
        {
            Site site = SpawnMissileBase(12f, 16f);
            t.Require(site != null, "an NPC missile base spawned 12–16 tiles away");
            yield return new WaitTicks(20);
            WorldObjectComp_AirDefense stock = site.GetComponent<WorldObjectComp_AirDefense>();
            t.Require(stock != null && stock.Available, "the base has abstract stock");
            int before = stock.Stock;
            TestPlainFlyer flyer = new TestPlainFlyer { def = DefDatabase<WorldObjectDef>.GetNamed("DMSE_DropPodTrack") };
            flyer.ID = Find.UniqueIDsManager.GetNextWorldObjectID();
            flyer.Tile = site.Tile;
            flyer.SetFaction(Player);
            Find.WorldObjects.Add(flyer);
            Track(flyer);
            var zone = new BlockadeZone { site = site, faction = site.Faction, center = MissileWorldUtility.TileCenter(site.Tile), radius = 10f, tile = site.Tile };
            var fire = typeof(WorldComponent_BlockadeInterception).GetMethod("TryFire", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            t.Require(fire != null, "the blockade firing method exists");
            var shot = fire.Invoke(null, new object[] { zone, flyer, MissileWorldUtility.TileCenter(site.Tile), Find.TickManager.TicksGame, new List<WorldObject_BlockadeInterceptor>() }) as WorldObject_BlockadeInterceptor;
            t.Require(shot != null, "the blockade fires");
            Track(shot);
            t.Check(stock.Stock == before - 1, $"the shot came from the node's stock ({before} to {stock.Stock})");
            t.Check(shot.target == flyer && shot.launchNode == site && shot.hitChance > 0f, $"it aims at the vehicle (chance {shot.hitChance:0.00})");
            t.Check(shot.interceptTick > Find.TickManager.TicksGame, "the flight time comes from the weapon's speed");
            shot.Destroy();
        }

        public static IEnumerable<Wait> FireControlWeapon(TestContext t)
        {
            Thing radar = SpawnBuilding(PhaseArray, 10, 10);
            yield return WaitPowered();
            yield return new WaitTicks(2);
            var gun = new TestEffector { source = radar, fireControl = true, angularSpeed = MissileWorldMotion.BaseAngularSpeed * 6f, cooldownTicks = 600 };
            Registry.Register(gun);
            try
            {
                BVRNode node = WorldComponent_BVRNetwork.Current.NodeOf(Home.Parent, Player);
                t.Require(node != null && node.HasInterceptorAmmo, "the registered weapon arms the node without any launcher");
                PlanetTile source = TileAtDistance(19f, 21f);
                t.Require(source.Valid, "a tile ~20 tiles away exists");
                WorldObject_IncomingMissile missile = LaunchEnemyMissile(source);
                bool sawLock = false;
                int channels = HomeBVR.Channels.Free(Player);
                bool heldChannel = false;
                var waitShot = new WaitUntil(() =>
                {
                    if (missile.Destroyed) { return true; }
                    sawLock |= missile.State.lockUntil >= 0;
                    heldChannel |= HomeBVR.Channels.Free(Player) < channels;
                    return missile.State.interceptor != null;
                }, missile.TicksToArrival);
                yield return waitShot;
                t.Require(!missile.Destroyed && missile.State.interceptor != null, "the weapon launches an interceptor");
                WorldObject_Interceptor shot = missile.State.interceptor;
                t.Check(sawLock, "fire control locked first");
                t.Check(heldChannel, "the lock held a channel");
                t.Check(gun.shots == 1, "the weapon fired once");
                t.Check(shot.usesChannel && shot.extraRolls == 0, "the interceptor uses a channel and has no splash");
                t.Check(shot.launchNode == Home.Parent && shot.Faction == Player && shot.target == missile, "it belongs to the node and aims at the missile");
                int free = HomeBVR.Channels.Free(Player);
                t.Check(free < channels, $"the channel stays held in flight ({free} of {channels})");
                shot.hitChance = 1f;
                var waitHit = new WaitUntil(() => missile.Destroyed, shot.interceptTick - Find.TickManager.TicksGame + 30);
                yield return waitHit;
                t.Check(missile.Destroyed, "the shot resolves like any interceptor");
            }
            finally { Registry.Deregister(gun); }
        }
    }
}
