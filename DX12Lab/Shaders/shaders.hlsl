Texture2D gTexture : register(t0);
SamplerState gSampler : register(s0);

cbuffer ConstantBuffer : register(b0)
{
    float4x4 WorldViewProj;
    float4x4 World;
    float4 LightDir;
    float4 LightColor;
    float4 AmbientColor;
    float2 TexOffset;
    float2 TexScale;
};

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

struct VertexOut
{
    float4 Position : SV_POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD;
};

VertexOut VSMain(VertexIn vin)
{
    VertexOut vout;
    vout.Position = mul(float4(vin.Position, 1.0f), WorldViewProj);
    vout.Normal = mul(vin.Normal, (float3x3) World);
    vout.TexCoord = vin.TexCoord * TexScale + TexOffset;
    return vout;
}

float4 PSMain(VertexOut pin) : SV_TARGET
{
    float3 normal = normalize(pin.Normal);
    float3 lightDir = normalize(-LightDir.xyz);

    float4 texColor = gTexture.Sample(gSampler, pin.TexCoord);

    float4 ambient = AmbientColor * texColor;

    float diff = max(dot(normal, lightDir), 0.0f);
    float4 diffuse = diff * LightColor * texColor;

    float3 reflectDir = reflect(-lightDir, normal);
    float3 viewDir = normalize(float3(0, 0, -1));
    float spec = pow(max(dot(viewDir, reflectDir), 0.0f), 32.0f);
    float4 specular = spec * LightColor * 0.3f;

    return ambient + diffuse + specular;
}