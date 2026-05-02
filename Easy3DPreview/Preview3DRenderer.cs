using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// 3Dプレビュー専用の D3D11 レンダラー。
/// CapturedItem のリストを受け取り、独自のカメラ行列で再描画する。
/// </summary>
internal sealed class Preview3DRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbScene
    {
        public Matrix4x4 ViewProj;      // 64 bytes
        public float HalfWidth;          // 4
        public float HalfHeight;         // 4
        public float _pad0;              // 4
        public float _pad1;              // 4  → 合計 80 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerObject
    {
        public Matrix4x4 WorldMatrix;   // 64 bytes
        public float Opacity;           // 4
        public float _pad0;             // 4
        public float _pad1;             // 4
        public float _pad2;             // 4  → 合計 80 bytes
    }

    private ID3D11Device? _d3d;
    private ID3D11DeviceContext? _ctx;

    // ── レンダーターゲット ──
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _renderTarget;
    private ID3D11Texture2D? _stagingTexture;
    private ID3D11DepthStencilView? _dsv;
    private ID3D11Texture2D? _depthStencil;

    // ── シェーダ/パイプライン ──
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _gridVertexBuffer;
    private int _gridVertexCount;
    private ID3D11Buffer? _cbScene;
    private ID3D11Buffer? _cbPerObject;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _blendState;
    private ID3D11DepthStencilState? _depthState;
    private ID3D11RasterizerState? _rasterizerState;

    // ── グリッド描画用 ──
    private ID3D11VertexShader? _gridVs;
    private ID3D11PixelShader? _gridPs;

    private int _width;
    private int _height;
    private bool _initialized;
    private bool _disposed;

    // ═══════════════════════════════════════════════════
    // シェーダソース
    // ═══════════════════════════════════════════════════

    private const string ShaderSource = @"
cbuffer CbScene : register(b0)
{
    row_major float4x4 ViewProj;
    float HalfWidth;
    float HalfHeight;
    float _pad0;
    float _pad1;
};

cbuffer CbPerObject : register(b1)
{
    row_major float4x4 WorldMatrix;
    float Opacity;
    float _obj_pad0;
    float _obj_pad1;
    float _obj_pad2;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float Op : TEXCOORD1; };

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS(VSInput input)
{
    PSInput o;
    float4 worldPos = mul(float4(input.Pos, 1.0), WorldMatrix);

    o.Pos = mul(worldPos, ViewProj);
    o.UV = input.UV;
    o.Op = Opacity;
    return o;
}

float4 PS(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - 0.004);
    return c;
}
";

    private const string GridShaderSource = @"
