using DX12Lab;

var window = new Window("DX12Lab", 1280, 720);
var input = new InputDevice(window);

window.SetUpdateCallback(() =>
{
    input.Update();

    if (input.IsKeyDown(0x1B))
        window.Close();
});

window.RunMessageLoop();