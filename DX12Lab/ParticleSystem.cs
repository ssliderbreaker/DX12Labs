using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace DX12Lab;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuParticle
{
    public Vector3 Position;
    public float Life;
    public Vector3 Velocity;
    public float Age;
    public Vector4 Color;
    public float Size;
    public Vector3 Pad;
}

public class ParticleSystem : IDisposable
{
    private const float EmitRatePerSecond = 500f;
    private const float EmitRateVariance = 150f;
    private const int MaxEmitPerFrame = 2000;
    private const float Gravity = -9.8f;

    public int MaxParticles { get; }

    private readonly ID3D12Resource _particlePool;
    private readonly ID3D12Resource _deadListBuffer;
    private readonly ID3D12Resource _deadListCounter;
    private readonly ID3D12Resource _zeroUpload;

    private readonly ID3D12Resource _renderCb;
    private unsafe readonly RenderConstants* _renderCbData;

    private readonly ID3D12DescriptorHeap _heap;
    private readonly uint _descSize;

    private readonly ID3D12RootSignature _initRootSig;
    private readonly ID3D12PipelineState _initPso;

    private readonly ID3D12RootSignature _emitRootSig;
    private readonly ID3D12PipelineState _emitPso;

    private readonly ID3D12RootSignature _simulateRootSig;
    private readonly ID3D12PipelineState _simulatePso;

    private readonly ID3D12RootSignature _renderRootSig;
    private readonly ID3D12PipelineState _renderPso;

    private float _emitAccumulator;
    private readonly Random _rand = new();

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct RenderConstants
    {
        public Matrix4x4 ViewProj;
        public Vector3 CameraRight;
        public float _Pad0;
        public Vector3 CameraUp;
        public float _Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConstants
    {
        public float Dt;
        public float Gravity;
        public uint MaxParticles;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EmitConstants
    {
        public Vector3 EmitterPos;
        public float Time;
        public uint NumToEmit;
        public uint Seed;
    }

    public ParticleSystem(ID3D12Device device, ID3D12GraphicsCommandList cmd, int maxParticles)
    {
        MaxParticles = maxParticles;
        int particleStride = Marshal.SizeOf<GpuParticle>();

        _particlePool = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            ResourceDescription.Buffer((ulong)(particleStride * MaxParticles), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);

        _deadListBuffer = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            ResourceDescription.Buffer((ulong)(sizeof(uint) * MaxParticles), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);

        _deadListCounter = device.CreateCommittedResource(
            new HeapProperties(HeapType.Default), HeapFlags.None,
            ResourceDescription.Buffer(64, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);

        _zeroUpload = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer(4), ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            _zeroUpload.Map(0, null, &ptr);
            *(uint*)ptr = 0;
            _zeroUpload.Unmap(0, null);
        }

        _heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            4, DescriptorHeapFlags.ShaderVisible));
        _descSize = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        var heapStart = _heap.GetCPUDescriptorHandleForHeapStart();

        var poolUavHandle = heapStart;
        device.CreateUnorderedAccessView(_particlePool, null, new UnorderedAccessViewDescription
        {
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = (uint)MaxParticles,
                StructureByteStride = (uint)particleStride,
            }
        }, poolUavHandle);

