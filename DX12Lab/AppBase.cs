namespace DX12Lab;

public abstract class AppBase
{
    protected Window Window;
    protected InputDevice Input;
    protected GameTimer Timer;

    protected AppBase(string title, int width, int height)
    {
        Window = new Window(title, width, height);
        Input = new InputDevice(Window);
        Timer = new GameTimer();

        Window.SetUpdateCallback(Update);
    }

    public void Run()
    {
        Timer.Start();
        Init();
        Window.RunMessageLoop();
        Shutdown();
    }

    private void Update()
    {
        Timer.Tick();
        Input.Update();

        if (Input.IsKeyDown(0x1B))
            Window.Close();

        OnUpdate(Timer.DeltaTime);
        OnRender();
    }

    protected abstract void Init();
    protected abstract void OnUpdate(double deltaTime);
    protected abstract void OnRender();
    protected abstract void Shutdown();
}