using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

// ── OBJモデル用キャプチャ型 ──────────────────────────────────────────────────

internal struct CapturedObjPart
{
    public int IndexOffset;
    public int IndexCount;
    public Vector4 BaseColor;
}

/// <summary>
/// OBJアイテムのCPUリードバック済みデータ。
/// 独立デバイス用バッファ再作成のための原本。
/// </summary>
internal sealed class CapturedObjModel
{
    public required byte[] VertexData { get; init; }
    public required byte[] IndexData { get; init; }
    public required CapturedObjPart[] Parts { get; init; }
    public Vector3 ModelCenter { get; init; }
    public float ModelScale { get; init; } = 1f;
    public required DrawDescription DrawDescription { get; init; }
    public float Opacity { get; init; } = 1f;
    public int Layer { get; init; }
}

// ── 既存アイテム型 ──────────────────────────────────────────────────────────

internal sealed class CapturedItem
{
    public required DrawDescription DrawDescription { get; init; }
    public ID3D11Texture2D? Texture { get; set; }
    /// <summary>true: テクスチャが独立デバイス上にある (SRV直接作成可能)</summary>
    public bool IsOnIndependentDevice { get; init; }
    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }
    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }
    public float Opacity { get; init; } = 1f;
    public int Layer { get; init; }

    // ── D3Dエフェクト情報 (Iyahon_D3D11Renderer_Core 経由) ──
    public string? D3DEffectId { get; init; }
    public object? D3DVideoEffect { get; init; }
    public long ItemFrame { get; init; }
    public long ItemLength { get; init; }
    public int Fps { get; init; }
}

/// <summary>
/// キャプチャ結果を保持するスナップショット。
/// </summary>
internal sealed class CapturedFrame : IDisposable
{
    public List<CapturedItem> Items { get; } = new();
    public List<CapturedObjModel> ObjModels { get; } = new(); // ★追加

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var item in Items)
        {
            item.Texture?.Dispose();
            item.Texture = null;
        }
        Items.Clear();
        ObjModels.Clear();
    }
}

/// <summary>
/// TimelineSource.Update() を Postfix パッチして、
/// 全アイテムの DrawDescription + PreRenderOutput を読み取り専用でキャプチャする。
/// OBJモデルアイテムも検出しCPUリードバックでキャプチャする。
/// </summary>
internal static class Preview3DPatch
{
    // ── 最新キャプチャ結果（スレッドセーフに交換） ──
    private static readonly object _lock = new();
    private static CapturedFrame? _latestFrame;

    // ── 独立デバイス (D3D11Hostのデバイス、スレッドセーフ) ──
    private static ID3D11Device? _independentDevice;

    public static void SetIndependentDevice(ID3D11Device? device) => _independentDevice = device;
    public static ID3D11Device? IndependentDevice => _independentDevice;

