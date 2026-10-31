using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Grammar;

// Real production classes and real 1.7 reference assemblies. The minimal fixture avoids booting a game/render loop.
static partial class Test
{
    static int count;
    static string section;
    static readonly List<string> failures = new List<string>();
    // A failed check is recorded and the section goes on, so one stale expectation does not hide the rest.
    static void Check(bool condition, string name)
    {
        if (!condition) { failures.Add(section + ": " + name); return; }
        count++;
    }
    // Runs one section; an exception fails that section only.
    static void Run(string name, Action test)
    {
        section = name;
        try { test(); }
        catch (Exception e) { failures.Add(name + ": threw " + e.GetType().Name + ": " + e.Message); }
    }
    // Writes with the real Scribe saver and returns the saved root element.
    static XElement SaveXml(string root, Action expose)
    {
        string path = Path.Combine(Path.GetTempPath(), "dmse-bvr-" + Guid.NewGuid() + ".xml");
        Scribe.saver.InitSaving(path, root);
        expose();
        Scribe.saver.FinalizeSaving();
        try { return XDocument.Load(path).Root; }
        finally { File.Delete(path); }
    }
    static void Near(float actual, float expected, string name, float tolerance = 0.001f)
        => Check(Math.Abs(actual - expected) <= tolerance, name + $" ({actual} != {expected})");
    static void Field(object obj, string name, object value)
    {
        for (Type type = obj.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field != null) { field.SetValue(obj, value); return; }
        }
        throw new Exception("Fixture field missing: " + name);
    }
    static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static TickManager clock;
    static World world;
    static List<WorldObject> objects;
    static void Tick(int now) => Field(clock, "ticksGameInt", now);
    static Faction Faction(bool player) => new Faction { def = new FactionDef { isPlayer = player } };
    static ThingWithComps Parent(Faction faction)
    {
        var parent = new ThingWithComps { def = Empty<ThingDef>() };
        Field(parent, "factionInt", faction);
        return parent;
    }
    static void Setup()
    {
        LanguageDatabase.activeLanguage = new LoadedLanguage("English");
        Field(LanguageDatabase.activeLanguage, "workerInt", new LanguageWorker_English());
        Field(LanguageDatabase.activeLanguage, "dataIsLoaded", true);
        foreach (string file in Directory.GetFiles("1.7/Languages/English/Keyed", "*.xml"))
        {
            foreach (XElement entry in XDocument.Load(file).Root.Elements())
            {
                string key = entry.Name.LocalName;
                LanguageDatabase.activeLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = entry.Value };
            }
        }
        LanguageDatabase.activeLanguage.keyedReplacements["PeriodSeconds"] =
            new LoadedLanguage.KeyedReplacement { key = "PeriodSeconds", value = "{0} seconds" };
        var game = Empty<Game>();
        Current.Game = game;
        clock = Empty<TickManager>();
        game.tickManager = clock;
        world = Empty<World>();
        Field(game, "worldInt", world);
        Field(game, "maps", new List<Map>());
        world.worldObjects = new WorldObjectsHolder();
        objects = new List<WorldObject>();
        Field(world.worldObjects, "worldObjects", objects);
        world.components = new List<WorldComponent>();
        world.factionManager = new FactionManager();
        world.grid = new WorldGrid();
        var surface = Empty<SurfaceLayer>();
        Field(surface, "averageTileSize", 0.5f);
        Field(surface, "radius", 100f);
        Field(world.grid, "surface", surface);
        var network = new WorldComponent_BVRNetwork(world);
        world.components.Add(network);
        Tick(1000);
    }

    static int nextThingId = 100;
    // Unique ids as in a real game: Thing.Equals compares id and def.
    static ThingWithComps Round(ThingDef def) => new ThingWithComps { def = def, thingIDNumber = nextThingId++ };
    static ThingDef RoleDef(MissileRole? role)
    {
        var def = Empty<ThingDef>();
        Field(def, "stackLimit", 1);
        if (role.HasValue) def.modExtensions = new List<DefModExtension> { new MissileRoleExtension { role = role.Value } };
        return def;
    }

    class ManualLauncher : CompMissileLauncher_WorldTargeting
    {
        public bool online = true, ammo = true;
        public MissileConfig config;
        public override bool Active => online;
        public override bool HasAmmo => ammo;
        protected override int LaunchCooldownTicks => 60;
        protected override string LoadedStatusText => "loaded";
        protected override ThingDef LaunchSkyfallerDef => parent.def;
        protected override ThingDef IncomingSkyfallerDef => parent.def;
        protected override WorldObjectDef TravelWorldObjectDef => new WorldObjectDef();
        public override IEnumerable<Thing> StrikeRounds => ammo ? new[] { parent } : Enumerable.Empty<Thing>();
        public override MissileConfig BuildFiringConfig(Thing round) => config;
        protected override void ConsumeAmmo(Thing round) { ammo = false; }
        protected override string GizmoLabel => "launch";
        protected override string GizmoDesc => "launch";
        protected override string NoAmmoDisableReason => "no ammo";
    }
    class OnlineRadar : CompFireControlRadar { public override bool Active => true; }
    class ArmedLauncher : CompMissileLauncher_Interceptor { public bool armed; public override bool Active => armed; }
    class OnlineCIWS : CompTerminalCIWS { public bool online = true; public override bool Active => online; }
    // A bare world track: subclasses only define how it moves.
    abstract class TrackBase : IBVRTrack
    {
        public WorldObject Self => null;
        public Faction Attacker => null;
        public PlanetTile Origin => PlanetTile.Invalid;
        public PlanetTile Destination => PlanetTile.Invalid;
        public BVRTargetProps TargetProps => BVRTargetProps.Default;
        public MissileConfig Config => null;
        public abstract float AngularSpeed { get; }
        public abstract int TicksToArrival { get; }
        public abstract Vector3 PositionAt(int ticksAhead);
        public BVRTrackState State { get; } = new BVRTrackState();
        public void Notify_Intercepted(Faction by) { }
    }
    class FakeTrack : TrackBase
    {
        public float angle, speed;
        public override float AngularSpeed => speed;
        public override int TicksToArrival => (int)Math.Ceiling(angle / speed);
        public override Vector3 PositionAt(int ticksAhead)
        {
            float a = Math.Min(angle, speed * ticksAhead);
            return new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 100;
        }
    }
    class LineTrack : TrackBase
    {
        public Vector3 a, b;
        public int eta = 1000;
        public override float AngularSpeed => GenMath.SphericalDistance(a.normalized, b.normalized) / Mathf.Max(1, eta);
        public override int TicksToArrival => eta;
        public override Vector3 PositionAt(int ticksAhead)
            => Vector3.Slerp(a, b, Mathf.Clamp01(ticksAhead / (float)Mathf.Max(1, eta)));
    }
}
