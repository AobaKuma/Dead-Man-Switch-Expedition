using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導引頭行為的抽象基底類別。
    /// 在 MissilePartDef.guidanceType 欄位中以 XML Class 屬性指定子類別，例如：
    /// <c>&lt;guidanceType Class="DMSE.GuidanceType_TV" /&gt;</c>
    ///
    /// 由 WorldObject_IncomingMissile.PickTargetCell() 呼叫以選擇落點格。
    /// 玩家發射（Scorer 流程）時玩家已在世界地圖選靶，目前不重新選格。
    /// 電視制導在敵方使用時退化為慣性制導（由子類別負責處理）。
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

        /// <summary>可選的 Def 驗證。</summary>
        public virtual IEnumerable<string> ConfigErrors(MissilePartDef parent)
        {
            yield break;
        }

        // ---- 共用靜態輔助 ----

        /// <summary>標準慣性制導：隨機敵對建築，次選隨機 Pawn，最後取地圖中心。</summary>
        public static IntVec3 InertialTarget(Map map, Faction attacker)
        {
            // 建築：優先選玩家陣營（或非攻擊方）的建築
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            if (attacker != null && !attacker.IsPlayer)
            {
                // 攻擊方非玩家 → 打玩家建築
                if (buildings != null && buildings.Count > 0)
                {
                    return buildings.RandomElement().Position;
                }
            }
            else
            {
                // 攻擊方是玩家（或未知）→ 打非玩家建築
                List<Building> enemyBuildings = map.listerBuildings.allBuildingsColonist
                    .Where(b => b.Faction != null && b.Faction != attacker)
                    .ToList();
                if (enemyBuildings.Count > 0) { return enemyBuildings.RandomElement().Position; }
            }

            // 次選殖民者 Pawn
            if (buildings != null && buildings.Count > 0)
            {
                return buildings.RandomElement().Position;
            }

            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
            if (pawns != null && pawns.Count > 0)
            {
                return pawns.RandomElement().Position;
            }

            return map.Center;
        }
    }

    // ============================================================
    //  慣性制導（Inertial）
    //  默認：隨機敵對建築，Scatter 由 config.Scatter 決定（可為正值）。
    //  此為所有無導引部件時的後備行為，通常不需要顯式指定。
    // ============================================================
    public class GuidanceType_Inertial : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  高能反應制導（High Energy Reactive）
    //  尋找地圖中發電量最大的敵對建築；找不到則慣性。
    // ============================================================
    public class GuidanceType_HighEnergy : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            Building best = null;
            float bestPower = float.MinValue;

            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (attacker != null && b.Faction == attacker) { continue; }
                CompPowerTrader power = b.TryGetComp<CompPowerTrader>();
                if (power == null || !power.PowerOn) { continue; }
                float output = power.PowerOutput;
                if (output > bestPower) { bestPower = output; best = b; }
            }

            return best != null ? best.Position : InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  熱源制導（Heat Source）
    //  體型最大的 Pawn；次選最熱的 Room；找不到則慣性。
    // ============================================================
    public class GuidanceType_HeatSource : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            // 體型最大的非攻擊方 Pawn
            Pawn biggestPawn = null;
            float biggestSize = -1f;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead) { continue; }
                if (attacker != null && p.Faction == attacker) { continue; }
                float sz = p.BodySize;
                if (sz > biggestSize) { biggestSize = sz; biggestPawn = p; }
            }
            if (biggestPawn != null) { return biggestPawn.Position; }

            // 最熱的 Room
            Room hottestRoom = null;
            float hottestTemp = float.MinValue;
            foreach (Room room in map.regionGrid.AllRooms)
            {
                if (room.PsychologicallyOutdoors) { continue; }
                float temp = room.Temperature;
                if (temp > hottestTemp) { hottestTemp = temp; hottestRoom = room; }
            }
            if (hottestRoom != null)
            {
                IntVec3 roomCell = hottestRoom.Cells.RandomElement();
                if (roomCell.IsValid) { return roomCell; }
            }

            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  反輻射（Anti-Radiation）
    //  尋找地圖中處於作用狀態的雷達建築（CompSearchRadar/CompFireControlRadar），
    //  以功率排序優先；找不到則高能反應制導，再找不到則慣性。
    // ============================================================
    public class GuidanceType_AntiRadiation : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            Building bestRadar = null;
            int bestPower = -1;

            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfClass<Building>())
            {
                if (attacker != null && b.Faction == attacker) { continue; }

                // 搜索雷達
                CompSearchRadar sr = b.TryGetComp<CompSearchRadar>();
                if (sr != null && sr.Active)
                {
                    int p = sr.Props.powerLevel;
                    if (p > bestPower) { bestPower = p; bestRadar = b; }
                    continue;
                }
                // 火控雷達
                CompFireControlRadar fc = b.TryGetComp<CompFireControlRadar>();
                if (fc != null && fc.Active)
                {
                    int p = fc.Props.powerLevel;
                    if (p > bestPower) { bestPower = p; bestRadar = b; }
                }
            }

            if (bestRadar != null) { return bestRadar.Position; }

            // 退化到高能反應
            return new GuidanceType_HighEnergy().PickTarget(N, map, attacker);
        }
    }

    // ============================================================
    //  紅外制導（Infrared）
    //  若落點地圖上有玩家殖民者，呈電視制導（讓玩家選）；
    //  否則呈慣性制導。但敵方使用此制導時，無法讓玩家選，直接慣性。
    // ============================================================
    public class GuidanceType_Infrared : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            // 非玩家攻擊方 → 若地圖有殖民者，退化為慣性（敵方無法操縱電視制導）
            // 注意：玩家發射的 IncomingMissile 路徑不走此方法；此處僅處理敵方來襲。
            bool hasColonist = map.mapPawns.FreeColonistsSpawned.Count > 0;
            if (hasColonist)
            {
                // 打最近的殖民者（代替真正的「電視制導選點」）
                Pawn target = map.mapPawns.FreeColonistsSpawned
                    .MinBy(p => p.Position.DistanceToSquared(map.Center));
                if (target != null) { return target.Position; }
            }
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  電視制導（TV Guided）
    //  玩家控制時由 Scorer 流程處理（已在外部選格）。
    //  敵方使用時退化為慣性制導。
    // ============================================================
    public class GuidanceType_TV : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            // 此路徑只由 WorldObject_IncomingMissile（敵方來襲）呼叫 → 退化慣性。
            return InertialTarget(map, attacker);
        }
    }

    // ============================================================
    //  火控雷達制導（Fire Control Radar）
    //  在 WorldObject_IncomingMissile 路徑退化為慣性。
    //  此制導的主要語意是「此導彈作為 BVR 攔截彈使用」，
    //  對應的攔截邏輯由 CompMissileLauncher 驅動，不影響落點選擇。
    // ============================================================
    public class GuidanceType_FireControl : GuidanceType
    {
        public override IntVec3 PickTarget(float N, Map map, Faction attacker)
        {
            return InertialTarget(map, attacker);
        }
    }
}