    public static void ClearLatestFrame()
    {
        lock (_lock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
    }

    public static CapturedFrame? TakeLatestFrame()
    {
        lock (_lock)
        {
            var frame = _latestFrame;
            _latestFrame = null;
            return frame;
        }
    }

    // ── フィールドキャッシュ ──
    private static Type? _timelineSourceType;
    private static FieldInfo? _timelineResourcesField;
    private static FieldInfo? _devicesField;
    private static FieldInfo? _sceneField;

    private static FieldInfo? _effectedSourceOutputsField;
    private static PropertyInfo? _kvpKeyProp;
    private static PropertyInfo? _kvpValueProp;

    private static Type? _effectedSourceOutputType;
    private static PropertyInfo? _esoPreRenderOutputProp;
    private static PropertyInfo? _esoDrawDescProp;

    // ── FPS/フレーム取得用キャッシュ ──
    private static PropertyInfo? _sceneCurrentFrameProp;
    private static PropertyInfo? _sceneTimelineProp;
    private static PropertyInfo? _timelineVideoInfoProp;
    private static PropertyInfo? _videoInfoFpsProp;

    // ── D3Dエフェクト検出用キャッシュ ──
    private static bool _d3dEffectSearched;
    private static Type? _id3dVideoEffectType;
    private static PropertyInfo? _d3dEffectIdProp;

    // ── YMM4 D3D11デバイス取得用キャッシュ ──
    private static PropertyInfo? _devicesD3DProp;
    private static PropertyInfo? _d3dDeviceProp;
    private static PropertyInfo? _d3dContextProp;
    private static bool _d3dDevicePropsSearched;

    internal static void Apply(Harmony harmony)
    {
        _timelineSourceType = FindType("YukkuriMovieMaker.Player.Video.TimelineSource");
        if (_timelineSourceType == null) { Log("TimelineSource が見つかりません。"); return; }

        var effectedItemSourceType = FindType("YukkuriMovieMaker.Player.Video.EffectedItemSource");
        if (effectedItemSourceType == null) { Log("EffectedItemSource が見つかりません。"); return; }

        _effectedSourceOutputType = FindType("YukkuriMovieMaker.Player.Video.EffectedSourceOutput");
        if (_effectedSourceOutputType == null) { Log("EffectedSourceOutput が見つかりません。"); return; }

        _timelineResourcesField = _timelineSourceType.GetField("timelineResources", BindingFlags.Instance | BindingFlags.NonPublic);
        _devicesField = _timelineSourceType.GetField("devices", BindingFlags.Instance | BindingFlags.NonPublic);
        _sceneField = _timelineSourceType.GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic);

        _effectedSourceOutputsField = effectedItemSourceType.GetField("effectedSourceOutputs", BindingFlags.Instance | BindingFlags.NonPublic);
        _esoPreRenderOutputProp = _effectedSourceOutputType.GetProperty("PreRenderOutput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _esoDrawDescProp = _effectedSourceOutputType.GetProperty("DrawDescription", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var kvpType = typeof(KeyValuePair<,>).MakeGenericType(typeof(IVideoItem), effectedItemSourceType);
        _kvpKeyProp = kvpType.GetProperty("Key");
        _kvpValueProp = kvpType.GetProperty("Value");

        if (_devicesField == null || _timelineResourcesField == null ||
            _effectedSourceOutputsField == null ||
            _esoPreRenderOutputProp == null || _esoDrawDescProp == null)
        {
            Log("必要なメンバのキャッシュに失敗。パッチを中止します。");
            return;
        }

        var sceneType = FindType("YukkuriMovieMaker.Project.Scene");
        if (sceneType != null)
        {
            _sceneCurrentFrameProp = sceneType.GetProperty("CurrentFrame", BindingFlags.Instance | BindingFlags.Public);
            _sceneTimelineProp = sceneType.GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public);
        }
        var timelineType = FindType("YukkuriMovieMaker.Project.Timeline");
        if (timelineType != null)
            _timelineVideoInfoProp = timelineType.GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public);
        var videoInfoType = FindType("YukkuriMovieMaker.Project.VideoInfo");
        if (videoInfoType != null)
            _videoInfoFpsProp = videoInfoType.GetProperty("FPS", BindingFlags.Instance | BindingFlags.Public);

