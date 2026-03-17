using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    public class Building_BatteryGraphene : Building
    {
        private int ticksToExplode;
        private Sustainer wickSustainer;

        private static readonly Vector2 BarSize = new Vector2(1.08f, 0.15f);
        private static readonly Material BatteryBarFilledMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.9f, 0.85f, 0.2f));
        private static readonly Material BatteryBarUnfilledMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.3f, 0.3f));

        private const float MinEnergyToExplode = 2000f;
        private const float EnergyToLoseWhenExplode = 400f;
        private const float ExplodeChancePerDamage = 0.01f;

        private CompPowerBattery_Graphene BatteryComp
        {
            get { return this.GetComp<CompPowerBattery_Graphene>(); }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ticksToExplode, "ticksToExplode", 0);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);

            CompPowerBattery_Graphene comp = BatteryComp;
            if (comp != null)
            {
                GenDraw.FillableBarRequest r = new GenDraw.FillableBarRequest
                {
                    center = drawLoc + Vector3.up * 0.01f + Vector3.forward * 0.18f,
                    size = BarSize,
                    fillPercent = comp.StoredEnergy / comp.Props.storedEnergyMax,
                    filledMat = BatteryBarFilledMat,
                    unfilledMat = BatteryBarUnfilledMat,
                    margin = 0.15f
                };
                Rot4 rotation = this.Rotation;
                rotation.Rotate(RotationDirection.Clockwise);
                r.rotation = rotation;
                GenDraw.DrawFillableBar(r);
            }

            if (ticksToExplode > 0 && this.Spawned)
            {
                this.Map.overlayDrawer.DrawOverlay(this, OverlayTypes.BurningWick);
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (ticksToExplode <= 0) return;

            if (wickSustainer == null) StartWickSustainer();
            else wickSustainer.Maintain();

            ticksToExplode--;
            if (ticksToExplode == 0)
            {
                GenExplosion.DoExplosion(
                    this.Spawned ? this.OccupiedRect().RandomCell : this.PositionHeld,
                    this.MapHeld,
                    Rand.Range(0.5f, 1f) * 3f,
                    DamageDefOf.Flame,
                    instigator: null);

                CompPowerBattery_Graphene comp = BatteryComp;
                if (comp != null) comp.DrawPower(EnergyToLoseWhenExplode);
            }
        }

        public override void PostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.PostApplyDamage(dinfo, totalDamageDealt);

            CompPowerBattery_Graphene comp = BatteryComp;
            if (!this.Destroyed
                && ticksToExplode == 0
                && comp != null
                && dinfo.Def == DamageDefOf.Flame
                && Rand.Value < ExplodeChancePerDamage
                && comp.StoredEnergy > MinEnergyToExplode)
            {
                ticksToExplode = Rand.Range(70, 150);
                StartWickSustainer();
            }
        }

        private void StartWickSustainer()
        {
            SoundInfo info = SoundInfo.InMap(this.SpawnedParentOrMe, MaintenanceType.PerTick);
            wickSustainer = SoundDefOf.HissSmall.TrySpawnSustainer(info);
        }
    }
}