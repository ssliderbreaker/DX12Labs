using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
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
}

[StructLayout(LayoutKind.Sequential)]
public struct LightData
{
    public Vector4 Position;
    public Vector4 Direction;
    public Vector4 Color;
    public Vector4 SpotParams;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct LightingConstantBuffer
{
    public Vector4 CameraPos;
    public LightData Light0;
    public LightData Light1;
    public LightData Light2;
    public LightData Light3;
    public LightData Light4;
    public LightData Light5;
    public LightData Light6;
    public LightData Light7;
    public LightData Light8;
    public LightData Light9;
    public LightData Light10;
    public LightData Light11;
    public LightData Light12;
    public LightData Light13;
    public LightData Light14;
    public LightData Light15;
    public int LightCount;
    public Vector3 Padding;
}

public class RenderingSystem : IDisposable
{
    private const int FrameCount = 2;

    private ID3D12Device _device;
    private IDXGISwapChain3 _swapChain;
    private ID3D12CommandQueue _commandQueue;
    private ID3D12CommandAllocator _commandAllocator;
    private ID3D12GraphicsCommandList _commandList;

    private ID3D12DescriptorHeap _rtvHeap;
    private ID3D12Resource[] _renderTargets = new ID3D12Resource[FrameCount];
    private uint _rtvDescriptorSize;

    private ID3D12DescriptorHeap _dsvHeap;
    private ID3D12Resource _depthBuffer;

    private GBuffer _gBuffer;

    private ID3D12RootSignature _geometryRootSig;
    private ID3D12PipelineState _geometryPso;
    private ID3D12Resource _geometryCb;
    private unsafe GeometryConstantBuffer* _geometryCbData;

    private ID3D12DescriptorHeap _geometryDescHeap;
    private uint _geometryDescSize;

    private ID3D12RootSignature _lightingRootSig;
    private ID3D12PipelineState _lightingPso;
    private ID3D12Resource _lightingCb;
    private unsafe LightingConstantBuffer* _lightingCbData;
    private ID3D12DescriptorHeap _lightingDescHeap;

    private Dictionary<string, uint> _textureIndices = new();

    private ID3D12Fence _fence;
    private ulong _fenceValue;
    private EventWaitHandle _fenceEvent;

    private uint _frameIndex;
    private int _width, _height;

    private Model _model;
    private List<ID3D12Resource> _uploadBuffers = new();

    public Vector3 CameraPos { get; set; } = new Vector3(-10, 3, 0);
    public Vector3 CameraTarget { get; set; } = new Vector3(10, 3, 0);
    public List<LightData> Lights { get; } = new();

    public RenderingSystem(IntPtr hwnd, int width, int height)
    {
        _width = width;
        _height = height;
        Init(hwnd);
    }

    private void Init(IntPtr hwnd)
    {
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
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
            rtvHandle.Ptr += _rtvDescriptorSize;
        }

        _dsvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
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

        _gBuffer = new GBuffer(_device, _width, _height);

        _fence = _device.CreateFence(0);
        _fenceValue = 1;
        _fenceEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        CreateGeometryPass();
        CreateLightingPass();

        _commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _commandAllocator, _geometryPso);

        LoadScene();
        SetupLights();
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

