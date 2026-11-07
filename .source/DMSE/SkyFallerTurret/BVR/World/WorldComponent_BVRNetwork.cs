using System.Collections.Generic;
using System.Linq;
using Fortified;
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
        private readonly List<BVRNode> autonomousNodes = new List<BVRNode>();
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

        // ---- 航跡來源 ----
        // 內部航跡（飛彈、空投幽靈）自己保存狀態。外部航跡只實作 IAirTrack，或由 AirTrackProvider 提供；
        // 它們的狀態存在這裡，包裝物件只是快取。
        private Dictionary<int, BVRTrackState> externalStates = new Dictionary<int, BVRTrackState>();
        private List<int> externalStateKeys;
        private List<BVRTrackState> externalStateValues;
        private readonly Dictionary<int, BVRExternalTrack> externalTracks = new Dictionary<int, BVRExternalTrack>();

        /// <summary>所有航跡：飛彈、空投幽靈、實作 <see cref="IAirTrack"/> 的世界物件與 <see cref="AirTrackProvider"/> 提供的物件。</summary>
        public IEnumerable<IBVRTrack> Tracks
        {
            get
            {
                List<WorldObject> objects = Find.WorldObjects.AllWorldObjects;
                for (int i = 0; i < objects.Count; i++)
                {
                    WorldObject o = objects[i];
                    if (o.Destroyed) { continue; }
                    if (o is IBVRTrack own) { yield return own; }
                    else if (o is IAirTrack air) { yield return ExternalFor(air); }
                }
                List<IAirTrack> provided = AirDefenseExtensions.CollectProvidedTracks();
                for (int i = 0; i < provided.Count; i++)
                {
                    WorldObject self = provided[i]?.Self;
                    if (self == null || self.Destroyed || self is IAirTrack) { continue; }
                    yield return ExternalFor(provided[i]);
                }
            }
        }

        /// <summary>只有飛彈：導引干擾與玩家飛彈警示只對它們有意義。</summary>
        public IEnumerable<MissileWorldObject> MissileTracks => Find.WorldObjects.AllWorldObjects.OfType<MissileWorldObject>()
            .Where(t => !t.Destroyed);
        public IEnumerable<IBVRTrack> PlayerIncoming => Tracks.Where(t => t.State.detectedByFaction == Faction.OfPlayer
            && TargetFaction(t) == Faction.OfPlayer).OrderBy(t => t.TicksToArrival);
        public static Faction TargetFaction(IBVRTrack track) => track.TargetParent?.Faction
            ?? Find.WorldObjects.MapParentAt(track.Destination)?.Faction;

        private BVRExternalTrack ExternalFor(IAirTrack track)
        {
            int id = track.Self.ID;
            if (externalTracks.TryGetValue(id, out BVRExternalTrack known))
            {
                known.Inner = track;
                return known;
            }
            if (!externalStates.TryGetValue(id, out BVRTrackState state))
            {
                state = new BVRTrackState();
                externalStates[id] = state;
            }
            var created = new BVRExternalTrack(track, state);
            externalTracks[id] = created;
            return created;
        }

        /// <summary>世界物件對應的航跡；不是航跡時回傳 null。</summary>
        public IBVRTrack TrackOf(WorldObject worldObject)
        {
            if (worldObject == null) { return null; }
            if (worldObject is IBVRTrack own) { return own; }
            if (worldObject is IAirTrack air) { return worldObject.Destroyed ? externalTracks.GetValueOrDefault(worldObject.ID) : ExternalFor(air); }
            if (externalTracks.TryGetValue(worldObject.ID, out BVRExternalTrack known)) { return known; }
            // 剛讀檔還沒掃描過：來源提供的航跡要問過來源才知道。
            return Tracks.FirstOrDefault(t => t.Self == worldObject);
        }

        /// <summary>丟掉已不存在或來源不再提供的外部航跡。</summary>
        private void PruneExternal(List<IBVRTrack> live)
        {
            if (externalStates.Count == 0 && externalTracks.Count == 0) { return; }
            HashSet<int> ids = new HashSet<int>();
            foreach (IBVRTrack track in live)
            {
                if (track is BVRExternalTrack) { ids.Add(track.Self.ID); }
            }
            foreach (int id in externalStates.Keys.Where(k => !ids.Contains(k)).ToList()) { externalStates.Remove(id); }
            foreach (int id in externalTracks.Keys.Where(k => !ids.Contains(k)).ToList()) { externalTracks.Remove(id); }
        }
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
        /// 接戰節點：目的地節點優先（點防禦）；否則由最近的「看得到或航跡飛越其覆蓋範圍」的節點接手
        /// （P4 區域防禦）。NPC 節點依警戒階段政策篩選，且本陣營必須在 <paramref name="intelSince"/> 之後
        /// 實際看到這條航跡（本地或同陣營資料鏈），避免先選最近節點後才發現沒有情報而放棄其他可用節點。
        /// </summary>
        private BVRNode NodeFor(IBVRTrack track, int intelSince, bool checkAltitude = true)
        {
            BVRNode best = null;
            float altitude = track.AltitudeAt(0);
            float bestDistance = float.MaxValue;
            foreach (BVRNode n in nodes)
            {
                if (n.parent == null || n.faction == null || !n.PolicyAllows(track)) { continue; }
                if (intelSince != int.MinValue && !track.State.FactionSees(n.faction, intelSince)) { continue; }
                if (!n.CanEngage(track)) { continue; }
                if (checkAltitude && !n.CanReachAltitude(altitude)) { continue; }
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
        /// 每個陣營各自記錄情報（<see cref="AirDefenseObservation"/>），NPC 節點同時把觀測交給警戒系統入帳。
        /// 回傳玩家介面用的代表探測者：玩家優先，其餘取最近的探測者。
        /// </summary>
        private BVRNode Observe(IBVRTrack track, int now)
        {
            BVRNode best = null;
            float bestDistance = float.MaxValue;
            WorldComponent_FactionAirDefenseAlert alert = WorldComponent_FactionAirDefenseAlert.Current;
            MapParent targetSite = null;
            bool targetResolved = false;
            foreach (BVRNode n in nodes)
            {
                if (n.parent == null || n.faction == null || !n.MayObserve(track)) { continue; }
                if (!n.CanSee(track)) { continue; }
                if (!targetResolved)
                {
                    targetSite = WorldComponent_FactionAirDefenseAlert.TargetSite(track);
                    targetResolved = true;
                }
                AirDefenseObservation observation = track.State.ObservationOf(n.faction, create: true);
                bool firstSight = observation.firstSeenTick < 0;
                if (firstSight) { observation.firstSeenTick = now; }
                observation.lastSeenTick = now;
                observation.lastPosition = track.PositionAt(0);
                observation.knownDestination = track.Destination;
                observation.knownTargetFaction = targetSite?.Faction;
                if (n.UsesAlertRules) { alert?.ReportObservation(n, track, observation, now); }
                if (firstSight) { AirDefenseHooks.WorldEvent(AirCombatEventKind.TrackDetected, track, n.faction, n.parent, n.local?.map); }
                float distance = MissileWorldUtility.SurfaceTilesBetween(n.Center, track.PositionAt(0));
                // 代表探測者：玩家優先，其次是被瞄準的陣營（NPC 也會觀測中立過境，避免由旁觀者代表），再取最近。
                int rank = n.faction.IsPlayer ? 2 : n.faction == targetSite?.Faction ? 1 : 0;
                int bestRank = best == null ? -1 : best.faction.IsPlayer ? 2 : best.faction == targetSite?.Faction ? 1 : 0;
                bool better = rank > bestRank || (rank == bestRank && distance < bestDistance);
                if (better) { best = n; bestDistance = distance; }
            }
            return best;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref externalStates, "externalTrackStates", LookMode.Value, LookMode.Deep, ref externalStateKeys, ref externalStateValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                rebuild = true;
                externalStates ??= new Dictionary<int, BVRTrackState>();
                externalTracks.Clear();
            }
        }
        public void AddFlash(Vector3 point) => drawer.AddFlash(point);
        public int CountEngaged(MapParent parent, Faction faction)
        {
            int locks = Tracks.Count(t => t.State.lockUntil >= 0 && t.State.lockNode == parent && t.State.lockFaction == faction);
            int flying = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Interceptor>()
                .Count(i => !i.Destroyed && i.usesChannel && i.launchNode == parent && i.Faction == faction);
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
            // Abstract NPC air-defence nodes are a world-map radar-network feature. Keep physical map
            // radars above active for local combat, but do not synthesize or register remote NPC nodes
            // until the player has researched the radar detection network.
            bool npcWorldNetworkEnabled = WorldComponent_FactionAirDefenseAlert.Enabled;
            if (npcWorldNetworkEnabled)
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
            ApplyAlertStages();
            autonomousNodes.Clear();
            foreach (BVRNode node in nodes)
            {
                node.jamEff = EWEnvironment.NodeJam(node);
                if (node.HasAutonomousEffector) { autonomousNodes.Add(node); }
            }
            lastRebuildTick = Find.TickManager.TicksGame;
        }

        /// <summary>
        /// NPC 節點套用警戒階段（docs/NPC防空警戒五階段_執行計畫.md §3）。只套用在節點陣營等於據點陣營的情況；
        /// 玩家節點、或別的陣營放在這張地圖上的裝置維持舊規則。
        /// </summary>
        private void ApplyAlertStages()
        {
            WorldComponent_FactionAirDefenseAlert alert = WorldComponent_FactionAirDefenseAlert.Current;
            bool enabled = alert != null && WorldComponent_FactionAirDefenseAlert.Enabled;
            foreach (BVRNode node in nodes)
            {
                node.alertStage = 0;
                node.rangeFactor = 1f;
                if (!enabled || node.faction == null || node.faction.IsPlayer || node.parent?.Faction != node.faction) { continue; }
                int stage = alert.StageOf(node.parent);
                if (stage <= 0) { continue; }
                node.alertStage = stage;
                node.rangeFactor = AirDefenseAlertPolicy.DetectionFactor(stage, alert.RadarWarm(node.parent));
            }
        }

        private int lastRebuildTick = -1;

        /// <summary>目前的節點（封鎖圈與介面使用）；本 tick 還沒建立時先建立一次。</summary>
        public List<BVRNode> Nodes
        {
            get
            {
                if (lastRebuildTick != Find.TickManager.TicksGame && (nodes.Count == 0 || rebuild)) { RebuildNodes(); }
                return nodes;
            }
        }

        public BVRNode NodeOf(MapParent parent, Faction faction = null)
            => Nodes.FirstOrDefault(n => n.parent == parent && (faction == null ? n.faction == parent.Faction : n.faction == faction));
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
            // 先做干擾機進圈判定：被判定消失的彈不會先被鎖定、白射一枚攔截彈（docs/電子戰建築_實作計畫.md §3.2）。
            JammerUtility.ProcessTracks(MissileTracks);
            RebuildNodes();
            List<IBVRTrack> live = Tracks.ToList();
            PruneExternal(live);
            // 排序只看哪個節點會接手（不看本次掃描的情報），與接戰時的情報檢查分開。
            foreach (IBVRTrack track in InEngagementOrder(live,
                t => NodeFor(t, int.MinValue)?.ThreatAssessment == true ? t.TargetProps.distanceLevel : 0f))
            {
                // 事件處理者可能已經移除後面的航跡。
                if (!track.Self.Destroyed) { Scan(track, now); }
            }
        }
        private void Scan(IBVRTrack track, int now)
        {
            BVRTrackState state = track.State;
            // 箔條成功斷鎖後的延遲期：這段時間內不重新鎖定（等於白吃一次反應時間）。
            if (now < state.lockDelayUntil) { return; }
            if (state.lastKnownDestination != track.Destination)
            {
                state.ClearLock();
                state.lastKnownDestination = track.Destination;
            }
            // 數據鏈：任何會觀測它的節點看到就算該陣營偵測到，不要求「目的地節點」自己看到。
            BVRNode detector = Observe(track, now);
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
                // 一小時內的後續偵測改用訊息，不再每發一封信件並暫停遊戲。
                MissileAlertThrottle.Notify("Detected", "DMSE.BVR.World.DetectedLetter".Translate(),
                    "DMSE.BVR.World.DetectedLetterText".Translate(track.Self.Label, detector.parent.Label,
                        track.TicksToArrival.ToStringTicksToPeriod()), LetterDefOf.ThreatBig,
                    new GlobalTargetInfo(track.Self));
            }
            // 已被鎖定時，箔條／干擾絲有機會打斷這次鎖定。
            TryExpendChaff(track, now);
            if (FireAutonomous(track, now)) return;
            if (state.interceptor != null && !state.interceptor.Destroyed) return;
            state.interceptor = null;

            // 區域防禦：目的地節點優先，否則由最近的可用節點接手。
            // 資料鏈：本節點自己看不到時，只要同陣營本次掃描看到仍可接戰（NodeFor 已檢查情報）。
            // 每次掃描都重查政策：鎖定期間降級、聯盟改變或改靶都會在發射前放掉鎖定。
            BVRNode node = NodeFor(track, now);
            state.altitudeDenied = node == null && NodeFor(track, now, checkAltitude: false) != null;
            if (node == null)
            {
                state.ClearLock();
                return;
            }
            if (state.lockUntil >= 0 && (state.lockNode != node.parent || state.lockFaction != node.faction)) state.ClearLock();
            int eta = track.TicksToArrival;
            var target = new BVRTarget { stealthLevel = track.TargetProps.stealthLevel, speed = track.TargetProps.speed, altitude = track.AltitudeAt(0) };
            CompFireControlRadar radar = node.BestRadar(target, eta);
            CompProperties_FireControlRadar settings = radar?.Props ?? node.DefenseFireControl;
            if (settings == null || eta > settings.maxRangeTicks || !settings.InAltitudeEnvelope(target.altitude))
            {
                state.ClearLock();
                return;
            }
            // No interceptor that can fire now or after reloading: do not lock, and drop any lock (frees the channel).
            bool armed = node.HasInterceptorAmmo;
            if (state.lockUntil < 0)
            {
                if (!armed || node.FreeChannels <= 0) return;
                int lockTicks = radar != null ? radar.LockOnTicksFor(target) : CompFireControlRadar.LockOnTicksFor(settings, target);
                float lockCost = AirDefenseHooks.WorldStat(AirDefenseStat.LockTicks, lockTicks * EWEnvironment.LockFactor(node.jamEff),
                    node, track, null, sensor: radar?.parent, eta: eta);
                state.lockUntil = now + Mathf.Max(1, Mathf.RoundToInt(lockCost));
                state.lockNode = node.parent;
                state.lockFaction = node.faction;
                state.lockNodeTile = node.Tile;
                if (node.UsesAlertRules) { WorldComponent_FactionAirDefenseAlert.Current?.NotifyEngagingPlayer(node, track); }
                AirDefenseHooks.WorldEvent(AirCombatEventKind.LockStarted, track, node.faction, node.parent, node.local?.map, effector: radar?.parent);
                return;
            }
            if (!armed)
            {
                state.ClearLock();
                return;
            }
            if (state.lockUntil > now) return;
            AirEngagement engagement = node.EngagementFor(track, track.Self, track.PositionAt(0), eta, target.altitude);
            IAirEffector effector = node.ReadyEffector(now, engagement);
            if (effector == null) return;
            AirEffectorShot shot = effector.PrepareShot(engagement);
            if (!SolveShot(node, track, shot, out int ticks, out Vector3 point))
            {
                state.ClearLock();
                return;
            }
            // 命中率看會合點的高度：飛行剖面可能在攔截彈到達前離開可攔截的高度。
            target.altitude = track.AltitudeAt(ticks);
            if (!settings.InAltitudeEnvelope(target.altitude))
            {
                state.ClearLock();
                return;
            }
            float baseChance = radar != null ? radar.ComputeHitChance(target, eta) : CompFireControlRadar.ComputeHitChance(settings, target, eta);
            float chance = ShotChance(baseChance, shot, node, track, radar?.parent, eta);
            if (!effector.TryFire(engagement, now, point)) return;
            WorldObject_Interceptor interceptor = Launch(node, track, effector, shot, chance, ticks, point, now, usesChannel: true);
            state.ClearLock();
            state.interceptor = interceptor;
            state.interceptAttempts++;
        }

        /// <summary>點防禦式武器：不鎖定、不佔通道，也不受在飛的攔截彈影響。回傳 true 表示航跡已被移除。</summary>
        private bool FireAutonomous(IBVRTrack track, int now)
        {
            if (autonomousNodes.Count == 0) { return false; }
            float altitude = track.AltitudeAt(0);
            int eta = track.TicksToArrival;
            Vector3 position = track.PositionAt(0);
            foreach (BVRNode node in autonomousNodes)
            {
                if (node.parent == null || node.faction == null || !track.State.FactionSees(node.faction, now) || !node.PolicyAllows(track)) { continue; }
                AirEngagement engagement = node.EngagementFor(track, track.Self, position, eta, altitude);
                foreach (IAirEffector effector in node.Effectors.Where(e => !e.UsesFireControl).ToList())
                {
                    if (!effector.CanFire(now) || !effector.CanEngage(engagement)) { continue; }
                    AirEffectorShot shot = effector.PrepareShot(engagement);
                    if (!SolveShot(node, track, shot, out int ticks, out Vector3 point)) { continue; }
                    float chance = ShotChance(shot.BaseHitChance, shot, node, track, effector.Source, eta);
                    if (!effector.TryFire(engagement, now, point)) { continue; }
                    Launch(node, track, effector, shot, chance, ticks, point, now, usesChannel: false);
                    track.State.interceptAttempts++;
                    if (track.Self.Destroyed) { return true; }
                }
            }
            return false;
        }

        private static bool SolveShot(BVRNode node, IBVRTrack track, AirEffectorShot shot, out int ticks, out Vector3 point)
        {
            if (shot.AngularSpeed <= 0f)
            {
                ticks = 0;
                point = track.PositionAt(0);
                return true;
            }
            return node.DestinationIs(track)
                ? BVRInterceptSolver.TrySolve(track, node.Center, shot.AngularSpeed, out ticks, out point)
                : BVRInterceptSolver.TrySolveGeneral(track, node.Center, shot.AngularSpeed, out ticks, out point);
        }

        private static float ShotChance(float baseChance, AirEffectorShot shot, BVRNode node, IBVRTrack track, Thing sensor, int eta)
            => AirDefenseHooks.WorldStat(AirDefenseStat.HitChance, Mathf.Clamp01(baseChance * shot.HitFactor + shot.HitBonus),
                node, track, null, shot.Loadout, sensor, eta);

        /// <summary>
        /// 武器已開火：瞬間命中當場結算並回傳 null；否則產生攔截物，到時由 <see cref="Resolve"/> 結算。
        /// </summary>
        private WorldObject_Interceptor Launch(BVRNode node, IBVRTrack track, IAirEffector effector, AirEffectorShot shot,
            float chance, int ticks, Vector3 point, int now, bool usesChannel)
        {
            void Announce() => AirDefenseHooks.WorldEvent(AirCombatEventKind.InterceptorLaunched, track, node.faction, node.parent, node.local?.map,
                shot.Loadout, effector.Source, chance);
            if (shot.AngularSpeed <= 0f)
            {
                Announce();
                RollShot(track, node.faction, node.parent, chance, shot.ExtraRolls, point, AirTrackDisplay.CurrentLift(track));
                return null;
            }
            WorldObjectDef def = shot.ShotDef ?? DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_Interceptor");
            var interceptor = def != null ? WorldObjectMaker.MakeWorldObject(def) as WorldObject_Interceptor : null;
            if (interceptor == null)
            {
                // 彈藥已經扣了：沒有可用的攔截物定義時當場結算，這一發不會憑空消失。
                Log.ErrorOnce($"[DMSE BVR] {def?.defName ?? "DMSE_Interceptor"} is not a {nameof(WorldObject_Interceptor)} world object.", 0x424c53);
                Announce();
                RollShot(track, node.faction, node.parent, chance, shot.ExtraRolls, point, AirTrackDisplay.CurrentLift(track));
                return null;
            }
            interceptor.Tile = node.Tile;
            interceptor.SetFaction(node.faction);
            interceptor.launchNode = node.parent;
            interceptor.launchTick = now;
            interceptor.interceptTick = now + ticks;
            interceptor.start = node.Center;
            interceptor.end = point;
            interceptor.target = track.Self;
            interceptor.targetDestination = track.Destination;
            interceptor.targetRevision = track.RouteRevision;
            interceptor.hitChance = chance;
            interceptor.extraRolls = shot.ExtraRolls;
            interceptor.usesChannel = usesChannel;
            MissileConfig config = shot.Loadout as MissileConfig;
            interceptor.warhead = config?.PartFor(MissilePartCategory.Warhead);
            interceptor.payloadCapacity = config?.PayloadCapacity ?? 0f;
            Find.WorldObjects.Add(interceptor);
            pending.Add(interceptor);
            pending.Sort((a, b) => a.interceptTick.CompareTo(b.interceptTick));
            Announce();
            return interceptor;
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

        /// <summary>擲命中骰、處理空爆波及，發出事件並顯示閃光。呼叫前目標必須仍有效。</summary>
        private void RollShot(IBVRTrack target, Faction by, MapParent site, float chance, int rolls, Vector3 point, float lift)
        {
            WorldObject targetObject = target.Self;
            PlanetTile destination = target.Destination;
            bool hit = Rand.Chance(chance);
            if (hit) target.Notify_Intercepted(by);
            else if (by == Faction.OfPlayer || target.Attacker == Faction.OfPlayer)
                Messages.Message("DMSE.BVR.World.Missed".Translate(targetObject.Label), new GlobalTargetInfo(targetObject),
                    MessageTypeDefOf.NeutralEvent, historical: false);
            AirDefenseHooks.WorldEvent(AirCombatEventKind.InterceptResolved, target, by, site, site?.Map,
                hitChance: chance, outcome: hit ? AirInterceptOutcome.Hit : AirInterceptOutcome.Miss);
            if (rolls > 0)
            {
                foreach (var other in Tracks.Where(t => t != target && t.Destination == destination
                    && WouldAirburst(by, t)
                    && MissileWorldUtility.SurfaceTilesBetween(t.PositionAt(0), point) <= BVRWorldTuningDef.Current.airburstRadiusTiles).ToList())
                {
                    if (rolls-- <= 0) break;
                    bool splash = Rand.Chance(chance);
                    if (splash) other.Notify_Intercepted(by);
                    AirDefenseHooks.WorldEvent(AirCombatEventKind.InterceptResolved, other, by, site, site?.Map,
                        hitChance: chance, outcome: splash ? AirInterceptOutcome.Hit : AirInterceptOutcome.Miss, secondary: true);
                }
            }
            drawer.AddFlash(AirTrackDisplay.Raised(point, lift));
        }

        private void Resolve(WorldObject_Interceptor interceptor)
        {
            IBVRTrack target = TrackOf(interceptor.target);
            WorldObject targetObject = target?.Self;
            float meetLift = interceptor.MeetLift; // 命中會移除目標，先記下閃光的高度。
            MapParent site = interceptor.launchNode;
            bool valid = target != null && !targetObject.Destroyed && target.TicksToArrival > 0
                && target.Destination == interceptor.targetDestination && target.RouteRevision == interceptor.targetRevision;
            // 結算前重查友軍身分：飛行中結盟就安全解除，不命中也不退款。
            bool friendly = target != null && target.Attacker != null && AirDefenseAlertPolicy.IsFriendly(interceptor.Faction, target.Attacker);
            if (valid && friendly)
            {
                if (interceptor.Faction == Faction.OfPlayer || target.Attacker == Faction.OfPlayer)
                    Messages.Message("DMSE.AirAlert.InterceptorSafed".Translate(targetObject.Label), new GlobalTargetInfo(targetObject),
                        MessageTypeDefOf.NeutralEvent, historical: false);
                AirDefenseHooks.WorldEvent(AirCombatEventKind.InterceptResolved, target, interceptor.Faction, site, site?.Map,
                    hitChance: interceptor.hitChance, outcome: AirInterceptOutcome.Safed);
            }
            else if (valid)
            {
                RollShot(target, interceptor.Faction, site, interceptor.hitChance, interceptor.ExtraRolls, interceptor.end, meetLift);
            }
            else if (target != null && !targetObject.Destroyed)
            {
                if (interceptor.Faction == Faction.OfPlayer || target.Attacker == Faction.OfPlayer)
                    Messages.Message("DMSE.BVR.World.Evaded".Translate(targetObject.Label), new GlobalTargetInfo(targetObject),
                        MessageTypeDefOf.NeutralEvent, historical: false);
                AirDefenseHooks.WorldEvent(AirCombatEventKind.InterceptResolved, target, interceptor.Faction, site, site?.Map,
                    hitChance: interceptor.hitChance, outcome: AirInterceptOutcome.Evaded);
            }
            if (target != null && target.State.interceptor == interceptor) target.State.interceptor = null;
            interceptor.Destroy();
        }

        /// <summary>開發工具：列出每條航跡被各節點接受或拒絕的理由，以及目前的封鎖圈。</summary>
        public string DevDescribeDecisions()
        {
            RebuildNodes();
            int now = Find.TickManager.TicksGame;
            int since = now - Mathf.Max(1, BVRWorldTuningDef.Current.scanIntervalTicks);
            var sb = new System.Text.StringBuilder();
            foreach (IBVRTrack track in Tracks)
            {
                MapParent target = WorldComponent_FactionAirDefenseAlert.TargetSite(track);
                sb.AppendLine($"{track.Self.Label} #{track.Self.ID}: attacker={track.Attacker?.Name ?? "unknown"}, target={target?.Label ?? track.Destination.ToString()} [{target?.Faction?.Name ?? "-"}], eta={track.TicksToArrival}, status={track.State.StatusKey.Translate()}");
                foreach (AirDefenseObservation o in track.State.observations)
                    sb.AppendLine($"    intel {o.faction?.Name}: last seen {now - o.lastSeenTick} ticks ago, reported sites {o.reportedSites.Count}");
                foreach (BVRNode n in nodes)
                {
                    if (n.parent == null || n.faction == null) { continue; }
                    string verdict = !n.CanReachAltitude(track.AltitudeAt(0)) ? "outside intercept altitude"
                        : !n.MayObserve(track) ? "ignores (same faction / ally)"
                        : !n.PolicyAllows(track) ? "observe only (stage policy)"
                        : !track.State.FactionSees(n.faction, since) ? "no fresh intel"
                        : !n.CanEngage(track) ? "outside coverage"
                        : !n.HasInterceptorAmmo ? "no interceptors"
                        : "may engage";
                    sb.AppendLine($"    {n.parent.Label} [{n.faction.Name}] stage={(n.UsesAlertRules ? n.alertStage.ToString() : "legacy")} range={n.DetectionRange(track.TargetProps, track):0.#}: {verdict}");
                }
            }
            foreach (BlockadeZone zone in AirspaceRestrictionUtility.ActiveZones)
                sb.AppendLine($"Blockade: {zone.site.Label} [{zone.faction.Name}] radius {zone.radius:0.#} tiles on {zone.tile.LayerDef.defName}");
            return sb.Length == 0 ? "No world tracks or blockades." : sb.ToString();
        }

        /// <summary>空爆波及：玩家（或警戒系統關閉時）只波及敵對導彈；NPC 波及非盟友導彈。</summary>
        private static bool WouldAirburst(Faction by, IBVRTrack other)
        {
            if (by == null) { return false; }
            if (by.IsPlayer || !WorldComponent_FactionAirDefenseAlert.Enabled) { return by.HostileTo(other.Attacker); }
            return AirDefenseAlertPolicy.IsNonAlly(by, other.Attacker);
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
        private static void TryExpendChaff(IBVRTrack track, int now)
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
                Messages.Message("DMSE.BVR.World.Chaff".Translate(track.Self.Label), new GlobalTargetInfo(track.Self),
                    MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        public override void WorldComponentUpdate()
        {
            base.WorldComponentUpdate();
            if (WorldRendererUtility.WorldSelected)
            {
                drawer.Draw(nodes, ShowAllCoverage);
                drawer.DrawAltitudeTracks(Tracks);
            }
        }
    }
}
