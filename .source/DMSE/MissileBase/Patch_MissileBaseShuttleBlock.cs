using System.Reflection;
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

        /// <summary>以反射動態定位 CompShuttle 的「是否封鎖」屬性 getter，
        /// 支援不同版本 RimWorld（同 FFF Patch_DisableShuttleLaunch 模式）。</summary>
        public static MethodBase FindShuttleBlockTarget()
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance;

            // RimWorld 1.6+ 可能是 IsBlocked
            var getter = typeof(CompShuttle).GetProperty("IsBlocked", bf)?.GetGetMethod();
            if (getter != null) return getter;

            // 退而求其次：LoadingInProgressOrReadyToLaunch
            getter = typeof(CompShuttle).GetProperty("LoadingInProgressOrReadyToLaunch", bf)?.GetGetMethod();
            return getter;
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
    // 補丁 2：原版穿梭機（CompLaunchable）— 封鎖導彈陣地範圍內的起飛
    // RimWorld 1.6：CanLaunch 是回傳 AcceptanceReport 的方法（非 bool getter）
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

    // ──────────────────────────────────────────────────────────────────────────
    // 補丁 3：CompShuttle — 動態反射，封鎖在陣地範圍內的穿梭機起飛
    // 使用與 FFF Patch_DisableShuttleLaunch 相同的模式：
    //   不使用 [HarmonyPatch] 自動掃描；由 HarmonyEntry 顯式呼叫 TryApply
    // ──────────────────────────────────────────────────────────────────────────

    public static class Patch_MissileBaseBlock_ShuttleBlocked
    {
        /// <summary>由 HarmonyEntry/模組初始化呼叫。方法不存在時靜默略過。</summary>
        public static void TryApply(Harmony harmony)
        {
            MethodBase target = MissileBaseShuttleBlockUtility.FindShuttleBlockTarget();
            if (target == null)
            {
                Log.Warning("[DMSE] Patch_MissileBaseBlock_ShuttleBlocked: 找不到 CompShuttle 目標方法，穿梭機封鎖補丁停用。");
                return;
            }

            harmony.Patch(target,
                postfix: new HarmonyMethod(
                    typeof(Patch_MissileBaseBlock_ShuttleBlocked),
                    nameof(Postfix)));
        }

        // IsBlocked getter 返回 true = 穿梭機被封鎖（封鎖 = true）
        // LoadingInProgressOrReadyToLaunch getter 返回 false = 不可發射（封鎖 = false 對應邏輯相反）
        // 因此使用 IsBlocked 語意：返回 true 代表封鎖
        static void Postfix(CompShuttle __instance, ref bool __result)
        {
            // 若已經是封鎖狀態，不需再處理
            if (__result) return;
            Map map = __instance.parent?.Map;
            if (map == null) return;

            if (MissileBaseShuttleBlockUtility.IsBlockedByMissileBase(map.Tile))
                __result = true;
        }
    }
}
