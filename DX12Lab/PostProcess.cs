using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DX12Lab;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct PostProcessConstants
{
    public float Exposure;
    public int ToneMappingEnabled;
    public int VignetteEnabled;
    public float VignetteStrength;
    public float VignetteRadius;
    public float VignetteSoftness;
    public Vector2 Padding;
}

public class PostProcess : IDisposable
{
    private const Format SceneColorFormat = Format.R16G16B16A16_Float;
    private const Format BackBufferFormat = Format.R8G8B8A8_UNorm;

    private readonly ID3D12Device _device;

    private ID3D12Resource _sceneColor;
    private ID3D12DescriptorHeap _rtvHeap;

    private ID3D12DescriptorHeap _srvCbvHeap;
    private uint _cbvSrvDescSize;

    private ID3D12RootSignature _rootSig;
    private ID3D12PipelineState _pso;

    private ID3D12Resource _cb;
    private unsafe PostProcessConstants* _cbData;

    private int _width, _height;

    public float Exposure { get; set; } = 1.0f;
    public bool ToneMappingEnabled { get; set; } = false;
    public bool VignetteEnabled { get; set; } = false;
    public float VignetteStrength { get; set; } = 0.6f;
    public float VignetteRadius { get; set; } = 0.55f;
    public float VignetteSoftness { get; set; } = 0.45f;

    public PostProcess(ID3D12Device device, int width, int height)
    {
        _device = device;
        _width = width;
        _height = height;

        CreateRootSignatureAndPso();
        CreateConstantBuffer();
        CreateSceneColorResources();
    }

    private void CreateRootSignatureAndPso()
    {
        var rootParams = new RootParameter1[]
        {
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 1, 0)),
                ShaderVisibility.Pixel),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ConstantBufferView, 1, 0)),
                ShaderVisibility.Pixel),
        };

        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = ComparisonFunction.Always,
            MaxLOD = float.MaxValue,
        };

        _rootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler }));

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "postprocess.hlsl");
        var vs = CompileShader(shaderPath, "VSMain", "vs_5_0");
        var ps = CompileShader(shaderPath, "PSMain", "ps_5_0");

        _pso = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = new InputLayoutDescription(),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid),
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { BackBufferFormat },
            SampleDescription = new SampleDescription(1, 0),
        });
    }

    private void CreateConstantBuffer()
    {
        int cbSize = (Marshal.SizeOf<PostProcessConstants>() + 255) & ~255;
        _cb = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize),
            ResourceStates.GenericRead);

        unsafe
        {
            void* ptr = null;
            _cb.Map(0, null, &ptr);
            _cbData = (PostProcessConstants*)ptr;
        }
    }

    private void CreateSceneColorResources()
    {
        _rtvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));

        _srvCbvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            2, DescriptorHeapFlags.ShaderVisible));
        _cbvSrvDescSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        CreateSceneColorTexture();

        int cbSize = (Marshal.SizeOf<PostProcessConstants>() + 255) & ~255;
        var cbvHandle = _srvCbvHeap.GetCPUDescriptorHandleForHeapStart();
        cbvHandle.Ptr += _cbvSrvDescSize;
        _device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_cb.GPUVirtualAddress, (uint)cbSize), cbvHandle);
    }

    private void CreateSceneColorTexture()
    {
        _sceneColor = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Texture2D(
                SceneColorFormat,
                (uint)_width, (uint)_height,
                1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource,
            new ClearValue(SceneColorFormat, new Color4(0, 0, 0, 1)));

        _device.CreateRenderTargetView(_sceneColor, null,
            _rtvHeap.GetCPUDescriptorHandleForHeapStart());

        var srvHandle = _srvCbvHeap.GetCPUDescriptorHandleForHeapStart();
        _device.CreateShaderResourceView(_sceneColor, new ShaderResourceViewDescription
        {
            Format = SceneColorFormat,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
        }, srvHandle);
    }

    public CpuDescriptorHandle SceneColorRtv => _rtvHeap.GetCPUDescriptorHandleForHeapStart();

    public void BeginScene(ID3D12GraphicsCommandList cmd)
    {
        cmd.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_sceneColor,
                ResourceStates.PixelShaderResource, ResourceStates.RenderTarget)));
    }

    public void EndScene(ID3D12GraphicsCommandList cmd)
    {
        cmd.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_sceneColor,
                ResourceStates.RenderTarget, ResourceStates.PixelShaderResource)));
    }

    public void Render(ID3D12GraphicsCommandList cmd, CpuDescriptorHandle backBufferRtv, int width, int height)
    {
        unsafe
        {
            _cbData->Exposure = Exposure;
            _cbData->ToneMappingEnabled = ToneMappingEnabled ? 1 : 0;
            _cbData->VignetteEnabled = VignetteEnabled ? 1 : 0;
            _cbData->VignetteStrength = VignetteStrength;
            _cbData->VignetteRadius = VignetteRadius;
            _cbData->VignetteSoftness = VignetteSoftness;
        }

        cmd.OMSetRenderTargets(backBufferRtv, null);
        cmd.SetPipelineState(_pso);
        cmd.SetGraphicsRootSignature(_rootSig);

        cmd.SetDescriptorHeaps(_srvCbvHeap);

        var srvGpu = _srvCbvHeap.GetGPUDescriptorHandleForHeapStart();
        cmd.SetGraphicsRootDescriptorTable(0, srvGpu);

        var cbvGpu = _srvCbvHeap.GetGPUDescriptorHandleForHeapStart();
        cbvGpu.Ptr += _cbvSrvDescSize;
        cmd.SetGraphicsRootDescriptorTable(1, cbvGpu);

        cmd.RSSetViewport(new Viewport(0, 0, width, height));
        cmd.RSSetScissorRect(new Vortice.RawRect(0, 0, width, height));
        cmd.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        cmd.DrawInstanced(3, 1, 0, 0);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _width = width;
        _height = height;

        _sceneColor?.Dispose();
        CreateSceneColorTexture();
    }

    private static byte[] CompileShader(string path, string entry, string profile)
    {
        Compiler.CompileFromFile(path, null, null, entry, profile,
            ShaderFlags.Debug | ShaderFlags.SkipOptimization, out var blob, out var errors);
        if (blob == null)
            throw new Exception($"{profile} error: {errors?.AsString()}");
        byte[] bytes = new byte[blob.BufferSize];
        Marshal.Copy(blob.BufferPointer, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        unsafe { _cb?.Unmap(0, null); }
        _cb?.Dispose();
        _sceneColor?.Dispose();
        _rtvHeap?.Dispose();
        _srvCbvHeap?.Dispose();
        _pso?.Dispose();
        _rootSig?.Dispose();
    }
}