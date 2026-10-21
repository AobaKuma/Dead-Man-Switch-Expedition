using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈落地引爆（原 MissileIncoming.Impact 的內容）：命中偏移、主彈體爆炸、戰鬥部特效、載荷、爆炸閃光。
    /// 舊 <see cref="MissileIncoming"/> 與新 <see cref="Projectile_MissileIncoming"/> 共用。
    /// </summary>
    public static class MissileImpactUtility
    {
        /// <summary>
        /// 本次引爆的傷害歸因（docs/Missile_Skyfaller轉Projectile_計畫.md §7）：<see cref="Detonate"/> 執行期間為發射建築（被攔截時為 null）。
        /// 戰鬥部／載荷效果（<see cref="WarheadEffect"/>、<see cref="PayloadEffect"/>）造成傷害、點火時以它為 instigator，
        /// 不必改動各效果的 Apply 簽名。Detonate 之外為 null。
        /// </summary>
        public static Thing Instigator { get; private set; }

        /// <summary>
        /// 在 <paramref name="position"/> 依 <paramref name="config"/> 引爆；回傳實際爆心。
        /// <paramref name="instigator"/>：傷害歸因（發射建築；被攔截的爆炸傳 null）。
        /// <paramref name="applyScatter"/>：是否套用導引的命中偏移（護盾碰觸點引爆時不套用）。
        /// </summary>
        public static IntVec3 Detonate(MissileConfig config, IntVec3 position, Map map, Faction attacker,
            Thing instigator = null, bool applyScatter = true)
        {
            Thing previous = Instigator;
            Instigator = instigator;
            try
            {
                return DetonateInner(config, position, map, attacker, instigator, applyScatter);
            }
            finally
            {
                Instigator = previous;
            }
        }

        private static IntVec3 DetonateInner(MissileConfig config, IntVec3 position, Map map, Faction attacker,
            Thing instigator, bool applyScatter)
        {
            IntVec3 center = position;
            if (config != null && config.Valid)
            {
                float N = config.PayloadCapacity;

                // 命中偏移（導引精度）
                if (applyScatter && config.Scatter >= 1f)
                {
                    center = CellFinder.RandomClosewalkCellNear(position, map, Mathf.RoundToInt(config.Scatter));
                }

                // 主彈體爆炸（Body explosion：基礎參數 + Warhead 數值修正）
                GenExplosion.DoExplosion(center, map, config.ExplosionRadius, config.DamageDef, instigator, config.DamageAmount);

                // 戰鬥部特效（WarheadEffect class）
                MissilePartDef warheadPart = config.PartFor(MissilePartCategory.Warhead);
                warheadPart?.warheadEffect?.Apply(N, center, map, attacker);

                // 載荷效果（PayloadEffect class）
                MissilePartDef payloadPart = config.PartFor(MissilePartCategory.Payload);
                if (payloadPart?.payloadEffect != null)
                {
                    payloadPart.payloadEffect.Apply(N, center, map, attacker);
                }
                else if (payloadPart != null)
                {
                    // 向後相容：舊式數值欄位（payloadExplosionRadius 等）仍可使用。
                    if (payloadPart.payloadExplosionRadius > 0f && payloadPart.payloadDamageDef != null)
                    {
                        GenExplosion.DoExplosion(center, map, payloadPart.payloadExplosionRadius,
                            payloadPart.payloadDamageDef, instigator,
                            payloadPart.payloadDamageAmount > 0 ? payloadPart.payloadDamageAmount : -1);
                    }
                    if (payloadPart.payloadSpawnThing != null && Rand.Chance(payloadPart.payloadSpawnChance))
                    {
                        int spawnN = Mathf.Max(1, payloadPart.payloadSpawnCount);
                        int spread = Mathf.Max(1, Mathf.RoundToInt(config.ExplosionRadius));
                        for (int i = 0; i < spawnN; i++)
                        {
                            IntVec3 c = CellFinder.RandomClosewalkCellNear(center, map, spread);
                            GenSpawn.Spawn(payloadPart.payloadSpawnThing, c, map);
                        }
                    }
                }
            }
            else
            {
                // 無設定時的後備爆炸。
                GenExplosion.DoExplosion(center, map, 5f, DamageDefOf.Bomb, instigator);
            }

            // 爆炸閃光：強度與導彈威力（爆炸半徑）成正比。
            if (map != null && map.weatherManager != null && map.weatherManager.eventHandler != null)
            {
                float radius = config != null && config.Valid ? config.ExplosionRadius : 5f;
                Vector2 shadow = new Vector2(Rand.Range(-4f, 4f), Rand.Range(-4f, 0f));
                map.weatherManager.eventHandler.AddEvent(new WeatherEvent_MissileFlash(map, radius / 1f, shadow));
            }
            return center;
        }

        /// <summary>只顯示 def.label 且剔除括號後綴（如「cruise missile (incoming)」→「cruise missile」）。</summary>
        public static string StripSuffix(string label)
        {
            if (label == null) { return null; }
            int paren = label.IndexOf(" (", System.StringComparison.Ordinal);
            return paren >= 0 ? label.Substring(0, paren) : label;
        }
    }
}
