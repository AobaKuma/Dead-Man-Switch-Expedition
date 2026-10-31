using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 防空封鎖的主動攔截（2026-10-31 取代起飛前拒絕）：每次掃描找出飛進封鎖圈、不被允許通行的載具
    /// （運輸艙與穿梭機、重力船、飛行中的轉移艦），由封鎖據點發射 <see cref="WorldObject_BlockadeInterceptor"/>。
    /// 使用據點自己的攔截彈庫存、火控與火力通道；同一據點對同一載具同時只有一枚在飛。
    /// </summary>
    public class WorldComponent_BlockadeInterception : WorldComponent
    {
        private const int ScanIntervalTicks = 60;

        public WorldComponent_BlockadeInterception(World world) : base(world) { }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            int now = Find.TickManager.TicksGame;
            if (now % ScanIntervalTicks != 0) { return; }
            List<BlockadeZone> zones = AirspaceRestrictionUtility.ActiveZones;
            if (zones.Count == 0) { return; }

            List<WorldObject> objects = Find.WorldObjects.AllWorldObjects.ToList();
            List<WorldObject_BlockadeInterceptor> shots = objects.OfType<WorldObject_BlockadeInterceptor>().Where(s => !s.Destroyed).ToList();
            foreach (WorldObject flyer in objects)
            {
                if (!BlockadeHitUtility.InFlight(flyer) || flyer.Faction == null) { continue; }
                Vector3 position = flyer.DrawPos;
                foreach (BlockadeZone zone in zones)
                {
                    if (zone.tile.Layer != flyer.Tile.Layer || AirspaceRestrictionUtility.AllowedThrough(zone, flyer.Faction)) { continue; }
                    if (MissileWorldUtility.SurfaceTilesBetween(zone.center, position) > zone.radius) { continue; }
                    if (shots.Any(s => s.target == flyer && s.launchNode == zone.site)) { continue; }
                    WorldObject_BlockadeInterceptor shot = TryFire(zone, flyer, position, now, shots);
                    if (shot != null) { shots.Add(shot); }
                }
            }
        }

        private static WorldObject_BlockadeInterceptor TryFire(BlockadeZone zone, WorldObject flyer, Vector3 position, int now,
            List<WorldObject_BlockadeInterceptor> shots)
        {
            BVRNode node = WorldComponent_BVRNetwork.Current?.NodeOf(zone.site, zone.faction);
            if (node == null || !node.HasInterceptorAmmo) { return null; }
            int ownShots = shots.Count(s => s.launchNode == zone.site && s.Faction == zone.faction);
            if (node.FreeChannels - ownShots <= 0) { return null; }
            CompMissileLauncher_Interceptor launcher = node.ReadyLauncher(now);
            if (launcher == null && !node.DefenseReady(now)) { return null; }

            MissileConfig config = launcher?.GetLoadedMissileConfig() ?? node.DefenseConfig;
            float speed = MissileWorldMotion.AngularSpeed(config) * BVRWorldTuningDef.Current.interceptorSpeedMultiplier;
            float angle = GenMath.SphericalDistance(node.Center.normalized, position.normalized);
            int ticks = Mathf.Max(BVRWorldTuningDef.Current.minInterceptMarginTicks, Mathf.CeilToInt(angle / Mathf.Max(1e-6f, speed)));
            var target = new BVRTarget();
            CompProperties_FireControlRadar settings = node.BestRadar(target, ticks)?.Props ?? node.DefenseFireControl;
            if (settings == null) { return null; }

            WorldObjectDef def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("DMSE_BlockadeInterceptor");
            if (def == null) { return null; }
            MissilePartDef warhead = config?.PartFor(MissilePartCategory.Warhead);
            float payload = config?.PayloadCapacity ?? 0;
            float chance = Mathf.Clamp01(CompFireControlRadar.ComputeHitChance(settings, target, ticks)
                * (config?.InterceptHitFactor ?? 1f)
                + (warhead?.warheadEffect?.InterceptBonus(payload) ?? 0));
            bool fired = launcher != null
                ? launcher.FireWorldInterceptor(now, MissileProjectileMaker.WorldInterceptBearing(node.Tile, position))
                : node.TryConsumeDefense(now);
            if (!fired) { return null; }

            var shot = (WorldObject_BlockadeInterceptor)WorldObjectMaker.MakeWorldObject(def);
            shot.Tile = node.Tile;
            shot.SetFaction(node.faction);
            shot.launchNode = node.parent;
            shot.launchTick = now;
            shot.interceptTick = now + ticks;
            shot.start = node.Center;
            shot.lastTargetPos = position;
            shot.target = flyer;
            shot.hitChance = chance;
            Find.WorldObjects.Add(shot);

            if (flyer.Faction.IsPlayer || node.faction.IsPlayer)
            {
                Messages.Message("DMSE.Blockade.Engaging".Translate(node.faction.NameColored, zone.site.LabelCap, flyer.LabelCap),
                    new GlobalTargetInfo(flyer), MessageTypeDefOf.ThreatSmall);
            }
            return shot;
        }
    }

    /// <summary>封鎖攔截彈：從據點追向載具目前的位置，到時擲骰命中。</summary>
    public class WorldObject_BlockadeInterceptor : WorldObject
    {
        public int launchTick, interceptTick;
        public Vector3 start, lastTargetPos;
        public WorldObject target;
        public MapParent launchNode;
        public float hitChance;

        private bool TargetAlive => target != null && !target.Destroyed && BlockadeHitUtility.InFlight(target);

        private Vector3 TargetPos => TargetAlive ? target.DrawPos : lastTargetPos;

        public override Vector3 DrawPos => Vector3.Slerp(start, TargetPos, Mathf.Clamp01(
            (float)(Find.TickManager.TicksGame - launchTick) / Mathf.Max(1, interceptTick - launchTick)));

        public override bool ExpandingIconFlipHorizontal
            => GenWorldUI.WorldToUIPosition(start).x > GenWorldUI.WorldToUIPosition(TargetPos).x;

        public override float ExpandingIconRotation
        {
            get
            {
                Vector2 a = GenWorldUI.WorldToUIPosition(start);
                Vector2 b = GenWorldUI.WorldToUIPosition(TargetPos);
                return Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg + 90f;
            }
        }

        public override string GetInspectString() => "DMSE.BVR.World.InterceptorInspect".Translate(
            target?.Label ?? "?", Mathf.Max(0, interceptTick - Find.TickManager.TicksGame).ToStringTicksToPeriod());

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (TargetAlive) { lastTargetPos = target.DrawPos; }
            if (Find.TickManager.TicksGame >= interceptTick) { Resolve(); }
        }

        private void Resolve()
        {
            bool notify = Faction == Faction.OfPlayer || target?.Faction == Faction.OfPlayer;
            if (!TargetAlive)
            {
                if (notify && target != null && !target.Destroyed)
                {
                    Messages.Message("DMSE.BVR.World.Evaded".Translate(target.Label), new GlobalTargetInfo(target), MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
            // 結算前重查友軍身分：飛行中結盟就安全解除。
            else if (AirDefenseAlertPolicy.IsFriendly(Faction, target.Faction))
            {
                if (notify)
                {
                    Messages.Message("DMSE.AirAlert.InterceptorSafed".Translate(target.Label), new GlobalTargetInfo(target), MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
            else if (Rand.Chance(hitChance))
            {
                WorldComponent_BVRNetwork.Current?.AddFlash(lastTargetPos);
                BlockadeHitUtility.ApplyHit(target, Faction);
            }
            else if (notify)
            {
                Messages.Message("DMSE.BVR.World.Missed".Translate(target.Label), new GlobalTargetInfo(target), MessageTypeDefOf.NeutralEvent, historical: false);
            }
            Destroy();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref launchTick, "launchTick");
            Scribe_Values.Look(ref interceptTick, "interceptTick");
            Scribe_Values.Look(ref start, "start");
            Scribe_Values.Look(ref lastTargetPos, "lastTargetPos");
            Scribe_Values.Look(ref hitChance, "hitChance");
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // 目標或據點可能已先消失：不寫入指向未存檔世界物件的參照。
                WorldObject liveTarget = target != null && !target.Destroyed ? target : null;
                MapParent liveNode = launchNode != null && !launchNode.Destroyed ? launchNode : null;
                Scribe_References.Look(ref liveTarget, "target");
                Scribe_References.Look(ref liveNode, "launchNode");
            }
            else
            {
                Scribe_References.Look(ref target, "target");
                Scribe_References.Look(ref launchNode, "launchNode");
            }
        }
    }

    /// <summary>封鎖攔截命中各類載具的效果。</summary>
    public static class BlockadeHitUtility
    {
        private static readonly AccessTools.FieldRef<TravellingTransporters, List<ActiveTransporterInfo>> TransportersOf =
            AccessTools.FieldRefAccess<TravellingTransporters, List<ActiveTransporterInfo>>("transporters");

        /// <summary>會被封鎖攔截的飛行中載具：運輸艙／穿梭機、重力船、飛行中的轉移艦。</summary>
        public static bool InFlight(WorldObject o) => o != null && !o.Destroyed
            && (o is TravellingTransporters || o is Gravship || (o is WorldObject_TransferShip ship && ship.InFlight));

        public static void ApplyHit(WorldObject target, Faction by)
        {
            switch (target)
            {
                case TravellingTransporters transporters:
                    DownOnePod(transporters, by);
                    break;
                case Gravship gravship:
                    DamageGravship(gravship, by);
                    break;
                case WorldObject_TransferShip ship:
                    DamageTransferShip(ship, by);
                    break;
            }
        }

        /// <summary>擊落一艘運輸艙／穿梭機：乘員依 <see cref="InterceptedPawnUtility"/> 處理，其餘內容物與艙體一起銷毀。</summary>
        private static void DownOnePod(TravellingTransporters transporters, Faction by)
        {
            List<ActiveTransporterInfo> pods = TransportersOf(transporters);
            if (pods.NullOrEmpty()) { return; }
            ActiveTransporterInfo pod = pods.RandomElement();
            string label = transporters.Label;
            // 載具可能在這次命中後消失：信件與訊息指向它當下的格子。
            GlobalTargetInfo where = new GlobalTargetInfo(transporters.Tile);
            InterceptedPawnUtility.Resolve(pod.innerContainer.OfType<Pawn>(), new LookTargets(where));
            pod.innerContainer.ClearAndDestroyContents();
            pods.Remove(pod);
            if (transporters.Faction == Faction.OfPlayer || by == Faction.OfPlayer)
            {
                Messages.Message("DMSE.Blockade.PodDown".Translate(label, by.NameColored, pods.Count), where, MessageTypeDefOf.NegativeEvent);
            }
            if (pods.Count == 0) { transporters.Destroy(); }
        }

        /// <summary>重力船：船上幾座建築受損（至少保留 1 點耐久，不在飛行中毀掉，避免落地時生成已毀物件）。</summary>
        private static void DamageGravship(Gravship gravship, Faction by)
        {
            List<Thing> buildings = gravship.Things.Where(t => t is Building && t.def.useHitPoints && !t.Destroyed).ToList();
            int damaged = 0;
            foreach (Thing thing in buildings.InRandomOrder().Take(4))
            {
                int loss = Mathf.CeilToInt(thing.MaxHitPoints * Rand.Range(0.25f, 0.5f));
                thing.HitPoints = Mathf.Max(1, thing.HitPoints - loss);
                damaged++;
            }
            Messages.Message("DMSE.Blockade.GravshipHit".Translate(gravship.LabelCap, by.NameColored, damaged),
                new GlobalTargetInfo(gravship), MessageTypeDefOf.NegativeEvent);
        }

        /// <summary>轉移艦：船體地圖上幾處建築發生爆炸。</summary>
        private static void DamageTransferShip(WorldObject_TransferShip ship, Faction by)
        {
            if (!ship.HasMap) { return; }
            Map map = ship.Map;
            List<Building> buildings = map.listerBuildings.allBuildingsColonist.Where(b => !b.Destroyed).ToList();
            int hits = 0;
            foreach (Building building in buildings.InRandomOrder().Take(3))
            {
                GenExplosion.DoExplosion(building.Position, map, 2.9f, DamageDefOf.Bomb, null, damAmount: 35);
                hits++;
            }
            Find.LetterStack.ReceiveLetter("DMSE.Blockade.TransferHitLabel".Translate(),
                "DMSE.Blockade.TransferHitText".Translate(ship.LabelCap, by.NameColored, hits), LetterDefOf.NegativeEvent, ship);
        }
    }
}
