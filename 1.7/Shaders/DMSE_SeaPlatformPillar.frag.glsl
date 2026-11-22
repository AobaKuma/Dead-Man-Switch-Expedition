#version 450

// DMSE 海上平台的柱身（ShaderDef DMSE_SeaPlatformPillar，1950；2026-11-23 從 DepthRim_Pillar.frag.glsl 複製）。
// 頂點著色器用 DMSE_WorldPos.vert.glsl（貼圖 UV ＋ 世界座標 XZ）。材質每張地圖一份（DMSE.MapComponent_SeaPlatformSea.PillarMaterial）。
//
// 柱身依每個片段離海面的高度自己算平台的影子（2026-11-15）：貼圖從下往上，入水處在 _Pillar.x，往上每一格貼圖高度就是離海面高一格；
// 斜俯視裡越高的東西畫得越北，所以這個片段正下方的海面點 = 畫面座標往南（高度）格（平台層再加看下去的落差 _Height.x）。
// 從那裡往太陽看，碰到甲板的水平距離是影子位移 ×（1 − 高度 / 甲板高）：靠海面的一段在甲板的影子裡，靠甲板的一段要看太陽角度；
// 高過甲板的柱頂不受影響。天光遮蔽看柱腳正上方有沒有甲板。頂點色 a = 柱腳的燈光照亮影子的程度。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MapSize;    // @default = vec4(250.0, 250.0, 0.0, 0.0)   地圖大小（格）
    vec4 _Height;     // @default = vec4(14.1, 14.1, 1.0, 0.0)     x = 看下去海面往南偏幾格，y = 甲板離海面幾格，z = 影子濃度倍率（遠近）
    vec4 _Cast;       // @default = vec4(0.0, -1.75, 1.0, 0.0)     xy = 甲板影子的位移（格），z = 影子強度
    vec4 _Deck;       // @default = vec4(0.0, 0.0, 0.55, 0.3)      y = 額外柔化，z = 陽光投影最暗，w = 天光遮蔽最暗
    vec4 _Pillar;     // @default = vec4(0.14, 18.333, 0.0, 0.0)   x = 入水處在貼圖從下往上的幾成，y = 貼圖高幾格
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black

// ---- platformShade begin（DMSE_SeaPlatformSea.frag.glsl 有一模一樣的一段）----
const int PILLAR_TAPS = 8;
// 支柱是圓柱：遮罩 g 只有入水處（前緣兩排），影子要用整個斷面算，所以沿柱子往北（往後）再取樣到 PILLAR_DEPTH 格
const int PILLAR_DEPTH_TAPS = 5;
const float PILLAR_DEPTH = 7.0;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

// 遮罩（世界座標 XZ，格；r = 甲板、g = 支柱入水處、a = 甲板大半徑模糊）；地圖外一律 0
vec4 maskAt(vec2 world)
{
    vec2 uv = world / max(u._MapSize.xy, vec2(1.0));
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
        return vec4(0.0);
    return texture(_MaskTex, flipV(uv));
}

// 海面座標點 sea（高出海面 heightAbove 格）被遮住的程度：x = 陽光，y = 天光
vec2 platformShade(vec2 sea, float heightAbove)
{
    float deckHeight = max(u._Height.y, 0.001);
    if (heightAbove >= deckHeight)
        return vec2(0.0);
    float reach = 1.0 - heightAbove / deckHeight;
    vec2 offset = u._Cast.xy * reach;
    float soft = clamp(length(offset) / 16.0 * 0.7 + u._Deck.y, 0.0, 1.0);
    vec4 m = maskAt(sea - offset);
    float sun = smoothstep(0.05, 0.85, mix(m.r, m.a, soft));
    if (heightAbove <= 0.0)
    {
        float column = 0.0;
        for (int i = 1; i <= PILLAR_TAPS; i++)
        {
            vec2 along = sea - u._Cast.xy * (float(i) / float(PILLAR_TAPS));
            for (int j = 0; j < PILLAR_DEPTH_TAPS; j++)
                column = max(column, maskAt(along - vec2(0.0, PILLAR_DEPTH * float(j) / float(PILLAR_DEPTH_TAPS - 1))).g);
        }

        sun = max(sun, smoothstep(0.35, 0.6, column));
    }
    return vec2(sun, smoothstep(0.1, 0.9, maskAt(sea).a));
}

// 兩種暗疊起來的濃度
float platformDarkness(vec2 shade)
{
    return 1.0 - (1.0 - u._Deck.z * u._Cast.z * shade.x) * (1.0 - u._Deck.w * shade.y);
}
// ---- platformShade end ----

void main()
{
    // 區塊網格的 UV 已經是裁過的貼圖座標（v = 0 在柱身南端）。不套 _MainTex_ST（ShaderDef 沒設 flipTextureST，引擎會換掉它的排列）；
    // 引擎的貼圖 v 從上往下，所以翻過來取樣
    vec4 tex = texture(_MainTex, flipV(vUv)) * u._Color;
    // 離海面的高度（格）；入水處以下（水裡的混凝土）當成貼著海面，但不算柱子自己的影子
    float heightAbove = max((vUv.y - u._Pillar.x) * u._Pillar.y, 0.001);
    vec2 sea = vec2(vWorld.x, vWorld.y + u._Height.x - heightAbove);
    float dark = platformDarkness(platformShade(sea, heightAbove)) * vColor.a * u._Height.z;
    outColor = vec4(tex.rgb * (1.0 - dark), tex.a);
}
