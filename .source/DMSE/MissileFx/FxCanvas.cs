using System.Diagnostics;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈特效共用畫布（docs/導彈發射特效_重製計畫.md §3，L1）：從攔截特效抽出的「自持特效」基礎，
    /// 攔截特效（<see cref="MapComponent_InterceptEffects"/>）與之後的發射特效共用。
    ///
    /// 內容：自持的 <see cref="FleckSystemStatic"/>（靜態 fleck）、彈道碎片（<see cref="InterceptDebrisFlecks"/>）、
    /// 漂浮煙團（<see cref="FxSmokeParticles"/>）、一個 DrawBatch，以及特效用的地圖風（<see cref="FxWind"/>）。
    /// 全部不經 FleckMaker／map.flecks：不受「地圖內＋視野內」的生成檢查與 <c>FleckThrown</c> 出界即死的限制；
    /// 遊戲時間推進（暫停時停住）；只在玩家看著的地圖上繪製。
    /// </summary>
    public class FxCanvas
    {
        private readonly Map map;
        private FleckSystem staticFlecks;
        private readonly DrawBatch drawBatch = new DrawBatch();

        /// <summary>
        /// <see cref="Draw"/> 的累計 CPU 時間（毫秒）與次數：效能量測用（驗收 I7、L6）。驗收環境的幀時間受視窗節流影響，
        /// 不可靠；這裡只量本畫布送出繪製的 CPU 端成本。
        /// </summary>
        public double drawCpuMs;
        public int drawCount;

        /// <summary>彈道碎片（攔截的 B 層細碎片）。</summary>
        public readonly InterceptDebrisFlecks Debris = new InterceptDebrisFlecks();

        /// <summary>漂浮煙團（發射煙柱、地面雲）。</summary>
        public readonly FxSmokeParticles Smoke = new FxSmokeParticles();

        public FxCanvas(Map map)
        {
            this.map = map;
        }

        /// <summary>自持的靜態 fleck 系統；還沒發過任何 fleck 時為 null。</summary>
        public FleckSystem StaticFlecksOrNull => staticFlecks;

        private FleckSystem StaticFlecks
        {
            get
            {
                if (staticFlecks == null) { staticFlecks = new FleckSystemStatic(map.flecks); }
                staticFlecks.parent = map.flecks;
                return staticFlecks;
            }
        }

        /// <summary>
        /// 直接交給自持系統（不經 FleckMaker 的地圖內檢查）；y 由 FleckDef 的 altitudeLayer 決定。
        /// <see cref="FleckStatic.Setup"/> 會用 Rand：純視覺呼叫端要先 PushState。
        /// </summary>
        public void Emit(FleckDef def, Vector3 pos, float scale, float rotation, Color color)
        {
            if (def == null) { return; }
            FleckSystem system = StaticFlecks;
            system.handledDefs.AddUnique(def);
            system.CreateFleck(new FleckCreationData
            {
                def = def,
                spawnPosition = pos,
                scale = scale,
                rotation = rotation,
                instanceColor = color,
                ageTicksOverride = -1
            });
        }

        /// <summary>
        /// 同 <see cref="Emit"/>，但顏色先乘上發射位置的環境光（<see cref="FxLighting"/>）：給煙這類「被照亮」的靜態 fleck。
        /// <paramref name="litAmount"/> &lt; 1 時保留部分自身亮度（例如剛被尾焰照亮的煙跡）。
        /// </summary>
        public void EmitLit(FleckDef def, Vector3 pos, float scale, float rotation, Color color, float litAmount = 1f)
            => Emit(def, pos, scale, rotation, FxLighting.Apply(color, FxLighting.LightAt(map, pos), litAmount));

        /// <summary>特效用的地圖風向量（<see cref="FxWind"/>）。</summary>
        public Vector3 Wind => FxWind.Vector(map);

        /// <summary>每遊戲 tick（MapComponentTick）呼叫。</summary>
        public void Tick()
        {
            staticFlecks?.Tick();
            if (Debris.Count == 0 && Smoke.Count == 0) { return; }
            Vector3 wind = Wind;
            if (Debris.Count > 0) { Debris.Tick(map, map.Size, wind); }
            if (Smoke.Count > 0) { Smoke.Tick(map.Size, wind, map); }
        }

        /// <summary>每幀（MapComponentUpdate）呼叫：只在玩家看著的地圖上推進即時 fleck 並繪製。</summary>
        public void Draw()
        {
            if (map != Find.CurrentMap || (staticFlecks == null && Debris.Count == 0 && Smoke.Count == 0)) { return; }
            long start = Stopwatch.GetTimestamp();
            if (staticFlecks != null)
            {
                staticFlecks.Update(Time.deltaTime);
                staticFlecks.ForceDraw(drawBatch);
            }
            Smoke.Draw(drawBatch, Find.CameraDriver.CurrentViewRect.ExpandedBy(2));
            Debris.Draw(drawBatch);
            drawBatch.Flush(true);
            drawCpuMs += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            drawCount++;
        }
    }
}
