using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    // A modal, real-time cutscene: simulation and saving remain paused until completion.
    // 外部模組可以繼承（TransferPresentation.takeoffCutsceneClass）：演出的每一段（音效與震動、鏡頭縮放、船體位移、
    // 顏色與淡出、縮放、火焰、額外繪製）都是 protected virtual，預設是太空船的離場（火箭音、震動、加速飛出畫面）。
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
        private const float IgnitionTime = 0.08f;

        /// <summary>動畫長度（實際秒數）。</summary>
        protected virtual float Duration => 10f;

        protected Map DisplayMap => displayMap;
        protected Vector3 Center => center;
        protected CellRect Bounds => bounds;
        protected float InitialZoom => initialZoom;
        protected float MaxZoom => maxZoom;
        protected float Elapsed => elapsed;
        protected Vector3 ThrustDirection => thrustDirection;
        protected TransferShipSurvey Survey => survey;
        protected TransferPresentation Presentation => TransferEnvironment.Presentation(captureMap ?? displayMap);

        /// <summary>動畫開始：鏡頭震動與起飛音效。</summary>
        protected virtual void StartEffects()
        {
            float shake = Presentation.cameraShake;
            if (shake > 0f)
            {
                Find.CameraDriver.shaker.DoShake(0.2f * shake, 120);
            }
            audio.BeginTakeoff();
        }

        protected virtual void UpdateEffects(float elapsed, float progress)
        {
            audio.Update(elapsed, progress, true);
        }

        protected virtual void StopEffects()
        {
            audio.EndTakeoff();
        }

        /// <summary>鏡頭大小：逐漸拉遠。</summary>
        protected virtual float ZoomAt(float progress)
        {
            return Mathf.Lerp(initialZoom, Mathf.Min(initialZoom * 1.6f, maxZoom), Mathf.SmoothStep(0f, 1f, progress));
        }

        /// <summary>船體位移：沿推進方向，前段幾乎不動、引擎全開，之後加速出畫面。</summary>
        protected virtual Vector3 ShipOffset(float progress)
        {
            return thrustDirection * (initialZoom * 4f * Mathf.Pow(progress, 3.5f));
        }

        /// <summary>船體顏色（含透明度）：最後 15% 淡出。</summary>
        protected virtual Color ShipColor(float progress)
        {
            return new Color(1f, 1f, 1f, 1f - FadeAt(progress));
        }

        /// <summary>淡出比例（0 = 完全顯示），火焰與排氣跟著收掉。</summary>
        protected virtual float FadeAt(float progress)
        {
            return Mathf.InverseLerp(0.85f, 1f, progress);
        }

        /// <summary>船體縮放（1 = 原尺寸）。</summary>
        protected virtual float ShipScale(float progress)
        {
            return 1f;
        }

        /// <summary>火焰長度比例：點火時短暫閃爍增強到全推力。</summary>
        protected virtual float FlameAt(float progress)
        {
            if (progress < IgnitionTime)
            {
                return Mathf.Lerp(0.3f, 1f, progress / IgnitionTime) * Mathf.Lerp(0.6f, 1f, Mathf.PerlinNoise1D(elapsed * 20f));
            }
            return 1f;
        }

        /// <summary>船體之後的額外繪製（例如水泡），預設沒有。</summary>
        protected virtual void DrawExtras(Vector3 offset, float progress)
        {
        }

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
            StartEffects();
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
                UpdateEffects(elapsed, progress);
                Find.CameraDriver.SetRootSize(ZoomAt(progress));
                Vector3 offset = ShipOffset(progress);
                float fade = FadeAt(progress);
                if (material != null)
                {
                    float scale = ShipScale(progress);
                    material.color = ShipColor(progress);
                    GenDraw.DrawQuad(material, (center + offset).WithY(AltitudeLayer.Skyfaller.AltitudeFor()),
                        Quaternion.identity, new Vector3(bounds.Width * scale, 1f, bounds.Height * scale));
                }
                if (survey != null)
                {
                    // 淡出時火焰與排氣一起收掉
                    engineEffects.Begin(displayMap);
                    engineEffects.DrawEngines(survey, FlameAt(progress) * (1f - fade), 1f - fade, offset);
                    engineEffects.End();
                }
                DrawExtras(offset, progress);
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
            StopEffects();
            if (capturer != null && !ready)
                GravshipCapturer.IsGravshipRenderInProgress = false;
            Find.CameraDriver.shaker.StopAllShaking();
            Find.CameraDriver.SetRootSize(previousZoom);
            Find.ScreenshotModeHandler.Active = previousScreenshotMode;
            if (material != null) UnityEngine.Object.Destroy(material);
            if (texture != null) UnityEngine.Object.Destroy(texture);
            base.PostClose();
            // 被外部關掉（其他模組、測試、WindowStack 清空）時也要完成離場：原地圖去留、分離信件、相機
            if (!finished)
            {
                finished = true;
                onComplete?.Invoke();
            }
        }
    }
}
