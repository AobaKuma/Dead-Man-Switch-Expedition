using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 引擎效果（設計稿 §6-1、§6-3）：
    /// <list type="bullet">
    /// <item>點火階段（父物件為點火中的 <see cref="WorldObject_TransferShip"/>）：全推力火焰、鏡頭光暈、排氣 fleck、
    /// 低頻震動、推進器音效、尾焰區傷害。</item>
    /// <item>倒數（引擎暖機，原地圖上 <see cref="CompTransferConsole"/> 處於 Countdown）：同樣的火焰與排氣，
    /// 依倒數進度從斷續點火增強到約半推力；噴口火花與熱光、逐漸增強的震動與音效。暖機不造成尾焰傷害（倒數警報已提示清場）。</item>
    /// </list>
    /// 滑行期沒有任何飛行副作用。火焰繪製邏輯對照原版 GravshipRenderer（與舊版 MapComponent_Ship 相同的公式）。
    ///
    /// 尾焰傷害取代舊版「每 tick 全 pawn × 全推進器」的汽化檢查：每 exhaustDamageIntervalTicks 只掃描
    /// 運作中引擎的尾焰區格子，成本只與尾焰區格數相關。
    /// </summary>
    [StaticConstructorOnStartup]
    public class MapComponent_TransferBurn : MapComponent
    {
        private const float ExhaustDamage = 30f;

        private const int ShakeInterval = 60;

        private const float ShakeMagnitude = 0.04f;

        // ── 暖機 ──
        private const int WarmUpShakeInterval = 30;

        private const int WarmUpSparkInterval = 12;

        /// <summary>暖機結束時的火焰長度（相對全推力）。</summary>
        private const float WarmUpMaxFlame = 0.55f;

        /// <summary>暖機前段火焰斷續：進度低於此值時火焰會隨機熄滅。</summary>
        private const float WarmUpSputterUntil = 0.35f;

        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        private static readonly List<CompTransferConsole> tmpConsoles = new List<CompTransferConsole>();

        private readonly TransferEngineEffects effects = new TransferEngineEffects();

        private Sustainer thrusterSustainer;

        public MapComponent_TransferBurn(Map map) : base(map)
        {
        }

        private WorldObject_TransferShip BurningShip
        {
            get
            {
                WorldObject_TransferShip ship = map.Parent as WorldObject_TransferShip;
                return ship != null && ship.IsBurning ? ship : null;
            }
        }

        /// <summary>此地圖上正在倒數（引擎暖機）的控制台。</summary>
        private List<CompTransferConsole> CountdownConsoles()
        {
            tmpConsoles.Clear();
            MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(map);
            if (comp == null)
            {
                return tmpConsoles;
            }
            List<CompTransferConsole> consoles = comp.Consoles;
            for (int i = 0; i < consoles.Count; i++)
            {
                if (consoles[i].State == TransferConsoleState.Countdown)
                {
                    tmpConsoles.Add(consoles[i]);
                }
            }
            return tmpConsoles;
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            int now = Find.TickManager.TicksGame;
            WorldObject_TransferShip ship = BurningShip;
            if (ship != null)
            {
                int interval = Mathf.Max(1, TransferFlightMath.Tuning.exhaustDamageIntervalTicks);
                if (now % interval == 0)
                {
                    TransferShipSurvey survey = ship.FlightSurvey();
                    if (survey != null)
                    {
                        DamageExhaustZones(survey);
                    }
                }
                if (now % ShakeInterval == 0 && Find.CurrentMap == map)
                {
                    Find.CameraDriver.shaker.DoShake(ShakeMagnitude);
                }
                return;
            }

            List<CompTransferConsole> consoles = CountdownConsoles();
            float maxProgress = 0f;
            for (int i = 0; i < consoles.Count; i++)
            {
                float t = consoles[i].CountdownProgress;
                maxProgress = Mathf.Max(maxProgress, t);
                TransferShipSurvey survey = consoles[i].Survey;
                if (survey != null && now % WarmUpSparkInterval == 0)
                {
                    WarmUpSparks(survey, t);
                }
            }
            // 震動從暖機三成開始，逐漸增強到接近點火時的強度
            if (maxProgress > 0.3f && now % WarmUpShakeInterval == 0 && Find.CurrentMap == map)
            {
                Find.CameraDriver.shaker.DoShake(Mathf.Lerp(0.005f, ShakeMagnitude * 0.75f, Mathf.InverseLerp(0.3f, 1f, maxProgress)));
            }
        }

        private void DamageExhaustZones(TransferShipSurvey survey)
        {
            for (int i = 0; i < survey.engines.Count; i++)
            {
                TransferShipSurvey.EngineEntry e = survey.engines[i];
                if (!e.Operational)
                {
                    continue;
                }
                e.engine.GetExclusionZone(tmpCells);
                for (int j = 0; j < tmpCells.Count; j++)
                {
                    IntVec3 c = tmpCells[j];
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    List<Thing> things = map.thingGrid.ThingsListAt(c).ListFullCopy();
                    for (int k = 0; k < things.Count; k++)
                    {
                        Thing t = things[k];
                        if (t == e.engine.parent || t.Destroyed || (!t.def.useHitPoints && !(t is Pawn)))
                        {
                            continue;
                        }
                        t.TakeDamage(new DamageInfo(DamageDefOf.Flame, ExhaustDamage, 0f, -1f, e.engine.parent));
                    }
                }
            }
        }

        /// <summary>暖機中噴口的火花與熱光；進度越高越頻繁。</summary>
        private void WarmUpSparks(TransferShipSurvey survey, float progress)
        {
            for (int i = 0; i < survey.engines.Count; i++)
            {
                TransferShipSurvey.EngineEntry e = survey.engines[i];
                if (!e.Operational || !Rand.Chance(Mathf.Lerp(0.25f, 0.9f, progress)))
                {
                    continue;
                }
                Vector3 nozzle = e.engine.NozzlePos;
                IntVec3 cell = nozzle.ToIntVec3();
                if (!cell.InBounds(map))
                {
                    continue;
                }
                FleckMaker.ThrowMicroSparks(nozzle, map);
                if (Rand.Chance(0.4f))
                {
                    FleckMaker.ThrowHeatGlow(cell, map, Mathf.Lerp(1f, 2.5f, progress) * e.engine.parent.def.size.x * 0.5f);
                }
            }
        }

        // ── 繪製與音效 ───────────────────────────────────────────────────────

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            float volume = 0f;
            // 太空地圖以世界為背景時 WorldRendered 也是 true；只有切到世界地圖（WorldSelected）才不畫。
            // 離場動畫擷取船體影像時不畫，否則火焰與全螢幕光暈會被拍進船體貼圖
            if (Find.CurrentMap == map && !WorldRendererUtility.WorldSelected && !GravshipCapturer.IsGravshipRenderInProgress)
            {
                volume = DrawEffects();
            }
            UpdateSustainer(volume);
        }

        /// <summary>繪製點火或暖機效果；回傳推進器音效音量（0 = 無）。</summary>
        private float DrawEffects()
        {
            WorldObject_TransferShip ship = BurningShip;
            if (ship != null)
            {
                TransferShipSurvey survey = ship.FlightSurvey();
                if (survey == null)
                {
                    return 0f;
                }
                effects.Begin(map);
                effects.DrawEngines(survey, 1f, 1f, Vector3.zero);
                effects.End();
                return 1f;
            }

            List<CompTransferConsole> consoles = CountdownConsoles();
            if (consoles.Count == 0)
            {
                return 0f;
            }
            float volume = 0f;
            effects.Begin(map);
            for (int i = 0; i < consoles.Count; i++)
            {
                TransferShipSurvey survey = consoles[i].Survey;
                if (survey == null)
                {
                    continue;
                }
                float t = consoles[i].CountdownProgress;
                float flame = Mathf.Lerp(0.1f, WarmUpMaxFlame, t);
                if (t < WarmUpSputterUntil && Mathf.PerlinNoise(Time.realtimeSinceStartup * 6f, i * 7.3f) < Mathf.Lerp(0.6f, 0.2f, t / WarmUpSputterUntil))
                {
                    // 點火初期斷續：火焰熄滅，只剩少量排氣
                    flame = 0f;
                }
                effects.DrawEngines(survey, flame, Mathf.Lerp(0.15f, 0.6f, t), Vector3.zero);
                volume = Mathf.Max(volume, Mathf.Lerp(0.15f, 0.8f, t));
            }
            effects.End();
            return volume;
        }

        /// <summary>推進器持續音效（原版逆重船降落時的推進器音）；只在檢視此地圖時播放，音量隨暖機進度增強。</summary>
        private void UpdateSustainer(float volume)
        {
            if (volume <= 0f)
            {
                if (thrusterSustainer != null && !thrusterSustainer.Ended)
                {
                    thrusterSustainer.End();
                }
                thrusterSustainer = null;
                return;
            }
            if (thrusterSustainer == null || thrusterSustainer.Ended)
            {
                thrusterSustainer = SoundDefOf.Gravship_RetroThruster_Loop.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.PerFrame));
                if (thrusterSustainer == null)
                {
                    return;
                }
            }
            thrusterSustainer.info.volumeFactor = volume;
            thrusterSustainer.Maintain();
        }
    }
}
