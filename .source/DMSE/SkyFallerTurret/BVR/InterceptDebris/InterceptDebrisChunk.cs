using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DMSE
{
    /// <summary>
    /// A 層主殘骸（docs/攔截殘骸特效_實作與技術美術計畫.md §4.4、§5.2、§5.4）：攔截後掉落的大塊碎片或斷裂彈體，可存檔。
    ///
    /// 計畫原寫 Projectile 子類；實作改為一般 Thing：Projectile 的發射、直線運動、出界銷毀與護盾攔截全都用不到
    /// （<see cref="Projectile_MissileIncoming"/> 也是用 landed 把它們關掉）。
    /// Position 固定在生成格（夾在圖內），畫面位置由 <see cref="state"/> 決定，可在圖外或迷霧上；
    /// def 的 drawOffscreen／seeThroughFog 讓原版不依 Position 剔除。每 tick 依 <see cref="InterceptGeometry.Step"/> 推進，
    /// 沿途留煙跡、熾熱光暈與火花，落地時依地面（屋頂、水、一般地面）產生效果，傷害只在設定開啟時才有。
    /// </summary>
    [StaticConstructorOnStartup]
    public class InterceptDebrisChunk : Thing
    {
        private static readonly Material ShadowMat = MaterialPool.MatFrom("Things/Skyfaller/SkyfallerShadowCircle", ShaderDatabase.Transparent);
        private static readonly MaterialPropertyBlock DrawBlock = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock ShadowBlock = new MaterialPropertyBlock();

        private static readonly Color HotColor = new Color(1f, 0.7f, 0.45f, 1f);
        private static readonly Color MetalColor = Color.white;
        private static readonly Color HulkColor = new Color(0.62f, 0.58f, 0.55f, 1f);

        public DebrisState state;
        /// <summary>斷裂彈體：畫來襲物自己的貼圖、煙跡較濃。</summary>
        public bool hulk;
        /// <summary>斷裂彈體的來源 def（取貼圖）；一般碎片為 null。</summary>
        public ThingDef sourceDef;
        public float drawScale = 1f;
        /// <summary>從熾熱色冷卻到金屬色所需 tick。</summary>
        public int coolTicks = 40;
        /// <summary>冷卻後的顏色（profile 的 majorTint／hulkTint）。</summary>
        public Color tint = Color.white;
        /// <summary>主殘骸貼圖資料夾（profile 的 majorTexPath）；null = def 的預設。</summary>
        public string texPath;
        /// <summary>
        /// 遠距攔截的殘骸：從看不見的虛擬爆心飛來，依畫面位置離外框的距離淡入（<see cref="InterceptGeometry.EdgeFade"/>）。
        /// 淡入要半透明，所以改畫 Transparent 版的材質（Cutout 只有全有或全無）。
        /// </summary>
        public bool remote;
        /// <summary>遠距殘骸已進入畫面；之前不做「畫在圖外」的剔除（它本來就從圖外飛進來）。</summary>
        private bool seen;
        private Graphic chunkGraphic;

        /// <summary>依 profile 的貼圖資料夾取 Graphic_Random（沒有指定時用 def 的）。</summary>
        private Graphic ChunkGraphic
        {
            get
            {
                if (chunkGraphic == null)
                {
                    chunkGraphic = texPath.NullOrEmpty() ? Graphic
                        : GraphicDatabase.Get<Graphic_Random>(texPath, ShaderDatabase.Cutout, Vector2.one, Color.white);
                }
                return chunkGraphic;
            }
        }

        /// <summary>主殘骸材質的 render queue offset（高於迷霧；與細碎片同層）。</summary>
        private const int RenderQueueOffset = 3;

        private static InterceptDebrisPhysics Physics => InterceptDebrisPhysics.Default;

        private static SoundDef impactSound;
        private static bool resolvedSound;

        /// <summary>落地音效：暫借原版子彈打金屬（P6 換自有音效 DMSE_DebrisImpact）。</summary>
        private static SoundDef ImpactSound
        {
            get
            {
                if (!resolvedSound)
                {
                    resolvedSound = true;
                    impactSound = DefDatabase<SoundDef>.GetNamedSilentFail("BulletImpact_Metal");
                }
                return impactSound;
            }
        }

        /// <summary>生成一塊殘骸。Position 為地面點夾進圖內的格；之後不再更新。</summary>
        public static InterceptDebrisChunk SpawnChunk(Map map, DebrisState s, float scale, bool hulk, ThingDef sourceDef, Color? tint = null,
            string texPath = null, bool remote = false)
        {
            var chunk = (InterceptDebrisChunk)ThingMaker.MakeThing(DMSE_DefOf.DMSE_InterceptDebrisChunk);
            chunk.state = s;
            chunk.texPath = texPath;
            chunk.remote = remote;
            chunk.tint = tint ?? (hulk ? HulkColor : MetalColor);
            chunk.drawScale = scale;
            chunk.hulk = hulk;
            chunk.sourceDef = hulk ? sourceDef : null;
            IntVec3 cell = MissileMapEntryUtility.InsetFromEdge(map.Size, s.ground).ToIntVec3();
            GenSpawn.Spawn(chunk, cell, map, WipeMode.Vanish);
            return chunk;
        }

        public override Vector3 DrawPos
        {
            get
            {
                Vector3 p = state.DrawPos(Physics);
                p.y = def.altitudeLayer.AltitudeFor(1.6f);
                return p;
            }
        }

        protected override void Tick()
        {
            base.Tick();
            InterceptGeometry.Step(ref state, Physics, FxWind.Vector(Map));
            if (state.Landed)
            {
                Land();
                return;
            }
            float fade = Fade;
            seen |= fade > 0f;
            if (InterceptGeometry.ShouldCull(state, Map.Size, Physics) || ((!remote || seen) && DrawnOutside()))
            {
                Destroy();
                return;
            }
            if (Map == Find.CurrentMap && fade > 0f) { EmitTrail(fade); }
        }

        /// <summary>不透明度：一般殘骸恆為 1；遠距殘骸依畫面位置離外框的距離淡入。</summary>
        private float Fade => remote ? InterceptGeometry.EdgeFade(Map.Size, state.DrawPos(Physics), Physics) : 1f;

        /// <summary>畫面位置在圖外超過 2 格、地面點也在圖外：被原版邊緣遮罩蓋住，也落不進地圖。</summary>
        private bool DrawnOutside()
        {
            Vector3 drawn = state.DrawPos(Physics);
            drawn.y = 0f;
            return InterceptGeometry.DistanceOutside(Map.Size, drawn) > 2f && InterceptGeometry.DistanceOutside(Map.Size, state.ground) > 0f;
        }

        /// <summary>
        /// 煙跡（§5.2）：上細下粗、上淡下濃靠 fleck 的 growthRate 自然形成；殘骸越晚煙越細。
        /// 熾熱期另有光暈與跡頭火花。純視覺，亂數包在獨立狀態裡。<paramref name="opacity"/> 為遠距殘骸的淡入係數，乘在透明度上。
        /// </summary>
        private void EmitTrail(float opacity)
        {
            MapComponent_InterceptEffects fx = Map.GetComponent<MapComponent_InterceptEffects>();
            if (fx == null) { return; }
            int age = state.age;
            int interval = InterceptFxBudget.TrailInterval(hulk ? 1 : 2, fx.LiveChunks);
            bool hot = age < coolTicks;
            if (age % interval != 0 && !(hot && age % 3 == 0) && age % 5 != 0) { return; }

            Vector3 pos = state.DrawPos(Physics);
            float m = Mathf.Sqrt(state.mass);
            Rand.PushState(Gen.HashCombineInt(thingIDNumber, age));
            try
            {
                if (age % interval == 0 && fx.TryTakeTrailToken())
                {
                    float fade = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(age / 120f));
                    // 中灰貼圖（P6）：tint 只在熾熱期偏暖，之後白色＝貼圖本身的灰。
                    Color c = hot ? new Color(1f, 0.85f, 0.7f, 0.75f) : new Color(1f, 1f, 1f, 0.7f);
                    c.a *= opacity;
                    fx.Canvas.EmitLit(DMSE_DefOf.DMSE_DebrisTrailPuff, pos + Jitter(0.08f), (0.6f + 0.5f * m) * fade * (hulk ? 1.5f : 1f),
                        Rand.Range(0f, 360f), c, hot ? 0.5f : 1f);
                }
                if (hot && age % 3 == 0)
                {
                    fx.Emit(DMSE_DefOf.DMSE_InterceptFireGlow, pos, 1.2f * m * (hulk ? 1.6f : 1f), 0f, new Color(1f, 0.7f, 0.35f, opacity));
                }
                if (age % 5 == 0 && age < 60)
                {
                    fx.Emit(DMSE_DefOf.DMSE_InterceptSparkStar, pos + Jitter(0.2f), 0.6f, Rand.Range(0f, 360f), new Color(1f, 0.9f, 0.55f, opacity));
                }
            }
            finally { Rand.PopState(); }
        }

        private static Vector3 Jitter(float radius)
        {
            Vector2 v = Rand.InsideUnitCircle * radius;
            return new Vector3(v.x, 0f, v.y);
        }

        /// <summary>
        /// 落地（§4.3、§5.4）：落點在圖外則安靜消失；有屋頂只在屋頂上冒火花（不傷人、不留碎屑）；水面濺水花；
        /// 一般地面揚塵、火花、碎屑。傷害只在 <see cref="PlayerConfigSettings.interceptDebrisDamage"/> 開啟時才有。
        /// </summary>
        private void Land()
        {
            Map map = Map;
            IntVec3 cell = state.ground.ToIntVec3();
            if (!cell.InBounds(map))
            {
                Destroy();
                return;
            }

            Vector3 loc = new Vector3(state.ground.x, 0f, state.ground.z);
            bool roofed = cell.Roofed(map);
            bool water = !roofed && cell.GetTerrain(map).IsWater;

            Rand.PushState(Gen.HashCombineInt(thingIDNumber, Find.TickManager.TicksGame));
            try
            {
                if (map == Find.CurrentMap)
                {
                    FleckMaker.ThrowMicroSparks(loc, map);
                    if (water)
                    {
                        FleckMaker.WaterSplash(loc, map, 1f + state.mass, 2f);
                    }
                    else if (!roofed)
                    {
                        int puffs = Mathf.Clamp(Mathf.RoundToInt(state.mass), 1, 3);
                        for (int i = 0; i < puffs; i++) { FleckMaker.ThrowDustPuff(loc + Jitter(0.4f), map, 1f + 0.3f * state.mass); }
                        if (hulk) { FleckMaker.ThrowFireGlow(loc, map, 1.5f); }
                    }
                }
            }
            finally { Rand.PopState(); }

            ImpactSound?.PlayOneShot(new TargetInfo(cell, map));
            if (!roofed && !water)
            {
                FilthMaker.TryMakeFilth(cell, map, DMSE_DefOf.DMSE_Filth_MetalScrap ?? ThingDefOf.Filth_RubbleBuilding);
            }
            if (!roofed && DMSEMod.Settings != null && DMSEMod.Settings.interceptDebrisDamage)
            {
                DamageAt(cell, map);
            }
            Destroy();
        }

        /// <summary>
        /// 小額鈍傷（5–12，斷裂彈體 ×1.5）：格內的 pawn 優先，其次是建築主體（牆、家具等 edifice），最後才是其他建築
        /// （例如埋在牆下的電線）。只在設定開啟時呼叫。
        /// </summary>
        private void DamageAt(IntVec3 cell, Map map)
        {
            Thing victim = cell.GetFirstPawn(map) ?? (Thing)cell.GetEdifice(map);
            if (victim == null)
            {
                foreach (Thing t in cell.GetThingList(map))
                {
                    if (t.def.category == ThingCategory.Building && t.def.useHitPoints) { victim = t; break; }
                }
            }
            if (victim == null) { return; }
            float amount = Rand.Range(5f, 12f) * (hulk ? 1.5f : 1f);
            victim.TakeDamage(new DamageInfo(DamageDefOf.Blunt, amount));
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            float fade = Fade;
            if (fade <= 0f) { return; }
            Vector3 pos = DrawPos;
            float cool = Mathf.Clamp01((float)state.age / Mathf.Max(1, coolTicks));
            Material mat;
            Vector2 size;
            if (hulk && sourceDef?.graphicData != null && sourceDef.graphic != null)
            {
                mat = InterceptFxMaterials.AboveFog(sourceDef.graphic.MatSingle, RenderQueueOffset, remote);
                size = sourceDef.graphicData.drawSize * 0.7f;
            }
            else
            {
                mat = InterceptFxMaterials.AboveFog(ChunkGraphic.MatSingleFor(this), RenderQueueOffset, remote);
                size = Vector2.one * drawScale;
            }
            Color color = FxLighting.Apply(Color.Lerp(HotColor, tint, cool), FxLighting.LightAt(Map, pos), cool);
            color.a *= fade;
            DrawBlock.SetColor(ShaderPropertyIDs.Color, color);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.AngleAxis(state.rotation, Vector3.up), new Vector3(size.x, 1f, size.y));
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0, null, 0, DrawBlock);

            // 影子在地面點（同 Skyfaller）：剩餘下墜 tick 用目前高度與下降速度估算，越接近地面越大越深。
            float fall = Mathf.Max(0.02f, -state.verticalSpeed);
            int ticksLeft = Mathf.RoundToInt(state.height / fall);
            DrawShadow(new Vector3(state.ground.x, 0f, state.ground.z), Vector2.one * Mathf.Max(0.6f, size.x * 0.6f), ticksLeft, fade);
        }

        /// <summary>
        /// 同原版 <see cref="Skyfaller.DrawDropSpotShadow(Vector3, Rot4, Material, Vector2, int)"/>，另乘上淡入係數：
        /// 遠距殘骸從北緣外落進來時，地面點早已在畫面內，影子要跟殘骸一起淡入（原版的共用 property block 帶不進透明度）。
        /// </summary>
        private static void DrawShadow(Vector3 center, Vector2 shadowSize, int ticksToImpact, float fade)
        {
            ticksToImpact = Mathf.Max(ticksToImpact, 0);
            center.y = AltitudeLayer.Shadows.AltitudeFor();
            float grow = 1f + ticksToImpact / 100f;
            Color c = Color.white;
            c.a = (ticksToImpact > 150 ? Mathf.InverseLerp(200f, 150f, ticksToImpact) : 1f) * fade;
            ShadowBlock.SetColor(ShaderPropertyIDs.Color, c);
            Matrix4x4 matrix = Matrix4x4.TRS(center, Quaternion.identity, new Vector3(grow * shadowSize.x, 1f, grow * shadowSize.y));
            Graphics.DrawMesh(MeshPool.plane10Back, matrix, ShadowMat, 0, null, 0, ShadowBlock);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref state.ground, "ground");
            Scribe_Values.Look(ref state.height, "height");
            Scribe_Values.Look(ref state.velocity, "velocity");
            Scribe_Values.Look(ref state.verticalSpeed, "verticalSpeed");
            Scribe_Values.Look(ref state.rotation, "rotation");
            Scribe_Values.Look(ref state.spin, "spin");
            Scribe_Values.Look(ref state.mass, "mass", 1f);
            Scribe_Values.Look(ref state.age, "age");
            Scribe_Values.Look(ref hulk, "hulk", false);
            Scribe_Defs.Look(ref sourceDef, "sourceDef");
            Scribe_Values.Look(ref drawScale, "drawScale", 1f);
            Scribe_Values.Look(ref coolTicks, "coolTicks", 40);
            Scribe_Values.Look(ref texPath, "texPath");
            Scribe_Values.Look(ref remote, "remote", false);
            Scribe_Values.Look(ref seen, "seen", false);
            Scribe_Values.Look(ref tint, "tint", hulk ? HulkColor : MetalColor);
        }
    }
}
