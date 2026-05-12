using System;
using System.ComponentModel;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// 3Dプレビュー ツールの ViewModel。
/// カメラ状態管理、SRVキャッシュ、コマンドを提供する。
/// 描画ループは View 側 (CompositionTarget.Rendering) が担当。
/// </summary>
internal sealed class Preview3DViewModel : INotifyPropertyChanged, ITimelineToolViewModel, IToolViewModel, IDisposable
{
    private Timeline? _timeline;

    // ── カメラ ──
    public CameraController Camera { get; } = new();

    // ── レンダラー (シェーダ等の管理のみ、デバイスは D3D11Host が所有) ──
    public Preview3DRenderer Renderer { get; } = new();

    // ── 最新のUI用アイテム (SRVキャッシュ) ──
    public List<UiItem> UiItems { get; } = new();
    public int VideoWidth { get; private set; } = 1920;
    public int VideoHeight { get; private set; } = 1080;

    // ── 前フレームを保持: SRV が参照する SharedHandle の元テクスチャを生かしておく ──
    private CapturedFrame? _lastFrame;

    // ── SRVキャッシュ: 新しいフレームが来るまで再利用 ──
    private bool _srvDirty = false;
    public bool HasNewFrame => _srvDirty;

    // ── UI プロパティ ──
    private string _projectionLabel = "透視投影";
    public string ProjectionLabel
    {
        get => _projectionLabel;
        set
        {
            if (_projectionLabel != value)
            {
                _projectionLabel = value;
                OnPropertyChanged(nameof(ProjectionLabel));
            }
        }
    }

    private string _statusText = "待機中...";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    // ── コマンド ──
    public ICommand ResetCommand { get; }
    public ICommand ToggleProjectionCommand { get; }

    public Preview3DViewModel()
    {
        Camera.Reset();

        ResetCommand = new ActionCommand(_ => true, _ =>
        {
            Camera.Reset();
            OnPropertyChanged(nameof(Camera));
        });

        ToggleProjectionCommand = new ActionCommand(_ => true, _ =>
        {
            Camera.IsOrthographic = !Camera.IsOrthographic;
            ProjectionLabel = Camera.IsOrthographic ? "平行投影" : "透視投影";
        });
    }

    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        _timeline = info.Timeline;
    }

    /// <summary>
    /// 新しいフレームが利用可能か確認し、SRV を更新する。
    /// CompositionTarget.Rendering から毎フレーム呼び出される。
    /// </summary>
    public void UpdateFromCapturedFrame(Vortice.Direct3D11.ID3D11Device? d3dDevice)
    {
        var newFrame = Preview3DPatch.TakeLatestFrame();
        if (newFrame == null) return; // 新フレームなし → キャッシュ済み SRV をそのまま使用

        // 古い UI アイテムを破棄
        foreach (var item in UiItems) item.Dispose();
        UiItems.Clear();

        VideoWidth = 1920;
        VideoHeight = 1080;

        if (d3dDevice != null)
        {
            foreach (var item in newFrame.Items)
            {
                if (item.Texture == null) continue;
                try
                {
                    Vortice.Direct3D11.ID3D11ShaderResourceView? srv;

                    if (item.IsOnIndependentDevice)
                    {
                        // YMM43D方式: テクスチャが独立デバイス上 → 直接SRV作成
                        srv = D2DD3DBridge.CreateSrv(item.Texture, d3dDevice);
                    }
                    else
                    {
                        // 旧方式: SharedHandle経由
                        using var dxgiRes = item.Texture.QueryInterface<Vortice.DXGI.IDXGIResource>();
                        if (dxgiRes == null) continue;
                        using var sharedTex = d3dDevice.OpenSharedResource<Vortice.Direct3D11.ID3D11Texture2D>(dxgiRes.SharedHandle);
                        srv = sharedTex != null ? D2DD3DBridge.CreateSrv(sharedTex, d3dDevice) : null;
                    }

                    if (srv != null)
                    {
                        UiItems.Add(new UiItem
                        {
                            DrawDescription = item.DrawDescription,
                            Srv = srv,
                            PixelWidth = item.PixelWidth,
                            PixelHeight = item.PixelHeight,
                            BoundsCenterX = item.BoundsCenterX,
                            BoundsCenterY = item.BoundsCenterY,
                            Opacity = item.Opacity,
                            Layer = item.Layer,
                            D3DEffectId = item.D3DEffectId,
                            D3DVideoEffect = item.D3DVideoEffect,
                            ItemFrame = item.ItemFrame,
                            ItemLength = item.ItemLength,
                            Fps = item.Fps,
                        });
                    }
                }
                catch (Exception ex)
                {
                    Preview3DPlugin.Log($"SRV作成エラー: {ex.Message}");
                }
            }
        }

        // 前フレームのテクスチャを破棄し、現フレームを保持
        // SharedHandle 経由の SRV が有効であるため、元テクスチャを生かしておく
        _lastFrame?.Dispose();
        _lastFrame = newFrame;

        StatusText = UiItems.Count > 0
            ? $"アイテム数: {UiItems.Count}"
            : "アイテムなし";

        _srvDirty = true;
    }

    /// <summary>SRV更新済みフラグをクリア</summary>
    public void ClearDirtyFlag() => _srvDirty = false;

    // ── IToolViewModel ──

    public string? Title => "簡単3Dプレビュー";

    public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;

    public ToolState SaveState() => new ToolState();
    public void LoadState(ToolState stateData) { }

    // ── INotifyPropertyChanged ──

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── IDisposable ──
    public event EventHandler? Disposed;

    public void Dispose()
    {
        foreach (var item in UiItems) item.Dispose();
        UiItems.Clear();
        _lastFrame?.Dispose();
        _lastFrame = null;
        Renderer.Dispose();
        Disposed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class UiItem : IDisposable
{
    public required YukkuriMovieMaker.Player.Video.DrawDescription DrawDescription { get; init; }
    public required Vortice.Direct3D11.ID3D11ShaderResourceView Srv { get; init; }
    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }
    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }
    public float Opacity { get; init; }
    public int Layer { get; init; }

    // ── D3Dエフェクト情報 ──
    public string? D3DEffectId { get; init; }
    public object? D3DVideoEffect { get; init; }
    public long ItemFrame { get; init; }
    public long ItemLength { get; init; }
    public int Fps { get; init; }

    public void Dispose()
    {
        Srv?.Dispose();
    }
}
