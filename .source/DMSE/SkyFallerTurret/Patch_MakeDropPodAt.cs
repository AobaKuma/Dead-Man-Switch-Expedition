using DMSE.SkyFallerTurret;
using HarmonyLib;
using RimWorld;
using System;
using Verse;

namespace DMSE
{
    [HarmonyPatch(typeof(SkyfallerMaker), nameof(SkyfallerMaker.SpawnSkyfaller),
        new Type[] { typeof(ThingDef), typeof(Thing), typeof(IntVec3), typeof(Map) })]
    public class Patch_MakeDropPodAt
    {
        [HarmonyPostfix]
        public static void postfix(Skyfaller __result, Thing innerThing, IntVec3 pos, Map map)
        {
            if (Prefs.DevMode) 
            {
                Log.Message("生成空投中：" + innerThing.Label);
            }
            bool hostile = false;
            if (innerThing is Pawn pawn && pawn.Faction != Faction.OfPlayer
                && pawn.Faction?.HostileTo(Faction.OfPlayer) == true) 
            {
                hostile = true;
            }
            if (innerThing is ActiveTransporter transporter && transporter.Contents.SingleContainedThing
                is Pawn pawn2 && pawn2.Faction != Faction.OfPlayer
                && pawn2.Faction?.HostileTo(Faction.OfPlayer) == true) 
            {
                hostile = true;
            }
            if (hostile) 
            {
                __result.DeSpawn();
                var comp = map.GetComponent<MapComponent_InterceptSkyfaller>();
                comp.Pods.Add(new DroppodData(Find.TickManager.TicksGame
                    + 600,__result,pos));
                if (Prefs.DevMode)
                {
                    Log.Message("空投延迟：" + innerThing.Label);
                }
                foreach (var turret in comp.turrets)
                {
                    if (turret.count > 0
                        && ((!(turret.parent.TryGetComp<CompPowerTrader>() is CompPowerTrader power)
                        || power.PowerOn))&& ((!(turret.parent.TryGetComp<CompRefuelable>() is CompRefuelable refuelable)
                        || refuelable.Fuel > 0)))
                    {
                        InterceptProjectile projectile = (InterceptProjectile)SkyfallerMaker.SpawnSkyfaller(
                            turret.Props.projectile, turret.parent.Position, map);
                        projectile.Rotation = Rot4.Random;
                        projectile.angle = projectile.Rotation.AsAngle;
                        projectile.faller = __result;
                        if (Prefs.DevMode)
                        {
                            Log.Message("发射拦截：" + projectile + $"curTIck:{Find.TickManager.TicksGame}," +
                                $"{turret.cooldown}");
                        }
                        turret.cooldown = Find.TickManager.TicksGame + turret.Props.cooldown;
                        turret.count--;
                        break;
                    }
                }
            }
        } 
    }
}