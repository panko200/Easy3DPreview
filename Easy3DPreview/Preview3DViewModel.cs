using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// OBJモデル1パーツのGPUリソース（独立デバイス上）。
/// </summary>
internal struct UiObjPart
{
    public int IndexOffset;
    public int IndexCount;
    public Vector4 BaseColor;
}

/// <summary>
/// OBJモデル1ファイル分のGPUリソース（独立デバイス上）。
/// </summary>
internal sealed class UiObjModel : IDisposable
{
    public required ID3D11Buffer VertexBuffer { get; init; }
    public required ID3D11Buffer IndexBuffer { get; init; }
    public required UiObjPart[] Parts { get; init; }
    public Vector3 ModelCenter { get; init; }
    public float ModelScale { get; init; } = 1f;
    public required YukkuriMovieMaker.Player.Video.DrawDescription DrawDescription { get; init; }
    public float Opacity { get; init; } = 1f;
    public int Layer { get; init; }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
    }
}

/// <summary>
/// 3Dプレビュー ツールの ViewModel。
/// カメラ状態管理、SRVキャッシュ、コマンドを提供する。
/// </summary>
internal sealed class Preview3DViewModel : INotifyPropertyChanged, ITimelineToolViewModel, IToolViewModel, IDisposable
{
    private Timeline? _timeline;

    public CameraController Camera { get; } = new();
    public Preview3DRenderer Renderer { get; } = new();

    public List<UiItem> UiItems { get; } = new();
    public List<UiObjModel> UiObjModels { get; } = new();   // ★追加: OBJモデル
    public int VideoWidth { get; private set; } = 1920;
    public int VideoHeight { get; private set; } = 1080;

    private CapturedFrame? _lastFrame;
    private bool _srvDirty = false;
    public bool HasNewFrame => _srvDirty;

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
    public void UpdateFromCapturedFrame(ID3D11Device? d3dDevice)
    {
        var newFrame = Preview3DPatch.TakeLatestFrame();
        if (newFrame == null) return;

        // 古い UI アイテムを破棄
        foreach (var item in UiItems) item.Dispose();
        UiItems.Clear();

        foreach (var obj in UiObjModels) obj.Dispose();   // ★追加
        UiObjModels.Clear();

        VideoWidth = 1920;
        VideoHeight = 1080;

        if (d3dDevice != null)
        {
            // ─── 通常アイテム (テクスチャ + D3Dエフェクト) ───────────────
            foreach (var item in newFrame.Items)
            {
                if (item.Texture == null) continue;
                try
                {
                    ID3D11ShaderResourceView? srv;
                    if (item.IsOnIndependentDevice)
                    {
                        srv = D2DD3DBridge.CreateSrv(item.Texture, d3dDevice);
                    }
                    else
                    {
                        using var dxgiRes = item.Texture.QueryInterface<IDXGIResource>();
                        if (dxgiRes == null) continue;
                        using var sharedTex = d3dDevice.OpenSharedResource<ID3D11Texture2D>(dxgiRes.SharedHandle);
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

            // ─── OBJモデル: 独立デバイス上にバッファを再作成 ─────────────
            foreach (var capturedObj in newFrame.ObjModels)
            {
                try
                {
                    var uiObj = CreateUiObjModel(d3dDevice, capturedObj);
                    if (uiObj != null)
                        UiObjModels.Add(uiObj);
                }
                catch (Exception ex)
                {
                    Preview3DPlugin.Log($"OBJバッファ作成エラー: {ex.Message}");
                }
            }
        }

        _lastFrame?.Dispose();
        _lastFrame = newFrame;

        int total = UiItems.Count + UiObjModels.Count;
        StatusText = total > 0
            ? $"アイテム数: {UiItems.Count}  OBJ: {UiObjModels.Count}"
            : "アイテムなし";

        _srvDirty = true;
    }

    /// <summary>
    /// CapturedObjModel のCPUデータから独立デバイス用GPUバッファを作成する。
    /// </summary>
    private static unsafe UiObjModel? CreateUiObjModel(ID3D11Device device, CapturedObjModel captured)
    {
        if (captured.VertexData.Length == 0 || captured.IndexData.Length == 0) return null;

        ID3D11Buffer? vb = null;
        ID3D11Buffer? ib = null;
        try
        {
            fixed (byte* pVerts = captured.VertexData)
            {
                vb = device.CreateBuffer(
                    new BufferDescription(captured.VertexData.Length, BindFlags.VertexBuffer),
                    new SubresourceData((IntPtr)pVerts));
            }

            fixed (byte* pIdx = captured.IndexData)
            {
                ib = device.CreateBuffer(
                    new BufferDescription(captured.IndexData.Length, BindFlags.IndexBuffer),
                    new SubresourceData((IntPtr)pIdx));
            }

            var parts = new UiObjPart[captured.Parts.Length];
            for (int i = 0; i < captured.Parts.Length; i++)
            {
                parts[i] = new UiObjPart
                {
                    IndexOffset = captured.Parts[i].IndexOffset,
                    IndexCount = captured.Parts[i].IndexCount,
                    BaseColor = captured.Parts[i].BaseColor,
                };
            }

            return new UiObjModel
            {
                VertexBuffer = vb,
                IndexBuffer = ib,
                Parts = parts,
                ModelCenter = captured.ModelCenter,
                ModelScale = captured.ModelScale,
                DrawDescription = captured.DrawDescription,
                Opacity = captured.Opacity,
                Layer = captured.Layer,
            };
        }
        catch
        {
            vb?.Dispose();
            ib?.Dispose();
            return null;
        }
    }

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
        foreach (var obj in UiObjModels) obj.Dispose();
        UiObjModels.Clear();
        _lastFrame?.Dispose();
        _lastFrame = null;
        Renderer.Dispose();
        Disposed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class UiItem : IDisposable
{
    public required YukkuriMovieMaker.Player.Video.DrawDescription DrawDescription { get; init; }
    public required ID3D11ShaderResourceView Srv { get; init; }
    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }
    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }
    public float Opacity { get; init; }
    public int Layer { get; init; }

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
