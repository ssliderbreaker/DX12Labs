using StbImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

// Loads a cubemap (optionally with several precomputed mip levels) from 6
// face images per mip level, and uploads it as a single ID3D12Resource
// (TextureCube, Format.R16G16B16A16_Float) ready for a ShaderResourceView.
//
// Used for:
//  - the irradiance map   (1 mip,  low resolution, diffuse IBL)
//  - the prefiltered environment map (N mips, mip = roughness level, specular IBL)
//
// Face order per mip level MUST be: +X, -X, +Y, -Y, +Z, -Z
// Accepts both HDR (.hdr, float data) and regular LDR images (png/jpg) per face.
public static class IblLoader
{
    public static ID3D12Resource LoadCubemap(
        ID3D12Device device,
        ID3D12GraphicsCommandList commandList,
        List<string[]> mips,
        List<ID3D12Resource> uploadBuffers)
    {
        // Cubemap faces are stored bottom-up like the rest of this project's
        // textures use top-down (TextureLoader flips diffuse/normal maps).
        // Face crops for cubemaps are direction-mapped, not UV-mapped, so we
        // explicitly disable the flip here regardless of what other loaders set.
        StbImage.stbi_set_flip_vertically_on_load(0);

        int mipCount = mips.Count;
        if (mipCount == 0)
            throw new ArgumentException("Cubemap needs at least 1 mip level (6 face images).");

        var faceData = new Half[mipCount][][];
        var mipSizes = new (int w, int h)[mipCount];
        int baseWidth = 0, baseHeight = 0;

        for (int mip = 0; mip < mipCount; mip++)
        {
            if (mips[mip].Length != 6)
                throw new ArgumentException(
                    $"Cubemap mip {mip} must have exactly 6 face paths (+X,-X,+Y,-Y,+Z,-Z), got {mips[mip].Length}.");

            var faces = new Half[6][];
            int w = 0, h = 0;

            for (int face = 0; face < 6; face++)
            {
                string path = mips[mip][face];
                if (!File.Exists(path))
                    throw new FileNotFoundException($"IBL cubemap face not found: {path}");

                bool isHdr = path.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase);
                float[] rgba;

                using (var stream = File.OpenRead(path))
                {
                    if (isHdr)
                    {
                        var img = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                        w = img.Width;
                        h = img.Height;
                        rgba = img.Data;
                    }
                    else
                    {
                        var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                        w = img.Width;
                        h = img.Height;
                        rgba = new float[img.Data.Length];
                        for (int i = 0; i < img.Data.Length; i++)
                            rgba[i] = img.Data[i] / 255f;
                    }
                }

                var half = new Half[w * h * 4];
                for (int i = 0; i < half.Length; i++)
                    half[i] = (Half)rgba[i];

                faces[face] = half;
            }

            faceData[mip] = faces;
            mipSizes[mip] = (w, h);
            if (mip == 0) { baseWidth = w; baseHeight = h; }
        }

        var textureDesc = ResourceDescription.Texture2D(
            Format.R16G16B16A16_Float,
            (uint)baseWidth, (uint)baseHeight,
            6, (ushort)mipCount, 1, 0);

        var texture = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            textureDesc,
            ResourceStates.CopyDest);

        int subresourceCount = mipCount * 6;
        var footprints = new PlacedSubresourceFootPrint[subresourceCount];
        var numRows = new uint[subresourceCount];
        var rowSizes = new ulong[subresourceCount];
        device.GetCopyableFootprints(textureDesc, 0, (uint)subresourceCount, 0,
            footprints, numRows, rowSizes, out ulong totalBytes);

        var uploadBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead);
        uploadBuffers.Add(uploadBuffer);

        unsafe
        {
            void* ptr = null;
            uploadBuffer.Map(0, null, &ptr);

            for (int mip = 0; mip < mipCount; mip++)
            {
                var (w, h) = mipSizes[mip];
                for (int face = 0; face < 6; face++)
                {
                    // D3D12 subresource index = MipSlice + ArraySlice * MipLevels
                    int sub = mip + face * mipCount;

                    var fp = footprints[sub];
                    uint rowPitch = fp.Footprint.RowPitch;
                    uint srcRowPitch = (uint)(w * 4 * sizeof(Half));

                    fixed (Half* src = faceData[mip][face])
                    {
                        byte* srcBytes = (byte*)src;
                        for (int row = 0; row < h; row++)
                        {
                            Buffer.MemoryCopy(
                                srcBytes + row * srcRowPitch,
                                (byte*)ptr + fp.Offset + row * rowPitch,
                                rowPitch,
                                srcRowPitch);
                        }
                    }
                }
            }

            uploadBuffer.Unmap(0, null);
        }

        for (int mip = 0; mip < mipCount; mip++)
        {
            for (int face = 0; face < 6; face++)
            {
                int sub = mip + face * mipCount;
                var dst = new TextureCopyLocation(texture, (uint)sub);
                var src = new TextureCopyLocation(uploadBuffer, footprints[sub]);
                commandList.CopyTextureRegion(dst, 0, 0, 0, src, null);
            }
        }

        commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(texture,
                ResourceStates.CopyDest,
                ResourceStates.PixelShaderResource)));

        return texture;
    }
}
