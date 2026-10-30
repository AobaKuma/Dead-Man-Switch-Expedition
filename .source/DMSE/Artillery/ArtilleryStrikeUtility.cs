using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 超視距火砲打擊的靜態工具類。
    /// 負責：
    /// 1. 球面切線框架方位角計算（<see cref="WorldDirectionAngle"/>）。
    /// 2. 根據方位角在地圖邊緣取格（<see cref="EdgeCellFromAngle"/>）。
    /// 3. 根據世界方向計算砲彈進入邊緣格（<see cref="CalcEntryEdgeCell"/>）。
    /// 4. 在地圖上生成並發射 <see cref="Projectile"/>（<see cref="SpawnShell"/>）。
    /// 5. 從 <see cref="WorldObject_ArtilleryStrike"/> 向 <see cref="MapComponent_ArtilleryStrikes"/> 排程（<see cref="QueueSalvo"/>）。
    /// </summary>
    public static class ArtilleryStrikeUtility
    {
        // =====================================================================
        //  球面方向工具
        // =====================================================================

        /// <summary>
        /// 計算從 <paramref name="src"/> 到 <paramref name="dest"/> 的
        /// 球面切線方位角（RimWorld 慣例：0 = North/+Z，90 = East/+X，
        /// 180 = South，270 = West）。
        ///
        /// 使用球面切平面投影，不依賴螢幕空間，在任何世界位置均穩定。
        /// 切平面的 East 軸 = Cross(星球北極, 法線)；North 軸 = Cross(法線, East)。
        /// </summary>
        public static float WorldDirectionAngle(PlanetTile src, PlanetTile dest)
        {
            WorldGrid grid   = Find.WorldGrid;
            Vector3   srcPos = grid.GetTileCenter(src);
            Vector3   dstPos = grid.GetTileCenter(dest);

            // 切平面上的方向向量（投影到 src 的法線切平面）
            Vector3 dir     = dstPos - srcPos;
            Vector3 normal  = srcPos.normalized;
            Vector3 tangent = Vector3.ProjectOnPlane(dir, normal);

            if (tangent.sqrMagnitude < 1e-6f) { return 0f; } // 幾乎同一 tile，回傳 North
            tangent = tangent.normalized;

            // 切平面上的 East/North 局部軸
            Vector3 worldUp = Vector3.up;                             // 星球北極（Y+）
            Vector3 east    = Vector3.Cross(worldUp, normal);
            if (east.sqrMagnitude < 1e-6f) { east = Vector3.right; } // 極點退化防護
            east  = east.normalized;
            Vector3 north = Vector3.Cross(normal, east).normalized;

            // 投影 → RimWorld 方位角（0=North, 順時針）
            float dx    = Vector3.Dot(tangent, east);   // 東分量（+X）
            float dy    = Vector3.Dot(tangent, north);  // 北分量（+Z）
            float angle = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
            return ((angle % 360f) + 360f) % 360f;
        }

        /// <summary>
        /// 根據方位角（0=North/+Z，90=East/+X）在地圖邊緣取出一個格。
        /// <paramref name="xCoord"/> / <paramref name="zCoord"/> 為沿邊緣軸的偏移參考座標
        /// （水平邊使用 x，垂直邊使用 z），夾至 [1, size-2] 以避免角落格。
        /// </summary>
        public static IntVec3 EdgeCellFromAngle(Map map, float angle, int xCoord, int zCoord)
        {
            angle = ((angle % 360f) + 360f) % 360f;

            if (angle < 45f || angle >= 315f)   // North → +Z 邊
                return new IntVec3(Mathf.Clamp(xCoord, 1, map.Size.x - 2), 0, map.Size.z - 1);
            if (angle < 135f)                   // East → +X 邊
                return new IntVec3(map.Size.x - 1, 0, Mathf.Clamp(zCoord, 1, map.Size.z - 2));
            if (angle < 225f)                   // South → -Z 邊
                return new IntVec3(Mathf.Clamp(xCoord, 1, map.Size.x - 2), 0, 0);
            return new IntVec3(0, 0, Mathf.Clamp(zCoord, 1, map.Size.z - 2));  // West → -X 邊
        }

        // =====================================================================
        //  邊緣格計算
        // =====================================================================

        /// <summary>
        /// 根據世界射擊方向（<paramref name="src"/> → <paramref name="dest"/>）
        /// 決定砲彈從 <paramref name="map"/> 的哪條邊緣進入。
        ///
        /// 砲彈從來源方向飛來，因此進入邊緣 = 地圖上朝向來源 tile 的那一側
        /// （即 dest→src 的方位角，與出發方向相反）。
        /// 邊緣格的沿邊座標使用目標格的對應分量，確保砲彈飛向正確位置。
        /// </summary>
        public static IntVec3 CalcEntryEdgeCell(Map map, IntVec3 target, PlanetTile src, PlanetTile dest)
        {
            // 砲彈從 src 飛來 → 進入邊緣朝向 src，即 dest→src 方向；沿真實方位從目標格往外找邊緣，
            // 進場航線才與發射方位一致（EdgeCellFromAngle 只取四個邊、航線會被拉成正東西南北）。
            float angle = WorldDirectionAngle(dest, src);
            return EdgeCellAlongBearing(map, target.ToVector3Shifted(), angle);
        }

        /// <summary>
        /// 從 <paramref name="origin"/> 沿方位角 <paramref name="angle"/>（0=North/+Z，90=East/+X）
        /// 直線前進，回傳最先碰到的地圖邊緣格（夾在 [0, size-1]）。
        /// </summary>
        public static IntVec3 EdgeCellAlongBearing(Map map, Vector3 origin, float angle)
        {
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            float maxX = map.Size.x - 0.5f, maxZ = map.Size.z - 0.5f;
            float t = float.MaxValue;
            if (dir.x > 1e-4f)  { t = Mathf.Min(t, (maxX - origin.x) / dir.x); }
            if (dir.x < -1e-4f) { t = Mathf.Min(t, (0.5f - origin.x) / dir.x); }
            if (dir.z > 1e-4f)  { t = Mathf.Min(t, (maxZ - origin.z) / dir.z); }
            if (dir.z < -1e-4f) { t = Mathf.Min(t, (0.5f - origin.z) / dir.z); }
            if (t == float.MaxValue || t < 0f) { t = 0f; }
            Vector3 hit = origin + dir * t;
            return new IntVec3(
                Mathf.Clamp(Mathf.FloorToInt(hit.x), 0, map.Size.x - 1),
                0,
                Mathf.Clamp(Mathf.FloorToInt(hit.z), 0, map.Size.z - 1));
        }

        // =====================================================================
        //  齊射排程
        // =====================================================================

        /// <summary>
        /// 將一組齊射請求（依 <see cref="WorldObject_ArtilleryStrike.salvoIntervalTicks"/> 錯開）
        /// 推入地圖組件的佇列。由 <see cref="WorldObject_ArtilleryStrike.Arrived"/> 呼叫。
        /// </summary>
        public static void QueueSalvo(Map map, WorldObject_ArtilleryStrike src)
        {
            MapComponent_ArtilleryStrikes comp = map.GetComponent<MapComponent_ArtilleryStrikes>();
            if (comp == null)
            {
                Log.Error("[DMSE Artillery] 地圖上缺少 MapComponent_ArtilleryStrikes，無法排程砲擊。");
                return;
            }

            // 用球面方向計算進入邊緣（src.InitialTile = 發射地，src.destinationTile = 目標地）
            IntVec3 entryEdgeCell = CalcEntryEdgeCell(
                map, src.targetCell, src.InitialTile, src.destinationTile);

            for (int i = 0; i < src.salvoCount; i++)
            {
                comp.Enqueue(new ArtilleryShellRequest
                {
                    fireTick        = Find.TickManager.TicksGame + i * src.salvoIntervalTicks,
                    targetCell      = src.targetCell,
                    entryEdgeCell   = entryEdgeCell,
                    projectileDef   = src.projectileDef,
                    attackerFaction = src.attackerFaction,
                    scatterRadius   = src.scatterRadius,
                    guided          = src.guided,
                });
            }
        }

        // =====================================================================
        //  雷達導引選靶
        // =====================================================================

        /// <summary>
        /// 雷達導引砲彈的瞄準點：<paramref name="map"/> 上敵對 <paramref name="attacker"/> 的目標中，
        /// 先挑砲塔，再挑用電設備，最後挑未倒地、不在迷霧中的人形／機械；同級取最靠近 <paramref name="fallback"/> 者。
        /// 沒有攻擊方陣營或找不到目標時回傳 <paramref name="fallback"/>。
        /// </summary>
        public static IntVec3 FindGuidedTarget(Map map, Faction attacker, IntVec3 fallback)
        {
            if (map == null || attacker == null) { return fallback; }

            IntVec3 best = IntVec3.Invalid;
            int bestTier = int.MaxValue;
            float bestDist = float.MaxValue;

            // 牆、地板、管線等沒有打擊價值：只挑砲塔與用電設備（雷達、發射架、發電機等會被雷達看到的目標）
            void ConsiderBuilding(Building b)
            {
                if (b is Building_Turret) { Consider(b, 0); }
                else if (b.TryGetComp<CompPowerTrader>() != null) { Consider(b, 1); }
            }

            void Consider(Thing t, int tier)
            {
                if (t == null || !t.Spawned || t.Faction == null || !t.HostileTo(attacker)) { return; }
                if (t.Position.Fogged(map) && !(t is Building)) { return; }
                float dist = (t.Position - fallback).LengthHorizontalSquared;
                if (tier < bestTier || (tier == bestTier && dist < bestDist))
                {
                    best = t.Position; bestTier = tier; bestDist = dist;
                }
            }

            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                ConsiderBuilding(b);
            }
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                ConsiderBuilding(b);
            }
            if (bestTier > 1)
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (!p.Downed) { Consider(p, 2); }
                }
            }
            return best.IsValid ? best : fallback;
        }

        // =====================================================================
        //  砲彈生成
        // =====================================================================

        /// <summary>
        /// 立即在地圖上生成一枚 <see cref="Projectile"/>，從邊緣格朝目標格飛行。
        /// 若散佈半徑 &gt; 0，目標格會在呼叫時隨機偏移後固定（保證同一發彈落在確定位置）。
        /// </summary>
        public static void SpawnShell(ArtilleryShellRequest req, Map map)
        {
            if (req.projectileDef == null || map == null) { return; }

            IntVec3 entry = req.entryEdgeCell;
            if (!entry.InBounds(map))
            {
                Log.Warning($"[DMSE Artillery] entryEdgeCell {entry} 超出地圖邊界，已跳過。");
                return;
            }

            // 雷達導引：以當下的敵方目標為中心（目標可能在齊射間被摧毀，所以每發重新挑）
            IntVec3 finalTarget = req.guided
                ? FindGuidedTarget(map, req.attackerFaction, req.targetCell)
                : req.targetCell;

            // 散佈計算：以目標格為中心隨機偏移
            if (req.scatterRadius > 0f)
            {
                Vector2 scatter = Rand.InsideUnitCircle * req.scatterRadius;
                finalTarget += new IntVec3(Mathf.RoundToInt(scatter.x), 0, Mathf.RoundToInt(scatter.y));
                finalTarget  = new IntVec3(
                    Mathf.Clamp(finalTarget.x, 0, map.Size.x - 1),
                    0,
                    Mathf.Clamp(finalTarget.z, 0, map.Size.z - 1));
            }

            // 生成 Projectile，從邊緣格朝目標格發射
            Projectile proj = (Projectile)GenSpawn.Spawn(req.projectileDef, entry, map);
            proj.Launch(
                launcher:            null,
                origin:              entry.ToVector3Shifted(),
                usedTarget:          new LocalTargetInfo(finalTarget),
                intendedTarget:      new LocalTargetInfo(finalTarget),
                hitFlags:            ProjectileHitFlags.IntendedTarget | ProjectileHitFlags.NonTargetPawns,
                preventFriendlyFire: false,
                equipment:           null
            );

            if (Prefs.DevMode)
            {
                Log.Message(
                    $"[DMSE Artillery] 砲彈生成：entry={entry} → target={finalTarget}，projectile={req.projectileDef.defName}");
            }
        }
    }
}
