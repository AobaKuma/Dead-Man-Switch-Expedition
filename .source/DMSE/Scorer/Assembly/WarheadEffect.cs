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
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.Bomb, null, groundDamageAmount);
        }
    }

    // ============================================================
    //  鎢芯穿甲（Tungsten Penetrator）
    //  無視厚岩頂阻擋：強制在命中位置生成爆炸，並嘗試坍塌覆蓋的屋頂。
    // ============================================================
    public class WarheadEffect_TungstenPenetrator : WarheadEffect
    {
        public int damageAmount = 80;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }

            // 強制在落點執行爆炸（不依賴正常爆炸傳播，直接對格格列舉）。
            RoofDef roof = map.roofGrid.RoofAt(center);
            if (roof != null && roof.isThickRoof)
            {
                // 坍塌厚岩頂並在此生成爆炸。
                map.roofGrid.SetRoof(center, null);
                RoofCollapserImmediate.DropRoofInCells(center, map);
            }
            GenExplosion.DoExplosion(center, map, 2f, DamageDefOf.Bomb, null, damageAmount);
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
                FireUtility.TryStartFireIn(cell, map, Rand.Range(0.4f, 1.2f), null);
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
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.EMP, null, empDamage);
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
    public class WarheadEffect_BerserkPulse : WarheadEffect
    {
        /// <summary>是否也令玩家的殖民者進入狂暴（預設 false）。</summary>
        public bool affectsColonists = false;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            float radius = N;
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead) { continue; }
                if (!affectsColonists && pawn.Faction != null && pawn.Faction.IsPlayer) { continue; }
                if (!pawn.Position.InHorDistOf(center, radius)) { continue; }
                pawn.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.Berserk, forced: true);
            }
        }
    }
}
