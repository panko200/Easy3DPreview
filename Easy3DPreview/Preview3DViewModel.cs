using System;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// 3Dプレビュー ツールの ViewModel。
/// カメラ状態管理、フレーム更新タイマー、コマンドを提供する。
/// </summary>
internal sealed class Preview3DViewModel : INotifyPropertyChanged, ITimelineToolViewModel, IToolViewModel, IDisposable
{
    private Timeline? _timeline;

    // ── カメラ ──
    public CameraController Camera { get; } = new();

    // ── レンダラー ──
    public Preview3DRenderer Renderer { get; } = new();

    // ── 更新タイマー ──
    private readonly DispatcherTimer _timer;

    // ── 最新のUI用アイテム ──
    public List<UiItem> UiItems { get; } = new();
    public int VideoWidth { get; private set; } = 1920;
    public int VideoHeight { get; private set; } = 1080;

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

    // ── 更新通知 ──
    public event Action? FrameUpdated;

    public Preview3DViewModel()
    {
        Camera.Reset();

        ResetCommand = new ActionCommand(_ => true, _ =>
        {
            Camera.Reset();
            OnPropertyChanged(nameof(Camera));
            RequestRender();
        });

        ToggleProjectionCommand = new ActionCommand(_ => true, _ =>
        {
            Camera.IsOrthographic = !Camera.IsOrthographic;
            ProjectionLabel = Camera.IsOrthographic ? "平行投影" : "透視投影";
            RequestRender();
        });

        // 60fps で更新チェック
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        _timeline = info.Timeline;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Harmony パッチからキャプチャされた最新フレームを取得
        var newFrame = Preview3DPatch.TakeLatestFrame();
        if (newFrame != null)
        {
            // 古い UI アイテムを破棄
            foreach (var item in UiItems) item.Dispose();
            UiItems.Clear();

            VideoWidth = 1920;
            VideoHeight = 1080; // Defaulting sizes here

            var d3dDevice = Renderer.Device;
            if (d3dDevice != null)
            {
                foreach (var item in newFrame.Items)
                {
                    if (item.SharedHandle == IntPtr.Zero) continue;
                    try
                    {
                        using var sharedTex = d3dDevice.OpenSharedResource<Vortice.Direct3D11.ID3D11Texture2D>(item.SharedHandle);
                        if (sharedTex != null)
                        {
                            var srv = D2DD3DBridge.CreateSrv(sharedTex, d3dDevice);
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
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Preview3DPlugin.Log($"OpenSharedResource エラー: {ex.Message}");
                    }
                }
            }

            // 元のフレーム（YMM4側テクスチャ）を解放
            newFrame.Dispose();

            StatusText = UiItems.Count > 0
                ? $"アイテム数: {UiItems.Count}"
                : "アイテムなし";
        }

        // カメラがUIから操作されたときなど、常に再描画する
        RequestRender();
    }

    /// <summary>手動でレンダリングを要求する。</summary>
    public void RequestRender()
    {
        FrameUpdated?.Invoke();
    }

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

    public void Dispose()
    {
        _timer.Stop();
        foreach (var item in UiItems) item.Dispose();
        UiItems.Clear();
        Renderer.Dispose();
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

    public void Dispose()
    {
        Srv?.Dispose();
    }
}
