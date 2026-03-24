using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public enum FlightMode
    {
        Standard,
        Transfer,
        Impact,
    }

    public class Dialog_SelectFlightMode : Window
    {
        private readonly Action<FlightMode> _onChosen;
        private readonly Action _onCancelled;
        private readonly bool _canTransfer;
        private readonly string _transferFailReason;
        private readonly int _engineCount;

        private bool CanImpact => _canTransfer && _engineCount > 2;

        // 統一一些 UI 常數，讓計算比較直觀
        private const float TitleHeight = 32f;
        private const float ButtonHeight = 32f;
        private const float LineGap = 6f;
        private const float SectionGap = 12f;
        private const float HorizontalPadding = 18f;
        private const float VerticalPadding = 18f;

        private float ContentHeight
        {
            get
            {
                float h = 0f;

                h += TitleHeight;                   // 標題
                h += SectionGap;

                h += ButtonHeight;                  // Standard
                h += LineGap;

                h += ButtonHeight;                  // Transfer (可用或不可用)
                if (!_canTransfer)
                {
                    h += LineGap;
                    h += 32f;                       // 失敗原因文字
                }

                if (CanImpact)
                {
                    h += SectionGap;
                    h += 20f;                       // Impact 區塊標題
                    h += LineGap;
                    h += ButtonHeight;              // Impact 按鈕
                }

                return h;
            }
        }

        public override Vector2 InitialSize => new Vector2(460f, 58f + ContentHeight + VerticalPadding * 2f + 40f);

        public Dialog_SelectFlightMode(Action<FlightMode> onChosen, CompPilotConsole comp, Action onCancelled = null)
        {
            _onChosen = onChosen;
            _onCancelled = onCancelled;
            _canTransfer = !FlightUtility.GetFailReason(comp, out _transferFailReason);
            _engineCount = FlightUtility.GetTransferThrusterCount(comp.engine);

            forcePause = true;
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        public override void PostClose()
        {
            base.PostClose();
            _onCancelled?.Invoke();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 內縮一圈，避免緊貼邊框
            Rect rect = inRect.ContractedBy(HorizontalPadding, VerticalPadding);
            float curY = rect.y;
            float width = rect.width;

            // 標題
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, curY, width, TitleHeight), "DMSE.Flight.Select".Translate());
            Text.Font = GameFont.Small;
            curY += TitleHeight;

            // 簡短說明
            curY += LineGap;
            string desc = "DMSE.Flight.Select.Desc".Translate();
            Widgets.Label(new Rect(rect.x, curY, width, 32f), desc);
            curY += 32f + SectionGap;

            // Standard 按鈕
            Rect standardRect = new Rect(rect.x, curY, width, ButtonHeight);
            if (Widgets.ButtonText(standardRect, "DMSE.Flight.Regular".Translate()))
            {
                _onChosen?.Invoke(FlightMode.Standard);
                Close();
                return;
            }

            curY += ButtonHeight + LineGap;

            // Transfer 區塊
            Rect transferRect = new Rect(rect.x, curY, width, ButtonHeight);
            if (_canTransfer)
            {
                if (Widgets.ButtonText(transferRect, "DMSE.Flight.Transfer".Translate()))
                {
                    _onChosen?.Invoke(FlightMode.Transfer);
                    Close();
                    return;
                }
                curY += ButtonHeight + SectionGap;
            }
            else
            {
                // 灰階不可用按鈕 + 失敗原因
                Color prevColor = GUI.color;

                Widgets.DrawHighlightIfMouseover(transferRect);
                GUI.color = Color.gray;
                Widgets.ButtonText(transferRect, "DMSE.Flight.Transfer".Translate(), active: false);
                GUI.color = prevColor;

                curY += ButtonHeight + LineGap;

                GUI.color = Color.red;
                Widgets.Label(new Rect(rect.x, curY, width, 32f), _transferFailReason);
                GUI.color = prevColor;

                curY += 32f + SectionGap;
            }

            // Impact 區塊
            if (CanImpact)
            {
                // 小標題警示文字
                GUI.color = new Color(1f, 0.4f, 0.4f);
                Widgets.Label(new Rect(rect.x, curY, width, 20f), "DMSE.Flight.Impact.Header".Translate());
                GUI.color = Color.white;

                curY += 20f + LineGap;

                Rect impactRect = new Rect(rect.x, curY, width, ButtonHeight);
                GUI.color = new Color(1f, 0.3f, 0.35f);
                if (Widgets.ButtonText(impactRect, "DMSE.Flight.Impact".Translate()))
                {
                    var confirm = Dialog_MessageBox.CreateConfirmation(
                        "DMSE.Flight.Impact.Confirm".Translate(),
                        delegate
                        {
                            _onChosen?.Invoke(FlightMode.Impact);
                        },
                        destructive: true);
                    confirm.interactionDelay = 6f;
                    Find.WindowStack.Add(confirm);
                    Close();
                    return;
                }
                GUI.color = Color.white;
            }
        }
    }
}