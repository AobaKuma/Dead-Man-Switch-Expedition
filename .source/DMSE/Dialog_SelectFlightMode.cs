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
        ImpactHard,
    }
    public class Dialog_SelectFlightMode : Window
    {
        private readonly Action<FlightMode> _onChosen;
        private readonly bool _canTransfer;
        private readonly string _transferFailReason;
        private bool _showAdvanced = false;

        private float ContentHeight
        {
            get
            {
                float h = 30f + 10f  // title + gap
                        + 32f + 6f  // 標準模式
                        + 32f;      // 轉移模式
                if (!_canTransfer) h += 6f + 24f; // reason label
                h += 6f + 24f;  // advanced toggle row
                if (_showAdvanced)
                    h += (32f + 6f) * 2; // Impact + ImpactHard
                return h;
            }
        }

        public override Vector2 InitialSize => new Vector2(420f, ContentHeight + 60f);

        public Dialog_SelectFlightMode(Action<FlightMode> onChosen, CompPilotConsole comp)
        {
            _onChosen = onChosen;
            _canTransfer = !TransferFlightUtility.GetTransferFailReason(comp, out _transferFailReason);
            forcePause = true;
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var y = inRect.y;

            Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "DMSE.Flight.Select".Translate());
            y += 40f;

            if (Widgets.ButtonText(new Rect(inRect.x, y, inRect.width, 32f), "DMSE.Flight.Regular".Translate()))
            {
                _onChosen?.Invoke(FlightMode.Standard);
                Close();
            }
            y += 38f;

            if (_canTransfer)
            {
                if (Widgets.ButtonText(new Rect(inRect.x, y, inRect.width, 32f), "DMSE.Flight.Transfer".Translate()))
                {
                    _onChosen?.Invoke(FlightMode.Transfer);
                    Close();
                }
                y += 38f;
            }
            else
            {
                Widgets.DrawBoxSolid(new Rect(inRect.x, y, inRect.width, 32f), new Color(0.3f, 0.3f, 0.3f, 0.5f));
                GUI.color = Color.gray;
                Widgets.ButtonText(new Rect(inRect.x, y, inRect.width, 32f), "DMSE.Flight.Transfer".Translate(), active: false);
                GUI.color = Color.white;
                y += 36f;
                GUI.color = Color.red;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), _transferFailReason);
                GUI.color = Color.white;
                y += 30f;
            }

            y += 6f;
            bool newShowAdvanced = _showAdvanced;
            Widgets.CheckboxLabeled(new Rect(inRect.x, y, inRect.width, 24f), "DMSE.Flight.ShowAdvanced".Translate(), ref newShowAdvanced);
            if (newShowAdvanced != _showAdvanced)
            {
                _showAdvanced = newShowAdvanced;
                windowRect.height = ContentHeight + 60f;
            }
            y += 30f;

            if (_showAdvanced)
            {
                if (Widgets.ButtonText(new Rect(inRect.x, y, inRect.width, 32f), "DMSE.Flight.Impact".Translate()))
                {
                    _onChosen?.Invoke(FlightMode.Impact);
                    Close();
                }
                y += 38f;

                if (Widgets.ButtonText(new Rect(inRect.x, y, inRect.width, 32f), "DMSE.Flight.ImpactHard".Translate()))
                {
                    _onChosen?.Invoke(FlightMode.ImpactHard);
                    Close();
                }
            }
        }
    }
}