        var updateMethod = _timelineSourceType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public);
        if (updateMethod == null) { Log("Update() が見つかりません。"); return; }

        harmony.Patch(updateMethod, postfix: new HarmonyMethod(typeof(Preview3DPatch), nameof(UpdatePostfix)));
        Log("TimelineSource.Update() Postfix パッチ適用完了。");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => ClearLatestFrame();
    }

    // ── D3Dエフェクト検出 ──

    private static void EnsureD3DEffectTypes()
    {
        if (_d3dEffectSearched) return;
        _d3dEffectSearched = true;
        try
        {
            _id3dVideoEffectType = FindType("Iyahon_D3D11Renderer_Core.D3DEffect.ID3DVideoEffect");
            if (_id3dVideoEffectType != null)
            {
                _d3dEffectIdProp = _id3dVideoEffectType.GetProperty("D3DEffectId",
                    BindingFlags.Instance | BindingFlags.Public);
                Log($"D3Dエフェクト型を検出: D3DEffectId={_d3dEffectIdProp != null}");
            }
            else
            {
                Log("Iyahon_D3D11Renderer_Core 未ロード — D3Dエフェクト検出スキップ。");
            }
        }
        catch (Exception ex) { Log($"D3Dエフェクト型検索エラー: {ex.Message}"); }
    }

    private static (string? effectId, object? videoEffect) DetectD3DEffect(IVideoItem item)
    {
        EnsureD3DEffectTypes();
        if (_id3dVideoEffectType == null || _d3dEffectIdProp == null)
            return (null, null);
        try
        {
            var videoEffects = item.VideoEffects;
            if (videoEffects == null) return (null, null);
            foreach (var ve in videoEffects)
            {
                if (ve == null || !ve.IsEnabled) continue;
                if (_id3dVideoEffectType.IsInstanceOfType(ve))
                {
                    var effectId = _d3dEffectIdProp.GetValue(ve) as string;
                    return (effectId, ve);
                }
            }
        }
        catch (Exception ex) { Log($"D3Dエフェクト検出エラー: {ex.Message}"); }
        return (null, null);
    }

    // ── YMM4 D3D11デバイス取得 ──

    private static (ID3D11Device? device, ID3D11DeviceContext? ctx) GetYmmD3D(IGraphicsDevicesAndContext devices)
    {
        try
        {
            if (!_d3dDevicePropsSearched)
            {
                _d3dDevicePropsSearched = true;
                var devType = devices.GetType();
                _devicesD3DProp = devType.GetProperty("D3D",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_devicesD3DProp != null)
                {
                    var d3dSample = _devicesD3DProp.GetValue(devices);
                    if (d3dSample != null)
                    {
                        var dt = d3dSample.GetType();
                        _d3dDeviceProp = dt.GetProperty("Device",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        _d3dContextProp = dt.GetProperty("DeviceContext",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
            }

            if (_devicesD3DProp == null) return (null, null);
            var d3d = _devicesD3DProp.GetValue(devices);
            if (d3d == null) return (null, null);

            var dev = _d3dDeviceProp?.GetValue(d3d) as ID3D11Device;
            var ctx = _d3dContextProp?.GetValue(d3d) as ID3D11DeviceContext;
            return (dev, ctx);
        }
        catch { return (null, null); }
    }

    // ── メインPostfix ──

    private static void UpdatePostfix(object __instance)
    {
        try
        {
            var devices = _devicesField!.GetValue(__instance) as IGraphicsDevicesAndContext;
            if (devices == null) return;

            var resources = _timelineResourcesField!.GetValue(__instance);
            if (resources == null) return;

            var scene = _sceneField?.GetValue(__instance);
            int fps = (int)(GetFps(scene) ?? 60.0);

            if (_independentDevice == null) return;

            // YMM4 D3D11デバイスをOBJリードバック用に取得
            var (ymmDevice, ymmCtx) = GetYmmD3D(devices);

            var dc = devices.DeviceContext;
            var frame = new CapturedFrame();

            foreach (object pair in (IEnumerable)resources)
            {
                var key = _kvpKeyProp!.GetValue(pair) as IVideoItem;
                var value = _kvpValueProp!.GetValue(pair);
                if (key == null || value == null) continue;

                var esoList = _effectedSourceOutputsField!.GetValue(value) as IList;
                if (esoList == null || esoList.Count == 0) continue;

                if (Preview3DVisibilityState.IsHiddenIn3DPreview(key)) continue;

                // ─── OBJモデルアイテムの検出 ───────────────────────────────
                if (ObjCaptureBridge.IsAvailable && ObjCaptureBridge.IsObjLoaderItem(key))
                {
                    var firstEso = esoList[0];
                    var drawDesc = firstEso != null
                        ? _esoDrawDescProp!.GetValue(firstEso) as DrawDescription
                        : null;

                    if (drawDesc != null && drawDesc.Opacity > 0.0 && ymmDevice != null && ymmCtx != null)
                    {
                        var captured = ObjCaptureBridge.TryCapture(
                            value, drawDesc, (float)drawDesc.Opacity, key.Layer,
                            ymmDevice, ymmCtx);
                        if (captured != null)
                            frame.ObjModels.AddRange(captured);
                    }
                    continue; // OBJアイテムは通常のテクスチャ描画ルートをスキップ
                }

                // ─── D3Dエフェクト検出 ────────────────────────────────────
                var (effectId, videoEffect) = DetectD3DEffect(key);
                long itemFrame = 0;
                long itemLength = 1;

                if (effectId != null)
                {
                    try
                    {
                        itemFrame = GetItemFrame(key, scene) ?? 0L;
                        itemLength = GetItemLength(key) ?? 1L;
                    }
                    catch { }
                }

                // ─── 通常アイテム ────────────────────────────────────────
                foreach (object? eso in esoList)
                {
                    if (eso == null) continue;

                    var preRenderOutput = _esoPreRenderOutputProp!.GetValue(eso) as ID2D1Image;
                    var drawDesc = _esoDrawDescProp!.GetValue(eso) as DrawDescription;

                    if (preRenderOutput == null || drawDesc == null) continue;
                    if ((double)drawDesc.Zoom.X == 0.0 || (double)drawDesc.Zoom.Y == 0.0 || drawDesc.Opacity == 0.0) continue;

                    RawRectF bounds;
                    try { bounds = dc.GetImageLocalBounds(preRenderOutput); }
                    catch { continue; }

                    int left = (int)MathF.Floor(bounds.Left);
                    int top = (int)MathF.Floor(bounds.Top);
                    int right = (int)MathF.Ceiling(bounds.Right);
                    int bottom = (int)MathF.Ceiling(bounds.Bottom);

                    float pw = right - left;
                    float ph = bottom - top;
                    if (pw <= 0 || ph <= 0) continue;

                    const int MaxTexSize = 100000000;
                    int texW = Math.Min((int)pw, MaxTexSize);
                    int texH = Math.Min((int)ph, MaxTexSize);
                    if (texW <= 0 || texH <= 0) continue;

                    ID3D11Texture2D? d3dTex = D2DD3DBridge.CreateOnIndependentDevice(
                        preRenderOutput, devices, _independentDevice, texW, texH, -left, -top);
                    if (d3dTex == null) continue;

                    float cx = left + texW / 2f;
                    float cy = top + texH / 2f;

                    frame.Items.Add(new CapturedItem
                    {
                        DrawDescription = drawDesc,
                        Texture = d3dTex,
                        IsOnIndependentDevice = true,
                        PixelWidth = texW,
                        PixelHeight = texH,
                        BoundsCenterX = cx,
                        BoundsCenterY = cy,
                        Opacity = (float)drawDesc.Opacity,
                        Layer = key.Layer,
                        D3DEffectId = effectId,
                        D3DVideoEffect = videoEffect,
                        ItemFrame = itemFrame,
                        ItemLength = itemLength,
                        Fps = fps,
                    });
                }
            }

            lock (_lock)
            {
                _latestFrame?.Dispose();
                _latestFrame = frame;
            }
        }
        catch (Exception ex)
        {
            Log($"UpdatePostfix 例外: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── ヘルパー ──

    private static double? GetFps(object? scene)
    {
        if (scene == null || _sceneTimelineProp == null || _timelineVideoInfoProp == null || _videoInfoFpsProp == null) return null;
        try
        {
            var tl = _sceneTimelineProp.GetValue(scene);
            if (tl == null) return null;
            var vi = _timelineVideoInfoProp.GetValue(tl);
            if (vi == null) return null;
            var fpsObj = _videoInfoFpsProp.GetValue(vi);
            return fpsObj != null ? Convert.ToDouble(fpsObj) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 外部からYMM4のD3D11デバイスとコンテキストを取得するためのプロキシ
    /// </summary>
    public static (ID3D11Device? device, ID3D11DeviceContext? ctx) GetYmmD3DExternal(IGraphicsDevicesAndContext devices)
    {
        return GetYmmD3D(devices);
    }

    private static long? GetItemFrame(IVideoItem item, object? scene)
    {
        try
        {
            if (scene != null && _sceneCurrentFrameProp != null)
            {
                var currentFrameObj = _sceneCurrentFrameProp.GetValue(scene);
                if (currentFrameObj != null)
                {
                    long sceneFrame = Convert.ToInt64(currentFrameObj);
                    long itemStart = item.Frame;
                    return Math.Max(0, sceneFrame - itemStart);
                }
            }
            return 0L;
        }
        catch { return null; }
    }

    private static long? GetItemLength(IVideoItem item)
    {
        try { return item.Length; }
        catch { return null; }
    }

    internal static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Easy3DPreview.Preview3D] Patch: {msg}");
}

// ══════════════════════════════════════════════════════════════════════════════
// OBJキャプチャ ブリッジ
// ObjLoaderBridge (in Iyahon_D3D11Renderer_Core) をリフレクション経由で呼び出し、
// YMM4のD3D11デバイス上にあるOBJバッファをCPUリードバックして保存する。
// ══════════════════════════════════════════════════════════════════════════════
internal static class ObjCaptureBridge
{
    private static bool _searched;
    private static bool _available;

    // D3D11Renderer_Core の ObjLoaderBridge への参照
    private static MethodInfo? _isObjLoaderItemMethod;
    private static MethodInfo? _tryGetModelDataMethod;

    // ObjModelData プロパティ
    private static PropertyInfo? _vbProp;        // VertexBuffer
    private static PropertyInfo? _ibProp;        // IndexBuffer
    private static PropertyInfo? _partsProp;     // Parts (ObjPartRenderInfo[])
    private static PropertyInfo? _centerProp;    // ModelCenter
    private static PropertyInfo? _scaleProp;     // ModelScale

    // ObjPartRenderInfo フィールド
    private static FieldInfo? _partIndexOffsetField;
    private static FieldInfo? _partIndexCountField;
    private static FieldInfo? _partBaseColorField;

    // バッファキャッシュ: NativePointer → raw data
    // 同じバッファポインタなら CPU リードバックをスキップ
    private sealed class CachedObjData
    {
        public required byte[] VertexData;
        public required byte[] IndexData;
        public required CapturedObjPart[] Parts;
        public Vector3 ModelCenter;
        public float ModelScale;
    }
    private static readonly Dictionary<IntPtr, CachedObjData> _bufferCache = new();

    public static bool IsAvailable
    {
        get { EnsureSearched(); return _available; }
    }

    private static void EnsureSearched()
    {
        if (_searched) return;
        _searched = true;

        try
        {
            var bridgeType = Preview3DPatch.FindType("Iyahon_D3D11Renderer_Core.ObjLoaderBridge");
            if (bridgeType == null)
            {
                Log("ObjLoaderBridge 未検出 — OBJ対応無効。");
                return;
            }

            // ObjLoaderBridge.Initialize() を呼んでObjLoader型のキャッシュを構築させる
            var initMethod = bridgeType.GetMethod("Initialize",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            initMethod?.Invoke(null, null);

            _isObjLoaderItemMethod = bridgeType.GetMethod("IsObjLoaderItem",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _tryGetModelDataMethod = bridgeType.GetMethod("TryGetModelData",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (_isObjLoaderItemMethod == null || _tryGetModelDataMethod == null)
            {
                Log("ObjLoaderBridge メソッド取得失敗。");
                return;
            }

            // ObjModelData プロパティをキャッシュ
            var modelDataType = Preview3DPatch.FindType("Iyahon_D3D11Renderer_Core.ObjModelData");
            if (modelDataType != null)
            {
                _vbProp = modelDataType.GetProperty("VertexBuffer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _ibProp = modelDataType.GetProperty("IndexBuffer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _partsProp = modelDataType.GetProperty("Parts",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _centerProp = modelDataType.GetProperty("ModelCenter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _scaleProp = modelDataType.GetProperty("ModelScale",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            // ObjPartRenderInfo フィールドをキャッシュ
            var partType = Preview3DPatch.FindType("Iyahon_D3D11Renderer_Core.ObjPartRenderInfo");
            if (partType != null)
            {
                _partIndexOffsetField = partType.GetField("IndexOffset",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _partIndexCountField = partType.GetField("IndexCount",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _partBaseColorField = partType.GetField("BaseColor",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            _available = _vbProp != null && _ibProp != null && _partsProp != null;
            Log($"ObjCaptureBridge 初期化完了: available={_available}");
        }
        catch (Exception ex)
        {
            Log($"ObjCaptureBridge 初期化エラー: {ex.Message}");
        }
    }

    public static bool IsObjLoaderItem(IVideoItem item)
    {
        try
        {
            return (bool)(_isObjLoaderItemMethod!.Invoke(null, new object[] { item }) ?? false);
        }
        catch { return false; }
    }

    /// <summary>
    /// OBJモデルのデータをYMM4デバイスからCPUリードバックしてキャプチャする。
    /// </summary>
    public static List<CapturedObjModel>? TryCapture(
        object eisValue,
        DrawDescription drawDesc,
        float opacity,
        int layer,
        ID3D11Device ymmDevice,
        ID3D11DeviceContext ymmCtx)
    {
        if (!_available) return null;

        try
        {
            // ObjLoaderBridge.TryGetModelData(eisValue) を呼んでObjModelDataリストを取得
            var modelDataList = _tryGetModelDataMethod!.Invoke(null, new object[] { eisValue });
            if (modelDataList is not IList rawList || rawList.Count == 0) return null;

            var results = new List<CapturedObjModel>();

            foreach (var modelData in rawList)
            {
                if (modelData == null) continue;

                var vb = _vbProp!.GetValue(modelData) as ID3D11Buffer;
                var ib = _ibProp!.GetValue(modelData) as ID3D11Buffer;
                if (vb == null || ib == null) continue;

                var center = _centerProp?.GetValue(modelData) is Vector3 c ? c : Vector3.Zero;
                var scale = _scaleProp?.GetValue(modelData) is float s ? s : 1f;

                // ─── バッファキャッシュ確認 ───────────────────────────────
                var cacheKey = vb.NativePointer;
                if (!_bufferCache.TryGetValue(cacheKey, out var cached))
                {
                    // CPU リードバック
                    var vertData = ReadbackBuffer(ymmDevice, ymmCtx, vb);
                    var idxData = ReadbackBuffer(ymmDevice, ymmCtx, ib);

                    if (vertData == null || idxData == null) continue;

                    var parts = ExtractParts(modelData);

                    cached = new CachedObjData
                    {
                        VertexData = vertData,
                        IndexData = idxData,
                        Parts = parts,
                        ModelCenter = center,
                        ModelScale = scale,
                    };
                    _bufferCache[cacheKey] = cached;
                }

                results.Add(new CapturedObjModel
                {
                    VertexData = cached.VertexData,
                    IndexData = cached.IndexData,
                    Parts = cached.Parts,
                    ModelCenter = cached.ModelCenter,
                    ModelScale = cached.ModelScale,
                    DrawDescription = drawDesc,
                    Opacity = opacity,
                    Layer = layer,
                });
            }

            return results.Count > 0 ? results : null;
        }
        catch (Exception ex)
        {
            Log($"TryCapture エラー: {ex.Message}");
            return null;
        }
    }

    private static CapturedObjPart[] ExtractParts(object modelData)
    {
        try
        {
            var partsObj = _partsProp!.GetValue(modelData);
            if (partsObj is not Array partsArray) return Array.Empty<CapturedObjPart>();

            var result = new CapturedObjPart[partsArray.Length];
            for (int i = 0; i < partsArray.Length; i++)
            {
                var p = partsArray.GetValue(i);
                if (p == null) continue;
                result[i] = new CapturedObjPart
                {
                    IndexOffset = _partIndexOffsetField?.GetValue(p) is int io ? io : 0,
                    IndexCount = _partIndexCountField?.GetValue(p) is int ic ? ic : 0,
                    BaseColor = _partBaseColorField?.GetValue(p) is Vector4 bc ? bc : Vector4.One,
                };
            }
            return result;
        }
        catch { return Array.Empty<CapturedObjPart>(); }
    }

    private static unsafe byte[]? ReadbackBuffer(ID3D11Device device, ID3D11DeviceContext ctx, ID3D11Buffer srcBuffer)
    {
        ID3D11Buffer? staging = null;
        try
        {
            int byteWidth = srcBuffer.Description.ByteWidth;
            if (byteWidth <= 0) return null;

            staging = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = byteWidth,
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                MiscFlags = ResourceOptionFlags.None,
                StructureByteStride = 0,
            });

            ctx.CopyResource(staging, srcBuffer);

            var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var data = new byte[byteWidth];
            Marshal.Copy(mapped.DataPointer, data, 0, byteWidth);
            ctx.Unmap(staging, 0);

            return data;
        }
        catch (Exception ex)
        {
            Log($"ReadbackBuffer エラー: {ex.Message}");
            return null;
        }
        finally
        {
            staging?.Dispose();
        }
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Easy3DPreview] ObjCaptureBridge: {msg}");
}
