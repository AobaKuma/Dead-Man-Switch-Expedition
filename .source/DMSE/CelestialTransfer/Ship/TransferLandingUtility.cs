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
    /// 降落到已有東西的格子（比照原版逆重船的降落選位）：船先停在目標旁的空格，玩家在目標地圖上選降落位置，
    /// 船體以位移搬入目標地圖（<see cref="ShipSeparationUtility.MoveHull"/>），船體地圖與世界物件隨後移除。
    /// 選定位置不能降落時自動找最近的可降落位置；整張地圖都放不下時船繼續停在旁邊。
    /// 目標沒有地圖時以足以容納船體的尺寸生成。
    /// </summary>
    public static class TransferLandingUtility
    {
        /// <summary>生成地圖時船體四周保留的格數（含原版 10 格的禁建邊緣）。</summary>
        private const int MapEdgeMargin = 14;

        private const int AutoSearchStride = 2;

        /// <summary>可以降落的地點：同一太空層、有 MapParent、不是起點，也不是飛行中的轉移艦。</summary>
        public static bool IsLandableSite(PlanetTile tile, PlanetTile origin, out MapParent site)
        {
            site = tile.Valid ? Find.WorldObjects.MapParentAt(tile) : null;
            if (site == null || tile == origin || tile.Layer != origin.Layer)
            {
                site = null;
                return false;
            }
            if (site is WorldObject_TransferShip other && other.InFlight)
            {
                site = null;
                return false;
            }
            return true;
        }

        /// <summary>有已抵達的轉移艦等著降落到這個地點（飛行中的不算）。</summary>
        public static bool IsPendingLandingSite(MapParent site)
        {
            List<WorldObject> objects = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is WorldObject_TransferShip ship && ship.landingTarget == site.Tile && !ship.InFlight && ship.HasMap && !ship.Destroyed)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>船體格子：船體地圖上所有非虛空的格子（含帶上船的其他網路與爭議格）。</summary>
        public static List<IntVec3> HullCells(Map shipMap)
        {
            List<IntVec3> cells = new List<IntVec3>();
            TerrainGrid tg = shipMap.terrainGrid;
            foreach (IntVec3 c in shipMap.AllCells)
            {
                if (tg.TerrainAt(c) != TerrainDefOf.Space)
                {
                    cells.Add(c);
                }
            }
            return cells;
        }

        // ── 降落位置判定（比照原版 Designator_MoveGravship.IsValidCell，地形由船體自帶所以不檢查） ──

        public static AcceptanceReport IsValidLandingCell(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map))
            {
                return "GravshipOutOfBounds".Translate();
            }
            if (!cell.InBounds(map, 1) || cell.InNoBuildEdgeArea(map))
            {
                return "GravshipInNoBuildArea".Translate();
            }
            if (map.landingBlockers != null)
            {
                foreach (CellRect blocker in map.landingBlockers)
                {
                    if (blocker.Contains(cell))
                    {
                        return "GravshipInBlockedArea".Translate();
                    }
                }
            }
            if (cell.Roofed(map))
            {
                return "GravshipBlockedByRoof".Translate();
            }
            if (cell.Fogged(map))
            {
                return "GravshipBlockedByFog".Translate();
            }
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (!thing.def.preventGravshipLandingOn)
                {
                    BuildingProperties building = thing.def.building;
                    if (building == null || building.canLandGravshipOn)
                    {
                        if (thing is Pawn pawn && (pawn.RaceProps.Humanlike || pawn.HostileTo(Faction.OfPlayer)))
                        {
                            return "GravshipBlockedBy".Translate(pawn);
                        }
                        continue;
                    }
                }
                return "GravshipBlockedBy".Translate(thing);
            }
            return true;
        }

        public static AcceptanceReport CanLandAt(Map map, List<IntVec3> cells, IntVec3 offset, List<IntVec3> validCells = null, List<IntVec3> invalidCells = null)
        {
            AcceptanceReport result = true;
            for (int i = 0; i < cells.Count; i++)
            {
                IntVec3 d = cells[i] + offset;
                AcceptanceReport r = IsValidLandingCell(d, map);
                if (r.Accepted)
                {
                    validCells?.Add(d);
                    continue;
                }
                result = r;
                invalidCells?.Add(d);
                if (validCells == null && invalidCells == null)
                {
                    return r;
                }
            }
            return result;
        }

        /// <summary>從 preferred 向外找最近的可降落位移；先在附近逐格找，再以較粗的間距掃整張地圖。</summary>
        public static bool TryFindLandingOffset(Map map, List<IntVec3> cells, IntVec3 preferred, out IntVec3 offset)
        {
            offset = preferred;
            if (cells.Count == 0)
            {
                return false;
            }
            CellRect bounds = CellRect.FromCellList(cells);
            sbyte[] cache = new sbyte[map.cellIndices.NumGridCells];
            int radial = GenRadial.RadialPattern.Length;
            for (int i = 0; i < radial; i++)
            {
                IntVec3 candidate = preferred + GenRadial.RadialPattern[i];
                if (Fits(map, cells, bounds, candidate, cache))
                {
                    offset = candidate;
                    return true;
                }
            }
            // 整張地圖粗掃：以距 preferred 的遠近排序
            List<IntVec3> coarse = new List<IntVec3>();
            for (int x = -bounds.minX; x + bounds.maxX < map.Size.x; x += AutoSearchStride)
            {
                for (int z = -bounds.minZ; z + bounds.maxZ < map.Size.z; z += AutoSearchStride)
                {
                    coarse.Add(new IntVec3(x, 0, z));
                }
            }
            coarse.SortBy(o => (o - preferred).LengthHorizontalSquared);
            for (int i = 0; i < coarse.Count; i++)
            {
                if (Fits(map, cells, bounds, coarse[i], cache))
                {
                    offset = coarse[i];
                    return true;
                }
            }
            return false;
        }

        private static bool Fits(Map map, List<IntVec3> cells, CellRect bounds, IntVec3 offset, sbyte[] cache)
        {
            CellRect moved = bounds.MovedBy(offset);
            if (!moved.InBounds(map))
            {
                return false;
            }
            for (int i = 0; i < cells.Count; i++)
            {
                IntVec3 d = cells[i] + offset;
                int idx = map.cellIndices.CellToIndex(d);
                if (cache[idx] == 0)
                {
                    cache[idx] = (sbyte)(IsValidLandingCell(d, map).Accepted ? 1 : -1);
                }
                if (cache[idx] < 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>讓船體置中的位移（自動降落的起點）。</summary>
        public static IntVec3 CenteredOffset(Map map, List<IntVec3> cells)
        {
            return map.Center - CellRect.FromCellList(cells).CenterCell;
        }

        // ── 目的地地圖 ───────────────────────────────────────────────────────

        /// <summary>生成目的地地圖的尺寸：原版預設 / 地點偏好尺寸，且至少能容納船體加上邊緣保留。</summary>
        public static IntVec3 RequiredMapSize(MapParent site, CellRect hullBounds)
        {
            IntVec3 size = Find.World.info.initialMapSize;
            if (site.def.overrideMapSize.HasValue)
            {
                size = site.def.overrideMapSize.Value;
            }
            if (site is Site s)
            {
                IntVec3 preferred = s.PreferredMapSize;
                size = new IntVec3(Mathf.Max(size.x, preferred.x), 1, Mathf.Max(size.z, preferred.z));
                if (s.MainSitePartDef?.minMapSize != null)
                {
                    IntVec3 min = s.MainSitePartDef.minMapSize.Value;
                    size = new IntVec3(Mathf.Max(size.x, min.x), 1, Mathf.Max(size.z, min.z));
                }
            }
            size.x = Mathf.Max(size.x, hullBounds.Width + MapEdgeMargin * 2);
            size.z = Mathf.Max(size.z, hullBounds.Height + MapEdgeMargin * 2);
            size.y = 1;
            return size;
        }

        /// <summary>抵達後或點「降落」時呼叫：目的地沒有地圖就先生成，再進入選位。</summary>
        public static void BeginLanding(WorldObject_TransferShip ship)
        {
            if (!ship.HasMap || ship.InFlight)
            {
                return;
            }
            MapParent site = Find.WorldObjects.MapParentAt(ship.landingTarget);
            if (site == null || site.Destroyed)
            {
                Messages.Message("DMSE.Transfer.Land.SiteGone".Translate(), ship, MessageTypeDefOf.RejectInput, historical: false);
                ship.landingTarget = PlanetTile.Invalid;
                return;
            }
            if (site.HasMap)
            {
                StartTargeting(ship, site.Map);
                return;
            }
            LongEventHandler.QueueLongEvent(delegate
            {
                Map map = GenerateSiteMap(ship, site);
                LongEventHandler.ExecuteWhenFinished(delegate { StartTargeting(ship, map); });
            }, "GeneratingMap", doAsynchronously: false, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
        }

        /// <summary>比照原版 GravshipUtility.ArriveNewMap：無主地點歸玩家；進入敵方據點時發信件並影響關係。</summary>
        private static Map GenerateSiteMap(WorldObject_TransferShip ship, MapParent site)
        {
            if (site.def.canHaveFaction && site.Faction == null)
            {
                site.SetFaction(Faction.OfPlayer);
            }
            IntVec3 size = RequiredMapSize(site, CellRect.FromCellList(HullCells(ship.Map)));
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, size, site.def);
            if ((map.Parent is Settlement || (map.Parent is Site s && s.parts.All(p => p.def.considerEnteringAsAttack))) && map.Parent.Faction != null && map.Parent.Faction != Faction.OfPlayer)
            {
                Find.TickManager.Notify_GeneratedPotentiallyHostileMap();
                TaggedString label = "LetterLabelGravshipEnteredEnemyBase".Translate();
                TaggedString text = "LetterGravshipEnteredEnemyBase".Translate(map.Parent.Label.ApplyTag(TagType.Settlement, map.Parent.Faction.GetUniqueLoadID())).CapitalizeFirst();
                SettlementUtility.AffectRelationsOnAttacked(map.Parent, ref text);
                Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent);
                Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: true);
            }
            return map;
        }

        /// <summary>
        /// 不在這裡直接選設計器：切換地圖（MapInterface.Notify_SwitchedMap）、開啟對話框（Window.PreOpen）都會取消目前的設計器，
        /// 抵達、生成地圖與信件又常在同一時刻發生，選上後立刻被取消就會變成「沒選位、船停在旁邊」。
        /// 改由 <see cref="GameComponent_TransferLanding"/> 等地圖切換完成、沒有長時間事件後的下一個畫面更新才選上。
        /// </summary>
        private static void StartTargeting(WorldObject_TransferShip ship, Map map)
        {
            if (map == null || !ship.HasMap)
            {
                return;
            }
            GameComponent_TransferLanding.Request(ship, map);
        }

        /// <summary>自動降落：從地圖中央附近找位置；找不到就留在原處。</summary>
        public static void LandAutomatically(WorldObject_TransferShip ship)
        {
            MapParent site = Find.WorldObjects.MapParentAt(ship.landingTarget);
            if (site == null || !site.HasMap)
            {
                BeginLanding(ship);
                return;
            }
            List<IntVec3> cells = HullCells(ship.Map);
            if (TryFindLandingOffset(site.Map, cells, CenteredOffset(site.Map, cells), out IntVec3 offset))
            {
                Land(ship, site.Map, offset, moved: false);
            }
            else
            {
                Messages.Message("DMSE.Transfer.Land.NoRoom".Translate(site.Label), ship, MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        public static void Land(WorldObject_TransferShip ship, Map dest, IntVec3 offset, bool moved)
        {
            Map shipMap = ship.Map;
            HashSet<IntVec3> cells = new HashSet<IntVec3>(HullCells(shipMap));
            LongEventHandler.QueueLongEvent(delegate
            {
                ShipSeparationUtility.MoveHull(shipMap, dest, cells, offset);
                // 不在船體格上的 pawn（例如在虛空中行走）放到降落處旁
                IntVec3 center = CellRect.FromCellList(cells).CenterCell + offset;
                foreach (Pawn p in shipMap.mapPawns.AllPawnsSpawned.ToList())
                {
                    if (!CellFinder.TryFindRandomCellNear(center, dest, 30, x => x.Standable(dest), out IntVec3 spot))
                    {
                        spot = center;
                    }
                    p.DeSpawn();
                    GenSpawn.Spawn(p, spot, dest);
                }
                string shipLabel = ship.Label;
                string siteLabel = dest.Parent?.Label ?? string.Empty;
                TransferGraceUtility.CarryOver(shipMap, dest);
                ship.landingTarget = PlanetTile.Invalid;
                ship.Destroy();
                Current.Game.CurrentMap = dest;
                CameraJumper.TryJump(center, dest);
                TaggedString text = "DMSE.Transfer.Land.LandedText".Translate(shipLabel, siteLabel);
                if (moved)
                {
                    text += "\n\n" + "DMSE.Transfer.Land.Moved".Translate();
                }
                Find.LetterStack.ReceiveLetter("DMSE.Transfer.Land.LandedLabel".Translate(), text, LetterDefOf.PositiveEvent, new TargetInfo(center, dest));
            }, "DMSE.Transfer.Landing", doAsynchronously: false, null);
        }
    }

    /// <summary>
    /// 原版 SpaceMapParent（小行星等）的地圖上沒有 pawn / 建築時，下一次檢查就連同地點一起移除；
    /// 為降落生成的空地圖會在選位前被刪掉。有轉移艦等著降落時跳過移除檢查，取消降落後恢復原版行為。
    /// </summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.CheckRemoveMapNow))]
    public static class Patch_MapParent_CheckRemoveMapNow_PendingLanding
    {
        [HarmonyPrefix]
        public static bool Prefix(MapParent __instance)
        {
            return !(__instance.HasMap && TransferLandingUtility.IsPendingLandingSite(__instance));
        }
    }

    /// <summary>延後選上降落選位設計器（見 <see cref="TransferLandingUtility"/>.StartTargeting）。不存檔：讀檔後用世界物件的「降落」Gizmo 重新開始。</summary>
    public class GameComponent_TransferLanding : GameComponent
    {
        private static WorldObject_TransferShip pendingShip;

        private static Map pendingMap;

        private static int stableFrames;

        public GameComponent_TransferLanding(Game game)
        {
            pendingShip = null;
            pendingMap = null;
        }

        public static void Request(WorldObject_TransferShip ship, Map map)
        {
            pendingShip = ship;
            pendingMap = map;
            stableFrames = 0;
        }

        public override void GameComponentUpdate()
        {
            if (pendingShip == null)
            {
                return;
            }
            if (pendingShip.Destroyed || !pendingShip.HasMap || pendingMap == null || !Find.Maps.Contains(pendingMap))
            {
                if (!pendingShip.Destroyed)
                {
                    Messages.Message("DMSE.Transfer.Land.SiteGone".Translate(), pendingShip, MessageTypeDefOf.RejectInput, historical: false);
                }
                pendingShip = null;
                pendingMap = null;
                return;
            }
            if (LongEventHandler.AnyEventNowOrWaiting)
            {
                stableFrames = 0;
                return;
            }
            if (Find.CurrentMap != pendingMap || WorldRendererUtility.WorldSelected)
            {
                Current.Game.CurrentMap = pendingMap;
                CameraJumper.TryJump(pendingMap.Center, pendingMap, CameraJumper.MovementMode.Cut);
                stableFrames = 0;
                return;
            }
            // 地圖切換後再等兩個畫面，讓同一時刻開啟的信件 / 對話框先處理完
            if (++stableFrames < 2)
            {
                return;
            }
            Find.DesignatorManager.Select(new Designator_LandTransferShip(pendingShip, pendingMap));
            pendingShip = null;
            pendingMap = null;
        }
    }

    /// <summary>降落選位（比照原版 Designator_MoveGravship，不旋轉）：船體輪廓跟著滑鼠，白色可降落、紅色受阻。</summary>
    public class Designator_LandTransferShip : Designator
    {
        private static readonly Color ValidStripes = ColorLibrary.White.WithAlpha(0.5f);

        private static readonly Color InvalidStripes = ColorLibrary.Red.WithAlpha(0.5f);

        private readonly WorldObject_TransferShip ship;

        private readonly Map map;

        private readonly List<IntVec3> cells;

        private readonly IntVec3 center;

        private readonly List<IntVec3> tmpValid = new List<IntVec3>();

        private readonly List<IntVec3> tmpInvalid = new List<IntVec3>();

        private AcceptanceReport lastReport = true;

        private float selectedAt;

        public Designator_LandTransferShip(WorldObject_TransferShip ship, Map map)
        {
            this.ship = ship;
            this.map = map;
            cells = TransferLandingUtility.HullCells(ship.Map);
            center = cells.Count > 0 ? CellRect.FromCellList(cells).CenterCell : IntVec3.Zero;
            defaultLabel = "DMSE.Transfer.Land.Designator".Translate();
            defaultDesc = "DMSE.Transfer.Land.DesignatorDesc".Translate();
            useMouseIcon = false;
        }

        public override bool AlwaysDoGuiControls => true;

        public override void Selected()
        {
            base.Selected();
            selectedAt = Time.realtimeSinceStartup;
        }

        private IntVec3 OffsetFor(IntVec3 mouseCell) => mouseCell - center;

        public override AcceptanceReport CanDesignateCell(IntVec3 loc)
        {
            // 受阻也允許點擊：會自動改降落在最近的可降落位置。剛選上的瞬間不接受點擊，避免觸發選位的那次點擊被當成降落位置
            return Find.CurrentMap == map && !ship.Destroyed && ship.HasMap && Time.realtimeSinceStartup - selectedAt > 0.3f;
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            IntVec3 offset = OffsetFor(c);
            bool moved = false;
            if (!TransferLandingUtility.CanLandAt(map, cells, offset).Accepted)
            {
                if (!TransferLandingUtility.TryFindLandingOffset(map, cells, offset, out offset))
                {
                    Messages.Message("DMSE.Transfer.Land.NoRoom".Translate(map.Parent?.Label ?? string.Empty), MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                moved = true;
            }
            Find.DesignatorManager.Deselect();
            TransferLandingUtility.Land(ship, map, offset, moved);
        }

        public override void SelectedUpdate()
        {
            if (Find.CurrentMap != map)
            {
                return;
            }
            tmpValid.Clear();
            tmpInvalid.Clear();
            lastReport = TransferLandingUtility.CanLandAt(map, cells, OffsetFor(UI.MouseCell()), tmpValid, tmpInvalid);
            GenDraw.DrawFieldEdges(tmpValid, ColorLibrary.White);
            GenDraw.DrawFieldEdges(tmpInvalid, ColorLibrary.Red);
            GenDraw.DrawDiagonalStripes(tmpValid, ValidStripes);
            GenDraw.DrawDiagonalStripes(tmpInvalid, InvalidStripes);
        }

        public override void DrawMouseAttachments()
        {
            if (!lastReport.Accepted)
            {
                GenUI.DrawMouseAttachment(null, lastReport.Reason + "\n" + "DMSE.Transfer.Land.WillMove".Translate(), 0f, default(Vector2), null, ColorLibrary.RedReadable, drawTextBackground: true, new Color(0f, 0f, 0f, 0.5f));
            }
        }
    }
}
