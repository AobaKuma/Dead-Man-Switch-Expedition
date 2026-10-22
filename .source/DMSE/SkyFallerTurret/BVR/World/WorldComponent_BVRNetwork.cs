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

        // ---- 移動目標登錄（反艦）----
        private const int MotionSampleIntervalTicks = 60;
        private const int MotionMemoryTicks = 900;
        private struct MotionSample
        {
            public PlanetTile tile;
            public int tick;
            public int lastSeen;
            public float tilesPerTick;
        }
        private readonly Dictionary<int, MotionSample> motion = new Dictionary<int, MotionSample>();
        private int lastMotionSampleTick = -1;
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
        /// <summary>
        /// With threat assessment (P4) a node engages the largest signature first; <paramref name="priority"/> returns
        /// that signature for tracks heading to such a node and 0 otherwise. Ties fall back to soonest arrival.
        /// </summary>
        public static List<T> InEngagementOrder<T>(IEnumerable<T> tracks, System.Func<T, float> priority) where T : IBVRTrack
            => tracks.OrderByDescending(priority).ThenBy(t => t.TicksToArrival).ToList();
        /// <summary>
        /// 接戰節點：目的地節點優先（點防禦）；否則由最近的「看得到或航跡飛越其覆蓋範圍」的敵對節點接手
        /// （P4 區域防禦）。
        /// </summary>
        private BVRNode NodeFor(MissileWorldObject track)
        {
            BVRNode best = null;
            float bestDistance = float.MaxValue;
            foreach (BVRNode n in nodes)
            {
                if (n.parent == null || n.faction == null || !n.faction.HostileTo(track.Attacker)) { continue; }
                if (!n.CanEngage(track)) { continue; }
                float distance = MissileWorldUtility.SurfaceTilesBetween(n.Center, track.PositionAt(0));
                bool better = best == null
                    || (n.DestinationIs(track) && !best.DestinationIs(track))
                    || (n.DestinationIs(track) == best.DestinationIs(track)
                        && (distance < bestDistance - 0.01f
                            || (Mathf.Abs(distance - bestDistance) <= 0.01f && n.faction.IsPlayer && !best.faction.IsPlayer)));
                if (better) { best = n; bestDistance = distance; }
            }
            return best;
        }

        /// <summary>
        /// 數據鏈（P4）：任何節點看到航跡都算「該陣營已偵測」，不要求目的地節點自己看到。
        /// 玩家優先，其餘取最近的探測者。
        /// </summary>
        private BVRNode DetectorFor(MissileWorldObject track)
        {
            BVRNode best = null;
            float bestDistance = float.MaxValue;
            foreach (BVRNode n in nodes)
            {
                if (n.parent == null || n.faction == null || !n.faction.HostileTo(track.Attacker)) { continue; }
                if (!n.CanSee(track)) { continue; }
                float distance = MissileWorldUtility.SurfaceTilesBetween(n.Center, track.PositionAt(0));
                bool better = best == null
                    || (n.faction.IsPlayer && !best.faction.IsPlayer)
                    || (n.faction.IsPlayer == best.faction.IsPlayer && distance < bestDistance);
                if (better) { best = n; bestDistance = distance; }
            }
            return best;
        }

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
                if (parent == null || parent.Destroyed) { continue; }
                WorldObjectComp_AirDefense comp = parent.GetComponent<WorldObjectComp_AirDefense>();
                if (comp?.Available == true)
                {
                    nodes.Add(new BVRNode { parent = parent, faction = parent.Faction, defense = new WorldAirDefenseAdapter(comp) });
                    continue;
                }
                // 一般據點自動防空（P4）：敵方、無地圖、沒有專屬防空元件的據點，依科技等級合成一套。
                IBVRDefense synthetic = WorldComponent_SettlementDefense.Current?.DefenseFor(parent);
                if (synthetic != null && synthetic.Available)
                {
                    nodes.Add(new BVRNode { parent = parent, faction = parent.Faction, defense = synthetic });
                }
            }
        }
        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int now = Find.TickManager.TicksGame;
            SampleMovement(now);
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
            foreach (MissileWorldObject track in InEngagementOrder(Tracks,
                t => NodeFor(t)?.ThreatAssessment == true ? t.TargetProps.distanceLevel : 0f)) Scan(track, now);
        }
        private void Scan(MissileWorldObject track, int now)
        {
            BVRTrackState state = track.State;
            // 箔條成功斷鎖後的延遲期：這段時間內不重新鎖定（等於白吃一次反應時間）。
            if (now < state.lockDelayUntil) { return; }
            if (state.lastKnownDestination != track.Destination)
            {
                state.ClearLock();
                state.lastKnownDestination = track.Destination;
            }
            // 數據鏈：任何敵對節點看到就算偵測到，不要求「目的地節點」自己看到。
            BVRNode detector = DetectorFor(track);
            if (detector == null)
            {
                state.ClearLock();
                state.detectedByFaction = null;
                return;
            }
            state.detectedByFaction = detector.faction;
            if (state.firstDetectedTick < 0) { state.firstDetectedTick = now; }
            if (detector.faction.IsPlayer && !state.playerNotified)
            {
                state.playerNotified = true;
                Find.LetterStack.ReceiveLetter("DMSE.BVR.World.DetectedLetter".Translate(),
                    "DMSE.BVR.World.DetectedLetterText".Translate(track.Label, detector.parent.Label,
                        track.TicksToArrival.ToStringTicksToPeriod()), LetterDefOf.ThreatBig,
                    new GlobalTargetInfo(track));
            }
            // 已被鎖定時，箔條／干擾絲有機會打斷這次鎖定。
            TryExpendChaff(track, now);
            if (state.interceptor != null && !state.interceptor.Destroyed) return;
            state.interceptor = null;

            // 區域防禦：目的地節點優先，否則由最近的可用節點接手。
            BVRNode node = NodeFor(track);
            if (node == null)
            {
                state.ClearLock();
                return;
            }
            // 資料鏈：本節點自己看不到時，只要同陣營已偵測到仍可接戰。
            if (!node.CanSee(track) && state.detectedByFaction != node.faction) { state.ClearLock(); return; }
            if (state.lockUntil >= 0 && (state.lockNode != node.parent || state.lockFaction != node.faction)) state.ClearLock();
            int eta = track.TicksToArrival;
            var target = new BVRTarget { stealthLevel = track.TargetProps.stealthLevel, speed = track.TargetProps.speed };
            CompFireControlRadar radar = node.BestRadar(target, eta);
            CompProperties_FireControlRadar settings = radar?.Props ?? node.DefenseFireControl;
            if (settings == null || eta > settings.maxRangeTicks)
            {
                state.ClearLock();
                return;
            }
            // No interceptor that can fire now or after reloading: do not lock, and drop any lock (frees the channel).
            bool armed = node.HasInterceptorAmmo;
            if (state.lockUntil < 0)
            {
                if (!armed || node.FreeChannels <= 0) return;
                state.lockUntil = now + CompFireControlRadar.LockOnTicksFor(settings, target);
                state.lockNode = node.parent;
                state.lockFaction = node.faction;
                state.lockNodeTile = node.Tile;
                return;
            }
            if (!armed)
            {
                state.ClearLock();
                return;
            }
            if (state.lockUntil > now) return;
            var launcher = node.ReadyLauncher(now);
            if (launcher == null && !node.DefenseReady(now)) return;
            MissileConfig config = launcher?.GetLoadedMissileConfig() ?? node.DefenseConfig;
            float speed = MissileWorldMotion.AngularSpeed(config) * BVRWorldTuningDef.Current.interceptorSpeedMultiplier;
            bool area = !node.DestinationIs(track);
            int ticks;
            Vector3 point;
            bool solved = area
                ? BVRInterceptSolver.TrySolveGeneral(track, node.Center, speed, out ticks, out point)
                : BVRInterceptSolver.TrySolve(track, node.Center, speed, out ticks, out point);
            if (!solved)
            {
                state.ClearLock();
                return;
            }
            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_Interceptor");
            if (def == null) { state.ClearLock(); return; }
            var warhead = config?.PartFor(MissilePartCategory.Warhead);
            float payload = config?.PayloadCapacity ?? 0;
            float chance = Mathf.Clamp01(CompFireControlRadar.ComputeHitChance(settings, target, eta)
                * (config?.InterceptHitFactor ?? 1f)
                + (warhead?.warheadEffect?.InterceptBonus(payload) ?? 0));
            if (launcher != null ? !launcher.FireWorldInterceptor(now, MissileProjectileMaker.WorldInterceptBearing(node.Tile, point)) : !node.TryConsumeDefense(now)) return;
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
        /// <summary>
        /// P4 空投虛擬航跡：把地圖波次中的空投也表示成一條世界層航跡，讓世界雷達與攔截彈能提前接戰。
        /// 由 Tuning 的 <see cref="BVRWorldTuningDef.podsUseWorldTracks"/> 開啟；導彈本來就有自己的航跡，不重複建立。
        /// </summary>
        public void TrySpawnDropPodTrack(Map map, BVRWave wave, BVRTarget target, Thing faller)
        {
            if (!BVRWorldTuningDef.Current.podsUseWorldTracks) { return; }
            if (map == null || wave == null || target == null || faller == null) { return; }
            if (faller is IIncomingMissile || !(faller is Skyfaller)) { return; }
            Faction defender = wave.defenderFaction;
            if (defender == null) { return; }

            if (Find.WorldObjects.AllWorldObjects.OfType<WorldObject_DropPodTrack>()
                .Any(t => !t.Destroyed && t.targetMap == map && t.waveTargetId == target.id)) { return; }

            if (nodes.Count == 0) { RebuildNodes(); }
            BVRNode node = nodes
                .Where(n => n.faction == defender && n.DetectionRange(BVRTargetProps.Default) > 0f)
                .OrderByDescending(n => n.faction.IsPlayer)
                .FirstOrDefault();
            if (node == null) { return; }

            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_DropPodTrack");
            if (def == null) { return; }

            int now = Find.TickManager.TicksGame;
            Vector3 end = MissileWorldUtility.TileCenter(map.Tile);
            Vector3 center = node.Center;
            float angle = GenMath.SphericalDistance(center.normalized, end.normalized);
            float range = node.DetectionRange(BVRTargetProps.Default);
            if (angle <= 0.000001f || range <= 0f) { return; }

            float tilesPerRadian = Mathf.Max(1f, Find.WorldGrid.ApproxDistanceInTiles(1f));
            float radiusAngle = range / tilesPerRadian;
            float ratio = Mathf.Clamp(radiusAngle / angle, 0.05f, 0.9f);
            Vector3 start = Vector3.Slerp(end, center, ratio);

            WorldObject_DropPodTrack ghost = (WorldObject_DropPodTrack)WorldObjectMaker.MakeWorldObject(def);
            ghost.Tile = map.Tile;
            ghost.destinationTile = map.Tile;
            ghost.SetFaction(node.faction);
            ghost.spawnTick = now;
            ghost.arrivalTick = Mathf.Max(now + 60, wave.tickToImpact);
            ghost.startPos = start;
            ghost.endPos = end;
            ghost.targetTile = map.Tile;
            ghost.targetMap = map;
            ghost.waveTargetId = target.id;
            ghost.incomingDef = faller.def;
            Find.WorldObjects.Add(ghost);

            // 世界層接手後，地圖內只保留末端 CIWS：中過程改在世界層接戰。
            target.terminalOnly = true;
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
        /// <summary>
        /// 每 60 ticks 取樣所有 MapParent 的 tile，推導移動速度。
        /// 只觀測、不 patch 原版，因此轉移艦、會搬遷的 NPC 陣地與其他 mod 的移動 MapParent 都涵蓋。
        /// </summary>
        private void SampleMovement(int now)
        {
            if (lastMotionSampleTick >= 0 && now - lastMotionSampleTick < MotionSampleIntervalTicks) { return; }
            int elapsed = lastMotionSampleTick < 0 ? MotionSampleIntervalTicks : Mathf.Max(1, now - lastMotionSampleTick);
            lastMotionSampleTick = now;

            foreach (MapParent parent in Find.WorldObjects.AllWorldObjects.OfType<MapParent>())
            {
                if (parent == null || parent.Destroyed) { continue; }
                int id = parent.ID;
                motion.TryGetValue(id, out MotionSample sample);
                if (sample.tile.Valid && parent.Tile.Valid && sample.tile != parent.Tile)
                {
                    float tiles = MissileWorldUtility.SurfaceTilesBetween(
                        MissileWorldUtility.TileCenter(sample.tile), MissileWorldUtility.TileCenter(parent.Tile));
                    sample.tilesPerTick = tiles / elapsed;
                    sample.tick = now;
                }
                sample.tile = parent.Tile;
                sample.lastSeen = now;
                motion[id] = sample;
            }

            // 清掉已消失的據點，避免字典隨長局無限成長。
            if (motion.Count > 0)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, MotionSample> pair in motion)
                {
                    if (now - pair.Value.lastSeen > MotionMemoryTicks)
                    {
                        stale ??= new List<int>();
                        stale.Add(pair.Key);
                    }
                }
                if (stale != null)
                {
                    foreach (int id in stale) { motion.Remove(id); }
                }
            }
        }

        /// <summary>該據點是否正在移動（最近 <see cref="MotionMemoryTicks"/> 內觀測到 tile 變化）。</summary>
        public bool IsMobileTarget(MapParent parent)
        {
            if (parent == null) { return false; }
            if (!motion.TryGetValue(parent.ID, out MotionSample sample)) { return false; }
            return sample.tilesPerTick > 0.00001f
                && Find.TickManager.TicksGame - sample.tick <= MotionMemoryTicks;
        }

        /// <summary>移動目標的觀測速度（地表格／tick）；靜止或未知為 0。</summary>
        public float MobileTilesPerTick(MapParent parent)
            => parent != null && motion.TryGetValue(parent.ID, out MotionSample sample) ? sample.tilesPerTick : 0f;

        /// <summary>
        /// 箔條／干擾絲：只在「已被火控鎖定」時觸發，成功則斷鎖並延後重新鎖定；
        /// 不論成敗都消耗一次，次數用完就不再作用（飛行中不補充）。
        /// </summary>
        private static void TryExpendChaff(MissileWorldObject track, int now)
        {
            BVRTrackState state = track.State;
            if (state.chaffLeft == 0) { return; }
            if (state.chaffLeft < 0)
            {
                state.chaffLeft = track.Config?.ChaffCharges ?? 0;
                if (state.chaffLeft <= 0) { return; }
            }
            if (state.lockUntil < 0 || state.lockNode == null || state.lockFaction == null) { return; }
            float jam = track.Config?.JamStrength ?? 0f;
            if (jam <= 0f) { return; }

            state.chaffLeft--;
            state.jamCount++;
            float chance = Mathf.Clamp01(BVRWorldTuningDef.Current.chaffBreakChance * jam);
            if (!Rand.Chance(chance)) { return; }

            state.ClearLock();
            state.lockDelayUntil = now + BVRWorldTuningDef.Current.chaffLockDelayTicks;
            if (track.Attacker == Faction.OfPlayer || state.detectedByFaction == Faction.OfPlayer)
            {
                Messages.Message("DMSE.BVR.World.Chaff".Translate(track.Label), new GlobalTargetInfo(track),
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        public override void WorldComponentUpdate()
        {
            base.WorldComponentUpdate();
            if (WorldRendererUtility.WorldSelected) drawer.Draw(nodes, ShowAllCoverage);
        }
    }
}
