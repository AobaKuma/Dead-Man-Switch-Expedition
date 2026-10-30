using UnityEngine;
using Verse;

namespace DMSE
{
    // Refactoring UI §1.5, §2.1, §3.6: preserve the game's dark surfaces and font
    // scale. Primary = decision/status; secondary = configuration; tertiary = details.
    // Reuse existing DMSE spacing/colors rather than introducing a new theme.
    internal static class DMSEUI
    {
        public const float Gap = 8f;
        public const float SectionGap = 16f;
        public const float ButtonHeight = 34f;
        public const float RowHeight = 28f;
        public const float ScrollbarWidth = 16f;
        public static readonly Color Muted = new Color(0.65f, 0.65f, 0.65f);
        public static readonly Color Warning = new Color(1f, 0.85f, 0.35f);
        public static readonly Color Success = new Color(0.55f, 0.9f, 0.55f);
        public static readonly Color Error = ColorLibrary.RedReadable;

        // §3.3: long translated labels/statuses must grow, not collide in a fixed row.
        public static Rect ValueRow(Listing_Standard listing, string label, string value, Color? valueColor = null)
        {
            float width = listing.ColumnWidth;
            float labelWidth = width * 0.6f - Gap;
            float valueWidth = width * 0.4f;
            Rect row = listing.GetRect(Mathf.Max(Text.CalcHeight(label, labelWidth), Text.CalcHeight(value, valueWidth)));
            Color old = GUI.color;
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Muted;
            Widgets.Label(new Rect(row.x, row.y, labelWidth, row.height), label);
            GUI.color = valueColor ?? old;
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(row.xMax - valueWidth, row.y, valueWidth, row.height), value);
            Text.Anchor = anchor;
            GUI.color = old;
            return row;
        }
    }
}
