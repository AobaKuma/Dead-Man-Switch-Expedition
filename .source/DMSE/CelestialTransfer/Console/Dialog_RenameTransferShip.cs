using Verse;

namespace DMSE
{
    /// <summary>
    /// 轉移艦命名對話框。沿用原版 <see cref="Dialog_Rename{T}"/>（同原版 Dialog_RenameGravship）；
    /// 名稱不可留空，確認後由 <see cref="CompTransferConsole.SetShipName"/> 套用到同船所有控制台與船體。
    /// </summary>
    public class Dialog_RenameTransferShip : Dialog_Rename<CompTransferConsole>
    {
        public Dialog_RenameTransferShip(CompTransferConsole console) : base(console) { }

        protected override AcceptanceReport NameIsValid(string name)
        {
            return !name.NullOrEmpty() && name.Trim().Length > 0;
        }
    }
}
