using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DX12Lab;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GeometryConstantBuffer
{
    public Matrix4x4 WorldViewProj;
    public Matrix4x4 World;
    public Matrix4x4 WorldInvTranspose;
    public Vector3 CameraPos;
    public float DisplacementScale;
    public float TessMin;
    public float TessMax;
    public float TessNearDist;
    public float TessFarDist;
    public float Roughness;
    public float Metallic;
    public float PadGeom0;
    public float PadGeom1;
    public float BumpStrength;
    public Vector2 DisplacementMapTexelSize;
    public float HeightMid;
}

[StructLayout(LayoutKind.Sequential)]
public struct LightData
{
    public Vector4 Position;
    public Vector4 Direction;
    public Vector4 Color;
    public Vector4 SpotParams;
}

[InlineArray(16)]
public struct LightArray16
{
    private LightData _element0;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct LightingConstantBuffer
{
    public Vector4 CameraPos;
    public LightArray16 Lights;
    public int LightCount;
    public Vector3 Padding;
    public Matrix4x4 CascadeViewProj0;
    public Matrix4x4 CascadeViewProj1;
    public Matrix4x4 CascadeViewProj2;
    public Matrix4x4 CascadeViewProj3;
    public Vector4 CascadeSplits;
    public float ShadowMapSize;
    public int ShadowsEnabled;
    public int GBufferViewMode;
    public float Padding3;
    public float PrefilteredMipCount;
    public float IblIntensity;
    public float Padding4;
    public float Padding5;
}

public class RenderingSystem : IDisposable
{
    private const int FrameCount = 2;
    private const int MaxInstances = 40000;
    private const int MaxParticleCount = 20000;

    private ID3D12Device _device;
    private IDXGISwapChain3 _swapChain;
    private ID3D12CommandQueue _commandQueue;
    private ID3D12CommandAllocator[] _commandAllocators = new ID3D12CommandAllocator[FrameCount];
    private ID3D12GraphicsCommandList _commandList;

    private ID3D12DescriptorHeap _rtvHeap;
    private ID3D12Resource[] _renderTargets = new ID3D12Resource[FrameCount];
    private uint _rtvDescriptorSize;

    private ID3D12DescriptorHeap _dsvHeap;
    private ID3D12Resource _depthBuffer;

    private GBuffer _gBuffer;

    private ID3D12RootSignature _geometryRootSig;
    private ID3D12PipelineState _geometryPso;
    private ID3D12PipelineState _geometryWireframePso;
    private ID3D12Resource _geometryCb;
    private unsafe GeometryConstantBuffer* _geometryCbData;

    private ID3D12DescriptorHeap _geometryDescHeap;
    private uint _geometryDescSize;

    private ID3D12RootSignature _lightingRootSig;
    private ID3D12PipelineState _lightingPso;
    private ID3D12Resource _lightingCb;
    private unsafe LightingConstantBuffer* _lightingCbData;
    private ID3D12DescriptorHeap _lightingDescHeap;

    private ID3D12Resource _irradianceMap;
    private ID3D12Resource _prefilteredEnvMap;
    private ID3D12Resource _brdfLutMap;
    private int _prefilteredMipCount;

    public float IblIntensity { get; set; } = 1.0f;

    private Dictionary<string, uint> _textureIndices = new();

    private ID3D12Fence _fence;
    private ulong[] _fenceValues = new ulong[FrameCount];
    private EventWaitHandle _fenceEvent;

    private uint _frameIndex;
    private int _width, _height;

    private Model _model;
    private string _whiteTexPath = "";
    private List<ID3D12Resource> _uploadBuffers = new();

    public Vector3 CameraPos { get; set; } = new Vector3(-17, 1.8f, 0);
    public Vector3 CameraTarget { get; set; } = new Vector3(15, 6, 0);
    public List<LightData> Lights { get; } = new();

    public float DisplacementScale { get; set; } = 0.05f;
    public float TessMin { get; set; } = 1.0f;
    public float TessMax { get; set; } = 16.0f;
    public float TessNearDist { get; set; } = 5.0f;
    public float TessFarDist { get; set; } = 50.0f;
    public float BumpStrength { get; set; } = 3.0f;
    public float HeightMid { get; set; } = 0.5f;

    private ID3D12Resource _globalDisplacementTex;
    private ID3D12Resource _globalNormalTex;
    private uint _globalTexHeapOffset;

    private List<SceneObject> _sceneObjects = new();
    private Octree _octree;

    private Mesh _cubeMesh;
    private ID3D12Resource _cubeDiffuseTex;

    private ID3D12RootSignature _instanceRootSig;
    private ID3D12PipelineState _instancePso;
    private ID3D12DescriptorHeap _instanceDescHeap;

    private ID3D12Resource _instanceViewProjCb;
    private unsafe Matrix4x4* _instanceViewProjCbData;

    private ID3D12Resource _instanceDataBuffer;
    private unsafe InstanceGpuData* _instanceDataPtr;

    public bool FrustumCullingEnabled { get; set; } = true;
    public bool OctreeAccelerationEnabled { get; set; } = true;
    public int TotalInstanceCount => _sceneObjects.Count;
    public int VisibleInstanceCount { get; private set; }

    private readonly List<int> _cullResults = new();

    private CascadedShadowMap _csm;

    private ID3D12RootSignature _shadowStaticRootSig;
    private ID3D12PipelineState _shadowStaticPso;

    private ID3D12RootSignature _shadowInstancedRootSig;
    private ID3D12PipelineState _shadowInstancedPso;

    public bool ShadowsEnabled { get; set; } = true;

