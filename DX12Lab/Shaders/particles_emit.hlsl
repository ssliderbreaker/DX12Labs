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
ConsumeStructuredBuffer<uint> gDeadList : register(u1);
\
cbuffer EmitConstants : register(b0)
{
    float3 gEmitterPos;
    float gTime;
    uint gNumToEmit;
    uint gSeed;
};
\
float rand(inout uint seed)
{
    seed = seed * 747796405u + 2891336453u;
    uint result = ((seed >> ((seed >> 28u) + 4u)) ^ seed) * 277803737u;
    result = (result >> 22u) ^ result;
    return float(result) / 4294967295.0f;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gNumToEmit)
        return;

    uint seed = gSeed + id.x * 9781u + 1u;
    
    uint index = gDeadList.Consume();

    float angle = rand(seed) * 6.28318530f;
    float radius = rand(seed) * 0.3f;
    float3 offset = float3(cos(angle) * radius, 0.0f, sin(angle) * radius);

    float3 dir = normalize(float3(
        (rand(seed) * 2.0f - 1.0f) * 0.4f,
        1.0f,
        (rand(seed) * 2.0f - 1.0f) * 0.4f));

    float speed = 3.0f + rand(seed) * 4.0f;

    Particle p;
    p.Position = gEmitterPos + offset;
    p.Velocity = dir * speed;
    p.Age = 0.0f;
    p.Life = 0.7f + rand(seed) * 0.9f;
    p.Size = 0.08f + rand(seed) * 0.10f;
    p.Color = float4(1.0f, 0.55f + rand(seed) * 0.3f, 0.15f, 1.0f);
    p.Pad = float3(0.0f, 0.0f, 0.0f);

    gParticles[index] = p;
}
