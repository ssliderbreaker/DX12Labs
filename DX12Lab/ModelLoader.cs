using Assimp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

[StructLayout(LayoutKind.Sequential)]
public struct ModelVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
}

public class Mesh : IDisposable
{
    public ID3D12Resource VertexBuffer { get; set; }
    public ID3D12Resource IndexBuffer { get; set; }
    public VertexBufferView VertexBufferView { get; set; }
    public IndexBufferView IndexBufferView { get; set; }
    public int IndexCount { get; set; }
    public int MaterialIndex { get; set; }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
    }
}

public class Material
{
    public string DiffuseTexturePath { get; set; } = "";
    public Vector4 DiffuseColor { get; set; } = Vector4.One;
}

public class Model : IDisposable
{
    public List<Mesh> Meshes { get; } = new();
    public List<Material> Materials { get; } = new();
    public Dictionary<string, ID3D12Resource> Textures { get; } = new();

    public void Dispose()
    {
        foreach (var mesh in Meshes) mesh.Dispose();
        foreach (var tex in Textures.Values) tex?.Dispose();
    }
}

public static class ModelLoader
{
    public static Model Load(ID3D12Device device,
        ID3D12GraphicsCommandList commandList,
        string path,
        List<ID3D12Resource> uploadBuffers)
    {
        var importer = new AssimpContext();
        var scene = importer.ImportFile(path,
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateNormals |
            PostProcessSteps.FlipUVs |
            PostProcessSteps.JoinIdenticalVertices);

        var model = new Model();
        string dir = Path.GetDirectoryName(path)!;

        foreach (var mat in scene.Materials)
        {
            var material = new Material();
            if (mat.HasTextureDiffuse)
            {
                string texPath = Path.Combine(dir, mat.TextureDiffuse.FilePath);
                material.DiffuseTexturePath = texPath;

                if (!model.Textures.ContainsKey(texPath) && File.Exists(texPath))
                {
                    var tex = TextureLoader.LoadTexture(device, commandList,
                        texPath, out var uploadBuf);
                    model.Textures[texPath] = tex;
                    uploadBuffers.Add(uploadBuf);
                }
            }
            model.Materials.Add(material);
        }

        foreach (var mesh in scene.Meshes)
        {
            var vertices = new ModelVertex[mesh.VertexCount];
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                vertices[i] = new ModelVertex
                {
                    Position = new Vector3(
                        mesh.Vertices[i].X,
                        mesh.Vertices[i].Y,
                        mesh.Vertices[i].Z),
                    Normal = mesh.HasNormals ? new Vector3(
                        mesh.Normals[i].X,
                        mesh.Normals[i].Y,
                        mesh.Normals[i].Z) : Vector3.UnitY,
                    TexCoord = mesh.HasTextureCoords(0) ? new Vector2(
                        mesh.TextureCoordinateChannels[0][i].X,
                        mesh.TextureCoordinateChannels[0][i].Y) : Vector2.Zero,
                };
            }

            var indices = new uint[mesh.FaceCount * 3];
            for (int i = 0; i < mesh.FaceCount; i++)
            {
                indices[i * 3 + 0] = (uint)mesh.Faces[i].Indices[0];
                indices[i * 3 + 1] = (uint)mesh.Faces[i].Indices[1];
                indices[i * 3 + 2] = (uint)mesh.Faces[i].Indices[2];
            }

            var m = new Mesh
            {
                IndexCount = indices.Length,
                MaterialIndex = mesh.MaterialIndex,
            };

            int vbSize = Marshal.SizeOf<ModelVertex>() * vertices.Length;
            m.VertexBuffer = device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)vbSize),
                ResourceStates.GenericRead);

            unsafe
            {
                void* ptr = null;
                m.VertexBuffer.Map(0, null, &ptr);
                fixed (ModelVertex* src = vertices)
                    Buffer.MemoryCopy(src, ptr, vbSize, vbSize);
                m.VertexBuffer.Unmap(0, null);
            }

            m.VertexBufferView = new VertexBufferView(
                m.VertexBuffer.GPUVirtualAddress,
                (uint)vbSize,
                (uint)Marshal.SizeOf<ModelVertex>());

            int ibSize = sizeof(uint) * indices.Length;
            m.IndexBuffer = device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)ibSize),
                ResourceStates.GenericRead);

            unsafe
            {
                void* ptr = null;
                m.IndexBuffer.Map(0, null, &ptr);
                fixed (uint* src = indices)
                    Buffer.MemoryCopy(src, ptr, ibSize, ibSize);
                m.IndexBuffer.Unmap(0, null);
            }

            m.IndexBufferView = new IndexBufferView(
                m.IndexBuffer.GPUVirtualAddress,
                (uint)ibSize,
                Format.R32_UInt);

            model.Meshes.Add(m);
        }

        return model;
    }
}