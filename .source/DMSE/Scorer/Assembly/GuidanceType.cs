using System;
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
    /// 實際鎖定（<see cref="ArrivalCell"/>、盤旋重新搜尋）會經 <see cref="SpreadBest{T}"/> 分散：
    /// 密集來襲的同類導引頭導彈不會全部鎖在同一個目標上。
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

        /// <summary>資訊卡用的選目標機制說明；null = 不顯示。</summary>
        public virtual string MechanismDescription => null;

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
            if (TryPickTarget(config, map, attacker, out IntVec3 cell)) { return cell; }
            return InertialTarget(map, attacker);
        }

        /// <summary>
        /// 只執行導引頭本身：回傳 true 表示找到符合的目標（<paramref name="cell"/> 為其格），
        /// 回傳 false 表示導引頭沒有目標（或根本沒有導引頭），由呼叫端決定要不要退化為慣性。
        /// 盤旋飛控靠這個方法分辨「導引頭找不到目標」與「找到目標」。
        /// </summary>
        /// <param name="claim">
        /// true = 導彈實際鎖定（抵達、盤旋重新搜尋）：避開同類導引頭最近已鎖定的目標，並登記這次鎖定
        /// （見 <see cref="SpreadBest{T}"/>）。false = 只查詢，永遠回傳最佳目標、不留紀錄。
        /// </param>
        public static bool TryPickTarget(MissileConfig config, Map map, Faction attacker, out IntVec3 cell, bool claim = false)
        {
            cell = IntVec3.Invalid;
            GuidanceType guidance = config?.TargetingGuidance;
            if (guidance == null || map == null) { return false; }
            bool previous = targetsUnderThickRoof;
            GuidanceType previousClaimer = claimingGuidance;
            targetsUnderThickRoof = config.PenetratesThickRoof;
            claimingGuidance = claim ? guidance : null;
            try
            {
                IntVec3 result = guidance.PickTarget(config.PayloadCapacity, map, attacker);
                if (result.IsValid && result.InBounds(map))
                {
                    cell = result;
                    return true;
                }
            }
            finally
            {
                targetsUnderThickRoof = previous;
                claimingGuidance = previousClaimer;
            }
            return false;
        }

        /// <summary>
        /// 抵達目標地圖時的落點：導引頭找到目標就用它；找不到且彈體裝了盤旋飛控時，
        /// 回傳慣性落點並把 <paramref name="loiter"/> 設為 true，由呼叫端改為盤旋等待；
        /// 其餘情況直接回退慣性（與 <see cref="PickCell"/> 相同）。
        /// </summary>
        public static IntVec3 ArrivalCell(MissileConfig config, Map map, Faction attacker, out bool loiter)
        {
            loiter = false;
            if (TryPickTarget(config, map, attacker, out IntVec3 cell, claim: true)) { return cell; }
            if (config != null && config.LoiterTicks > 0) { loiter = true; }
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

        // ---- 共用：分散鎖定 ----

        /// <summary>同一目標被鎖定後，同類導引頭在這段時間內會優先改選其他目標（ticks）。</summary>
        public const int ClaimWindowTicks = 1200;

        private struct Claim
        {
            public Type guidance;
            public object target;
            public int tick;
        }

        /// <summary>最近的實際鎖定紀錄（僅執行期，不存檔；過期即清除）。</summary>
        private static readonly List<Claim> claims = new List<Claim>();

        /// <summary><see cref="TryPickTarget"/> 以 claim 模式執行期間的導引頭；其餘時候為 null（不分散、不登記）。</summary>
        private static GuidanceType claimingGuidance;

        /// <summary>清除所有鎖定紀錄（測試用）。</summary>
        public static void ClearClaims() => claims.Clear();

        private static int ClaimCount(Type guidance, object target)
        {
            int count = 0;
            for (int i = 0; i < claims.Count; i++)
            {
                if (claims[i].guidance == guidance && ReferenceEquals(claims[i].target, target)) { count++; }
            }
            return count;
        }

        /// <summary>
        /// 從候選中挑目標：<paramref name="better"/>(a, b) 為 true 表示 a 嚴格優於 b，同分保留先出現者。
        /// 只查詢時直接回傳最佳者；實際鎖定時先比「同類導引頭在 <see cref="ClaimWindowTicks"/> 內已鎖定的次數」，
        /// 次數少者優先、再比優劣，並登記這次鎖定——密集來襲的同類導彈因此依序分散到次佳目標，
        /// 所有候選都被鎖過後再輪回最佳者。
        /// </summary>
        protected static T SpreadBest<T>(IEnumerable<T> candidates, Func<T, T, bool> better) where T : class
        {
            Type guidance = claimingGuidance?.GetType();
            int now = Find.TickManager?.TicksGame ?? 0;
            if (guidance != null)
            {
                // 過期或時間倒退（讀入其他存檔）的紀錄一併清除。
                claims.RemoveAll(c => now - c.tick >= ClaimWindowTicks || now < c.tick);
            }

            T best = null;
            int bestClaims = int.MaxValue;
            foreach (T item in candidates)
            {
                int count = guidance != null ? ClaimCount(guidance, item) : 0;
                if (best == null || count < bestClaims || (count == bestClaims && better(item, best)))
                {
                    best = item;
                    bestClaims = count;
                }
            }
            if (best != null && guidance != null)
            {
                claims.Add(new Claim { guidance = guidance, target = best, tick = now });
            }
            return best;
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
            Building largest = SpreadBest(HostileBuildings(map, attacker),
                (a, b) => a.def.size.x * a.def.size.z > b.def.size.x * b.def.size.z);
            if (largest != null) { return AimCell(largest, map); }
            return BiggestPawnCell(map, attacker);
        }

        /// <summary>體型最大、打得到的敵對單位所在格；沒有則 Invalid。</summary>
        public static IntVec3 BiggestPawnCell(Map map, Faction attacker)
        {
            Pawn biggest = SpreadBest(map.mapPawns.AllPawnsSpawned
                    .Where(p => !p.Dead && IsHostileTarget(p.Faction, attacker) && Strikable(p.Position, map)),
                (a, b) => a.BodySize > b.BodySize);
            return biggest != null ? biggest.Position : IntVec3.Invalid;
        }

        /// <summary>電磁訊號：耗電量最大的運作中敵對建築；沒有則回傳 Invalid。</summary>
        public static IntVec3 HighestPowerDrawTarget(Map map, Faction attacker)
        {
            Building best = SpreadBest(HostileBuildings(map, attacker).Where(b => PowerDraw(b) > 0f),
                (a, b) => PowerDraw(a) > PowerDraw(b));
            return best != null ? AimCell(best, map) : IntVec3.Invalid;
        }

        /// <summary>耐久最高的敵對建築（反艦導引頭用）；沒有則回傳 Invalid。</summary>
        public static IntVec3 ToughestHostileBuilding(Map map, Faction attacker)
        {
            Building best = SpreadBest(HostileBuildings(map, attacker), (a, b) => a.MaxHitPoints > b.MaxHitPoints);
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

        public override string MechanismDescription => "DMSE.Card.Guidance.Inertial".Translate();

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  反輻射（Anti-Radiation）
    //  鎖定開機（ActiveForSeeker：Active，或電網正要開啟）的敵對雷達建築中功率等級最高者；同級再比耗電量。
    //  標記 immuneToAntiRadiationSeeker 的雷達（純被動聲學預警）不列入。
    //  找不到時預設退化為電磁訊號（fallBackToEmissions），再找不到由 PickCell 退化為慣性；
    //  巡飛彈的專用尋標頭把 fallBackToEmissions 設為 false，只回報雷達，沒有雷達就交給盤旋飛控等待開機。
    // ============================================================
    public class GuidanceType_AntiRadiation : GuidanceType
    {
        /// <summary>
        /// 找不到開機雷達時是否退化為「耗電最大的運作中建築」（電磁訊號）。
        /// false = 只看雷達，沒有雷達就回報沒有目標（配合盤旋飛控在目標上空等待）。
        /// </summary>
        public bool fallBackToEmissions = true;

        public override string MechanismDescription
            => "DMSE.Card.Guidance.AntiRadiation".Translate() + " "
               + (fallBackToEmissions ? "DMSE.Card.Guidance.AntiRadiationFallback" : "DMSE.Card.Guidance.AntiRadiationNoFallback").Translate();

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            IntVec3 radar = BestRadarCell(map, attacker);
            if (radar.IsValid) { return radar; }
            return fallBackToEmissions ? HighestPowerDrawTarget(map, attacker) : IntVec3.Invalid;
        }

        /// <summary>開機且可被反輻射尋標頭鎖定的敵對雷達中功率等級最高者；同級再比耗電。沒有則 Invalid。</summary>
        public static IntVec3 BestRadarCell(Map map, Faction attacker)
        {
            Building bestRadar = SpreadBest(HostileBuildings(map, attacker).Where(b => RadarPowerLevel(b) >= 0),
                (a, b) =>
                {
                    int pa = RadarPowerLevel(a), pb = RadarPowerLevel(b);
                    return pa > pb || (pa == pb && PowerDraw(a) > PowerDraw(b));
                });
            return bestRadar != null ? AimCell(bestRadar, map) : IntVec3.Invalid;
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

        public override string MechanismDescription
            => "DMSE.Card.Guidance.HeatSource".Translate(minRoomTempAboveOutdoor.ToStringTemperatureOffset());

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            IntVec3 room = HottestRoomCell(map);
            if (room.IsValid) { return room; }

            Pawn biggest = SpreadBest(map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && Strikable(p.Position, map)
                    && (attacker == null || p.Faction == null || IsHostileTarget(p.Faction, attacker))),
                (a, b) => a.BodySize > b.BodySize);
            return biggest != null ? biggest.Position : IntVec3.Invalid;
        }

        private IntVec3 HottestRoomCell(Map map)
        {
            float threshold = map.mapTemperature.OutdoorTemp + minRoomTempAboveOutdoor;
            Room hottest = SpreadBest(map.regionGrid.AllRooms.Where(room => !room.PsychologicallyOutdoors
                    && !room.UsesOutdoorTemperature && room.Temperature >= threshold && room.Cells.Any(c => Strikable(c, map))),
                (a, b) => a.Temperature > b.Temperature);
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
        public override string MechanismDescription => "DMSE.Card.Guidance.ElectromagneticSignal".Translate();

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

        public override string MechanismDescription => "DMSE.Card.Guidance.TV".Translate();

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
        public override string MechanismDescription => "DMSE.Card.Guidance.Laser".Translate();

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
        public override string MechanismDescription => "DMSE.Card.Guidance.FireControl".Translate();

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
        public override string MechanismDescription => "DMSE.Card.Guidance.RadioCommand".Translate();
    }

    // ============================================================
    //  自動影像尋標（Imaging Seeker）
    //  只選屋頂外的敵方生物，先按戰力、再按體型；完全同分時以 Thing ID 穩定排序。
    // ============================================================
    public class GuidanceType_Imaging : GuidanceType
    {
        public override string MechanismDescription => "DMSE.Card.Guidance.Imaging".Translate();

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            Pawn best = SpreadBest(map.mapPawns.AllPawnsSpawned.Where(pawn => pawn.Spawned && !pawn.Dead
                    && IsHostileTarget(pawn.Faction, attacker) && !map.roofGrid.Roofed(pawn.Position) && Strikable(pawn.Position, map)),
                (a, b) =>
                {
                    float ta = a.kindDef?.combatPower ?? 0f, tb = b.kindDef?.combatPower ?? 0f;
                    if (ta != tb) { return ta > tb; }
                    if (a.BodySize != b.BodySize) { return a.BodySize > b.BodySize; }
                    return a.thingIDNumber < b.thingIDNumber;
                });
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
        public override string MechanismDescription => "DMSE.Card.Guidance.ActiveRadar".Translate();

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

        public override string MechanismDescription => "DMSE.Card.Guidance.AntiShip".Translate(ExtraTerminalUpdates);

        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            IntVec3 building = ToughestHostileBuilding(map, attacker);
            if (building.IsValid) { return building; }
            return BiggestPawnCell(map, attacker);
        }
    }
}
