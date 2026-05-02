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
    public IntPtr SharedHandle { get; set; }
    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }
    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }
    public float Opacity { get; init; } = 1f;
    public int Layer { get; init; }
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
/// </summary>
internal static class Preview3DPatch
{
    // ── 最新キャプチャ結果（スレッドセーフに交換） ──
    private static readonly object _lock = new();
    private static CapturedFrame? _latestFrame;
    private static int _captureConsumers = 0;

    public static void RegisterConsumer()
    {
        System.Threading.Interlocked.Increment(ref _captureConsumers);
    }

    public static void UnregisterConsumer()
    {
        if (System.Threading.Interlocked.Decrement(ref _captureConsumers) <= 0)
        {
            lock (_lock)
            {
                _latestFrame?.Dispose();
                _latestFrame = null;
            }
        }
    }

    /// <summary>
    /// 最新のキャプチャ結果を取得（呼び出し側が Dispose 責任を持つ）。
    /// </summary>
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

    private static FieldInfo? _effectedSourceOutputsField;
    private static PropertyInfo? _kvpKeyProp;
    private static PropertyInfo? _kvpValueProp;

    private static Type? _effectedSourceOutputType;
    private static PropertyInfo? _esoPreRenderOutputProp;
    private static PropertyInfo? _esoDrawDescProp;

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

        var updateMethod = _timelineSourceType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public);
        if (updateMethod == null) { Log("Update() が見つかりません。"); return; }

        harmony.Patch(updateMethod, postfix: new HarmonyMethod(typeof(Preview3DPatch), nameof(UpdatePostfix)));
        Log("TimelineSource.Update() Postfix パッチ適用完了。");
    }

    private static void UpdatePostfix(object __instance)
    {
        if (System.Threading.Volatile.Read(ref _captureConsumers) <= 0) return;

        try
        {
            var devices = _devicesField!.GetValue(__instance) as IGraphicsDevicesAndContext;
            if (devices == null) return;

            var resources = _timelineResourcesField!.GetValue(__instance);
            if (resources == null) return;

            var dc = devices.DeviceContext;
            var frame = new CapturedFrame();
            bool frameProcessed = false;

            try
            {
                // ── D3D11 の状態を必ず保存 ──
                using (new D3D11StateSaver(devices.D3D.DeviceContext))
                {
                    foreach (object pair in (IEnumerable)resources)
                    {
                        var key = _kvpKeyProp!.GetValue(pair) as IVideoItem;
                        var value = _kvpValueProp!.GetValue(pair);
                        if (key == null || value == null) continue;

                        var esoList = _effectedSourceOutputsField!.GetValue(value) as IList;
                        if (esoList == null || esoList.Count == 0) continue;

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

                            const int MaxTexSize = 4096;
                            int texW = Math.Min((int)pw, MaxTexSize);
                            int texH = Math.Min((int)ph, MaxTexSize);
                            if (texW <= 0 || texH <= 0) continue;

                            var d3dTex = D2DD3DBridge.BakeToD3DTexture(preRenderOutput, devices, texW, texH, -left, -top);
                            if (d3dTex == null) continue;

                            try
                            {
                                using var dxgiResource = d3dTex.QueryInterface<Vortice.DXGI.IDXGIResource>();
                                if (dxgiResource == null)
                                {
                                    d3dTex.Dispose();
                                    continue;
                                }

                                float cx = left + texW / 2f;
                                float cy = top + texH / 2f;

                                frame.Items.Add(new CapturedItem
                                {
                                    DrawDescription = drawDesc,
                                    Texture = d3dTex,
                                    SharedHandle = dxgiResource.SharedHandle,
                                    PixelWidth = texW,
                                    PixelHeight = texH,
                                    BoundsCenterX = cx,
                                    BoundsCenterY = cy,
                                    Opacity = (float)drawDesc.Opacity,
                                    Layer = key.Layer,
                                });
                            }
                            catch
                            {
                                d3dTex.Dispose();
                                throw;
                            }
                        }
                    }

                    lock (_lock)
                    {
                        _latestFrame?.Dispose();
                        _latestFrame = frame;
                    }
                    frameProcessed = true;
                }
            }
            finally
            {
                if (!frameProcessed)
                {
                    frame.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"UpdatePostfix 例外: {ex.GetType().Name}: {ex.Message}");
        }
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
