using System;
using System.Collections;
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
/// 通常アイテム、D3Dエフェクトアイテム、OBJモデルアイテムを独自カメラで描画する。
/// ライティング定数バッファ (CbLgt / register b1) を構築して D3Dエフェクト・OBJシェーダに供給する。
/// </summary>
internal sealed class Preview3DRenderer : IDisposable
{
    // ══════════════════════════════════════════════════════════════════
    // 通常アイテム用シェーダ構造体
    // ══════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbScene
    {
        public Matrix4x4 ViewProj;
        public float HalfWidth;
        public float HalfHeight;
        public float _pad0;
        public float _pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerObject
    {
        public Matrix4x4 WorldMatrix;
        public float Opacity;
        public float _pad0;
        public float _pad1;
        public float _pad2;
    }

    // ══════════════════════════════════════════════════════════════════
    // ライティング定数バッファ構造体 (CbLgt — b1)
    // LightingShaderCode.HlslCode 内の cbuffer CbLgt と完全一致が必要。
    // ══════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewGpuShadowData   // 112 bytes
    {
        public Matrix4x4 LightViewProj0; // 64
        public Vector4 ShadowParams;      // 16
        public Vector4 AtlasParams;       // 16
        public Vector4 DepthParams;       // 16
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewGpuLightData    // 64 bytes
    {
        public Vector4 PositionAndType;
        public Vector4 DirectionAndIntensity;
        public Vector4 ColorAndRange;
        public Vector4 SpotParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewCbLighting      // 1456 bytes  (= 91×16, 16byte整合)
    {
        public int LightCount;
        public float UseSimpleLight;
        public float EnableShadow;
        public float AmbientIntensity;    // ─── 16 bytes
        public Vector4 AmbientColor;      // 16 ── 32 total

        public PreviewGpuShadowData Shadow0;  // 112 ── 144
        public PreviewGpuShadowData Shadow1;  // 112 ── 256
        public PreviewGpuShadowData Shadow2;  // 112 ── 368
        public PreviewGpuShadowData Shadow3;  // 112 ── 480
        public PreviewGpuShadowData Shadow4;  // 112 ── 592
        public PreviewGpuShadowData Shadow5;  // 112 ── 704
        public PreviewGpuShadowData Shadow6;  // 112 ── 816
        public PreviewGpuShadowData Shadow7;  // 112 ── 928

        public int ShadowCount;
        public float EnableSoftShadow;
        public Vector2 _padShadow;            // ─── 16 bytes ── 944 total

        public PreviewGpuLightData Light0;    // 64 ── 1008
        public PreviewGpuLightData Light1;    // 64 ── 1072
        public PreviewGpuLightData Light2;    // 64 ── 1136
        public PreviewGpuLightData Light3;    // 64 ── 1200
        public PreviewGpuLightData Light4;    // 64 ── 1264
        public PreviewGpuLightData Light5;    // 64 ── 1328
        public PreviewGpuLightData Light6;    // 64 ── 1392
        public PreviewGpuLightData Light7;    // 64 ── 1456

        public void SetLight(int idx, PreviewGpuLightData d)
        {
            switch (idx)
            {
                case 0: Light0 = d; break; case 1: Light1 = d; break;
                case 2: Light2 = d; break; case 3: Light3 = d; break;
                case 4: Light4 = d; break; case 5: Light5 = d; break;
                case 6: Light6 = d; break; case 7: Light7 = d; break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // OBJモデル用 per-object 定数バッファ
    // ObjModelRenderer.CbObjModel と完全一致が必要。
    // ══════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct CbObjModel
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public float HalfWidth;
        public float HalfHeight;
        public float Opacity;
        public float MinAlphaVal;
        public Vector4 BaseColor;
        public Vector3 ShadowLightPos;
        public float ShadowLightRange;
    }

    // ══════════════════════════════════════════════════════════════════
    // フィールド
    // ══════════════════════════════════════════════════════════════════

    private ID3D11Device? _d3d;

    // ── 通常アイテム ──
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _cbScene;
    private ID3D11Buffer? _cbPerObject;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _blendState;
    private ID3D11DepthStencilState? _depthState;
    private ID3D11RasterizerState? _rasterizerState;

    // ── グリッド ──
    private ID3D11VertexShader? _gridVs;
    private ID3D11PixelShader? _gridPs;
    private ID3D11Buffer? _gridVertexBuffer;
    private int _gridVertexCount;

    // ── ライティング定数バッファ (b1) + シャドウアトラス代替 ──
    private ID3D11Buffer? _cbLighting;
    private ID3D11Texture2D? _whiteShadowTex;
    private ID3D11ShaderResourceView? _whiteShadowSrv;
    private ID3D11SamplerState? _shadowSampler;

    // ── OBJモデル ──
    private ID3D11VertexShader? _objVs;
    private ID3D11PixelShader? _objPs;
    private ID3D11InputLayout? _objInputLayout;
    private ID3D11Buffer? _objCbBuffer;
    private ID3D11SamplerState? _objSampler;
    private ID3D11Texture2D? _objWhiteTex;
    private ID3D11ShaderResourceView? _objWhiteSrv;
    private bool _objPipelineReady;

    private bool _initialized;
    private bool _disposed;

    private readonly D3DEffectHelper _effectHelper = new();
    private readonly LightingHelper _lightingHelper = new();

    public ID3D11Device? Device => _d3d;

    // ══════════════════════════════════════════════════════════════════
    // 通常アイテム シェーダソース
    // ══════════════════════════════════════════════════════════════════

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

    // OBJシェーダ本体 (ライティングHLSLコードを実行時に結合)
    private const string ObjShaderPrefix = @"
cbuffer CbObjModel : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float MinAlphaVal;
    float4 BaseColor;
    float3 ShadowLightPos;
    float ShadowLightRange;
};
";

    private const string ObjShaderBody = @"
struct VSInput
{
    float3 Pos    : POSITION;
    float3 Normal : NORMAL;
    float2 UV     : TEXCOORD0;
    float4 Color  : COLOR;
};

struct PSInput
{
    float4 Pos      : SV_POSITION;
    float3 Normal   : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float4 Color    : TEXCOORD2;
    float  Op       : TEXCOORD3;
    float3 WorldPos : TEXCOORD4;
};

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS_Obj(VSInput input)
{
    PSInput o;
    float4 worldPos = mul(float4(input.Pos, 1.0), WorldMatrix);

    if (HalfWidth > 0.0)
    {
        float4 pos = mul(worldPos, ViewProjMatrix);
        o.Pos.x =  pos.x / HalfWidth;
        o.Pos.y = -pos.y / HalfHeight;
        o.Pos.z = -pos.z / 200000.0 + 0.5 * pos.w;
        o.Pos.w =  pos.w;
    }
    else
    {
        o.Pos = mul(worldPos, ViewProjMatrix);
    }

    o.Normal = normalize(mul(input.Normal, (float3x3)WorldMatrix));
    o.UV = input.UV;
    o.Color = input.Color;
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    return o;
}

float3 CalcSimpleLgtFallback(float3 normal)
{
    float3 lightDir = normalize(float3(0.3, 0.7, -1.0));
    float ndl = saturate(dot(normal, -lightDir));
    return float3(0.3, 0.3, 0.35) + float3(1.0, 0.95, 0.9) * ndl;
}

float4 PS_Obj(PSInput input) : SV_Target
{
    float4 texColor = gTex.Sample(gSampler, input.UV);
    float4 vertexColor = input.Color;
    if (vertexColor.r == 0.0 && vertexColor.g == 0.0 && vertexColor.b == 0.0 && vertexColor.a == 0.0)
        vertexColor = float4(1.0, 1.0, 1.0, 1.0);

    float4 c = texColor * vertexColor * BaseColor;
    float3 n = normalize(input.Normal);
    float3 lgtVal;

    if (UseSimpleLight > 0.5)
        lgtVal = CalcSimpleLgtFallback(n);
    else
        lgtVal = CalcDynamicLgtEff(n, input.WorldPos);

    c.rgb *= lgtVal;
    c *= input.Op;
    clip(c.a - MinAlphaVal);
    return c;
}
";

    // D3D11Renderer_Core が入っていない場合のライティングHLSLフォールバック
    // → CbLgt のバッファレイアウトを正確に定義しつつ、シンプルな固定ライトを使う
    private const string FallbackLightingHlsl = @"
#define MAX_LIGHTS 8

struct LightData
{
    float4 PositionAndType;
    float4 DirectionAndIntensity;
    float4 ColorAndRange;
    float4 SpotParams;
};

struct ShadowData
{
    row_major float4x4 LightViewProj0;
    float4 ShadowParams;
    float4 AtlasParams;
    float4 DepthParams;
};

cbuffer CbLgt : register(b1)
{
    int   LightCount;
    float UseSimpleLight;
    float EnableShadow;
    float AmbientIntensity;
    float4 AmbientColor;
    ShadowData Shadows[8];
    int ShadowCount;
    float EnableSoftShadow;
    float2 _padShadow;
    LightData Lights[MAX_LIGHTS];
};

Texture2D shadowAtlasTex : register(t2);
SamplerState shadowAtlasSampler : register(s1);

float CalcSimpleLgtEff(float3 normal, float lightIntensityParam)
{
    float3 lightDir = normalize(float3(0.3, 0.7, -1.0));
    float ndl = saturate(dot(normal, -lightDir));
    float3 col = float3(0.3, 0.3, 0.35) + float3(1.0, 0.95, 0.9) * ndl;
    return dot(col, float3(0.333, 0.333, 0.333));
}

float3 CalcDynamicLgtEff(float3 normal, float3 worldPos)
{
    float3 lightDir = normalize(float3(0.3, 0.7, -1.0));
    float ndl = saturate(dot(normal, -lightDir));
    float3 ambient = AmbientColor.rgb * AmbientIntensity;
    float3 diffuse = float3(1.0, 0.95, 0.9) * ndl;
    return ambient + diffuse;
}
";

    // ══════════════════════════════════════════════════════════════════
    // 初期化
    // ══════════════════════════════════════════════════════════════════

    public bool Initialize(ID3D11Device device)
    {
        if (_initialized && _d3d == device) return true;

        Dispose();
        _disposed = false;
        _initialized = false;
        _d3d = device;

        try
        {
            if (!InitializeShaders()) return false;
            if (!InitializeStates()) return false;
            InitializeGeometry();
            InitializeLightingResources();
            InitializeObjPipeline();

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

        var gridVerts = new List<Vertex>();
        const float gridSize = 2000f;
        const float gridStep = 200f;
        int lineCount = (int)(gridSize / gridStep) * 2 + 1;

        for (int i = 0; i < lineCount; i++)
        {
            float pos = -gridSize + i * gridStep;
            gridVerts.Add(new Vertex { Position = new Vector3(-gridSize, 0f, pos) });
            gridVerts.Add(new Vertex { Position = new Vector3(gridSize, 0f, pos) });
            gridVerts.Add(new Vertex { Position = new Vector3(pos, 0f, -gridSize) });
            gridVerts.Add(new Vertex { Position = new Vector3(pos, 0f, gridSize) });
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
    /// ライティング定数バッファとシャドウアトラス代替テクスチャを初期化する。
    /// </summary>
    private unsafe void InitializeLightingResources()
    {
        try
        {
            _cbLighting = _d3d!.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<PreviewCbLighting>(), BindFlags.ConstantBuffer));

            // シャドウアトラス (t2) の代替: 1×1 白テクスチャ
            uint white = 0xFFFFFFFF;
            _whiteShadowTex = _d3d.CreateTexture2D(new Texture2DDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            }, new SubresourceData[] { new SubresourceData((IntPtr)(&white), 4) });

            _whiteShadowSrv = _d3d.CreateShaderResourceView(_whiteShadowTex);

            // シャドウアトラスサンプラー (s1)
            _shadowSampler = _d3d.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"InitializeLightingResources エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// OBJモデル描画パイプラインを初期化する。
    /// シェーダには LightingShaderCode.HlslCode を実行時リフレクションで取得して埋め込む。
    /// D3D11Renderer_Core が存在しない場合はフォールバック定義を使用する。
    /// </summary>
    private void InitializeObjPipeline()
    {
        try
        {
            // LightingShaderCode.HlslCode をリフレクション取得 (フォールバックあり)
            string lightingHlsl = GetLightingHlsl();
            string fullObjShader = ObjShaderPrefix + lightingHlsl + ObjShaderBody;

            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                fullObjShader, "VS_Obj", "obj_preview",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (vsBlob == null) { Preview3DPlugin.Log("OBJ VS コンパイル失敗"); return; }

            _objVs = _d3d!.CreateVertexShader(vsBlob);

            var objInputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
                new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 32, 0),
            };
            _objInputLayout = _d3d.CreateInputLayout(objInputElements, vsBlob);
            vsBlob.Dispose();

            var psBlob = Vortice.D3DCompiler.Compiler.Compile(
                fullObjShader, "PS_Obj", "obj_preview",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psBlob == null) { Preview3DPlugin.Log("OBJ PS コンパイル失敗"); return; }
            _objPs = _d3d.CreatePixelShader(psBlob);
            psBlob.Dispose();

            _objCbBuffer = _d3d.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CbObjModel>(), BindFlags.ConstantBuffer));

            _objSampler = _d3d.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            CreateObjWhiteTexture();

            _objPipelineReady = true;
            Preview3DPlugin.Log("OBJパイプライン初期化完了");
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"InitializeObjPipeline エラー: {ex.Message}");
        }
    }

    private unsafe void CreateObjWhiteTexture()
    {
        uint white = 0xFFFFFFFF;
        _objWhiteTex = _d3d!.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        }, new SubresourceData[] { new SubresourceData((IntPtr)(&white), 4) });
        _objWhiteSrv = _d3d.CreateShaderResourceView(_objWhiteTex);
    }

    /// <summary>
    /// D3D11Renderer_Core から LightingShaderCode.HlslCode をリフレクション取得する。
    /// 未ロードの場合はフォールバック定義を返す。
    /// </summary>
    private static string GetLightingHlsl()
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "Iyahon_D3D11Renderer_Core.Lighting.LightingShaderCode");

            if (type != null)
            {
                var field = type.GetField("HlslCode",
                    BindingFlags.Public | BindingFlags.Static);
                if (field?.GetValue(null) is string hlsl && hlsl.Length > 0)
                {
                    Preview3DPlugin.Log("LightingShaderCode.HlslCode をリフレクションで取得。");
                    return hlsl;
                }
            }
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"LightingShaderCode取得エラー: {ex.Message}");
        }

        Preview3DPlugin.Log("LightingShaderCode フォールバックを使用。");
        return FallbackLightingHlsl;
    }

    // ══════════════════════════════════════════════════════════════════
    // 描画
    // ══════════════════════════════════════════════════════════════════

    public void Render(
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView rtv,
        ID3D11DepthStencilView dsv,
        int width, int height,
        List<UiItem> items,
        List<UiObjModel> objModels,      // ★追加
        CameraController camera,
        int screenWidth, int screenHeight)
    {
        if (!_initialized || _d3d == null) return;

        float aspectRatio = (float)width / height;
        var viewMatrix = camera.GetViewMatrix();
        var projMatrix = camera.GetProjectionMatrix(aspectRatio);
        var viewProj = viewMatrix * projMatrix;

        // ライティング定数バッファを更新 (D3DエフェクトとOBJ共用)
        UpdateLightingBuffer(ctx);

        try
        {
            ctx.ClearRenderTargetView(rtv, new Color4(0.15f, 0.15f, 0.18f, 1f));
            ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1.0f, 0);

            ctx.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
            ctx.RSSetState(_rasterizerState);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            ctx.OMSetRenderTargets(rtv, dsv);
            ctx.OMSetBlendState(_blendState, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthState, 0);

            var cbScene = new CbScene
            {
                ViewProj = viewProj,
                HalfWidth = screenWidth / 2f,
                HalfHeight = screenHeight / 2f,
            };
            ctx.UpdateSubresource(ref cbScene, _cbScene!);
            ctx.VSSetConstantBuffer(0, _cbScene);
            ctx.PSSetConstantBuffer(0, _cbScene);

            // ── グリッド ──
            DrawGrid(ctx);

            // ── OBJモデルアイテム ──
            if (_objPipelineReady && objModels.Count > 0)
            {
                DrawObjModels(ctx, objModels, viewProj, screenWidth, screenHeight);

                // パイプライン状態を通常描画用に復元
                RestoreStandardPipeline(ctx);
            }

            // ── 通常アイテム + D3Dエフェクト ──
            foreach (var item in items)
            {
                if (item.Srv == null) continue;

                var world = BuildWorldMatrix(item);

                if (item.D3DEffectId != null && _effectHelper.IsAvailable)
                {
                    try
                    {
                        if (_effectHelper.RenderEffect(
                            ctx, _d3d!, item.Srv, item, world, viewProj, camera.CameraPosition,
                            _cbLighting, _whiteShadowSrv, _shadowSampler))
                        {
                            RestoreStandardPipeline(ctx);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Preview3DPlugin.Log($"D3Dエフェクト描画エラー: {ex.Message}");
                    }
                }

                // 通常板ポリ描画
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                ctx.IASetInputLayout(_inputLayout);
                ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                ctx.VSSetShader(_vs);
                ctx.PSSetShader(_ps);
                ctx.PSSetSampler(0, _sampler);
                ctx.VSSetConstantBuffer(1, _cbPerObject);
                ctx.PSSetConstantBuffer(1, _cbPerObject);

                var cbObj = new CbPerObject { WorldMatrix = world, Opacity = item.Opacity };
                ctx.UpdateSubresource(ref cbObj, _cbPerObject!);
                ctx.PSSetShaderResource(0, item.Srv);
                ctx.Draw(4, 0);
            }

            // ── 外部アドオン ──
            var addons = Easy3DPreviewAPI.Addons;
            foreach (var addon in addons)
            {
                using var stateSaver = new D3D11StateSaver(ctx);
                try
                {
                    addon.Render(ctx, _d3d!, viewProj, camera.CameraPosition);
                }
                catch (Exception ex)
                {
                    Preview3DPlugin.Log($"外部アドオン描画エラー ({addon.GetType().Name}): {ex.Message}");
                }
            }

            ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"Render エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// LightingHelper からライトデータを取得し CbLighting バッファを更新する。
    /// プレビューではシャドウは無効 (EnableShadow = 0)。
    /// </summary>
    private void UpdateLightingBuffer(ID3D11DeviceContext ctx)
    {
        if (_cbLighting == null) return;
        try
        {
            var cb = _lightingHelper.BuildCbLighting();
            ctx.UpdateSubresource(ref cb, _cbLighting);
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"UpdateLightingBuffer エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// OBJモデルを描画する。
    /// - b0 : CbObjModel (per-model)
    /// - b1 : CbLighting (lighting) ← UpdateLightingBuffer() で更新済み
    /// - t2 : whiteShadowSrv (シャドウアトラス代替)
    /// - s1 : shadowSampler
    /// </summary>
    private void DrawObjModels(
        ID3D11DeviceContext ctx,
        List<UiObjModel> objModels,
        Matrix4x4 viewProj,
        int screenWidth, int screenHeight)
    {
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(_objInputLayout);
        ctx.VSSetShader(_objVs);
        ctx.PSSetShader(_objPs);
        ctx.PSSetSampler(0, _objSampler);
        ctx.PSSetSampler(1, _shadowSampler);
        ctx.PSSetShaderResource(2, _whiteShadowSrv);

        // b1 にライティングバッファをバインド
        ctx.PSSetConstantBuffer(1, _cbLighting);
        ctx.VSSetConstantBuffer(1, _cbLighting);

        foreach (var obj in objModels)
        {
            var worldMatrix = BuildObjWorldMatrix(obj);

            ctx.IASetVertexBuffer(0, obj.VertexBuffer, 48, 0); // stride=48
            ctx.IASetIndexBuffer(obj.IndexBuffer, Format.R32_UInt, 0);

            foreach (var part in obj.Parts)
            {
                if (part.IndexCount <= 0) continue;

                var cb = new CbObjModel
                {
                    WorldMatrix = worldMatrix,
                    ViewProjMatrix = viewProj,
                    HalfWidth = 0f,   // 0 = ViewProj モード
                    HalfHeight = 0f,
                    Opacity = obj.Opacity,
                    MinAlphaVal = 0.004f,
                    BaseColor = part.BaseColor == Vector4.Zero ? Vector4.One : part.BaseColor,
                    ShadowLightPos = Vector3.Zero,
                    ShadowLightRange = 0f,
                };

                ctx.UpdateSubresource(ref cb, _objCbBuffer!);
                ctx.VSSetConstantBuffer(0, _objCbBuffer);
                ctx.PSSetConstantBuffer(0, _objCbBuffer);

                // テクスチャは省略しホワイトを使用 (マテリアルテクスチャは別デバイス上のため)
                ctx.PSSetShaderResource(0, _objWhiteSrv);

                ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
            }
        }
    }

    private void RestoreStandardPipeline(ID3D11DeviceContext ctx)
    {
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

    // ══════════════════════════════════════════════════════════════════
    // ワールド行列ビルダー
    // ══════════════════════════════════════════════════════════════════

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

        return S * Toffset * Zoom * Rz * Ry * Rx * Tdraw;
    }

    /// <summary>
    /// OBJモデルのワールド行列を構築する。
    /// DepthSortRenderer.BuildObjModelMatrix() と完全に対応する。
    /// </summary>
    private static Matrix4x4 BuildObjWorldMatrix(UiObjModel obj)
    {
        var desc = obj.DrawDescription;
        float d2r = MathF.PI / 180f;

        // モデル正規化 (中心を原点に移し、スケール適用)
        var normalize = Matrix4x4.CreateTranslation(-obj.ModelCenter)
                      * Matrix4x4.CreateScale(obj.ModelScale);

        // YMM4のY軸反転 (D3Dは右手Y上、YMM4は下Y)
        var yFlip = Matrix4x4.CreateScale(1f, -1f, 1f);

        // ベーススケール (OBJモデルの200倍が標準)
        const float BaseScale = 200f;
        var baseScaleM = Matrix4x4.CreateScale(BaseScale);

        float zx = (float)desc.Zoom.X;
        float zy = (float)desc.Zoom.Y;
        if (desc.Invert) zx = -zx;
        float zScale = (MathF.Abs(zx) + MathF.Abs(zy)) / 2f;
        var Zoom = Matrix4x4.CreateScale(zx, zy, zScale);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);

        return normalize * yFlip * baseScaleM * Zoom * Rz * Ry * Rx * Tdraw;
    }

    // ══════════════════════════════════════════════════════════════════
    // Dispose
    // ══════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _effectHelper.Dispose();
        _d3d = null;

        _vs?.Dispose();  _ps?.Dispose();
        _gridVs?.Dispose(); _gridPs?.Dispose();
        _inputLayout?.Dispose();
        _vertexBuffer?.Dispose(); _gridVertexBuffer?.Dispose();
        _cbScene?.Dispose(); _cbPerObject?.Dispose();
        _sampler?.Dispose(); _blendState?.Dispose();
        _depthState?.Dispose(); _rasterizerState?.Dispose();

        // ライティング
        _cbLighting?.Dispose();
        _whiteShadowSrv?.Dispose(); _whiteShadowTex?.Dispose();
        _shadowSampler?.Dispose();

        // OBJ
        _objVs?.Dispose(); _objPs?.Dispose();
        _objInputLayout?.Dispose(); _objCbBuffer?.Dispose();
        _objSampler?.Dispose();
        _objWhiteSrv?.Dispose(); _objWhiteTex?.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════
    // LightingHelper — LightManager から光源データを取得して CbLighting を構築
    // ══════════════════════════════════════════════════════════════════

    private sealed class LightingHelper
    {
        private bool _searched;
        private MethodInfo? _getActiveLightsMethod;
        private PropertyInfo? _ldTypeProp, _ldPosProp, _ldDirProp, _ldColorProp;
        private PropertyInfo? _ldIntensityProp, _ldRangeProp;
        private PropertyInfo? _ldSpotInnerProp, _ldSpotOuterProp;
        private PropertyInfo? _ldAreaWProp, _ldAreaHProp;

        private void EnsureSearched()
        {
            if (_searched) return;
            _searched = true;

            try
            {
                var lmType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "Iyahon_D3D11Renderer_Core.Lighting.LightManager");

                if (lmType == null)
                {
                    Preview3DPlugin.Log("LightManager 未検出 — シンプルライトを使用。");
                    return;
                }

                _getActiveLightsMethod = lmType.GetMethod("GetActiveLights",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                // LightData プロパティをキャッシュ
                var ldType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "Iyahon_D3D11Renderer_Core.Lighting.LightData");

                if (ldType != null)
                {
                    _ldTypeProp = ldType.GetProperty("Type");
                    _ldPosProp = ldType.GetProperty("Position");
                    _ldDirProp = ldType.GetProperty("Direction");
                    _ldColorProp = ldType.GetProperty("Color");
                    _ldIntensityProp = ldType.GetProperty("Intensity");
                    _ldRangeProp = ldType.GetProperty("Range");
                    _ldSpotInnerProp = ldType.GetProperty("SpotInnerAngle");
                    _ldSpotOuterProp = ldType.GetProperty("SpotOuterAngle");
                    _ldAreaWProp = ldType.GetProperty("AreaWidth");
                    _ldAreaHProp = ldType.GetProperty("AreaHeight");
                }

                Preview3DPlugin.Log($"LightingHelper 初期化完了: GetActiveLights={_getActiveLightsMethod != null}");
            }
            catch (Exception ex)
            {
                Preview3DPlugin.Log($"LightingHelper 初期化エラー: {ex.Message}");
            }
        }

        public PreviewCbLighting BuildCbLighting()
        {
            EnsureSearched();

            IList? lights = null;
            try
            {
                lights = _getActiveLightsMethod?.Invoke(null, null) as IList;
            }
            catch { }

            int lightCount = Math.Min(lights?.Count ?? 0, 8);

            var cb = new PreviewCbLighting
            {
                LightCount = lightCount,
                UseSimpleLight = lightCount > 0 ? 0f : 1f,
                EnableShadow = 0f,              // プレビューではシャドウ無効
                AmbientIntensity = 0.3f,
                AmbientColor = new Vector4(0.3f, 0.3f, 0.35f, 1.0f),
                ShadowCount = 0,
                EnableSoftShadow = 0f,
            };

            if (lights != null)
            {
                for (int i = 0; i < lightCount; i++)
                {
                    var ld = lights[i];
                    if (ld == null) continue;
                    cb.SetLight(i, BuildGpuLightData(ld));
                }
            }

            return cb;
        }

        private PreviewGpuLightData BuildGpuLightData(object ld)
        {
            try
            {
                float d2r = MathF.PI / 180f;

                int typeInt = _ldTypeProp?.GetValue(ld) is Enum e ? (int)(object)e : 0;
                var pos = _ldPosProp?.GetValue(ld) is Vector3 p ? p : Vector3.Zero;
                var dir = _ldDirProp?.GetValue(ld) is Vector3 d ? Vector3.Normalize(d) : new Vector3(0, -1, 0);
                var color = _ldColorProp?.GetValue(ld) is Vector3 c ? c : Vector3.One;
                float intensity = _ldIntensityProp?.GetValue(ld) is float fi ? fi : 1f;
                float range = _ldRangeProp?.GetValue(ld) is float r ? r : 5000f;
                float spotInner = _ldSpotInnerProp?.GetValue(ld) is float si ? si : 15f;
                float spotOuter = _ldSpotOuterProp?.GetValue(ld) is float so ? so : 30f;
                float areaW = _ldAreaWProp?.GetValue(ld) is float aw ? aw : 200f;
                float areaH = _ldAreaHProp?.GetValue(ld) is float ah ? ah : 200f;

                return new PreviewGpuLightData
                {
                    PositionAndType = new Vector4(pos, typeInt),
                    DirectionAndIntensity = new Vector4(dir, intensity),
                    ColorAndRange = new Vector4(color, range),
                    SpotParams = new Vector4(
                        MathF.Cos(spotInner * d2r),
                        MathF.Cos(spotOuter * d2r),
                        areaW, areaH),
                };
            }
            catch
            {
                return new PreviewGpuLightData
                {
                    PositionAndType = new Vector4(Vector3.Zero, 0),
                    DirectionAndIntensity = new Vector4(new Vector3(0, -1, 0), 1f),
                    ColorAndRange = new Vector4(Vector3.One, 5000f),
                    SpotParams = Vector4.Zero,
                };
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // D3Dエフェクト リフレクションヘルパー
    // ══════════════════════════════════════════════════════════════════

    private sealed class D3DEffectHelper : IDisposable
    {
        private bool _searched;
        private bool _available;

        private MethodInfo? _createEffectMethod;
        private MethodInfo? _effectInitializeMethod;
        private MethodInfo? _effectRenderMethod;
        private MethodInfo? _effectDisposeMethod;
        private Type? _renderContextType;
        private MethodInfo? _configureEffectMethod;

        private readonly Dictionary<string, object> _effectCache = new();

        public bool IsAvailable { get { EnsureSearched(); return _available; } }

        private void EnsureSearched()
        {
            if (_searched) return;
            _searched = true;

            try
            {
                var registryType = Find("Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry");
                var effectType = Find("Iyahon_D3D11Renderer_Core.D3DEffect.ID3DEffect");
                _renderContextType = Find("Iyahon_D3D11Renderer_Core.D3DEffect.D3DRenderContext");
                var videoEffectType = Find("Iyahon_D3D11Renderer_Core.D3DEffect.ID3DVideoEffect");

                if (registryType == null || effectType == null || _renderContextType == null)
                {
                    Preview3DPlugin.Log("D3DEffectHelper: D3D11Renderer_Core 未検出。");
                    return;
                }

                _createEffectMethod = registryType.GetMethod("CreateEffect",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
                _effectInitializeMethod = effectType.GetMethod("Initialize");
                _effectRenderMethod = effectType.GetMethod("Render");
                _effectDisposeMethod = typeof(IDisposable).GetMethod("Dispose");

                if (videoEffectType != null)
                    _configureEffectMethod = videoEffectType.GetMethod("ConfigureEffect");

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

        /// <summary>
        /// D3Dエフェクトを描画する。ライティングとシャドウアトラス代替も合わせてバインドする。
        /// </summary>
        public bool RenderEffect(
            ID3D11DeviceContext ctx, ID3D11Device device,
            ID3D11ShaderResourceView srv, UiItem item,
            Matrix4x4 worldMatrix, Matrix4x4 viewProj, Vector3 cameraPos,
            ID3D11Buffer? cbLighting,
            ID3D11ShaderResourceView? whiteShadowSrv,
            ID3D11SamplerState? shadowSampler)
        {
            if (!_available || item.D3DEffectId == null) return false;

            if (!_effectCache.TryGetValue(item.D3DEffectId, out var effect))
            {
                effect = _createEffectMethod!.Invoke(null, new object[] { item.D3DEffectId });
                if (effect == null) return false;
                _effectCache[item.D3DEffectId] = effect;
            }

            _effectInitializeMethod!.Invoke(effect, new object[] { device, ctx });

            if (_configureEffectMethod != null && item.D3DVideoEffect != null)
            {
                _configureEffectMethod.Invoke(item.D3DVideoEffect,
                    new object[] { effect, item.ItemFrame, item.ItemLength, item.Fps });
            }

            var renderContext = Activator.CreateInstance(_renderContextType!);
            if (renderContext == null) return false;

            var rcType = _renderContextType!;
            rcType.GetProperty("WorldMatrix")!.SetValue(renderContext, worldMatrix);
            rcType.GetProperty("ViewProjectionMatrix")!.SetValue(renderContext, viewProj);
            rcType.GetProperty("CameraWorldPosition")!.SetValue(renderContext, cameraPos);
            rcType.GetProperty("TextureWidth")!.SetValue(renderContext, (int)item.PixelWidth);
            rcType.GetProperty("TextureHeight")!.SetValue(renderContext, (int)item.PixelHeight);
            rcType.GetProperty("HalfScreenWidth")!.SetValue(renderContext, 0f);
            rcType.GetProperty("HalfScreenHeight")!.SetValue(renderContext, 0f);
            rcType.GetProperty("Opacity")!.SetValue(renderContext, item.Opacity);
            rcType.GetProperty("AlphaThreshold")!.SetValue(renderContext, 0.004f);
            rcType.GetProperty("CameraMatrix")!.SetValue(renderContext, item.DrawDescription.Camera);

            // ★ライティングバッファとシャドウアトラス代替をエフェクト呼び出し前にバインド
            // エフェクト側シェーダは b1=CbLgt, t2=shadowAtlas, s1=shadowSampler を参照する。
            if (cbLighting != null) ctx.PSSetConstantBuffer(1, cbLighting);
            if (whiteShadowSrv != null) ctx.PSSetShaderResource(2, whiteShadowSrv);
            if (shadowSampler != null) ctx.PSSetSampler(1, shadowSampler);

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

        private static Type? Find(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == fullName);
    }
}