        _geometryRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler }));

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "geometry_pass.hlsl");
        var vs = CompileShader(shaderPath, "VSMain", "vs_5_0");
        var ps = CompileShader(shaderPath, "PSMain", "ps_5_0");

        _geometryPso = _device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = _geometryRootSig,
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
                RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid),
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

        _geometryDescSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        _geometryDescHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            129, DescriptorHeapFlags.ShaderVisible));

        _device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_geometryCb.GPUVirtualAddress, (uint)cbSize),
            _geometryDescHeap.GetCPUDescriptorHandleForHeapStart());
    }

    private void CreateLightingPass()
    {
        var rootParams = new RootParameter1[]
        {
            new RootParameter1(
                new RootDescriptorTable1(new DescriptorRange1(
                    DescriptorRangeType.ShaderResourceView, 3, 0)),
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

        _lightingRootSig = _device.CreateRootSignature(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams, new[] { sampler }));

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
            4, DescriptorHeapFlags.ShaderVisible));

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

        _device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_lightingCb.GPUVirtualAddress, (uint)cbSize),
            dstHandle);
    }

    private void LoadScene()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sponza", "sponza.obj");
        _model = ModelLoader.Load(_device, _commandList, path, _uploadBuffers);

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

        _commandList.Close();
        _commandQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)_commandList });
        WaitForGpu();

        foreach (var buf in _uploadBuffers) buf?.Dispose();
        _uploadBuffers.Clear();

        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator, _geometryPso);
        _commandList.Close();
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
            Position = new Vector4(0, 10, 0, 30), 
            Direction = new Vector4(0, -1, 0, 1),        
            Color = new Vector4(1f, 0.5f, 0.2f, 3f),
        });

        Lights.Add(new LightData
        {
            Position = new Vector4(-10, 5, 0, 20),
            Direction = new Vector4(0, -1, 0, 1),
            Color = new Vector4(0.2f, 0.5f, 1f, 1.5f),
        });
    }

    public void Render(double deltaTime)
    {
        WaitForGpu();
        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator, _geometryPso);

        try
        {
            GeometryPass();
            LightingPass();
            _commandList.Close();
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(ex.ToString(), "Render Error");
            return;
        }

        _commandQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)_commandList });
        _swapChain.Present(1, PresentFlags.None);
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
        _commandList.SetPipelineState(_geometryPso);
        _commandList.RSSetViewport(new Viewport(0, 0, _width, _height));
        _commandList.RSSetScissorRect(new Vortice.RawRect(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        var view = Matrix4x4.CreateLookAt(CameraPos, CameraTarget, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, (float)_width / _height, 0.01f, 500f);
        unsafe
        {
            _geometryCbData->WorldViewProj = Matrix4x4.Transpose(Matrix4x4.Identity * view * proj);
            _geometryCbData->World = Matrix4x4.Transpose(Matrix4x4.Identity);
        }

        _commandList.SetDescriptorHeaps(_geometryDescHeap);

        _commandList.SetGraphicsRootDescriptorTable(0,
            _geometryDescHeap.GetGPUDescriptorHandleForHeapStart());

        foreach (var mesh in _model.Meshes)
        {
            var mat = _model.Materials[mesh.MaterialIndex];

            if (!string.IsNullOrEmpty(mat.DiffuseTexturePath) &&
                _textureIndices.TryGetValue(mat.DiffuseTexturePath, out uint texSlot))
            {
                var gpuHandle = _geometryDescHeap.GetGPUDescriptorHandleForHeapStart();
                gpuHandle.Ptr += (1 + texSlot) * _geometryDescSize;
                _commandList.SetGraphicsRootDescriptorTable(1, gpuHandle);
            }

            _commandList.IASetVertexBuffers(0, mesh.VertexBufferView);
            _commandList.IASetIndexBuffer(mesh.IndexBufferView);
            _commandList.DrawIndexedInstanced((uint)mesh.IndexCount, 1, 0, 0, 0);
        }

        for (int i = 0; i < GBuffer.Count; i++)
            _commandList.ResourceBarrier(new ResourceBarrier(
                new ResourceTransitionBarrier(_gBuffer.RenderTargets[i],
                    ResourceStates.RenderTarget,
                    ResourceStates.PixelShaderResource)));
    }

    private void LightingPass()
    {
        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle.Ptr += _frameIndex * _rtvDescriptorSize;

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_renderTargets[_frameIndex],
                ResourceStates.Present, ResourceStates.RenderTarget)));

        _commandList.ClearRenderTargetView(rtvHandle, new Color4(0, 0, 0, 1));
        _commandList.OMSetRenderTargets(rtvHandle, null);
        _commandList.SetPipelineState(_lightingPso);
        _commandList.SetGraphicsRootSignature(_lightingRootSig);

        unsafe
        {
            _lightingCbData->CameraPos = new Vector4(CameraPos, 1);
            _lightingCbData->LightCount = Math.Min(Lights.Count, 16);
            LightData* lightsPtr = &_lightingCbData->Light0;
            for (int i = 0; i < _lightingCbData->LightCount; i++)
                lightsPtr[i] = Lights[i];
        }

        uint descSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        _commandList.SetDescriptorHeaps(_lightingDescHeap);

        _commandList.SetGraphicsRootDescriptorTable(0,
            _lightingDescHeap.GetGPUDescriptorHandleForHeapStart());

        var cbvGpu = _lightingDescHeap.GetGPUDescriptorHandleForHeapStart();
        cbvGpu.Ptr += (uint)GBuffer.Count * descSize;
        _commandList.SetGraphicsRootDescriptorTable(1, cbvGpu);

        _commandList.RSSetViewport(new Viewport(0, 0, _width, _height));
        _commandList.RSSetScissorRect(new Vortice.RawRect(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _commandList.DrawInstanced(3, 1, 0, 0);

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(_renderTargets[_frameIndex],
                ResourceStates.RenderTarget, ResourceStates.Present)));
    }

    private void WaitForGpu()
    {
        _commandQueue.Signal(_fence, _fenceValue);
        _fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
        _fenceEvent.WaitOne();
        _fenceValue++;
        _frameIndex = _swapChain.CurrentBackBufferIndex;
    }

    public void Dispose()
    {
        WaitForGpu();
        unsafe
        {
            _geometryCb?.Unmap(0, null);
            _lightingCb?.Unmap(0, null);
        }
        _model?.Dispose();
        _gBuffer?.Dispose();
        _geometryPso?.Dispose();
        _geometryRootSig?.Dispose();
        _lightingPso?.Dispose();
        _lightingRootSig?.Dispose();
        _geometryCb?.Dispose();
        _lightingCb?.Dispose();
        _geometryDescHeap?.Dispose();
        _lightingDescHeap?.Dispose();
        _fence?.Dispose();
        _fenceEvent?.Dispose();
        _depthBuffer?.Dispose();
        _dsvHeap?.Dispose();
        foreach (var rt in _renderTargets) rt?.Dispose();
        _rtvHeap?.Dispose();
        _swapChain?.Dispose();
        _commandQueue?.Dispose();
        _commandList?.Dispose();
        _commandAllocator?.Dispose();
        _device?.Dispose();
    }
}