    private ParticleSystem _particles;
    private float _totalTime;
    private readonly Vector3 _particleEmitterPos = new Vector3(0f, 20f, 0f);

    private PostProcess _postProcess;
    public bool ToneMappingEnabled { get; set; } = true;
    public bool VignetteEnabled { get; set; } = true;
    public float Exposure { get; set; } = 1.0f;
    public bool ParticlesEnabled { get; set; } = true;
    public float CascadeLambda { get; set; } = 0.65f;
    public int GBufferViewMode { get; set; } = 0;
    public bool WireframeEnabled { get; set; } = false;

    public RenderingSystem(IntPtr hwnd, int width, int height)
    {
        _width = width;
        _height = height;
        Init(hwnd);
    }

    private void Init(IntPtr hwnd)
    {
#if DEBUG
        if (D3D12.D3D12GetDebugInterface<ID3D12Debug>(out var debug).Success)
            debug!.EnableDebugLayer();
#endif

        DXGI.CreateDXGIFactory2(false, out IDXGIFactory4 factory);

        var res = D3D12.D3D12CreateDevice(null,
            Vortice.Direct3D.FeatureLevel.Level_12_0, out _device);
        if (res.Failure) throw new Exception($"Device failed: {res}");

        _commandQueue = _device.CreateCommandQueue(
            new CommandQueueDescription(CommandListType.Direct));

        using var sc1 = factory.CreateSwapChainForHwnd(_commandQueue, hwnd,
            new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = FrameCount,
                SwapEffect = SwapEffect.FlipDiscard,
            });
        _swapChain = sc1.QueryInterface<IDXGISwapChain3>();
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        _rtvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.RenderTargetView);
        CreateRenderTargetViews();

        _dsvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
        CreateDepthBuffer();

        _gBuffer = new GBuffer(_device, _width, _height);

        _fence = _device.CreateFence(0);
        _fenceEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        _geometryDescSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        CreateGeometryPass();
        CreateShadowPass();
        CreateLightingPass();

