using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 平台下方海面的明暗（從 DepthRim 的 PlatformShadow 簡化）：天光遮蔽（甲板正下方，大半徑模糊）與陽光投影
    /// （甲板與支柱擋住太陽，方向、長度、強度照原版建築影子）。著色器直接在海面與柱身上算，沒有另外的暗化層，也不影響遊戲邏輯的光照。
    /// </summary>
    public static class SeaPlatformShadow
    {
        /// <summary>原版影子向量換成甲板影子位移的倍率（再乘甲板高度）。</summary>
        public const float CastScale = 0.1f;

        public const float MaxCast = 14f;

        public const float CastDarkness = 0.55f;

        public const float SkyDarkness = 0.3f;

        public const float MoonShare = 0.3f;

        public const float ClearShadowLuma = 0.74f;

        /// <summary>海面遠在下方：影子淡一點（濃度倍率）、柔一點（多混大半徑模糊）。</summary>
        public const float Fade = 0.65f;

        public const float ExtraSoftness = 0.35f;

        public const float DeckSigma = 0.9f;

        public const float SkySigma = 3f;

        public const float PillarSigma = 0.8f;

        /// <summary>這一刻的太陽：xy = 甲板影子位移（格），z = 影子強度（時刻、天氣、日夜）。</summary>
        public static Vector4 Cast(Map map, float seaDrop)
        {
            GenCelestial.LightInfo info = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow);
            Color shadow = map.skyManager.CurSky.colors.shadow;
            float luma = (shadow.r + shadow.g + shadow.b) / 3f;
            float strength = info.intensity * Mathf.InverseLerp(1f, ClearShadowLuma, luma);
            if (!GenCelestial.IsDaytime(GenCelestial.CurCelestialSunGlow(map)))
            {
                strength *= MoonShare;
            }
            Vector2 cast = Vector2.ClampMagnitude(info.vector * (seaDrop * CastScale), MaxCast);
            return new Vector4(cast.x, cast.y, Mathf.Clamp01(strength), 0f);
        }

        /// <summary>可分離的高斯模糊（地圖外當成 0）。</summary>
        public static float[] Blur(float[] src, int w, int h, float sigma)
        {
            int r = Mathf.CeilToInt(sigma * 3f);
            float[] kernel = new float[r * 2 + 1];
            float sum = 0f;
            for (int i = -r; i <= r; i++)
            {
                kernel[i + r] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += kernel[i + r];
            }
            for (int i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= sum;
            }
            float[] tmp = new float[src.Length];
            float[] dst = new float[src.Length];
            for (int z = 0; z < h; z++)
            {
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    float v = 0f;
                    int k0 = Mathf.Max(-r, -x);
                    int k1 = Mathf.Min(r, w - 1 - x);
                    for (int k = k0; k <= k1; k++)
                    {
                        v += src[row + x + k] * kernel[k + r];
                    }
                    tmp[row + x] = v;
                }
            }
            for (int z = 0; z < h; z++)
            {
                int k0 = Mathf.Max(-r, -z);
                int k1 = Mathf.Min(r, h - 1 - z);
                for (int x = 0; x < w; x++)
                {
                    float v = 0f;
                    for (int k = k0; k <= k1; k++)
                    {
                        v += tmp[(z + k) * w + x] * kernel[k + r];
                    }
                    dst[z * w + x] = v;
                }
            }
            return dst;
        }

        public static byte ToByte(float v)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
        }
    }

    /// <summary>
    /// 海面的擺動（和 DepthRim 的 SeaSway 同一組式子，著色器的 swayOffset／swayVelocity）：左右為主、上下為輔，來回擺盪。
    /// 幅度隨風速：平靜時 1，暴風時最多 2.5 倍。
    /// </summary>
    public static class SeaPlatformSway
    {
        public const float BaseAmplitude = 0.6f;

        public const float VerticalShare = 0.35f;

        public const float ChangePerSecond = 0.25f;

        public static float WeatherFactor(float windSpeed)
        {
            return Mathf.Clamp(1f + 1.2f * (windSpeed - 0.5f), 1f, 2.5f);
        }
    }

    /// <summary>
    /// 海上平台地圖的海面資料：材質（海面、柱身）與遮罩（每格一個像素：r = 甲板（模糊）、g = 支柱入水處（模糊）、
    /// b = 不用、a = 甲板（大半徑模糊，天光遮蔽））。遮罩在地形或支柱改變時標記，節流後在主執行緒重建。
    /// 只在海上平台地圖上建立材質。
    /// </summary>
    public class MapComponent_SeaPlatformSea : MapComponent
    {
        /// <summary>原版深海的畫法：x = 原版配色比重、y = 自己的浪峰線、z = 漣漪密度、w = 漣漪強度。</summary>
        public static readonly Vector4 VanillaParams = new Vector4(1f, 0f, 1f, 1f);

        /// <summary>浪花擺動的相位場：x = 雜訊圖一個像素幾格、y = 相位最多差幾秒、zw = 雜訊圖平移（像素／秒）。</summary>
        public static readonly Vector4 FoamPhaseParams = new Vector4(12f, 5.7f, 0.01f, 0.006f);

        public const float TextureRebuildSeconds = 0.25f;

        private Material material;

        private readonly Dictionary<string, Material> pillarMaterials = new Dictionary<string, Material>();

        private Texture2D mask;

        private bool maskStale = true;

        private float nextMaskRebuild;

        private float swayFactor = 1f;

        public MapComponent_SeaPlatformSea(Map map) : base(map)
        {
        }

        private SeaPlatformMapExtension Ext => SeaPlatformUtility.Ext(map);

        public void MarkDirty()
        {
            maskStale = true;
        }

        public Texture2D Mask
        {
            get
            {
                if (mask == null)
                {
                    RebuildMask();
                }
                return mask;
            }
        }

        /// <summary>這張地圖的海面材質（第一次要用時建立）。著色器沒解析到、或不是海上平台地圖時回 null。</summary>
        public Material Material
        {
            get
            {
                if (material == null)
                {
                    SeaPlatformMapExtension ext = Ext;
                    Shader shader = ext == null ? null : ShaderFor("DMSE_SeaPlatformSea");
                    if (shader == null)
                    {
                        return null;
                    }
                    material = new Material(shader)
                    {
                        name = "DMSE_SeaPlatformSea_" + map.uniqueID,
                        mainTexture = BaseContent.WhiteTex,
                    };
                    // 著色器的 @default 不會自動套到材質上，每個參數都要在這裡設
                    material.SetColor("_Color", new Color(0.055f, 0.215f, 0.300f));
                    material.SetColor("_DeepColor", new Color(0.030f, 0.120f, 0.190f));
                    material.SetColor("_CrestColor", new Color(0.300f, 0.520f, 0.600f));
                    material.SetColor("_FoamColor", new Color(0.850f, 0.920f, 0.940f));
                    ApplyShadowParams(material, ext);
                    material.SetVector("_Sway", SwayVector());
                    material.SetFloat("_WaveSpeed", 1f);
                    material.SetFloat("_FoamAmount", 1f);
                    material.SetFloat("_GlintAmount", 0f);
                    material.SetVector("_FoamPhase", FoamPhaseParams);
                    material.SetVector("_Vanilla", VanillaParams);
                    material.SetTexture("_RippleTex", TexGame.RippleTex);
                    material.SetTexture("_NoiseTex", TexGame.NoiseTex);
                    material.SetTexture("_WaterReflectionTex", ContentFinder<Texture2D>.Get("Other/WaterReflection", reportFailure: false) ?? BaseContent.GreyTex);
                    material.SetTexture("_RampTex", VanillaDeepRamp ?? BaseContent.GreyTex);
                }
                return material;
            }
        }

        /// <summary>柱身材質（每種柱身貼圖一份）：柱身依高度自己算平台的影子。</summary>
        public Material PillarMaterial(SeaPlatformPillarExtension pillar)
        {
            SeaPlatformMapExtension ext = Ext;
            if (ext == null || pillar == null || pillar.pillarTexPath.NullOrEmpty())
            {
                return null;
            }
            if (!pillarMaterials.TryGetValue(pillar.pillarTexPath, out Material mat))
            {
                Texture2D tex = ContentFinder<Texture2D>.Get(pillar.pillarTexPath, reportFailure: false);
                Shader shader = tex == null ? null : ShaderFor("DMSE_SeaPlatformPillar");
                if (shader != null)
                {
                    mat = new Material(shader)
                    {
                        name = "DMSE_SeaPlatformPillar_" + map.uniqueID,
                        mainTexture = tex,
                    };
                    mat.SetColor("_Color", Color.white);
                    ApplyShadowParams(mat, ext);
                    // 入水處在貼圖從下往上的幾成、貼圖高幾格（片段的高度 = (v − x) × y）
                    mat.SetVector("_Pillar", new Vector4(1f - SeaPlatformGeometry.WaterlineFraction, pillar.pillarDrawSize.y, 0f, 0f));
                }
                pillarMaterials[pillar.pillarTexPath] = mat;
            }
            return mat;
        }

        private static Shader ShaderFor(string defName)
        {
            Shader shader = DefDatabase<ShaderTypeDef>.GetNamedSilentFail(defName)?.Shader;
            if (shader == null)
            {
                Log.ErrorOnce($"[DMSE] ShaderTypeDef {defName} is missing; the sea under the platform will not be drawn.", defName.GetHashCode());
            }
            return shader;
        }

        /// <summary>原版深海水（WaterOceanDeep）的光照色階貼圖。</summary>
        private static Texture2D VanillaDeepRamp
        {
            get
            {
                string path = DefDatabase<TerrainDef>.GetNamedSilentFail("WaterOceanDeep")?.texturePath ?? "Terrain/Surfaces/WaterDeepRamp";
                return ContentFinder<Texture2D>.Get(path, reportFailure: false);
            }
        }

        /// <summary>
        /// 海面與柱身共用的參數：地圖大小；_Height = (看下去的落差, 甲板高, 影子濃度倍率, 0)；
        /// _Deck = (0, 額外柔化, 陽光投影最暗, 天光遮蔽最暗)；遮罩；目前的太陽。
        /// </summary>
        private void ApplyShadowParams(Material mat, SeaPlatformMapExtension ext)
        {
            mat.SetVector("_MapSize", new Vector4(map.Size.x, map.Size.z, 0f, 0f));
            mat.SetVector("_Height", new Vector4(ext.seaDrop, ext.seaDrop, SeaPlatformShadow.Fade, 0f));
            mat.SetVector("_Deck", new Vector4(0f, SeaPlatformShadow.ExtraSoftness, SeaPlatformShadow.CastDarkness, SeaPlatformShadow.SkyDarkness));
            mat.SetTexture("_MaskTex", Mask);
            mat.SetVector("_Cast", SeaPlatformShadow.Cast(map, ext.seaDrop));
        }

        private Vector4 SwayVector()
        {
            return new Vector4(SeaPlatformSway.BaseAmplitude * swayFactor, SeaPlatformSway.VerticalShare, 0f, 0f);
        }

        public override void MapComponentUpdate()
        {
            if (Find.CurrentMap != map || (material == null && pillarMaterials.Count == 0))
            {
                return;
            }
            SeaPlatformMapExtension ext = Ext;
            if (ext == null)
            {
                return;
            }
            bool rebuilt = false;
            if (maskStale && Time.realtimeSinceStartup >= nextMaskRebuild)
            {
                RebuildMask();
                rebuilt = true;
            }
            swayFactor = Mathf.MoveTowards(swayFactor, SeaPlatformSway.WeatherFactor(map.windManager?.WindSpeed ?? 0f), SeaPlatformSway.ChangePerSecond * Time.deltaTime);
            Vector4 cast = SeaPlatformShadow.Cast(map, ext.seaDrop);
            void Apply(Material mat)
            {
                if (mat == null)
                {
                    return;
                }
                mat.SetVector("_Cast", cast);
                if (rebuilt)
                {
                    mat.SetTexture("_MaskTex", mask);
                }
            }
            if (material != null)
            {
                material.SetVector("_Sway", SwayVector());
                Apply(material);
            }
            foreach (Material mat in pillarMaterials.Values)
            {
                Apply(mat);
            }
        }

        /// <summary>立刻重建遮罩：r = 甲板（σ 0.9）、g = 支柱入水處（σ 0.8）、a = 甲板（σ 3，天光遮蔽）。</summary>
        public void RebuildMask()
        {
            SeaPlatformMapExtension ext = Ext;
            int w = map.Size.x;
            int h = map.Size.z;
            float[] deck = new float[w * h];
            float[] pillar = new float[w * h];
            if (ext != null)
            {
                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        TerrainDef t = map.terrainGrid.TerrainAt(new IntVec3(x, 0, z));
                        deck[z * w + x] = t != null && t != ext.voidTerrain ? 1f : 0f;
                    }
                }
                foreach (Building_SeaPlatformPillar b in map.GetComponent<MapComponent_SeaPlatformSupport>()?.Pillars ?? new List<Building_SeaPlatformPillar>())
                {
                    if (!b.Spawned)
                    {
                        continue;
                    }
                    foreach (IntVec3 c in SeaPlatformGeometry.WaterlineCells(b.Position, b.Ext.waterlineWidth))
                    {
                        if (c.InBounds(map))
                        {
                            pillar[c.z * w + c.x] = 1f;
                        }
                    }
                }
            }
            float[] r = SeaPlatformShadow.Blur(deck, w, h, SeaPlatformShadow.DeckSigma);
            float[] a = SeaPlatformShadow.Blur(deck, w, h, SeaPlatformShadow.SkySigma);
            float[] g = SeaPlatformShadow.Blur(pillar, w, h, SeaPlatformShadow.PillarSigma);
            Color32[] pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(SeaPlatformShadow.ToByte(r[i]), SeaPlatformShadow.ToByte(g[i]), 0, SeaPlatformShadow.ToByte(a[i]));
            }
            if (mask == null || mask.width != w || mask.height != h)
            {
                DestroyMask();
                mask = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "DMSE_SeaPlatformMask_" + map.uniqueID,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            mask.SetPixels32(pixels);
            mask.Apply(false);
            maskStale = false;
            nextMaskRebuild = Time.realtimeSinceStartup + TextureRebuildSeconds;
        }

        private void DestroyMask()
        {
            if (mask != null)
            {
                Object.Destroy(mask);
                mask = null;
            }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            DestroyMask();
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
            foreach (Material mat in pillarMaterials.Values)
            {
                if (mat != null)
                {
                    Object.Destroy(mat);
                }
            }
            pillarMaterials.Clear();
        }
    }

    /// <summary>
    /// 海面背景層：只在高空格輸出四邊形（地形本身 dontRender）。著色器排在 1900，比地形（2000）早；
    /// 柱身（1950）蓋在它上面，平台與平台側面再蓋在柱身上面。平台影子在同一個著色器裡算。
    /// </summary>
    public class SectionLayer_SeaPlatformSea : SectionLayer
    {
        public SectionLayer_SeaPlatformSea(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings;
        }

        public override bool Visible => true;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            SeaPlatformMapExtension ext = SeaPlatformUtility.Ext(map);
            MapComponent_SeaPlatformSea comp = ext == null ? null : map.GetComponent<MapComponent_SeaPlatformSea>();
            if (comp == null)
            {
                return;
            }
            // 地形或支柱變了：遮罩要重建（節流）
            comp.MarkDirty();
            bool any = false;
            foreach (IntVec3 c in section.CellRect)
            {
                if (SeaPlatformUtility.ShowsSea(c, map, ext))
                {
                    any = true;
                    break;
                }
            }
            Material mat = any ? comp.Material : null;
            if (mat == null)
            {
                return;
            }
            LayerSubMesh sm = GetSubMesh(mat);
            float y = AltitudeLayer.BelowTerrain.AltitudeFor();
            foreach (IntVec3 c in section.CellRect)
            {
                if (!SeaPlatformUtility.ShowsSea(c, map, ext))
                {
                    continue;
                }
                int start = sm.verts.Count;
                sm.verts.Add(new Vector3(c.x, y, c.z));
                sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);
            }
            FinalizeMesh(MeshParts.All);
        }
    }

    /// <summary>
    /// 柱身層：每個區塊只畫柱身落在自己範圍內的那一段（貼圖座標跟著裁），排在 1950：海面之上、甲板（2000）與平台側面之下。
    /// 支柱生成或移除時，柱身蓋到的區塊都會標記重建。
    /// </summary>
    public class SectionLayer_SeaPlatformPillars : SectionLayer
    {
        public SectionLayer_SeaPlatformPillars(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Buildings;
        }

        public override bool Visible => DebugViewSettings.drawThingsPrinted;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = Map;
            if (!SeaPlatformUtility.IsSeaPlatformMap(map))
            {
                return;
            }
            MapComponent_SeaPlatformSea comp = map.GetComponent<MapComponent_SeaPlatformSea>();
            MapComponent_SeaPlatformSupport support = map.GetComponent<MapComponent_SeaPlatformSupport>();
            if (comp == null || support == null || support.Pillars.Count == 0)
            {
                return;
            }
            CellRect cr = section.CellRect;
            Rect sectionRect = Rect.MinMaxRect(cr.minX, cr.minZ, cr.maxX + 1, cr.maxZ + 1);
            float y = AltitudeLayer.Terrain.AltitudeFor();
            foreach (Building_SeaPlatformPillar b in support.Pillars)
            {
                if (!b.Spawned || !SeaPlatformGeometry.Clip(b.PillarRect, sectionRect, out Rect quad, out Rect uv))
                {
                    continue;
                }
                Material mat = comp.PillarMaterial(b.Ext);
                if (mat == null)
                {
                    continue;
                }
                LayerSubMesh sm = GetSubMesh(mat);
                int start = sm.verts.Count;
                sm.verts.Add(new Vector3(quad.xMin, y, quad.yMin));
                sm.verts.Add(new Vector3(quad.xMin, y, quad.yMax));
                sm.verts.Add(new Vector3(quad.xMax, y, quad.yMax));
                sm.verts.Add(new Vector3(quad.xMax, y, quad.yMin));
                sm.uvs.Add(new Vector3(uv.xMin, uv.yMin, 0f));
                sm.uvs.Add(new Vector3(uv.xMin, uv.yMax, 0f));
                sm.uvs.Add(new Vector3(uv.xMax, uv.yMax, 0f));
                sm.uvs.Add(new Vector3(uv.xMax, uv.yMin, 0f));
                for (int i = 0; i < 4; i++)
                {
                    sm.colors.Add(new Color32(255, 255, 255, 255));
                }
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
