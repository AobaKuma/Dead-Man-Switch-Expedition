using UnityEngine;
using Verse;
using RimWorld;

namespace DMSE
{
    public class CompProperties_BatteryTempSelfDischarge : CompProperties_Battery
    {
        // 20°C 無自放電
        public float zeroDischargeTemperature = 20f;

        // 距離 zero 多少度達峰值（40 -> 20±40）
        public float peakDistance = 40f;

        // 峰值自放電功率（W）
        public float maxSelfDischargeWatts = 100f;

        public CompProperties_BatteryTempSelfDischarge()
        {
            compClass = typeof(CompPowerBattery_TempSelfDischarge);
        }
    }

    public class CompPowerBattery_TempSelfDischarge : CompPowerBattery
    {
        // 方便轉型
        public new CompProperties_BatteryTempSelfDischarge Props
            => (CompProperties_BatteryTempSelfDischarge)props;

        public override void CompTick()
        {
            // 不呼叫 base.CompTick()，避免原版固定 5W 自放電被再算一次
            // base.CompTick() 在 CompPowerBattery 中會固定抽 5W (換算成 Wd/tick)

            float temp = parent.AmbientTemperature;
            float factor = Mathf.Clamp01(Mathf.Abs(temp - Props.zeroDischargeTemperature) / Props.peakDistance);

            // 線性 V 型：20°C => 0；到 20±40°C => 1
            float dischargeWatts = Props.maxSelfDischargeWatts * factor;

            // Watt -> Watt-day/tick，且不超過目前儲能
            float amountWd = Mathf.Min(dischargeWatts * RimWorld.CompPower.WattsToWattDaysPerTick, StoredEnergy);
            if (amountWd > 0f)
            {
                DrawPower(amountWd);
            }
        }

        public override string CompInspectStringExtra()
        {
            // 直接重寫，避免顯示原版固定 5W 文案
            var p = Props;
            string text =
                "PowerBatteryStored".Translate() + ": " + StoredEnergy.ToString("F0") + " / " + p.storedEnergyMax.ToString("F0") + " Wd" +
                "\n" + "PowerBatteryEfficiency".Translate() + ": " + (p.efficiency * 100f).ToString("F0") + "%";

            float temp = parent.AmbientTemperature;
            float factor = Mathf.Clamp01(Mathf.Abs(temp - p.zeroDischargeTemperature) / p.peakDistance);
            float dischargeWatts = p.maxSelfDischargeWatts * factor;

            if (StoredEnergy > 0f)
            {
                text += "\n" + "SelfDischarging".Translate() + ": " + dischargeWatts.ToString("F1") + " W";
            }

            // 追加電網資訊（相當於原版最底下 + base.CompInspectStringExtra()）
            string powerText = base.CompInspectStringExtra();
            if (!powerText.NullOrEmpty())
                text += "\n" + powerText;

            return text;
        }
    }
}