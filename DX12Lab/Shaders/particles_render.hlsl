struct Particle
{
    float3 Position;
    float Life;
    float3 Velocity;
    float Age;
    float4 Color;
    float Size;
    float3 Pad;
};

StructuredBuffer<Particle> gParticles : register(t0);

cbuffer RenderConstants : register(b0)
{
    float4x4 ViewProj;
    float3 CameraRight;
    float _Pad0;
    float3 CameraUp;
    float _Pad1;
};

struct VS_OUT
{
    float3 WorldPos : POSITION;
    float4 Color : COLOR;
    float Size : SIZE;
};

VS_OUT VSMain(uint id : SV_VertexID)
{
    VS_OUT o;
    Particle p = gParticles[id];

    bool alive = (p.Life >= 0.0f) && (p.Age < p.Life);

    o.WorldPos = p.Position;
    o.Color = p.Color;
    o.Size = alive ? p.Size : 0.0f; 
    return o;
}

struct GS_OUT
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD;
    float4 Color : COLOR;
};

[maxvertexcount(4)]
void GSMain(point VS_OUT input[1], inout TriangleStream<GS_OUT> stream)
{
    if (input[0].Size <= 0.0f)
        return; 

    float3 center = input[0].WorldPos;
    float halfSize = input[0].Size * 0.5f;

    float3 right = CameraRight * halfSize;
    float3 up = CameraUp * halfSize;

    float3 corners[4] =
    {
        center - right - up,
        center - right + up,
        center + right - up,
        center + right + up,
    };
    float2 uvs[4] = { float2(0, 1), float2(0, 0), float2(1, 1), float2(1, 0) };

    GS_OUT o;
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        o.Position = mul(float4(corners[i], 1.0f), ViewProj);
        o.TexCoord = uvs[i];
        o.Color = input[0].Color;
        stream.Append(o);
    }
}

float4 PSMain(GS_OUT input) : SV_TARGET
{
    float2 centered = input.TexCoord * 2.0f - 1.0f;
    float dist2 = dot(centered, centered);
    if (dist2 > 1.0f)
        discard; 

    float shade = saturate(1.0f - dist2);
    return float4(input.Color.rgb * (0.6f + 0.4f * shade), 1.0f);
}
