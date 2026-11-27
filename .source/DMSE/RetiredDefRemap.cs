using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 舊存檔相容：已發布版本（1.6 的 0.35、1.7 的 0.7）存檔裡、現在已不存在的導彈 def。
    ///
    /// 1.6 版的巡弋導彈落地 skyfaller <c>DMSE_Incoming_CruiseMissile</c> 換成 Moskit 的落地 def。
    /// 1.6 版的 <c>DMSE_SkyFaller_CruiseMissile</c>、<c>DMSE_InterceptProjectile</c> 與 1.7 0.7 版的平台備援升空 skyfaller
    /// 是 skyfaller 類別，換成 Projectile 的新 def 會讀壞，所以存檔裡正在飛的這幾種直接丟棄（回報為已移除，不記錯誤）。
    /// （2026-11-27 刪掉只在開發期存在、從未發布的對應：Ballistic／Cruise 彈體與其升空落地 def、DMSE_SkyFaller_BallisticMissile。）
    ///
    /// 原版 <see cref="BackCompatibility.BackCompatibleDefName"/> 在「存檔與目前環境完全相同」時會直接跳過所有轉換器，
    /// 而只更新 DMSE 的舊存檔正是這種情況，所以用 postfix：只有在名字已找不到 def 時才換。
    /// </summary>
    public static class RetiredDefRemap
    {
        /// <summary>舊名 → (def 型別, 新名)。</summary>
        public static readonly Dictionary<string, (Type type, string replacement)> Renamed = new Dictionary<string, (Type, string)>
        {
            { "DMSE_Incoming_CruiseMissile", (typeof(ThingDef), "DMSE_Incoming_Moskit") },
        };

        /// <summary>沒有對應的新 def、讀檔時直接丟棄的舊 ThingDef（只會出現在飛行中的舊 skyfaller）。</summary>
        public static readonly HashSet<string> Dropped = new HashSet<string>
        {
            "DMSE_SkyFaller_CruiseMissile",
            "DMSE_InterceptProjectile",
            // 2026-11-26：平台備援的升空 skyfaller 退役（類別 ScorerProjectile／InterceptProjectile 刪除），
            // 備援改用 DMSE_Launch_*；只剩產生器範本。
            "DMSE_SkyFaller_LightMissile",
            "DMSE_SkyFaller_Loiter",
            "DMSE_SkyFaller_Interceptor",
        };

        /// <summary>舊名換新名；不是退役的名字、型別不符或舊名其實還存在時回傳原名。</summary>
        public static string Resolve(Type defType, string defName)
        {
            if (defName == null || !Renamed.TryGetValue(defName, out var entry)) { return defName; }
            if (defType != null && !entry.type.IsAssignableFrom(defType) && !defType.IsAssignableFrom(entry.type)) { return defName; }
            return entry.replacement;
        }
    }

    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.BackCompatibleDefName))]
    public static class Patch_RetiredDefRemap_DefName
    {
        public static void Postfix(Type defType, ref string __result)
        {
            if (__result == null || GenDefDatabase.GetDefSilentFail(defType, __result, false) != null) { return; }
            __result = RetiredDefRemap.Resolve(defType, __result);
        }
    }

    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.WasDefRemoved))]
    public static class Patch_RetiredDefRemap_Removed
    {
        public static void Postfix(string defName, Type type, ref bool __result)
        {
            if (!__result && type == typeof(ThingDef) && RetiredDefRemap.Dropped.Contains(defName)) { __result = true; }
        }
    }
}
