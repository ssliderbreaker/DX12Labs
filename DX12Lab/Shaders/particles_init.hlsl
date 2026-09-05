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

cbuffer InitConstants : register(b0)
{
    uint gMaxParticles;
};

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= gMaxParticles)
        return;

    gParticles[index].Life = -1.0f; 
    gParticles[index].Age = 0.0f;

    gDeadList.Append(index);
}
