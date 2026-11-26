using System.Collections.Generic;
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
    ///   自訂名稱透過原版 <see cref="ThingComp.TransformLabel"/> 套用：<c>ThingWithComps.LabelNoCount</c>
    ///   與 <c>GetCustomLabelNoCount</c> 會依序呼叫每個元件的 TransformLabel，所以不需要 Harmony，
    ///   沒有這個元件的物品也不會多付任何成本。留空確認可還原為 def 預設名稱。
    /// </para>
    /// <para>
    ///   改名按鈕是元件自己的 gizmo（玩家陣營、已生成時才顯示），開啟 <see cref="Dialog_RenameBuilding"/>。
    /// </para>
    /// </summary>
    public class CompRenameable : ThingComp, IRenameable
    {
        /// <summary>玩家設定的自訂名稱；null 表示沿用 def 預設名稱。</summary>
        public string customLabel;

        public bool HasCustomLabel => !customLabel.NullOrEmpty();

        // ---- IRenameable ----

        /// <summary>
        /// 可讀寫的顯示名稱。getter 回傳自訂名稱（或 def 預設）；
        /// setter 將空字串視為「重置」，儲存 null。
        /// </summary>
        public string RenamableLabel
        {
            get => HasCustomLabel ? customLabel : parent.def.label;
            set => customLabel = value.NullOrEmpty() ? null : value;
        }

        /// <summary>不帶自訂名稱的原始標籤，用於多選時的標籤合併顯示。</summary>
        public string BaseLabel => parent.def.label;

        /// <summary>顯示於選取欄標題的名稱（自訂名稱優先）。</summary>
        public string InspectLabel => RenamableLabel;

        // ---- 標籤 ----

        /// <summary>
        /// 自訂名稱整個取代標籤。其他元件若要在自己的 TransformLabel 加字，
        /// 先檢查 <see cref="HasCustomLabel"/>，自訂名稱才會蓋過一切、不受 comps 順序影響。
        /// </summary>
        public override string TransformLabel(string label) => HasCustomLabel ? customLabel : label;

        // ---- 改名按鈕 ----

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }
            if (!parent.Spawned || parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            yield return new Command_Action
            {
                defaultLabel = "Rename".Translate(),
                icon = TexButton.Rename,
                alsoClickIfOtherInGroupClicked = false,
                action = delegate { Find.WindowStack.Add(new Dialog_RenameBuilding(this)); }
            };
        }

        // ---- 存檔 ----

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref customLabel, "customLabel", null);
        }
    }
}