        for (int i = 0; i < FrameCount; i++)
            _commandAllocators[i] = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _commandAllocators[_frameIndex], _geometryPso);

        CreateInstancePass();

        _particles = new ParticleSystem(_device, _commandList, MaxParticleCount);

        CreateIblTextures();

        LoadScene();
        SetupLights();

        _sceneObjects = SceneGenerator.Generate(MaxInstances);
        BuildOctree();

        _postProcess = new PostProcess(_device, _width, _height);
    }

    private void CreateRenderTargetViews()
    {
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
            rtvHandle.Ptr += _rtvDescriptorSize;
        }
    }

    private void CreateDepthBuffer()
    {
        _depthBuffer = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            ResourceDescription.Texture2D(Format.D32_Float,
                (uint)_width, (uint)_height, 1, 0, 1, 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(Format.D32_Float, 1.0f, 0));
        _device.CreateDepthStencilView(_depthBuffer,
            new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = DepthStencilViewDimension.Texture2D
            },
            _dsvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    private byte[] CompileShader(string path, string entry, string profile)
    {
        Compiler.CompileFromFile(path, null, null, entry, profile,
            ShaderFlags.Debug | ShaderFlags.SkipOptimization,
            out var blob, out var errors);
        if (blob == null)
            throw new Exception($"{profile} error: {errors?.AsString()}");
        byte[] bytes = new byte[blob.BufferSize];
        Marshal.Copy(blob.BufferPointer, bytes, 0, bytes.Length);
        return bytes;
    }

    private void CreateGeometryPass()
    {
        var rootParams = new RootParameter1[]
        {
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ConstantBufferView, 1, 0)),
                ShaderVisibility.All),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 1, 0)),
                ShaderVisibility.Pixel),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 1, 1)),
                ShaderVisibility.All),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 1, 2)),
                ShaderVisibility.Pixel),
        };

        var sampler = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Always,
            MaxLOD = float.MaxValue,
        };

        _geometryRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler }));

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "geometry_pass.hlsl");
        var vs = CompileShader(shaderPath, "VSMain", "vs_5_0");
        var ps = CompileShader(shaderPath, "PSMain", "ps_5_0");
        var hs = CompileShader(shaderPath, "HSMain", "hs_5_0");
        var ds = CompileShader(shaderPath, "DSMain", "ds_5_0");

        _geometryPso = _device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = _geometryRootSig,
                VertexShader = vs,
                PixelShader = ps,
                HullShader = hs,
                DomainShader = ds,
                InputLayout = new InputLayoutDescription(new[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                    new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
                }),
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Patch,
                RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid),
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.Default,
                RenderTargetFormats = GBuffer.Formats,
                DepthStencilFormat = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
            });

        _geometryWireframePso = _device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = _geometryRootSig,
                VertexShader = vs,
                PixelShader = ps,
                HullShader = hs,
                DomainShader = ds,
                InputLayout = new InputLayoutDescription(new[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                    new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
                }),
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Patch,
                RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Wireframe),
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.Default,
                RenderTargetFormats = GBuffer.Formats,
                DepthStencilFormat = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
            });

        int cbSize = (Marshal.SizeOf<GeometryConstantBuffer>() + 255) & ~255;
        _geometryCb = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize),
            ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            _geometryCb.Map(0, null, &ptr);
            _geometryCbData = (GeometryConstantBuffer*)ptr;
        }

    }

    private void CreateShadowPass()
    {
        _csm = new CascadedShadowMap(_device);

        var staticParams = new RootParameter1[]
        {
            new RootParameter1(new RootConstants(0, 0, 16), ShaderVisibility.Vertex),
        };
        _shadowStaticRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, staticParams));

        string staticShaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "shadow_depth_static.hlsl");
        var staticVs = CompileShader(staticShaderPath, "VSMain", "vs_5_0");

        _shadowStaticPso = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _shadowStaticRootSig,
            VertexShader = staticVs,
            InputLayout = new InputLayoutDescription(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
            }),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = new RasterizerDescription(CullMode.Back, FillMode.Solid),
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
        });

        var instParams = new RootParameter1[]
        {
            new RootParameter1(new RootConstants(0, 0, 16), ShaderVisibility.Vertex),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0)),
                ShaderVisibility.Vertex),
        };
        _shadowInstancedRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, instParams));

        string instShaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "shadow_depth_instanced.hlsl");
        var instVs = CompileShader(instShaderPath, "VSMain", "vs_5_0");

        _shadowInstancedPso = _device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _shadowInstancedRootSig,
            VertexShader = instVs,
            InputLayout = new InputLayoutDescription(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
            }),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = new RasterizerDescription(CullMode.Back, FillMode.Solid),
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
        });
    }

    private void CreateLightingPass()
    {
        var rootParams = new RootParameter1[]
        {
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 7, 0)),
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

        var shadowSampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0)
        {
            Filter = Filter.ComparisonMinMagMipLinear,
            AddressU = TextureAddressMode.Border,
            AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border,
            ComparisonFunction = ComparisonFunction.LessEqual,
            BorderColor = StaticBorderColor.OpaqueWhite,
            MaxLOD = float.MaxValue,
        };

        _lightingRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler, shadowSampler }));

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "lighting_pass.hlsl");
        var vs = CompileShader(shaderPath, "VSMain", "vs_5_0");
        var ps = CompileShader(shaderPath, "PSMain", "ps_5_0");

        _lightingPso = _device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = _lightingRootSig,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(),
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid),
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Format.R8G8B8A8_UNorm },
                SampleDescription = new SampleDescription(1, 0),
            });

        int cbSize = (Marshal.SizeOf<LightingConstantBuffer>() + 255) & ~255;
        _lightingCb = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize),
            ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            _lightingCb.Map(0, null, &ptr);
            _lightingCbData = (LightingConstantBuffer*)ptr;
        }

        _lightingDescHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            GBuffer.Count + 5, DescriptorHeapFlags.ShaderVisible));

        RefreshLightingGBufferDescriptors();

        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        var shadowSrvHandle = _lightingDescHeap.GetCPUDescriptorHandleForHeapStart();
        shadowSrvHandle.Ptr += (uint)GBuffer.Count * descSize;
        _csm.CreateSrv(shadowSrvHandle);

        var cbvHandle = _lightingDescHeap.GetCPUDescriptorHandleForHeapStart();
        cbvHandle.Ptr += (uint)(GBuffer.Count + 4) * descSize;
        _device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_lightingCb.GPUVirtualAddress, (uint)cbSize),
            cbvHandle);
    }

    private void CreateIblTextures()
    {
        string iblDir = Path.Combine(AppContext.BaseDirectory, "Assets", "IBL");

        string[] irradianceFaces =
        {
            Path.Combine(iblDir, "irradiance", "px.hdr"),
            Path.Combine(iblDir, "irradiance", "nx.hdr"),
            Path.Combine(iblDir, "irradiance", "py.hdr"),
            Path.Combine(iblDir, "irradiance", "ny.hdr"),
            Path.Combine(iblDir, "irradiance", "pz.hdr"),
            Path.Combine(iblDir, "irradiance", "nz.hdr"),
        };
        _irradianceMap = IblLoader.LoadCubemap(_device, _commandList,
            new List<string[]> { irradianceFaces }, _uploadBuffers);

        var prefilteredMips = new List<string[]>();
        for (int mip = 0; ; mip++)
        {
            string mipDir = Path.Combine(iblDir, "prefiltered", $"mip{mip}");
            string px = Path.Combine(mipDir, "px.hdr");
            if (!File.Exists(px)) break;

            prefilteredMips.Add(new[]
            {
                px,
                Path.Combine(mipDir, "nx.hdr"),
                Path.Combine(mipDir, "py.hdr"),
                Path.Combine(mipDir, "ny.hdr"),
                Path.Combine(mipDir, "pz.hdr"),
                Path.Combine(mipDir, "nz.hdr"),
            });
        }
        if (prefilteredMips.Count == 0)
            throw new Exception("No prefiltered environment mips found in Assets/IBL/prefiltered/mip0..");

        _prefilteredEnvMap = IblLoader.LoadCubemap(_device, _commandList, prefilteredMips, _uploadBuffers);
        _prefilteredMipCount = prefilteredMips.Count;

        string brdfLutPath = Path.Combine(iblDir, "brdf_lut.png");
        _brdfLutMap = TextureLoader.LoadTexture(_device, _commandList, brdfLutPath, out var brdfUpload);
        _uploadBuffers.Add(brdfUpload);

        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        var handle = _lightingDescHeap.GetCPUDescriptorHandleForHeapStart();
        handle.Ptr += (uint)(GBuffer.Count + 1) * descSize;

        _device.CreateShaderResourceView(_irradianceMap, new ShaderResourceViewDescription
        {
            Format = Format.R16G16B16A16_Float,
            ViewDimension = ShaderResourceViewDimension.TextureCube,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            TextureCube = new TextureCubeShaderResourceView { MipLevels = 1 },
        }, handle);
        handle.Ptr += descSize;

        _device.CreateShaderResourceView(_prefilteredEnvMap, new ShaderResourceViewDescription
        {
            Format = Format.R16G16B16A16_Float,
            ViewDimension = ShaderResourceViewDimension.TextureCube,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            TextureCube = new TextureCubeShaderResourceView { MipLevels = (uint)_prefilteredMipCount },
        }, handle);
        handle.Ptr += descSize;

        _device.CreateShaderResourceView(_brdfLutMap, new ShaderResourceViewDescription
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, handle);
    }

    private void RefreshLightingGBufferDescriptors()
    {
        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        var dstHandle = _lightingDescHeap.GetCPUDescriptorHandleForHeapStart();
        var srcHandle = _gBuffer.SrvCpuHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < GBuffer.Count; i++)
        {
            _device.CopyDescriptorsSimple(1, dstHandle, srcHandle,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            dstHandle.Ptr += descSize;
            srcHandle.Ptr += _gBuffer.SrvDescriptorSize;
        }
    }

    private void CreateInstancePass()
    {
        var rootParams = new RootParameter1[]
        {
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ConstantBufferView, 1, 0)),
                ShaderVisibility.Vertex),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 1, 0)),
                ShaderVisibility.Pixel),
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 1, 1)),
                ShaderVisibility.Vertex),
        };

        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Always,
            MaxLOD = float.MaxValue,
        };

        _instanceRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler }));

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "instance_pass.hlsl");
        var vs = CompileShader(shaderPath, "VSMain", "vs_5_0");
        var ps = CompileShader(shaderPath, "PSMain", "ps_5_0");

        _instancePso = _device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = _instanceRootSig,
                VertexShader = vs,
                PixelShader = ps,
                InputLayout = new InputLayoutDescription(new[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                    new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
                }),
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = new RasterizerDescription(CullMode.Back, FillMode.Solid),
                BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.Default,
                RenderTargetFormats = GBuffer.Formats,
                DepthStencilFormat = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
            });

        _cubeMesh = CubeMesh.Create(_device);

        int cbSize = (Marshal.SizeOf<Matrix4x4>() + 255) & ~255;
        _instanceViewProjCb = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            _instanceViewProjCb.Map(0, null, &ptr);
            _instanceViewProjCbData = (Matrix4x4*)ptr;
        }

        int strideBytes = Marshal.SizeOf<InstanceGpuData>();
        _instanceDataBuffer = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)(strideBytes * MaxInstances)),
            ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            _instanceDataBuffer.Map(0, null, &ptr);
            _instanceDataPtr = (InstanceGpuData*)ptr;
        }

        _instanceDescHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            3, DescriptorHeapFlags.ShaderVisible));

        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        var handle = _instanceDescHeap.GetCPUDescriptorHandleForHeapStart();

        _device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_instanceViewProjCb.GPUVirtualAddress, (uint)cbSize),
            handle);
        handle.Ptr += descSize;
        handle.Ptr += descSize;

        _device.CreateShaderResourceView(_instanceDataBuffer,
            new ShaderResourceViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Buffer = new BufferShaderResourceView
                {
                    FirstElement = 0,
                    NumElements = (uint)MaxInstances,
                    StructureByteStride = (uint)strideBytes,
                }
            }, handle);
    }

    private void LoadScene()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sponza", "sponza.obj");
        _model = ModelLoader.Load(_device, _commandList, path, _uploadBuffers,
            new Vector3(0, 15.3f, 0), skipGlass: true);

        _whiteTexPath = Path.Combine(AppContext.BaseDirectory, "Assets", "white.png");
        _model.Textures[_whiteTexPath] = TextureLoader.LoadTexture(_device, _commandList, _whiteTexPath, out var whiteUpload);
        _uploadBuffers.Add(whiteUpload);

        uint textureCount = (uint)_model.Textures.Count; 
        _globalTexHeapOffset = 1 + textureCount;
        _geometryDescHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            1 + textureCount + 2, DescriptorHeapFlags.ShaderVisible));

        int cbSize = (Marshal.SizeOf<GeometryConstantBuffer>() + 255) & ~255;
        _device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_geometryCb.GPUVirtualAddress, (uint)cbSize),
            _geometryDescHeap.GetCPUDescriptorHandleForHeapStart());

        uint slot = 0;
        foreach (var (texPath, tex) in _model.Textures)
        {
            var handle = _geometryDescHeap.GetCPUDescriptorHandleForHeapStart();
            handle.Ptr += (1 + slot) * _geometryDescSize;
            _device.CreateShaderResourceView(tex,
                new ShaderResourceViewDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
                }, handle);
            _textureIndices[texPath] = slot;
            slot++;
        }

        string displacementPath = Path.Combine(AppContext.BaseDirectory, "Assets", "displacement.png");
        string normalPath = Path.Combine(AppContext.BaseDirectory, "Assets", "normal.png");

        _globalDisplacementTex = TextureLoader.LoadTexture(_device, _commandList, displacementPath, out var dispUpload);
        _uploadBuffers.Add(dispUpload);
        _globalNormalTex = TextureLoader.LoadTexture(_device, _commandList, normalPath, out var normUpload);
        _uploadBuffers.Add(normUpload);

        var dispHandle = _geometryDescHeap.GetCPUDescriptorHandleForHeapStart();
        dispHandle.Ptr += _globalTexHeapOffset * _geometryDescSize;
        _device.CreateShaderResourceView(_globalDisplacementTex,
            new ShaderResourceViewDescription
            {
                Format = Format.R8G8B8A8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, dispHandle);

        var normHandle = _geometryDescHeap.GetCPUDescriptorHandleForHeapStart();
        normHandle.Ptr += (_globalTexHeapOffset + 1) * _geometryDescSize;
        _device.CreateShaderResourceView(_globalNormalTex,
            new ShaderResourceViewDescription
            {
                Format = Format.R8G8B8A8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, normHandle);

        string cubeTexPath = Path.Combine(AppContext.BaseDirectory, "Assets", "texture.png");
        _cubeDiffuseTex = TextureLoader.LoadTexture(_device, _commandList, cubeTexPath, out var cubeTexUpload);
        _uploadBuffers.Add(cubeTexUpload);

        uint instDescSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        var diffuseSlot = _instanceDescHeap.GetCPUDescriptorHandleForHeapStart();
        diffuseSlot.Ptr += instDescSize;
        _device.CreateShaderResourceView(_cubeDiffuseTex,
            new ShaderResourceViewDescription
            {
                Format = Format.R8G8B8A8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, diffuseSlot);

        _commandList.Close();
        _commandQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)_commandList });
        WaitForGpu();

        foreach (var buf in _uploadBuffers) buf?.Dispose();
        _uploadBuffers.Clear();

        _commandAllocators[_frameIndex].Reset();
        _commandList.Reset(_commandAllocators[_frameIndex], _geometryPso);
        _commandList.Close();
    }

    private void BuildOctree()
    {
        if (_sceneObjects.Count == 0) return;

        var bounds = _sceneObjects[0].WorldBounds;
        foreach (var obj in _sceneObjects)
            bounds = bounds.Union(obj.WorldBounds);

        _octree = new Octree(bounds);
        for (int i = 0; i < _sceneObjects.Count; i++)
            _octree.Insert(i, _sceneObjects[i].WorldBounds);
    }

    private void SetupLights()
    {
        Lights.Clear();

        Lights.Add(new LightData
        {
            Direction = new Vector4(0.5f, -1f, 0.5f, 0),
            Color = new Vector4(1f, 0.95f, 0.8f, 0.8f),
        });

        Lights.Add(new LightData
        {
            Position = new Vector4(0, 22, 0, 30),
            Direction = new Vector4(0, -1, 0, 1),
            Color = new Vector4(1f, 0.5f, 0.2f, 3f),
        });

        Lights.Add(new LightData
        {
            Position = new Vector4(-10, 22, 0, 20),
            Direction = new Vector4(0, -1, 0, 1),
            Color = new Vector4(0.2f, 0.5f, 1f, 1.5f),
        });
        Lights.Add(new LightData
        {
            Position = new Vector4(-8, 22, 0, 25),
            Direction = new Vector4(0, -1, 0, 1),
            Color = new Vector4(1f, 0.85f, 0.6f, 2f),
        });
        Lights.Add(new LightData
        {
            Position = new Vector4(8, 22, 0, 25),
            Direction = new Vector4(0, -1, 0, 1),
            Color = new Vector4(1f, 0.85f, 0.6f, 2f),
        });
    }

    public void Render(double deltaTime)
    {
        try
        {
            _commandAllocators[_frameIndex].Reset();
            _commandList.Reset(_commandAllocators[_frameIndex], _geometryPso);

            GeometryPass();
            ShadowPass();

            _totalTime += (float)deltaTime;
            if (ParticlesEnabled)
                _particles.Update(_commandList, (float)deltaTime, _particleEmitterPos, _totalTime);

            LightingPass();
            PostProcessPass();
            _commandList.Close();

            _commandQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)_commandList });
            _swapChain.Present(1, PresentFlags.None);

            WaitForGpu();
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, ex.ToString(), "Render Error", 0x10 /* MB_ICONERROR */);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private void MoveToNextFrame()
    {
        ulong currentFenceValue = _fenceValues[_frameIndex];
        _commandQueue.Signal(_fence, currentFenceValue);

        _frameIndex = _swapChain.CurrentBackBufferIndex;

        if (_fence.CompletedValue < _fenceValues[_frameIndex])
        {
            _fence.SetEventOnCompletion(_fenceValues[_frameIndex], _fenceEvent);
            _fenceEvent.WaitOne();
        }

        _fenceValues[_frameIndex] = currentFenceValue + 1;
    }

    private void GeometryPass()
    {
        for (int i = 0; i < GBuffer.Count; i++)
            _commandList.ResourceBarrier(new ResourceBarrier(
                new ResourceTransitionBarrier(_gBuffer.RenderTargets[i],
                    ResourceStates.PixelShaderResource,
                    ResourceStates.RenderTarget)));

        var rtvHandle = _gBuffer.RtvHeap.GetCPUDescriptorHandleForHeapStart();
        var clearColor = new Color4(0, 0, 0, 1);
        for (int i = 0; i < GBuffer.Count; i++)
        {
            _commandList.ClearRenderTargetView(rtvHandle, clearColor);
            rtvHandle.Ptr += _gBuffer.RtvDescriptorSize;
        }

        var dsvHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        _commandList.ClearDepthStencilView(dsvHandle, ClearFlags.Depth, 1.0f, 0);

        var rtvHandles = new CpuDescriptorHandle[GBuffer.Count];
        var startRtv = _gBuffer.RtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < GBuffer.Count; i++)
        {
            rtvHandles[i] = startRtv;
            rtvHandles[i].Ptr += (uint)i * _gBuffer.RtvDescriptorSize;
        }
        _commandList.OMSetRenderTargets(rtvHandles, dsvHandle);
        _commandList.SetGraphicsRootSignature(_geometryRootSig);
        _commandList.SetPipelineState(WireframeEnabled ? _geometryWireframePso : _geometryPso);
        _commandList.RSSetViewport(new Viewport(0, 0, _width, _height));
        _commandList.RSSetScissorRect(new Vortice.RawRect(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.PatchListWith3ControlPoints);

        var view = Matrix4x4.CreateLookAt(CameraPos, CameraTarget, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, (float)_width / _height, 0.01f, 500f);
        var viewProj = view * proj;

        var world = Matrix4x4.Identity;
        Matrix4x4.Invert(world, out var worldInv);

        unsafe
        {
            _geometryCbData->WorldViewProj = Matrix4x4.Transpose(world * viewProj);
            _geometryCbData->World = Matrix4x4.Transpose(world);
            _geometryCbData->WorldInvTranspose = Matrix4x4.Transpose(worldInv);
            _geometryCbData->CameraPos = CameraPos;
            _geometryCbData->DisplacementScale = DisplacementScale;
            _geometryCbData->TessMin = TessMin;
            _geometryCbData->TessMax = TessMax;
            _geometryCbData->TessNearDist = TessNearDist;
            _geometryCbData->TessFarDist = TessFarDist;
        }

        _commandList.SetDescriptorHeaps(_geometryDescHeap);

        _commandList.SetGraphicsRootDescriptorTable(0,
            _geometryDescHeap.GetGPUDescriptorHandleForHeapStart());

        var globalDispHandle = _geometryDescHeap.GetGPUDescriptorHandleForHeapStart();
        globalDispHandle.Ptr += _globalTexHeapOffset * _geometryDescSize;

        var globalNormHandle = _geometryDescHeap.GetGPUDescriptorHandleForHeapStart();
        globalNormHandle.Ptr += (_globalTexHeapOffset + 1) * _geometryDescSize;

        foreach (var mesh in _model.Meshes)
        {
            var mat = _model.Materials[mesh.MaterialIndex];

            ID3D12Resource dispTexRes = _globalDisplacementTex;
            if (mat.HasDisplacement && _model.Textures.TryGetValue(mat.DisplacementTexturePath, out var matDispTex))
                dispTexRes = matDispTex;

            var dispDesc = dispTexRes.Description;

            unsafe
            {
                _geometryCbData->Roughness = mat.Roughness;
                _geometryCbData->Metallic = mat.Metallic;
                _geometryCbData->BumpStrength = BumpStrength;
                _geometryCbData->DisplacementMapTexelSize = new Vector2(
                    1.0f / dispDesc.Width, 1.0f / dispDesc.Height);
                _geometryCbData->HeightMid = HeightMid;
            }

            string diffKey = (!string.IsNullOrEmpty(mat.DiffuseTexturePath) &&
                  _textureIndices.ContainsKey(mat.DiffuseTexturePath))
                 ? mat.DiffuseTexturePath : _whiteTexPath;
            var hd = _geometryDescHeap.GetGPUDescriptorHandleForHeapStart();
            hd.Ptr += (1 + _textureIndices[diffKey]) * _geometryDescSize;
            _commandList.SetGraphicsRootDescriptorTable(1, hd);

            if (mat.HasDisplacement &&
                _textureIndices.TryGetValue(mat.DisplacementTexturePath, out uint dispSlot))
            {
                var h = _geometryDescHeap.GetGPUDescriptorHandleForHeapStart();
                h.Ptr += (1 + dispSlot) * _geometryDescSize;
                _commandList.SetGraphicsRootDescriptorTable(2, h);
            }
            else
            {
                _commandList.SetGraphicsRootDescriptorTable(2, globalDispHandle);
            }

            if (mat.HasNormalMap &&
                _textureIndices.TryGetValue(mat.NormalTexturePath, out uint normSlot))
            {
                var h = _geometryDescHeap.GetGPUDescriptorHandleForHeapStart();
                h.Ptr += (1 + normSlot) * _geometryDescSize;
                _commandList.SetGraphicsRootDescriptorTable(3, h);
            }
            else
            {
                _commandList.SetGraphicsRootDescriptorTable(3, globalNormHandle);
            }

            _commandList.IASetVertexBuffers(0, mesh.VertexBufferView);
            _commandList.IASetIndexBuffer(mesh.IndexBufferView);
            _commandList.DrawIndexedInstanced((uint)mesh.IndexCount, 1, 0, 0, 0);
        }

        DrawSceneObjects(viewProj);

        for (int i = 0; i < GBuffer.Count; i++)
            _commandList.ResourceBarrier(new ResourceBarrier(
                new ResourceTransitionBarrier(_gBuffer.RenderTargets[i],
                    ResourceStates.RenderTarget,
                    ResourceStates.PixelShaderResource)));
    }

    private void DrawSceneObjects(Matrix4x4 viewProj)
    {
        if (_sceneObjects.Count == 0) return;

        var frustum = Frustum.FromViewProjection(viewProj);

        _cullResults.Clear();
        if (!FrustumCullingEnabled)
        {
            for (int i = 0; i < _sceneObjects.Count; i++)
                _cullResults.Add(i);
        }
        else if (OctreeAccelerationEnabled)
        {
            _octree.Query(frustum, _cullResults);
        }
        else
        {
            for (int i = 0; i < _sceneObjects.Count; i++)
                if (frustum.Intersects(_sceneObjects[i].WorldBounds))
                    _cullResults.Add(i);
        }

        VisibleInstanceCount = _cullResults.Count;
        if (VisibleInstanceCount == 0)
            return;

        unsafe
        {
            *_instanceViewProjCbData = Matrix4x4.Transpose(viewProj);

            int count = Math.Min(VisibleInstanceCount, MaxInstances);
            for (int i = 0; i < count; i++)
            {
                var obj = _sceneObjects[_cullResults[i]];
                var world = obj.World;
                Matrix4x4.Invert(world, out var worldInv);

                _instanceDataPtr[i] = new InstanceGpuData
                {
                    World = world,
                    WorldInvTranspose = Matrix4x4.Transpose(worldInv),
                };
            }
            VisibleInstanceCount = count;
        }

        _commandList.SetPipelineState(_instancePso);
        _commandList.SetGraphicsRootSignature(_instanceRootSig);
        _commandList.SetDescriptorHeaps(_instanceDescHeap);

        var heapStart = _instanceDescHeap.GetGPUDescriptorHandleForHeapStart();
        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        _commandList.SetGraphicsRootDescriptorTable(0, heapStart);
        var diffuseHandle = heapStart; diffuseHandle.Ptr += descSize;
        _commandList.SetGraphicsRootDescriptorTable(1, diffuseHandle);
        var instHandle = heapStart; instHandle.Ptr += 2 * descSize;
        _commandList.SetGraphicsRootDescriptorTable(2, instHandle);

        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _commandList.IASetVertexBuffers(0, _cubeMesh.VertexBufferView);
        _commandList.IASetIndexBuffer(_cubeMesh.IndexBufferView);
        _commandList.DrawIndexedInstanced(
            (uint)_cubeMesh.IndexCount, (uint)VisibleInstanceCount, 0, 0, 0);
    }

    private void ShadowPass()
    {
        if (Lights.Count == 0) return;

        var sun = Lights[0];
        Vector3 lightDir = Vector3.Normalize(new Vector3(sun.Direction.X, sun.Direction.Y, sun.Direction.Z));

        var view = Matrix4x4.CreateLookAt(CameraPos, CameraTarget, Vector3.UnitY);
        float aspect = (float)_width / _height;
        float fovY = MathF.PI / 4f;

        _csm.ComputeCascades(lightDir, view, fovY, aspect, 0.1f, 60f, CascadeLambda);

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_csm.ShadowTexture,
                ResourceStates.PixelShaderResource, ResourceStates.DepthWrite)));

        _commandList.RSSetViewport(new Viewport(0, 0, CascadedShadowMap.ShadowMapSize, CascadedShadowMap.ShadowMapSize));
        _commandList.RSSetScissorRect(new Vortice.RawRect(0, 0, CascadedShadowMap.ShadowMapSize, CascadedShadowMap.ShadowMapSize));
        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        uint instDescSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        var instHeapStart = _instanceDescHeap.GetGPUDescriptorHandleForHeapStart();
        var instHandle = instHeapStart; instHandle.Ptr += 2 * instDescSize;

        for (int c = 0; c < CascadedShadowMap.CascadeCount; c++)
        {
            var dsv = _csm.GetDsv(c);
            _commandList.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
            _commandList.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), dsv);

            var lightViewProjT = Matrix4x4.Transpose(_csm.CascadeViewProj[c]);

            _commandList.SetPipelineState(_shadowStaticPso);
            _commandList.SetGraphicsRootSignature(_shadowStaticRootSig);
            unsafe { _commandList.SetGraphicsRoot32BitConstants(0, 16, &lightViewProjT, 0); }

            foreach (var mesh in _model.Meshes)
            {
                _commandList.IASetVertexBuffers(0, mesh.VertexBufferView);
                _commandList.IASetIndexBuffer(mesh.IndexBufferView);
                _commandList.DrawIndexedInstanced((uint)mesh.IndexCount, 1, 0, 0, 0);
            }

            if (VisibleInstanceCount > 0)
            {
                _commandList.SetPipelineState(_shadowInstancedPso);
                _commandList.SetGraphicsRootSignature(_shadowInstancedRootSig);
                unsafe { _commandList.SetGraphicsRoot32BitConstants(0, 16, &lightViewProjT, 0); }

                _commandList.SetDescriptorHeaps(_instanceDescHeap);
                _commandList.SetGraphicsRootDescriptorTable(1, instHandle);

                _commandList.IASetVertexBuffers(0, _cubeMesh.VertexBufferView);
                _commandList.IASetIndexBuffer(_cubeMesh.IndexBufferView);
                _commandList.DrawIndexedInstanced((uint)_cubeMesh.IndexCount, (uint)VisibleInstanceCount, 0, 0, 0);
            }
        }

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_csm.ShadowTexture,
                ResourceStates.DepthWrite, ResourceStates.PixelShaderResource)));
    }

    private void LightingPass()
    {
        var rtvHandle = _postProcess.SceneColorRtv;

        _postProcess.BeginScene(_commandList);

        _commandList.ClearRenderTargetView(rtvHandle, new Color4(0, 0, 0, 1));
        _commandList.OMSetRenderTargets(rtvHandle, null);
        _commandList.SetPipelineState(_lightingPso);
        _commandList.SetGraphicsRootSignature(_lightingRootSig);

        unsafe
        {
            _lightingCbData->CameraPos = new Vector4(CameraPos, 1);
            _lightingCbData->LightCount = Math.Min(Lights.Count, 16);
            for (int i = 0; i < _lightingCbData->LightCount; i++)
                _lightingCbData->Lights[i] = Lights[i];

            _lightingCbData->CascadeViewProj0 = Matrix4x4.Transpose(_csm.CascadeViewProj[0]);
            _lightingCbData->CascadeViewProj1 = Matrix4x4.Transpose(_csm.CascadeViewProj[1]);
            _lightingCbData->CascadeViewProj2 = Matrix4x4.Transpose(_csm.CascadeViewProj[2]);
            _lightingCbData->CascadeViewProj3 = Matrix4x4.Transpose(_csm.CascadeViewProj[3]);
            _lightingCbData->CascadeSplits = new Vector4(
                _csm.CascadeSplits[0], _csm.CascadeSplits[1], _csm.CascadeSplits[2], _csm.CascadeSplits[3]);
            _lightingCbData->ShadowMapSize = CascadedShadowMap.ShadowMapSize;
            _lightingCbData->ShadowsEnabled = ShadowsEnabled ? 1 : 0;
            _lightingCbData->GBufferViewMode = GBufferViewMode;
            _lightingCbData->PrefilteredMipCount = _prefilteredMipCount; // NEW
            _lightingCbData->IblIntensity = IblIntensity;                // NEW
        }

        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        _commandList.SetDescriptorHeaps(_lightingDescHeap);

        _commandList.SetGraphicsRootDescriptorTable(0,
            _lightingDescHeap.GetGPUDescriptorHandleForHeapStart());

        var cbvGpu = _lightingDescHeap.GetGPUDescriptorHandleForHeapStart();
        cbvGpu.Ptr += (uint)(GBuffer.Count + 4) * descSize; // NEW: was Count+1
        _commandList.SetGraphicsRootDescriptorTable(1, cbvGpu);

        _commandList.RSSetViewport(new Viewport(0, 0, _width, _height));
        _commandList.RSSetScissorRect(new Vortice.RawRect(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _commandList.DrawInstanced(3, 1, 0, 0);

        var particleDsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        _commandList.OMSetRenderTargets(rtvHandle, particleDsv);

        if (ParticlesEnabled)
        {
            var particleView = Matrix4x4.CreateLookAt(CameraPos, CameraTarget, Vector3.UnitY);
            var particleProj = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f, (float)_width / _height, 0.01f, 500f);
            var particleViewProj = particleView * particleProj;

            _particles.Render(_commandList, particleViewProj, CameraPos, CameraTarget);
        }

        _postProcess.EndScene(_commandList);
    }

    private void PostProcessPass()
    {
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle.Ptr += _frameIndex * _rtvDescriptorSize;

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_renderTargets[_frameIndex],
                ResourceStates.Present, ResourceStates.RenderTarget)));

        _postProcess.ToneMappingEnabled = ToneMappingEnabled;
        _postProcess.VignetteEnabled = VignetteEnabled;
        _postProcess.Exposure = Exposure;
        _postProcess.Render(_commandList, rtvHandle, _width, _height);

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_renderTargets[_frameIndex],
                ResourceStates.RenderTarget, ResourceStates.Present)));
    }

    private void WaitForGpu()
    {
        ulong value = _fenceValues[_frameIndex] + 1;
        _commandQueue.Signal(_fence, value);
        _fence.SetEventOnCompletion(value, _fenceEvent);
        _fenceEvent.WaitOne();
        _fenceValues[_frameIndex] = value;
        _frameIndex = _swapChain.CurrentBackBufferIndex;
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height) return;

        WaitForGpu();

        foreach (var rt in _renderTargets) rt?.Dispose();
        _depthBuffer?.Dispose();
        _gBuffer?.Dispose();

        _width = width;
        _height = height;

        _swapChain.ResizeBuffers((uint)FrameCount, (uint)_width, (uint)_height,
            Format.R8G8B8A8_UNorm, SwapChainFlags.None);
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        CreateRenderTargetViews();
        CreateDepthBuffer();

        _gBuffer = new GBuffer(_device, _width, _height);
        RefreshLightingGBufferDescriptors();

        _postProcess.Resize(_width, _height);

        for (int i = 0; i < FrameCount; i++)
            _fenceValues[i] = _fenceValues[_frameIndex];
    }

    public void Dispose()
    {
        WaitForGpu();
        unsafe
        {
            _geometryCb?.Unmap(0, null);
            _lightingCb?.Unmap(0, null);
            _instanceViewProjCb?.Unmap(0, null);
            _instanceDataBuffer?.Unmap(0, null);
        }
        _particles?.Dispose();
        _postProcess?.Dispose();
        _model?.Dispose();
        _globalDisplacementTex?.Dispose();
        _globalNormalTex?.Dispose();
        _irradianceMap?.Dispose();
        _prefilteredEnvMap?.Dispose();
        _brdfLutMap?.Dispose();
        _gBuffer?.Dispose();
        _geometryPso?.Dispose();
        _geometryWireframePso?.Dispose();
        _geometryRootSig?.Dispose();
        _lightingPso?.Dispose();
        _lightingRootSig?.Dispose();
        _geometryCb?.Dispose();
        _lightingCb?.Dispose();
        _geometryDescHeap?.Dispose();
        _lightingDescHeap?.Dispose();
        _cubeMesh?.Dispose();
        _cubeDiffuseTex?.Dispose();
        _instancePso?.Dispose();
        _instanceRootSig?.Dispose();
        _instanceViewProjCb?.Dispose();
        _instanceDataBuffer?.Dispose();
        _instanceDescHeap?.Dispose();
        _csm?.Dispose();
        _shadowStaticPso?.Dispose();
        _shadowStaticRootSig?.Dispose();
        _shadowInstancedPso?.Dispose();
        _shadowInstancedRootSig?.Dispose();
        _fence?.Dispose();
        _fenceEvent?.Dispose();
        _depthBuffer?.Dispose();
        _dsvHeap?.Dispose();
        foreach (var rt in _renderTargets) rt?.Dispose();
        _rtvHeap?.Dispose();
        _swapChain?.Dispose();
        _commandQueue?.Dispose();
        _commandList?.Dispose();
        foreach (var alloc in _commandAllocators) alloc?.Dispose();
        _device?.Dispose();
    }
}