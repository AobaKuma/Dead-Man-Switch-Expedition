using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>組裝台窗口與方案清單共用的繪製：數值卡、數值差異、圖示。</summary>
    [StaticConstructorOnStartup]
    public static class MissileDesignUI
    {
        public static readonly Texture2D LockedTex = ContentFinder<Texture2D>.Get("UI/Overlays/Locked");
        public static readonly Texture2D UnlockedTex = ContentFinder<Texture2D>.Get("UI/Overlays/LockedMonochrome");

        public const float StatCardHeight = 56f;

        /// <summary>
        /// 數值差異文字：「▲ 24」「▼ 0.5」，沒變為空。顏色依好壞（<paramref name="higherIsBetter"/> 為 null 時中性），
        /// 箭頭方向永遠照數值，所以不靠顏色也讀得出來。
        /// </summary>
        public static string Delta(float now, float baseline, string format, bool? higherIsBetter, out Color color)
        {
            color = DMSEUI.Muted;
            float d = now - baseline;
            if (Mathf.Abs(d) < 0.005f) { return string.Empty; }
            bool up = d > 0f;
            if (higherIsBetter.HasValue) { color = up == higherIsBetter.Value ? DMSEUI.Success : DMSEUI.Error; }
            return (up ? "▲ " : "▼ ") + Mathf.Abs(d).ToString(format);
        }

        /// <summary>四張關鍵數值卡：射程、傷害、爆炸半徑、導引方式；與 <paramref name="baseline"/>（通常是預設構型）比較。</summary>
        public static void DrawStatCards(Rect rect, MissileConfig cfg, MissileConfig baseline)
        {
            float w = (rect.width - 3f * DMSEUI.Gap) / 4f;
            Rect Card(int i) => new Rect(rect.x + i * (w + DMSEUI.Gap), rect.y, w, rect.height);

            Color c = DMSEUI.Muted;
            string d = baseline != null ? Delta(cfg.Range, baseline.Range, "0", true, out c) : null;
            DrawCard(Card(0), "DMSE.UI.RangeChange".Translate(), cfg.Range.ToString(), d, c);
            d = baseline != null ? Delta(cfg.DamageAmount, baseline.DamageAmount, "0", true, out c) : null;
            DrawCard(Card(1), "DMSE.UI.DamageChange".Translate(), cfg.DamageAmount.ToString(), d, c);
            d = baseline != null ? Delta(cfg.ExplosionRadius, baseline.ExplosionRadius, "0.#", true, out c) : null;
            DrawCard(Card(2), "DMSE.Bench.Stat.Radius".Translate(), cfg.ExplosionRadius.ToString("0.#"), d, c);
            DrawCard(Card(3), "DMSE.Bench.Stat.Guidance".Translate(), cfg.TargetingLabel, null, DMSEUI.Muted);
        }

        private static void DrawCard(Rect r, string label, string value, string delta, Color deltaColor)
        {
            Widgets.DrawLightHighlight(r);
            Rect inner = r.ContractedBy(DMSEUI.Gap / 2f);
            TextAnchor oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = value.Length > 8 ? GameFont.Small : GameFont.Medium;
            Rect valueRect = new Rect(inner.x, inner.y, inner.width, 28f);
            Widgets.Label(valueRect, value.Truncate(valueRect.width));
            if (value.Length > 8) { TooltipHandler.TipRegion(valueRect, value); }
            Text.Font = GameFont.Tiny;
            GUI.color = DMSEUI.Muted;
            Rect labelRect = new Rect(inner.x, inner.yMax - 16f, inner.width, 16f);
            Widgets.Label(labelRect, label.Truncate(labelRect.width));
            if (!delta.NullOrEmpty())
            {
                GUI.color = deltaColor;
                Text.Anchor = TextAnchor.LowerRight;
                Widgets.Label(labelRect, delta);
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = oldAnchor;
        }

        /// <summary>彈體圖示（物品圖示優先，其次彈體自身圖示）。</summary>
        public static void DrawBodyIcon(Rect r, MissileBodyDef body)
        {
            // 物品的 uiIcon 縮放進框內；DefIcon 會用世界貼圖的原生 drawSize，長條導彈會溢出列高。
            ThingDef item = MissileItemUtility.ItemDefFor(body);
            if (item?.uiIcon != null && item.uiIcon != BaseContent.BadTex)
            {
                Widgets.DrawTextureFitted(r, item.uiIcon, 1f);
                return;
            }
            Texture tex = MissileResearchUnlocks.BodyIcon(body);
            if (tex != null) { GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit); }
        }

        /// <summary>部件成本的短字串：「鋼 40 · 合金 5」。</summary>
        public static string CostText(List<ThingDefCountClass> cost)
        {
            if (cost == null || cost.Count == 0) { return string.Empty; }
            List<string> parts = new List<string>();
            foreach (ThingDefCountClass c in cost) { parts.Add(c.thingDef.LabelCap + " " + c.count); }
            return string.Join(" · ", parts);
        }

        public static string SizeClassLabel(MissileSizeClass size) => ("DMSE.Bench.Size." + size).Translate();
    }
}
