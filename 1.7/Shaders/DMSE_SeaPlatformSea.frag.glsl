#version 450

// DMSE 海上平台的海面背景層（DMSE.SectionLayer_SeaPlatformSea）：畫在高空格上（類虛空地形，本身不畫）。
// 從 DepthRim 的 DepthRim_SeaSurface.frag.glsl 簡化（2026-11-23）：沒有浮船（遮罩 b）、航跡、海況；
// 平台的影子（天光遮蔽、陽光投影、柱影）直接在這裡算，不另外蓋暗化層。
// 頂點著色器沿用原版 MapWorldUV.vert.glsl（vUv = 世界座標 XZ）。
//
// 海面在甲板下方 _Height.x 格：地圖平面上的點 w 看到的海面座標 S = w + (0, _Height.x)（斜俯視，越低的東西畫得越南）。
// 遮罩 _MaskTex 每格一個像素，C# 先模糊好：r = 甲板，g = 支柱入水處，a = 甲板（大半徑模糊，天光遮蔽）。
// 海水本身照原版深海（TerrainWater ＋ WaterDepth）的作法畫；晝夜明暗交給原版的光照暗化層。

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(0.055, 0.215, 0.300, 1.0)  海水底色
    vec4 _DeepColor;      // @color @default = vec4(0.030, 0.120, 0.190, 1.0)  大片的深色斑塊
    vec4 _CrestColor;     // @color @default = vec4(0.300, 0.520, 0.600, 1.0)  浪峰的亮面
    vec4 _FoamColor;      // @color @default = vec4(0.850, 0.920, 0.940, 1.0)  白沫
    vec4 _MapSize;        // @default = vec4(250.0, 250.0, 0.0, 0.0)          地圖大小（格）
    vec4 _Height;         // @default = vec4(14.1, 14.1, 0.65, 0.0)           x = 海面往南偏幾格，y = 甲板離海面幾格，z = 影子濃度倍率
    vec4 _Cast;           // @default = vec4(0.0, -1.75, 1.0, 0.0)            太陽（每幀）：xy = 甲板影子的位移（格），z = 影子強度
    vec4 _Deck;           // @default = vec4(0.0, 0.35, 0.55, 0.3)            y = 額外柔化，z = 陽光投影最暗，w = 天光遮蔽最暗
    vec4 _Sway;           // @default = vec4(0.6, 0.35, 0.0, 0.0)             海水擺動：x = 左右幅度（格，已乘天氣倍率），y = 上下佔左右的比例
    float _GameSeconds;   // 動畫時鐘；全域
    float _WaveSpeed;     // @default = 1.0    浪的速度倍率
    float _FoamAmount;    // @default = 1.0    支柱腳下白沫的強度（0 = 沒有）
    float _GlintAmount;   // @default = 0.0    陽光反光點的強度
    vec4 _Vanilla;        // @default = vec4(1.0, 0.0, 1.0, 1.0)            x = 原版深海配色的比重，y = 浪峰線強度，z = 漣漪密度，w = 漣漪強度
    vec4 _FoamPhase;      // @default = vec4(12.0, 5.7, 0.01, 0.006)       浪花擺動的相位場
    vec4 _WaterCastVectSun;               // 全域（SkyManager）
    vec4 _WaterCastVectMoon;              // 全域（SkyManager）
    float _DayPercent;                    // 全域（SkyManager）
    float _LightsourceShineSizeReduction; // 全域（SkyManager）
    float _LightsourceShineIntensity;     // 全域（SkyManager）
} u;

layout(set = 2, binding = 1) uniform sampler2D _MaskTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _RippleTex;          // 原版漣漪（TexGame.RippleTex）
layout(set = 2, binding = 3) uniform sampler2D _RampTex;            // 原版深海的光照色階（WaterOceanDeep 的貼圖）
layout(set = 2, binding = 4) uniform sampler2D _WaterReflectionTex; // 原版雲的倒影（Other/WaterReflection）
layout(set = 2, binding = 5) uniform sampler2D _NoiseTex;           // 原版表面雜訊（TexGame.NoiseTex）

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec4 maskAt(vec2 world)
{
    vec2 uv = world / max(u._MapSize.xy, vec2(1.0));
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0)
        return vec4(0.0);
    return texture(_MaskTex, flipV(uv));
}

