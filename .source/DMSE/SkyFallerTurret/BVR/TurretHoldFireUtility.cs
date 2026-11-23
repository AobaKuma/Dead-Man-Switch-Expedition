using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 建築上的砲塔「停火」狀態，給掛在砲塔上的 DMSE 防空裝置（末端 CIWS、攔截彈發射架）共用：
    /// 玩家對砲塔按了停火，這座建築上的所有防空武器都不開火。
    ///   - 原版 <see cref="Building_TurretGun"/>：讀私有欄位 holdFire，且同原版 TryStartShootSomething 只在
    ///     PlayerControlled 時生效（NPC 砲塔或被非殖民者操作時，殘留的停火旗標不算）。
    ///   - 非砲塔的平台：讀 <see cref="CompBuildingTurretGun"/> 的停火。
    ///   - 其他砲塔類別（例如 CE 的 Building_TurretGunCE）：讀同名的 holdFire 欄位，只認玩家陣營。
    /// </summary>
    public static class TurretHoldFireUtility
    {
        private static readonly AccessTools.FieldRef<Building_TurretGun, bool> TurretHoldFireRef = TryHoldFireRef();
        private static readonly Func<Building_TurretGun, bool> TurretPlayerControlled = TryPlayerControlledGetter();
        private static readonly Dictionary<Type, FieldInfo> otherHoldFireFields = new Dictionary<Type, FieldInfo>();

        private static AccessTools.FieldRef<Building_TurretGun, bool> TryHoldFireRef()
        {
            try { return AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire"); }
            catch (Exception e) { Log.Warning($"[DMSE] Building_TurretGun.holdFire not found; hold fire will not stop air defense. {e.Message}"); return null; }
        }

        private static Func<Building_TurretGun, bool> TryPlayerControlledGetter()
        {
            try
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(Building_TurretGun), "PlayerControlled");
                return getter != null ? AccessTools.MethodDelegate<Func<Building_TurretGun, bool>>(getter) : null;
            }
            catch { return null; }
        }

        /// <summary>玩家對 <paramref name="building"/> 下了停火。</summary>
        public static bool HoldingFire(ThingWithComps building)
        {
            if (building == null) { return false; }
            if (building is Building_TurretGun turret)
            {
                if (TurretHoldFireRef == null || !TurretHoldFireRef(turret)) { return false; }
                return TurretPlayerControlled != null ? TurretPlayerControlled(turret) : turret.Faction == Faction.OfPlayer;
            }
            CompBuildingTurretGun turretGunComp = building.GetComp<CompBuildingTurretGun>();
            if (turretGunComp != null) { return turretGunComp.HoldFire; }
            return building is Building_Turret && building.Faction == Faction.OfPlayer && OtherTurretHoldFire(building);
        }

        private static bool OtherTurretHoldFire(Thing turret)
        {
            Type type = turret.GetType();
            if (!otherHoldFireFields.TryGetValue(type, out FieldInfo field))
            {
                field = AccessTools.Field(type, "holdFire");
                if (field != null && field.FieldType != typeof(bool)) { field = null; }
                otherHoldFireFields[type] = field;
            }
            return field != null && (bool)field.GetValue(turret);
        }
    }
}
