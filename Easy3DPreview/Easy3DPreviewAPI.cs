#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D11;

namespace Easy3DPreview;

/// <summary>
/// 外部プラグインが「簡単3Dプレビュー」に介入するための公開インターフェース。
/// </summary>
public interface IEasy3DAddon
{
    /// <summary>
    /// 毎フレームの描画処理。
    /// 簡単3Dプレビューの描画ループ（D3D11Host）のコンテキストで実行されます。
    /// </summary>
    /// <param name="ctx">D3D11デバイスコンテキスト</param>
    /// <param name="device">D3D11デバイス</param>
    /// <param name="viewProj">現在のカメラのビュー・プロジェクション行列</param>
    /// <param name="cameraPos">現在のカメラのワールド座標</param>
    void Render(ID3D11DeviceContext ctx, ID3D11Device device, Matrix4x4 viewProj, Vector3 cameraPos);
}

/// <summary>
/// 簡単3Dプレビューの外部APIエントリーポイント。
/// </summary>
public static class Easy3DPreviewAPI
{
    private static readonly List<IEasy3DAddon> _addons = new();
    private static readonly object _lock = new();

    /// <summary>
    /// 現在登録されているアドオンの一覧を取得します。
    /// </summary>
    public static IReadOnlyList<IEasy3DAddon> Addons
    {
        get
        {
            lock (_lock)
            {
                return _addons.ToArray();
            }
        }
    }

    /// <summary>
    /// 3Dプレビューが管理する独立D3D11デバイスを取得します。
    /// </summary>
    public static ID3D11Device? IndependentDevice => Preview3DPatch.IndependentDevice;

    /// <summary>
    /// 3Dプレビューにアドオンを登録します。
    /// </summary>
    public static void RegisterAddon(IEasy3DAddon addon)
    {
        lock (_lock)
        {
            if (!_addons.Contains(addon))
            {
                _addons.Add(addon);
                Preview3DPlugin.Log($"外部アドオンが登録されました: {addon.GetType().FullName}");
            }
        }
    }

    /// <summary>
    /// 3Dプレビューからアドオンを解除します。
    /// </summary>
    public static void UnregisterAddon(IEasy3DAddon addon)
    {
        lock (_lock)
        {
            _addons.Remove(addon);
            Preview3DPlugin.Log($"外部アドオンが解除されました: {addon.GetType().FullName}");
        }
    }
}