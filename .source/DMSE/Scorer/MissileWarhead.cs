using RimWorld;
using System.Collections.Generic;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// ?????型定? (Missile warhead type definition)
    /// </summary>
    public enum WarheadType
    {
        Explosive,      // 爆炸?? (High explosive)
        Incendiary,     // 燃??? (Incendiary)
        EMP,            // ?磁?? (Electromagnetic pulse)
        Fragmentation,  // 破片?? (Fragmentation)
        Custom          // 自定? (Custom - defined by ModExtension)
    }

    /// <summary>
    /// ????定? (Missile warhead definition)
    /// </summary>
    public class MissileWarheadDef : Def
    {
        // 基??性
        public WarheadType warheadType = WarheadType.Explosive;
        public float blastRadius = 15f;
        public float blastDamage = 100f;
        public DamageDef damageType;
        
        // 效果
        public EffecterDef detonationEffecter;
        public SoundDef detonationSound;
        public FleckDef explosionFleck;
        
        // 燃?特有
        public float fireExplosionChance = 0.3f;
        
        // EMP特有
        public float empDisableChance = 0.7f;
        public int empDisableDuration = 3000; // ticks
        
        // 破片特有
        public int fragmentCount = 12;
        public float fragmentDamage = 15f;
        public ThingDef fragmentProjectile;
        
        // 性能??
        public float weight = 50f;  // 影???速度和射程 (Affects missile speed and range)
        public float cost = 100f;   // 制造或?充成本 (Manufacturing or replenishment cost)
    }

    /// <summary>
    /// ?????据 (Missile warhead data - runtime information)
    /// </summary>
    public class MissileWarheadData : IExposable
    {
        public MissileWarheadDef warheadDef;
        public int loadedTicks = 0; // ????
        
        public MissileWarheadData() { }
        
        public MissileWarheadData(MissileWarheadDef def)
        {
            warheadDef = def;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref warheadDef, "warheadDef");
            Scribe_Values.Look(ref loadedTicks, "loadedTicks", 0);
        }

        /// <summary>
        /// 在目?位置引爆?? (Detonate warhead at target location)
        /// </summary>
        public void Detonate(IntVec3 targetPos, Map map)
        {
            if (warheadDef == null)
                return;

            // 播放效果和?音
            warheadDef.detonationEffecter?.Spawn(targetPos, map);
            if (warheadDef.detonationSound != null)
            {
                warheadDef.detonationSound.PlayOneShot(new TargetInfo(targetPos, map));
            }

            // 根据???型造成?害
            switch (warheadDef.warheadType)
            {
                case WarheadType.Explosive:
                    DetonateExplosive(targetPos, map);
                    break;
                case WarheadType.Incendiary:
                    DetonateIncendiary(targetPos, map);
                    break;
                case WarheadType.EMP:
                    DetonateEMP(targetPos, map);
                    break;
                case WarheadType.Fragmentation:
                    DetonateFragmentation(targetPos, map);
                    break;
                case WarheadType.Custom:
                    DetonateCustom(targetPos, map);
                    break;
            }
        }

        private void DetonateExplosive(IntVec3 targetPos, Map map)
        {
            DamageDef damageType = warheadDef.damageType ?? DamageDefOf.Bomb;
            GenExplosion.DoExplosion(targetPos, map, warheadDef.blastRadius, 
                damageType, null, (int)warheadDef.blastDamage);

            // ?建爆炸?光
            if (warheadDef.explosionFleck != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    FleckMaker.Static(targetPos.ToVector3Shifted(), map, 
                        warheadDef.explosionFleck, 
                        Rand.Range(0.5f, 1.5f));
                }
            }
        }

        private void DetonateIncendiary(IntVec3 targetPos, Map map)
        {
            DamageDef damageType = warheadDef.damageType ?? DamageDefOf.Flame;
            GenExplosion.DoExplosion(targetPos, map, warheadDef.blastRadius, 
                damageType, null, (int)warheadDef.blastDamage);

            // 在爆炸半???燃
            foreach (var cell in GenRadial.RadialCellsAround(targetPos, warheadDef.blastRadius, true))
            {
                if (!cell.InBounds(map))
                    continue;

                if (Rand.Chance(warheadDef.fireExplosionChance))
                {
                    FireUtility.TryStartFireIn(cell, map, 0.1f, null);
                }
            }
        }

        private void DetonateEMP(IntVec3 targetPos, Map map)
        {
            // EMP爆炸
            DamageDef damageType = warheadDef.damageType ?? DamageDefOf.EMP;
            GenExplosion.DoExplosion(targetPos, map, warheadDef.blastRadius, 
                damageType, null, (int)warheadDef.blastDamage);

            // 禁用范??的所有建筑
            foreach (var cell in GenRadial.RadialCellsAround(targetPos, warheadDef.blastRadius, true))
            {
                if (!cell.InBounds(map))
                    continue;

                foreach (var thing in map.thingGrid.ThingsAt(cell))
                {
                    if (thing is Building building && Rand.Chance(warheadDef.empDisableChance))
                    {
                        // 禁用建筑（如果支持）
                        var comp = building.GetComp<CompPower>();
                        if (comp != null)
                        {
                            // 通??害?禁用
                            building.TakeDamage(new DamageInfo(damageType, warheadDef.blastDamage));
                        }
                    }
                }
            }
        }

        private void DetonateFragmentation(IntVec3 targetPos, Map map)
        {
            // 初始爆炸
            DamageDef damageType = warheadDef.damageType ?? DamageDefOf.Bomb;
            GenExplosion.DoExplosion(targetPos, map, warheadDef.blastRadius / 2, 
                damageType, null, (int)(warheadDef.blastDamage / 2));

            // ?射破片
            if (warheadDef.fragmentProjectile != null)
            {
                for (int i = 0; i < warheadDef.fragmentCount; i++)
                {
                    Projectile projectile = null;
                    try
                    {
                        projectile = (Projectile)GenSpawn.Spawn(warheadDef.fragmentProjectile, 
                            targetPos, map);
                    }
                    catch
                    {
                        continue;
                    }

                    if (projectile == null)
                        continue;
                    
                    var randomCell = targetPos + GenRadial.RadialPattern[Rand.Range(0, 
                        GenRadial.RadialPattern.Length)];
                    
                    if (randomCell.InBounds(map))
                    {
                        // 使用Verb?射而不是Launch
                        var verb = projectile.def.projectile;
                        if (verb != null)
                        {
                            projectile.Launch(projectile, new LocalTargetInfo(randomCell), new LocalTargetInfo(randomCell), ProjectileHitFlags.All);
                        }
                    }
                }
            }
        }

        private void DetonateCustom(IntVec3 targetPos, Map map)
        {
            // 自定?引爆?? - 可由ModExtension?展
            DamageDef damageType = warheadDef.damageType ?? DamageDefOf.Bomb;
            GenExplosion.DoExplosion(targetPos, map, warheadDef.blastRadius, 
                damageType, null, (int)warheadDef.blastDamage);
        }
    }

    /// <summary>
    /// ?准????合集 (Standard missile warhead definitions)
    /// </summary>
    public static class StandardWarheads
    {
        public static MissileWarheadDef HighExplosive;
        public static MissileWarheadDef Incendiary;
        public static MissileWarheadDef EMP;
        public static MissileWarheadDef Fragmentation;
    }
}
