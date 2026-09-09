namespace DX12Lab;

public class DX12App : AppBase
{
    private RenderingSystem _renderer;
    private Camera _camera = new();
    private DebugMenu _menu;
    private double _titleTimer;
    private int _frameCount;

    public DX12App() : base("DX12 Lab", 1280, 720) { }

    protected override void Init()
    {
        _renderer = new RenderingSystem(Window.Hwnd, Window.Width, Window.Height);

        _renderer.DisplacementScale = 0.02f;
        _renderer.TessMax = 16f;

        _menu = new DebugMenu(Window, _renderer);

        Window.OnResize += (width, height) => _renderer.Resize(width, height);
    }

    protected override void OnUpdate(double deltaTime)
    {
        _camera.Update(Input, (float)deltaTime);
        _renderer.CameraPos = _camera.Position;
        _renderer.CameraTarget = _camera.Target;

        _frameCount++;
        _titleTimer += deltaTime;
        if (_titleTimer > 0.2)
        {
            double fps = _frameCount / _titleTimer;
            _menu.SetFps(fps);

            Window.SetTitle(
                $"DX12 Lab | Visible {_renderer.VisibleInstanceCount}/{_renderer.TotalInstanceCount}");

            _titleTimer = 0;
            _frameCount = 0;
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