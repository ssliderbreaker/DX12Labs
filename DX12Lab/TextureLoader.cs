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

        // Получаем footprint для правильного выравнивания строк
        PlacedSubresourceFootPrint[] footprints = new PlacedSubresourceFootPrint[1];
        uint[] numRows = new uint[1];
        ulong[] rowSizes = new ulong[1];
        ulong totalBytes;
        device.GetCopyableFootprints(textureDesc, 0, 1, 0,
            footprints, numRows, rowSizes, out totalBytes);

        uploadBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead);

        // Копируем пиксели с учётом выравнивания строк (RowPitch может быть > width*4)
        unsafe
        {
            void* ptr = null;
            uploadBuffer.Map(0, null, &ptr);

            uint rowPitch = footprints[0].Footprint.RowPitch;
            uint srcRowPitch = (uint)(width * 4);

            fixed (byte* srcPixels = pixels)
            {
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        srcPixels + row * srcRowPitch,
                        (byte*)ptr + row * rowPitch,
                        rowPitch,
                        srcRowPitch);
                }
            }

            uploadBuffer.Unmap(0, null);
        }

        // Правильная копия буфер → текстура
        var dst = new TextureCopyLocation(texture, 0);
        var src = new TextureCopyLocation(uploadBuffer, footprints[0]);
        commandList.CopyTextureRegion(dst, 0, 0, 0, src, null);

        commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(texture,
                ResourceStates.CopyDest,
                ResourceStates.PixelShaderResource)));

        return texture;
    }
}