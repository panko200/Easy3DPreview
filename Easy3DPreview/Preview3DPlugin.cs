using System;
using YukkuriMovieMaker.Plugin;

namespace Easy3DPreview;

/// <summary>
/// 3Dプレビュー ツールプラグインのエントリーポイント。
/// YMM4 のツールメニューに「3Dプレビュー」を追加する。
/// </summary>
public class Preview3DPlugin : IToolPlugin
{
    public string Name => "簡単3Dプレビュー";
    public Type ViewModelType => typeof(Preview3DViewModel);
    public Type ViewType => typeof(Preview3DControl);

    /// <summary>
    /// Harmony パッチの適用（プラグイン読み込み時に一度だけ実行）
    /// </summary>
    static Preview3DPlugin()
    {
        try
        {
            var harmony = new HarmonyLib.Harmony("Easy3DPreview.Preview3D");
            Preview3DPatch.Apply(harmony);
            Log("Harmony パッチ適用完了");
        }
        catch (Exception ex)
        {
            Log($"Harmony パッチ適用エラー: {ex.Message}");
        }
    }

    internal static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Easy3DPreview.Preview3D] {msg}");
}
