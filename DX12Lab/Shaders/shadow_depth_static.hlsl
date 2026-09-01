cbuffer RootConstants : register(b0)
{
    float4x4 LightViewProj;
};

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

float4 VSMain(VertexIn vin) : SV_POSITION
{
    return mul(float4(vin.Position, 1.0f), LightViewProj);
}