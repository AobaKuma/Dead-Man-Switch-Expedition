using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// P4：未偵測的敵方航跡在世界地圖上隱藏。原版的展開圖示由 <see cref="ExpandableWorldObjectsUtility.HiddenByRules"/>
    /// 統一過濾，這裡接上 <see cref="MissileWorldObject.VisibleToPlayer"/>；世界物件本身的繪製與可選取性由
    /// <see cref="MissileWorldObject"/> 覆寫。
    /// </summary>
    [HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.HiddenByRules))]
    public static class Patch_HiddenTracks
    {
        public static void Postfix(WorldObject wo, ref bool __result)
        {
            if (!__result && wo is MissileWorldObject track && !track.VisibleToPlayer)
            {
                __result = true;
            }
        }
    }
}
