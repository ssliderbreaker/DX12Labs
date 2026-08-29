cbuffer ConstantBuffer : register(b0)
{
    float4x4 ViewProj;
};

struct InstanceData
{
    row_major float4x4 World;
    row_major float4x4 WorldInvTranspose;
};
StructuredBuffer<InstanceData> gInstances : register(t1);

Texture2D gDiffuseMap : register(t0);
SamplerState gSampler : register(s0);

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

struct VS_OUT
{
    float4 Position : SV_POSITION;
    float3 PosWorld : POSITION1;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

VS_OUT VSMain(VertexIn vin, uint instanceID : SV_InstanceID)
{
    InstanceData inst = gInstances[instanceID];

    float4 worldPos = mul(float4(vin.Position, 1.0f), inst.World);

    VS_OUT vout;
    vout.PosWorld = worldPos.xyz;
    vout.Position = mul(worldPos, ViewProj);
    vout.Normal = mul(vin.Normal, (float3x3) inst.WorldInvTranspose);
    vout.TexCoord = vin.TexCoord;
    return vout;
}

struct PSOutput
{
    float4 Position : SV_TARGET0;
    float4 Normal : SV_TARGET1;
    float4 Albedo : SV_TARGET2;
};

PSOutput PSMain(VS_OUT pin)
{
    PSOutput output;
    output.Position = float4(pin.PosWorld, 1.0f);
    output.Normal = float4(normalize(pin.Normal), 1.0f);
    output.Albedo = gDiffuseMap.Sample(gSampler, pin.TexCoord);
    return output;
}