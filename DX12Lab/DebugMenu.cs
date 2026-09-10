using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DX12Lab;

public class DebugMenu
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint BS_AUTOCHECKBOX = 0x00000003;
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_HSCROLL = 0x0114;
    private const uint WM_MOVE = 0x0003;
    private const uint WM_DESTROY = 0x0002;
    private const int BM_SETCHECK = 0x00F1;
    private const int BM_GETCHECK = 0x00F0;
    private const int BST_CHECKED = 1;
    private const int BST_UNCHECKED = 0;
    private const uint TBS_HORZ = 0x0000;
    private const uint TBS_NOTICKS = 0x0010;
    private const int TBM_SETRANGE = 0x0406;
    private const int TBM_SETPOS = 0x0405;
    private const int TBM_GETPOS = 0x0400;
    private const int DEFAULT_GUI_FONT = 17;
    private const int COLOR_BTNFACE = 15;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int CollapsedHeight = 44;

    private const int Margin = 12;
    private const int PanelWidth = 224;

    private readonly IntPtr _mainHwnd;
    private readonly RenderingSystem _renderer;
    private readonly IntPtr _font;
    private IntPtr _panel;
    private IntPtr _labelFps;
    private IntPtr _btnCollapse;
    private bool _collapsed;
    private int _expandedHeight;
    private readonly List<IntPtr> _collapsibleHandles = new();

    private WndProcDelegate _panelWndProc;
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private class CheckboxBinding
    {
        public IntPtr Handle;
        public Action<bool> Apply;
    }

    private class SliderBinding
    {
        public IntPtr Trackbar;
        public IntPtr Label;
        public string Name;
        public Action<int> Apply;
        public Func<int, string> Format;
    }

    private readonly List<CheckboxBinding> _checkboxes = new();
    private readonly List<SliderBinding> _sliders = new();
    private int _nextControlId = 3000;

    public DebugMenu(Window window, RenderingSystem renderer)
    {
        _mainHwnd = window.Hwnd;
        _renderer = renderer;
        _font = GetStockObject(DEFAULT_GUI_FONT);
        _panelWndProc = PanelWndProc;

        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC_BAR_CLASSES
        };
        InitCommonControlsEx(ref icc);

        RegisterPanelClass();
        CreatePanel();
        CreateControls();

        window.OnMessage += (msg, wParam, lParam) =>
        {
            if (msg == WM_MOVE)
                RepositionPanel();
        };
    }

    public void SetFps(double fps)
    {
        SetWindowTextW(_labelFps, $"FPS: {fps:0}");
    }


    private void CreateControls()
    {
        const int x = 10;
        const int width = 200;
        int y = 10;

        _labelFps = CreateLabel("FPS: --", x, y, 130);
        _btnCollapse = CreateButton("\u25B2 Hide", x + 132, y - 2, 70, 22);
        y += 24;

        AddCheckbox("Frustum Culling", x, ref y, width,
            _renderer.FrustumCullingEnabled, v => _renderer.FrustumCullingEnabled = v);
        AddCheckbox("Octree Acceleration", x, ref y, width,
            _renderer.OctreeAccelerationEnabled, v => _renderer.OctreeAccelerationEnabled = v);

        AddCheckbox("Shadows", x, ref y, width,
            _renderer.ShadowsEnabled, v => _renderer.ShadowsEnabled = v);

        AddCheckbox("Wireframe", x, ref y, width,
            _renderer.WireframeEnabled, v => _renderer.WireframeEnabled = v);

        AddCheckbox("Particles", x, ref y, width,
            _renderer.ParticlesEnabled, v => _renderer.ParticlesEnabled = v);

        AddCheckbox("Tone Mapping", x, ref y, width,
            _renderer.ToneMappingEnabled, v => _renderer.ToneMappingEnabled = v);
        AddCheckbox("Vignette", x, ref y, width,
            _renderer.VignetteEnabled, v => _renderer.VignetteEnabled = v);

        y += 10;

        AddSlider("Exposure", x, ref y, width,
            0, 300, (int)Math.Round(_renderer.Exposure * 100f),
            pos => _renderer.Exposure = pos / 100f,
            pos => $"Exposure: {pos / 100f:0.00}");

        AddSlider("IBL Intensity", x, ref y, width,
            0, 300, (int)Math.Round(_renderer.IblIntensity * 100f),
            pos => _renderer.IblIntensity = pos / 100f,
            pos => $"IBL Intensity: {pos / 100f:0.00}");

        AddSlider("Displacement Scale", x, ref y, width,
            0, 100, (int)Math.Round(_renderer.DisplacementScale * 1000f),
            pos => _renderer.DisplacementScale = pos / 1000f,
            pos => $"Displacement: {pos / 1000f:0.000}");

        AddSlider("Tess Min", x, ref y, width,
            1, 16, (int)Math.Round(_renderer.TessMin),
            pos => _renderer.TessMin = pos,
            pos => $"Tess Min: {pos}");

        AddSlider("Tess Max", x, ref y, width,
            1, 64, (int)Math.Round(_renderer.TessMax),
            pos => _renderer.TessMax = pos,
            pos => $"Tess Max: {pos}");

        AddSlider("Cascade Lambda", x, ref y, width,
            0, 100, (int)Math.Round(_renderer.CascadeLambda * 100f),
            pos => _renderer.CascadeLambda = pos / 100f,
            pos => $"Cascade Lambda: {pos / 100f:0.00}");

        string[] gbufferModes = { "Final", "Albedo", "Normal", "Position", "Roughness", "Metallic" };
        AddSlider("G-Buffer View", x, ref y, width,
            0, 5, _renderer.GBufferViewMode,
            pos => _renderer.GBufferViewMode = pos,
            pos => $"G-Buffer View: {gbufferModes[Math.Clamp(pos, 0, gbufferModes.Length - 1)]}");

        y += 6;
        _expandedHeight = y;
        ResizePanel(_expandedHeight);
    }

    private void AddCheckbox(string text, int x, ref int y, int width, bool initial, Action<bool> apply)
    {
        int id = _nextControlId++;
        IntPtr hwnd = CreateCheckbox(text, x, y, id, initial, width);
        _checkboxes.Add(new CheckboxBinding { Handle = hwnd, Apply = apply });
        _collapsibleHandles.Add(hwnd);
        y += 26;
    }

    private void AddSlider(string name, int x, ref int y, int width,
        int min, int max, int initial, Action<int> apply, Func<int, string> format)
    {
        int id = _nextControlId++;
        IntPtr label = CreateLabel(format(initial), x, y, width);
        y += 20;
        IntPtr trackbar = CreateTrackbar(x, y, width, id, min, max, initial);
        y += 34;

        _sliders.Add(new SliderBinding
        {
            Trackbar = trackbar,
            Label = label,
            Name = name,
            Apply = apply,
            Format = format
        });
        _collapsibleHandles.Add(label);
        _collapsibleHandles.Add(trackbar);
    }

    private IntPtr PanelWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COMMAND)
        {
            HandleCommand(lParam);
            return IntPtr.Zero;
        }
        if (msg == WM_HSCROLL)
        {
            HandleScroll(lParam);
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY)
        {
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void HandleCommand(IntPtr control)
    {
        if (control == IntPtr.Zero) return;

        if (control == _btnCollapse)
        {
            ToggleCollapse();
            return;
        }

        foreach (var cb in _checkboxes)
        {
            if (cb.Handle == control)
            {
                cb.Apply(IsChecked(cb.Handle));
                return;
            }
        }
    }

    private void ToggleCollapse()
    {
        _collapsed = !_collapsed;

        int showCmd = _collapsed ? SW_HIDE : SW_SHOW;
        foreach (var handle in _collapsibleHandles)
            ShowWindow(handle, showCmd);

        SetWindowTextW(_btnCollapse, _collapsed ? "\u25BC Show" : "\u25B2 Hide");
        ResizePanel(_collapsed ? CollapsedHeight : _expandedHeight);
    }

    private void HandleScroll(IntPtr control)
    {
        foreach (var slider in _sliders)
        {
            if (slider.Trackbar != control) continue;

            int pos = (int)SendMessageW(slider.Trackbar, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);
            slider.Apply(pos);
            SetWindowTextW(slider.Label, slider.Format(pos));
            return;
        }
    }

    private bool IsChecked(IntPtr checkbox)
        => SendMessageW(checkbox, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == (IntPtr)BST_CHECKED;


    private void RegisterPanelClass()
    {
        var wc = new WNDCLASSEXW();
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>();
        wc.style = 0;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_panelWndProc);
        wc.hInstance = GetModuleHandleW(null);
        wc.lpszClassName = "DX12DebugMenuPanel";
        wc.hbrBackground = (IntPtr)(COLOR_BTNFACE + 1);

        RegisterClassExW(ref wc);
    }

    private void CreatePanel()
    {
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(_mainHwnd, ref pt);

        _panel = CreateWindowExW(0, "DX12DebugMenuPanel", "",
            WS_POPUP | WS_VISIBLE,
            pt.X + Margin, pt.Y + Margin, PanelWidth, 200,
            _mainHwnd, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private void ResizePanel(int contentHeight)
    {
        SetWindowPos(_panel, IntPtr.Zero, 0, 0, PanelWidth, contentHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);
    }

    private void RepositionPanel()
    {
        var pt = new POINT { X = 0, Y = 0 };
        ClientToScreen(_mainHwnd, ref pt);
        SetWindowPos(_panel, IntPtr.Zero, pt.X + Margin, pt.Y + Margin, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private IntPtr CreateCheckbox(string text, int x, int y, int id, bool initialChecked, int width)
    {
        IntPtr hwnd = CreateWindowExW(0, "BUTTON", text,
            WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX,
            x, y, width, 22,
            _panel, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);

        SendMessageW(hwnd, BM_SETCHECK, initialChecked ? (IntPtr)BST_CHECKED : (IntPtr)BST_UNCHECKED, IntPtr.Zero);
        SendMessageW(hwnd, WM_SETFONT, _font, (IntPtr)1);
        return hwnd;
    }

    private IntPtr CreateButton(string text, int x, int y, int width, int height)
    {
        int id = _nextControlId++;
        IntPtr hwnd = CreateWindowExW(0, "BUTTON", text,
            WS_CHILD | WS_VISIBLE,
            x, y, width, height,
            _panel, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);

        SendMessageW(hwnd, WM_SETFONT, _font, (IntPtr)1);
        return hwnd;
    }

    private IntPtr CreateLabel(string text, int x, int y, int width)
    {
        IntPtr hwnd = CreateWindowExW(0, "STATIC", text,
            WS_CHILD | WS_VISIBLE,
            x, y, width, 18,
            _panel, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

        SendMessageW(hwnd, WM_SETFONT, _font, (IntPtr)1);
        return hwnd;
    }

    private IntPtr CreateTrackbar(int x, int y, int width, int id, int min, int max, int initialPos)
    {
        IntPtr hwnd = CreateWindowExW(0, "msctls_trackbar32", "",
            WS_CHILD | WS_VISIBLE | TBS_HORZ | TBS_NOTICKS,
            x, y, width, 26,
            _panel, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);

        int range = (max & 0xFFFF) << 16 | (min & 0xFFFF);
        SendMessageW(hwnd, TBM_SETRANGE, (IntPtr)1, (IntPtr)range);
        SendMessageW(hwnd, TBM_SETPOS, (IntPtr)1, (IntPtr)initialPos);
        return hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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
    private struct INITCOMMONCONTROLSEX
    {
        public int dwSize;
        public int dwICC;
    }

    private const int ICC_BAR_CLASSES = 0x0004;

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);
}