using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>重新命名打擊任務：原版重新命名視窗，加上一個「隨機」按鈕。</summary>
    public class Dialog_RenameStrikeMission : Dialog_Rename<StrikeMission>
    {
        public Dialog_RenameStrikeMission(StrikeMission mission) : base(mission) { }

        public override Vector2 InitialSize => new Vector2(320f, 215f);

        protected override int MaxNameLength => 40;

        public override void DoWindowContents(Rect inRect)
        {
            base.DoWindowContents(inRect);
            // 放在輸入框與確定鈕之間
            if (Widgets.ButtonText(new Rect(15f, inRect.height - 85f, inRect.width - 30f, 32f), "Randomize".Translate()))
            {
                string rolled = StrikeMissionNamer.RandomName(curName);
                if (!rolled.NullOrEmpty())
                {
                    curName = rolled;
                    // 輸入框有焦點時不會顯示程式改寫的文字
                    UI.UnfocusCurrentControl();
                }
            }
        }
    }
}
