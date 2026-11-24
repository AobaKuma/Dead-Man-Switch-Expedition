using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>分導／誘餌載荷的介面文字（資訊卡、裝配頁、組裝台、打擊規劃；docs/MIRV分導彈頭與誘餌載荷_實作計畫.md §11）。</summary>
    public static class MirvText
    {
        public static MissileBehaviorProps_ReleaseChildren ReleaseOf(MissilePartDef part)
            => part?.behaviors?.OfType<MissileBehaviorProps_ReleaseChildren>().FirstOrDefault();

        public static MissileBehaviorProps_ReleaseChildren ReleaseOf(MissileConfig config)
            => config?.BehaviorProps.OfType<MissileBehaviorProps_ReleaseChildren>().FirstOrDefault();

        /// <summary>短句：「3 枚彈頭」「2 枚彈頭＋3 枚誘餌」「彈頭續飛＋5 枚誘餌」。</summary>
        public static string Short(MissileBehaviorProps_ReleaseChildren release)
        {
            if (release == null) { return null; }
            int warheads = release.CountOf(MirvRole.Warhead);
            int decoys = release.CountOf(MirvRole.Decoy);
            string head = release.retireParent
                ? "DMSE.Mirv.Short.Warheads".Translate(warheads).ToString()
                : "DMSE.Mirv.Short.Continues".Translate().ToString();
            return decoys > 0 ? "DMSE.Mirv.Short.WithDecoys".Translate(head, decoys).ToString() : head;
        }

        /// <summary>完整說明：時機、各組內容、繼承的部件、母彈去留。</summary>
        public static string Detail(MissileBehaviorProps_ReleaseChildren release)
        {
            if (release == null) { return null; }
            var lines = new List<string>();
            lines.Add(release.ticksBeforeArrival > 0
                ? "DMSE.Mirv.Detail.WhenTicks".Translate(release.ticksBeforeArrival.ToStringTicksToPeriod(), release.releaseIntervalTicks).ToString()
                : "DMSE.Mirv.Detail.WhenProgress".Translate(release.atProgress.ToStringPercent(), release.releaseIntervalTicks).ToString());
            foreach (MissileReleaseGroup group in release.Groups)
            {
                if (group == null || group.count <= 0) { continue; }
                string body = group.body?.LabelCap ?? "-";
                string line = "DMSE.Mirv.Detail.Group".Translate(group.count, body).ToString();
                if (!group.inheritCategories.NullOrEmpty())
                {
                    line += " " + "DMSE.Mirv.Detail.Inherits".Translate(
                        string.Join(", ", group.inheritCategories.Select(c => ("DMSE.Missile.Cat." + c).Translate().ToString()))).ToString();
                }
                if (group.body?.decoy != null)
                {
                    line += " " + (group.body.decoy.reentrySurvivalChance >= 1f
                        ? "DMSE.Mirv.Detail.DecoyReachesMap".Translate()
                        : group.body.decoy.reentrySurvivalChance <= 0f
                            ? "DMSE.Mirv.Detail.DecoyBurnsUp".Translate()
                            : "DMSE.Mirv.Detail.DecoySurvives".Translate(group.body.decoy.reentrySurvivalChance.ToStringPercent())).ToString();
                }
                lines.Add("  - " + line);
            }
            lines.Add((release.retireParent ? "DMSE.Mirv.Detail.BusRetires" : "DMSE.Mirv.Detail.BusContinues").Translate().ToString());
            lines.Add("DMSE.Mirv.Detail.SameTarget".Translate().ToString());
            return string.Join("\n", lines);
        }
    }
}
