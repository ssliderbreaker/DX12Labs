Texture2D gPosition : register(t0);
Texture2D gNormal : register(t1);
Texture2D gAlbedo : register(t2);
SamplerState gSampler : register(s0);

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

float4 CalcLight(LightData light, float3 worldPos, float3 normal, float3 viewDir, float4 albedo)
{
    float3 lightDir = float3(0, 1, 0); // инициализация по умолчанию
    float attenuation = 1.0f;
    int type = (int) light.Direction.w;

    if (type == 0) // Directional
    {
        lightDir = normalize(-light.Direction.xyz);
    }
    else if (type == 1) // Point
    {
        float3 toLight = light.Position.xyz - worldPos;
        float dist = length(toLight);
        if (dist > light.Position.w)
            return float4(0, 0, 0, 0);
        lightDir = normalize(toLight);
        attenuation = 1.0f - saturate(dist / light.Position.w);
        attenuation *= attenuation;
    }
    else // Spot
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

    for (int i = 0; i < LightCount; i++)
        lighting += CalcLight(Lights[i], worldPos, normal, viewDir, albedo);

    return saturate(lighting);
}