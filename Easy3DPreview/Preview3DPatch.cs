using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

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
    /// <summary>D3DEffectRegistry に登録されたエフェクトID (null = D3Dエフェクトなし)</summary>
    public string? D3DEffectId { get; init; }
    /// <summary>ID3DVideoEffect の実体 (ConfigureEffect 呼び出し用)</summary>
    public object? D3DVideoEffect { get; init; }
    /// <summary>アイテム内の現在フレーム位置</summary>
    public long ItemFrame { get; init; }
    /// <summary>アイテムの長さ（フレーム数）</summary>
    public long ItemLength { get; init; }
    /// <summary>FPS</summary>
    public int Fps { get; init; }
}

/// <summary>
/// キャプチャ結果を保持するスナップショット。
/// </summary>
internal sealed class CapturedFrame : IDisposable
{
    public List<CapturedItem> Items { get; } = new();

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
    }
}

/// <summary>
/// TimelineSource.Update() を Postfix パッチして、
/// 全アイテムの DrawDescription + PreRenderOutput を読み取り専用でキャプチャする。
/// YMM4 の通常プレビューには一切影響しない。
/// D3Dエフェクトが検出された場合、エフェクト情報も合わせてキャプチャする。
/// </summary>
internal static class Preview3DPatch
{
    // ── 最新キャプチャ結果（スレッドセーフに交換） ──
    private static readonly object _lock = new();
    private static CapturedFrame? _latestFrame;

    // ── 独立デバイス (D3D11Hostのデバイス、スレッドセーフ) ──
    private static ID3D11Device? _independentDevice;

    /// <summary>独立デバイスを設定 (D3D11Host初期化時に呼ぶ)</summary>
    public static void SetIndependentDevice(ID3D11Device? device) => _independentDevice = device;

    public static ID3D11Device? IndependentDevice => _independentDevice;

    /// <summary>最新フレームを明示的に破棄（プロジェクト切り替え時のメモリリーク防止）</summary>
    public static void ClearLatestFrame()
    {
        lock (_lock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }
    }

    /// <summary>最新のキャプチャ結果を取得</summary>
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

    // ── D3Dエフェクト検出用キャッシュ (リフレクション) ──
    private static bool _d3dEffectSearched;
    private static Type? _id3dVideoEffectType;
    private static PropertyInfo? _d3dEffectIdProp;

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

        // FPS/フレーム用キャッシュ初期化 (見つからなくてもパッチ自体は続行)
        var sceneType = FindType("YukkuriMovieMaker.Project.Scene");
        if (sceneType != null)
        {
            _sceneCurrentFrameProp = sceneType.GetProperty("CurrentFrame", BindingFlags.Instance | BindingFlags.Public);
            _sceneTimelineProp = sceneType.GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public);
        }
        var timelineType = FindType("YukkuriMovieMaker.Project.Timeline");
        if (timelineType != null)
        {
            _timelineVideoInfoProp = timelineType.GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public);
        }
        var videoInfoType = FindType("YukkuriMovieMaker.Project.VideoInfo");
        if (videoInfoType != null)
        {
            _videoInfoFpsProp = videoInfoType.GetProperty("FPS", BindingFlags.Instance | BindingFlags.Public);
        }

        var updateMethod = _timelineSourceType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public);
        if (updateMethod == null) { Log("Update() が見つかりません。"); return; }

        harmony.Patch(updateMethod, postfix: new HarmonyMethod(typeof(Preview3DPatch), nameof(UpdatePostfix)));
        Log("TimelineSource.Update() Postfix パッチ適用完了。");

        // アプリ終了時に未消費フレームを確実に解放する安全網
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ClearLatestFrame();
    }

    /// <summary>
    /// D3Dエフェクトの型情報を遅延検索する。
    /// Iyahon_D3D11Renderer_Core がロードされていなければ null のまま。
    /// </summary>
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
                Log($"D3Dエフェクト型を検出: ID3DVideoEffect, D3DEffectId={_d3dEffectIdProp != null}");
            }
            else
            {
                Log("Iyahon_D3D11Renderer_Core が未ロード — D3Dエフェクト検出をスキップ。");
            }
        }
        catch (Exception ex)
        {
            Log($"D3Dエフェクト型検索エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// アイテムの VideoEffects から ID3DVideoEffect を検出し、エフェクトIDを返す。
    /// </summary>
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
        catch (Exception ex)
        {
            Log($"D3Dエフェクト検出エラー: {ex.Message}");
        }

        return (null, null);
    }

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

            // 3Dプレビューが開かれていない（独立デバイス未設定）場合は
            // テクスチャを作っても誰も消費しないので早期リターン。
            // これがメモリリークの根本原因を防ぐ。
            if (_independentDevice == null) return;

            var dc = devices.DeviceContext;
            var frame = new CapturedFrame();

            foreach (object pair in (IEnumerable)resources)
            {
                var key = _kvpKeyProp!.GetValue(pair) as IVideoItem;
                var value = _kvpValueProp!.GetValue(pair);
                if (key == null || value == null) continue;

                var esoList = _effectedSourceOutputsField!.GetValue(value) as IList;
                if (esoList == null || esoList.Count == 0) continue;

                // 3Dプレビューで非表示設定ならスキップ
                if (Preview3DVisibilityState.IsHiddenIn3DPreview(key)) continue;

                // D3Dエフェクトの検出
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

                    // YMM43D方式: 独立デバイスにテクスチャを作成 (後の SRV 作成が軽量)
                    // ※ _independentDevice == null のときは UpdatePostfix の先頭で早期リターン済みなので
                    //   ここに到達する場合は必ず non-null。
                    ID3D11Texture2D? d3dTex = D2DD3DBridge.CreateOnIndependentDevice(
                        preRenderOutput, devices, _independentDevice, texW, texH, -left, -top);
                    if (d3dTex == null) continue;

                    float cx = left + texW / 2f;
                    float cy = top + texH / 2f;

                    frame.Items.Add(new CapturedItem
                    {
                        DrawDescription = drawDesc,
                        Texture = d3dTex,
                        IsOnIndependentDevice = (_independentDevice != null),
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

    private static int? GetSceneWidth(object? scene)
    {
        if (scene == null) return null;
        try
        {
            var tl = scene.GetType().GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene);
            var vi = tl?.GetType().GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public)?.GetValue(tl);
            return (int?)vi?.GetType().GetProperty("Width", BindingFlags.Instance | BindingFlags.Public)?.GetValue(vi);
        }
        catch { return null; }
    }

    private static int? GetSceneHeight(object? scene)
    {
        if (scene == null) return null;
        try
        {
            var tl = scene.GetType().GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene);
            var vi = tl?.GetType().GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public)?.GetValue(tl);
            return (int?)vi?.GetType().GetProperty("Height", BindingFlags.Instance | BindingFlags.Public)?.GetValue(vi);
        }
        catch { return null; }
    }

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Easy3DPreview.Preview3D] Patch: {msg}");
}