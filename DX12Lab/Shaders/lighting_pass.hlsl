Texture2D gPosition : register(t0);
Texture2D gNormal : register(t1);
Texture2D gAlbedo : register(t2);
Texture2DArray gShadowMap : register(t3);
TextureCube gIrradianceMap : register(t4);
TextureCube gPrefilteredEnvMap : register(t5);
Texture2D gBRDFLUT : register(t6);

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
    int GBufferViewMode;
    float Padding3;
    float PrefilteredMipCount;
    float IblIntensity;
    float Padding4;
    float Padding5;
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


static const float PI = 3.14159265359f;

float DistributionGGX(float3 N, float3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0f);
    float NdotH2 = NdotH * NdotH;

    float denom = (NdotH2 * (a2 - 1.0f) + 1.0f);
    denom = PI * denom * denom;
    return a2 / max(denom, 1e-4f);
}

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = roughness + 1.0f;
    float k = (r * r) / 8.0f; 
    return NdotV / (NdotV * (1.0f - k) + k);
}

float GeometrySmith(float3 N, float3 V, float3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0f);
    float NdotL = max(dot(N, L), 0.0f);
    return GeometrySchlickGGX(NdotV, roughness) * GeometrySchlickGGX(NdotL, roughness);
}

float3 FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0f - F0) * pow(saturate(1.0f - cosTheta), 5.0f);
}

float3 FresnelSchlickRoughness(float cosTheta, float3 F0, float roughness)
{
    float3 Fmax = max(float3(1.0f - roughness, 1.0f - roughness, 1.0f - roughness), F0);
    return F0 + (Fmax - F0) * pow(saturate(1.0f - cosTheta), 5.0f);
}

float3 CalcLightPBR(LightData light, float3 worldPos, float3 N, float3 V,
    float3 albedo, float roughness, float metallic, float3 F0)
{
    float3 L;
    float attenuation = 1.0f;
    int type = (int) light.Direction.w;

    if (type == 0)
    {
        L = normalize(-light.Direction.xyz);
    }
    else if (type == 1)
    {
        float3 toLight = light.Position.xyz - worldPos;
        float dist = length(toLight);
        if (dist > light.Position.w)
            return float3(0, 0, 0);
        L = normalize(toLight);
        attenuation = 1.0f - saturate(dist / light.Position.w);
        attenuation *= attenuation;
    }
    else
    {
        float3 toLight = light.Position.xyz - worldPos;
        float dist = length(toLight);
        if (dist > light.Position.w)
            return float3(0, 0, 0);
        L = normalize(toLight);
        attenuation = 1.0f - saturate(dist / light.Position.w);
        attenuation *= attenuation;

        float cosAngle = dot(-L, normalize(light.Direction.xyz));
        float cosInner = cos(light.SpotParams.x);
        float cosOuter = cos(light.SpotParams.y);
        attenuation *= saturate((cosAngle - cosOuter) / (cosInner - cosOuter));
    }

    float3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0f);
    float NdotV = max(dot(N, V), 0.0001f);

    if (NdotL <= 0.0f)
        return float3(0, 0, 0);

    float3 radiance = light.Color.xyz * light.Color.w * attenuation;

    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0f), F0);

    float3 numerator = NDF * G * F;
    float denom = 4.0f * NdotV * NdotL + 0.0001f;
    float3 specular = numerator / denom;
    
    float3 kS = F;
    float3 kD = (1.0f - kS) * (1.0f - metallic);

    return (kD * albedo / PI + specular) * radiance * NdotL;
}


float4 PSMain(VertexOut pin) : SV_TARGET
{
    float3 worldPos = gPosition.Sample(gSampler, pin.TexCoord).xyz;
    float4 normalSample = gNormal.Sample(gSampler, pin.TexCoord);
    float3 N = normalize(normalSample.xyz);
    float4 albedoSample = gAlbedo.Sample(gSampler, pin.TexCoord);

    float3 albedo = albedoSample.rgb;
    float roughness = clamp(albedoSample.a, 0.045f, 1.0f);
    float metallic = saturate(normalSample.a);

    if (GBufferViewMode == 1)
        return float4(albedo, 1.0f);
    if (GBufferViewMode == 2)
        return float4(N * 0.5f + 0.5f, 1.0f);
    if (GBufferViewMode == 3)
        return float4(frac(worldPos * 0.05f), 1.0f);
    if (GBufferViewMode == 4)
        return float4(roughness.xxx, 1.0f);
    if (GBufferViewMode == 5)
        return float4(metallic.xxx, 1.0f);

    float3 V = normalize(CameraPos.xyz - worldPos);
    float NdotV = max(dot(N, V), 0.0001f);
    
    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo, metallic);
    
    float shadow = 1.0f;
    bool light0IsSun = (LightCount > 0) && ((int) Lights[0].Direction.w == 0);
    if (light0IsSun)
    {
        float3 sunDir = normalize(-Lights[0].Direction.xyz);
        shadow = CalcShadow(worldPos, N, sunDir);
    }

    float3 Lo = float3(0, 0, 0);
    for (int i = 0; i < LightCount; i++)
    {
        float shadowFactor = (i == 0 && light0IsSun) ? shadow : 1.0f;
        Lo += CalcLightPBR(Lights[i], worldPos, N, V, albedo, roughness, metallic, F0) * shadowFactor;
    }
    
    float3 kS = FresnelSchlickRoughness(NdotV, F0, roughness);
    float3 kD = (1.0f - kS) * (1.0f - metallic);

    float3 irradiance = gIrradianceMap.Sample(gSampler, N).rgb;
    float3 diffuseIBL = irradiance * albedo;

    float maxMip = max(PrefilteredMipCount - 1.0f, 0.0f);
    float3 R = reflect(-V, N);
    float3 prefilteredColor = gPrefilteredEnvMap.SampleLevel(gSampler, R, roughness * maxMip).rgb;
    float2 envBRDF = gBRDFLUT.Sample(gSampler, float2(NdotV, roughness)).rg;
    float3 specularIBL = prefilteredColor * (kS * envBRDF.x + envBRDF.y);

    float3 ambient = (kD * diffuseIBL + specularIBL) * IblIntensity;

    float3 color = ambient + Lo;

    return float4(color, 1.0f);
}