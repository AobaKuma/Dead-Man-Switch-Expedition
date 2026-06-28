using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>抵達落點的導彈。依攜帶的 <see cref="MissileConfig"/> 套用彈頭、命中偏移與特殊酬載。</summary>
    public class MissileIncoming : Skyfaller
    {
        public MissileConfig config;

        /// <summary>發射此導彈的陣營（來襲方）。供攔截/識別用。</summary>
        public Faction attacker;

        /// <summary>是否已交由 BVR 處理（避免重複登記攔截）。</summary>
        public bool bvrHandled;

        /// <summary>只顯示 def.label 且剔除括號後綴（如「cruise missile (incoming)」→「cruise missile」）。</summary>
        public override string Label
        {
            get
            {
                string l = def.label;
                int paren = l.IndexOf(" (", System.StringComparison.Ordinal);
                return paren >= 0 ? l.Substring(0, paren) : l;
            }
        }

        private bool checkedBvr;

        protected override void Tick()
        {
            // 首次 tick 時（此時呼叫端已設好 attacker/config）嘗試交給 BVR 攔截。
            if (!checkedBvr)
            {
                checkedBvr = true;
                if (!bvrHandled)
                {
                    MissileBVRUtility.TryRegister(this, Position);
                    if (!Spawned) { return; } // 已被 BVR 收起。
                }
            }
            base.Tick();
        }

        protected override void Impact()
        {
            Map map = Map;
            IntVec3 center = Position;

            if (config != null && config.Valid)
            {
                float N = config.PayloadCapacity;

                // 命中偏移（導引精度）
                if (config.Scatter >= 1f)
                {
                    center = CellFinder.RandomClosewalkCellNear(Position, map, Mathf.RoundToInt(config.Scatter));
                }

                // 主彈體爆炸（Body explosion：基礎參數 + Warhead 數值修正）
                GenExplosion.DoExplosion(center, map, config.ExplosionRadius, config.DamageDef, null, config.DamageAmount);

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
                            payloadPart.payloadDamageDef, null,
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
                GenExplosion.DoExplosion(center, map, 5f, DamageDefOf.Bomb, null);
            }

            // 爆炸閃光：強度與導彈威力（爆炸半徑）成正比。
            if (map != null && map.weatherManager != null && map.weatherManager.eventHandler != null)
            {
                float radius = config != null && config.Valid ? config.ExplosionRadius : 5f;
                Vector2 shadow = new Vector2(Rand.Range(-4f, 4f), Rand.Range(-4f, 0f));
                map.weatherManager.eventHandler.AddEvent(new WeatherEvent_MissileFlash(map, radius / 1f, shadow));
            }

            base.Impact();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref config, "config");
            Scribe_References.Look(ref attacker, "attacker");
            Scribe_Values.Look(ref bvrHandled, "bvrHandled", false);
            Scribe_Values.Look(ref checkedBvr, "checkedBvr", false);
        }
    }
}
