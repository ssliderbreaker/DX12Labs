cbuffer ConstantBuffer : register(b0)
{
    float4x4 WorldViewProj; 
    float4x4 World; 
    float4 LightDir; 
    float4 LightColor; 
    float4 AmbientColor; 
};

struct VertexIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float4 Color : COLOR;
};

struct VertexOut
{
    float4 Position : SV_POSITION;
    float3 Normal : NORMAL;
    float4 Color : COLOR;
};

VertexOut VSMain(VertexIn vin)
{
    VertexOut vout;
    vout.Position = mul(float4(vin.Position, 1.0f), WorldViewProj);
    vout.Normal = mul(vin.Normal, (float3x3) World);
    vout.Color = vin.Color;
    return vout;
}

float4 PSMain(VertexOut pin) : SV_TARGET
{
    float3 normal = normalize(pin.Normal);
    float3 lightDir = normalize(-LightDir.xyz);
    
    float4 ambient = AmbientColor * pin.Color;
    
    float diff = max(dot(normal, lightDir), 0.0f);
    float4 diffuse = diff * LightColor * pin.Color;
    
    float3 viewDir = float3(0, 0, -1); 
    float3 reflectDir = reflect(-lightDir, normal);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0f), 32.0f);
    float4 specular = spec * LightColor;

    return ambient + diffuse + specular;
}