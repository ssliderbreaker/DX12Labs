using System;
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

public class DX12Renderer : IDisposable
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

    private ID3D12DescriptorHeap _cbvHeap;
    private ID3D12Resource _constantBuffer;
    private unsafe ConstantBufferData* _cbvDataBegin;

    private ID3D12RootSignature _rootSignature;
    private ID3D12PipelineState _pipelineState;
    private MeshGeometry _cubeMesh;

    private ID3D12Fence _fence;
    private ulong _fenceValue;
    private EventWaitHandle _fenceEvent;

    private uint _frameIndex;
    private int _width, _height;

    private float _rotationAngle = 0f;

    public DX12Renderer(IntPtr hwnd, int width, int height)
    {
        _width = width;
        _height = height;
        InitDX12(hwnd);
    }

    private void InitDX12(IntPtr hwnd)
    {
#if DEBUG
        if (D3D12.D3D12GetDebugInterface<ID3D12Debug>(out var debug).Success)
            debug!.EnableDebugLayer();
#endif
        DXGI.CreateDXGIFactory2(false, out IDXGIFactory4 factory);
        D3D12.D3D12CreateDevice(null, Vortice.Direct3D.FeatureLevel.Level_12_0, out _device);

        _commandQueue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));

        var swapChainDesc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = FrameCount,
            SwapEffect = SwapEffect.FlipDiscard,
        };
        using var swapChain1 = factory.CreateSwapChainForHwnd(_commandQueue, hwnd, swapChainDesc);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        // RTV Heap
        _rtvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
            rtvHandle.Ptr += _rtvDescriptorSize;
        }

        // CBV Heap
        _cbvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            1, DescriptorHeapFlags.ShaderVisible));

        // Constant Buffer
        int cbSize = (Marshal.SizeOf<ConstantBufferData>() + 255) & ~255; // выравнивание 256 байт
        _constantBuffer = _device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload),
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize),
            ResourceStates.GenericRead);

        var cbvDesc = new ConstantBufferViewDescription(
            _constantBuffer.GPUVirtualAddress, (uint)cbSize);
        _device.CreateConstantBufferView(cbvDesc, _cbvHeap.GetCPUDescriptorHandleForHeapStart());

        unsafe
        {
            void* ptr = null;
            _constantBuffer.Map(0, null, &ptr);
            _cbvDataBegin = (ConstantBufferData*)ptr;
        }

        // Root Signature
        var rootParam = new RootParameter1(
            new RootDescriptorTable1(new DescriptorRange1(
                DescriptorRangeType.ConstantBufferView, 1, 0)),
            ShaderVisibility.All);

        var rootSigDesc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { rootParam });

        _rootSignature = _device.CreateRootSignature(rootSigDesc);

        // Компиляция шейдеров
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "shaders.hlsl");
        Compiler.CompileFromFile(shaderPath, null, null, "VSMain", "vs_5_0",
            ShaderFlags.Debug, out var vsByteCode, out var vsErrors);
        Compiler.CompileFromFile(shaderPath, null, null, "PSMain", "ps_5_0",
            ShaderFlags.Debug, out var psByteCode, out var psErrors);

        if (vsErrors != null) throw new Exception("VS error: " + vsErrors.AsString());
        if (psErrors != null) throw new Exception("PS error: " + psErrors.AsString());

        byte[] vsBytes = new byte[vsByteCode.BufferSize];
        System.Runtime.InteropServices.Marshal.Copy(vsByteCode.BufferPointer, vsBytes, 0, vsBytes.Length);

        byte[] psBytes = new byte[psByteCode.BufferSize];
        System.Runtime.InteropServices.Marshal.Copy(psByteCode.BufferPointer, psBytes, 0, psBytes.Length);

        // Input Layout — описываем структуру вершины
        var inputLayout = new InputLayoutDescription(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,    0, 0),
            new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float,   12, 0),
            new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 24, 0),
        });

        // PSO — Pipeline State Object
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = vsBytes,
            PixelShader = psBytes,
            InputLayout = inputLayout,
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = new RasterizerDescription(CullMode.Front, FillMode.Solid),
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Format.R8G8B8A8_UNorm },
            SampleDescription = new SampleDescription(1, 0),
        };
        _pipelineState = _device.CreateGraphicsPipelineState(psoDesc);

        _commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _commandAllocator, _pipelineState);
        _commandList.Close();

        _fence = _device.CreateFence(0);
        _fenceValue = 1;
        _fenceEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        // Геометрия куба
        _cubeMesh = MeshGeometry.CreateCube(_device);
    }

    public void Render(double deltaTime)
    {
        _rotationAngle += (float)deltaTime * 1.0f;

        // Матрицы трансформации
        var world = Matrix4x4.CreateRotationY(_rotationAngle) *
                    Matrix4x4.CreateRotationX(_rotationAngle * 0.5f);

        var view = Matrix4x4.CreateLookAt(
            new Vector3(0, 2, -5),
            Vector3.Zero,
            Vector3.UnitY);

        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f,
            (float)_width / _height,
            0.1f, 100f);

        // Обновляем константный буфер
        unsafe
        {
            _cbvDataBegin->WorldViewProj = Matrix4x4.Transpose(world * view * proj);
            _cbvDataBegin->World = Matrix4x4.Transpose(world);
            _cbvDataBegin->LightDir = new Vector4(1, -1, 1, 0);
            _cbvDataBegin->LightColor = new Vector4(1, 1, 1, 1);
            _cbvDataBegin->AmbientColor = new Vector4(0.1f, 0.1f, 0.1f, 1);
        }

        // Запись команд
        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator, _pipelineState);

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(
                _renderTargets[_frameIndex],
                ResourceStates.Present,
                ResourceStates.RenderTarget)));

        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle.Ptr += _frameIndex * _rtvDescriptorSize;

        _commandList.ClearRenderTargetView(rtvHandle, new Color4(0.1f, 0.1f, 0.2f, 1f));
        _commandList.OMSetRenderTargets(rtvHandle, null);

        // Настройка пайплайна
        _commandList.SetGraphicsRootSignature(_rootSignature);
        _commandList.SetDescriptorHeaps(_cbvHeap);
        _commandList.SetGraphicsRootDescriptorTable(0,
            _cbvHeap.GetGPUDescriptorHandleForHeapStart());

        _commandList.RSSetViewport(new Viewport(0, 0, _width, _height));
        _commandList.RSSetScissorRect(new Vortice.RawRect(0, 0, _width, _height));
        _commandList.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _commandList.IASetVertexBuffers(0, _cubeMesh.VertexBufferView);
        _commandList.IASetIndexBuffer(_cubeMesh.IndexBufferView);

        _commandList.DrawIndexedInstanced((uint)_cubeMesh.IndexCount, 1, 0, 0, 0);

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(
                _renderTargets[_frameIndex],
                ResourceStates.RenderTarget,
                ResourceStates.Present)));

        _commandList.Close();
        _commandQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)_commandList });
        _swapChain.Present(1, PresentFlags.None);
        WaitForGpu();
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
        unsafe { _constantBuffer.Unmap(0, null); }
        _fenceEvent.Dispose();
        _fence.Dispose();
        _cubeMesh.Dispose();
        _pipelineState.Dispose();
        _rootSignature.Dispose();
        _constantBuffer.Dispose();
        _cbvHeap.Dispose();
        _commandList.Dispose();
        _commandAllocator.Dispose();
        foreach (var rt in _renderTargets) rt.Dispose();
        _rtvHeap.Dispose();
        _swapChain.Dispose();
        _commandQueue.Dispose();
        _device.Dispose();
    }
}