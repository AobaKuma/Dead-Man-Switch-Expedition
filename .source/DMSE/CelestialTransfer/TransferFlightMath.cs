using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移飛行數值模型（設計稿 §5-3）。常數取自 <see cref="TransferFlightTuningDef"/>。
    /// <code>
    /// 燃料需求     F      = W × (B + P × d) × η
    /// 最大航程     d_max  = floor( (Fuel / (W × η) − B) / P )
    /// 單次點火時間 t_burn = clamp( burnHoursRef × twrRef / TWR , minBurnHours , maxBurnHours )
    ///              （離軌點火的 TWR 含一次性助推器，入軌點火只計引擎）
    /// 滑行時間     t_coast = d × coastHoursPerTile
    /// 總時間       t_total = t_burn(離軌) + t_coast + t_burn(入軌)
    /// </code>
    /// </summary>
    public static class TransferFlightMath
    {
        public static TransferFlightTuningDef Tuning => DMSE_DefOf.DMSE_TransferFlightTuning;

        public static float FuelRequired(float massWeight, float eta, int distance)
        {
            TransferFlightTuningDef t = Tuning;
            return massWeight * (t.fuelBasePerWeight + t.fuelPerTilePerWeight * distance) * eta;
        }

        /// <summary>以現有燃料可達的最大距離；連基礎項都付不起時為 -1。</summary>
        public static int MaxDistance(float fuel, float massWeight, float eta)
        {
            TransferFlightTuningDef t = Tuning;
            if (massWeight <= 0f || eta <= 0f || t.fuelPerTilePerWeight <= 0f)
            {
                return -1;
            }
            float perWeight = fuel / (massWeight * eta);
            if (perWeight < t.fuelBasePerWeight)
            {
                return -1;
            }
            return Mathf.FloorToInt((perWeight - t.fuelBasePerWeight) / t.fuelPerTilePerWeight);
        }

        /// <summary>推力越大點火越短；推重比為 0 時取上限。</summary>
        public static float BurnHours(float twr)
        {
            TransferFlightTuningDef t = Tuning;
            if (twr <= 0f)
            {
                return t.maxBurnHours;
            }
            return Mathf.Clamp(t.burnHoursRef * t.twrRef / twr, t.minBurnHours, t.maxBurnHours);
        }

        public static float CoastHours(int distance)
        {
            return distance * Tuning.coastHoursPerTile;
        }

        public static float TotalHours(float departureTwr, float arrivalTwr, int distance)
        {
            return BurnHours(departureTwr) + CoastHours(distance) + BurnHours(arrivalTwr);
        }

        public static int HoursToTicks(float hours)
        {
            return Mathf.RoundToInt(hours * GenDate.TicksPerHour);
        }

        public static int Distance(PlanetTile from, PlanetTile to)
        {
            return Find.WorldGrid.TraversalDistanceBetween(from, to, passImpassable: true);
        }
    }

    /// <summary>某艘船（網路）以某距離轉移時的估算結果；ITab、確認視窗與整備檢查共用。</summary>
    public class TransferEstimate
    {
        public float massWeight;

        public float thrust;

        /// <summary>一次性助推器推力（只在離軌點火）。</summary>
        public float boosterThrust;

        /// <summary>離軌點火推重比（含助推器）。</summary>
        public float twr;

        public float eta;

        public float fuelAvailable;

        public int maxDistance;

        /// <summary>未指定目的地時為 -1，以下各項只有 distance ≥ 0 時有意義。</summary>
        public int distance = -1;

        public float fuelRequired;

        /// <summary>離軌點火時間。</summary>
        public float burnHours;

        /// <summary>入軌點火時間（助推器已燒毀）；撞擊飛行只用一半。</summary>
        public float arrivalBurnHours;

        public float coastHours;

        public float totalHours;

        public bool HasDestination => distance >= 0;

        public bool EnoughFuel => !HasDestination || fuelAvailable >= fuelRequired;

        /// <param name="impact">撞擊飛行：沒有入軌減速，到達撞擊點上方時的速度直接撞向地表（總時間 = 離軌點火 + 滑行 + 0.5 × 入軌點火）。</param>
        public static TransferEstimate For(TransferShipSurvey survey, int distance, bool impact = false)
        {
            TransferEstimate e = new TransferEstimate
            {
                massWeight = survey.MassWeight,
                thrust = survey.totalThrust,
                boosterThrust = survey.boosterThrust,
                twr = survey.TWR,
                eta = survey.Eta,
                fuelAvailable = survey.totalFuel
            };
            e.maxDistance = TransferFlightMath.MaxDistance(e.fuelAvailable, e.massWeight, e.eta);
            e.burnHours = TransferFlightMath.BurnHours(e.twr);
            e.arrivalBurnHours = TransferFlightMath.BurnHours(survey.ArrivalTWR);
            if (distance >= 0)
            {
                e.distance = distance;
                e.fuelRequired = TransferFlightMath.FuelRequired(e.massWeight, e.eta, distance);
                e.coastHours = TransferFlightMath.CoastHours(distance);
                e.totalHours = e.burnHours + e.coastHours + (impact ? 0.5f : 1f) * e.arrivalBurnHours;
            }
            return e;
        }
    }
}
