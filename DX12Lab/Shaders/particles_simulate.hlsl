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

RWStructuredBuffer<Particle> gParticles : register(u0);
AppendStructuredBuffer<uint> gDeadList : register(u1);

cbuffer SimConstants : register(b0)
{
    float gDt;
    float gGravity;
    uint gMaxParticles;
};

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= gMaxParticles)
        return;

    Particle p = gParticles[index];

    if (p.Life < 0.0f)
        return;

    p.Velocity.y += gGravity * gDt;
    p.Position += p.Velocity * gDt;
    p.Age += gDt;
    
    float t = saturate(p.Age / p.Life);
    p.Color = lerp(float4(1.0f, 0.85f, 0.35f, 1.0f), float4(0.6f, 0.05f, 0.0f, 1.0f), t);

    if (p.Age >= p.Life)
    {
        p.Life = -1.0f;
        gDeadList.Append(index);
    }

    gParticles[index] = p;
}
