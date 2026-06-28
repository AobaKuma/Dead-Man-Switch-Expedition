using HarmonyLib;
using System.Reflection;
using Verse;

namespace DMSE
{
    [StaticConstructorOnStartup]
    public static class PatchMain
    {
        static PatchMain()
        {
            Harmony harmony = new Harmony("DMSE");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // 動態補丁（目標方法名稱在不同版本可能不同，以反射定位）
            Patch_MissileBaseBlock_ShuttleBlocked.TryApply(harmony);
        }
    }
}
