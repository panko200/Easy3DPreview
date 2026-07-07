using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vortice.Direct3D11;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// 3Dプレビュー コントロール。
/// D3D11Host (HwndHost + SwapChain) で D3D11 のレンダリング結果を直接表示し、
/// CompositionTarget.Rendering でフレームループを駆動する。
/// GPU→CPU コピーは一切行わない。
/// </summary>
public partial class Preview3DControl : UserControl
{
    private D3D11Host? _d3dHost;
    private bool _hostInitialized;

    private Point _lastMousePos;
    private bool _isRightDragging;
    private bool _isMiddleDragging;

    public Preview3DControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is Preview3DViewModel oldVm)
        {
            oldVm.Disposed -= OnViewModelDisposed;
        }
        if (e.NewValue is Preview3DViewModel newVm)
        {
            newVm.Disposed += OnViewModelDisposed;
        }
    }

    private void OnViewModelDisposed(object? sender, EventArgs e)
    {
        DisposeHost();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // D3D11Host を動的に作成してコンテナに追加
        if (_d3dHost == null)
        {
            _d3dHost = new D3D11Host();
            _d3dHost.Render += OnRender;
            _d3dHost.MouseAction += OnMouseAction;
            PreviewBorder.Child = _d3dHost;
        }

        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;

        // ViewModel のリソース解放
        if (DataContext is Preview3DViewModel vm)
        {
            vm.Dispose();
        }

        DisposeHost();
    }

    private void DisposeHost()
    {
        // Patch の独立デバイス参照をクリア、未消費フレームを破棄
        Preview3DPatch.SetIndependentDevice(null);
        Preview3DPatch.ClearLatestFrame();

        if (_d3dHost != null)
        {
            _d3dHost.Render -= OnRender;
            _d3dHost.MouseAction -= OnMouseAction;
            PreviewBorder.Child = null;
            _d3dHost.Dispose();
            _d3dHost = null;
            _hostInitialized = false;
        }
    }

    /// <summary>
    /// WPF の VSync に同期した描画ループ。
    /// 毎フレーム呼ばれ、新しいキャプチャデータがあれば SRV を更新し、
    /// 常に SwapChain に描画 → Present する。
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_d3dHost == null) return;

        // 初回: デバイス初期化
        if (!_hostInitialized)
        {
            _d3dHost.InitializeDevice();
            if (_d3dHost.Device != null && DataContext is Preview3DViewModel vm)
            {
                vm.Renderer.Initialize(_d3dHost.Device);
                // Patch に独立デバイスを設定 (YMM43D方式のテクスチャ作成用)
                Preview3DPatch.SetIndependentDevice(_d3dHost.Device);
            }
            _hostInitialized = true;
        }

        // ViewModel からキャプチャデータを取得・SRV更新
        if (DataContext is Preview3DViewModel vm2 && _d3dHost.Device != null)
        {
            vm2.UpdateFromCapturedFrame(_d3dHost.Device);
        }

        // 描画 (SwapChain の Present を含む)
        _d3dHost.RenderFrame();
    }

    /// <summary>
    /// D3D11Host の Render イベントハンドラ。
    /// SwapChain のバックバッファに直接描画する。
    /// </summary>
    private void OnRender(ID3D11DeviceContext ctx, int width, int height)
    {
        if (DataContext is not Preview3DViewModel vm) return;

        var rtv = _d3dHost?.RenderTargetView;
        var dsv = _d3dHost?.DepthStencilView;
        if (rtv == null || dsv == null) return;

        vm.Renderer.Render(
            ctx, rtv, dsv,
            width, height,
            vm.UiItems,
            vm.UiObjModels,   // ★OBJモデルリストを追加
            vm.Camera,
            vm.VideoWidth, vm.VideoHeight);
    }

    // ═══════════════════════════════════════════════════
    // マウスイベントハンドラ (D3D11Host の WndProc 経由)
    // ═══════════════════════════════════════════════════

    private void OnMouseAction(Point pos, D3D11Host.MouseEventKind kind, int delta)
    {
        if (DataContext is not Preview3DViewModel vm) return;

        switch (kind)
        {
            case D3D11Host.MouseEventKind.RightDown:
                _lastMousePos = pos;
                _isRightDragging = true;
                break;
            case D3D11Host.MouseEventKind.MiddleDown:
                _lastMousePos = pos;
                _isMiddleDragging = true;
                break;
            case D3D11Host.MouseEventKind.RightUp:
                _isRightDragging = false;
                break;
            case D3D11Host.MouseEventKind.MiddleUp:
                _isMiddleDragging = false;
                break;
            case D3D11Host.MouseEventKind.Move:
                float deltaX = (float)(pos.X - _lastMousePos.X);
                float deltaY = (float)(pos.Y - _lastMousePos.Y);
                _lastMousePos = pos;

                if (_isRightDragging)
                {
                    vm.Camera.Orbit(deltaX, deltaY);
                }
                
                if (_isMiddleDragging)
                {
                    vm.Camera.Pan(deltaX, deltaY);
                }
                break;

            case D3D11Host.MouseEventKind.Wheel:
                // Shiftキーが押されている場合はZ回転（ロール）
                if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift)
                {
                    vm.Camera.AddRoll(delta * 0.1f);
                }
                else
                {
                    // それ以外は常にズーム（右ドラッグ中や中ドラッグ中でも可能）
                    vm.Camera.Zoom(delta);
                }
                break;
        }
    }
}
