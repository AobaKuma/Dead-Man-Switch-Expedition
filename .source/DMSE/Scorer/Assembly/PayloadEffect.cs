using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈載荷效果的抽象基底類別。
    /// 在 MissilePartDef.payloadEffect 欄位中以 XML Class 屬性指定子類別，例如：
    /// <c>&lt;payloadEffect Class="DMSE.PayloadEffect_Cluster" /&gt;</c>
    ///
    /// 觸發時機：MissileIncoming.Impact() 中主彈體爆炸與戰鬥部效果之後。
    /// 效果範圍與強度以 N（MissileBodyDef.payloadCapacity）縮放。
    /// </summary>
    public abstract class PayloadEffect
    {
        /// <summary>在落點地圖執行載荷效果。</summary>
        public virtual void Apply(float N, IntVec3 center, Map map, Faction attacker) { }

        /// <summary>
        /// 對射程的乘法係數（預設 1.0）。
        /// 在 MissileConfig.Range 最終計算時作用於加法項總和之後。
        /// </summary>
        public virtual float RangeFactor(float N) => 1f;

        /// <summary>能否穿透厚岩頂：穿透時引爆前先在岩層開孔（<see cref="MissileImpactUtility.BreachThickRoof"/>），導引頭也會鎖定岩頂下的目標。</summary>
        public virtual bool PenetratesThickRoof => false;

        /// <summary>可選的 Def 驗證。</summary>
        public virtual IEnumerable<string> ConfigErrors(MissilePartDef parent)
        {
            yield break;
        }
    }

    // ============================================================
    //  空載（None）— 無效果（預設可不指定；也可顯式聲明）
    // ============================================================
    public class PayloadEffect_None : PayloadEffect { }

    // ============================================================
    //  高爆炸藥（High Explosive）
    //  產生一個 N 半徑的爆炸。
    // ============================================================
    public class PayloadEffect_HighExplosive : PayloadEffect
    {
        public int damageAmount = 100;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.Bomb, MissileImpactUtility.Instigator, damageAmount);
        }
    }

    // ============================================================
    //  反碉堡（Bunker Buster）
    //  延時引信的硬化穿甲彈：穿透厚岩頂後在內部引爆。
    //  小半徑（N×0.4，2–5 格）高穿甲爆炸，並對爆心附近的建築額外施加結構傷害。
    // ============================================================
    public class PayloadEffect_BunkerBuster : PayloadEffect
    {
        public int damageAmount = 150;
        public float armorPenetration = 1f;

        /// <summary>爆心 <see cref="structureRadius"/> 格內建築額外受到的傷害。</summary>
        public int structureDamage = 300;
        public float structureRadius = 1.9f;

        public override bool PenetratesThickRoof => true;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            float radius = Mathf.Clamp(N * 0.4f, 2f, 5f);
            GenExplosion.DoExplosion(center, map, radius, DamageDefOf.Bomb, MissileImpactUtility.Instigator,
                damageAmount, armorPenetration);

            // 取快照：傷害可能摧毀建築而改動格子上的物件清單。
            List<Building> buildings = new List<Building>();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, structureRadius, true))
            {
                if (!cell.InBounds(map)) { continue; }
                foreach (Thing t in cell.GetThingList(map))
                {
                    if (t is Building b && !buildings.Contains(b)) { buildings.Add(b); }
                }
            }
            foreach (Building b in buildings)
            {
                if (b.Destroyed || !b.def.useHitPoints) { continue; }
                b.TakeDamage(new DamageInfo(DamageDefOf.Bomb, structureDamage, armorPenetration, -1f, MissileImpactUtility.Instigator));
            }
        }
    }

    // ============================================================
    //  子母彈（Cluster）
    //  在 N 範圍內產生 floor(N/4) 個半徑為 N/4 的子爆炸。
    // ============================================================
    public class PayloadEffect_Cluster : PayloadEffect
    {
        public int subDamageAmount = 40;

        /// <summary>固定子彈藥數量；0 = 沿用 floor(N/4)。用於「固定 6 枚 BLU-97」這類真實配置。</summary>
        public int countOverride = 0;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            int count = countOverride > 0 ? countOverride : Mathf.Max(1, Mathf.FloorToInt(N / 4f));
            float subRadius = Mathf.Max(1f, countOverride > 0 ? N / 6f : N / 4f);
            int spreadRadius = Mathf.Max(1, Mathf.RoundToInt(N));
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = CellFinder.RandomClosewalkCellNear(center, map, spreadRadius);
                GenExplosion.DoExplosion(c, map, subRadius, DamageDefOf.Bomb, MissileImpactUtility.Instigator, subDamageAmount);
            }
        }
    }

    // ============================================================
    //  落葉劑（Defoliant）
    //  N 範圍內的植物隨機 75% 被殺死。
    // ============================================================
    public class PayloadEffect_Defoliant : PayloadEffect
    {
        public float killChance = 0.75f;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            float radius = N;
            // 取快照避免迭代中修改集合
            List<Plant> plants = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .OfType<Plant>()
                .Where(p => !p.Destroyed && p.Position.InHorDistOf(center, radius))
                .ToList();
            // listerThings 不含 Plant，改用 listerThings.ThingsOfDef 或直接遍歷格子
            plants.Clear();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, Mathf.RoundToInt(radius), true))
            {
                if (!cell.InBounds(map)) { continue; }
                foreach (Thing t in cell.GetThingList(map))
                {
                    if (t is Plant plant && !plant.Destroyed)
                    {
                        plants.Add(plant);
                    }
                }
            }
            foreach (Plant plant in plants)
            {
                if (Rand.Chance(killChance))
                {
                    plant.Kill(null, null);
                }
            }
        }
    }

    // ============================================================
    //  消防泡沫（Fire Foam）
    //  在 N/2 範圍內所有格子生成消防泡沫並撲滅現有火焰。
    // ============================================================
    public class PayloadEffect_FireFoam : PayloadEffect
    {
        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            int radius = Mathf.Max(1, Mathf.RoundToInt(N / 2f));
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) { continue; }
                // 撲滅火焰
                List<Thing> things = cell.GetThingList(map);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    if (things[i] is Fire fire) { fire.Destroy(DestroyMode.Vanish); }
                }
                // 生成消防泡沫汙垢
                FilthMaker.TryMakeFilth(cell, map, ThingDefOf.Filth_FireFoam);
            }
        }
    }

    // ============================================================
    //  反粒子（Antiparticle）
    //  N 範圍的 BombSuper 爆炸（由使用者確認使用原版 BombSuper 傷害）。
    // ============================================================
    public class PayloadEffect_Antiparticle : PayloadEffect
    {
        public int damageAmount = 150;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.Vaporize, MissileImpactUtility.Instigator, damageAmount);
        }
    }

    // ============================================================
    //  毒氣（Toxic Gas）
    //  在 N 範圍內注入毒氣（使用 GasUtility）。
    // ============================================================
    public class PayloadEffect_ToxicGas : PayloadEffect
    {
        /// <summary>每格注入的毒氣濃度（GasUtility 單位）。</summary>
        public int gasAmountPerCell = 10000;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            int radius = Mathf.Max(1, Mathf.RoundToInt(N));
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map)) { continue; }
                GasUtility.AddGas(cell, map, GasType.ToxGas, gasAmountPerCell);
            }
        }
    }

    // ============================================================
    //  電磁脈衝酬載（EMP）
    //  N 半徑 EMP 爆炸：癱瘓電子設備、震暈機械體。石墨纖維彈頭用。
    // ============================================================
    public class PayloadEffect_EMP : PayloadEffect
    {
        public int empDamage = 150;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            GenExplosion.DoExplosion(center, map, N, DamageDefOf.EMP, MissileImpactUtility.Instigator, empDamage);
        }
    }

    // ============================================================
    //  空爆鎢芯（Airburst Tungsten）
    //  射程 -25%；N 範圍內每個 Pawn 受到 (體型+1) 次 9 點穿刺傷害，40% AP。
    // ============================================================
    public class PayloadEffect_AirburstTungsten : PayloadEffect
    {
        public int damagePerHit = 9;
        public float armorPenetration = 0.4f;

        public override float RangeFactor(float N) => 0.75f;

        public override void Apply(float N, IntVec3 center, Map map, Faction attacker)
        {
            if (map == null || !center.InBounds(map)) { return; }
            float radius = N;
            List<Pawn> targets = map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Dead && p.Position.InHorDistOf(center, radius))
                .ToList();
            foreach (Pawn pawn in targets)
            {
                int hits = Mathf.CeilToInt(pawn.BodySize) + 1;
                for (int i = 0; i < hits; i++)
                {
                    DamageInfo dinfo = new DamageInfo(
                        DamageDefOf.Stab,
                        damagePerHit,
                        armorPenetration,
                        instigator: MissileImpactUtility.Instigator,
                        hitPart: null,
                        weapon: null);
                    pawn.TakeDamage(dinfo);
                    if (pawn.Dead) { break; }
                }
            }
        }
    }
}
