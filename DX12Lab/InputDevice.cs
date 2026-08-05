using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DX12Lab;

public class InputDevice
{
    private readonly Window _window;
    private readonly HashSet<int> _keysDownPrev = new();
    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<int> _keysPressed = new();
    private int _mouseX, _mouseY;
    private bool _leftButton, _rightButton;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;

    public int MouseX => _mouseX;
    public int MouseY => _mouseY;
    public bool LeftButton => _leftButton;
    public bool RightButton => _rightButton;

    public InputDevice(Window window)
    {
        _window = window;
        window.OnMessage += HandleMessage;
    }

    public void Update()
    {
        _keysPressed.Clear();
        _keysDownPrev.Clear();
        _keysDownPrev.UnionWith(_keysDown);
        _keysDown.Clear();

        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                _keysDown.Add(vk);
                if (!_keysDownPrev.Contains(vk))
                    _keysPressed.Add(vk);
            }
        }
    }

    public bool IsKeyDown(int vkCode) => _keysDown.Contains(vkCode);
    public bool IsKeyPressed(int vkCode) => _keysPressed.Contains(vkCode);

    private void HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_MOUSEMOVE:
                _mouseX = (int)lParam & 0xFFFF;
                _mouseY = ((int)lParam >> 16) & 0xFFFF;
                break;

            case WM_LBUTTONDOWN: _leftButton = true; break;
            case WM_LBUTTONUP: _leftButton = false; break;
            case WM_RBUTTONDOWN: _rightButton = true; break;
            case WM_RBUTTONUP: _rightButton = false; break;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}