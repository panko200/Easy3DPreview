using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// D2D1 画像を D3D11 テクスチャに変換するヘルパー。
/// テクスチャプール対応: 既存テクスチャへの再描画をサポート。
/// </summary>
internal static class D2DD3DBridge
{
    public static (ID3D11Texture2D texture, ID2D1Bitmap1 bitmap)? CreateSharedTexture(
        IGraphicsDevicesAndContext devices, int width, int height)
    {
        try
        {
            var texDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared,
            };

            var texture = devices.D3D.Device.CreateTexture2D(texDesc);
            using var surface = texture.QueryInterface<IDXGISurface>();

            float dpiX = devices.DeviceContext.Dpi.Width;
            float dpiY = devices.DeviceContext.Dpi.Height;

            var bitmapProps = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dpiX, dpiY, BitmapOptions.Target
            );

            var bitmap = devices.DeviceContext.CreateBitmapFromDxgiSurface(surface, bitmapProps);
            return (texture, bitmap);
        }
        catch { return null; }
    }

    /// <summary>
    /// 【旧方式】YMM4のデバイスにテクスチャを作成してD2D画像をベイクする。
    /// </summary>
    public static ID3D11Texture2D? BakeToD3DTexture(
        ID2D1Image image, IGraphicsDevicesAndContext devices, int width, int height,
        float offsetX = 0f, float offsetY = 0f)
    {
        try
        {
            var result = CreateSharedTexture(devices, width, height);
            if (result == null) return null;
            var (texture, bitmap) = result.Value;

            // ─── 追加：YMM4側のD3D11コンテキストステートを退避 ───
            var (_, ymmCtx) = Preview3DPatch.GetYmmD3DExternal(devices);
            using var stateSaver = ymmCtx != null ? new D3D11StateSaver(ymmCtx) : null;

            var dc = devices.DeviceContext;
            var prevTarget = dc.Target;
            try
            {
                dc.Target = bitmap;
                dc.BeginDraw();
                dc.Clear(new Color4(0f, 0f, 0f, 0f));
                dc.DrawImage(image, new Vector2(offsetX, offsetY), null, InterpolationMode.Linear, CompositeMode.SourceOver);
                dc.EndDraw();
            }
            finally
            {
                dc.Target = prevTarget;
                bitmap.Dispose();
            }

            return texture;
        }
        catch { return null; }
    }

    /// <summary>
    /// 【YMM43D方式】独立デバイスにテクスチャを作成し、YMM4のD2Dで描画する。
    /// 戻り値のテクスチャは独立デバイス上にあるため、SRV作成にOpenSharedResourceが不要。
    /// </summary>
    public static ID3D11Texture2D? CreateOnIndependentDevice(
        ID2D1Image image, IGraphicsDevicesAndContext devices,
        ID3D11Device independentDevice, int width, int height,
        float offsetX = 0f, float offsetY = 0f)
    {
        try
        {
            // 1. 独立デバイスに共有テクスチャを作成
            var renderTexture = independentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared,
            });

            // 2. 共有ハンドル経由でYMM4のデバイスで開く
            using var dxgiRes = renderTexture.QueryInterface<IDXGIResource>();
            nint sharedHandle = dxgiRes.SharedHandle;

            using var ymmTexture = devices.D3D.Device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
            using var surface = ymmTexture.QueryInterface<IDXGISurface>();

            // ─── 追加：YMM4側のD3D11コンテキストステートを退避 ───
            var (_, ymmCtx) = Preview3DPatch.GetYmmD3DExternal(devices);
            using var stateSaver = ymmCtx != null ? new D3D11StateSaver(ymmCtx) : null;

            // 3. YMM4のD2DコンテキストでD2D画像を描画
            var dc = devices.DeviceContext;
            float dpiX = dc.Dpi.Width;
            float dpiY = dc.Dpi.Height;
            var bitmapProps = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dpiX, dpiY, BitmapOptions.Target);

            using var bitmap = dc.CreateBitmapFromDxgiSurface(surface, bitmapProps);
            var prevTarget = dc.Target;
            try
            {
                dc.Target = bitmap;
                dc.BeginDraw();
                dc.Clear(new Color4(0f, 0f, 0f, 0f));
                dc.DrawImage(image, new Vector2(offsetX, offsetY), null, InterpolationMode.Linear, CompositeMode.SourceOver);
                dc.EndDraw();
            }
            finally
            {
                dc.Target = prevTarget;
            }

            // usingステートメントを抜ける（stateSaverがDisposeされる）ことで、
            // D2Dによって変更されたD3D11イミディエイトコンテキストの状態が自動的に元の状態に修復されます。

            return renderTexture;
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"CreateOnIndependentDevice エラー: {ex.Message}");
            return null;
        }
    }

    public static ID3D11ShaderResourceView? CreateSrv(ID3D11Texture2D texture, ID3D11Device d3dDevice)
    {
        try
        {
            var srvDesc = new ShaderResourceViewDescription
            {
                Format = Format.B8G8R8A8_UNorm,
                ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
            };
            return d3dDevice.CreateShaderResourceView(texture, srvDesc);
        }
        catch { return null; }
    }

    public static ID2D1Bitmap1? GetD2DBitmapFromD3DTexture(ID3D11Texture2D renderTexture, IGraphicsDevicesAndContext devices)
    {
        try
        {
            using var surface = renderTexture.QueryInterface<IDXGISurface>();
            if (surface == null) return null;

            float dpiX = devices.DeviceContext.Dpi.Width;
            float dpiY = devices.DeviceContext.Dpi.Height;

            var bitmapProps = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                dpiX, dpiY, BitmapOptions.None
            );
            return devices.DeviceContext.CreateBitmapFromDxgiSurface(surface, bitmapProps);
        }
        catch { return null; }
    }
}
