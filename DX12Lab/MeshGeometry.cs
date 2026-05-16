using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public System.Numerics.Vector3 Position;
    public System.Numerics.Vector3 Normal;
    public System.Numerics.Vector4 Color;
}

public class MeshGeometry : IDisposable
{
    public ID3D12Resource VertexBuffer { get; private set; }
    public ID3D12Resource IndexBuffer { get; private set; }
    public VertexBufferView VertexBufferView { get; private set; }
    public IndexBufferView IndexBufferView { get; private set; }
    public int IndexCount { get; private set; }

    public static MeshGeometry CreateCube(ID3D12Device device)
    {
        var vertices = new Vertex[]
        {
            // Front
            new() { Position = new(-1,-1,-1), Normal = new(0,0,-1), Color = new(1,0,0,1) },
            new() { Position = new(-1, 1,-1), Normal = new(0,0,-1), Color = new(1,0,0,1) },
            new() { Position = new( 1, 1,-1), Normal = new(0,0,-1), Color = new(1,0,0,1) },
            new() { Position = new( 1,-1,-1), Normal = new(0,0,-1), Color = new(1,0,0,1) },
            // Back
            new() { Position = new( 1,-1, 1), Normal = new(0,0, 1), Color = new(0,1,0,1) },
            new() { Position = new( 1, 1, 1), Normal = new(0,0, 1), Color = new(0,1,0,1) },
            new() { Position = new(-1, 1, 1), Normal = new(0,0, 1), Color = new(0,1,0,1) },
            new() { Position = new(-1,-1, 1), Normal = new(0,0, 1), Color = new(0,1,0,1) },
            // Top
            new() { Position = new(-1, 1,-1), Normal = new(0,1,0), Color = new(0,0,1,1) },
            new() { Position = new(-1, 1, 1), Normal = new(0,1,0), Color = new(0,0,1,1) },
            new() { Position = new( 1, 1, 1), Normal = new(0,1,0), Color = new(0,0,1,1) },
            new() { Position = new( 1, 1,-1), Normal = new(0,1,0), Color = new(0,0,1,1) },
            // Bottom
            new() { Position = new(-1,-1, 1), Normal = new(0,-1,0), Color = new(1,1,0,1) },
            new() { Position = new(-1,-1,-1), Normal = new(0,-1,0), Color = new(1,1,0,1) },
            new() { Position = new( 1,-1,-1), Normal = new(0,-1,0), Color = new(1,1,0,1) },
            new() { Position = new( 1,-1, 1), Normal = new(0,-1,0), Color = new(1,1,0,1) },
            // Left
            new() { Position = new(-1,-1, 1), Normal = new(-1,0,0), Color = new(1,0,1,1) },
            new() { Position = new(-1, 1, 1), Normal = new(-1,0,0), Color = new(1,0,1,1) },
            new() { Position = new(-1, 1,-1), Normal = new(-1,0,0), Color = new(1,0,1,1) },
            new() { Position = new(-1,-1,-1), Normal = new(-1,0,0), Color = new(1,0,1,1) },
            // Right
            new() { Position = new( 1,-1,-1), Normal = new(1,0,0), Color = new(0,1,1,1) },
            new() { Position = new( 1, 1,-1), Normal = new(1,0,0), Color = new(0,1,1,1) },
            new() { Position = new( 1, 1, 1), Normal = new(1,0,0), Color = new(0,1,1,1) },
            new() { Position = new( 1,-1, 1), Normal = new(1,0,0), Color = new(0,1,1,1) },
        };

        var indices = new ushort[]
        {
             0, 1, 2,  0, 2, 3,  // Front
             4, 5, 6,  4, 6, 7,  // Back
             8, 9,10,  8,10,11,  // Top
            12,13,14, 12,14,15,  // Bottom
            16,17,18, 16,18,19,  // Left
            20,21,22, 20,22,23,  // Right
        };

        var mesh = new MeshGeometry();
        mesh.IndexCount = indices.Length;

        // Vertex Buffer
        int vbSize = Marshal.SizeOf<Vertex>() * vertices.Length;
        mesh.VertexBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)vbSize),
            ResourceStates.GenericRead);

        unsafe
        {
            void* ptr = null;
            mesh.VertexBuffer.Map(0, null, &ptr);
            fixed (Vertex* src = vertices)
                Buffer.MemoryCopy(src, ptr, vbSize, vbSize);
            mesh.VertexBuffer.Unmap(0, null);
        }

        mesh.VertexBufferView = new VertexBufferView(
            mesh.VertexBuffer.GPUVirtualAddress,
            (uint)vbSize,
            (uint)Marshal.SizeOf<Vertex>());

        // Index Buffer
        int ibSize = sizeof(ushort) * indices.Length;
        mesh.IndexBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)ibSize),
            ResourceStates.GenericRead);

        unsafe
        {
            void* ptr = null;
            mesh.IndexBuffer.Map(0, null, &ptr);
            fixed (ushort* src = indices)
                Buffer.MemoryCopy(src, ptr, ibSize, ibSize);
            mesh.IndexBuffer.Unmap(0, null);
        }

        mesh.IndexBufferView = new IndexBufferView(
            mesh.IndexBuffer.GPUVirtualAddress,
            (uint)ibSize,
            Format.R16_UInt);

        return mesh;
    }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
    }
}