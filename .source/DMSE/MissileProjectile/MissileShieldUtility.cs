using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 高角護盾攔截落地導彈（docs/Missile_Skyfaller轉Projectile_計畫.md §6）。
    ///
    /// 每 tick 以導彈畫面位置的前後兩點呼叫原版 <see cref="CompProjectileInterceptor.CheckIntercept"/>：
    /// 只有攔截「空中彈」的護盾（interceptAirProjectiles，例如 ShieldGeneratorMortar）會攔，地面護盾不攔；
    /// 穿過護盾圓周、護盾特效／攔截錐、扣護盾 HP、打破護盾全部照原版。
    ///
    /// 敵我：原版以 projectile.Launcher 判斷，Launcher 為 null 時直接放行（除非 interceptNonHostileProjectiles）。
    /// 有發射建築時照原版；沒有時（敵方世界導彈、發射建築已摧毀）先以導彈的攻擊方陣營自行判斷敵對，通過後在呼叫期間
    /// 暫時打開該護盾私有的 debugInterceptNonHostileProjectiles，讓原版接著做幾何與結算，呼叫後還原。
    /// </summary>
    public static class MissileShieldUtility
    {
        private static readonly AccessTools.FieldRef<CompProjectileInterceptor, bool> InterceptNonHostile = Bind();
        private static bool warned;

        private static AccessTools.FieldRef<CompProjectileInterceptor, bool> Bind()
        {
            try
            {
                return AccessTools.FieldRefAccess<CompProjectileInterceptor, bool>("debugInterceptNonHostileProjectiles");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// <paramref name="missile"/> 從 <paramref name="from"/> 移到 <paramref name="to"/>（地圖格座標）時是否被高角護盾攔下；
        /// 攔下時回傳碰觸格（原版護盾特效的位置）。
        /// </summary>
        public static bool TryIntercept(Projectile_MissileIncoming missile, Vector3 from, Vector3 to, out IntVec3 contact)
        {
            contact = IntVec3.Invalid;
            Map map = missile.Map;
            if (map == null) { return false; }
            from.y = 0f;
            to.y = 0f;
            if (from == to) { return false; }

            List<Thing> shields = map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor);
            for (int i = 0; i < shields.Count; i++)
            {
                CompProjectileInterceptor comp = shields[i].TryGetComp<CompProjectileInterceptor>();
                if (comp == null || !CompProjectileInterceptor.InterceptsProjectile(comp.Props, missile)) { continue; }
                if (Check(comp, missile, from, to))
                {
                    contact = to.ToIntVec3().ClampInsideMap(map);
                    return true;
                }
            }
            return false;
        }

        private static bool Check(CompProjectileInterceptor comp, Projectile_MissileIncoming missile, Vector3 from, Vector3 to)
        {
            // 有發射建築（且有陣營）：原版的 Launcher 敵我判斷可用。
            if (missile.Launcher != null) { return comp.CheckIntercept(missile, from, to); }

            // 沒有發射建築：以攻擊方陣營判斷（無主護盾照原版一律攔截）。
            Faction owner = comp.parent.Faction;
            if (owner != null && (missile.attacker == null || !missile.attacker.HostileTo(owner))) { return false; }
            if (InterceptNonHostile == null)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("[DMSE] CompProjectileInterceptor.debugInterceptNonHostileProjectiles not found: shields will not stop missiles without a launcher.");
                }
                return false;
            }
            ref bool flag = ref InterceptNonHostile(comp);
            bool old = flag;
            flag = true;
            try
            {
                return comp.CheckIntercept(missile, from, to);
            }
            finally
            {
                flag = old;
            }
        }
    }
}
