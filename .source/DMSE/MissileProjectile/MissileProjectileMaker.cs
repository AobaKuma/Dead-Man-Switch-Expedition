using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈 Projectile 的集中生成點（docs/Missile_Skyfaller轉Projectile_計畫.md §3.1）。
    /// 原版 Projectile 必須 Spawn 後再 Launch 才有起訖點；此處固定兩步的順序，
    /// 並保持與舊 Skyfaller 發射相同的設定時機（SpawnSetup 決定時間軸 → 設定朝向與 angle）。
    /// 發射 def 若仍是舊的 skyfaller（彈體沒有 launchProjectile、退回平台設定），照舊生成 skyfaller。
    /// </summary>
    public static class MissileProjectileMaker
    {
        /// <summary>
        /// 在 <paramref name="cell"/> 生成一枚升空中的導彈，朝向 <paramref name="rotation"/>
        /// （同舊版 <c>faller.Rotation = parent.Rotation; faller.angle = Rotation.AsAngle</c>）。
        /// </summary>
        public static Projectile_MissileBase SpawnAscent(ThingDef def, IntVec3 cell, Map map, Rot4 rotation, Thing launcher)
        {
            Projectile_MissileBase missile = (Projectile_MissileBase)GenSpawn.Spawn(def, cell, map);
            missile.Rotation = rotation;
            missile.angle = rotation.AsAngle;
            missile.Launch(launcher, cell.ToVector3Shifted(), new LocalTargetInfo(cell), new LocalTargetInfo(cell), ProjectileHitFlags.None);
            return missile;
        }

        private static bool IsMissileProjectile(ThingDef def)
            => def?.thingClass != null && typeof(Projectile_MissileLaunch).IsAssignableFrom(def.thingClass);

        /// <summary>
        /// 對地發射：生成升空彈，離場時把 <paramref name="worldObject"/> 加入世界。
        /// 有 <paramref name="heading"/>（世界航跡的出發方位）時，升空中途轉向並沿該方位飛出地圖邊緣（§8.3）。
        /// </summary>
        public static Thing LaunchDeparture(ThingDef def, IntVec3 cell, Map map, Rot4 rotation, Thing launcher, WorldObject worldObject, float? heading = null)
        {
            if (IsMissileProjectile(def))
            {
                Projectile_MissileLaunch missile = (Projectile_MissileLaunch)SpawnAscent(def, cell, map, rotation, launcher);
                missile.worldObject = worldObject;
                if (heading != null) { missile.SetDepartureHeading(heading.Value); }
                return missile;
            }

            ScorerProjectile faller = (ScorerProjectile)SkyfallerMaker.SpawnSkyfaller(def, cell, map);
            faller.Rotation = rotation;
            faller.angle = rotation.AsAngle;
            faller.worldObject = worldObject;
            return faller;
        }

        private static bool IsIncomingProjectile(ThingDef def)
            => def?.thingClass != null && typeof(Projectile_MissileIncoming).IsAssignableFrom(def.thingClass);

        /// <summary>建立（未生成的）落地導彈：新 Projectile 或舊 skyfaller def 皆可；不是導彈的 def 回傳 null。</summary>
        public static Thing MakeIncoming(ThingDef def)
        {
            if (def == null) { return null; }
            Thing thing = IsIncomingProjectile(def) ? ThingMaker.MakeThing(def) : SkyfallerMaker.MakeSkyfaller(def);
            return thing is IIncomingMissile ? thing : null;
        }

        /// <summary>
        /// 在 <paramref name="cell"/> 生成落地導彈。有 <paramref name="entryHeading"/>（世界航跡最後一段在目標的方位）時，
        /// 新 Projectile 從該方位的地圖邊緣飛入（§8.4）；沒有時照原本墜落。回傳的物件實作 <see cref="IIncomingMissile"/>
        /// （呼叫端在第一個 tick 前填 config／attacker）。
        /// </summary>
        public static Thing SpawnIncoming(ThingDef def, IntVec3 cell, Map map, float? entryHeading = null)
        {
            Thing thing = MakeIncoming(def);
            if (thing == null) { return null; }
            if (entryHeading != null && thing is Projectile_MissileIncoming p) { p.SetEntryHeading(entryHeading.Value); }
            return GenSpawn.Spawn(thing, cell, map);
        }

        /// <summary>
        /// 攔截發射：生成升空彈，離場時對 <paramref name="targetId"/> 擲骰（-1 = 純視覺）。
        /// 有 <paramref name="heading"/>（來襲方位）時，升空中途轉向該方位、沿 speedCurve 加速飛出地圖邊緣後才結算。
        /// </summary>
        public static Thing LaunchInterceptor(ThingDef def, IntVec3 cell, Map map, Rot4 rotation, Thing launcher, int targetId, float hitChance, float? heading = null)
        {
            if (IsMissileProjectile(def))
            {
                Projectile_MissileLaunch missile = (Projectile_MissileLaunch)SpawnAscent(def, cell, map, rotation, launcher);
                missile.isInterceptor = true;
                missile.targetId = targetId;
                missile.hitChance = hitChance;
                if (heading != null) { missile.SetDepartureHeading(heading.Value); }
                return missile;
            }

            InterceptProjectile faller = (InterceptProjectile)SkyfallerMaker.SpawnSkyfaller(def, cell, map);
            faller.Rotation = rotation;
            faller.angle = rotation.AsAngle;
            faller.targetId = targetId;
            faller.hitChance = hitChance;
            return faller;
        }

        /// <summary>
        /// 地圖層攔截目標的來襲方位（攔截彈升空後轉向它飛出地圖）：
        /// 導彈 Projectile 用它的進入方位（世界航跡最後一段的方向）；其餘（空投艙、無方位的導彈）用發射點到預測落點的方向；
        /// 落點太近或未知時回傳 null（照原本垂直升空）。
        /// </summary>
        public static float? IncomingBearing(BVRTarget target, IntVec3 from)
        {
            if (target == null || target.id < 0) { return null; }
            if (target.Payload is Projectile_MissileIncoming missile && missile.HasEntryHeading) { return missile.EntryHeading; }
            if (!target.position.IsValid) { return null; }
            Vector3 d = (target.position - from).ToVector3();
            d.y = 0f;
            return d.magnitude >= 3f ? MissileMapEntryUtility.HeadingOf(d) : (float?)null;
        }

        /// <summary>世界層攔截：從發射據點朝世界會合點 <paramref name="meetingPoint"/> 的方位；跨圖層或同點時 null。</summary>
        public static float? WorldInterceptBearing(PlanetTile launchTile, Vector3 meetingPoint)
        {
            if (!launchTile.Valid) { return null; }
            Vector3 start = MissileWorldUtility.TileCenter(launchTile);
            if (!MissileMapEntryUtility.HasHorizontalHeading(launchTile.Layer, launchTile.Layer, start, meetingPoint)) { return null; }
            return MissileMapEntryUtility.DepartureHeading(launchTile.Layer, start, meetingPoint);
        }

        /// <summary>
        /// 世界航跡在發射地的出發方位（<paramref name="from"/> → <paramref name="to"/>）；
        /// 跨圖層或幾乎同點時沒有水平方位，回傳 null（離場彈照原本升空後消失）。
        /// </summary>
        public static float? DepartureHeading(PlanetTile from, PlanetTile to)
        {
            if (!from.Valid || !to.Valid) { return null; }
            Vector3 start = MissileWorldUtility.TileCenter(from);
            Vector3 end = MissileWorldUtility.TileCenter(to);
            if (!MissileMapEntryUtility.HasHorizontalHeading(from.Layer, to.Layer, start, end)) { return null; }
            return MissileMapEntryUtility.DepartureHeading(from.Layer, start, end);
        }

        /// <summary>
        /// 以 <paramref name="def"/> 從 <paramref name="cell"/> 朝 <paramref name="heading"/> 離場時，到飛出地圖的 tick 數。
        /// 新發射 Projectile 用與實際飛行相同的模擬；舊 skyfaller 為升空動畫長度。
        /// </summary>
        public static int DepartureTicksOf(ThingDef def, IntVec3 cell, IntVec3 mapSize, Rot4 rotation, float? heading)
        {
            MissileFlightExtension flight = def?.GetModExtension<MissileFlightExtension>();
            if (flight == null || !IsMissileProjectile(def)) { return DepartureTicksOf(def); }
            // 波次打擊規劃可能每幀為每枚彈估算：結果只取決於下列輸入，快取起來。
            var key = (def, cell, mapSize, rotation.AsInt, heading.HasValue ? Mathf.RoundToInt(heading.Value * 100f) : int.MinValue);
            if (!departureTicksCache.TryGetValue(key, out int ticks))
            {
                if (departureTicksCache.Count > 4096) { departureTicksCache.Clear(); }
                ticks = MissileDepartureKinematics.SimulateDepartureTicks(flight, cell, mapSize, rotation, heading);
                departureTicksCache[key] = ticks;
            }
            return ticks;
        }

        private static readonly System.Collections.Generic.Dictionary<(ThingDef, IntVec3, IntVec3, int, int), int> departureTicksCache
            = new System.Collections.Generic.Dictionary<(ThingDef, IntVec3, IntVec3, int, int), int>();

        /// <summary>升空到離開地圖的 tick 數（不知道方位時的時間軸估算）：新發射 Projectile 讀 <see cref="MissileFlightExtension"/>，舊 skyfaller 讀 &lt;skyfaller&gt;。</summary>
        public static int DepartureTicksOf(ThingDef def)
        {
            if (def == null) { return 0; }
            MissileFlightExtension flight = def.GetModExtension<MissileFlightExtension>();
            if (flight != null)
            {
                return flight.reversed ? Mathf.RoundToInt(flight.ticksToDiscardInReverse.Average) : 0;
            }
            SkyfallerProperties sf = def.skyfaller;
            return sf != null && sf.reversed ? Mathf.RoundToInt(sf.ticksToDiscardInReverse.Average) : 0;
        }
    }
}
