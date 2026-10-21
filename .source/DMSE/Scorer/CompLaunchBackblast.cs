using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>發射時的尾焰與衝擊波（開放式塔架這類沒有導流與抑爆設計的平台）。</summary>
    public class CompProperties_LaunchBackblast : CompProperties
    {
        /// <summary>波及半徑（格），以建築中心起算；建築自己佔的格不算。</summary>
        public float radius = 3.5f;

        /// <summary>對 Pawn 的傷害型別；留空為火焰。</summary>
        public DamageDef damageDef;

        public int damageAmount = 18;

        /// <summary>每一格嘗試點火的機率（植物、可燃地板與地上的可燃物）。</summary>
        public float igniteChance = 0.8f;

        public float fireSize = 0.6f;

        /// <summary>範圍內 Pawn 被震懾的時間（ticks）；0 = 不震懾。</summary>
        public int stunTicks = 60;

        /// <summary>是否傷害建築（含發射架本身以外的建築）。預設不傷，避免玩家把塔蓋在倉庫旁就自毀基地。</summary>
        public bool affectsBuildings = false;

        /// <summary>發射瞬間在建築中心播放的視覺效果（可選）。</summary>
        public EffecterDef effecter;

        public CompProperties_LaunchBackblast()
        {
            compClass = typeof(CompLaunchBackblast);
        }
    }

    /// <summary>
    /// 發射後掃過周圍的尾焰：燒傷與震懾 Pawn、點燃可燃物。由發射平台在發射成功後呼叫
    /// <see cref="TryTrigger"/>（<see cref="CompMissileLauncher_WorldTargeting.TryLaunchRound"/>、
    /// <see cref="CompMissileLauncher_Interceptor.FireInterceptor"/>）；平台沒掛本 Comp 時什麼都不做。
    /// </summary>
    public class CompLaunchBackblast : ThingComp
    {
        private static readonly List<Thing> tmpThings = new List<Thing>();

        public CompProperties_LaunchBackblast Props => (CompProperties_LaunchBackblast)props;

        public static void TryTrigger(ThingWithComps launcher)
        {
            launcher?.GetComp<CompLaunchBackblast>()?.Trigger();
        }

        public void Trigger()
        {
            Map map = parent.Map;
            if (map == null || !parent.Spawned) { return; }

            CellRect self = parent.OccupiedRect();
            Vector3 center = parent.TrueCenter();
            float radiusSq = Props.radius * Props.radius;
            DamageDef damage = Props.damageDef ?? DamageDefOf.Flame;

            if (Props.effecter != null)
            {
                Effecter effect = Props.effecter.Spawn(parent.Position, map);
                effect.Trigger(new TargetInfo(parent.Position, map), TargetInfo.Invalid);
                effect.Cleanup();
            }

            foreach (IntVec3 c in self.ExpandedBy(Mathf.CeilToInt(Props.radius)))
            {
                if (!c.InBounds(map) || self.Contains(c)) { continue; }
                float dx = c.x + 0.5f - center.x;
                float dz = c.z + 0.5f - center.z;
                if (dx * dx + dz * dz > radiusSq) { continue; }

                tmpThings.Clear();
                tmpThings.AddRange(c.GetThingList(map));
                foreach (Thing t in tmpThings)
                {
                    if (t.Destroyed) { continue; }
                    if (t is Pawn pawn)
                    {
                        if (pawn.Dead) { continue; }
                        pawn.TakeDamage(new DamageInfo(damage, Props.damageAmount, 0f, -1f, parent));
                        if (Props.stunTicks > 0 && !pawn.Dead && pawn.Spawned)
                        {
                            pawn.stances?.stunner?.StunFor(Props.stunTicks, parent, false, true, false);
                        }
                    }
                    else if (Props.affectsBuildings && t is Building && t != parent)
                    {
                        t.TakeDamage(new DamageInfo(damage, Props.damageAmount, 0f, -1f, parent));
                    }
                }
                tmpThings.Clear();

                // 不傷建築時也不直接在建築（牆、機器等 edifice）格點火；地板下的電線不算（之後蔓延的火勢不在此限）。
                if (!Props.affectsBuildings && c.GetEdifice(map) != null) { continue; }
                if (Rand.Chance(Props.igniteChance))
                {
                    FireUtility.TryStartFireIn(c, map, Props.fireSize, parent);
                }
            }
        }
    }
}
