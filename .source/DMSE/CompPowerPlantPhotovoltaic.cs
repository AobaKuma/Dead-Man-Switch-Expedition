using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    [StaticConstructorOnStartup]
    public class CompPowerPlantPhotovoltaic : CompPowerPlant
    {
        private const float NightOutputFactor = 0.10f;   // 夜晚 10%
        private const float MinVacuumToOperate = 0.25f;   // 視為真空的門檻

        private static readonly Vector2 BarSize = new Vector2(1.4f, 0.08f);
        private static readonly Material BarFilledMat =
            SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.5f, 0.475f, 0.1f));
        private static readonly Material BarUnfilledMat =
            SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.15f, 0.15f, 0.15f));

        protected override float DesiredPowerOutput
        {
            get
            {
                if (parent?.Map == null) return 0f;

                // 只在真空中工作
                float vacuum = parent.Position.GetVacuum(parent.Map);
                if (vacuum < MinVacuumToOperate)
                    return 0f;

                // 白天 100%，夜晚保底 10%
                float skyGlow = parent.Map.skyManager.CurSkyGlow; // 0~1
                float solarFactor = Mathf.Lerp(NightOutputFactor, 1f, skyGlow);

                // CompProperties_Power.basePowerConsumption 在發電建築是負值
                float maxOutput = 0f - Props.PowerConsumption;
                return maxOutput * solarFactor * RoofedPowerOutputFactor;
            }
        }

        private float RoofedPowerOutputFactor
        {
            get
            {
                int total = 0;
                int roofed = 0;
                foreach (IntVec3 c in parent.OccupiedRect())
                {
                    total++;
                    if (parent.Map.roofGrid.Roofed(c))
                        roofed++;
                }
                return total == 0 ? 0f : (float)(total - roofed) / total;
            }
        }

        public override void PostDraw()
        {
            base.PostDraw();
            GenDraw.FillableBarRequest r = new GenDraw.FillableBarRequest
            {
                center = parent.DrawPos + Vector3.up * 0.1f,
                size = BarSize,
                fillPercent = PowerOutput / (0f - Props.PowerConsumption),
                filledMat = BarFilledMat,
                unfilledMat = BarUnfilledMat,
                margin = 0.15f
            };
            Rot4 rot = parent.Rotation;
            rot.Rotate(RotationDirection.Clockwise);
            r.rotation = rot;
            GenDraw.DrawFillableBar(r);
        }
    }
}
