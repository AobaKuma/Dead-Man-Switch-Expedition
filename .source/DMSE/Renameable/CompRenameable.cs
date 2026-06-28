using RimWorld;
using Verse;

namespace DMSE
{
    public class CompProperties_Renameable : CompProperties
    {
        public CompProperties_Renameable() { compClass = typeof(CompRenameable); }
    }

    /// <summary>
    /// 允許玩家為任何建築設定自訂名稱。
    /// <para>
    ///   實作 <see cref="IRenameable"/>，令 <see cref="Patch_InspectPaneRename"/> 能攔截
    ///   <c>MainTabWindow_Inspect.DoInspectPaneButtons</c>，在原版筆形按鈕的同一位置插入
    ///   重命名按鈕，並開啟 <see cref="Dialog_RenameBuilding"/>（繼承原版 <c>Dialog_Rename&lt;T&gt;</c>）。
    /// </para>
    /// <para>
    ///   自訂名稱透過 <see cref="Patch_ThingLabelNoCount"/> 覆寫 <see cref="Thing.LabelNoCount"/>，
    ///   使 UI 各處一律顯示玩家設定的名稱；留空確認可還原為 def 預設名稱。
    /// </para>
    /// </summary>
    public class CompRenameable : ThingComp, IRenameable
    {
        /// <summary>玩家設定的自訂名稱；null 表示沿用 def 預設名稱。</summary>
        public string customLabel;

        // ---- IRenameable ----

        /// <summary>
        /// 可讀寫的顯示名稱。getter 回傳自訂名稱（或 def 預設）；
        /// setter 將空字串視為「重置」，儲存 null。
        /// </summary>
        public string RenamableLabel
        {
            get => customLabel.NullOrEmpty() ? parent.def.label : customLabel;
            set => customLabel = value.NullOrEmpty() ? null : value;
        }

        /// <summary>不帶自訂名稱的原始標籤，用於多選時的標籤合併顯示。</summary>
        public string BaseLabel => parent.def.label;

        /// <summary>顯示於選取欄標題的名稱（自訂名稱優先）。</summary>
        public string InspectLabel => RenamableLabel;

        // ---- 存檔 ----

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref customLabel, "customLabel", null);
        }
    }
}
