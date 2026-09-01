Texture2D gPosition : register(t0);
Texture2D gNormal : register(t1);
Texture2D gAlbedo : register(t2);
Texture2DArray gShadowMap : register(t3);
SamplerState gSampler : register(s0);
SamplerComparisonState gShadowSampler : register(s1);

struct LightData
{
    float4 Position;
    float4 Direction;
    float4 Color;
    float4 SpotParams;
};

cbuffer LightingBuffer : register(b0)
{
    float4 CameraPos;
    LightData Lights[16];
    int LightCount;
    float3 Padding;
    float4x4 CascadeViewProj[4];
    float4 CascadeSplits;
    float ShadowMapSize;
    int ShadowsEnabled;
    float2 Padding3;
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

int SelectCascade(float dist)
{
    if (dist < CascadeSplits.x)
        return 0;
    if (dist < CascadeSplits.y)
        return 1;
    if (dist < CascadeSplits.z)
        return 2;
    return 3;
}

float CalcShadow(float3 worldPos, float3 normal, float3 lightDir)
{
    if (ShadowsEnabled == 0)
        return 1.0f;

    float dist = length(worldPos - CameraPos.xyz);
    int cascade = SelectCascade(dist);

    float4 lightSpacePos = mul(float4(worldPos, 1.0f), CascadeViewProj[cascade]);
    lightSpacePos.xyz /= lightSpacePos.w;

    float2 shadowUV = lightSpacePos.xy * 0.5f + 0.5f;
    shadowUV.y = 1.0f - shadowUV.y;
    float currentDepth = lightSpacePos.z;

    if (shadowUV.x < 0.0f || shadowUV.x > 1.0f ||
        shadowUV.y < 0.0f || shadowUV.y > 1.0f ||
        currentDepth > 1.0f)
        return 1.0f;

    float NdotL = saturate(dot(normal, lightDir));
    float bias = max(0.0025f * (1.0f - NdotL), 0.0006f);

    float texel = 1.0f / ShadowMapSize;
    float shadow = 0.0f;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2(x, y) * texel;
            shadow += gShadowMap.SampleCmpLevelZero(
                gShadowSampler,
                float3(shadowUV + offset, (float) cascade),
                currentDepth - bias);
        }
    }

    return shadow / 9.0f;
}

float4 CalcLight(LightData light, float3 worldPos, float3 normal, float3 viewDir, float4 albedo)
{
    float3 lightDir = float3(0, 1, 0);
    float attenuation = 1.0f;
    int type = (int) light.Direction.w;

    if (type == 0)
    {
        lightDir = normalize(-light.Direction.xyz);
    }
    else if (type == 1)
    {
        float3 toLight = light.Position.xyz - worldPos;
        float dist = length(toLight);
        if (dist > light.Position.w)
            return float4(0, 0, 0, 0);
        lightDir = normalize(toLight);
        attenuation = 1.0f - saturate(dist / light.Position.w);
        attenuation *= attenuation;
    }
    else
    {
        float3 toLight = light.Position.xyz - worldPos;
        float dist = length(toLight);
        if (dist > light.Position.w)
            return float4(0, 0, 0, 0);
        lightDir = normalize(toLight);
        float cosAngle = dot(-lightDir, normalize(light.Direction.xyz));
        float cosInner = cos(light.SpotParams.x);
        float cosOuter = cos(light.SpotParams.y);
        attenuation = saturate((cosAngle - cosOuter) / (cosInner - cosOuter));
    }

    float diff = max(dot(normal, lightDir), 0.0f);
    float3 diffuse = diff * light.Color.xyz * light.Color.w;

    float3 reflectDir = reflect(-lightDir, normal);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0f), 32.0f);
    float3 specular = spec * light.Color.xyz * light.Color.w * 0.3f;

    return float4((diffuse + specular) * attenuation * albedo.rgb, 1.0f);
}

float4 PSMain(VertexOut pin) : SV_TARGET
{
    float3 worldPos = gPosition.Sample(gSampler, pin.TexCoord).xyz;
    float3 normal = normalize(gNormal.Sample(gSampler, pin.TexCoord).xyz);
    float4 albedo = gAlbedo.Sample(gSampler, pin.TexCoord);

    float3 viewDir = normalize(CameraPos.xyz - worldPos);

    float4 ambient = float4(0.05f, 0.05f, 0.05f, 1.0f) * albedo;
    float4 lighting = ambient;
    
    float shadow = 1.0f;
    bool light0IsSun = (LightCount > 0) && ((int) Lights[0].Direction.w == 0);
    if (light0IsSun)
    {
        float3 sunDir = normalize(-Lights[0].Direction.xyz);
        shadow = CalcShadow(worldPos, normal, sunDir);
    }

    for (int i = 0; i < LightCount; i++)
    {
        float shadowFactor = (i == 0 && light0IsSun) ? shadow : 1.0f;
        lighting += CalcLight(Lights[i], worldPos, normal, viewDir, albedo) * shadowFactor;
    }

    return saturate(lighting);
}