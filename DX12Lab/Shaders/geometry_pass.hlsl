cbuffer ConstantBuffer : register(b0)
{
    float4x4 WorldViewProj;
    float4x4 World;
};

Texture2D gDiffuseMap : register(t0);
SamplerState gSampler : register(s0);

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

struct VertexOut
{
    float4 Position : SV_POSITION;
    float3 WorldPos : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

VertexOut VSMain(VertexIn vin)
{
    VertexOut vout;
    vout.Position = mul(float4(vin.Position, 1.0f), WorldViewProj);
    vout.WorldPos = mul(float4(vin.Position, 1.0f), World).xyz;
    vout.Normal = mul(vin.Normal, (float3x3) World);
    vout.TexCoord = vin.TexCoord;
    return vout;
}

struct PSOutput
{
    float4 Position : SV_TARGET0;
    float4 Normal : SV_TARGET1;
    float4 Albedo : SV_TARGET2;
};

PSOutput PSMain(VertexOut pin)
{
    PSOutput output;
    output.Position = float4(pin.WorldPos, 1.0f);
    output.Normal = float4(normalize(pin.Normal), 1.0f);
    output.Albedo = gDiffuseMap.Sample(gSampler, pin.TexCoord);
    return output;
}