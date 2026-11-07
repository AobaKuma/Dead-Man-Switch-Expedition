using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

static partial class Test
{
    class FakeEffector : IAirEffector
    {
        public Thing source;
        public bool fireControl = true, armed = true, canFire = true, canEngage = true;
        public int readyTick, fired;
        public Thing Source => source;
        public bool UsesFireControl => fireControl;
        public bool Armed => armed;
        public int ReadyTick => readyTick;
        public bool CanFire(int now) => canFire;
        public bool CanEngage(AirEngagement engagement) => canEngage;
        public AirEffectorShot PrepareShot(AirEngagement engagement) => new AirEffectorShot();
        public bool TryFire(AirEngagement engagement, int now, Vector3 meetingPoint) { fired++; return true; }
    }

    // Weapons reach the air defense node through one interface: launchers, abstract stock and third-party effectors alike.
    static void AirEffector()
    {
        var defender = Faction(false);
        var other = Faction(false);
        var map = new Map();
        var registry = new MapComponent_AirEffectors(map);
        map.components.Add(registry);
        var manager = new MapComponent_BVRCombat(map);
        var site = new MapParent { def = new WorldObjectDef { canHaveFaction = true } };
        site.SetFaction(defender);
        var node = new BVRNode { parent = site, faction = defender, local = manager };
        var engagement = new AirEngagement();

        Check(!node.Effectors.Any() && !node.HasInterceptorAmmo && !node.HasAutonomousEffector, "a map without weapons has no effectors");
        Check(node.ReadyEffector(0, engagement) == null, "no weapon, nothing ready");

        // Launchers are effectors; only the node's own faction counts.
        var sam = new ArmedLauncher { parent = Parent(defender), props = new CompProperties_MissileLauncher { interceptorSkyfaller = Empty<ThingDef>() } };
        var foreignSam = new ArmedLauncher { parent = Parent(other), props = new CompProperties_MissileLauncher { interceptorSkyfaller = Empty<ThingDef>() }, armed = true };
        manager.launchers.Add(sam);
        manager.launchers.Add(foreignSam);
        Check(node.Effectors.SequenceEqual(new IAirEffector[] { sam }), "the node lists its own faction's launcher only");
        Check(!node.HasInterceptorAmmo, "an inactive launcher does not arm the node");
        sam.armed = true;
        Check(node.HasInterceptorAmmo, "an active launcher arms the node");
        manager.launchers.Clear();

        // Registered effectors: faction filter, readiness and order.
        var idle = new FakeEffector { source = Parent(defender), readyTick = 10 };
        var busy = new FakeEffector { source = Parent(defender), readyTick = 500 };
        var enemy = new FakeEffector { source = Parent(other) };
        registry.Register(busy);
        registry.Register(idle);
        registry.Register(idle);
        registry.Register(enemy);
        Check(registry.Effectors.Count == 3, "registering twice keeps one entry");
        Check(node.Effectors.Count() == 2 && !node.Effectors.Contains(enemy), "another faction's effector is not the node's");
        Check(node.ReadyEffector(0, engagement) == idle, "the idlest weapon fires first");
        idle.canFire = false;
        Check(node.ReadyEffector(0, engagement) == busy, "a weapon that cannot fire is skipped");
        busy.canEngage = false;
        Check(node.ReadyEffector(0, engagement) == null, "a weapon that cannot reach the target is skipped");
        idle.canFire = true;
        busy.canEngage = true;

        // Point-defense weapons stay out of the fire-control route.
        registry.Deregister(idle);
        registry.Deregister(busy);
        var laser = new FakeEffector { source = Parent(defender), fireControl = false };
        registry.Register(laser);
        Check(node.HasAutonomousEffector, "a point-defense weapon is detected");
        Check(!node.HasInterceptorAmmo, "it does not count as interceptor ammo for locking");
        Check(node.ReadyEffector(0, engagement) == null, "the fire-control route does not pick it");
        registry.Deregister(laser);
        Check(!node.HasAutonomousEffector, "deregistering removes it");

        // Abstract stock is an effector with no thing behind it.
        var stock = new WorldObjectComp_AirDefense { parent = site,
            props = new CompProperties_WorldAirDefense { interceptorStock = 1, restockPerDay = 0, launchCooldownTicks = 0 } };
        var abstractNode = new BVRNode { parent = site, faction = defender, defense = new WorldAirDefenseAdapter(stock) };
        IAirEffector abstractWeapon = abstractNode.Effectors.Single();
        Check(abstractWeapon.Source == null && abstractWeapon.UsesFireControl, "abstract stock has no source and uses fire control");
        Check(ReferenceEquals(abstractNode.Effectors.Single(), abstractWeapon), "the node keeps one stock effector");
        Check(abstractWeapon.Armed && abstractNode.HasInterceptorAmmo, "stock arms the node");
        Check(abstractWeapon.TryFire(engagement, Find.TickManager.TicksGame, Vector3.zero), "firing consumes stock");
        Check(!abstractWeapon.Armed && !abstractWeapon.TryFire(engagement, Find.TickManager.TicksGame, Vector3.zero), "no stock, no shot");

        // Shot parameters: an interceptor without a config uses neutral factors.
        AirEffectorShot plain = AirDefenseHooks.ShotFrom(null);
        Check(plain.AngularSpeed > 0f && plain.Loadout == null && plain.ExtraRolls == 0, "no config still flies");
        Near(plain.HitFactor, 1f, "no config means no hit factor");
        Near(plain.HitBonus, 0f, "no config means no hit bonus");

        // Old saves have no stored roll count and fall back to the warhead.
        var saved = new WorldObject_Interceptor { extraRolls = 3 };
        Check(saved.ExtraRolls == 3, "a stored roll count wins");
        saved.extraRolls = -1;
        Check(saved.ExtraRolls == 0, "no count and no warhead means no rolls");
        Check(new WorldObject_Interceptor().usesChannel, "interceptors use a channel unless told otherwise");
    }
}
