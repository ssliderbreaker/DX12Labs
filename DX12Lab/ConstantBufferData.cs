using System.Numerics;
using System.Runtime.InteropServices;

namespace DX12Lab;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct ConstantBufferData
{
    public Matrix4x4 WorldViewProj;
    public Matrix4x4 World;
    public Vector4 LightDir;
    public Vector4 LightColor;
    public Vector4 AmbientColor;
    public Vector2 TexOffset;
    public Vector2 TexScale;
}