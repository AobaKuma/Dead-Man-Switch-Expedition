using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    public class Alert_TransferCountdown : Alert
    {
        private readonly List<Thing> culprits = new List<Thing>();

        /// <summary>文字用的地圖（第一艘倒數中的船；潛艇用航海用語）。</summary>
        private Map textMap;

        public Alert_TransferCountdown()
        {
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel()
        {
            int ticks = int.MaxValue;
            foreach (CompTransferConsole console in TransferAlertUtility.LaunchingConsoles())
                if (console.State == TransferConsoleState.Countdown)
                    ticks = System.Math.Min(ticks, console.CountdownTicksLeft);
            return "DMSE_ShipWarmUpCountdown".Translate(
                ticks == int.MaxValue ? 0 : UnityEngine.Mathf.CeilToInt(ticks / 60f));
        }

        public override TaggedString GetExplanation()
        {
            return TransferText.Key("DMSE.Transfer.Alert.CountdownDesc", textMap).Translate();
        }

        public override AlertReport GetReport()
        {
            culprits.Clear();
            foreach (CompTransferConsole console in TransferAlertUtility.LaunchingConsoles())
                if (console.State == TransferConsoleState.Countdown) culprits.Add(console.parent);
            textMap = culprits.Count > 0 ? culprits[0].MapHeld : null;
            return AlertReport.CulpritsAre(culprits);
        }
    }

    /// <summary>起飛程序中（等待操作員或倒數）的船，每張地圖每艘一筆。</summary>
    internal static class TransferAlertUtility
    {
        public static IEnumerable<CompTransferConsole> LaunchingConsoles()
        {
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(maps[i]);
                if (comp == null)
                {
                    continue;
                }
                List<CompTransferConsole> consoles = comp.Consoles;
                for (int j = 0; j < consoles.Count; j++)
                {
                    if (consoles[j].InLaunchSequence)
                    {
                        yield return consoles[j];
                    }
                }
            }
        }
    }

    /// <summary>倒數期間：在原地圖、不在船體範圍內的殖民者/囚犯/奴隸/動物（左鍵輪流跳轉）。</summary>
    public class Alert_TransferPawnsOutside : Alert
    {
        private readonly List<Pawn> culprits = new List<Pawn>();

        /// <summary>文字用的地圖（第一艘起飛程序中的船；潛艇用航海用語）。</summary>
        private Map textMap;

        public Alert_TransferPawnsOutside()
        {
            defaultLabel = "DMSE.Transfer.Alert.PawnsOutside".Translate();
            defaultExplanation = "DMSE.Transfer.Alert.PawnsOutsideDesc".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() => TransferText.Key("DMSE.Transfer.Alert.PawnsOutside", textMap).Translate();

        public override TaggedString GetExplanation() => TransferText.Key("DMSE.Transfer.Alert.PawnsOutsideDesc", textMap).Translate();

        public override AlertReport GetReport()
        {
            culprits.Clear();
            textMap = null;
            foreach (CompTransferConsole console in TransferAlertUtility.LaunchingConsoles())
            {
                textMap = textMap ?? console.parent.Map;
                PileNetwork net = console.Network;
                if (net == null)
                {
                    continue;
                }
                HashSet<IntVec3> footprint = net.Footprint;
                IReadOnlyList<Pawn> pawns = console.parent.Map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if ((p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony) && !footprint.Contains(p.Position) && !culprits.Contains(p))
                    {
                        culprits.Add(p);
                    }
                }
            }
            return AlertReport.CulpritsAre(culprits);
        }
    }

    /// <summary>倒數期間：站在船上可用引擎尾焰區的 pawn。</summary>
    public class Alert_TransferExhaustDanger : Alert_Critical
    {
        private readonly List<Pawn> culprits = new List<Pawn>();

        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        public Alert_TransferExhaustDanger()
        {
            defaultLabel = "DMSE.Transfer.Alert.ExhaustDanger".Translate();
            defaultExplanation = "DMSE.Transfer.Alert.ExhaustDangerDesc".Translate();
        }

        public override AlertReport GetReport()
        {
            culprits.Clear();
            foreach (CompTransferConsole console in TransferAlertUtility.LaunchingConsoles())
            {
                TransferShipSurvey survey = console.Survey;
                // 尾焰不傷人的圖層（例如潛艇）不警告
                if (survey == null || TransferEnvironment.Presentation(console.parent.Map).exhaustDamage <= 0f)
                {
                    continue;
                }
                Map map = console.parent.Map;
                for (int i = 0; i < survey.engines.Count; i++)
                {
                    if (!survey.engines[i].Operational)
                    {
                        continue;
                    }
                    survey.engines[i].engine.GetExclusionZone(tmpCells);
                    for (int j = 0; j < tmpCells.Count; j++)
                    {
                        if (!tmpCells[j].InBounds(map))
                        {
                            continue;
                        }
                        List<Thing> things = map.thingGrid.ThingsListAtFast(tmpCells[j]);
                        for (int k = 0; k < things.Count; k++)
                        {
                            Pawn p = things[k] as Pawn;
                            if (p != null && !culprits.Contains(p))
                            {
                                culprits.Add(p);
                            }
                        }
                    }
                }
            }
            return AlertReport.CulpritsAre(culprits);
        }
    }
}
