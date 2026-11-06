using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>戰情終端面板：狀態總覽、遙控、交戰限制、操作員加成（§2.2–2.4）。</summary>
    public class ITab_WarRoom : ITab
    {
        private Vector2 scroll;
        private float contentHeight = 900f;

        public ITab_WarRoom()
        {
            size = new Vector2(460f, 620f);
            labelKey = "DMSE.EW.Tab";
        }

        protected override void FillTab()
        {
            CompWarRoomConsole console = SelThing?.TryGetComp<CompWarRoomConsole>();
            MapComponent_EWControl control = MapComponent_EWControl.For(SelThing?.Map);
            if (console == null || control == null) { return; }

            Rect outer = new Rect(0f, 0f, size.x, size.y).ContractedBy(12f);
            Rect view = new Rect(0f, 0f, outer.width - 18f, contentHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            var list = new Listing_Standard();
            list.Begin(new Rect(0f, 0f, view.width, 100000f));
            Text.Font = GameFont.Small;

            DrawOverview(list, control);
            if (console.PowerOK)
            {
                DrawRemote(list, control);
                DrawRoe(list, control);
            }
            else
            {
                list.Label("DMSE.EW.Tab.NoPower".Translate().Colorize(ColoredText.WarningColor));
            }
            DrawOperator(list, control);

            if (Event.current.type == EventType.Layout) { contentHeight = list.CurHeight + 12f; }
            list.End();
            Widgets.EndScrollView();
        }

        private static void Header(Listing_Standard list, string key)
        {
            list.Gap(6f);
            Text.Font = GameFont.Medium;
            list.Label(key.Translate());
            Text.Font = GameFont.Small;
        }

        private static void DrawOverview(Listing_Standard list, MapComponent_EWControl control)
        {
            Header(list, "DMSE.EW.Tab.Overview");
            MapComponent_BVRCombat bvr = control.map.GetComponent<MapComponent_BVRCombat>();
            List<ThingWithComps> radars = control.Devices(EWDeviceGroup.SearchRadars)
                .Concat(control.Devices(EWDeviceGroup.FireControlRadars)).Distinct().ToList();
            int online = radars.Count(r => CompBVRDevice.IsOperational(r));
            list.Label("DMSE.EW.Tab.RadarsOnline".Translate(online, radars.Count));
            foreach (CompJammer jammer in control.jammers.Where(j => j.parent.Faction == Faction.OfPlayer))
            {
                list.Label("DMSE.EW.Tab.JammerRow".Translate(jammer.parent.LabelShort,
                    jammer.CurrentJamPower.ToStringPercent("0"), JammerUtility.TracksCoveredBy(jammer)));
            }
            float jam = EWEnvironment.JamAt(MissileWorldUtility.TileCenter(control.map.Tile), Faction.OfPlayer);
            list.Label("DMSE.EW.Tab.LocalJam".Translate(jam.ToStringPercent("0")));
            // 基礎 × 功率 × 操作員 = 有效距離（對預設目標）。兩個係數相乘，要讓玩家算得出來。
            foreach (CompSearchRadar radar in bvr.searchRadars.Where(r => r.parent.Faction == Faction.OfPlayer))
            {
                list.Label("DMSE.EW.Tab.RadarRow".Translate(radar.parent.LabelShort,
                    MissileWorldMotion.DetectionRange(radar.Props.worldDetectionRange, radar.Props.antiStealthLevel, BVRTargetProps.Default, radar.Props.resolutionLevel).ToString("0.#"),
                    PowerThrottleUtility.RangeFactor(radar.parent).ToString("0.00"),
                    EWControlUtility.RadarRangeFactor(radar.parent).ToString("0.00"),
                    radar.EffectiveWorldRange(BVRTargetProps.Default).ToString("0.#")));
            }
            if (control.Silenced) { list.Label("DMSE.EW.Tab.SilencedWarning".Translate().Colorize(ColoredText.WarningColor)); }
            if (control.CommsJammed) { list.Label("DMSE.EW.Jammer.CommsBlocked".Translate().Colorize(ColoredText.WarningColor)); }
        }

        private static void DrawRemote(Listing_Standard list, MapComponent_EWControl control)
        {
            Header(list, "DMSE.EW.Tab.Remote");
            foreach (EWDeviceGroup group in new[] { EWDeviceGroup.Jammers, EWDeviceGroup.SearchRadars, EWDeviceGroup.FireControlRadars, EWDeviceGroup.All })
            {
                Rect row = list.GetRect(28f);
                Widgets.Label(row.LeftPart(0.34f), ("DMSE.EW.Group." + group).Translate());
                Rect right = row.RightPart(0.66f);
                float w = right.width / 3f;
                if (Widgets.ButtonText(new Rect(right.x, right.y, w - 2f, 26f), "DMSE.EW.Tab.PowerOn".Translate())) { control.SetPower(group, true); }
                if (Widgets.ButtonText(new Rect(right.x + w, right.y, w - 2f, 26f), "DMSE.EW.Tab.PowerOff".Translate())) { control.SetPower(group, false); }
                if (Widgets.ButtonText(new Rect(right.x + 2f * w, right.y, w - 2f, 26f), "DMSE.EW.Tab.Throttle".Translate()))
                {
                    var options = new List<FloatMenuOption>();
                    for (int s = 4; s >= 0; s--)
                    {
                        int chosen = s;
                        options.Add(new FloatMenuOption(CompPowerThrottle.StepLabel(s, 4), () => control.SetThrottle(group, chosen)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            if (control.Silenced)
            {
                if (list.ButtonText("DMSE.EW.Tab.Restore".Translate())) { control.Restore(); }
            }
            else if (list.ButtonText("DMSE.EW.Tab.Silence".Translate())) { control.Silence(); }
        }

        private static void DrawRoe(Listing_Standard list, MapComponent_EWControl control)
        {
            Header(list, "DMSE.EW.Tab.Roe");
            list.CheckboxLabeled("DMSE.EW.Roe.AutoEngage".Translate(), ref control.autoEngage, "DMSE.EW.Roe.AutoEngageDesc".Translate());
            bool all = control.engagePolicy == EngagePolicy.All;
            list.CheckboxLabeled("DMSE.EW.Roe.PolicyAll".Translate(), ref all, "DMSE.EW.Roe.PolicyAllDesc".Translate());
            control.engagePolicy = all ? EngagePolicy.All : EngagePolicy.SelfDefense;
            list.CheckboxLabeled("DMSE.EW.Roe.Neutral".Translate(), ref control.engageNeutral);
            bool largest = control.priority == EngagePriority.LargestFirst;
            list.CheckboxLabeled("DMSE.EW.Roe.LargestFirst".Translate(), ref largest, "DMSE.EW.Roe.LargestFirstDesc".Translate());
            control.priority = largest ? EngagePriority.LargestFirst : EngagePriority.FirstCome;

            string capLabel = control.channelCap < 0 ? "DMSE.EW.Roe.CapUnlimited".Translate().ToString() : control.channelCap.ToString();
            Rect capRow = list.GetRect(28f);
            Widgets.Label(capRow.LeftPart(0.6f), "DMSE.EW.Roe.ChannelCap".Translate(capLabel));
            Rect capButtons = capRow.RightPart(0.4f);
            if (Widgets.ButtonText(capButtons.LeftHalf(), "-")) { control.channelCap = Mathf.Max(MapComponent_EWControl.ChannelCapUnlimited, control.channelCap - 1); }
            if (Widgets.ButtonText(capButtons.RightHalf(), "+")) { control.channelCap++; }

            list.Label("DMSE.EW.Roe.HoldFire".Translate());
            HoldFireRow(list, control, HoldFireClass.Loitering);
            HoldFireRow(list, control, HoldFireClass.DropPod);
            HoldFireRow(list, control, HoldFireClass.Ballistic);
        }

        private static void HoldFireRow(Listing_Standard list, MapComponent_EWControl control, HoldFireClass cls)
        {
            bool hold = (control.holdFireClasses & cls) != 0;
            list.CheckboxLabeled("   " + ("DMSE.EW.Roe.Class." + cls).Translate(), ref hold);
            control.holdFireClasses = hold ? control.holdFireClasses | cls : control.holdFireClasses & ~cls;
        }

        private static void DrawOperator(Listing_Standard list, MapComponent_EWControl control)
        {
            Header(list, "DMSE.EW.Tab.Operator");
            int intellectual = control.BestOperatorLevel(SkillDefOf.Intellectual);
            int shooting = control.BestOperatorLevel(SkillDefOf.Shooting);
            if (intellectual < 0 && shooting < 0)
            {
                list.Label("DMSE.EW.Console.NoOperator".Translate());
                return;
            }
            list.Label("DMSE.EW.Tab.OperatorRange".Translate(intellectual, EWControlUtility.IntellectualRangeFactor(intellectual).ToString("0.00")));
            list.Label("DMSE.EW.Tab.OperatorCooldown".Translate(shooting, EWControlUtility.ShootingCooldownFactor(shooting).ToString("0.00")));
        }
    }
}
