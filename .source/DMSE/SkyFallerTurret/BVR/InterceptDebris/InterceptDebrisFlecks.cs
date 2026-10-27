using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// B 層細碎片（docs/攔截殘骸特效_實作與技術美術計畫.md §5.3）：純視覺、不存檔，每 tick 依 <see cref="InterceptGeometry.Step"/> 推進，
    /// 落地或出界就移除。
    ///
    /// 不用原版 <see cref="FleckSystemBase{TFleck}"/>：它只從 <see cref="FleckCreationData"/> 建 fleck，帶不進高度、垂直速度與質量；
    /// 原版 <see cref="FleckThrown"/> 一跨出地圖就死、也沒有落地。所以每片碎片自己持有一個 <see cref="FleckStatic"/>
    /// 只負責繪製、淡出與縮放，位置用 <see cref="FleckDrawPosition.height"/>（畫成往畫面上方的偏移，與殘骸高度同一慣例）。
    /// 由共用畫布 <see cref="FxCanvas"/> 持有並推進（風由畫布傳入）。
    /// </summary>
    public class InterceptDebrisFlecks
    {
        public struct Piece
        {
            public FleckStatic fleck;
            public DebrisState state;
            public Color hot;
            public Color cold;
            /// <summary>從熾熱色漸變到金屬色所需 tick。</summary>
            public int coolTicks;
            /// <summary>遠距攔截的碎片：從看不見的虛擬爆心飛來，依畫面位置離外框的距離淡入（<see cref="InterceptGeometry.EdgeFade"/>）。</summary>
            public bool remote;
            /// <summary>遠距碎片已進入畫面；之前不做「畫在圖外」的剔除（它本來就從圖外飛進來）。</summary>
            public bool seen;
        }

        /// <summary>移除原因統計（驗收與單元測試用）。</summary>
        public int landedCount, culledCount, expiredCount;

        private readonly List<Piece> pieces = new List<Piece>();
        private readonly List<FleckDef> handledDefs = new List<FleckDef>();
        private readonly InterceptDebrisPhysics physics;

        public InterceptDebrisFlecks(InterceptDebrisPhysics physics = null)
        {
            this.physics = physics ?? InterceptDebrisPhysics.Default;
        }

        public int Count => pieces.Count;

        public IReadOnlyList<Piece> Pieces => pieces;

        /// <summary>
        /// 加一片碎片。<see cref="FleckStatic.Setup"/> 會用 Rand 並讀遊戲 tick：呼叫端必須已 PushState（純視覺，不擾動遊戲亂數）。
        /// </summary>
        public void Spawn(FleckDef def, DebrisState state, float scale, Color hot, Color cold, int coolTicks, bool remote = false)
        {
            if (def == null) { return; }
            handledDefs.AddUnique(def);
            var fleck = new FleckStatic();
            fleck.Setup(new FleckCreationData
            {
                def = def,
                spawnPosition = state.ground,
                scale = scale,
                rotation = state.rotation,
                instanceColor = hot,
                ageTicksOverride = -1
            });
            fleck.position.height = state.height * physics.heightScale;
            if (remote) { fleck.instanceColor.a = 0f; }
            pieces.Add(new Piece { fleck = fleck, state = state, hot = hot, cold = cold, coolTicks = Mathf.Max(1, coolTicks), remote = remote });
        }

        /// <summary>
        /// 每遊戲 tick 推進一次。<paramref name="map"/> 為 null（測試）時不產生落地揚塵。
        /// 落地揚塵只在落點在圖內、無屋頂時產生，並包在獨立亂數狀態裡。
        /// </summary>
        public void Tick(Map map, IntVec3 mapSize, Vector3 wind)
        {
            // 環境光（FxLighting）：碎片冷卻後才完全受光，剛炸開的熾熱期是自己發光。
            FxLighting.Sampler light = FxLighting.For(map);
            for (int i = pieces.Count - 1; i >= 0; i--)
            {
                Piece p = pieces[i];
                InterceptGeometry.Step(ref p.state, physics, wind);

                if (p.state.Landed)
                {
                    landedCount++;
                    OnLanded(map, p);
                    pieces.RemoveAt(i);
                    continue;
                }
                float fade = p.remote ? InterceptGeometry.EdgeFade(mapSize, p.state.DrawPos(physics), physics) : 1f;
                p.seen |= fade > 0f;
                if (InterceptGeometry.ShouldCull(p.state, mapSize, physics) || ((!p.remote || p.seen) && DrawnOutside(p.state, mapSize)))
                {
                    culledCount++;
                    pieces.RemoveAt(i);
                    continue;
                }
                if (p.fleck.TimeInterval(1f / 60f, map))
                {
                    expiredCount++;
                    pieces.RemoveAt(i);
                    continue;
                }

                p.fleck.position.worldPosition = p.state.ground;
                p.fleck.position.height = p.state.height * physics.heightScale;
                p.fleck.exactRotation = p.state.rotation;
                float cooled = Mathf.Clamp01((float)p.state.age / p.coolTicks);
                Color color = FxLighting.Apply(Color.Lerp(p.hot, p.cold, cooled), light.At(p.state.DrawPos(physics)), cooled);
                color.a *= fade;
                p.fleck.instanceColor = color;
                pieces[i] = p;
            }
        }

        /// <summary>
        /// 畫面位置在地圖外框外超過 2 格就移除：地圖外被原版邊緣遮罩蓋住（P2 實機截圖），再飛下去也看不見。
        /// 落地前高度仍高的碎片（畫面位置在北緣外、地面點在圖內）也算看不見。
        /// </summary>
        private bool DrawnOutside(DebrisState s, IntVec3 mapSize)
        {
            Vector3 drawn = s.DrawPos(physics);
            drawn.y = 0f;
            return InterceptGeometry.DistanceOutside(mapSize, drawn) > 2f && InterceptGeometry.DistanceOutside(mapSize, s.ground) > 0f;
        }

        private static void OnLanded(Map map, Piece p)
        {
            if (map == null || p.state.mass < 0.35f) { return; }
            IntVec3 cell = p.state.ground.ToIntVec3();
            if (!cell.InBounds(map) || cell.Roofed(map) || map != Find.CurrentMap) { return; }
            Rand.PushState(Gen.HashCombineInt(cell.GetHashCode(), Find.TickManager.TicksGame));
            try { FleckMaker.ThrowDustPuff(p.state.ground, map, 0.5f + p.state.mass * 0.5f); }
            finally { Rand.PopState(); }
        }

        public void Draw(DrawBatch batch)
        {
            if (pieces.Count == 0) { return; }
            for (int i = 0; i < handledDefs.Count; i++)
            {
                handledDefs[i].graphicData?.ExplicitlyInitCachedGraphic();
                if (handledDefs[i].randomGraphics != null)
                {
                    foreach (GraphicData g in handledDefs[i].randomGraphics) { g.ExplicitlyInitCachedGraphic(); }
                }
            }
            for (int i = 0; i < pieces.Count; i++)
            {
                // 遠距碎片還在邊緣帶外（透明）時不送繪製。
                if (pieces[i].fleck.instanceColor.a <= 0f) { continue; }
                pieces[i].fleck.Draw(batch);
            }
        }
    }
}
