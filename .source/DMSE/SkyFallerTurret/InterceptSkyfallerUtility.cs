using RimWorld;
using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public static class InterceptSkyfallerUtility
    {
        public static void Handle(Map map, Skyfaller faller, IntVec3 pos, int delayTicks)
        {
            if (map == null || faller == null)
            {
                return;
            }

            MapComponent_InterceptSkyfaller comp = map.GetComponent<MapComponent_InterceptSkyfaller>();
            if (comp == null)
            {
                return;
            }

            int time = Find.TickManager.TicksGame + delayTicks;
            if (comp.Pods.Find(p => p.tickToSpawn == time) is DroppodData data)
            {
                data.pods.Add(new PodData(faller, pos, map));
            }
            else
            {
                DroppodData d = new DroppodData(time, new List<PodData> { new PodData(faller, pos, map) });
                comp.Pods.Add(d);
            }

            TryLaunchInterceptProjectile(comp, map, faller);
        }

        private static void TryLaunchInterceptProjectile(MapComponent_InterceptSkyfaller comp, Map map, Skyfaller faller)
        {
            foreach (var turret in comp.turrets)
            {
                if (turret.count <= 0)
                {
                    continue;
                }

                CompPowerTrader power = turret.parent.TryGetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn)
                {
                    continue;
                }

                CompRefuelable refuelable = turret.parent.TryGetComp<CompRefuelable>();
                if (refuelable != null && refuelable.Fuel <= 0f)
                {
                    continue;
                }
                if (turret?.Props?.projectile == null)
                {
                    continue;
                }
                InterceptProjectile projectile = (InterceptProjectile)SkyfallerMaker.SpawnSkyfaller(
                    turret.Props.projectile, turret.parent.Position, map);

                projectile.Rotation = Rot4.Random;
                projectile.angle = projectile.Rotation.AsAngle;
                projectile.faller = faller;

                if (Prefs.DevMode)
                {
                    Log.Message("Launch intercept¡G" + projectile + $"curTIck:{Find.TickManager.TicksGame},{turret.cooldown}");
                }

                turret.cooldown = Find.TickManager.TicksGame + turret.Props.cooldown;
                turret.count--;
                break;
            }
        }
    }
}