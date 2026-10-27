using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    // ========================================================================
    //  臨時開發工具（2026-10-26）：手動調導彈貼圖尺寸，調完匯出數值寫回 def 後整個 Dev 資料夾可刪。
    //  - Debug actions「DMSE Missile Size」：開調整視窗、把每種導彈物品放在地上、把導彈裝上架子、在地上排出飛行貼圖。
    //  - 視窗：每種導彈物品（地上／架上用）與每個彈體的飛行貼圖（incoming＋發射 projectile＋現役模板）各一條滑桿，
    //    即時改 def 的 drawSize（物品另可調 drawOffset.z），匯出到剪貼簿與 docs/review/missile_sizes_tuned.txt。
    //  只改記憶體中的 def，不存檔；重開遊戲回到 XML 的值。
    // ========================================================================

    /// <summary>一條可調的尺寸：同一組 def 共用一個 drawSize。</summary>
    public class MissileSizeEntry
    {
        public string key;
        public string label;
        public bool item;
        public List<ThingDef> defs = new List<ThingDef>();
        public float size, originalSize;
        public float offsetZ, originalOffsetZ;

        public void Apply()
        {
            foreach (ThingDef def in defs)
            {
                def.graphicData.drawSize = new Vector2(size, size);
                if (item) { def.graphicData.drawOffset = new Vector3(def.graphicData.drawOffset.x, def.graphicData.drawOffset.y, offsetZ); }
                MissileSizeTuner.RefreshGraphic(def);
            }
        }
    }

    public static class MissileSizeTuner
    {
        private static readonly AccessTools.FieldRef<GraphicData, Graphic> CachedGraphic = AccessTools.FieldRefAccess<GraphicData, Graphic>("cachedGraphic");

        /// <summary>現役彈體的飛行貼圖也用在舊的模板 skyfaller（發射器的預設），一起改才一致。</summary>
        private static readonly Dictionary<string, string[]> Templates = new Dictionary<string, string[]>
        {
            { "DMSE_MissileBody_Ballistic", new[] { "DMSE_SkyFaller_BallisticMissile" } },
            { "DMSE_MissileBody_Light", new[] { "DMSE_SkyFaller_LightMissile", "DMSE_SkyFaller_Interceptor" } },
            { "DMSE_MissileBody_Loiter", new[] { "DMSE_SkyFaller_Loiter" } },
            { "DMSE_MissileBody_Cruise", new[] { "DMSE_SkyFaller_CruiseMissile" } },
        };

        private static List<MissileSizeEntry> entries;

        public static List<MissileSizeEntry> Entries => entries ?? (entries = Build());

        public static IEnumerable<ThingDef> MissileItems => DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Item && d.graphicData != null
                && (d.HasComp(typeof(CompMissileConfig)) || d.HasModExtension<MissileItemExtension>()))
            .OrderBy(d => d.defName);

        private static List<MissileSizeEntry> Build()
        {
            var list = new List<MissileSizeEntry>();
            foreach (ThingDef d in MissileItems)
            {
                list.Add(new MissileSizeEntry
                {
                    key = d.defName, label = d.LabelCap, item = true, defs = { d },
                    size = d.graphicData.drawSize.x, originalSize = d.graphicData.drawSize.x,
                    offsetZ = d.graphicData.drawOffset.z, originalOffsetZ = d.graphicData.drawOffset.z
                });
            }
            foreach (MissileBodyDef body in DefDatabase<MissileBodyDef>.AllDefsListForReading.OrderBy(b => b.defName))
            {
                var e = new MissileSizeEntry { key = body.defName, label = body.LabelCap, item = false };
                if (body.incomingSkyfaller?.graphicData != null) { e.defs.Add(body.incomingSkyfaller); }
                if (body.launchProjectile?.graphicData != null) { e.defs.Add(body.launchProjectile); }
                if (Templates.TryGetValue(body.defName, out string[] names))
                {
                    foreach (string n in names)
                    {
                        ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                        if (t?.graphicData != null) { e.defs.Add(t); }
                    }
                }
                if (e.defs.Count == 0) { continue; }
                e.size = e.originalSize = e.defs[0].graphicData.drawSize.x;
                list.Add(e);
            }
            return list;
        }

        /// <summary>改了 drawSize 之後：重建 def 的 Graphic，並讓地上、架上、飛行中的實體重抓（地上的要重畫地圖網格）。</summary>
        public static void RefreshGraphic(ThingDef def)
        {
            CachedGraphic(def.graphicData) = null;
            def.graphic = def.graphicData.Graphic;
            foreach (Map map in Find.Maps)
            {
                foreach (Thing t in map.listerThings.ThingsOfDef(def)) { t.Notify_ColorChanged(); }
                foreach (Thing b in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                {
                    IReadOnlyList<Thing> held = MissileRackUtil.Of(b)?.HeldThings;
                    if (held == null) { continue; }
                    foreach (Thing t in held) { if (t.def == def) { t.Notify_ColorChanged(); } }
                }
            }
        }

        public static string Export()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DMSE missile sizes tuned in game " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("# kind\tkey\tdrawSize\tdrawOffsetZ\tdefs");
            foreach (MissileSizeEntry e in Entries)
            {
                string changed = Mathf.Abs(e.size - e.originalSize) > 1e-3f || (e.item && Mathf.Abs(e.offsetZ - e.originalOffsetZ) > 1e-3f) ? "\t*changed" : "";
                sb.AppendLine($"{(e.item ? "item" : "flight")}\t{e.key}\t{e.size:0.##}\t{(e.item ? e.offsetZ.ToString("0.##") : "-")}\t{string.Join(",", e.defs.Select(d => d.defName))}{changed}");
            }
            return sb.ToString();
        }

        public static string ExportPath => Path.Combine(LoadedModManager.GetMod<DMSEMod>().Content.RootDir, "docs", "review", "missile_sizes_tuned.txt");
    }

    /// <summary>調整視窗：不暫停、不擋地圖操作，可以一邊拖滑桿一邊看地上與架上的導彈。</summary>
    public class Dialog_MissileSizeTuner : Window
    {
        private Vector2 scroll;
        private bool showItems = true, showFlight = true;

        public override Vector2 InitialSize => new Vector2(560f, 720f);

        /// <summary>靠螢幕右邊開，不擋住地圖中央。</summary>
        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect.x = UI.screenWidth - windowRect.width - 12f;
            windowRect.y = 60f;
        }

        public Dialog_MissileSizeTuner()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            forcePause = false;
            onlyOneOfTypeAllowed = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            var top = new Rect(inRect.x, inRect.y, inRect.width, 28f);
            Widgets.CheckboxLabeled(new Rect(top.x, top.y, 120f, 24f), "Items", ref showItems);
            Widgets.CheckboxLabeled(new Rect(top.x + 130f, top.y, 120f, 24f), "Flight", ref showFlight);
            if (Widgets.ButtonText(new Rect(inRect.xMax - 200f, top.y, 95f, 26f), "Export"))
            {
                string text = MissileSizeTuner.Export();
                GUIUtility.systemCopyBuffer = text;
                Directory.CreateDirectory(Path.GetDirectoryName(MissileSizeTuner.ExportPath));
                File.WriteAllText(MissileSizeTuner.ExportPath, text);
                Messages.Message("DMSE: sizes copied to clipboard and saved to " + MissileSizeTuner.ExportPath, MessageTypeDefOf.TaskCompletion, false);
            }
            if (Widgets.ButtonText(new Rect(inRect.xMax - 100f, top.y, 95f, 26f), "Reset all"))
            {
                foreach (MissileSizeEntry e in MissileSizeTuner.Entries)
                {
                    e.size = e.originalSize;
                    e.offsetZ = e.originalOffsetZ;
                    e.Apply();
                }
            }

            List<MissileSizeEntry> shown = MissileSizeTuner.Entries.Where(e => e.item ? showItems : showFlight).ToList();
            var outer = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
            float rowH = 52f;
            var view = new Rect(0f, 0f, outer.width - 16f, shown.Count * rowH);
            Widgets.BeginScrollView(outer, ref scroll, view);
            for (int i = 0; i < shown.Count; i++)
            {
                DoRow(new Rect(0f, i * rowH, view.width, rowH - 4f), shown[i]);
            }
            Widgets.EndScrollView();
        }

        private static void DoRow(Rect r, MissileSizeEntry e)
        {
            Widgets.DrawLightHighlight(r);
            bool changed = Mathf.Abs(e.size - e.originalSize) > 1e-3f || Mathf.Abs(e.offsetZ - e.originalOffsetZ) > 1e-3f;
            GUI.color = changed ? new Color(1f, 0.85f, 0.4f) : Color.white;
            Widgets.Label(new Rect(r.x + 4f, r.y, r.width - 8f, 22f), $"{(e.item ? "[item]" : "[flight]")} {e.label}  ({e.key})");
            GUI.color = Color.white;

            float y = r.y + 24f;
            float before = e.size, beforeOff = e.offsetZ;
            if (Widgets.ButtonText(new Rect(r.x + 4f, y, 24f, 22f), "-")) { e.size = Mathf.Max(0.2f, e.size - 0.1f); }
            if (Widgets.ButtonText(new Rect(r.x + 30f, y, 24f, 22f), "+")) { e.size += 0.1f; }
            float sliderW = e.item ? (r.width - 70f) * 0.6f : r.width - 70f;
            e.size = Widgets.HorizontalSlider(new Rect(r.x + 60f, y + 2f, sliderW, 20f), e.size, 0.5f, 10f, false,
                $"size {e.size:0.0} (was {e.originalSize:0.0})", null, null, 0.1f);
            if (e.item)
            {
                e.offsetZ = Widgets.HorizontalSlider(new Rect(r.x + 70f + sliderW, y + 2f, r.width - 80f - sliderW, 20f), e.offsetZ, -3f, 3f, false,
                    $"offset z {e.offsetZ:0.0}", null, null, 0.05f);
            }
            if (e.size != before || e.offsetZ != beforeOff) { e.Apply(); }
        }
    }

    /// <summary>在地上排出每個彈體的飛行貼圖（機頭朝北），附名稱；錨點由 debug action 設定。</summary>
    public class MapComponent_MissileSizePreview : MapComponent
    {
        public IntVec3 anchor = IntVec3.Invalid;

        public MapComponent_MissileSizePreview(Map map) : base(map) { }

        /// <summary>每顆之間至少這麼寬（格），名稱標籤分三排錯開，才不會疊在一起。</summary>
        private const float MinSpacing = 2.4f;

        private IEnumerable<(MissileSizeEntry e, ThingDef def, Vector3 pos, int row)> Layout()
        {
            float x = 0f;
            int i = 0;
            foreach (MissileSizeEntry e in MissileSizeTuner.Entries.Where(e => !e.item))
            {
                ThingDef def = e.defs.FirstOrDefault(d => d.thingClass == typeof(Projectile_MissileLaunch)) ?? e.defs[0];
                float half = Mathf.Max(e.size * 0.35f, MinSpacing * 0.5f);
                x += half;
                yield return (e, def, anchor.ToVector3Shifted() + new Vector3(x, 0f, e.size * 0.5f), i++ % 3);
                x += half;
            }
        }

        /// <summary>標籤用短名：彈體 defName 去掉前綴。</summary>
        private static string ShortName(MissileSizeEntry e) => e.key.Replace("DMSE_MissileBody_", "");

        public override void MapComponentUpdate()
        {
            if (!anchor.IsValid || Find.CurrentMap != map) { return; }
            foreach (var (_, def, pos, _) in Layout())
            {
                def.graphic?.DrawFromDef(pos.WithY(AltitudeLayer.MetaOverlays.AltitudeFor()), Rot4.North, def);
            }
        }

        public override void MapComponentOnGUI()
        {
            if (!anchor.IsValid || Find.CurrentMap != map) { return; }
            foreach (var (e, _, pos, row) in Layout())
            {
                GenMapUI.DrawThingLabel(new Vector3(pos.x, 0f, anchor.z - 0.2f - row * 0.55f).MapToUIPosition(), $"{ShortName(e)} {e.size:0.0}", Color.white);
            }
        }
    }

    public static class DebugActions_MissileSize
    {
        private const string Category = "DMSE Missile Size";

        [DebugAction(Category, "Open missile size tuner", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void OpenTuner() => Find.WindowStack.Add(new Dialog_MissileSizeTuner());

        [DebugAction(Category, "Spawn every missile item here", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnItems()
        {
            Map map = Find.CurrentMap;
            IntVec3 cell = UI.MouseCell();
            int x = 0;
            foreach (ThingDef def in MissileSizeTuner.MissileItems)
            {
                IntVec3 c = cell + new IntVec3(x, 0, 0);
                if (!c.InBounds(map)) { break; }
                GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map);
                x += Mathf.Max(1, Mathf.CeilToInt(def.graphicData.drawSize.x * 0.4f));
            }
        }

        [DebugAction(Category, "Load missiles into rack...", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LoadRack()
        {
            IMissileRack rack = UI.MouseCell().GetThingList(Find.CurrentMap).Select(MissileRackUtil.Of).FirstOrDefault(r => r != null);
            if (rack == null)
            {
                Messages.Message("DMSE: no missile rack under the cursor.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("empty the rack", () =>
                {
                    foreach (Thing t in rack.HeldThings.ToList()) { t.Destroy(); }
                })
            };
            foreach (ThingDef def in MissileSizeTuner.MissileItems)
            {
                ThingDef d = def;
                Thing probe = ThingMaker.MakeThing(d);
                if (!rack.Accepts(probe)) { continue; }
                options.Add(new FloatMenuOption($"fill with {d.LabelCap}", () =>
                {
                    ThingOwner owner = rack.GetDirectlyHeldThings();
                    while (!rack.Full)
                    {
                        Thing t = ThingMaker.MakeThing(d);
                        if (!rack.Accepts(t) || !owner.TryAdd(t)) { break; }
                    }
                }));
            }
            if (options.Count == 1) { options.Add(new FloatMenuOption("(this rack accepts none of the missile items)", null)); }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        [DebugAction(Category, "Show flight sprites here", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ShowFlight() => Find.CurrentMap.GetComponent<MapComponent_MissileSizePreview>().anchor = UI.MouseCell();

        [DebugAction(Category, "Hide flight sprites", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void HideFlight() => Find.CurrentMap.GetComponent<MapComponent_MissileSizePreview>().anchor = IntVec3.Invalid;
    }
}
