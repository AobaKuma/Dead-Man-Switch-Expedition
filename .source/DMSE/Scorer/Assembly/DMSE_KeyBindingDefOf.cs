using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>本模組的快捷鍵（1.7/Defs/DMSE_KeyBindings.xml），可在原版「按鍵設定」裡改。</summary>
    [DefOf]
    public static class DMSE_KeyBindingDefOf
    {
        /// <summary>裝配分頁：切到架上上一枚導彈。</summary>
        public static KeyBindingDef DMSE_MissileAssemblyPrevious;

        /// <summary>裝配分頁：切到架上下一枚導彈。</summary>
        public static KeyBindingDef DMSE_MissileAssemblyNext;

        static DMSE_KeyBindingDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_KeyBindingDefOf));
    }
}
