using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

public class CascadedShadowMap : IDisposable
{
    public const int CascadeCount = 4;
    public const int ShadowMapSize = 2048;

    private const Format DsvFormat = Format.D32_Float;
    private const Format ResourceFormatTypeless = Format.R32_Typeless;
    private const Format SrvFormat = Format.R32_Float;

    private readonly ID3D12Device _device;

    public ID3D12Resource ShadowTexture { get; private set; }
    public ID3D12DescriptorHeap DsvHeap { get; private set; }
    public uint DsvDescriptorSize { get; private set; }

    public Matrix4x4[] CascadeViewProj { get; } = new Matrix4x4[CascadeCount];
    public float[] CascadeSplits { get; } = new float[CascadeCount];

    public CascadedShadowMap(ID3D12Device device)
    {
        _device = device;
        Create();
    }

    private void Create()
    {
        DsvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, CascadeCount));
        DsvDescriptorSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.DepthStencilView);

        ShadowTexture = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Texture2D(
                ResourceFormatTypeless,
                ShadowMapSize, ShadowMapSize,
                CascadeCount, 1, 1, 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.PixelShaderResource,
            new ClearValue(DsvFormat, 1.0f, 0));

        var dsvHandle = DsvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < CascadeCount; i++)
        {
            _device.CreateDepthStencilView(ShadowTexture, new DepthStencilViewDescription
            {
                Format = DsvFormat,
                ViewDimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayDepthStencilView
                {
                    MipSlice = 0,
                    FirstArraySlice = (uint)i,
                    ArraySize = 1,
                }
            }, dsvHandle);
            dsvHandle.Ptr += DsvDescriptorSize;
        }
    }

    public CpuDescriptorHandle GetDsv(int cascade)
    {
        var h = DsvHeap.GetCPUDescriptorHandleForHeapStart();
        h.Ptr += (uint)cascade * DsvDescriptorSize;
        return h;
    }

    public void CreateSrv(CpuDescriptorHandle handle)
    {
        _device.CreateShaderResourceView(ShadowTexture, new ShaderResourceViewDescription
        {
            Format = SrvFormat,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
                FirstArraySlice = 0,
                ArraySize = CascadeCount,
            }
        }, handle);
    }


    public void ComputeCascades(
    Vector3 lightDirection,
    Matrix4x4 cameraView,
    float fovY, float aspect,
    float nearZ, float farZ,
    float lambda = 0.65f)
    {
        for (int i = 0; i < CascadeCount; i++)
        {
            float p = (i + 1) / (float)CascadeCount;
            float logSplit = nearZ * MathF.Pow(farZ / nearZ, p);
            float uniSplit = nearZ + (farZ - nearZ) * p;
            CascadeSplits[i] = lambda * logSplit + (1f - lambda) * uniSplit;
        }

        var lightDir = Vector3.Normalize(lightDirection);
        Vector3 worldUp = MathF.Abs(lightDir.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 lightRight = Vector3.Normalize(Vector3.Cross(worldUp, lightDir));
        Vector3 lightUp = Vector3.Cross(lightDir, lightRight);

        Matrix4x4.Invert(cameraView, out var invView);
        Vector3 cameraPos = invView.Translation;

        float prevSplit = nearZ;
        for (int i = 0; i < CascadeCount; i++)
        {
            float splitFar = CascadeSplits[i];

            float tanHalfFovY = MathF.Tan(fovY / 2f);
            float diagonalFactor = MathF.Sqrt(1f + aspect * aspect) * tanHalfFovY;
            float radius = splitFar * MathF.Max(1f, diagonalFactor);
            radius = MathF.Ceiling(radius * 16f) / 16f;

            Vector3 center = cameraPos;

            float worldUnitsPerTexel = (radius * 2f) / ShadowMapSize;
            float distRight = Vector3.Dot(center, lightRight);
            float distUp = Vector3.Dot(center, lightUp);
            float distDir = Vector3.Dot(center, lightDir);

            distRight = MathF.Floor(distRight / worldUnitsPerTexel) * worldUnitsPerTexel;
            distUp = MathF.Floor(distUp / worldUnitsPerTexel) * worldUnitsPerTexel;

            center = lightRight * distRight + lightUp * distUp + lightDir * distDir;

            var eye = center - lightDir * radius * 2f;
            var lightView = Matrix4x4.CreateLookAt(eye, center, lightUp);
            var lightProj = Matrix4x4.CreateOrthographicOffCenter(
                -radius, radius, -radius, radius, 0.01f, radius * 4f);

            CascadeViewProj[i] = lightView * lightProj;

            prevSplit = splitFar;
        }
    }

    private static Vector3[] GetFrustumCornersWorld(
        Matrix4x4 view, float fovY, float aspect, float nearZ, float farZ)
    {
        Matrix4x4.Invert(view, out var invView);

        float tanHalfFovY = MathF.Tan(fovY / 2f);
        float nearHeight = tanHalfFovY * nearZ;
        float nearWidth = nearHeight * aspect;
        float farHeight = tanHalfFovY * farZ;
        float farWidth = farHeight * aspect;

        Span<Vector3> corners = stackalloc Vector3[]
        {
            new(-nearWidth, -nearHeight, nearZ),
            new( nearWidth, -nearHeight, nearZ),
            new( nearWidth,  nearHeight, nearZ),
            new(-nearWidth,  nearHeight, nearZ),
            new(-farWidth,  -farHeight,  farZ),
            new( farWidth,  -farHeight,  farZ),
            new( farWidth,   farHeight,  farZ),
            new(-farWidth,   farHeight,  farZ),
        };

        var result = new Vector3[8];
        for (int i = 0; i < 8; i++)
            result[i] = Vector3.Transform(corners[i], invView);

        return result;
    }

    public void Dispose()
    {
        ShadowTexture?.Dispose();
        DsvHeap?.Dispose();
    }
}