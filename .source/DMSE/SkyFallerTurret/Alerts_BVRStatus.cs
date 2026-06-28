using RimWorld;
using System.Text;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 常駐顯示玩家當前地圖上所有 BVR 裝置（搜索雷達 + 火控雷達）的即時狀態。
    /// 只要玩家在當前地圖持有任一 BVR 裝置即啟動；與 <see cref="Alerts_Pods"/> 完全獨立，
    /// 無論是否有來襲目標，系統狀態始終可見。
    /// </summary>
    public class Alerts_BVRStatus : Alert
    {
        private static MapComponent_BVRCombat PlayerComp()
        {
            Map map = Find.CurrentMap;
            if (map == null) { return null; }
            MapComponent_BVRCombat comp = map.GetComponent<MapComponent_BVRCombat>();
            if (comp == null) { return null; }
            // 只要有任一隸屬玩家的 BVR 裝置就啟動。
            foreach (CompSearchRadar r in comp.searchRadars)
            {
                if (r.parent.Faction == Faction.OfPlayer) { return comp; }
            }
            foreach (CompFireControlRadar fc in comp.fireControlRadars)
            {
                if (fc.parent.Faction == Faction.OfPlayer) { return comp; }
            }
            return null;
        }

        public override string GetLabel() => "DMSE.Alert.BVRStatus.Label".Translate();

        public override AlertReport GetReport()
            => PlayerComp() != null ? AlertReport.Active : AlertReport.Inactive;

        public override TaggedString GetExplanation()
        {
            MapComponent_BVRCombat comp = PlayerComp();
            if (comp == null) { return base.GetExplanation(); }

            StringBuilder sb = new StringBuilder();

            // ---- 搜索雷達 ----
            sb.AppendLine("DMSE.Alert.BVRStatus.SearchRadars".Translate());
            bool anySearch = false;
            foreach (CompSearchRadar r in comp.searchRadars)
            {
                if (r.parent.Faction != Faction.OfPlayer) { continue; }
                anySearch = true;
                TaggedString status = r.Active
                    ? "DMSE.BVR.Online".Translate()
                    : "DMSE.BVR.Offline".Translate();
                sb.AppendLine("DMSE.Alert.BVRStatus.SearchRadar.Line".Translate(
                    r.parent.LabelShort,
                    r.Props.searchDistance,
                    r.Props.antiStealthLevel,
                    r.Props.powerLevel,
                    status));
            }
            if (!anySearch)
            {
                sb.AppendLine("DMSE.Alert.BVRStatus.None".Translate());
            }

            // ---- 火控雷達 ----
            sb.AppendLine();
            sb.AppendLine("DMSE.Alert.BVRStatus.FireControlRadars".Translate());
            bool anyFC = false;
            int totalChannels = 0;
            foreach (CompFireControlRadar fc in comp.fireControlRadars)
            {
                if (fc.parent.Faction != Faction.OfPlayer) { continue; }
                anyFC = true;
                if (fc.Active) { totalChannels += fc.Props.maxTargets; }
                TaggedString status = fc.Active
                    ? "DMSE.BVR.Online".Translate()
                    : "DMSE.BVR.Offline".Translate();
                sb.AppendLine("DMSE.Alert.BVRStatus.FireControl.Line".Translate(
                    fc.parent.LabelShort,
                    fc.Props.maxTargets,
                    fc.Props.powerLevel,
                    status));
            }
            if (!anyFC)
            {
                sb.AppendLine("DMSE.Alert.BVRStatus.None".Translate());
            }
            else if (totalChannels > 0)
            {
                sb.AppendLine("DMSE.Alert.BVRStatus.FireControl.Occupied".Translate(
                    comp.CountEngaged(), totalChannels));
            }

            return sb.ToString().TrimEnd();
        }
    }
}
