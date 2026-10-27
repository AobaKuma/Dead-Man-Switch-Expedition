using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導引頭行為的抽象基底類別。
    /// 在 MissilePartDef.guidanceType 欄位中以 XML Class 屬性指定子類別，例如：
    /// <c>&lt;guidanceType Class="DMSE.GuidanceType_TV" /&gt;</c>；
    /// 沒有導引槽的彈體可用 <see cref="MissileBodyDef.builtInGuidance"/> 指定選目標方式。
    ///
    /// 導彈抵達目標地圖時決定落點（<see cref="PickCell"/>）：
    ///   - 敵方與玩家發射一律由 <see cref="PickTarget"/> 自動鎖定；
    ///   - 唯一例外是 <see cref="PlayerChoosesTarget"/> 為 true 的導引頭（電視制導）：玩家發射時暫停遊戲讓玩家選點，
    ///     敵方發射時退化為慣性。
    /// 找不到符合的目標時回傳 <see cref="IntVec3.Invalid"/>，由 <see cref="PickCell"/> 退化為慣性邏輯。
    /// </summary>
    public abstract class GuidanceType
    {
        /// <summary>
        /// 選擇落點格。
        /// </summary>
        /// <param name="N">載荷容量係數。</param>
        /// <param name="map">目標地圖。</param>
        /// <param name="attacker">發射方陣營（null = 未知）。</param>
        /// <returns>目標格；回傳 <see cref="IntVec3.Invalid"/> 時由呼叫端退化為預設慣性邏輯。</returns>
        public abstract IntVec3 PickTarget(float N, Map map, Faction attacker);

        /// <summary>
        /// 是否具備導引能力：可跨圖層打擊，並可依彈體的 terminalUpdates 追蹤移動目標。
        /// 慣性制導覆寫為 false。
        /// </summary>
        public virtual bool TerminalGuidance => true;

        /// <summary>玩家發射時是否由玩家手動選擇落點（電視制導）。其餘導引頭一律自動鎖定。</summary>
        public virtual bool PlayerChoosesTarget => false;

        /// <summary>此導引頭額外提供的終端位置更新次數（反艦的主動雷達比一般導引頭更常修正）。</summary>
        public virtual int ExtraTerminalUpdates => 0;

        /// <summary>是否以世界層移動目標為對象（反艦）：航跡會縮短終端檢查間隔。</summary>
        public virtual bool TracksMobileTargets => false;

        /// <summary>可選的 Def 驗證。</summary>
        public virtual IEnumerable<string> ConfigErrors(MissilePartDef parent)
        {
            yield break;
        }

        // ---- 落點選擇入口 ----

        /// <summary>
        /// 依導彈設定的導引頭（<see cref="MissileConfig.TargetingGuidance"/>）在目標地圖上自動選擇落點；
        /// 導引頭找不到目標或沒有導引頭時退化為慣性制導。
        /// </summary>
        public static IntVec3 PickCell(MissileConfig config, Map map, Faction attacker)
        {
            float N = config?.PayloadCapacity ?? 5f;
            GuidanceType guidance = config?.TargetingGuidance;
            if (guidance != null)
            {
                bool previous = targetsUnderThickRoof;
                targetsUnderThickRoof = config.PenetratesThickRoof;
                try
                {
                    IntVec3 result = guidance.PickTarget(N, map, attacker);
                    if (result.IsValid && result.InBounds(map)) { return result; }
                }
                finally
                {
                    targetsUnderThickRoof = previous;
                }
            }
            return InertialTarget(map, attacker);
        }

        // ---- 共用：厚岩頂 ----

        /// <summary>
        /// <see cref="PickCell"/> 執行期間：導彈能否穿透厚岩頂（<see cref="MissileConfig.PenetratesThickRoof"/>）。
        /// 不能穿透時導引頭不鎖定厚岩頂下的目標（打上去只會在岩層表面引爆）。PickCell 之外為 false。
        /// </summary>
        private static bool targetsUnderThickRoof;

        /// <summary>導引頭可否以該格為落點：能穿透厚岩頂，或該格不在厚岩頂下。</summary>
        public static bool Strikable(IntVec3 cell, Map map)
            => targetsUnderThickRoof || !MissileImpactUtility.UnderThickRoof(cell, map);

        /// <summary>
        /// 對物件的瞄準格：<see cref="Thing.Position"/> 可打時用它，否則用占地中任一不在厚岩頂下的格
        /// （半埋在山裡的大型建築仍可從露出的部分打）；整個都在厚岩頂下則為 Invalid。
        /// </summary>
        public static IntVec3 AimCell(Thing t, Map map)
        {
            if (Strikable(t.Position, map)) { return t.Position; }
            foreach (IntVec3 cell in t.OccupiedRect())
            {
                if (cell.InBounds(map) && Strikable(cell, map)) { return cell; }
            }
            return IntVec3.Invalid;
        }

        // ---- 共用：目標篩選 ----

        /// <summary>
        /// 是否為攻擊方的打擊對象：有陣營、不是攻擊方本身，且與攻擊方敵對。
        /// 攻擊方未知時，任何有陣營者都算。
        /// </summary>
        public static bool IsHostileTarget(Faction owner, Faction attacker)
        {
            if (owner == null) { return false; }
            if (attacker == null) { return true; }
            return owner != attacker && owner.HostileTo(attacker);
        }

        /// <summary>目標地圖上屬於攻擊方敵人、且導引頭打得到（<see cref="AimCell"/>）的建築（玩家與非玩家建築都會檢查）。</summary>
        public static IEnumerable<Building> HostileBuildings(Map map, Faction attacker)
        {
            List<Building> colonist = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < colonist.Count; i++)
            {
                if (IsHostileTarget(colonist[i].Faction, attacker) && AimCell(colonist[i], map).IsValid) { yield return colonist[i]; }
            }
            List<Building> others = map.listerBuildings.allBuildingsNonColonist;
            for (int i = 0; i < others.Count; i++)
            {
                if (IsHostileTarget(others[i].Faction, attacker) && AimCell(others[i], map).IsValid) { yield return others[i]; }
            }
        }

        /// <summary>慣性制導不鎖敵：以地圖中心為均值，在內切圓中取二維截斷正態分布。</summary>
        public static IntVec3 InertialTarget(Map map, Faction attacker)
        {
            return InertialTarget(map.Size);
        }

        /// <summary>圓半徑為中心到最近邊界的距離；各軸標準差為半徑的三分之一。</summary>
        public static IntVec3 InertialTarget(IntVec3 mapSize)
        {
            IntVec3 center = new IntVec3(mapSize.x / 2, 0, mapSize.z / 2);
            int radius = Mathf.Min(Mathf.Min(center.x, mapSize.x - 1 - center.x),
                Mathf.Min(center.z, mapSize.z - 1 - center.z));
            if (radius <= 0) { return center; }
            float sigma = radius / 3f;
            // Reject outside the circle instead of clamping, which would pile hits onto its edge.
            for (int i = 0; i < 128; i++)
            {
                int x = Mathf.RoundToInt(Rand.Gaussian(0f, sigma));
                int z = Mathf.RoundToInt(Rand.Gaussian(0f, sigma));
                if ((double)x * x + (double)z * z <= (double)radius * radius)
                {
                    return new IntVec3(center.x + x, 0, center.z + z);
                }
            }
            return center;
        }

        /// <summary>主動雷達依占地面積鎖定最大敵對建築；沒有建築時鎖定體型最大的敵對單位。</summary>
        public static IntVec3 LargestHostileTarget(Map map, Faction attacker)
        {
            Building largest = null;
            int largestArea = -1;
            foreach (Building b in HostileBuildings(map, attacker))
            {
                int area = b.def.size.x * b.def.size.z;
                if (area > largestArea) { largestArea = area; largest = b; }
            }
            if (largest != null) { return AimCell(largest, map); }

            Pawn biggest = null;
            float biggestSize = -1f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || !IsHostileTarget(p.Faction, attacker) || !Strikable(p.Position, map)) { continue; }
                if (p.BodySize > biggestSize) { biggestSize = p.BodySize; biggest = p; }
            }
            return biggest != null ? biggest.Position : IntVec3.Invalid;
        }

        /// <summary>電磁訊號：耗電量最大的運作中敵對建築；沒有則回傳 Invalid。</summary>
        public static IntVec3 HighestPowerDrawTarget(Map map, Faction attacker)
        {
            Building best = null;
            float bestDraw = 0f;
            foreach (Building b in HostileBuildings(map, attacker))
            {
                float draw = PowerDraw(b);
                if (draw > bestDraw) { bestDraw = draw; best = b; }
            }
            return best != null ? AimCell(best, map) : IntVec3.Invalid;
        }

        /// <summary>耐久最高的敵對建築（反艦導引頭用）；沒有則回傳 Invalid。</summary>
        public static IntVec3 ToughestHostileBuilding(Map map, Faction attacker)
        {
            Building best = null;
            float bestHp = -1f;
            foreach (Building b in HostileBuildings(map, attacker))
            {
                if (b.MaxHitPoints > bestHp) { bestHp = b.MaxHitPoints; best = b; }
            }
            return best != null ? AimCell(best, map) : IntVec3.Invalid;
        }

        /// <summary>
        /// 建築目前的耗電量（瓦）；未通電或為發電設施時為 0。
        /// 電網正要開啟的建築（<see cref="PowerStarting"/>）以額定耗電計。
        /// </summary>
        public static float PowerDraw(Building b)
        {
            CompPowerTrader power = b.TryGetComp<CompPowerTrader>();
            if (power == null) { return 0f; }
            if (power.PowerOn) { return power.PowerOutput < 0f ? -power.PowerOutput : 0f; }
            return PowerStarting(power) ? Mathf.Max(0f, power.Props.PowerConsumption) : 0f;
        }

        /// <summary>
        /// 尚未通電、但電網正要開啟它：開關為開、未故障、電網有可用電源且供得起（與原版 <c>PowerNet.PowerNetTick</c> 的開機條件相同）。
        /// 原版電網每 30–200 ticks 才開啟一棟耗電建築，導彈抵達時才生成的地圖在抵達那一 tick 全部建築都處於此狀態；
        /// 導引頭把它們視為運作中，否則反輻射／電磁訊號打未生成地圖的據點時必然退化為慣性。
        /// </summary>
        public static bool PowerStarting(CompPowerTrader power)
        {
            if (power.PowerOn) { return false; }
            PowerNet net = power.PowerNet;
            if (net == null || !net.HasActivePowerSource || !net.CanPowerNow(power)) { return false; }
            ThingWithComps parent = power.parent;
            Map map = parent.MapHeld;
            if (map == null || map.gameConditionManager.ElectricityDisabled(map)) { return false; }
            return FlickUtility.WantsToBeOn(parent) && !parent.IsBrokenDown();
        }
    }

    // ============================================================
    //  慣性制導（Inertial）
    //  地圖中心的圓形截斷正態分布，不搜尋建築或單位。
    //  也是沒有導引部件、導引頭找不到目標時的後備行為。
    // ============================================================
    public class GuidanceType_Inertial : GuidanceType
    {
        // 慣性制導無法跨圖層打擊，也無法追蹤移動目標
        public override bool TerminalGuidance => false;

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  反輻射（Anti-Radiation）
    //  鎖定開機（ActiveForSeeker：Active，或電網正要開啟）的敵對雷達建築中功率等級最高者；同級再比耗電量。
    //  標記 immuneToAntiRadiationSeeker 的雷達（純被動聲學預警）不列入。
    //  找不到則退化為電磁訊號，再找不到由 PickCell 退化為慣性。
    // ============================================================
    public class GuidanceType_AntiRadiation : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            Building bestRadar = null;
            int bestPower = -1;
            float bestDraw = -1f;

            foreach (Building b in HostileBuildings(map, attacker))
            {
                int power = RadarPowerLevel(b);
                if (power < 0) { continue; }
                float draw = PowerDraw(b);
                if (power > bestPower || (power == bestPower && draw > bestDraw))
                {
                    bestPower = power;
                    bestDraw = draw;
                    bestRadar = b;
                }
            }

            if (bestRadar != null) { return AimCell(bestRadar, map); }
            return HighestPowerDrawTarget(map, attacker);
        }

        /// <summary>建築上開機且未豁免的雷達中最高的功率等級；沒有則 -1。</summary>
        private static int RadarPowerLevel(Building b)
        {
            int level = -1;
            CompSearchRadar sr = b.TryGetComp<CompSearchRadar>();
            if (sr != null && sr.ActiveForSeeker && !sr.Props.immuneToAntiRadiationSeeker)
            {
                level = sr.Props.powerLevel;
            }
            CompFireControlRadar fc = b.TryGetComp<CompFireControlRadar>();
            if (fc != null && fc.ActiveForSeeker && !fc.Props.immuneToAntiRadiationSeeker && fc.Props.powerLevel > level)
            {
                level = fc.Props.powerLevel;
            }
            return level;
        }
    }

    // ============================================================
    //  熱訊號（Heat Source）
    //  最熱的室內房間比室外高出 minRoomTempAboveOutdoor 度以上 → 打該房間；
    //  否則打體型最大的 Pawn（不分陣營，攻擊方與其盟友除外，野生動物也會被鎖定）；
    //  都沒有則退化為慣性。
    // ============================================================
    public class GuidanceType_HeatSource : GuidanceType
    {
        /// <summary>房間需比室外溫度高出多少度（°C）才會被選為目標。</summary>
        public float minRoomTempAboveOutdoor = 15f;

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            IntVec3 room = HottestRoomCell(map);
            if (room.IsValid) { return room; }

            Pawn biggest = null;
            float biggestSize = -1f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || !Strikable(p.Position, map)) { continue; }
                if (attacker != null && p.Faction != null && !IsHostileTarget(p.Faction, attacker)) { continue; }
                if (p.BodySize > biggestSize) { biggestSize = p.BodySize; biggest = p; }
            }
            return biggest != null ? biggest.Position : IntVec3.Invalid;
        }

        private IntVec3 HottestRoomCell(Map map)
        {
            float threshold = map.mapTemperature.OutdoorTemp + minRoomTempAboveOutdoor;
            Room hottest = null;
            float hottestTemp = float.MinValue;
            foreach (Room room in map.regionGrid.AllRooms)
            {
                if (room.PsychologicallyOutdoors || room.UsesOutdoorTemperature) { continue; }
                if (!room.Cells.Any(c => Strikable(c, map))) { continue; }
                float temp = room.Temperature;
                if (temp >= threshold && temp > hottestTemp)
                {
                    hottestTemp = temp;
                    hottest = room;
                }
            }
            if (hottest == null) { return IntVec3.Invalid; }

            if (hottest.Cells.Where(c => c.Standable(map) && Strikable(c, map)).TryRandomElement(out IntVec3 cell))
            {
                return cell;
            }
            return hottest.Cells.Where(c => Strikable(c, map)).FirstOrFallback(IntVec3.Invalid);
        }
    }

    // ============================================================
    //  紅外制導（Infrared）— 舊類別名，行為同熱訊號導引頭。
    // ============================================================
    public class GuidanceType_Infrared : GuidanceType_HeatSource
    {
    }

    // ============================================================
    //  電磁訊號（Electromagnetic Signal）
    //  鎖定耗電量最大的運作中敵對建築；找不到則退化為慣性。
    // ============================================================
    public class GuidanceType_ElectromagneticSignal : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return HighestPowerDrawTarget(map, attacker);
        }
    }

    // ============================================================
    //  高能反應制導（High Energy）— 舊類別名，行為同電磁訊號導引頭。
    // ============================================================
    public class GuidanceType_HighEnergy : GuidanceType_ElectromagneticSignal
    {
    }

    // ============================================================
    //  電視制導（TV Guided）
    //  玩家發射時，抵達後暫停遊戲由玩家自由選擇落點。
    //  敵方使用時無人操作，退化為慣性制導。
    // ============================================================
    public class GuidanceType_TV : GuidanceType
    {
        public override bool PlayerChoosesTarget => true;

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  雷射制導（Laser Guided）
    //  鎖定目標地圖上攻擊方自己的雷射引導標點（LaserDesignatorMark），多個時取最近放置的；
    //  沒有標點則退化為慣性。
    // ============================================================
    public class GuidanceType_Laser : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            LaserDesignatorMark mark = LaserDesignatorUtility.LatestMarkFor(map, attacker);
            return mark != null && Strikable(mark.Position, map) ? mark.Position : IntVec3.Invalid;
        }
    }

    // ============================================================
    //  火控雷達制導（Fire Control Radar）
    //  主要語意是「此導彈作為 BVR 攔截彈使用」，攔截邏輯由 CompMissileLauncher_Interceptor 驅動；
    //  對地打擊時以慣性選格（散布較小）。
    // ============================================================
    public class GuidanceType_FireControl : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  無線電指令導引（Radio Command）
    //  地面操作員／火控持續提供修正；沒有改變 BVR 攔截命中模型。
    // ============================================================
    public class GuidanceType_RadioCommand : GuidanceType_FireControl
    {
    }

    // ============================================================
    //  自動影像尋標（Imaging Seeker）
    //  只選屋頂外的敵方生物，先按戰力、再按體型；完全同分時以 Thing ID 穩定排序。
    // ============================================================
    public class GuidanceType_Imaging : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            Pawn best = null;
            float bestThreat = -1f;
            float bestSize = -1f;
            int bestId = int.MaxValue;
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (!pawn.Spawned || pawn.Dead || !IsHostileTarget(pawn.Faction, attacker)
                    || map.roofGrid.Roofed(pawn.Position) || !Strikable(pawn.Position, map)) { continue; }

                float threat = pawn.kindDef?.combatPower ?? 0f;
                float size = pawn.BodySize;
                int id = pawn.thingIDNumber;
                if (threat > bestThreat || (threat == bestThreat && size > bestSize)
                    || (threat == bestThreat && size == bestSize && id < bestId))
                {
                    best = pawn;
                    bestThreat = threat;
                    bestSize = size;
                    bestId = id;
                }
            }
            return best != null ? best.Position : IntVec3.Invalid;
        }
    }

    // ============================================================
    //  主動雷達（Active Radar / MMW）
    //  射後不理：鎖定占地最大的敵對建築，其次為體型最大的敵對單位；找不到則退化為慣性。
    //  與反輻射（打雷達）、電磁訊號（打耗電）、反艦（打耐久）區隔。
    // ============================================================
    public class GuidanceType_ActiveRadar : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return LargestHostileTarget(map, attacker);
        }
    }

    // ============================================================
    //  反艦主動雷達（Anti-Ship）
    //  對「移動中的大地圖目標」：艦上打最硬的敵對建築（引擎／艦橋），沒有建築則打最大的敵對 Pawn。
    //  世界層由 MissileConfig.TracksMobileTargets 驅動更密集的終端修正，次數額外 +2。
    // ============================================================
    public class GuidanceType_AntiShip : GuidanceType
    {
        public override int ExtraTerminalUpdates => 2;

        public override bool TracksMobileTargets => true;

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            IntVec3 building = ToughestHostileBuilding(map, attacker);
            if (building.IsValid) { return building; }

            Pawn biggest = null;
            float biggestSize = -1f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || !IsHostileTarget(p.Faction, attacker) || !Strikable(p.Position, map)) { continue; }
                if (p.BodySize > biggestSize) { biggestSize = p.BodySize; biggest = p; }
            }
            return biggest != null ? biggest.Position : IntVec3.Invalid;
        }
    }
}
