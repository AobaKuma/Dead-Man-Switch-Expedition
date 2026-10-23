using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 「可以裝飛彈的平台」的抽象。存在的理由：飛彈平台有兩種實作——
    ///   1) <see cref="Building_MissileRack"/>：建築本身就是容器（IThingHolder 在建築上）；
    ///   2) <see cref="CompMissileRackStorage"/>：容器在 comp 上，掛在別種建築（例如
    ///      <see cref="Building_TurretGun"/> 的野戰防空砲）身上。
    /// 任務、ITab、自動填充都只依賴這組成員，不再寫死建築型別。
    /// </summary>
    public interface IMissileRack : IThingHolder, IStoreSettingsParent
    {
        /// <summary>平台本體（建築）。需要 Faction／Map／def 時用它，不要假設介面本身就是 Thing。</summary>
        Thing RackThing { get; }

        /// <summary>容量／量級／發射模式／渲染選項（可能為 null）。</summary>
        MissileRackExtension RackExt { get; }

        /// <summary>最大可存放數量。</summary>
        int MaxStored { get; }

        /// <summary>目前存放的飛彈。</summary>
        IReadOnlyList<Thing> HeldThings { get; }

        int StoredCount { get; }

        bool Full { get; }

        /// <summary>渲染 comp（沒有則為 null）。</summary>
        CompMissileRackRenderer Renderer { get; }

        bool Accepts(Thing t);

        int SpaceRemainingFor(ThingDef def);
    }

    /// <summary>從任意 Thing 取得飛彈平台（建築自帶容器，或掛了 <see cref="CompMissileRackStorage"/> 的建築）。</summary>
    public static class MissileRackUtil
    {
        public static IMissileRack Of(Thing t)
        {
            if (t == null) { return null; }
            if (t is IMissileRack direct) { return direct; }
            return t.TryGetComp<CompMissileRackStorage>();
        }
    }
}
