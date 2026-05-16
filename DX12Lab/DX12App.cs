namespace DX12Lab;

public class DX12App : AppBase
{
    private DX12Renderer _renderer;

    public DX12App() : base("DX12 Lab", 1280, 720) { }

    protected override void Init()
    {
        _renderer = new DX12Renderer(Window.Hwnd, Window.Width, Window.Height);
    }

    protected override void OnUpdate(double deltaTime) { }

    protected override void OnRender()
    {
        _renderer.Render(Timer.DeltaTime);
    }

    protected override void Shutdown()
    {
        _renderer.Dispose();
    }
}