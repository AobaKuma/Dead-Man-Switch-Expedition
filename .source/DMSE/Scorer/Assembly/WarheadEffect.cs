using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈戰鬥部效果的抽象基底類別。
    /// 在 MissilePartDef.warheadEffect 欄位中以 XML Class 屬性指定具體子類別，例如：
    /// <c>&lt;warheadEffect Class="DMSE.WarheadEffect_Incendiary" /&gt;</c>
    ///
    /// 觸發時機：MissileIncoming.Impact() 中主彈體爆炸之後、載荷之前。
    /// 效果強度以彈體的 payloadCapacity（N）縮放。
    /// </summary>
    public abstract class WarheadEffect
    {
        /// <summary>在落點地圖執行戰鬥部特效。</summary>
        /// <param name="N">載荷容量係數（MissileBodyDef.payloadCapacity）。</param>
        /// <param name="center">命中中心格。</param>
        /// <param name="map">目標地圖。</param>
        /// <param name="attacker">發射方陣營。</param>
        public virtual void Apply(float N, IntVec3 center, Map map, Faction attacker) { }

        /// <summary>
        /// 此導彈作為攔截彈使用時，對命中率的加算加成（0-1 範圍）。
        /// 加成在 MapComponent_BVRCombat 計算命中率後疊加（clamp 至 1）。
        /// </summary>
        public virtual float InterceptBonus(float N) => 0f;

        /// <summary>
        /// 此導彈作為攔截彈使用時，額外對同波次其他目標進行的獨立攔截擲骰次數。
        /// 每次額外擲骰與主要攔截使用相同命中率，不消耗額外彈藥或冷卻。
        /// </summary>
        public virtual int ExtraInterceptRolls(float N) => 0;

        /// <summary>
        /// 對射程的額外格數加成（正值增程，負值減程）。
        /// 在 MissileConfig.Range 計算的加法項中加入（在乘法係數之前）。
        /// </summary>
        public virtual float RangeOffset(float N) => 0f;

        /// <summary>能否穿透厚岩頂：穿透時引爆前先在岩層開孔（<see cref="MissileImpactUtility.BreachThickRoof"/>），導引頭也會鎖定岩頂下的目標。</summary>
        public virtual bool PenetratesThickRoof => false;

        /// <summary>可選的設定驗證。</summary>
        public virtual IEnumerable<string> ConfigErrors(MissilePartDef parent)
        {
            yield break;
        }
    }

    // ============================================================
    //  連續桿（Continuous Rod）
    //  作為防空導彈時，命中率 + N×5%
    // ============================================================
    public class WarheadEffect_ContinuousRod : WarheadEffect
    {
        public override float InterceptBonus(float N) => N * 0.05f;
    }

    // ============================================================
    //  增程燃油（Extended Range Fuel）
    //  射程 + N×2 格；Impact 無特殊效果。
    // ============================================================
    public class WarheadEffect_ExtendedFuel : WarheadEffect
    {
        public override float RangeOffset(float N) => N * 2f;
    }

    // ============================================================
    //  空爆（Airburst）
    //  防空：額外 floor(N/5) 次獨立攔截擲骰。
    //  對地：中心 N 半徑爆炸。
    // ============================================================
    public class WarheadEffect_Airburst : WarheadEffect
    {
        /// <summary>對地爆炸傷害量（可在 XML 中覆寫）。</summary>
        public int groundDamageAmount = 50;

        public override int ExtraInterceptRolls(float N) => Mathf.FloorToInt(N / 5f);

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.Bomb, MissileImpactUtility.Instigator, groundDamageAmount);
        }
    }

    // ============================================================
    //  鎢芯穿甲（Tungsten Penetrator）
    //  穿透厚岩頂（開孔由 MissileImpactUtility 在引爆前處理），之後在落點補一個 2 格爆炸。
    // ============================================================
    public class WarheadEffect_TungstenPenetrator : WarheadEffect
    {
        public int damageAmount = 80;

        public override bool PenetratesThickRoof => true;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            GenExplosion.DoExplosion(center, map, 2f, DamageDefOf.Bomb, MissileImpactUtility.Instigator, damageAmount);
        }
    }

    // ============================================================
    //  燒夷彈（Incendiary）
    //  無視 LOS，N 半徑內點燃。
    // ============================================================
    public class WarheadEffect_Incendiary : WarheadEffect
    {
        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            int radius = Mathf.Max(1, Mathf.RoundToInt(N));
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) { continue; }
                FireUtility.TryStartFireIn(cell, map, Rand.Range(0.4f, 1.2f), MissileImpactUtility.Instigator);
            }
        }
    }

    // ============================================================
    //  電磁脈衝（EMP）
    //  無視 LOS，N 半徑 EMP 爆炸。
    // ============================================================
    public class WarheadEffect_EMP : WarheadEffect
    {
        public int empDamage = 120;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.EMP, MissileImpactUtility.Instigator, empDamage);
        }
    }

    // ============================================================
    //  動物狂暴脈衝（Animal Berserk Pulse）
    //  全圖所有動物進入狂暴狀態。
    // ============================================================
    public class WarheadEffect_AnimalBerserk : WarheadEffect
    {
        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null) { return; }
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead || !pawn.RaceProps.Animal) { continue; }
                pawn.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
            }
        }
    }

    // ============================================================
    //  狂暴脈衝（Berserk Pulse）
    //  N 半徑內所有生物進入狂暴狀態（不含玩家陣營可選）。
    // ============================================================
    // ============================================================
    //  溫壓（Thermobaric / MAC）
    //  小半徑但近距離燒傷＋點燃；密閉空間（房間）效果最明顯。
    //  不做額外爆炸（主彈體已爆炸），避免同一次命中判兩次。
    // ============================================================
    public class WarheadEffect_Thermobaric : WarheadEffect
    {
        /// <summary>半徑內每個 Pawn 受到的燒傷量。</summary>
        public int pawnDamage = 90;

        /// <summary>每格點燃的機率。</summary>
        public float igniteChance = 0.6f;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }

            float radius = Mathf.Max(1.5f, N * 0.5f);
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead || !pawn.Position.InHorDistOf(center, radius)) { continue; }
                pawn.TakeDamage(new DamageInfo(DamageDefOf.Burn, pawnDamage, 0.3f, -1f, MissileImpactUtility.Instigator));
            }

            int cells = Mathf.Max(1, Mathf.RoundToInt(radius));
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, cells, true))
            {
                if (!cell.InBounds(map)) { continue; }
                if (Rand.Chance(igniteChance))
                {
                    FireUtility.TryStartFireIn(cell, map, Rand.Range(0.4f, 1f), MissileImpactUtility.Instigator);
                }
            }
        }
    }

    // ============================================================
    //  動能刀刃（Kinetic Blades，R-9X 型）
    //  近乎無爆炸：只對最近的 Pawn 施加切割傷害；傷害隨 N 略增。
    // ============================================================
    public class WarheadEffect_KineticBlades : WarheadEffect
    {
        /// <summary>每片刀刃的基礎切割傷害。</summary>
        public int damagePerBlade = 35;

        /// <summary>最多命中幾個目標（每次命中消耗一片刀刃）。</summary>
        public int maxTargets = 3;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }

            int blades = Mathf.Clamp(Mathf.RoundToInt(N * 0.5f) + 1, 1, Mathf.Max(1, maxTargets));

            // 只打最近的一個 Pawn：刀刃是精確殺傷，不是區域壓制。
            Pawn nearest = null;
            float best = float.MaxValue;
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead || !pawn.Position.InHorDistOf(center, 3f)) { continue; }
                float distance = (pawn.Position - center).LengthHorizontalSquared;
                if (distance < best) { best = distance; nearest = pawn; }
            }
            if (nearest == null) { return; }

            int damage = damagePerBlade + Mathf.RoundToInt(N * 2f);
            for (int i = 0; i < blades; i++)
            {
                nearest.TakeDamage(new DamageInfo(DamageDefOf.Cut, damage, 0.6f, -1f, MissileImpactUtility.Instigator));
                if (nearest.Dead) { break; }
            }
        }
    }
}
