namespace DX12Lab;

public class Lab2App : AppBase
{
    private Renderer _renderer;

    public Lab2App() : base("DX12 Lab 2", 1280, 720) { }

    protected override void Init()
    {
        _renderer = new Renderer(Window.Hwnd, Window.Width, Window.Height);
    }

    protected override void OnUpdate(double deltaTime) { }

    protected override void OnRender()
    {
        _renderer.ClearScreen(0.1f, 0.2f, 0.4f);
    }

    protected override void Shutdown()
    {
        _renderer.Dispose();
    }
}