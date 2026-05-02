using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

#nullable enable
namespace Easy3DPreview;

/// <summary>
/// YMM4 の D3D11 Immediate Context の状態を一時的に退避し、復元するためのヘルパークラス。
/// D2D 描画などによって D3D 状態が破壊されるのを防ぐ。
/// </summary>
internal sealed class D3D11StateSaver : IDisposable
{
    private readonly ID3D11DeviceContext _ctx;

    private ID3D11RenderTargetView?[]? _savedRtvs;
    private ID3D11DepthStencilView? _savedDsv;
    private Viewport[] _savedViewports = Array.Empty<Viewport>();
    private ID3D11RasterizerState? _savedRasterizerState;
    private ID3D11BlendState? _savedBlendState;
    private Color4 _savedBlendFactor;
    private int _savedSampleMask;
    private ID3D11DepthStencilState? _savedDepthStencilState;
    private int _savedStencilRef;
    private ID3D11VertexShader? _savedVs;
    private ID3D11PixelShader? _savedPs;
    private ID3D11InputLayout? _savedInputLayout;
    private PrimitiveTopology _savedTopology;
    private ID3D11Buffer?[]? _savedVsConstantBuffers;
    private ID3D11Buffer?[]? _savedPsConstantBuffers;
    private ID3D11SamplerState?[]? _savedPsSamplers;
    private ID3D11ShaderResourceView?[]? _savedPsSrvs;
    private ID3D11Buffer?[]? _savedVertexBuffers;
    private int[]? _savedVbStrides;
    private int[]? _savedVbOffsets;

    public D3D11StateSaver(ID3D11DeviceContext ctx)
    {
        _ctx = ctx;
        Save();
    }

    private void Save()
    {
        try
        {
            _savedRtvs = new ID3D11RenderTargetView?[1];
            _ctx.OMGetRenderTargets(1, _savedRtvs, out _savedDsv);
            _savedViewports = _ctx.RSGetViewports<Viewport>();
            _savedRasterizerState = _ctx.RSGetState();
            _ctx.OMGetBlendState(out _savedBlendState, out _savedBlendFactor, out _savedSampleMask);
            _ctx.OMGetDepthStencilState(out _savedDepthStencilState, out _savedStencilRef);
            _savedVs = _ctx.VSGetShader();
            _savedPs = _ctx.PSGetShader();
            _savedInputLayout = _ctx.IAGetInputLayout();
            _savedTopology = _ctx.IAGetPrimitiveTopology();
            
            _savedVertexBuffers = new ID3D11Buffer?[1];
            _savedVbStrides = new int[1];
            _savedVbOffsets = new int[1];
            _ctx.IAGetVertexBuffers(0, 1, _savedVertexBuffers, _savedVbStrides, _savedVbOffsets);

            _savedVsConstantBuffers = new ID3D11Buffer?[2];
            _ctx.VSGetConstantBuffers(0, 2, _savedVsConstantBuffers);

            _savedPsConstantBuffers = new ID3D11Buffer?[2];
            _ctx.PSGetConstantBuffers(0, 2, _savedPsConstantBuffers);

            _savedPsSamplers = new ID3D11SamplerState?[1];
            _ctx.PSGetSamplers(0, 1, _savedPsSamplers);

            _savedPsSrvs = new ID3D11ShaderResourceView?[1];
            _ctx.PSGetShaderResources(0, 1, _savedPsSrvs);
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"D3D11StateSaver.Save エラー: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_savedRtvs != null)
            {
                _ctx.OMSetRenderTargets(_savedRtvs, _savedDsv);
                foreach (var rtv in _savedRtvs) rtv?.Dispose();
                _savedDsv?.Dispose();
                _savedRtvs = null;
                _savedDsv = null;
            }

            if (_savedViewports.Length > 0)
                _ctx.RSSetViewports(_savedViewports);

            if (_savedRasterizerState != null)
            {
                _ctx.RSSetState(_savedRasterizerState);
                _savedRasterizerState.Dispose();
                _savedRasterizerState = null;
            }

            if (_savedBlendState != null)
            {
                _ctx.OMSetBlendState(_savedBlendState, _savedBlendFactor, _savedSampleMask);
                _savedBlendState.Dispose();
                _savedBlendState = null;
            }

            if (_savedDepthStencilState != null)
            {
                _ctx.OMSetDepthStencilState(_savedDepthStencilState, _savedStencilRef);
                _savedDepthStencilState.Dispose();
                _savedDepthStencilState = null;
            }

            _ctx.VSSetShader(_savedVs);
            _savedVs?.Dispose();
            _savedVs = null;

            _ctx.PSSetShader(_savedPs);
            _savedPs?.Dispose();
            _savedPs = null;

            if (_savedInputLayout != null)
            {
                _ctx.IASetInputLayout(_savedInputLayout);
                _savedInputLayout.Dispose();
                _savedInputLayout = null;
            }
            _ctx.IASetPrimitiveTopology(_savedTopology);

            if (_savedVertexBuffers != null)
            {
                _ctx.IASetVertexBuffer(0, _savedVertexBuffers[0], _savedVbStrides![0], _savedVbOffsets![0]);
                _savedVertexBuffers[0]?.Dispose();
                _savedVertexBuffers = null;
            }

            if (_savedVsConstantBuffers != null)
            {
                _ctx.VSSetConstantBuffers(0, _savedVsConstantBuffers);
                foreach (var cb in _savedVsConstantBuffers) cb?.Dispose();
                _savedVsConstantBuffers = null;
            }

            if (_savedPsConstantBuffers != null)
            {
                _ctx.PSSetConstantBuffers(0, _savedPsConstantBuffers);
                foreach (var cb in _savedPsConstantBuffers) cb?.Dispose();
                _savedPsConstantBuffers = null;
            }

            if (_savedPsSamplers != null)
            {
                _ctx.PSSetSamplers(0, _savedPsSamplers);
                foreach (var s in _savedPsSamplers) s?.Dispose();
                _savedPsSamplers = null;
            }

            if (_savedPsSrvs != null)
            {
                _ctx.PSSetShaderResources(0, _savedPsSrvs);
                foreach (var srv in _savedPsSrvs) srv?.Dispose();
                _savedPsSrvs = null;
            }
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"D3D11StateSaver.Restore エラー: {ex.Message}");
        }
    }
}
