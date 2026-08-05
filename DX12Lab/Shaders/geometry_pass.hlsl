cbuffer ConstantBuffer : register(b0)
{
    float4x4 WorldViewProj;
    float4x4 World;
    float4x4 WorldInvTranspose;
    float3 CameraPos;
    float DisplacementScale;
    float TessMin;
    float TessMax;
    float TessNearDist;
    float TessFarDist;
};

Texture2D gDiffuseMap : register(t0);
Texture2D gDisplacementMap : register(t1); 
Texture2D gNormalMap : register(t2); 
SamplerState gSampler : register(s0);

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

struct VS_OUT
{
    float3 PosWorld : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

VS_OUT VSMain(VertexIn vin)
{
    VS_OUT vout;
    vout.PosWorld = mul(float4(vin.Position, 1.0f), World).xyz;
    vout.Normal = mul(vin.Normal, (float3x3) WorldInvTranspose);
    vout.TexCoord = vin.TexCoord;
    return vout;
}

struct PatchTess
{
    float EdgeTess[3] : SV_TessFactor;
    float InsideTess[1] : SV_InsideTessFactor;
};

float CalcTessFactor(float3 worldPos)
{
    float d = distance(worldPos, CameraPos);
    float t = saturate((d - TessNearDist) / (TessFarDist - TessNearDist));
    return lerp(TessMax, TessMin, t);
}

PatchTess ConstantHS(InputPatch<VS_OUT, 3> patch, uint patchID : SV_PrimitiveID)
{
    PatchTess pt;
    
    float3 e0 = 0.5f * (patch[1].PosWorld + patch[2].PosWorld);
    float3 e1 = 0.5f * (patch[0].PosWorld + patch[2].PosWorld);
    float3 e2 = 0.5f * (patch[0].PosWorld + patch[1].PosWorld);
    float3 c = (patch[0].PosWorld + patch[1].PosWorld + patch[2].PosWorld) / 3.0f;

    pt.EdgeTess[0] = CalcTessFactor(e0);
    pt.EdgeTess[1] = CalcTessFactor(e1);
    pt.EdgeTess[2] = CalcTessFactor(e2);
    pt.InsideTess[0] = CalcTessFactor(c);

    return pt;
}

[domain("tri")]
[partitioning("fractional_odd")]
[outputtopology("triangle_cw")]
[outputcontrolpoints(3)]
[patchconstantfunc("ConstantHS")]
[maxtessfactor(64.0f)]
VS_OUT HSMain(
    InputPatch<VS_OUT, 3> patch,
    uint i : SV_OutputControlPointID,
    uint patchID : SV_PrimitiveID)
{
    return patch[i];
}

struct DS_OUT
{
    float4 Position : SV_POSITION;
    float3 PosWorld : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

[domain("tri")]
DS_OUT DSMain(
    PatchTess patchTess,
    float3 bary : SV_DomainLocation,
    const OutputPatch<VS_OUT, 3> patch)
{
    DS_OUT dout;
    
    float3 pos = bary.x * patch[0].PosWorld
                    + bary.y * patch[1].PosWorld
                    + bary.z * patch[2].PosWorld;

    float3 normal = normalize(
                      bary.x * patch[0].Normal
                    + bary.y * patch[1].Normal
                    + bary.z * patch[2].Normal);

    float2 texCoord = bary.x * patch[0].TexCoord
                    + bary.y * patch[1].TexCoord
                    + bary.z * patch[2].TexCoord;
    
    float h = gDisplacementMap.SampleLevel(gSampler, texCoord, 0).r;
    h = h * 0.5f; 
    pos += normal * (h * DisplacementScale);

    dout.Position = mul(float4(pos, 1.0f), WorldViewProj);
    dout.PosWorld = pos;
    dout.Normal = normal;
    dout.TexCoord = texCoord;

    return dout;
}

struct PSOutput
{
    float4 Position : SV_TARGET0;
    float4 Normal : SV_TARGET1;
    float4 Albedo : SV_TARGET2;
};

PSOutput PSMain(DS_OUT pin)
{
    float3 N = normalize(pin.Normal);
    
    float4 normalSample = gNormalMap.Sample(gSampler, pin.TexCoord);
    
    float3 perturbedN = N;
    if (normalSample.b > 0.5f && (normalSample.r != normalSample.g || normalSample.r != normalSample.b))
    {
        float3 dp1 = ddx(pin.PosWorld);
        float3 dp2 = ddy(pin.PosWorld);
        float2 duv1 = ddx(pin.TexCoord);
        float2 duv2 = ddy(pin.TexCoord);

        float r = 1.0f / (duv1.x * duv2.y - duv1.y * duv2.x);
        float3 T = normalize((dp1 * duv2.y - dp2 * duv1.y) * r);
        float3 B = normalize((dp2 * duv1.x - dp1 * duv2.x) * r);

        float3 nm = normalSample.xyz * 2.0f - 1.0f;
        perturbedN = normalize(nm.x * T + nm.y * B + nm.z * N);
    }

    PSOutput output;
    output.Position = float4(pin.PosWorld, 1.0f);
    output.Normal = float4(perturbedN, 1.0f);
    output.Albedo = gDiffuseMap.Sample(gSampler, pin.TexCoord);

    return output;
}
