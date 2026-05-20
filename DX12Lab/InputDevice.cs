using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DX12Lab;

public class InputDevice
{
    private readonly Window _window;
    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<int> _keysPressed = new(); 
    private int _mouseX, _mouseY;
    private bool _leftButton, _rightButton;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
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
    }

    public bool IsKeyDown(int vkCode) => _keysDown.Contains(vkCode);
    public bool IsKeyPressed(int vkCode) => _keysPressed.Contains(vkCode);

    private void HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_KEYDOWN:
                int key = (int)wParam;
                if (!_keysDown.Contains(key))
                    _keysPressed.Add(key);
                _keysDown.Add(key);
                break;

            case WM_KEYUP:
                _keysDown.Remove((int)wParam);
                break;

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
}