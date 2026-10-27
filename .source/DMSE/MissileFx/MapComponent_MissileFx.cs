using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈發射特效的地圖元件（docs/導彈發射特效_重製計畫.md §3）：持有發射用的 <see cref="FxCanvas"/>（煙柱、地面雲、閃光），
    /// 以及正在飛的發射特效（<see cref="LaunchFxEmitter"/>）清單，每幀畫它們的尾焰。
    /// 發射 Projectile 的 DrawAt 不會呼叫 comp 的繪製，所以尾焰由這裡統一畫；舊存檔沒有本元件時原版自動補上。
    /// </summary>
    public class MapComponent_MissileFx : MapComponent
    {
        public FxCanvas Canvas { get; }

        private readonly List<LaunchFxEmitter> active = new List<LaunchFxEmitter>();
        private readonly DrawBatch plumeBatch = new DrawBatch();

        public MapComponent_MissileFx(Map map) : base(map)
        {
            Canvas = new FxCanvas(map);
        }

        public static MapComponent_MissileFx For(Map map) => map?.GetComponent<MapComponent_MissileFx>();

        public IReadOnlyList<LaunchFxEmitter> Active => active;

        public void Register(LaunchFxEmitter emitter)
        {
            if (emitter != null && !active.Contains(emitter)) { active.Add(emitter); }
        }

        public void Unregister(LaunchFxEmitter emitter) => active.Remove(emitter);

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            Canvas.Tick();
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            Canvas.Draw();
            if (map != Find.CurrentMap || active.Count == 0) { return; }
            float alpha = TickInterpolation.Alpha;
            for (int i = 0; i < active.Count; i++)
            {
                active[i].Draw(plumeBatch, alpha);
            }
            plumeBatch.Flush(true);
        }
    }
}
