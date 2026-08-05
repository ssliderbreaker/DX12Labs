namespace DX12Lab;

public class DX12App : AppBase
{
    private RenderingSystem _renderer;
    private Camera _camera = new();

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