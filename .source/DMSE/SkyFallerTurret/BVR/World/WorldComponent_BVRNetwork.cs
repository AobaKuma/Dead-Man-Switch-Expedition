using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>World interception scheduler. Durable state belongs to tracks/interceptors, never this cache.</summary>
    public class WorldComponent_BVRNetwork : WorldComponent
    {
        public static WorldComponent_BVRNetwork Current => Find.World?.GetComponent<WorldComponent_BVRNetwork>();
        private readonly List<BVRNode> nodes = new List<BVRNode>();
        private readonly List<WorldObject_Interceptor> pending = new List<WorldObject_Interceptor>();
        private readonly BVRWorldDrawer drawer = new BVRWorldDrawer();
        private bool rebuild = true;
        public bool ShowAllCoverage
        {
            get => DMSEMod.Settings?.showBVRWorldCoverage ?? false;
            set
            {
                if (DMSEMod.Settings == null) return;
                DMSEMod.Settings.showBVRWorldCoverage = value;
                DMSEMod.Settings.Write();
            }
        }
        public WorldComponent_BVRNetwork(World world) : base(world) { }
        public IEnumerable<MissileWorldObject> Tracks => Find.WorldObjects.AllWorldObjects.OfType<MissileWorldObject>()
            .Where(t => !t.Destroyed);
        public IEnumerable<MissileWorldObject> PlayerIncoming => Tracks.Where(t => t.State.detectedByFaction == Faction.OfPlayer
            && TargetFaction(t) == Faction.OfPlayer).OrderBy(t => t.TicksToArrival);
        public static Faction TargetFaction(MissileWorldObject track) => track.targetParent?.Faction
            ?? Find.WorldObjects.MapParentAt(track.Destination)?.Faction;
        /// <summary>
        /// Engagement order: soonest arrival first (stable, so ties keep creation order). Free channels go to
        /// the most urgent tracks, which makes decoy waves predictable (docs/BVR_波次打擊規劃.md §11).
        /// </summary>
        public static List<T> InEngagementOrder<T>(IEnumerable<T> tracks) where T : IBVRTrack
            => tracks.OrderBy(t => t.TicksToArrival).ToList();

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) rebuild = true;
        }
        public void AddFlash(Vector3 point) => drawer.AddFlash(point);
        public int CountEngaged(MapParent parent, Faction faction)
        {
            int locks = Tracks.Count(t => t.State.lockUntil >= 0 && t.State.lockNode == parent && t.State.lockFaction == faction);
            int flying = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>()
                .Count(i => !i.Destroyed && i.launchNode == parent && i.Faction == faction);
            return locks + flying;
        }
        private void RebuildNodes()
        {
            nodes.Clear();
            foreach (Map map in Find.Maps)
            {
                MapComponent_BVRCombat local = map.GetComponent<MapComponent_BVRCombat>();
                var factions = local.searchRadars.Select(r => r.parent.Faction)
                    .Concat(local.fireControlRadars.Select(r => r.parent.Faction)).Where(f => f != null).Distinct();
                foreach (Faction faction in factions)
                    nodes.Add(new BVRNode { parent = map.Parent, faction = faction, local = local });
            }
            foreach (MapParent parent in Find.WorldObjects.AllWorldObjects.OfType<MapParent>())
            {
                var defense = parent.GetComponent<WorldObjectComp_AirDefense>();
                if (defense?.Available == true)
                    nodes.Add(new BVRNode { parent = parent, faction = parent.Faction, abstractDefense = defense });
            }
        }
        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int now = Find.TickManager.TicksGame;
            if (rebuild)
            {
                pending.Clear();
                pending.AddRange(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>().Where(i => !i.Destroyed));
                pending.Sort((a, b) => a.interceptTick.CompareTo(b.interceptTick));
                rebuild = false;
            }
            // Sorted by due tick; only inspect the head each tick.
            while (pending.Count > 0 && (pending[0].Destroyed || pending[0].interceptTick <= now))
            {
                var interceptor = pending[0];
                pending.RemoveAt(0);
                if (!interceptor.Destroyed) Resolve(interceptor);
            }
            if (now % Mathf.Max(1, BVRWorldTuningDef.Current.scanIntervalTicks) != 0) return;
            RebuildNodes();
            foreach (MissileWorldObject track in InEngagementOrder(Tracks)) Scan(track, now);
        }
        private void Scan(MissileWorldObject track, int now)
        {
            BVRTrackState state = track.State;
            if (state.lastKnownDestination != track.Destination)
            {
                state.ClearLock();
                state.lastKnownDestination = track.Destination;
            }
            BVRNode node = nodes.Where(n => n.Tile == track.Destination && n.faction.HostileTo(track.Attacker))
                .OrderByDescending(n => n.faction.IsPlayer).FirstOrDefault();
            if (node == null)
            {
                state.ClearLock();
                state.detectedByFaction = null;
                return;
            }
            float range = node.DetectionRange(track.TargetProps);
            if (range <= 0) state.detectedByFaction = null;
            else if (MissileWorldUtility.SurfaceTilesBetween(node.Center, track.PositionAt(0)) <= range)
            {
                state.detectedByFaction = node.faction;
                if (state.firstDetectedTick < 0) state.firstDetectedTick = now;
                if (node.faction.IsPlayer && !state.playerNotified)
                {
                    state.playerNotified = true;
                    Find.LetterStack.ReceiveLetter("DMSE.BVR.World.DetectedLetter".Translate(),
                        "DMSE.BVR.World.DetectedLetterText".Translate(track.Label, node.parent.Label,
                            track.TicksToArrival.ToStringTicksToPeriod()), LetterDefOf.ThreatBig,
                        new GlobalTargetInfo(track));
                }
            }
            if (state.interceptor != null && !state.interceptor.Destroyed) return;
            state.interceptor = null;
            if (state.lockUntil >= 0 && (state.lockNode != node.parent || state.lockFaction != node.faction)) state.ClearLock();
            int eta = track.TicksToArrival;
            var target = new BVRTarget { stealthLevel = track.TargetProps.stealthLevel, speed = track.TargetProps.speed };
            CompFireControlRadar radar = node.BestRadar(target, eta);
            CompProperties_FireControlRadar settings = radar?.Props ?? node.abstractDefense?.FireControl;
            if (settings == null || eta > settings.maxRangeTicks)
            {
                state.ClearLock();
                return;
            }
            if (state.lockUntil < 0)
            {
                if (state.detectedByFaction != node.faction || node.FreeChannels <= 0) return;
                state.lockUntil = now + CompFireControlRadar.LockOnTicksFor(settings, target);
                state.lockNode = node.parent;
                state.lockFaction = node.faction;
                state.lockNodeTile = node.Tile;
                return;
            }
            if (state.lockUntil > now) return;
            var launcher = node.ReadyLauncher(now);
            var defense = node.abstractDefense;
            if (launcher == null && (defense == null || !defense.Ready(now))) return;
            MissileConfig config = launcher?.GetLoadedMissileConfig() ?? defense?.Config;
            float speed = MissileWorldMotion.AngularSpeed(config) * BVRWorldTuningDef.Current.interceptorSpeedMultiplier;
            if (!BVRInterceptSolver.TrySolve(track, node.Center, speed, out int ticks, out Vector3 point))
            {
                state.ClearLock();
                return;
            }
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_Interceptor");
            if (def == null) { state.ClearLock(); return; }
            var warhead = config?.PartFor(MissilePartCategory.Warhead);
            float payload = config?.PayloadCapacity ?? 0;
            float chance = Mathf.Clamp01(CompFireControlRadar.ComputeHitChance(settings, target, eta)
                + (warhead?.warheadEffect?.InterceptBonus(payload) ?? 0));
            if (launcher != null ? !launcher.FireWorldInterceptor(now) : !defense.TryConsume(now)) return;
            var interceptor = (WorldObject_Interceptor)WorldObjectMaker.MakeWorldObject(def);
            interceptor.Tile = node.Tile;
            interceptor.SetFaction(node.faction);
            interceptor.launchNode = node.parent;
            interceptor.launchTick = now;
            interceptor.interceptTick = now + ticks;
            interceptor.start = node.Center;
            interceptor.end = point;
            interceptor.target = track;
            interceptor.targetDestination = track.Destination;
            interceptor.targetRevision = track.RouteRevision;
            interceptor.hitChance = chance;
            interceptor.warhead = warhead;
            interceptor.payloadCapacity = payload;
            Find.WorldObjects.Add(interceptor);
            pending.Add(interceptor);
            pending.Sort((a, b) => a.interceptTick.CompareTo(b.interceptTick));
            state.ClearLock();
            state.interceptor = interceptor;
            state.interceptAttempts++;
        }
        private void Resolve(WorldObject_Interceptor interceptor)
        {
            MissileWorldObject target = interceptor.target;
            bool valid = target != null && !target.Destroyed && target.TicksToArrival > 0
                && target.Destination == interceptor.targetDestination && target.RouteRevision == interceptor.targetRevision;
            if (valid)
            {
                if (Rand.Chance(interceptor.hitChance)) target.Notify_Intercepted(interceptor.Faction);
                else if (interceptor.Faction == Faction.OfPlayer || target.Attacker == Faction.OfPlayer)
                    Messages.Message("DMSE.BVR.World.Missed".Translate(target.Label), new GlobalTargetInfo(target),
                        MessageTypeDefOf.NeutralEvent, historical: false);
                int rolls = interceptor.warhead?.warheadEffect?.ExtraInterceptRolls(interceptor.payloadCapacity) ?? 0;
                if (rolls > 0)
                {
                    foreach (var other in Tracks.Where(t => t != target && t.Destination == interceptor.targetDestination
                        && interceptor.Faction.HostileTo(t.Attacker)
                        && MissileWorldUtility.SurfaceTilesBetween(t.PositionAt(0), interceptor.end) <= BVRWorldTuningDef.Current.airburstRadiusTiles).ToList())
                    {
                        if (rolls-- <= 0) break;
                        if (Rand.Chance(interceptor.hitChance)) other.Notify_Intercepted(interceptor.Faction);
                    }
                }
                drawer.AddFlash(interceptor.end);
            }
            else if (target != null && !target.Destroyed && (interceptor.Faction == Faction.OfPlayer || target.Attacker == Faction.OfPlayer))
                Messages.Message("DMSE.BVR.World.Evaded".Translate(target.Label), new GlobalTargetInfo(target),
                    MessageTypeDefOf.NeutralEvent, historical: false);
            if (target != null && target.State.interceptor == interceptor) target.State.interceptor = null;
            interceptor.Destroy();
        }
        public override void WorldComponentUpdate()
        {
            base.WorldComponentUpdate();
            if (WorldRendererUtility.WorldSelected) drawer.Draw(nodes, ShowAllCoverage);
        }
    }
}
