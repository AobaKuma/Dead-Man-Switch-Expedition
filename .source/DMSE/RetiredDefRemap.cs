using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 舊存檔相容：2026-10-27 退役的導彈相關 def。
    ///
    /// 原本的「彈道導彈」與「巡弋導彈」彈體一直借用 Moskit 的貼圖當佔位，已併入有自己外觀的彈體：
    /// 彈道 → AdvancedBallistic（同為發射井內裝配的戰略彈道彈體；戰略發射井與彈道發射塔的預設）、
    /// 巡弋 → Moskit（同為中型物品導彈；射門手 MLS 的彈體，舊存檔裡帶巡弋彈體的白蛉物品因此變回真正的白蛉）。
    /// 原本要求併入 Tochka／AdvancedCruise，但 Tochka 同日改為中型物品導彈、AdvancedCruise 是輕型，等級不符，使用者改選這兩個。
    /// 它們的升空與落地 def 一併改名到新彈體的對應 def；存檔裡的物品、裝配設定、飛行中的導彈與世界航跡讀檔時直接換成新名字。
    ///
    /// 舊的升空 skyfaller（<c>DMSE_SkyFaller_BallisticMissile</c>／<c>_CruiseMissile</c>）只剩產生器範本
    /// （.source/Tools/launch_templates.xml），與舊的通用攔截彈 <c>DMSE_InterceptProjectile</c> 一樣不再是遊戲 def：
    /// 它們是 skyfaller 類別，換成 Projectile 的新 def 會讀壞，所以存檔裡正在飛的這幾種直接丟棄（回報為已移除，不記錯誤）。
    ///
    /// 原版 <see cref="BackCompatibility.BackCompatibleDefName"/> 在「存檔與目前環境完全相同」時會直接跳過所有轉換器，
    /// 而只更新 DMSE 的舊存檔正是這種情況，所以用 postfix：只有在名字已找不到 def 時才換。
    /// </summary>
    public static class RetiredDefRemap
    {
        /// <summary>舊名 → (def 型別, 新名)。</summary>
        public static readonly Dictionary<string, (Type type, string replacement)> Renamed = new Dictionary<string, (Type, string)>
        {
            { "DMSE_MissileBody_Ballistic", (typeof(MissileBodyDef), "DMSE_MissileBody_AdvancedBallistic") },
            { "DMSE_MissileBody_Cruise", (typeof(MissileBodyDef), "DMSE_MissileBody_Moskit") },
            { "DMSE_Launch_Ballistic", (typeof(ThingDef), "DMSE_Launch_AdvancedBallistic") },
            { "DMSE_Launch_Cruise", (typeof(ThingDef), "DMSE_Launch_Moskit") },
            { "DMSE_Incoming_BallisticMissile", (typeof(ThingDef), "DMSE_Incoming_AdvancedBallisticMissile") },
            { "DMSE_Incoming_CruiseMissile", (typeof(ThingDef), "DMSE_Incoming_Moskit") },
        };

        /// <summary>沒有對應的新 def、讀檔時直接丟棄的舊 ThingDef（只會出現在飛行中的舊 skyfaller）。</summary>
        public static readonly HashSet<string> Dropped = new HashSet<string>
        {
            "DMSE_SkyFaller_BallisticMissile",
            "DMSE_SkyFaller_CruiseMissile",
            "DMSE_InterceptProjectile",
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
