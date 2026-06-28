using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 封鎖玩家車隊在再進入冷卻期間進入導彈陣地。<br/>
    /// <br/>
    /// 攔截 <see cref="WorldObject.GetFloatMenuOptions(Caravan)"/>：<br/>
    /// 當站點具有 <see cref="WorldObjectComp_MissileBase"/> 且 <see cref="WorldObjectComp_MissileBase.IsReentryBlocked"/>
    /// 為真時，以一個「禁止進入（顯示剩餘時間）」的停用選項替換所有原始互動選項，
    /// 讓玩家知道為何無法選擇進入，同時避免遊戲 NullRef（原選項為空列表亦覆蓋正常）。<br/>
    /// <br/>
    /// 注：封鎖僅阻止地面進入（車隊）；世界層的其他互動（選取、資訊視窗）不受影響。
    /// </summary>
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.GetFloatMenuOptions))]
    public static class Patch_MissileBaseReentry
    {
        [HarmonyPostfix]
        public static void Postfix(
            WorldObject __instance,
            ref IEnumerable<FloatMenuOption> __result)
        {
            // 只處理具有導彈陣地 Comp 的世界物件
            WorldObjectComp_MissileBase woc = __instance.GetComponent<WorldObjectComp_MissileBase>();
            if (woc == null || !woc.IsReentryBlocked) return;

            // 以單一停用選項替換所有互動選項
            string label = "DMSE.MissileBase.ReentryBlocked"
                .Translate(woc.ReentryBlockedFor.ToStringTicksToPeriod());

            __result = new FloatMenuOption[]
            {
                new FloatMenuOption(label, action: null) { Disabled = true }
            };
        }
    }
}
