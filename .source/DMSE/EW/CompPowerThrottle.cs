using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 通用功率調節（docs/ECM與ECCM_實作計畫.md §3）：段位 0–steps，存檔，預設滿段。
    /// 同一個 comp 掛在雷達與干擾機上；滿段時所有係數剛好是 1，與沒有掛 comp 的裝置位元相同。
    /// </summary>
    public class CompProperties_PowerThrottle : CompProperties
    {
        public int steps = 4;

        /// <summary>滿段耗電（W）；≤ 0 時取 CompProperties_Power 的耗電。</summary>
        public float drawAtFull = -1f;

        /// <summary>段位 0 的耗電佔滿段的比例。</summary>
        public float idleFraction = 0.2f;

        public CompProperties_PowerThrottle() { compClass = typeof(CompPowerThrottle); }
    }

    public class CompPowerThrottle : ThingComp
    {
        private int step = -1;

        public CompProperties_PowerThrottle Props => (CompProperties_PowerThrottle)props;

        public int Steps => Mathf.Max(1, Props.steps);

        public int Step
        {
            get => step < 0 ? Steps : Mathf.Min(step, Steps);
            set
            {
                step = Mathf.Clamp(value, 0, Steps);
                ApplyPowerDraw();
            }
        }

        public float Fraction => (float)Step / Steps;
        public bool IsStandby => Step == 0;

        public float DrawAtFull
        {
            get
            {
                if (Props.drawAtFull > 0f) { return Props.drawAtFull; }
                CompPowerTrader power = parent.GetComp<CompPowerTrader>();
                return power != null ? power.Props.PowerConsumption : 0f;
            }
        }

        private float DrawFactorAt(int atStep)
            => atStep >= Steps ? 1f : Props.idleFraction + (1f - Props.idleFraction) * atStep / Steps;

        public float DrawFactor => DrawFactorAt(Step);

        public float CurrentDraw => DrawAtFull * DrawFactor;

        /// <summary>
        /// 把段位寫進電力輸出。原版會在生成與重新接電時重設 PowerOutput，
        /// 所以 <see cref="MapComponent_EWControl"/> 也會定期重新套用。
        /// </summary>
        public void ApplyPowerDraw()
        {
            if (parent == null || !parent.Spawned) { return; }
            CompPowerTrader power = parent.GetComp<CompPowerTrader>();
            if (power == null) { return; }
            float output = -CurrentDraw;
            if (!Mathf.Approximately(power.powerOutputInt, output)) { power.PowerOutput = output; }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_EWControl.For(parent.Map)?.throttles.Add(this);
            ApplyPowerDraw();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_EWControl.For(map)?.throttles.Remove(this);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref step, "throttleStep", -1);
        }

        public static string StepLabel(int step, int steps)
            => step <= 0 ? "DMSE.EW.Throttle.Standby".Translate().ToString() : ((float)step / steps).ToStringPercent("0");

        public override string CompInspectStringExtra()
            => "DMSE.EW.Throttle.Inspect".Translate(StepLabel(Step, Steps), CurrentDraw.ToString("0"));

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra()) { yield return gizmo; }
            if (parent.Faction != Faction.OfPlayer) { yield break; }
            yield return new Command_Action
            {
                defaultLabel = "DMSE.EW.Throttle.Gizmo".Translate(StepLabel(Step, Steps)),
                defaultDesc = "DMSE.EW.Throttle.GizmoDesc".Translate(),
                icon = parent.def.uiIcon,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    for (int s = Steps; s >= 0; s--)
                    {
                        int chosen = s;
                        float draw = DrawAtFull * DrawFactorAt(s);
                        options.Add(new FloatMenuOption($"{StepLabel(s, Steps)} ({draw:0} W)", () => Step = chosen));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                },
            };
        }
    }

    /// <summary>
    /// 所有消費者只透過這裡取功率段位的係數。沒有掛 comp 的裝置一律回 1（SeekerLevel 回 baseLevel），
    /// 所以舊 Def、舊存檔與聲學雷達都不受影響。
    /// </summary>
    public static class PowerThrottleUtility
    {
        private static BVRWorldTuningDef Tuning => BVRWorldTuningDef.Current;

        public static float Fraction(ThingWithComps thing)
        {
            CompPowerThrottle throttle = thing?.GetComp<CompPowerThrottle>();
            return throttle != null ? throttle.Fraction : 1f;
        }

        private static float Floored(ThingWithComps thing, float floor)
        {
            float f = Fraction(thing);
            return f >= 1f ? 1f : Mathf.Lerp(floor, 1f, f);
        }

        public static float WindowFactor(ThingWithComps thing) => Floored(thing, Tuning.throttleWindowFloor);
        public static float RangeFactor(ThingWithComps thing) => Floored(thing, Tuning.throttleRangeFloor);
        public static float HitFactor(ThingWithComps thing) => Floored(thing, Tuning.throttleHitFloor);

        public static float LockFactor(ThingWithComps thing)
        {
            float f = Fraction(thing);
            return f >= 1f ? 1f : 1f + Tuning.throttleLockPenalty * (1f - f);
        }

        public static float JamFactor(ThingWithComps thing) => Fraction(thing);

        public static float DrawFactor(ThingWithComps thing)
        {
            CompPowerThrottle throttle = thing?.GetComp<CompPowerThrottle>();
            return throttle != null ? throttle.DrawFactor : 1f;
        }

        /// <summary>反輻射導引頭看到的等效功率：低功率的裝置較晚被咬，待機為 0。</summary>
        public static int SeekerLevel(ThingWithComps thing, int baseLevel)
        {
            CompPowerThrottle throttle = thing?.GetComp<CompPowerThrottle>();
            if (throttle == null || throttle.Step >= throttle.Steps) { return baseLevel; }
            return throttle.Step == 0 ? 0 : Mathf.Max(1, Mathf.CeilToInt(baseLevel * throttle.Fraction));
        }
    }
}
