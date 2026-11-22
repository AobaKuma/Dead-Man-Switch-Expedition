#version 450

// DMSE 世界座標頂點著色器（2026-11-23 從 DepthRim_Midwater.vert.glsl 複製）。
// 海上平台的柱身（DMSE_SeaPlatformPillar.frag.glsl）用：區塊網格是世界座標。
// 除了貼圖 UV，還把世界座標 XZ 傳給片段著色器，用來查平台遮罩。

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0;      // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vWorld;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vColor = inColor;
    vUv = inUv0.xy;
    vWorld = world.xz;
}
