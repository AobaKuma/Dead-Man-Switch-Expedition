using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    // ──────────────────────────────────────────────────────────────────────────
    // 工具靜態類：判斷 tile 是否受導彈陣地封鎖
    // ──────────────────────────────────────────────────────────────────────────

    public static class MissileBaseShuttleBlockUtility
    {
        /// <summary>導彈陣地封鎖半徑（世界 tile）。</summary>
        public const int BlockRadius = 5;

        /// <summary>指定 PlanetTile 是否位於任何 DMSE 導彈陣地的封鎖範圍內。</summary>
        public static bool IsBlockedByMissileBase(PlanetTile tile)
        {
            foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo.GetComponent<WorldObjectComp_MissileBase>() == null) continue;
                float dist = Find.WorldGrid.ApproxDistanceInTiles(tile, wo.Tile);
                if (dist <= BlockRadius) return true;
            }
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 補丁 1：重力船（GravShip）— 封鎖導彈陣地範圍內的起飛
    // Priority.High：在 DMSE 主補丁（Priority.Normal）之前執行，搶先回傳 false
    // ──────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    public static class Patch_MissileBaseBlock_GravshipLaunch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(CompPilotConsole __instance)
        {
            Map map = __instance.parent?.Map;
            if (map == null) return true;

            if (MissileBaseShuttleBlockUtility.IsBlockedByMissileBase(map.Tile))
            {
                Messages.Message(
                    "DMSE.MissileBase.ShuttleBlocked".Translate(MissileBaseShuttleBlockUtility.BlockRadius),
                    MessageTypeDefOf.RejectInput, historical: false);
                return false;  // 跳過原方法
            }
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 補丁 2：原版穿梭機與運輸艙（CompLaunchable）— 封鎖導彈陣地範圍內的起飛
    // RimWorld 1.6 / 1.7：CanLaunch 是回傳 AcceptanceReport 的方法（非 bool getter）。
    // 穿梭機的 CompShuttle.CanLaunch 只由這裡的結尾 `return Shuttle.CanLaunch` 呼叫，
    // 所以這個 postfix 已涵蓋穿梭機，不需要另外補丁 CompShuttle。
    // ──────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.CanLaunch))]
    public static class Patch_MissileBaseBlock_ShuttleLaunch
    {
        [HarmonyPostfix]
        public static void Postfix(CompLaunchable __instance, ref AcceptanceReport __result)
        {
            if (!__result.Accepted) return;
            Map map = __instance.parent?.Map;
            if (map == null) return;

            if (MissileBaseShuttleBlockUtility.IsBlockedByMissileBase(map.Tile))
                __result = "DMSE.MissileBase.ShuttleBlocked"
                    .Translate(MissileBaseShuttleBlockUtility.BlockRadius);
        }
    }
}
