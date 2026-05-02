using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// 3Dプレビュー コントロール。
/// WriteableBitmap で D3D11 のレンダリング結果を表示し、
/// マウスイベントでカメラを操作する。
/// </summary>
public partial class Preview3DControl : UserControl
{
    private WriteableBitmap? _bitmap;
    private int _lastRenderWidth;
    private int _lastRenderHeight;

    private Point _lastMousePos;
    private bool _isRightDragging;
    private bool _isMiddleDragging;

    public Preview3DControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is Preview3DViewModel vm)
        {
            vm.FrameUpdated += OnFrameUpdated;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is Preview3DViewModel vm)
        {
            vm.FrameUpdated -= OnFrameUpdated;
        }
    }

    private void OnFrameUpdated()
    {
        if (DataContext is not Preview3DViewModel vm) return;

        try
        {
            // レンダラーから独立したD3Dデバイス・コンテキストを使用する
            // 描画サイズ
            int rw = Math.Max(1, (int)ActualWidth);
            int rh = Math.Max(1, (int)ActualHeight);
            rw = Math.Min(rw, 1920);
            rh = Math.Min(rh, 1080);

            if (!vm.Renderer.Initialize(rw, rh)) return;

            // 描画
            var tex = vm.Renderer.Render(vm.UiItems, vm.Camera, vm.VideoWidth, vm.VideoHeight);
            if (tex == null) return;

            EnsureBitmap(rw, rh);
            if (_bitmap == null) return;

            if (vm.Renderer.CopyToBitmap(tex, _bitmap))
            {
                if (PreviewImage.Source != _bitmap)
                {
                    PreviewImage.Source = _bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"OnFrameUpdated エラー: {ex.Message}");
        }
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != null && _lastRenderWidth == width && _lastRenderHeight == height) return;

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _lastRenderWidth = width;
        _lastRenderHeight = height;
    }

    // ═══════════════════════════════════════════════════
    // マウスイベントハンドラ
    // ═══════════════════════════════════════════════════

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is Preview3DViewModel vm)
        {
            vm.Camera.Zoom(e.Delta);
            vm.RequestRender();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastMousePos = e.GetPosition(this);

        if (e.RightButton == MouseButtonState.Pressed)
        {
            _isRightDragging = true;
            ((IInputElement)sender).CaptureMouse();
        }
        else if (e.MiddleButton == MouseButtonState.Pressed)
        {
            _isMiddleDragging = true;
            ((IInputElement)sender).CaptureMouse();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRightDragging && !_isMiddleDragging) return;
        if (DataContext is not Preview3DViewModel vm) return;

        var pos = e.GetPosition(this);
        float deltaX = (float)(pos.X - _lastMousePos.X);
        float deltaY = (float)(pos.Y - _lastMousePos.Y);
        _lastMousePos = pos;

        if (_isRightDragging)
        {
            vm.Camera.Orbit(deltaX, deltaY);
        }
        else if (_isMiddleDragging)
        {
            vm.Camera.Pan(deltaX, deltaY);
        }

        vm.RequestRender();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Released && _isRightDragging)
        {
            _isRightDragging = false;
            ((IInputElement)sender).ReleaseMouseCapture();
        }
        if (e.MiddleButton == MouseButtonState.Released && _isMiddleDragging)
        {
            _isMiddleDragging = false;
            ((IInputElement)sender).ReleaseMouseCapture();
        }
    }
}
