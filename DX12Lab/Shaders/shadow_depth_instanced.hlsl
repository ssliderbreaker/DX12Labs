cbuffer RootConstants : register(b0)
{
    float4x4 LightViewProj;
};

struct InstanceData
{
    row_major float4x4 World;
    row_major float4x4 WorldInvTranspose;
};
StructuredBuffer<InstanceData> gInstances : register(t0);

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

float4 VSMain(VertexIn vin, uint instanceID : SV_InstanceID) : SV_POSITION
{
    InstanceData inst = gInstances[instanceID];
    float4 worldPos = mul(float4(vin.Position, 1.0f), inst.World);
    return mul(worldPos, LightViewProj);
}