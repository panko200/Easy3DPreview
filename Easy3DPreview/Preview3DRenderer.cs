using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
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

    // デバイスは外部 (D3D11Host) から受け取る
    private ID3D11Device? _d3d;

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

    private bool _initialized;
    private bool _disposed;

    // ── D3Dエフェクト (リフレクション経由) ──
    private readonly D3DEffectHelper _effectHelper = new();

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

    /// <summary>
    /// 外部デバイスでシェーダ等を初期化する。
    /// D3D11Host のデバイス作成後に一度だけ呼ぶ。
    /// </summary>
    public bool Initialize(ID3D11Device device)
    {
        if (_initialized && _d3d == device) return true;

        Dispose(); // Clean up old resources if any
        _disposed = false;
        _initialized = false;

        _d3d = device;

        try
        {
            if (!InitializeShaders()) return false;
            if (!InitializeStates()) return false;
            InitializeGeometry();

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
    /// <summary>
    /// 外部の RTV/DSV に描画する。D3D11Host の SwapChain バックバッファを想定。
    /// </summary>
    public void Render(
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView rtv,
        ID3D11DepthStencilView dsv,
        int width, int height,
        List<UiItem> items, CameraController camera,
        int screenWidth, int screenHeight)
    {
        if (!_initialized || _d3d == null) return;

        float aspectRatio = (float)width / height;
        var viewMatrix = camera.GetViewMatrix();
        var projMatrix = camera.GetProjectionMatrix(aspectRatio);
        var viewProj = viewMatrix * projMatrix;

        try
        {
            // クリア (ダークグレー背景)
            ctx.ClearRenderTargetView(rtv, new Color4(0.15f, 0.15f, 0.18f, 1f));
            ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1.0f, 0);

            ctx.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
            ctx.RSSetState(_rasterizerState);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            ctx.OMSetRenderTargets(rtv, dsv);
            ctx.OMSetBlendState(_blendState, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthState, 0);

            // シーン定数バッファを更新
            var cbScene = new CbScene
            {
                ViewProj = viewProj,
                HalfWidth = screenWidth / 2f,
                HalfHeight = screenHeight / 2f,
            };
            ctx.UpdateSubresource(ref cbScene, _cbScene!);
            ctx.VSSetConstantBuffer(0, _cbScene);
            ctx.PSSetConstantBuffer(0, _cbScene);

            // ── グリッド描画 ──
            DrawGrid(ctx);

            // ── アイテム描画 ──
            foreach (var item in items)
            {
                if (item.Srv == null) continue;

                var world = BuildWorldMatrix(item);

                // D3Dエフェクトがある場合はエフェクトで描画
                if (item.D3DEffectId != null && _effectHelper.IsAvailable)
                {
                    try
                    {
                        if (_effectHelper.RenderEffect(
                            ctx, _d3d!, item.Srv, item, world, viewProj, camera.CameraPosition))
                        {
                            // エフェクト描画後、パイプライン状態を復元
                            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                            ctx.IASetInputLayout(_inputLayout);
                            ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                            ctx.VSSetShader(_vs);
                            ctx.PSSetShader(_ps);
                            ctx.PSSetSampler(0, _sampler);
                            ctx.VSSetConstantBuffer(0, _cbScene);
                            ctx.PSSetConstantBuffer(0, _cbScene);
                            ctx.VSSetConstantBuffer(1, _cbPerObject);
                            ctx.PSSetConstantBuffer(1, _cbPerObject);
                            ctx.RSSetState(_rasterizerState);
                            ctx.OMSetBlendState(_blendState, null, unchecked((int)0xFFFFFFFF));
                            ctx.OMSetDepthStencilState(_depthState, 0);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Preview3DPlugin.Log($"D3Dエフェクト描画エラー: {ex.Message}");
                    }
                }

                // 通常の板ポリ描画
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                ctx.IASetInputLayout(_inputLayout);
                ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                ctx.VSSetShader(_vs);
                ctx.PSSetShader(_ps);
                ctx.PSSetSampler(0, _sampler);
                ctx.VSSetConstantBuffer(1, _cbPerObject);
                ctx.PSSetConstantBuffer(1, _cbPerObject);

                var cbObj = new CbPerObject
                {
                    WorldMatrix = world,
                    Opacity = item.Opacity,
                };
                ctx.UpdateSubresource(ref cbObj, _cbPerObject!);
                ctx.PSSetShaderResource(0, item.Srv);
                ctx.Draw(4, 0);
            }

            // ── 外部アドオン（物理演算オブジェクトやMMDなど）の描画 ──
            var addons = Easy3DPreviewAPI.Addons;
            foreach (var addon in addons)
            {
                // アドオンごとに状態を退避 → 描画 → 自動復元
                using var stateSaver = new D3D11StateSaver(ctx);
                try
                {
                    addon.Render(ctx, _d3d!, viewProj, camera.CameraPosition);
                }
                catch (Exception ex)
                {
                    Preview3DPlugin.Log($"外部アドオン描画エラー ({addon.GetType().Name}): {ex.Message}");
                }
                // スコープ終了で stateSaver.Dispose() が走り、全ステートが復元される
            }

            // レンダーターゲットをアンバインド
            ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"Render エラー: {ex.Message}");
        }
    }


    private void DrawGrid(ID3D11DeviceContext ctx)
    {
        if (_gridVertexBuffer == null || _gridVs == null || _gridPs == null) return;

        ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _gridVertexBuffer, Marshal.SizeOf<Vertex>(), 0);
        ctx.VSSetShader(_gridVs);
        ctx.PSSetShader(_gridPs);
        ctx.Draw(_gridVertexCount, 0);
    }

    /// <summary>
    /// YMM4 の DrawDescription からワールド行列を構築。
    /// 3Dプレビューでは独自のカメラ (ViewProj) を使うため、
    /// YMM4 の Camera 行列と d2dProj は含めない。
    /// アイテム自身の座標・回転・拡大のみでワールド位置を決定する。
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

        // Camera 行列と d2dProj は除外:
        // - d2dProj: YMM4 内部の疑似3D投影 → 3Dプレビューは独自 ViewProj を使用
        // - Camera: YMM4 のカメラエフェクト → 3Dプレビューではアイテム位置に影響させない
        return S * Toffset * Zoom * Rz * Ry * Rx * Tdraw;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _effectHelper.Dispose();
        // デバイスは D3D11Host が所有するので Dispose しない
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

    // ═══════════════════════════════════════════════════════
    // D3Dエフェクト リフレクションヘルパー
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Iyahon_D3D11Renderer_Core の D3DEffectRegistry / ID3DEffect / D3DRenderContext を
    /// リフレクション経由で呼び出すヘルパー。コンパイル時依存なし。
    /// </summary>
    private sealed class D3DEffectHelper : IDisposable
    {
        private bool _searched;
        private bool _available;

        // D3DEffectRegistry
        private MethodInfo? _createEffectMethod;

        // ID3DEffect
        private MethodInfo? _effectInitializeMethod;
        private MethodInfo? _effectRenderMethod;
        private MethodInfo? _effectDisposeMethod;

        // D3DRenderContext
        private Type? _renderContextType;

        // ID3DVideoEffect
        private MethodInfo? _configureEffectMethod;

        // エフェクトキャッシュ (effectId -> instance)
        private readonly Dictionary<string, object> _effectCache = new();

        public bool IsAvailable
        {
            get
            {
                EnsureSearched();
                return _available;
            }
        }

        private void EnsureSearched()
        {
            if (_searched) return;
            _searched = true;

            try
            {
                var registryType = FindType("Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry");
                var effectType = FindType("Iyahon_D3D11Renderer_Core.D3DEffect.ID3DEffect");
                _renderContextType = FindType("Iyahon_D3D11Renderer_Core.D3DEffect.D3DRenderContext");
                var videoEffectType = FindType("Iyahon_D3D11Renderer_Core.D3DEffect.ID3DVideoEffect");

                if (registryType == null || effectType == null || _renderContextType == null)
                {
                    Preview3DPlugin.Log("D3DEffectHelper: Iyahon_D3D11Renderer_Core 未検出。");
                    return;
                }

                _createEffectMethod = registryType.GetMethod("CreateEffect",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);

                _effectInitializeMethod = effectType.GetMethod("Initialize");
                _effectRenderMethod = effectType.GetMethod("Render");
                _effectDisposeMethod = typeof(IDisposable).GetMethod("Dispose");

                if (videoEffectType != null)
                {
                    _configureEffectMethod = videoEffectType.GetMethod("ConfigureEffect");
                }

                _available = _createEffectMethod != null &&
                             _effectInitializeMethod != null &&
                             _effectRenderMethod != null;

                Preview3DPlugin.Log($"D3DEffectHelper: available={_available}");
            }
            catch (Exception ex)
            {
                Preview3DPlugin.Log($"D3DEffectHelper 初期化エラー: {ex.Message}");
            }
        }

        public bool RenderEffect(
            ID3D11DeviceContext ctx, ID3D11Device device,
            ID3D11ShaderResourceView srv, UiItem item,
            Matrix4x4 worldMatrix, Matrix4x4 viewProj, Vector3 cameraPos)
        {
            if (!_available || item.D3DEffectId == null) return false;

            // エフェクトインスタンスを取得またはキャッシュから作成
            if (!_effectCache.TryGetValue(item.D3DEffectId, out var effect))
            {
                effect = _createEffectMethod!.Invoke(null, new object[] { item.D3DEffectId });
                if (effect == null) return false;
                _effectCache[item.D3DEffectId] = effect;
            }

            // Initialize
            _effectInitializeMethod!.Invoke(effect, new object[] { device, ctx });

            // ConfigureEffect (VideoEffect のパラメータを設定)
            if (_configureEffectMethod != null && item.D3DVideoEffect != null)
            {
                _configureEffectMethod.Invoke(item.D3DVideoEffect,
                    new object[] { effect, item.ItemFrame, item.ItemLength, item.Fps });
            }

            // D3DRenderContext を構築
            var renderContext = Activator.CreateInstance(_renderContextType!);
            if (renderContext == null) return false;

            var rcType = _renderContextType!;
            rcType.GetProperty("WorldMatrix")!.SetValue(renderContext, worldMatrix);
            rcType.GetProperty("ViewProjectionMatrix")!.SetValue(renderContext, viewProj);
            rcType.GetProperty("CameraWorldPosition")!.SetValue(renderContext, cameraPos);
            rcType.GetProperty("TextureWidth")!.SetValue(renderContext, (int)item.PixelWidth);
            rcType.GetProperty("TextureHeight")!.SetValue(renderContext, (int)item.PixelHeight);
            rcType.GetProperty("HalfScreenWidth")!.SetValue(renderContext, 0f); // 0 = ViewProj mode
            rcType.GetProperty("HalfScreenHeight")!.SetValue(renderContext, 0f);
            rcType.GetProperty("Opacity")!.SetValue(renderContext, item.Opacity);
            rcType.GetProperty("AlphaThreshold")!.SetValue(renderContext, 0.004f);
            rcType.GetProperty("CameraMatrix")!.SetValue(renderContext, item.DrawDescription.Camera);

            // Render
            _effectRenderMethod!.Invoke(effect, new object[] { ctx, device, srv, renderContext });

            return true;
        }

        public void Dispose()
        {
            foreach (var effect in _effectCache.Values)
            {
                try { _effectDisposeMethod?.Invoke(effect, null); }
                catch { }
            }
            _effectCache.Clear();
        }

        private static Type? FindType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == fullName);
    }
}
