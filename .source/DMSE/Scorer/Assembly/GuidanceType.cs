using System.Collections.Generic;
using System.Linq;
using RimWorld;
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
                IntVec3 result = guidance.PickTarget(N, map, attacker);
                if (result.IsValid && result.InBounds(map)) { return result; }
            }
            return InertialTarget(map, attacker);
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

        /// <summary>目標地圖上屬於攻擊方敵人的建築（玩家與非玩家建築都會檢查）。</summary>
        public static IEnumerable<Building> HostileBuildings(Map map, Faction attacker)
        {
            List<Building> colonist = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < colonist.Count; i++)
            {
                if (IsHostileTarget(colonist[i].Faction, attacker)) { yield return colonist[i]; }
            }
            List<Building> others = map.listerBuildings.allBuildingsNonColonist;
            for (int i = 0; i < others.Count; i++)
            {
                if (IsHostileTarget(others[i].Faction, attacker)) { yield return others[i]; }
            }
        }

        /// <summary>標準慣性制導：隨機一棟敵對建築，次選隨機一名敵對 Pawn，最後取地圖中心。</summary>
        public static IntVec3 InertialTarget(Map map, Faction attacker)
        {
            if (HostileBuildings(map, attacker).TryRandomElement(out Building building))
            {
                return building.Position;
            }
            if (map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Dead && IsHostileTarget(p.Faction, attacker))
                .TryRandomElement(out Pawn pawn))
            {
                return pawn.Position;
            }
            return map.Center;
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
            return best != null ? best.Position : IntVec3.Invalid;
        }

        /// <summary>建築目前的耗電量（瓦）；未通電或為發電設施時為 0。</summary>
        public static float PowerDraw(Building b)
        {
            CompPowerTrader power = b.TryGetComp<CompPowerTrader>();
            if (power == null || !power.PowerOn) { return 0f; }
            return power.PowerOutput < 0f ? -power.PowerOutput : 0f;
        }
    }

    // ============================================================
    //  慣性制導（Inertial）
    //  隨機一棟敵對建築，散布由 config.Scatter 決定。
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
    //  鎖定開機（Active）的敵對雷達建築中功率等級最高者；同級再比耗電量。
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

            if (bestRadar != null) { return bestRadar.Position; }
            return HighestPowerDrawTarget(map, attacker);
        }

        /// <summary>建築上開機且未豁免的雷達中最高的功率等級；沒有則 -1。</summary>
        private static int RadarPowerLevel(Building b)
        {
            int level = -1;
            CompSearchRadar sr = b.TryGetComp<CompSearchRadar>();
            if (sr != null && sr.Active && !sr.Props.immuneToAntiRadiationSeeker)
            {
                level = sr.Props.powerLevel;
            }
            CompFireControlRadar fc = b.TryGetComp<CompFireControlRadar>();
            if (fc != null && fc.Active && !fc.Props.immuneToAntiRadiationSeeker && fc.Props.powerLevel > level)
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
                if (p.Dead) { continue; }
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
                float temp = room.Temperature;
                if (temp >= threshold && temp > hottestTemp)
                {
                    hottestTemp = temp;
                    hottest = room;
                }
            }
            if (hottest == null) { return IntVec3.Invalid; }

            if (hottest.Cells.Where(c => c.Standable(map)).TryRandomElement(out IntVec3 cell))
            {
                return cell;
            }
            return hottest.Cells.FirstOrFallback(IntVec3.Invalid);
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
            return mark != null ? mark.Position : IntVec3.Invalid;
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
}