cbuffer CbScene : register(b0)
{
    row_major float4x4 ViewProj;
    float HalfWidth;
    float HalfHeight;
    float _pad0;
    float _pad1;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

PSInput VS_Grid(VSInput input)
{
    PSInput o;
    o.Pos = mul(float4(input.Pos, 1.0), ViewProj);
    o.UV = input.UV;
    return o;
}

float4 PS_Grid(PSInput input) : SV_Target
{
    return float4(0.3, 0.3, 0.3, 0.5);
}
";

    public ID3D11Device? Device => _d3d;

    public bool Initialize(int width, int height)
    {
        if (_initialized && _width == width && _height == height) return true;

        if (_d3d == null)
        {
            var flags = Vortice.Direct3D11.DeviceCreationFlags.BgraSupport;
            // 独立した D3D11 デバイスを作成
            if (Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                null,
                Vortice.Direct3D.DriverType.Hardware,
                flags,
                null,
                out _d3d,
                out _ctx).Failure)
            {
                return false;
            }
        }

        DisposeTargets();
        _width = width;
        _height = height;

        try
        {
            // レンダーターゲット
            _renderTarget = _d3d.CreateTexture2D(new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _rtv = _d3d.CreateRenderTargetView(_renderTarget);

            // CPU読み出し用ステージングテクスチャ
            _stagingTexture = _d3d.CreateTexture2D(new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });

            // 深度バッファ
            _depthStencil = _d3d.CreateTexture2D(new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
            });
            _dsv = _d3d.CreateDepthStencilView(_depthStencil);

            if (!_initialized)
            {
                if (!InitializeShaders()) return false;
                if (!InitializeStates()) return false;
                InitializeGeometry();
            }

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"Renderer Initialize エラー: {ex.Message}");
            return false;
        }
    }

    private bool InitializeShaders()
    {
        try
        {
            // メインシェーダ
            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "VS", "inline_preview3d",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (vsBlob == null) return false;

            _vs = _d3d!.CreateVertexShader(vsBlob);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            };
            _inputLayout = _d3d.CreateInputLayout(inputElements, vsBlob);
            vsBlob.Dispose();

            var psBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "PS", "inline_preview3d",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psBlob == null) return false;
            _ps = _d3d.CreatePixelShader(psBlob);
            psBlob.Dispose();

            // グリッドシェーダ
            var gridVsBlob = Vortice.D3DCompiler.Compiler.Compile(
                GridShaderSource, "VS_Grid", "inline_grid",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (gridVsBlob == null) return false;
            _gridVs = _d3d.CreateVertexShader(gridVsBlob);
            gridVsBlob.Dispose();

            var gridPsBlob = Vortice.D3DCompiler.Compiler.Compile(
                GridShaderSource, "PS_Grid", "inline_grid",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (gridPsBlob == null) return false;
            _gridPs = _d3d.CreatePixelShader(gridPsBlob);
            gridPsBlob.Dispose();

            // 定数バッファ
            _cbScene = _d3d.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CbScene>(), BindFlags.ConstantBuffer));
            _cbPerObject = _d3d.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CbPerObject>(), BindFlags.ConstantBuffer));

            return true;
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"InitializeShaders エラー: {ex.Message}");
            return false;
        }
    }

    private bool InitializeStates()
    {
        try
        {
            _sampler = _d3d!.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            // プレマルチプライドアルファ合成
            var blendDesc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = Blend.One,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            _blendState = _d3d.CreateBlendState(blendDesc);

            _depthState = _d3d.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.Less,
                StencilEnable = false,
            });

            _rasterizerState = _d3d.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"InitializeStates エラー: {ex.Message}");
            return false;
        }
    }

    private unsafe void InitializeGeometry()
    {
        // アイテム描画用クワッド (単位四角形, 中心原点)
        var vertices = new Vertex[]
        {
            new() { Position = new Vector3(-0.5f, -0.5f, 0f), TexCoord = new Vector2(0f, 0f) },
            new() { Position = new Vector3( 0.5f, -0.5f, 0f), TexCoord = new Vector2(1f, 0f) },
            new() { Position = new Vector3(-0.5f,  0.5f, 0f), TexCoord = new Vector2(0f, 1f) },
            new() { Position = new Vector3( 0.5f,  0.5f, 0f), TexCoord = new Vector2(1f, 1f) },
        };

        int stride = Marshal.SizeOf<Vertex>();
        int totalBytes = stride * vertices.Length;
        fixed (Vertex* pVerts = vertices)
        {
            _vertexBuffer = _d3d!.CreateBuffer(
                new BufferDescription(totalBytes, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pVerts, totalBytes));
        }

        // グリッド線 (XZ 平面、Y=0)
        var gridVerts = new List<Vertex>();
        const float gridSize = 2000f;
        const float gridStep = 200f;
        int lineCount = (int)(gridSize / gridStep) * 2 + 1;

        for (int i = 0; i < lineCount; i++)
        {
            float pos = -gridSize + i * gridStep;
            // X 方向の線 (Z を変化させる)
            gridVerts.Add(new Vertex { Position = new Vector3(-gridSize, 0f, pos), TexCoord = Vector2.Zero });
            gridVerts.Add(new Vertex { Position = new Vector3(gridSize, 0f, pos), TexCoord = Vector2.Zero });
            // Z 方向の線 (X を変化させる)
            gridVerts.Add(new Vertex { Position = new Vector3(pos, 0f, -gridSize), TexCoord = Vector2.Zero });
            gridVerts.Add(new Vertex { Position = new Vector3(pos, 0f, gridSize), TexCoord = Vector2.Zero });
        }

        _gridVertexCount = gridVerts.Count;
        var gridArray = gridVerts.ToArray();
        int gridBytes = stride * gridArray.Length;
        fixed (Vertex* pVerts = gridArray)
        {
            _gridVertexBuffer = _d3d.CreateBuffer(
                new BufferDescription(gridBytes, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pVerts, gridBytes));
        }
    }

    /// <summary>
    /// キャプチャされたアイテムを独自カメラで描画し、結果テクスチャを返す。
    /// </summary>
    public ID3D11Texture2D? Render(List<UiItem> items, CameraController camera, int screenWidth, int screenHeight)
    {
        if (!_initialized || _rtv == null || _dsv == null || _ctx == null) return null;

        float aspectRatio = (float)_width / _height;
        var viewMatrix = camera.GetViewMatrix();
        var projMatrix = camera.GetProjectionMatrix(aspectRatio);
        var viewProj = viewMatrix * projMatrix;

        try
        {
            // クリア (ダークグレー背景)
            _ctx.ClearRenderTargetView(_rtv!, new Color4(0.15f, 0.15f, 0.18f, 1f));
            _ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1.0f, 0);

            _ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0f, 1f));
            _ctx.RSSetState(_rasterizerState);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            _ctx.OMSetRenderTargets(_rtv!, _dsv);
            _ctx.OMSetBlendState(_blendState, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthState, 0);

            // シーン定数バッファを更新
            var cbScene = new CbScene
            {
                ViewProj = viewProj,
                HalfWidth = screenWidth / 2f,
                HalfHeight = screenHeight / 2f,
            };
            _ctx.UpdateSubresource(ref cbScene, _cbScene!);
            _ctx.VSSetConstantBuffer(0, _cbScene);
            _ctx.PSSetConstantBuffer(0, _cbScene);

            // ── グリッド描画 ──
            DrawGrid();

            // ── アイテム描画 ──
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _ctx.IASetInputLayout(_inputLayout);
            _ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            _ctx.VSSetShader(_vs);
            _ctx.PSSetShader(_ps);
            _ctx.PSSetSampler(0, _sampler);

            _ctx.VSSetConstantBuffer(1, _cbPerObject);
            _ctx.PSSetConstantBuffer(1, _cbPerObject);

            foreach (var item in items)
            {
                if (item.Srv == null) continue;

                var world = BuildWorldMatrix(item);
                var cbObj = new CbPerObject
                {
                    WorldMatrix = world,
                    Opacity = item.Opacity,
                };
                _ctx.UpdateSubresource(ref cbObj, _cbPerObject!);
                _ctx.PSSetShaderResource(0, item.Srv);
                _ctx.Draw(4, 0);
            }

            // レンダーターゲットをアンバインド
            _ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);

            return _renderTarget;
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"Render エラー: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// レンダリング結果を WriteableBitmap にコピーする。
    /// Staging Texture を使って CPU 側にピクセルデータを読み出す。
    /// </summary>
    public unsafe bool CopyToBitmap(ID3D11Texture2D source, System.Windows.Media.Imaging.WriteableBitmap bitmap)
    {
        if (_d3d == null || _ctx == null) return false;

        try
        {
            var desc = source.Description;
            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            using var stagingTex = _d3d.CreateTexture2D(stagingDesc);
            _ctx.CopyResource(stagingTex, source);

            var mapped = _ctx.Map(stagingTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int bufferSize = desc.Width * desc.Height * 4;
                bitmap.Lock();
                try
                {
                    if (mapped.RowPitch == desc.Width * 4)
                    {
                        System.Buffer.MemoryCopy((void*)mapped.DataPointer, (void*)bitmap.BackBuffer, bufferSize, bufferSize);
                    }
                    else
                    {
                        for (int y = 0; y < desc.Height; y++)
                        {
                            IntPtr srcPtr = mapped.DataPointer + y * mapped.RowPitch;
                            IntPtr dstPtr = bitmap.BackBuffer + y * desc.Width * 4;
                            System.Buffer.MemoryCopy((void*)srcPtr, (void*)dstPtr, desc.Width * 4, desc.Width * 4);
                        }
                    }
                    bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, desc.Width, desc.Height));
                }
                finally
                {
                    bitmap.Unlock();
                }
            }
            finally
            {
                _ctx.Unmap(stagingTex, 0);
            }

            return true;
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"CopyToBitmap エラー: {ex.Message}");
            return false;
        }
    }



    private void DrawGrid()
    {
        if (_gridVertexBuffer == null || _gridVs == null || _gridPs == null || _ctx == null) return;

        _ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        _ctx.IASetInputLayout(_inputLayout);
        _ctx.IASetVertexBuffer(0, _gridVertexBuffer, Marshal.SizeOf<Vertex>(), 0);
        _ctx.VSSetShader(_gridVs);
        _ctx.PSSetShader(_gridPs);
        _ctx.Draw(_gridVertexCount, 0);
    }

    /// <summary>
    /// YMM4 の DrawDescription からワールド行列を構築。
    /// Iyahon の BuildWorldMatrix と同じロジック（ただし d2dProj は除く）。
    /// </summary>
    private Matrix4x4 BuildWorldMatrix(UiItem item)
    {
        var desc = item.DrawDescription;
        float d2r = MathF.PI / 180f;

        var S = Matrix4x4.CreateScale(item.PixelWidth, item.PixelHeight, 1f);
        var Toffset = Matrix4x4.CreateTranslation(item.BoundsCenterX, item.BoundsCenterY, 0f);

        float zx = (float)desc.Zoom.X;
        float zy = (float)desc.Zoom.Y;
        if (desc.Invert) zx = -zx;
        var Zoom = Matrix4x4.CreateScale(zx, zy, 1f);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);
        var cam = desc.Camera;

        // YMM4 内部の d2dProj は除外（独自の ViewProj を使うため）
        // ただしカメラ行列は YMM4 が設定したものをそのまま適用
        return S * Toffset * Zoom * Rz * Ry * Rx * Tdraw * cam;
    }

    private void DisposeTargets()
    {
        _rtv?.Dispose(); _rtv = null;
        _dsv?.Dispose(); _dsv = null;
        _depthStencil?.Dispose(); _depthStencil = null;
        _stagingTexture?.Dispose(); _stagingTexture = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeTargets();
        _renderTarget?.Dispose();
        _ctx?.Dispose();
        _ctx = null;
        _d3d?.Dispose();
        _d3d = null;
        _vs?.Dispose();
        _ps?.Dispose();
        _gridVs?.Dispose();
        _gridPs?.Dispose();
        _inputLayout?.Dispose();
        _vertexBuffer?.Dispose();
        _gridVertexBuffer?.Dispose();
        _cbScene?.Dispose();
        _cbPerObject?.Dispose();
        _sampler?.Dispose();
        _blendState?.Dispose();
        _depthState?.Dispose();
        _rasterizerState?.Dispose();
    }
}
