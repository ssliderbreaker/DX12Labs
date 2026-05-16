using System;
using System.Threading;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DX12Lab;

public class Renderer : IDisposable
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

    private ID3D12Fence _fence;
    private ulong _fenceValue;
    private EventWaitHandle _fenceEvent;

    private uint _frameIndex;
    private int _width, _height;

    public Renderer(IntPtr hwnd, int width, int height)
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

        var queueDesc = new CommandQueueDescription(CommandListType.Direct);
        _commandQueue = _device.CreateCommandQueue(queueDesc);

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

        using var swapChain1 = factory.CreateSwapChainForHwnd(
            _commandQueue, hwnd, swapChainDesc);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
        _frameIndex = _swapChain.CurrentBackBufferIndex;

        var rtvHeapDesc = new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, FrameCount);
        _rtvHeap = _device.CreateDescriptorHeap(rtvHeapDesc);
        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.RenderTargetView);

        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int i = 0; i < FrameCount; i++)
        {
            _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
            _device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
            rtvHandle.Ptr += _rtvDescriptorSize;
        }

        _commandAllocator = _device.CreateCommandAllocator(CommandListType.Direct);
        _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _commandAllocator);
        _commandList.Close();

        _fence = _device.CreateFence(0);
        _fenceValue = 1;
        _fenceEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
    }

    public void ClearScreen(float r, float g, float b)
    {
        _commandAllocator.Reset();
        _commandList.Reset(_commandAllocator);

        _commandList.ResourceBarrier(new ResourceBarrier(
            new ResourceTransitionBarrier(
                _renderTargets[_frameIndex],
                ResourceStates.Present,
                ResourceStates.RenderTarget)));

        var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        rtvHandle.Ptr += _frameIndex * _rtvDescriptorSize;

        _commandList.ClearRenderTargetView(rtvHandle, new Color4(r, g, b, 1.0f));

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
        _fenceEvent.Dispose();
        _fence.Dispose();
        _commandList.Dispose();
        _commandAllocator.Dispose();
        foreach (var rt in _renderTargets) rt.Dispose();
        _rtvHeap.Dispose();
        _swapChain.Dispose();
        _commandQueue.Dispose();
        _device.Dispose();
    }
}