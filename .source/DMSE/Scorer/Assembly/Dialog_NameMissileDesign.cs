using System;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 保存裝配方案前問名稱：沿用原版 <see cref="Dialog_Rename{T}"/> 的外觀（文字框＋確定、Enter 確認），
    /// 名稱規則與組裝台一致（<see cref="MissileDesignNamer.IsValidName"/>）。
    /// </summary>
    public class Dialog_NameMissileDesign : Dialog_Rename<IRenameable>
    {
        private readonly Action<string> onAccept;

        protected override int MaxNameLength => MissileDesignNamer.MaxNameLength + 1;

        public Dialog_NameMissileDesign(string initialName, Action<string> onAccept) : base(null)
        {
            this.onAccept = onAccept;
            curName = initialName ?? string.Empty;
        }

        protected override AcceptanceReport NameIsValid(string name)
        {
            if (!MissileDesignNamer.IsValidName(name)) { return "DMSE.Bench.Block.BadName".Translate(); }
            return true;
        }

        protected override void OnRenamed(string name) => onAccept?.Invoke(name.Trim());
    }
}
