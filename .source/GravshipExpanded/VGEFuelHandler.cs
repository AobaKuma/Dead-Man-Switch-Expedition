using RimWorld;
using System.Linq;
using Verse;

namespace DMSE.VGE
{
    [StaticConstructorOnStartup]
    public static class VGEFuelHandler
    {
        static VGEFuelHandler()
        {
            VGECompatibility.RegisterConsumeFuelDelegate(ConsumeFuel);
            Log.Message("[DMSE.VGE] VGE fuel handler registered");
        }

        public static void ConsumeFuel(Building_GravEngine engine, float cost)
        {
            Log.Message($"[DMSE.VGE] ConsumeFuel called: cost={cost}");

            float totalFuel = engine.TotalFuel;
            if (totalFuel <= 0f)
            {
                Log.Error("[DMSE.VGE] Cannot consume VGE fuel: TotalFuel is 0");
                return;
            }

            float ratio = cost / totalFuel;
            int tanksProcessed = 0;
            float totalConsumed = 0f;

            // 消耗Astrofuel
            foreach (var comp in engine.GravshipComponents)
            {
                if (comp.Props.providesFuel && comp.CanBeActive)
                {
                    var storage = comp.parent.AllComps.FirstOrDefault(c => c.GetType().Name == "CompResourceStorage");
                    if (storage != null)
                    {
                        var amountStoredProp = storage.GetType().GetProperty("AmountStored");
                        var drawResourceMethod = storage.GetType().GetMethod("DrawResource");

                        if (amountStoredProp != null && drawResourceMethod != null)
                        {
                            float amountStored = (float)amountStoredProp.GetValue(storage);
                            float toSpend = amountStored * ratio;
                            drawResourceMethod.Invoke(storage, new object[] { toSpend });

                            tanksProcessed++;
                            totalConsumed += toSpend;
                            Log.Message($"[DMSE.VGE] Tank {comp.parent.Label}: stored={amountStored}, consumed={toSpend}");
                        }
                    }
                }
            }

            Log.Message($"[DMSE.VGE] Processed {tanksProcessed} tanks, total consumed={totalConsumed}");

            // 添加热量
            var heatManager = engine.AllComps.FirstOrDefault(c => c.GetType().Name == "CompHeatManager");
            if (heatManager != null)
            {
                var addHeatMethod = heatManager.GetType().GetMethod("AddHeat");
                if (addHeatMethod != null)
                {
                    addHeatMethod.Invoke(heatManager, new object[] { cost });
                    Log.Message($"[DMSE.VGE] Added heat: {cost}");
                }
            }

            // 设置冷却时间
            int ticksGame = GenTicks.TicksGame;
            LaunchInfo launchInfo = engine.launchInfo;
            engine.cooldownCompleteTick = ticksGame + (int)GravshipUtility.LaunchCooldownFromQuality(
                launchInfo != null ? launchInfo.quality : 1f);
        }
    }
}
