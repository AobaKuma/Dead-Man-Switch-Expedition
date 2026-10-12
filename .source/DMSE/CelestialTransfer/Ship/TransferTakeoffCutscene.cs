using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    // A modal, real-time cutscene: simulation and saving remain paused until completion.
    public class TransferTakeoffCutscene : Window
    {
        private readonly Map displayMap;
        private readonly Map captureMap;
        private readonly HashSet<IntVec3> cells;
        private readonly Action onComplete;
        private readonly Vector3 center;
        private readonly GravshipAudio audio = new GravshipAudio();
        private GravshipCapturer capturer;
        private Texture2D texture;
        private Material material;
        private CellRect bounds;
        private float elapsed;
        private float captureWait;
        private float initialZoom;
        private float maxZoom;
        private float previousZoom;
        private bool ready;
        private bool finished;
        private bool previousScreenshotMode;
        // 離場方向與引擎效果：船沿推進方向（運作中引擎的推力加權朝向）移動，引擎火焰與排氣跟著船體
        private TransferShipSurvey survey;
        private Vector3 thrustDirection = Vector3.forward;
        private readonly TransferEngineEffects engineEffects = new TransferEngineEffects();
        private const float Duration = 10f;
        private const float IgnitionTime = 0.08f;

        public TransferTakeoffCutscene(Map displayMap, Vector3 center, Action onComplete,
            Map captureMap = null, HashSet<IntVec3> cells = null)
        {
            this.displayMap = displayMap;
            this.center = center;
            this.onComplete = onComplete;
            this.captureMap = captureMap;
            this.cells = cells;
            forcePause = true;
            preventSave = true;
            absorbInputAroundWindow = true;
            closeOnAccept = closeOnCancel = false;
            doWindowBackground = drawShadow = false;
            soundAppear = soundClose = null;
        }

        public override Vector2 InitialSize => new Vector2(UI.screenWidth, UI.screenHeight);
        protected override float Margin => 0f;
        public override void DoWindowContents(Rect inRect) { }

        public override void PostOpen()
        {
            base.PostOpen();
            previousScreenshotMode = Find.ScreenshotModeHandler.Active;
            previousZoom = Find.CameraDriver.RootSize;
            try
            {
                StartCaptureOrAnimation();
            }
            catch (Exception ex)
            {
                Log.Error("[DMSE] Could not start transfer takeoff animation: " + ex);
                Finish();
            }
        }

        private void StartCaptureOrAnimation()
        {
            if (!Prefs.GravshipCutscenes || !ModsConfig.OdysseyActive)
            {
                Finish();
                return;
            }
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
            Find.ScreenshotModeHandler.Active = true;
            survey = (captureMap?.Parent as WorldObject_TransferShip)?.FlightSurvey();
            thrustDirection = TransferEngineEffects.ThrustDirection(survey) ?? Vector3.forward;
            if (captureMap != null && cells != null && cells.Count > 0)
            {
                Current.Game.CurrentMap = captureMap;
                captureMap.mapDrawer.RegenerateEverythingNow();
                bounds = CellRect.FromCellList(cells).ExpandedBy(2);
                // 地圖只繪製主鏡頭視野內的區塊；原版 BeginGravshipRender 會設定 GravshipCaptureBounds 讓擷取範圍一併繪製，
                // BeginTerrainRender 不會，所以這裡自己設定，並把鏡頭移到船體中心
                GravshipCapturer.GravshipCaptureBounds = bounds;
                Find.CameraDriver.JumpToCurrentMapLoc(bounds.CenterVector3);
                capturer = new GravshipCapturer();
                capturer.BeginTerrainRender(bounds, CaptureComplete);
            }
            else
            {
                BeginAnimation();
            }
        }

        private void CaptureComplete(Capture capture)
        {
            if (finished)
            {
                UnityEngine.Object.Destroy(capture.capture.Texture);
                return;
            }
            texture = capture.capture.Texture;
            // The capture camera fits these bounds exactly; remove pixels outside the hull.
            // BeginTerrainRender（不同於原版 BeginGravshipRender 的色鍵 + GravshipBlit）不產生遮罩，
            // 擷取到的 alpha 來自各層 shader：太空地圖的地形常寫入 0，船體地板會變成半透明甚至看不見。
            // 船體範圍內的格子都是實心的，一律設為不透明。
            Color32[] pixels = texture.GetPixels32();
            for (int z = 0; z < texture.height; z++)
            {
                int cellZ = bounds.minZ + z * bounds.Height / texture.height;
                for (int x = 0; x < texture.width; x++)
                {
                    int cellX = bounds.minX + x * bounds.Width / texture.width;
                    pixels[z * texture.width + x].a = cells.Contains(new IntVec3(cellX, 0, cellZ)) ? (byte)255 : (byte)0;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            material = new Material(ShaderDatabase.Transparent) { mainTexture = texture, renderQueue = 3200 };
            BeginAnimation();
        }

        private void BeginAnimation()
        {
            Current.Game.CurrentMap = displayMap;
            CameraJumper.TryJump(center.ToIntVec3(), displayMap);
            // 鏡頭高度隨縮放上升（CameraDriver.ApplyPositionToGameObject），超過 sizeRange.max 會超出遠裁切面，
            // 地形與建築由下往上被裁掉；比照原版過場把縮放限制在允許範圍內
            FloatRange sizeRange = Find.CameraDriver.config.sizeRange;
            maxZoom = sizeRange.max;
            initialZoom = Mathf.Clamp(Mathf.Max(Find.CameraDriver.RootSize,
                texture == null ? 20f : Mathf.Max(bounds.Height, bounds.Width / Find.Camera.aspect) * 0.6f), sizeRange.min, maxZoom);
            Find.CameraDriver.SetRootSize(initialZoom);
            Find.CameraDriver.shaker.DoShake(0.2f, 120);
            audio.BeginTakeoff();
            ready = true;
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (finished) return;
            try
            {
                if (!ready)
                {
                    capturer?.Update();
                    captureWait += Time.unscaledDeltaTime;
                    if (captureWait > 10f) Finish();
                    return;
                }
                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(elapsed / Duration);
                audio.Update(elapsed, progress, true);
                Find.CameraDriver.SetRootSize(Mathf.Lerp(initialZoom, Mathf.Min(initialZoom * 1.6f, maxZoom),
                    Mathf.SmoothStep(0f, 1f, progress)));
                // 沿推進方向加速離開（前段幾乎不動、引擎全開，之後加速出畫面）
                Vector3 offset = thrustDirection * (initialZoom * 4f * Mathf.Pow(progress, 3.5f));
                float fade = Mathf.InverseLerp(0.85f, 1f, progress);
                if (material != null)
                {
                    material.color = new Color(1f, 1f, 1f, 1f - fade);
                    GenDraw.DrawQuad(material, (center + offset).WithY(AltitudeLayer.Skyfaller.AltitudeFor()),
                        Quaternion.identity, new Vector3(bounds.Width, 1f, bounds.Height));
                }
                if (survey != null)
                {
                    // 點火：開頭短暫閃爍增強到全推力；淡出時火焰一起收掉
                    float flame = 1f;
                    if (progress < IgnitionTime)
                    {
                        flame = Mathf.Lerp(0.3f, 1f, progress / IgnitionTime) * Mathf.Lerp(0.6f, 1f, Mathf.PerlinNoise1D(elapsed * 20f));
                    }
                    engineEffects.Begin(displayMap);
                    engineEffects.DrawEngines(survey, flame * (1f - fade), 1f - fade, offset);
                    engineEffects.End();
                }
                if (elapsed >= Duration || !Prefs.GravshipCutscenes) Finish();
            }
            catch (Exception ex)
            {
                Log.Error("[DMSE] Transfer takeoff animation failed: " + ex);
                Finish();
            }
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            Close(false);
            onComplete?.Invoke();
        }

        public override void PostClose()
        {
            audio.EndTakeoff();
            if (capturer != null && !ready)
                GravshipCapturer.IsGravshipRenderInProgress = false;
            Find.CameraDriver.shaker.StopAllShaking();
            Find.CameraDriver.SetRootSize(previousZoom);
            Find.ScreenshotModeHandler.Active = previousScreenshotMode;
            if (material != null) UnityEngine.Object.Destroy(material);
            if (texture != null) UnityEngine.Object.Destroy(texture);
            base.PostClose();
        }
    }
}
