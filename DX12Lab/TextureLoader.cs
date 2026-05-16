using StbImageSharp;
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

public static class TextureLoader
{
    public static ID3D12Resource LoadTexture(ID3D12Device device,
        ID3D12GraphicsCommandList commandList,
        string path,
        out ID3D12Resource uploadBuffer)
    {
        StbImage.stbi_set_flip_vertically_on_load(1);
        ImageResult image;
        using (var stream = System.IO.File.OpenRead(path))
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        int width = image.Width;
        int height = image.Height;
        byte[] pixels = image.Data;

        var textureDesc = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm, (uint)width, (uint)height);

        var texture = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            textureDesc,
            ResourceStates.CopyDest);

        ulong uploadSize = GetRequiredIntermediateSize(device, texture);
        uploadBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer(uploadSize),
            ResourceStates.GenericRead);

        unsafe
        {
            void* ptr = null;
            uploadBuffer.Map(0, null, &ptr);
            fixed (byte* src = pixels)
                Buffer.MemoryCopy(src, ptr, pixels.Length, pixels.Length);
            uploadBuffer.Unmap(0, null);
        }

        // Копируем через CopyBufferRegion вместо CopyTextureRegion
        ulong rowPitch = (ulong)(width * 4);
        ulong alignedRowPitch = (rowPitch + 255) & ~255UL;

        commandList.CopyBufferRegion(texture, 0, uploadBuffer, 0, (ulong)pixels.Length);

        commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(texture,
                ResourceStates.CopyDest,
                ResourceStates.PixelShaderResource)));

        return texture;
    }

    private static ulong GetRequiredIntermediateSize(ID3D12Device device, ID3D12Resource resource)
    {
        var desc = resource.Description;
        ulong totalBytes = 0;
        PlacedSubresourceFootPrint[] footprint = new PlacedSubresourceFootPrint[1];
        uint[] rows = new uint[1];
        ulong[] rowSizes = new ulong[1];
        device.GetCopyableFootprints(desc, 0, 1, 0, footprint, rows, rowSizes, out totalBytes);
        return totalBytes;
    }
}