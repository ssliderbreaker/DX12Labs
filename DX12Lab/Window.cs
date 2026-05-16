using System;
using System.Runtime.InteropServices;

namespace DX12Lab;

public class Window
{
    public IntPtr Hwnd { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool ShouldClose { get; private set; } = false;

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private const int WM_QUIT = 0x0012;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate _wndProcDelegate;

    public event Action<uint, IntPtr, IntPtr>? OnMessage;
    private Action? _updateCallback;
    public Window(string title, int width, int height)
    {
        Width = width;
        Height = height;
        _wndProcDelegate = WndProc;

        string className = "DX12LabWindow";

        var wc = new WNDCLASSEXW();
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>();
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = className;

        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0)
            throw new Exception($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        Hwnd = CreateWindowExW(
            0,
            className,
            title,
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT,
            width, height,
            IntPtr.Zero, IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero
        );

        if (Hwnd == IntPtr.Zero)
            throw new Exception($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
    }
    public void SetUpdateCallback(Action callback)
    {
        _updateCallback = callback;
    }

    public void Close()
    {
        ShouldClose = true;
    }

    protected virtual void OnUpdate()
    {
        _updateCallback?.Invoke();
    }

    public void RunMessageLoop()
    {
        while (!ShouldClose)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                if (msg.message == WM_QUIT)
                {
                    ShouldClose = true;
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            OnUpdate();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        OnMessage?.Invoke(msg, wParam, lParam);

        switch ((int)msg)
        {
            case WM_CLOSE:
                ShouldClose = true;
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}