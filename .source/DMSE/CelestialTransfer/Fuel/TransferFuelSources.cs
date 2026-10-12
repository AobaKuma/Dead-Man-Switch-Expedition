using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移燃料來源（設計稿 §5-2）。以介面抽象，VGE 等管線燃料可另外註冊 provider。
    /// </summary>
    public interface ITransferFuelSource
    {
        Thing Parent { get; }

        /// <summary>可用燃料，以化學燃料當量計（轉移數值模型的單位）。</summary>
        float Available { get; }

        /// <summary>扣除化學燃料當量 amount。</summary>
        void Draw(float amount);

        /// <summary>開發者指令：加滿。</summary>
        void DebugFill();
    }

    /// <summary>以 <see cref="CompRefuelable"/> 儲存化學燃料的來源。</summary>
    public class RefuelableFuelSource : ITransferFuelSource
    {
        private readonly CompRefuelable comp;

        public RefuelableFuelSource(CompRefuelable comp)
        {
            this.comp = comp;
        }

        public Thing Parent => comp.parent;

        public float Available => comp.Fuel;

        public void Draw(float amount)
        {
            comp.ConsumeFuel(Mathf.Min(amount, comp.Fuel));
        }

        public void DebugFill()
        {
            comp.Refuel(comp.Props.fuelCapacity - comp.Fuel);
        }
    }

    /// <summary>掛在其他 mod 的燃料槽 ThingDef 上，使其成為轉移燃料來源（仍需 CompRefuelable 可裝化學燃料）。</summary>
    public class TransferFuelSourceExtension : DefModExtension
    {
    }

    public static class TransferFuelSources
    {
        private static readonly List<Func<Thing, ITransferFuelSource>> providers = new List<Func<Thing, ITransferFuelSource>>();

        /// <summary>註冊額外的燃料來源判定；回傳 null 表示此 provider 不處理該物件。先註冊者優先，最後才是內建判定。</summary>
        public static void RegisterProvider(Func<Thing, ITransferFuelSource> provider)
        {
            if (provider != null && !providers.Contains(provider))
            {
                providers.Add(provider);
            }
        }

        public static ITransferFuelSource TryGetSource(Thing thing)
        {
            for (int i = 0; i < providers.Count; i++)
            {
                ITransferFuelSource source = providers[i](thing);
                if (source != null)
                {
                    return source;
                }
            }
            return BuiltInSource(thing);
        }

        /// <summary>
        /// 內建判定：<see cref="CompGravshipFacility"/> 的 providesFuel（原版化學燃料槽、DMSE 燃料筒倉；不需連到重力引擎），
        /// 或掛有 <see cref="TransferFuelSourceExtension"/>；且 CompRefuelable 允許化學燃料。
        /// </summary>
        private static ITransferFuelSource BuiltInSource(Thing thing)
        {
            ThingWithComps twc = thing as ThingWithComps;
            if (twc == null)
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
            CompRefuelable refuelable = twc.GetComp<CompRefuelable>();
            if (refuelable == null || refuelable.Props.fuelFilter == null || !refuelable.Props.fuelFilter.Allows(ThingDefOf.Chemfuel))
            {
                return null;
            }
            return new RefuelableFuelSource(refuelable);
        }

        public static float TotalAvailable(List<ITransferFuelSource> sources)
        {
            float total = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                total += sources[i].Available;
            }
            return total;
        }

        /// <summary>依各來源存量等比例扣除；回傳實際扣除量。</summary>
        public static float DrawProportionally(List<ITransferFuelSource> sources, float amount)
        {
            float total = TotalAvailable(sources);
            if (total <= 0f || amount <= 0f)
            {
                return 0f;
            }
            float ratio = Mathf.Min(1f, amount / total);
            float drawn = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                float take = sources[i].Available * ratio;
                sources[i].Draw(take);
                drawn += take;
            }
            return drawn;
        }
    }
}
