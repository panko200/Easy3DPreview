using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// HwndHost ベースの D3D11 レンダリングサーフェス。
/// 独立した D3D11 デバイスと SwapChain を管理し、
/// GPU→CPU コピーなしで直接画面に描画する。
/// </summary>
internal sealed partial class D3D11Host : HwndHost, IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID3D11Texture2D? _depthBuffer;
    private ID3D11DepthStencilView? _depthStencilView;

    /// <summary>描画用のデバイス</summary>
    public ID3D11Device? Device => _device;
    /// <summary>描画用のコンテキスト</summary>
    public ID3D11DeviceContext? DeviceContext => _deviceContext;
    /// <summary>バックバッファの RTV</summary>
    public ID3D11RenderTargetView? RenderTargetView => _renderTargetView;
    /// <summary>深度バッファの DSV</summary>
    public ID3D11DepthStencilView? DepthStencilView => _depthStencilView;

    /// <summary>描画コールバック (context, pixelWidth, pixelHeight)</summary>
    public event Action<ID3D11DeviceContext, int, int>? Render;
    /// <summary>マウスアクションコールバック</summary>
    public event Action<Point, MouseEventKind, int>? MouseAction;

    public enum MouseEventKind { Down, Move, Up, Wheel, RightDown, RightUp, MiddleDown, MiddleUp }

    private const string WindowClassName = "Easy3DPreview_Preview3D_Host";
    private static bool _isClassRegistered;
    private static WndProcDelegate? _defWndProc;

    private int _pixelWidth = 1;
    private int _pixelHeight = 1;

    public int PixelWidth => _pixelWidth;
    public int PixelHeight => _pixelHeight;

    /// <summary>
    /// 独立した D3D11 デバイスを初期化する。
    /// BuildWindowCore 後に呼び出す。
    /// </summary>
    public void InitializeDevice()
    {
        if (_device != null) return;

        var flags = DeviceCreationFlags.BgraSupport;
        if (D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, flags, null,
            out _device, out _deviceContext).Failure)
        {
            Preview3DPlugin.Log("D3D11Host: デバイス作成失敗");
            return;
        }

        CreateSwapChain();
    }

    /// <summary>1フレーム描画して Present する</summary>
    public void RenderFrame()
    {
        if (_device == null || _deviceContext == null || _swapChain == null) return;

        Render?.Invoke(_deviceContext, _pixelWidth, _pixelHeight);
        try
        {
            _swapChain.Present(1, PresentFlags.None);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // デバイスロスト等は無視
        }
    }

    // ═══════════════════════════════════════════
    // HwndHost
    // ═══════════════════════════════════════════

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (!_isClassRegistered)
        {
            _defWndProc = DefWindowProc;
            var classNamePtr = Marshal.StringToHGlobalUni(WindowClassName);
            try
            {
                var wndClass = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0x0008, // CS_DBLCLKS
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_defWndProc),
                    hInstance = nint.Zero,
                    hCursor = LoadCursor(nint.Zero, (nint)32512),
                    hbrBackground = GetStockObject(4), // BLACK_BRUSH
                    lpszClassName = classNamePtr,
                };
                RegisterClassEx(ref wndClass);
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }
            _isClassRegistered = true;
        }

        UpdatePixelSize();

        var classPtr = Marshal.StringToHGlobalUni(WindowClassName);
        var windowPtr = Marshal.StringToHGlobalUni("");
        try
        {
            var hwnd = CreateWindowEx(
                0, classPtr, windowPtr,
                0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
                0, 0, _pixelWidth, _pixelHeight,
                hwndParent.Handle, nint.Zero, nint.Zero, nint.Zero);
            return new HandleRef(this, hwnd);
        }
        finally
        {
            Marshal.FreeHGlobal(classPtr);
            Marshal.FreeHGlobal(windowPtr);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        // リソース解放は Dispose で一元管理
    }

    private bool _disposed;

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CleanupSwapChain();
        _deviceContext?.Dispose();
        _device?.Dispose();
        _deviceContext = null;
        _device = null;
        base.Dispose();
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0201: // WM_LBUTTONDOWN
                SetCapture(hwnd);
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.Down, 0);
                handled = true;
                break;
            case 0x0202: // WM_LBUTTONUP
                ReleaseCapture();
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.Up, 0);
                handled = true;
                break;
            case 0x0204: // WM_RBUTTONDOWN
                SetCapture(hwnd);
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.RightDown, 0);
                handled = true;
                break;
            case 0x0205: // WM_RBUTTONUP
                ReleaseCapture();
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.RightUp, 0);
                handled = true;
                break;
            case 0x0207: // WM_MBUTTONDOWN
                SetCapture(hwnd);
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.MiddleDown, 0);
                handled = true;
                break;
            case 0x0208: // WM_MBUTTONUP
                ReleaseCapture();
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.MiddleUp, 0);
                handled = true;
                break;
            case 0x0200: // WM_MOUSEMOVE
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.Move, 0);
                handled = true;
                break;
            case 0x020A: // WM_MOUSEWHEEL
                short delta = (short)((long)wParam >> 16);
                MouseAction?.Invoke(new Point(0, 0), MouseEventKind.Wheel, delta);
                handled = true;
                break;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdatePixelSize();
        CreateSwapChain();
    }

    // ═══════════════════════════════════════════
    // SwapChain 管理
    // ═══════════════════════════════════════════

    private void UpdatePixelSize()
    {
        // DPI スケーリングを考慮した実ピクセルサイズ
        var source = PresentationSource.FromVisual(this);
        double dpiScaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiScaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        _pixelWidth = Math.Max(1, (int)(ActualWidth * dpiScaleX));
        _pixelHeight = Math.Max(1, (int)(ActualHeight * dpiScaleY));
    }

    private void CreateSwapChain()
    {
        if (_device == null || Handle == nint.Zero) return;
        if (_pixelWidth <= 0 || _pixelHeight <= 0) return;

        CleanupSwapChain();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var desc = new SwapChainDescription
            {
                BufferCount = 1,
                BufferDescription = new ModeDescription(
                    _pixelWidth, _pixelHeight,
                    new Rational(60, 1),
                    Format.R8G8B8A8_UNorm),
                BufferUsage = Usage.RenderTargetOutput,
                OutputWindow = Handle,
                SampleDescription = new SampleDescription(1, 0),
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
            };

            _swapChain = factory.CreateSwapChain(_device, desc);

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _renderTargetView = _device.CreateRenderTargetView(backBuffer);

            _depthBuffer = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = _pixelWidth,
                Height = _pixelHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
            });
            _depthStencilView = _device.CreateDepthStencilView(_depthBuffer);
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"D3D11Host: SwapChain作成エラー: {ex.Message}");
            CleanupSwapChain();
        }
    }

    private void CleanupSwapChain()
    {
        _depthStencilView?.Dispose(); _depthStencilView = null;
        _depthBuffer?.Dispose(); _depthBuffer = null;
        _renderTargetView?.Dispose(); _renderTargetView = null;
        _swapChain?.Dispose(); _swapChain = null;
    }

    private static Point GetPoint(nint lParam)
    {
        int x = (short)((int)lParam & 0xFFFF);
        int y = (short)((int)lParam >> 16);
        return new Point(x, y);
    }

    // ═══════════════════════════════════════════
    // Win32 API
    // ═══════════════════════════════════════════

    private delegate nint WndProcDelegate(nint hWnd, int msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize; public int style; public nint lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra;
        public nint hInstance; public nint hIcon; public nint hCursor;
        public nint hbrBackground; public nint lpszMenuName;
        public nint lpszClassName; public nint hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern short RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern nint CreateWindowEx(
        int dwExStyle, nint lpClassName, nint lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint GetStockObject(int fnObject);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
}
