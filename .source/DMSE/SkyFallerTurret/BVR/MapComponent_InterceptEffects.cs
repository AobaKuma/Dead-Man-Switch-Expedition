using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// 地圖特效類：導彈/空投攔截成功時，在來襲航線上的爆心（可在地圖外）生成空爆特效與音效
    /// （docs/攔截殘骸特效_實作與技術美術計畫.md §5）。爆心由 <see cref="InterceptEventData"/> 決定：
    /// 末端在 CIWS 掃射點上空，中過程在入場邊緣外。遠距末端攔截（<see cref="InterceptEventData.remote"/>）的爆心看不見：
    /// 不畫爆炸，只丟會飛進畫面的殘骸，進入畫面時淡入。
    ///
    /// 特效 fleck、細碎片與煙都畫在共用的 <see cref="FxCanvas"/> 上（自持 fleck 系統，不經 FleckMaker／map.flecks，
    /// 不受「地圖內＋視野內」的生成檢查限制；docs/導彈發射特效_重製計畫.md L1 從本類別抽出）。
    /// 類別名不改：MapComponent 依類別名存檔。
    /// </summary>
    public class MapComponent_InterceptEffects : MapComponent
    {
        /// <summary>共用特效畫布（自持 fleck 系統、細碎片、漂浮煙團）。</summary>
        public FxCanvas Canvas { get; }

        private int lastWeatherFlashTick = -9999;
        private const int WeatherFlashMinInterval = 12;

        // 齊射節流：短時間內多次攔截時減少每次的 fleck 數（正式預算見計畫 §8，P6）。
        private int lastBurstTick = -9999;
        private int recentBursts;
        private const int DenseBurstInterval = 6;
        /// <summary>間隔內第幾次攔截起改用精簡版。</summary>
        private const int DenseBurstCount = 3;

        private static SoundDef defaultSound;
        private static bool resolvedSound;

        private int liveChunksTick = -1;
        private int liveChunks;
        private int trailTokensTick = -1;
        private int trailTokens;

        public MapComponent_InterceptEffects(Map map) : base(map)
        {
            Canvas = new FxCanvas(map);
        }

        /// <summary>煙跡總量預算：本 tick 還能發一團就扣一個並回傳 true（<see cref="InterceptFxBudget.TrailPuffsPerTick"/>）。</summary>
        public bool TryTakeTrailToken()
        {
            int now = Find.TickManager.TicksGame;
            if (trailTokensTick != now)
            {
                trailTokensTick = now;
                trailTokens = InterceptFxBudget.TrailPuffsPerTick;
            }
            if (trailTokens <= 0) { return false; }
            trailTokens--;
            return true;
        }

        /// <summary>目前在空中的主殘骸數（每 tick 快取一次；預算與煙跡間隔用）。</summary>
        public int LiveChunks
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                if (liveChunksTick != now)
                {
                    liveChunksTick = now;
                    liveChunks = map.listerThings.ThingsOfDef(DMSE_DefOf.DMSE_InterceptDebrisChunk).Count;
                }
                return liveChunks;
            }
        }

        /// <summary>最近一次攔截預測的殘骸落點（屍體空投艙用；驗收與除錯讀取）。</summary>
        public IntVec3 LastDebrisLandingCell { get; private set; } = IntVec3.Invalid;

        /// <summary>
        /// 外部呼叫入口：依攔截資料在航線上的爆心生成空爆特效、殘骸與音效。
        /// 回傳「載著屍體的那塊殘骸」的預測落點（斷裂彈體／艙體優先，否則最重的一塊）；落在圖外或沒有主殘骸時為 Invalid。
        /// </summary>
        public static IntVec3 Trigger(Map map, InterceptEventData data)
        {
            if (map == null) { return IntVec3.Invalid; }
            MapComponent_InterceptEffects comp = map.GetComponent<MapComponent_InterceptEffects>();
            return comp != null ? comp.AddInterception(data) : IntVec3.Invalid;
        }

        /// <summary>只有落點時的相容入口：方位退回「地圖中心 → 落點」。</summary>
        public static void Trigger(Map map, IntVec3 cell, bool terminal, SoundDef sound = null)
        {
            if (map == null) { return; }
            Trigger(map, InterceptEventData.FromCell(map, cell, terminal, sound));
        }

        public IntVec3 AddInterception(InterceptEventData data)
        {
            InterceptDebrisProfileDef profile = InterceptDebrisProfileDef.For(data);
            int nowTick = Find.TickManager.TicksGame;
            recentBursts = nowTick - lastBurstTick < DenseBurstInterval ? recentBursts + 1 : 0;
            lastBurstTick = nowTick;
            bool dense = recentBursts >= DenseBurstCount;

            // 純視覺：只在玩家看著的地圖上產生；亂數包在獨立狀態裡，不擾動遊戲亂數序列（FleckStatic.Setup 也會用 Rand）。
            if (map == Find.CurrentMap)
            {
                Rand.PushState(data.seed);
                try
                {
                    if (data.remote) { SpawnMinorDebris(data, profile, dense); }
                    else { SpawnBurst(data, profile, dense); }
                }
                finally { Rand.PopState(); }
            }

            // A 層主殘骸會落地（碎屑、可選傷害），所以不論玩家看哪張地圖都生成；亂數用獨立狀態，同一次攔截結果固定。
            IntVec3 landing;
            Rand.PushState(Gen.HashCombineInt(data.seed, 0x5bd1));
            try { landing = SpawnMajorDebris(data, profile, dense); }
            finally { Rand.PopState(); }
            LastDebrisLandingCell = landing;

            // 中過程攔截發生在地圖外：不播放地圖內爆炸音，也不觸發天光，只讓殘骸淡入畫面。
            SoundDef s = data.terminal ? data.sound ?? DefaultSound : null;
            if (s != null)
            {
                s.PlayOneShot(SoundInfo.InMap(new TargetInfo(data.landing, map)));
            }

            // 末端攔截才觸發空爆閃光；遠距末端在畫面外，只剩微弱天光。中過程不觸發閃光。
            if (data.terminal && nowTick - lastWeatherFlashTick >= WeatherFlashMinInterval)
            {
                lastWeatherFlashTick = nowTick;
                Vector2 shadow = new Vector2(Rand.Range(-4f, 4f), Rand.Range(-4f, 0f));
                float intensity = data.remote ? 0.25f : data.terminal ? 0.45f : 0.65f;
                map.weatherManager.eventHandler.AddEvent(new WeatherEvent_MissileFlash(map, intensity, shadow));
            }
            return landing;
        }

        /// <summary>
        /// 空爆（§5.1）：白熱閃光、火球、星芒火花、慢慢擴散的煙團。中過程（較高較遠）整體放大；
        /// profile 沒有火球時（空投艙沒有彈頭）只出煙團。
        /// </summary>
        private void SpawnBurst(InterceptEventData data, InterceptDebrisProfileDef profile, bool dense)
        {
            Vector3 center = data.BurstDrawPos(InterceptDebrisPhysics.Default);
            float size = data.terminal ? 1f : 1.4f;
            bool fire = profile.fireball;

            // 尺寸（格）依 2026-10-26 實機截圖放大：原本 2–3 格的煙團在一般縮放下幾乎看不到。
            Emit(DMSE_DefOf.DMSE_InterceptFlash, center, 10f * size, 0f, new Color(1f, 0.95f, 0.85f, 1f));

            if (fire)
            {
                int fireballs = dense ? 1 : InterceptFxBudget.Scale(Rand.RangeInclusive(2, 4), 1);
                for (int i = 0; i < fireballs; i++)
                {
                    // 貼圖自帶火色漸層（P6），tint 只做一點冷暖變化。
                    Color c = Color.Lerp(new Color(1f, 1f, 1f, 0.95f), new Color(1f, 0.85f, 0.75f, 0.95f), Rand.Value);
                    Emit(DMSE_DefOf.DMSE_InterceptFireball, center + Jitter(1f * size), Rand.Range(3f, 4.5f) * size, Rand.Range(0f, 360f), c);
                }
                Emit(DMSE_DefOf.DMSE_InterceptFireGlow, center, 7f * size, 0f, new Color(1f, 0.7f, 0.35f, 1f));
            }

            int smokes = dense ? 2 : InterceptFxBudget.Scale(Rand.RangeInclusive(4, 7), 2);
            for (int i = 0; i < smokes; i++)
            {
                // 灰階在貼圖裡（淺灰／中灰，P6）；tint 只做 ±10% 明暗變化。P2 的 Mote shader 上深色 tint 會消失，現在改 Transparent。
                float grey = Rand.Range(0.9f, 1f);
                // 煙受環境光（FxLighting）；火球、光暈、閃光、火花是自己發光，不套用。
                Canvas.EmitLit(DMSE_DefOf.DMSE_InterceptSmokeBall, center + Jitter(2f * size), Rand.Range(4f, 6.5f) * size,
                    Rand.Range(0f, 360f), new Color(grey, grey, grey, 0.85f));
            }

            int sparks = dense ? 4 : InterceptFxBudget.Scale(Rand.RangeInclusive(6, 12), 2);
            for (int i = 0; i < sparks; i++)
            {
                Emit(DMSE_DefOf.DMSE_InterceptSparkStar, center + Jitter(3f * size), Rand.Range(1.2f, 2.2f) * size,
                    Rand.Range(0f, 360f), new Color(1f, 0.9f, 0.55f, 1f));
            }

            SpawnMinorDebris(data, profile, dense);
        }

        /// <summary>
        /// B 層細碎片（§4、§5.3）：繼承來襲物的前進動量，所以從來襲方向那一側往前散落。輕片阻力大、落點近；
        /// 剛炸開時是熾熱橙色，約半秒內冷卻成 profile 的顏色。遠距攔截只生成會飛進畫面的碎片（數量照算，看不到的略過）。
        /// </summary>
        private void SpawnMinorDebris(InterceptEventData data, InterceptDebrisProfileDef profile, bool dense)
        {
            InterceptDebrisPhysics p = InterceptDebrisPhysics.Default;
            int count = InterceptFxBudget.MinorCount(dense ? profile.denseMinorCount : profile.minorCount.RandomInRange, Canvas.Debris.Count);
            float speed = data.targetSpeed * profile.inheritFactor;
            Vector3 wind = Canvas.Wind;
            for (int i = 0; i < count; i++)
            {
                float mass = profile.minorMass.RandomInRange;
                DebrisState s = InterceptGeometry.Launch(data.burstGround, data.burstHeight, data.heading, speed, mass, p);
                if (data.remote && !InterceptGeometry.BecomesVisible(s, map.Size, p, wind)) { continue; }
                float scale = 0.45f + 0.5f * Mathf.Sqrt(mass);
                Canvas.Debris.Spawn(DMSE_DefOf.DMSE_InterceptDebrisMinor, s, scale, new Color(1f, 0.75f, 0.45f, 1f), profile.minorTint, 30, data.remote);
            }
        }

        /// <summary>
        /// A 層主殘骸（§4、§5.2）：profile 的塊數＋一定機率一段斷裂彈體／艙體（畫來襲物自己的貼圖）；齊射精簡版不丟斷裂彈體。
        /// 回傳屍體空投艙該落的格（§1-4）：斷裂彈體優先，否則最重的一塊，用無風彈道預測落點；落在圖外為 Invalid。
        /// 遠距攔截只生成會飛進畫面的殘骸（載屍殘骸也只從生成的裡面挑）。
        /// </summary>
        private IntVec3 SpawnMajorDebris(InterceptEventData data, InterceptDebrisProfileDef profile, bool dense)
        {
            InterceptDebrisPhysics p = InterceptDebrisPhysics.Default;
            float speed = data.targetSpeed * profile.inheritFactor;
            Vector3 wind = Canvas.Wind;
            int live = LiveChunks;
            int count = InterceptFxBudget.MajorCount(dense ? profile.denseMajorCount : profile.majorCount.RandomInRange, live);
            DebrisState carrier = default;
            bool hasCarrier = false;
            for (int i = 0; i < count; i++)
            {
                float mass = profile.majorMass.RandomInRange;
                DebrisState s = InterceptGeometry.Launch(data.burstGround, data.burstHeight, data.heading, speed, mass, p);
                if (data.remote && !InterceptGeometry.BecomesVisible(s, map.Size, p, wind)) { continue; }
                InterceptDebrisChunk.SpawnChunk(map, s, 0.6f + 0.35f * mass, false, null, profile.majorTint, profile.majorTexPath, data.remote);
                if (!hasCarrier || s.mass > carrier.mass) { carrier = s; hasCarrier = true; }
            }
            if (!dense && InterceptFxBudget.AllowHulk(live + count) && data.payloadDef != null && Rand.Chance(profile.hulkChance))
            {
                DebrisState hulk = InterceptGeometry.Launch(data.burstGround, data.burstHeight, data.heading, speed, 2f, p, hulk: true);
                if (!data.remote || InterceptGeometry.BecomesVisible(hulk, map.Size, p, wind))
                {
                    InterceptDebrisChunk.SpawnChunk(map, hulk, 1f, true, data.payloadDef, profile.hulkTint, remote: data.remote);
                    carrier = hulk;
                    hasCarrier = true;
                }
            }
            if (!hasCarrier)
            {
                // 主殘骸已達上限、這次沒丟：屍體空投落點用一塊不生成的模擬殘骸預測。
                carrier = InterceptGeometry.Launch(data.burstGround, data.burstHeight, data.heading, speed, profile.majorMass.max, p);
            }
            // 與殘骸實際飛行用同一個風（FxWind），預測才一致。
            IntVec3 cell = InterceptGeometry.SimulateLanding(carrier, p, wind, out _).ToIntVec3();
            return cell.InBounds(map) ? cell : IntVec3.Invalid;
        }

        private static Vector3 Jitter(float radius)
        {
            Vector2 v = Rand.InsideUnitCircle * radius;
            return new Vector3(v.x, 0f, v.y);
        }

        /// <summary>直接交給共用畫布的自持系統（不經 FleckMaker 的地圖內檢查）；y 由 FleckDef 的 altitudeLayer 決定。</summary>
        internal void Emit(FleckDef def, Vector3 pos, float scale, float rotation, Color color) => Canvas.Emit(def, pos, scale, rotation, color);

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            Canvas.Tick();
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            Canvas.Draw();
        }

        private static SoundDef DefaultSound
        {
            get
            {
                if (!resolvedSound)
                {
                    resolvedSound = true;
                    defaultSound = DefDatabase<SoundDef>.GetNamedSilentFail("DMSE_Missile_Shot");
                }
                return defaultSound;
            }
        }
    }
}
