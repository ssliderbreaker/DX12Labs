using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace DX12Lab;

public struct SceneObject
{
    public Vector3 Position;
    public float Scale;

    public BoundingBox WorldBounds => BoundingBox.FromCenterExtents(
        Position, new Vector3(0.5f * Scale));

    public Matrix4x4 World => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateTranslation(Position);
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct InstanceGpuData
{
    public Matrix4x4 World;
    public Matrix4x4 WorldInvTranspose;
}

public static class SceneGenerator
{
    public static List<SceneObject> Generate(int count, int seed = 12345)
    {
        var rnd = new Random(seed);
        var list = new List<SceneObject>(count);

        for (int i = 0; i < count; i++)
        {
            var pos = new Vector3(
                Lerp(rnd, -30f, 30f),
                Lerp(rnd, 0.3f, 13f),
                Lerp(rnd, -18f, 18f));

            float scale = Lerp(rnd, 0.15f, 0.5f);

            list.Add(new SceneObject { Position = pos, Scale = scale });
        }

        return list;
    }

    private static float Lerp(Random rnd, float min, float max)
        => min + (float)rnd.NextDouble() * (max - min);
}

public static class CubeMesh
{
    public static Mesh Create(ID3D12Device device)
    {
        Span<Vector3> facePos = stackalloc Vector3[]
        {
            new(-0.5f,-0.5f,-0.5f), new(-0.5f, 0.5f,-0.5f), new(0.5f, 0.5f,-0.5f), new(0.5f,-0.5f,-0.5f), 
            new(0.5f,-0.5f, 0.5f),  new(0.5f, 0.5f, 0.5f),  new(-0.5f,0.5f, 0.5f), new(-0.5f,-0.5f, 0.5f), 
            new(-0.5f,-0.5f, 0.5f), new(-0.5f,0.5f, 0.5f),  new(-0.5f,0.5f,-0.5f),new(-0.5f,-0.5f,-0.5f),
            new(0.5f,-0.5f,-0.5f),  new(0.5f, 0.5f,-0.5f),  new(0.5f, 0.5f, 0.5f),new(0.5f,-0.5f, 0.5f), 
            new(-0.5f, 0.5f,-0.5f), new(-0.5f,0.5f, 0.5f),  new(0.5f, 0.5f, 0.5f),new(0.5f, 0.5f,-0.5f), 
            new(-0.5f,-0.5f, 0.5f), new(-0.5f,-0.5f,-0.5f), new(0.5f,-0.5f,-0.5f),new(0.5f,-0.5f, 0.5f), 
        };

        Vector3[] faceNormals = { -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY };
        Vector2[] uvs = { new(0, 1), new(0, 0), new(1, 0), new(1, 1) };

        var vertices = new ModelVertex[24];
        for (int f = 0; f < 6; f++)
            for (int v = 0; v < 4; v++)
                vertices[f * 4 + v] = new ModelVertex
                {
                    Position = facePos[f * 4 + v],
                    Normal = faceNormals[f],
                    TexCoord = uvs[v],
                };

        var indices = new uint[36];
        for (int f = 0; f < 6; f++)
        {
            uint b = (uint)(f * 4);
            uint[] quad = { b, b + 2, b + 1, b, b + 3, b + 2 }; 
            Array.Copy(quad, 0, indices, f * 6, 6);
        }

        var mesh = new Mesh { IndexCount = indices.Length };

        int vbSize = Marshal.SizeOf<ModelVertex>() * vertices.Length;
        mesh.VertexBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)vbSize), ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            mesh.VertexBuffer.Map(0, null, &ptr);
            fixed (ModelVertex* src = vertices)
                Buffer.MemoryCopy(src, ptr, vbSize, vbSize);
            mesh.VertexBuffer.Unmap(0, null);
        }
        mesh.VertexBufferView = new VertexBufferView(
            mesh.VertexBuffer.GPUVirtualAddress, (uint)vbSize, (uint)Marshal.SizeOf<ModelVertex>());

        int ibSize = sizeof(uint) * indices.Length;
        mesh.IndexBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)ibSize), ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            mesh.IndexBuffer.Map(0, null, &ptr);
            fixed (uint* src = indices)
                Buffer.MemoryCopy(src, ptr, ibSize, ibSize);
            mesh.IndexBuffer.Unmap(0, null);
        }
        mesh.IndexBufferView = new IndexBufferView(
            mesh.IndexBuffer.GPUVirtualAddress, (uint)ibSize, Vortice.DXGI.Format.R32_UInt);

        return mesh;
    }
}