float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 w = f * f * (3.0 - 2.0 * f);
    float a = hash12(i);
    float b = hash12(i + vec2(1.0, 0.0));
    float c = hash12(i + vec2(0.0, 1.0));
    float d = hash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, w.x), mix(c, d, w.x), w.y);
}

const mat2 OCTAVE_ROT = mat2(0.8, 0.6, -0.6, 0.8);

float fbm(vec2 p)
{
    float v = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += valueNoise(p) * amp;
        p = OCTAVE_ROT * p * 2.03 + vec2(17.1, 9.2);
        amp *= 0.5;
    }
    return v;
}

float ridge(vec2 p)
{
    return 1.0 - abs(2.0 * valueNoise(p) - 1.0);
}

// ── 原版深海（WaterDepth.frag.glsl 的高度場、TerrainWater.frag.glsl 的打光）──
const float WaterMovementSpeed = 0.00055;

float rippleLayer(vec2 originUv, vec2 phaseOffsets, float rotate, float t)
{
    mat2 rot = mat2(cos(rotate), sin(rotate), -sin(rotate), cos(rotate));
    vec2 uvRotated = rot * originUv + t * phaseOffsets * WaterMovementSpeed;
    return textureLod(_RippleTex, uvRotated * u._Vanilla.z * 15.0, 0.0).r;
}

float rippleHeight(vec2 world, float t)
{
    vec2 origin = world / max(u._MapSize.xy, vec2(1.0));
    float a = rippleLayer(origin, vec2(0.0, 5.0), 0.3, t);
    float b = rippleLayer(origin, vec2(2.1, 3.333), -0.35, t);
    float c = rippleLayer(origin, vec2(-2.3, 1.877), -0.8, t);
    float d = rippleLayer(origin, vec2(3.312, 0.444), 0.65, t);
    return (a * b * c * d - 0.0625) * u._Vanilla.w;
}

float waterDiffuseIntensity(vec3 normal, vec3 lightDirection)
{
    float current = dot(-lightDirection, normal);
    float vertical = dot(-lightDirection, vec3(0.0, 0.0, 1.0));
    if (current < vertical)
        return pow(max(current / vertical, 0.0), 100.0) * 0.5;
    return (1.0 - pow(1.0 - (current - vertical) / (1.0 - vertical), 5.0)) * 0.5 + 0.5;
}

float waterSpecularIntensity(vec3 normal, vec3 lightDirection, vec3 viewDirection)
{
    return pow(max(0.0, dot(reflect(-lightDirection, normal), viewDirection)), max(u._LightsourceShineSizeReduction, 1.0)) * u._LightsourceShineIntensity;
}

vec3 rampColor(float diffuse, float specular)
{
    return texture(_RampTex, vec2(clamp(diffuse, 0.0, 1.0), 1.0 - clamp(specular, 0.0, 1.0))).rgb;
}

