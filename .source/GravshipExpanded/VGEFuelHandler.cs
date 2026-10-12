using PipeSystem;
using RimWorld;
using UnityEngine;
using VanillaGravshipExpanded;
using Verse;

namespace DMSE.VGE
{
    /// <summary>
    /// VGE 環境下的轉移燃料來源（設計稿 §10-3）：VGE 把燃料槽（含 DMSE 燃料筒倉）改成 PipeSystem 的
    /// <see cref="CompResourceStorage"/>（Astrofuel），這裡把它們註冊為 <see cref="ITransferFuelSource"/>。
    /// 轉移飛行直接從船上的燃料槽扣除，不經過管線、也不經過重力引擎（不加熱、不設冷卻）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class VGEFuelHandler
    {
        /// <summary>原版化學燃料每格航程的消耗；VGE 的 resourceToRangeRatio 以此為基準換算成化學燃料當量。</summary>
        private const float ChemfuelPerTile = 10f;

        static VGEFuelHandler()
        {
            TransferFuelSources.RegisterProvider(TryGetSource);
        }

        private static ITransferFuelSource TryGetSource(Thing thing)
        {
            ThingWithComps twc = thing as ThingWithComps;
            if (twc == null)
            {
                return null;
            }
            CompResourceStorage storage = twc.GetComp<CompResourceStorage>();
            if (storage == null)
            {
                return null;
            }
            bool flagged = thing.def.HasModExtension<TransferFuelSourceExtension>();
            if (!flagged)
            {
                CompGravshipFacility facility = twc.GetComp<CompGravshipFacility>();
                flagged = facility != null && facility.Props.providesFuel;
            }
            if (!flagged)
            {
                return null;
            }
            GravshipFuelExtension fuelExt = storage.Props.pipeNet?.GetModExtension<GravshipFuelExtension>();
            if (fuelExt == null || fuelExt.resourceToRangeRatio <= 0f)
            {
                return null;
            }
            return new PipeStorageFuelSource(storage, ChemfuelPerTile / fuelExt.resourceToRangeRatio);
        }
    }

    /// <summary>以 PipeSystem 儲存槽供應的轉移燃料；對外以化學燃料當量計。</summary>
    public class PipeStorageFuelSource : ITransferFuelSource
    {
        private readonly CompResourceStorage storage;

        /// <summary>每單位儲存資源相當的化學燃料量（Astrofuel：10 / 5 = 2）。</summary>
        private readonly float chemfuelPerUnit;

        public PipeStorageFuelSource(CompResourceStorage storage, float chemfuelPerUnit)
        {
            this.storage = storage;
            this.chemfuelPerUnit = chemfuelPerUnit;
        }

        public Thing Parent => storage.parent;

        public float Available => storage.AmountStored * chemfuelPerUnit;

        public void Draw(float amount)
        {
            float units = Mathf.Min(amount / chemfuelPerUnit, storage.AmountStored);
            if (units > 0f)
            {
                storage.DrawResource(units);
            }
        }

        public void DebugFill()
        {
            storage.AddResource(storage.AmountCanAccept);
        }
    }
}
