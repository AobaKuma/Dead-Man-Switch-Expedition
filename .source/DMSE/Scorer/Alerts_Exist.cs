using RimWorld;
using RimWorld.Planet;
using System.Linq;
using System.Text;
using Verse;

namespace DMSE
{
    public class Alerts_Exist : Alert
    {
        public override string GetLabel()
        {
            Map map = Find.CurrentMap;
            if (map != null && GameComponent_MissileEngage.Comp.times.TryGetValue(map, out int time))
            {
                return "DMS_Exist".Translate((time - Find.TickManager.TicksGame)
                    .ToStringTicksToPeriod(true));
            }
            return base.GetLabel();
        }

        public override AlertReport GetReport()
        {
            Map map = Find.CurrentMap;
            if (map != null && GameComponent_MissileEngage.Comp.times.ContainsKey(map))
            {
                return AlertReport.Active;
            }
            return AlertReport.Inactive;
        }

        public override TaggedString GetExplanation()
        {
            Map map = Find.CurrentMap;
            if (map == null || !GameComponent_MissileEngage.Comp.times.TryGetValue(map, out int endTick))
                return base.GetExplanation();

            var sb = new StringBuilder();

            // 目標地圖名稱
            string mapLabel = map.Parent?.LabelCap ?? map.ToString();
            sb.AppendLine("DMSE.Alert.Exist.Observing".Translate(mapLabel));

            // 剩餘觀察時間
            int ticksLeft = endTick - Find.TickManager.TicksGame;
            sb.AppendLine("DMSE.Alert.Exist.TimeLeft".Translate(
                ticksLeft.ToStringTicksToPeriod(allowSeconds: false)));

            // 存活敵軍數量
            int enemyCount = map.mapPawns.AllPawnsSpawned
                .Count(p => !p.Dead && p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer));
            sb.AppendLine("DMSE.Alert.Exist.EnemiesAlive".Translate(enemyCount));

            // 若為導彈陣地，加入額外說明
            bool isMissileBase = map.Parent is Site s
                && s.GetComponent<WorldObjectComp_MissileBase>() != null;
            if (isMissileBase)
            {
                sb.AppendLine();
                sb.AppendLine("DMSE.Alert.Exist.IsMissileBase".Translate());
            }

            // 點擊提示
            sb.AppendLine();
            sb.AppendLine("DMSE.Alert.Exist.ClickHint".Translate());

            return sb.ToString().TrimEnd();
        }

        protected override void OnClick()
        {
            Map map = Find.CurrentMap;
            if (map == null || !GameComponent_MissileEngage.Comp.times.ContainsKey(map)) return;

            bool isMissileBase = map.Parent is Site site
                && site.GetComponent<WorldObjectComp_MissileBase>() != null;

            string confirmText = isMissileBase
                ? "DMSE.Alert.Exist.EndEarly.Confirm.MissileBase".Translate()
                : "DMSE.Alert.Exist.EndEarly.Confirm".Translate();

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                confirmText,
                () => EndObservationEarly(map),
                destructive: true));
        }

        private static void EndObservationEarly(Map map)
        {
            GameComponent_MissileEngage.Comp.times.Remove(map);

            Messages.Message(
                "DMSE.Alert.Exist.ObservationEnded".Translate(),
                MessageTypeDefOf.CautionInput,
                historical: false);
        }
    }
}
