using System;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

public class GBuffer : IDisposable
{
    public const int Count = 3;

    public static readonly Format[] Formats = new[]
    {
        Format.R32G32B32A32_Float, 
        Format.R32G32B32A32_Float, 
        Format.R8G8B8A8_UNorm,     
    };

    private ID3D12Device _device;

    public ID3D12Resource[] RenderTargets { get; } = new ID3D12Resource[Count];

    public ID3D12DescriptorHeap RtvHeap { get; private set; }
    public uint RtvDescriptorSize { get; private set; }

    public ID3D12DescriptorHeap SrvHeap { get; private set; }
    public uint SrvDescriptorSize { get; private set; }
    public ID3D12DescriptorHeap SrvCpuHeap { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public GBuffer(ID3D12Device device, int width, int height)
    {
        _device = device;
        Width = width;
        Height = height;
        Create();
    }

    private void Create()
    {
        RtvHeap = _device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, Count));
        RtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.RenderTargetView);

        SrvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            Count, DescriptorHeapFlags.ShaderVisible));
        SrvDescriptorSize = _device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        SrvCpuHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            Count)); 

        var rtvHandle = RtvHeap.GetCPUDescriptorHandleForHeapStart();
        var srvHandle = SrvHeap.GetCPUDescriptorHandleForHeapStart();
        var srvCpuHandle = SrvCpuHeap.GetCPUDescriptorHandleForHeapStart();

        for (int i = 0; i < Count; i++)
        {
            RenderTargets[i] = _device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Texture2D(
                    Formats[i],
                    (uint)Width, (uint)Height,
                    1, 1, 1, 0,
                    ResourceFlags.AllowRenderTarget),
                ResourceStates.PixelShaderResource,
                new ClearValue(Formats[i], new Vortice.Mathematics.Color4(0, 0, 0, 1)));

            _device.CreateRenderTargetView(RenderTargets[i], null, rtvHandle);
            rtvHandle.Ptr += RtvDescriptorSize;

            var srvDesc = new ShaderResourceViewDescription
            {
                Format = Formats[i],
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            };

            _device.CreateShaderResourceView(RenderTargets[i], srvDesc, srvHandle);
            srvHandle.Ptr += SrvDescriptorSize;

            _device.CreateShaderResourceView(RenderTargets[i], srvDesc, srvCpuHandle);
            srvCpuHandle.Ptr += SrvDescriptorSize;
        }
    }

    public void TransitionTo(ID3D12GraphicsCommandList cmd, ResourceStates state)
    {
        for (int i = 0; i < Count; i++)
            cmd.ResourceBarrier(new ResourceBarrier(
                new ResourceTransitionBarrier(RenderTargets[i],
                    ResourceStates.PixelShaderResource, state)));
    }

    public void TransitionFrom(ID3D12GraphicsCommandList cmd, ResourceStates state)
    {
        for (int i = 0; i < Count; i++)
            cmd.ResourceBarrier(new ResourceBarrier(
                new ResourceTransitionBarrier(RenderTargets[i],
                    state, ResourceStates.PixelShaderResource)));
    }

    public void Dispose()
    {
        foreach (var rt in RenderTargets) rt?.Dispose();
        RtvHeap?.Dispose();
        SrvHeap?.Dispose();
        SrvCpuHeap?.Dispose();
    }
}