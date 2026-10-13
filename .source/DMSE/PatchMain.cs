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
        }
    }
}
