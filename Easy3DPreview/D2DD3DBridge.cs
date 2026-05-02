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
/// Iyahon_D3D11Renderer_Core の D2DD3DBridge を参考にした独立コピー。
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
            try
            {
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
            catch
            {
                texture?.Dispose();
                throw;
            }
        }
        catch { return null; }
    }

    public static ID3D11Texture2D? BakeToD3DTexture(
        ID2D1Image image, IGraphicsDevicesAndContext devices, int width, int height, float offsetX = 0f, float offsetY = 0f)
    {
        try
        {
            var result = CreateSharedTexture(devices, width, height);
            if (result == null) return null;
            var (texture, bitmap) = result.Value;

            var dc = devices.DeviceContext;
            var prevTarget = dc.Target;

            try
            {
                dc.Target = bitmap;
                dc.BeginDraw();
                dc.Clear(new Color4(0f, 0f, 0f, 0f));
                dc.DrawImage(image, new Vector2(offsetX, offsetY), null, InterpolationMode.Linear, CompositeMode.SourceOver);
                dc.EndDraw();
                return texture;
            }
            catch
            {
                texture?.Dispose();
                throw;
            }
            finally
            {
                // 必ず元の Target を復元する
                dc.Target = prevTarget;
                bitmap.Dispose();
            }
        }
        catch { return null; }
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