vec3 vanillaDeepOcean(vec2 world, float t)
{
    const float e = 0.12;
    float h0 = rippleHeight(world, t);
    float hx = rippleHeight(world + vec2(e, 0.0), t);
    float hz = rippleHeight(world + vec2(0.0, e), t);
    float k = 0.064 / e;
    vec3 normal = normalize(vec3((hx - h0) * k, -(hz - h0) * k, 1.0));

    vec3 viewDirection = vec3(0.0, 0.0, -1.0);
    vec3 castSun = normalize(vec3(-u._WaterCastVectSun.x, -u._WaterCastVectSun.z, -50.0));
    vec3 castMoon = normalize(vec3(-u._WaterCastVectMoon.x, -u._WaterCastVectMoon.z, -50.0));
    vec3 sunCol = rampColor(waterDiffuseIntensity(normal, castSun), waterSpecularIntensity(normal, castSun, viewDirection));
    vec3 moonCol = rampColor(waterDiffuseIntensity(normal, castMoon), waterSpecularIntensity(normal, castMoon, viewDirection));
    float moonW = u._WaterCastVectMoon.w;
    float sunW = u._WaterCastVectSun.w;
    vec3 lit = mix(sunCol, moonCol, moonW / max(moonW + sunW, 1e-4));
    float sunAmount = clamp(length(vec2(moonW, sunW)), 0.0, 1.0);
    vec3 col = mix(rampColor(0.0, 0.0), lit, sunAmount);

    vec2 uv = world / 16.0;
    float adjustedDay = 2.0 * mix(u._DayPercent, 1.0 - u._DayPercent, step(u._DayPercent, 0.5));
    float reflectionIntensity = mix(0.06, 0.04, adjustedDay);
    vec2 reflectionUV = (mix(uv / 16.0, world / 70.0, 0.2) + t * 0.003) * 3.0 + normal.xz * 10.0;
    col += (1.0 - 2.0 * texture(_WaterReflectionTex, flipV(reflectionUV)).rgb) * reflectionIntensity;
    col = mix(col, col + texture(_NoiseTex, flipV(uv)).rgb - vec3(0.5), 0.025);
    return col;
}

// 海水的擺動（和 C# 的 DMSE.SeaPlatformSway 同一組式子）
vec2 swayOffset(float t)
{
    float x = sin(0.55 * t) + 0.3 * sin(1.37 * t + 1.3);
    float z = u._Sway.y * (sin(0.41 * t + 0.8) + 0.4 * sin(1.11 * t + 2.1));
    return vec2(x, z) * u._Sway.x;
}

vec2 swayVelocity(float t)
{
    float x = 0.55 * cos(0.55 * t) + 0.3 * 1.37 * cos(1.37 * t + 1.3);
    float z = u._Sway.y * (0.41 * cos(0.41 * t + 0.8) + 0.4 * 1.11 * cos(1.11 * t + 2.1));
    return vec2(x, z) * u._Sway.x;
}

float noiseMap(vec2 p)
{
    ivec2 size = max(textureSize(_NoiseTex, 0), ivec2(1));
    vec2 i = floor(p);
    vec2 f = p - i;
    vec2 w = f * f * (3.0 - 2.0 * f);
    ivec2 a = ivec2(i);
    ivec2 a0 = ((a % size) + size) % size;
    ivec2 a1 = (((a + 1) % size) + size) % size;
    float v00 = texelFetch(_NoiseTex, ivec2(a0.x, a0.y), 0).r;
    float v10 = texelFetch(_NoiseTex, ivec2(a1.x, a0.y), 0).r;
    float v01 = texelFetch(_NoiseTex, ivec2(a0.x, a1.y), 0).r;
    float v11 = texelFetch(_NoiseTex, ivec2(a1.x, a1.y), 0).r;
    return mix(mix(v00, v10, w.x), mix(v01, v11, w.x), w.y);
}

float foamPhase(vec2 world, float t)
{
    vec2 p = world / max(u._FoamPhase.x, 0.001);
    vec2 scroll = u._FoamPhase.zw * t;
    float n1 = noiseMap(p + scroll);
    float n2 = noiseMap(p * 1.9 + vec2(37.0, 11.0) + vec2(-scroll.y, scroll.x) * 1.3);
    return (n1 * 0.65 + n2 * 0.35) * u._FoamPhase.y;
}

// ---- platformShade begin（DMSE_SeaPlatformPillar.frag.glsl 有一模一樣的一段）----
const int PILLAR_TAPS = 8;
const int PILLAR_DEPTH_TAPS = 5;
const float PILLAR_DEPTH = 7.0;

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

float platformDarkness(vec2 shade)
{
    return 1.0 - (1.0 - u._Deck.z * u._Cast.z * shade.x) * (1.0 - u._Deck.w * shade.y);
}
// ---- platformShade end ----

