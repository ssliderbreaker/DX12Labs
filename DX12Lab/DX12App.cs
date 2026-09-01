namespace DX12Lab;

public class DX12App : AppBase
{
    private RenderingSystem _renderer;
    private Camera _camera = new();
    private double _titleTimer;

    public DX12App() : base("DX12 Lab", 1280, 720) { }

    protected override void Init()
    {
        _renderer = new RenderingSystem(Window.Hwnd, Window.Width, Window.Height);

        _renderer.DisplacementScale = 0.02f;
        _renderer.TessMax = 16f;

        Window.OnResize += (width, height) => _renderer.Resize(width, height);
    }

    protected override void OnUpdate(double deltaTime)
    {
        _camera.Update(Input, (float)deltaTime);
        _renderer.CameraPos = _camera.Position;
        _renderer.CameraTarget = _camera.Target;

        if (Input.IsKeyPressed(0x70))
            _renderer.FrustumCullingEnabled = !_renderer.FrustumCullingEnabled;

        if (Input.IsKeyPressed(0x71))
            _renderer.OctreeAccelerationEnabled = !_renderer.OctreeAccelerationEnabled;

        if (Input.IsKeyPressed(0x72))
            _renderer.ShadowsEnabled = !_renderer.ShadowsEnabled;

        _titleTimer += deltaTime;
        if (_titleTimer > 0.2)
        {
            _titleTimer = 0;
            string mode = !_renderer.FrustumCullingEnabled ? "Culling: OFF"
                : _renderer.OctreeAccelerationEnabled ? "Culling: Frustum+Octree"
                : "Culling: Frustum (brute-force)";
            string shadows = _renderer.ShadowsEnabled ? "Shadows: ON" : "Shadows: OFF";
            Window.SetTitle(
                $"DX12 Lab | {mode} | {shadows} | Visible {_renderer.VisibleInstanceCount}/{_renderer.TotalInstanceCount}");
        }
    }

    protected override void OnRender()
    {
        _renderer.Render(Timer.DeltaTime);
    }

    protected override void Shutdown()
    {
        _renderer.Dispose();
    }
}