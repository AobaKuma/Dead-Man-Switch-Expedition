using Verse;

namespace DMSE
{
    /// <summary>
    /// 建築重命名對話框。繼承原版 <see cref="Dialog_Rename{T}"/>，取得一致的 UI（標題、文字框、OK 按鈕）。
    /// <para>
    ///   覆寫 <see cref="NameIsValid"/> 以允許空字串——確認空名稱時，
    ///   <see cref="CompRenameable.RenamableLabel"/> 的 setter 會將 <c>customLabel</c> 設為 null，
    ///   從而還原為 def 預設名稱。
    /// </para>
    /// </summary>
    public class Dialog_RenameBuilding : Dialog_Rename<CompRenameable>
    {
        public Dialog_RenameBuilding(CompRenameable comp) : base(comp) { }

        /// <summary>任何輸入均合法（包含空字串，代表重置為預設名稱）。</summary>
        protected override AcceptanceReport NameIsValid(string name) => true;
    }
}