        var deadUavHandle = heapStart; deadUavHandle.Ptr += _descSize;
        device.CreateUnorderedAccessView(_deadListBuffer, _deadListCounter, new UnorderedAccessViewDescription
        {
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = (uint)MaxParticles,
                StructureByteStride = sizeof(uint),
                CounterOffsetInBytes = 0,
            }
        }, deadUavHandle);

        var poolSrvHandle = heapStart; poolSrvHandle.Ptr += 2 * _descSize;
        device.CreateShaderResourceView(_particlePool, new ShaderResourceViewDescription
        {
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = (uint)MaxParticles,
                StructureByteStride = (uint)particleStride,
            }
        }, poolSrvHandle);

        int cbSize = (Marshal.SizeOf<RenderConstants>() + 255) & ~255;
        _renderCb = device.CreateCommittedResource(
            new HeapProperties(HeapType.Upload), HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        unsafe
        {
            void* ptr = null;
            _renderCb.Map(0, null, &ptr);
            _renderCbData = (RenderConstants*)ptr;
        }
        var cbvHandle = heapStart; cbvHandle.Ptr += 3 * _descSize;
        device.CreateConstantBufferView(
            new ConstantBufferViewDescription(_renderCb.GPUVirtualAddress, (uint)cbSize), cbvHandle);

        var uavRange = new RootParameter1(
            new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, 0)),
            ShaderVisibility.All);

        _initRootSig = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            new[] { uavRange, new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.All) }));

        _emitRootSig = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            new[] { uavRange, new RootParameter1(new RootConstants(0, 0, 6), ShaderVisibility.All) }));

        _simulateRootSig = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            new[] { uavRange, new RootParameter1(new RootConstants(0, 0, 3), ShaderVisibility.All) }));

        var cbvRange = new RootParameter1(
            new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ConstantBufferView, 1, 0)),
            ShaderVisibility.All);
        var srvRange = new RootParameter1(
            new RootDescriptorTable1(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0)),
            ShaderVisibility.All);

        _renderRootSig = device.CreateRootSignature(new RootSignatureDescription1(RootSignatureFlags.None,
            new[] { cbvRange, srvRange }));

        string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var initCs = CompileShader(Path.Combine(shaderDir, "particles_init.hlsl"), "CSMain", "cs_5_0");
        var emitCs = CompileShader(Path.Combine(shaderDir, "particles_emit.hlsl"), "CSMain", "cs_5_0");
        var simCs = CompileShader(Path.Combine(shaderDir, "particles_simulate.hlsl"), "CSMain", "cs_5_0");

        string renderPath = Path.Combine(shaderDir, "particles_render.hlsl");
        var renderVs = CompileShader(renderPath, "VSMain", "vs_5_0");
        var renderGs = CompileShader(renderPath, "GSMain", "gs_5_0");
        var renderPs = CompileShader(renderPath, "PSMain", "ps_5_0");

        _initPso = device.CreateComputePipelineState(new ComputePipelineStateDescription
        { RootSignature = _initRootSig, ComputeShader = initCs });
        _emitPso = device.CreateComputePipelineState(new ComputePipelineStateDescription
        { RootSignature = _emitRootSig, ComputeShader = emitCs });
        _simulatePso = device.CreateComputePipelineState(new ComputePipelineStateDescription
        { RootSignature = _simulateRootSig, ComputeShader = simCs });

        _renderPso = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _renderRootSig,
            VertexShader = renderVs,
            GeometryShader = renderGs,
            PixelShader = renderPs,
            InputLayout = new InputLayoutDescription(),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Point,
            RasterizerState = new RasterizerDescription(CullMode.None, FillMode.Solid),
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Format.R8G8B8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
        });

        cmd.CopyBufferRegion(_deadListCounter, 0, _zeroUpload, 0, 4);
        UavBarrier(cmd, _deadListCounter);

        cmd.SetDescriptorHeaps(_heap);
        var uavTable = _heap.GetGPUDescriptorHandleForHeapStart();

        cmd.SetPipelineState(_initPso);
        cmd.SetComputeRootSignature(_initRootSig);
        cmd.SetComputeRootDescriptorTable(0, uavTable);
        uint maxParticlesU = (uint)MaxParticles;
        unsafe { cmd.SetComputeRoot32BitConstants(1, 1, &maxParticlesU, 0); }
        cmd.Dispatch((uint)((MaxParticles + 63) / 64), 1, 1);

        UavBarrier(cmd, _particlePool);
        UavBarrier(cmd, _deadListBuffer);
    }

    public void Update(ID3D12GraphicsCommandList cmd, float dt, Vector3 emitterPos, float totalTime)
    {
        cmd.SetDescriptorHeaps(_heap);
        var uavTable = _heap.GetGPUDescriptorHandleForHeapStart();

        cmd.SetPipelineState(_simulatePso);
        cmd.SetComputeRootSignature(_simulateRootSig);
        cmd.SetComputeRootDescriptorTable(0, uavTable);
        var simConsts = new SimConstants { Dt = dt, Gravity = Gravity, MaxParticles = (uint)MaxParticles };
        unsafe { cmd.SetComputeRoot32BitConstants(1, 3, &simConsts, 0); }
        cmd.Dispatch((uint)((MaxParticles + 63) / 64), 1, 1);

        UavBarrier(cmd, _particlePool);
        UavBarrier(cmd, _deadListBuffer);

        _emitAccumulator += EmitRatePerSecond * dt + (RandFloat() * 2f - 1f) * EmitRateVariance * dt;
        int emitCount = Math.Max(0, (int)_emitAccumulator);
        emitCount = Math.Min(emitCount, MaxEmitPerFrame);
        _emitAccumulator -= emitCount;

        if (emitCount > 0)
        {
            cmd.SetPipelineState(_emitPso);
            cmd.SetComputeRootSignature(_emitRootSig);
            cmd.SetComputeRootDescriptorTable(0, uavTable);
            var emitConsts = new EmitConstants
            {
                EmitterPos = emitterPos,
                Time = totalTime,
                NumToEmit = (uint)emitCount,
                Seed = unchecked((uint)_rand.Next()) ^ (uint)(totalTime * 1000f),
            };
            unsafe { cmd.SetComputeRoot32BitConstants(1, 6, &emitConsts, 0); }
            cmd.Dispatch((uint)((emitCount + 63) / 64), 1, 1);

            UavBarrier(cmd, _particlePool);
            UavBarrier(cmd, _deadListBuffer);
        }
    }

    public void Render(ID3D12GraphicsCommandList cmd, Matrix4x4 viewProj, Vector3 cameraPos, Vector3 cameraTarget)
    {
        cmd.ResourceBarrier(new ResourceBarrier(new ResourceTransitionBarrier(_particlePool,
            ResourceStates.UnorderedAccess,
            ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource)));

        var forward = Vector3.Normalize(cameraTarget - cameraPos);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);

        unsafe
        {
            _renderCbData->ViewProj = Matrix4x4.Transpose(viewProj);
            _renderCbData->CameraRight = right;
            _renderCbData->CameraUp = up;
        }

        cmd.SetDescriptorHeaps(_heap);
        var heapStart = _heap.GetGPUDescriptorHandleForHeapStart();
        var cbvHandle = heapStart; cbvHandle.Ptr += 3 * _descSize;
        var srvHandle = heapStart; srvHandle.Ptr += 2 * _descSize;

        cmd.SetPipelineState(_renderPso);
        cmd.SetGraphicsRootSignature(_renderRootSig);
        cmd.SetGraphicsRootDescriptorTable(0, cbvHandle);
        cmd.SetGraphicsRootDescriptorTable(1, srvHandle);
        cmd.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.PointList);
        cmd.DrawInstanced((uint)MaxParticles, 1, 0, 0);

        cmd.ResourceBarrier(new ResourceBarrier(new ResourceTransitionBarrier(_particlePool,
            ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource,
            ResourceStates.UnorderedAccess)));
    }

    private float RandFloat() => (float)_rand.NextDouble();

    private static void UavBarrier(ID3D12GraphicsCommandList cmd, ID3D12Resource resource)
        => cmd.ResourceBarrier(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(resource)));

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
        unsafe { _renderCb?.Unmap(0, null); }
        _renderCb?.Dispose();
        _particlePool?.Dispose();
        _deadListBuffer?.Dispose();
        _deadListCounter?.Dispose();
        _zeroUpload?.Dispose();
        _heap?.Dispose();

        _initPso?.Dispose(); _initRootSig?.Dispose();
        _emitPso?.Dispose(); _emitRootSig?.Dispose();
        _simulatePso?.Dispose(); _simulateRootSig?.Dispose();
        _renderPso?.Dispose(); _renderRootSig?.Dispose();
    }
}