void main()
{
    vec2 world = vUv + vec2(0.0, u._Height.x);
    float t = u._GameSeconds * u._WaveSpeed;

    float patches = fbm(world * 0.035 + vec2(t * 0.003, -t * 0.002));
    vec3 col = mix(u._DeepColor.rgb, u._Color.rgb, smoothstep(0.25, 0.75, patches));

    vec2 sway = swayOffset(t);
    vec2 waterUv = world - sway - vec2(t * 0.06, 0.0);

    vec2 wind = normalize(vec2(1.0, 0.18));
    vec2 side = vec2(-wind.y, wind.x);
    vec2 q = vec2(dot(waterUv, wind) * 1.5, dot(waterUv, side) * 0.55);
    vec2 warp = vec2(fbm(q * 0.35 + vec2(t * 0.04, 0.0)), fbm(q * 0.35 + vec2(5.2, 1.3 - t * 0.03)));
    float r1 = ridge(q + warp * 1.8);
    float r2 = ridge(q * 2.3 + warp * 1.2 + vec2(13.0, 7.0) - vec2(sway.x * 0.8, 0.0));
    float crest = pow(r1, 5.0) * 0.6 + pow(r2, 6.0) * 0.4;
    col = mix(col, vanillaDeepOcean(waterUv, t), u._Vanilla.x);
    col = mix(col, u._CrestColor.rgb, crest * u._Vanilla.y);

    // 陽光反光點：浪峰上稀疏、閃爍的亮點；平台擋住太陽的地方沒有
    float sunBlocked = smoothstep(0.05, 0.85, maskAt(world - u._Cast.xy).r);
    float sunOn = clamp(u._Cast.z, 0.0, 1.0) * (1.0 - sunBlocked);
    vec2 g = waterUv * 4.0 + vec2(t * 0.05, 0.0);
    vec2 gid = floor(g);
    float gr = hash12(gid);
    float twinkle = 0.5 + 0.5 * sin(t * (2.0 + 3.0 * hash12(gid + 3.1)) + gr * 40.0);
    float spot = 1.0 - smoothstep(0.03, 0.10, length(fract(g) - 0.5));
    float glint = step(0.95, gr) * spot * twinkle * smoothstep(0.2, 0.6, crest) * sunOn;
    col += vec3(0.9, 0.95, 1.0) * glint * 0.7 * u._GlintAmount;

    // 支柱腳下的白沫與拖在水流下游的尾流（浪花用各處自己的擺動相位）
    float foamT = t + foamPhase(world, t);
    vec2 flow = swayVelocity(foamT);
    vec2 foamUv = world - swayOffset(foamT) - vec2(t * 0.06, 0.0);
    float flowSpeed = length(flow);
    vec2 downstream = flowSpeed > 0.001 ? flow / flowSpeed : vec2(1.0, 0.0);
    float flowShare = clamp(flowSpeed / max(u._Sway.x * 0.9, 0.001), 0.0, 1.0);
    float wakeLength = 1.0 + 2.2 * flowShare;
    float pillarNear = maskAt(world).g;
    float pillarWake = maskAt(world - downstream * wakeLength).g;
    float foamNoise = fbm(foamUv * 3.0 + vec2(t * 0.05, t * 0.04)) + crest * 0.2;
    float ring = smoothstep(0.02, 0.30, pillarNear);
    float foam = smoothstep(0.75 - 0.35 * ring, 0.90 - 0.35 * ring, foamNoise) * ring;
    foam = max(foam, smoothstep(0.70, 0.85, foamNoise) * smoothstep(0.02, 0.25, pillarWake) * 0.6);
    col = mix(col, u._FoamColor.rgb, clamp(foam * u._FoamAmount, 0.0, 1.0) * 0.8);

    // 平台的影子（天光遮蔽、陽光投影、柱影）
    float dark = platformDarkness(platformShade(world, 0.0)) * u._Height.z;
    col *= 1.0 - dark;

    outColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
