
Texture2D gSceneColor : register(t0);
SamplerState gSampler : register(s0);

cbuffer PostProcessBuffer : register(b0)
{
    float Exposure;
    int ToneMappingEnabled;
    int VignetteEnabled;
    float VignetteStrength;
    float VignetteRadius;
    float VignetteSoftness;
    float2 Padding;
};

struct VertexOut
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD;
};

VertexOut VSMain(uint id : SV_VertexID)
{
    float2 texCoord = float2((id << 1) & 2, id & 2);

    VertexOut vout;
    vout.Position = float4(texCoord * float2(2, -2) + float2(-1, 1), 0, 1);
    vout.TexCoord = texCoord;
    return vout;
}

float3 ACESFilm(float3 x)
{
    const float a = 2.51f;
    const float b = 0.03f;
    const float c = 2.43f;
    const float d = 0.59f;
    const float e = 0.14f;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float3 ApplyToneMapping(float3 color)
{
    color *= Exposure;
    color = ACESFilm(color);
    color = pow(max(color, 0.0f), 1.0f / 2.2f); 
    return color;
}

float3 ApplyVignette(float3 color, float2 uv)
{
    float2 centered = uv - 0.5f;
    float dist = length(centered) * 1.4142136f; 
    float vig = 1.0f - VignetteStrength *
        smoothstep(VignetteRadius, VignetteRadius + VignetteSoftness, dist);
    return color * saturate(vig);
}

float4 PSMain(VertexOut pin) : SV_TARGET
{
    float3 color = gSceneColor.Sample(gSampler, pin.TexCoord).rgb;

    if (ToneMappingEnabled != 0)
        color = ApplyToneMapping(color);

    if (VignetteEnabled != 0)
        color = ApplyVignette(color, pin.TexCoord);

    return float4(color, 1.0f);
}
