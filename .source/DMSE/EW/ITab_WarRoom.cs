using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 戰情終端面板（§2.2–2.4）。頂部狀態列回答「防空現在會不會開火」；下方左欄是裝置，右欄是交戰限制。
    /// 終端沒電時規則仍然生效，所以照常畫出來，只是不能改（editable = false）。
    /// </summary>
    public class ITab_WarRoom : ITab
    {
        // 間距刻度：4 / 8 / 16 / 20，群組外距一定大於群組內距。頂部留 20 避開面板右上角的關閉鈕。
        private const float TopInset = 20f;
        private const float Inset = 12f;
        private const float ItemGap = 8f;
        private const float SectionGap = 16f;
        private const float ColumnGap = 20f;
        private const float RowHeight = 28f;
        private const float SubRowHeight = 22f;
        private const float HeaderHeight = 24f;
        private const float ControlHeight = 24f;
        private const float TileHeight = 56f;
        private const float SilenceWidth = 96f;
        private const float ToggleWidth = 52f;
        private const float ThrottleWidth = 68f;
        private const float StepperWidth = 28f;
        private const float StepperValueWidth = 52f;
        private const float SegmentWidth = 76f;
        private const float ScrollbarWidth = 16f;

        private static readonly Color Muted = new Color(0.65f, 0.67f, 0.70f);
        private static readonly Color HeaderColor = new Color(0.79f, 0.73f, 0.56f);
        private static readonly Color Positive = new Color(0.55f, 0.85f, 0.45f);
        private static readonly Color Dimmed = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color UnselectedSegment = new Color(0.7f, 0.7f, 0.7f);

        private Vector2 scroll;
        private float contentHeight;

        public ITab_WarRoom()
        {
            size = new Vector2(680f, 530f);
            labelKey = "DMSE.EW.Tab";
        }

        /// <summary>NPC 陣地的終端不給玩家遙控與交戰限制面板。</summary>
        public override bool IsVisible => base.IsVisible && SelThing?.Faction == Faction.OfPlayer;

        protected override void FillTab()
        {
            CompWarRoomConsole console = SelThing?.TryGetComp<CompWarRoomConsole>();
            MapComponent_EWControl control = MapComponent_EWControl.For(SelThing?.Map);
            if (console == null || control == null) { return; }

            Text.Font = GameFont.Small;
            Rect outer = new Rect(Inset, TopInset, size.x - 2f * Inset, size.y - TopInset - Inset);
            float width = outer.width - (contentHeight > outer.height ? ScrollbarWidth : 0f);
            Rect view = new Rect(0f, 0f, width, Mathf.Max(contentHeight, outer.height));
            Widgets.BeginScrollView(outer, ref scroll, view);

            bool editable = console.PowerOK;
            float y = 0f;
            DrawStatusStrip(new Rect(0f, y, width, TileHeight), control, editable);
            y += TileHeight + ItemGap;
            DrawWarnings(width, ref y, control, editable);
            y += SectionGap - ItemGap;

            float columnWidth = (width - ColumnGap) / 2f;
            float leftY = y;
            DrawRemote(0f, columnWidth, ref leftY, control, editable);
            DrawPerformance(0f, columnWidth, ref leftY, control);
            DrawOperator(0f, columnWidth, ref leftY, control);
            float rightY = y;
            DrawRoe(columnWidth + ColumnGap, columnWidth, ref rightY, control, editable);
            contentHeight = Mathf.Max(leftY, rightY);

            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        // ====================================================================
        //  狀態列
        // ====================================================================

        private static void DrawStatusStrip(Rect strip, MapComponent_EWControl control, bool editable)
        {
            List<ThingWithComps> radars = control.Devices(EWDeviceGroup.SearchRadars)
                .Concat(control.Devices(EWDeviceGroup.FireControlRadars)).Distinct().ToList();
            int online = radars.Count(r => CompBVRDevice.IsOperational(r));
            float jam = EWEnvironment.JamAt(MissileWorldUtility.TileCenter(control.map.Tile), Faction.OfPlayer);

            const float gap = 8f;
            const float smallTile = 108f;
            float stateWidth = strip.width - SilenceWidth - 3f * smallTile - 4f * gap;
            float x = strip.x;
            string state = StateLabel(control, online, out Color stateColor);
            DrawTile(new Rect(x, strip.y, stateWidth, strip.height), "DMSE.EW.Tab.StateLabel".Translate(), state, stateColor);
            x += stateWidth + gap;
            DrawTile(new Rect(x, strip.y, smallTile, strip.height), "DMSE.EW.Tab.RadarsOnline".Translate(), $"{online} / {radars.Count}",
                online < radars.Count ? ColoredText.WarningColor : Color.white);
            x += smallTile + gap;
            DrawTile(new Rect(x, strip.y, smallTile, strip.height), "DMSE.EW.Tab.LocalJam".Translate(), jam.ToStringPercent("0"),
                jam > 0f ? ColoredText.WarningColor : Color.white);
            x += smallTile + gap;
            Rect channels = new Rect(x, strip.y, smallTile, strip.height);
            DrawTile(channels, "DMSE.EW.Tab.Channels".Translate(), ChannelSummary(control), Color.white);
            TooltipHandler.TipRegion(channels, "DMSE.EW.Tab.ChannelsDesc".Translate());
            x += smallTile + gap;

            Rect silence = new Rect(x, strip.y, SilenceWidth, strip.height);
            if (control.Silenced)
            {
                if (Button(silence, "DMSE.EW.Tab.Restore".Translate(), editable, "DMSE.EW.Tab.RestoreDesc".Translate())) { control.Restore(); }
            }
            else
            {
                bool anyOn = control.Devices(EWDeviceGroup.All).Any(d => d.GetComp<CompFlickable>()?.SwitchIsOn ?? false);
                if (Button(silence, "DMSE.EW.Tab.Silence".Translate(), editable && anyOn, "DMSE.EW.Tab.SilenceDesc".Translate())) { control.Silence(); }
            }
        }

        /// <summary>一句話回答防空現在會不會開火；顏色只是輔助，文字本身就說明狀態。</summary>
        private static string StateLabel(MapComponent_EWControl control, int onlineRadars, out Color color)
        {
            color = ColoredText.WarningColor;
            if (control.Silenced) { return "DMSE.EW.Tab.StateSilenced".Translate(); }
            if (!control.autoEngage || control.channelCap == 0) { return "DMSE.EW.Tab.StateHold".Translate(); }
            if (onlineRadars == 0) { return "DMSE.EW.Tab.StateNoRadar".Translate(); }
            color = Positive;
            return control.engagePolicy == EngagePolicy.All
                ? "DMSE.EW.Tab.StateAll".Translate()
                : "DMSE.EW.Tab.StateSelfDefense".Translate();
        }

        /// <summary>防空佔用的通道 / 防空目前可用的通道（扣掉砲擊與打擊，再套用通道上限）。</summary>
        private static string ChannelSummary(MapComponent_EWControl control)
        {
            BVRChannelLedger ledger = control.map.GetComponent<MapComponent_BVRCombat>().Channels;
            Faction player = Faction.OfPlayer;
            int available = Mathf.Max(0, ledger.Capacity(player) - ledger.Artillery(player) - ledger.Strike(player));
            if (control.channelCap >= 0) { available = Mathf.Min(available, control.channelCap); }
            return $"{ledger.Local(player) + ledger.World(player)} / {available}";
        }

        private static void DrawTile(Rect rect, string label, string value, Color valueColor)
        {
            Widgets.DrawLightHighlight(rect);
            Rect inner = rect.ContractedBy(8f, 4f);
            // Tiny 的中文在 1080p 下太糊，只有放不下時才退回。
            if (Text.CalcSize(label).x > inner.width) { Text.Font = GameFont.Tiny; }
            GUI.color = Muted;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, SubRowHeight), label);

            // 英文等較長的狀態放不下 Medium 時退回 Small，不換行也不截斷。
            Text.Font = GameFont.Medium;
            if (Text.CalcSize(value).x > inner.width) { Text.Font = GameFont.Small; }
            GUI.color = valueColor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(inner.x, inner.y + SubRowHeight, inner.width, inner.height - SubRowHeight), value);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        private static void DrawWarnings(float width, ref float y, MapComponent_EWControl control, bool editable)
        {
            if (!editable) { Warning(width, ref y, "DMSE.EW.Tab.NoPower".Translate()); }
            if (control.Silenced) { Warning(width, ref y, "DMSE.EW.Tab.SilencedWarning".Translate()); }
            if (control.CommsJammed) { Warning(width, ref y, "DMSE.EW.Jammer.CommsBlocked".Translate()); }
        }

        private static void Warning(float width, ref float y, string text)
        {
            float height = Text.CalcHeight(text, width);
            GUI.color = ColoredText.WarningColor;
            Widgets.Label(new Rect(0f, y, width, height), text);
            GUI.color = Color.white;
            y += height + 4f;
        }

        // ====================================================================
        //  左欄：遙控、裝置效能、操作員
        // ====================================================================

        private static void DrawRemote(float x, float width, ref float y, MapComponent_EWControl control, bool editable)
        {
            Header(x, width, ref y, "DMSE.EW.Tab.Remote".Translate());
            RemoteRow(x, width, ref y, control, EWDeviceGroup.Jammers, editable);
            RemoteRow(x, width, ref y, control, EWDeviceGroup.SearchRadars, editable);
            RemoteRow(x, width, ref y, control, EWDeviceGroup.FireControlRadars, editable);
            RemoteRow(x, width, ref y, control, EWDeviceGroup.All, editable);
            y += SectionGap;
        }

        private static void RemoteRow(float x, float width, ref float y, MapComponent_EWControl control, EWDeviceGroup group, bool editable)
        {
            Rect row = new Rect(x, y, width, RowHeight);
            y += RowHeight;
            List<ThingWithComps> devices = control.Devices(group).ToList();
            string name = ("DMSE.EW.Group." + group).Translate();
            if (devices.Count == 0)
            {
                // 沒有裝置就沒有東西可按：「全部」列整列隱藏，其他列說明未部署。
                if (group == EWDeviceGroup.All) { y -= RowHeight; return; }
                RowLabel(row, name);
                RowValue(row, "DMSE.EW.Tab.NotDeployed".Translate(), Muted);
                return;
            }

            List<CompFlickable> switches = devices.Select(d => d.GetComp<CompFlickable>()).Where(f => f != null).ToList();
            List<CompPowerThrottle> throttles = devices.Select(d => d.GetComp<CompPowerThrottle>()).Where(t => t != null).ToList();
            int on = switches.Count(f => f.SwitchIsOn);

            float right = row.xMax;
            if (throttles.Count > 0)
            {
                right -= ThrottleWidth;
                Rect throttle = new Rect(right, row.y + (RowHeight - ControlHeight) / 2f, ThrottleWidth, ControlHeight);
                if (Button(throttle, ThrottleLabel(throttles) + " ▾", editable)) { OpenThrottleMenu(control, group, throttles); }
                right -= 4f;
            }
            // 「全部」只留功率：全部關機與一鍵靜默重複，後者還能還原。
            if (group != EWDeviceGroup.All && switches.Count > 0)
            {
                right -= ToggleWidth;
                bool allOn = on == switches.Count;
                Rect toggle = new Rect(right, row.y + (RowHeight - ControlHeight) / 2f, ToggleWidth, ControlHeight);
                string action = (allOn ? "DMSE.EW.Tab.PowerOff" : "DMSE.EW.Tab.PowerOn").Translate();
                if (Button(toggle, action, editable)) { control.SetPower(group, !allOn); }
                right -= 4f;
            }

            float nameWidth = Mathf.Min(Text.CalcSize(name).x, right - row.x);
            RowLabel(new Rect(row.x, row.y, nameWidth, RowHeight), name);
            if (switches.Count > 0)
            {
                Rect statusRect = new Rect(row.x + nameWidth + 6f, row.y, Mathf.Max(0f, right - row.x - nameWidth - 6f), RowHeight);
                RowLabel(statusRect, "DMSE.EW.Tab.GroupStatus".Translate(on, switches.Count), on < switches.Count ? ColoredText.WarningColor : Muted);
            }
        }

        private static string ThrottleLabel(List<CompPowerThrottle> throttles)
        {
            float first = throttles[0].Fraction;
            if (throttles.Any(t => !Mathf.Approximately(t.Fraction, first))) { return "DMSE.EW.Tab.ThrottleMixed".Translate(); }
            return CompPowerThrottle.StepLabel(throttles[0].Step, throttles[0].Steps);
        }

        private static void OpenThrottleMenu(MapComponent_EWControl control, EWDeviceGroup group, List<CompPowerThrottle> throttles)
        {
            int steps = throttles.Max(t => t.Steps);
            var options = new List<FloatMenuOption>();
            for (int s = steps; s >= 0; s--)
            {
                int chosen = s;
                options.Add(new FloatMenuOption(CompPowerThrottle.StepLabel(s, steps), () => control.SetThrottle(group, chosen)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void DrawPerformance(float x, float width, ref float y, MapComponent_EWControl control)
        {
            MapComponent_BVRCombat bvr = control.map.GetComponent<MapComponent_BVRCombat>();
            List<CompSearchRadar> radars = bvr.searchRadars.Where(r => r.parent.Faction == Faction.OfPlayer).ToList();
            List<CompJammer> jammers = control.jammers.Where(j => j.parent.Faction == Faction.OfPlayer).ToList();
            if (radars.Count == 0 && jammers.Count == 0) { return; }

            Header(x, width, ref y, "DMSE.EW.Tab.Performance".Translate());
            foreach (CompSearchRadar radar in radars)
            {
                Rect row = new Rect(x, y, width, RowHeight);
                RowLabel(row, radar.parent.LabelShort);
                RowValue(row, "DMSE.EW.Tab.RadarRange".Translate(radar.EffectiveWorldRange(BVRTargetProps.Default).ToString("0.#")), Color.white);
                y += RowHeight;
                // 基礎 × 功率 × 操作員 = 有效距離（對預設目標）。兩個係數相乘，要讓玩家算得出來。
                SubRow(x, width, ref y, "DMSE.EW.Tab.RadarRow".Translate(
                    MissileWorldMotion.DetectionRange(radar.Props.worldDetectionRange, radar.Props.antiStealthLevel, BVRTargetProps.Default, radar.Props.resolutionLevel).ToString("0.#"),
                    PowerThrottleUtility.RangeFactor(radar.parent).ToString("0.00"),
                    EWControlUtility.RadarRangeFactor(radar.parent).ToString("0.00")));
            }
            foreach (CompJammer jammer in jammers)
            {
                Rect row = new Rect(x, y, width, RowHeight);
                RowLabel(row, jammer.parent.LabelShort);
                RowValue(row, "DMSE.EW.Tab.JammerRow".Translate(jammer.CurrentJamPower.ToStringPercent("0"), JammerUtility.TracksCoveredBy(jammer)), Color.white);
                y += RowHeight;
            }
            y += SectionGap;
        }

        private static void DrawOperator(float x, float width, ref float y, MapComponent_EWControl control)
        {
            Header(x, width, ref y, "DMSE.EW.Tab.Operator".Translate());
            int intellectual = control.OperatorLevelTotal(SkillDefOf.Intellectual, Faction.OfPlayer);
            int shooting = control.OperatorLevelTotal(SkillDefOf.Shooting, Faction.OfPlayer);
            if (intellectual < 0 && shooting < 0)
            {
                RowLabel(new Rect(x, y, width, RowHeight), "DMSE.EW.Console.NoOperator".Translate(), Muted);
                y += RowHeight;
                return;
            }

            string names = control.consoles
                .Where(c => c.parent.Faction == Faction.OfPlayer && c.Operator != null)
                .Select(c => c.Operator.LabelShort).ToCommaList();
            SubRow(x, width, ref y, "DMSE.EW.Console.Operator".Translate(names));
            if (intellectual >= 0)
            {
                float factor = EWControlUtility.IntellectualRangeFactor(intellectual);
                OperatorRow(x, width, ref y, "DMSE.EW.Tab.OperatorRange".Translate(intellectual, EWControlUtility.OperatorLevelCap, factor.ToString("0.00")), factor - 1f);
            }
            if (shooting >= 0)
            {
                float factor = EWControlUtility.ShootingCooldownFactor(shooting);
                OperatorRow(x, width, ref y, "DMSE.EW.Tab.OperatorCooldown".Translate(shooting, EWControlUtility.OperatorLevelCap, factor.ToString("0.00")), 1f - factor);
            }
        }

        /// <param name="benefit">大於 0 是加成，小於 0 是懲罰（冷卻倍率越小越好，不能只看數字方向）。</param>
        private static void OperatorRow(float x, float width, ref float y, string text, float benefit)
        {
            Rect row = new Rect(x, y, width, RowHeight);
            RowLabel(row, text);
            if (Mathf.Abs(benefit) > 0.001f)
            {
                if (benefit > 0f) { RowValue(row, "DMSE.EW.Tab.OperatorBonus".Translate(), Positive); }
                else { RowValue(row, "DMSE.EW.Tab.OperatorPenalty".Translate(), ColoredText.WarningColor); }
            }
            y += RowHeight;
        }

        // ====================================================================
        //  右欄：交戰限制
        // ====================================================================

        private static void DrawRoe(float x, float width, ref float y, MapComponent_EWControl control, bool editable)
        {
            Header(x, width, ref y, "DMSE.EW.Tab.Roe".Translate());
            CheckRow(x, width, ref y, "DMSE.EW.Roe.AutoEngage".Translate(), ref control.autoEngage, editable, "DMSE.EW.Roe.AutoEngageDesc".Translate());

            // 自動接戰關閉時下方設定暫不生效（AllowsEngagement 直接回 false），但仍可預先調整，所以只變暗不鎖定。
            Color previous = GUI.color;
            if (!control.autoEngage) { GUI.color = Dimmed; }

            Rect policy = new Rect(x, y, width, RowHeight);
            y += RowHeight;
            int chosen = SegmentRow(policy, "DMSE.EW.Roe.Policy".Translate(), "DMSE.EW.Roe.PolicySelfDefense".Translate(), "DMSE.EW.Roe.PolicyAll".Translate(),
                control.engagePolicy == EngagePolicy.SelfDefense, editable, "DMSE.EW.Roe.PolicyDesc".Translate());
            if (chosen == 0) { control.engagePolicy = EngagePolicy.SelfDefense; }
            else if (chosen == 1) { control.engagePolicy = EngagePolicy.All; }

            Rect priority = new Rect(x, y, width, RowHeight);
            y += RowHeight;
            chosen = SegmentRow(priority, "DMSE.EW.Roe.Priority".Translate(), "DMSE.EW.Roe.PriorityFirstCome".Translate(), "DMSE.EW.Roe.PriorityLargest".Translate(),
                control.priority == EngagePriority.FirstCome, editable, "DMSE.EW.Roe.PriorityDesc".Translate());
            if (chosen == 0) { control.priority = EngagePriority.FirstCome; }
            else if (chosen == 1) { control.priority = EngagePriority.LargestFirst; }

            CheckRow(x, width, ref y, "DMSE.EW.Roe.Neutral".Translate(), ref control.engageNeutral, editable, null);
            ChannelCapRow(x, width, ref y, control, editable);

            y += SectionGap;
            Header(x, width, ref y, "DMSE.EW.Roe.Intercept".Translate());
            InterceptRow(x, width, ref y, control, HoldFireClass.Loitering, editable);
            InterceptRow(x, width, ref y, control, HoldFireClass.DropPod, editable);
            InterceptRow(x, width, ref y, control, HoldFireClass.Ballistic, editable);
            GUI.color = previous;
            y += SectionGap;
        }

        private static void ChannelCapRow(float x, float width, ref float y, MapComponent_EWControl control, bool editable)
        {
            Rect row = new Rect(x, y, width, RowHeight);
            y += RowHeight;
            RowLabel(row, "DMSE.EW.Roe.ChannelCap".Translate());
            TooltipHandler.TipRegion(row, "DMSE.EW.Roe.ChannelCapDesc".Translate());

            int cap = control.channelCap;
            int capacity = control.map.GetComponent<MapComponent_BVRCombat>().Channels.Capacity(Faction.OfPlayer);
            float top = row.y + (RowHeight - ControlHeight) / 2f;
            Rect plus = new Rect(row.xMax - StepperWidth, top, StepperWidth, ControlHeight);
            Rect value = new Rect(plus.x - StepperValueWidth, row.y, StepperValueWidth, RowHeight);
            Rect minus = new Rect(value.x - StepperWidth, top, StepperWidth, ControlHeight);
            if (Button(minus, "-", editable && cap != 0)) { control.channelCap = EWControlUtility.NextChannelCap(cap, capacity, -1); }
            if (Button(plus, "+", editable && cap >= 0)) { control.channelCap = EWControlUtility.NextChannelCap(cap, capacity, 1); }

            string label = cap < 0 ? "DMSE.EW.Roe.CapUnlimited".Translate().ToString() : cap.ToString();
            Color color = GUI.color;
            if (cap == 0) { GUI.color = ColoredText.WarningColor; }
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(value, label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = color;
        }

        /// <summary>介面顯示「攔截」；儲存的仍是 holdFireClasses（不攔截的類別），舊存檔不受影響。</summary>
        private static void InterceptRow(float x, float width, ref float y, MapComponent_EWControl control, HoldFireClass cls, bool editable)
        {
            bool intercept = (control.holdFireClasses & cls) == 0;
            CheckRow(x, width, ref y, ("DMSE.EW.Roe.Class." + cls).Translate(), ref intercept, editable, null);
            control.holdFireClasses = intercept ? control.holdFireClasses & ~cls : control.holdFireClasses | cls;
        }

        // ====================================================================
        //  小工具
        // ====================================================================

        private static void Header(float x, float width, ref float y, string label)
        {
            Color color = GUI.color;
            GUI.color = color * HeaderColor;
            Widgets.Label(new Rect(x, y, width, HeaderHeight - 4f), label);
            GUI.color = Widgets.SeparatorLineColor;
            Widgets.DrawLineHorizontal(x, y + HeaderHeight - 3f, width);
            GUI.color = color;
            y += HeaderHeight;
        }

        private static void RowLabel(Rect rect, string text) => RowText(rect, text, GUI.color, TextAnchor.MiddleLeft);

        private static void RowLabel(Rect rect, string text, Color color) => RowText(rect, text, color, TextAnchor.MiddleLeft);

        private static void RowValue(Rect rect, string text, Color color) => RowText(rect, text, color, TextAnchor.MiddleRight);

        private static void RowText(Rect rect, string text, Color color, TextAnchor anchor)
        {
            Color previous = GUI.color;
            GUI.color = color;
            Text.Anchor = anchor;
            Widgets.Label(rect, text);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = previous;
        }

        private static void SubRow(float x, float width, ref float y, string text)
        {
            RowLabel(new Rect(x, y, width, SubRowHeight), text, Muted);
            y += SubRowHeight;
        }

        private static void CheckRow(float x, float width, ref float y, string label, ref bool value, bool editable, string tooltip)
        {
            Rect row = new Rect(x, y, width, RowHeight);
            y += RowHeight;
            if (editable) { Widgets.DrawHighlightIfMouseover(row); }
            Widgets.CheckboxLabeled(row, label, ref value, disabled: !editable);
            if (!tooltip.NullOrEmpty()) { TooltipHandler.TipRegion(row, tooltip); }
        }

        /// <summary>二選一：兩個選項都看得到，選中的那段有高亮。回傳這一幀點到的選項，沒點回 -1。</summary>
        private static int SegmentRow(Rect row, string label, string first, string second, bool firstSelected, bool editable, string tooltip)
        {
            RowLabel(row, label);
            TooltipHandler.TipRegion(row, tooltip);
            float top = row.y + (RowHeight - ControlHeight) / 2f;
            Rect b = new Rect(row.xMax - SegmentWidth, top, SegmentWidth, ControlHeight);
            Rect a = new Rect(b.x - SegmentWidth, top, SegmentWidth, ControlHeight);
            int result = -1;
            if (Segment(a, first, firstSelected, editable)) { result = 0; }
            if (Segment(b, second, !firstSelected, editable)) { result = 1; }
            return result;
        }

        private static bool Segment(Rect rect, string label, bool selected, bool editable)
        {
            Color color = GUI.color;
            if (!selected) { GUI.color = color * UnselectedSegment; }
            bool pressed = Widgets.ButtonText(rect, label, active: editable && !selected);
            GUI.color = color;
            if (selected) { Widgets.DrawHighlightSelected(rect); }
            return pressed;
        }

        /// <summary>Widgets.ButtonText 的 active=false 只擋點擊、外觀不變，所以另外壓暗。</summary>
        private static bool Button(Rect rect, string label, bool active, string tooltip = null)
        {
            Color color = GUI.color;
            if (!active) { GUI.color = color * Dimmed; }
            bool pressed = Widgets.ButtonText(rect, label, active: active);
            GUI.color = color;
            if (!tooltip.NullOrEmpty()) { TooltipHandler.TipRegion(rect, tooltip); }
            return pressed;
        }
    }
}
