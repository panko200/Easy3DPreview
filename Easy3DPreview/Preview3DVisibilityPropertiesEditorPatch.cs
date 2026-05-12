using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

internal static class Preview3DVisibilityPropertiesEditorPatch
{
    private static readonly Type _editorType = typeof(PropertiesEditor);

    private static readonly FieldInfo? _currentTargetsField =
        _editorType.GetField("currentTargets",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? _getEditablePropertiesMethod =
        _editorType.GetMethod("GetEditableProperties",
            BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly MethodInfo? _attachMethod =
        typeof(PropertiesEditor.EditorCache).GetMethod("Attach",
            BindingFlags.Instance | BindingFlags.Public);

    private const string InjectedTag = "Preview3D_Visibility_Injected";

    public static void Apply(Harmony harmony)
    {
        var target = AccessTools.Method(_editorType, "RefreshControls");
        if (target == null)
        {
            Preview3DPlugin.Log("RefreshControls method not found.");
            return;
        }

        if (_currentTargetsField == null || _getEditablePropertiesMethod == null || _attachMethod == null)
        {
            Preview3DPlugin.Log($"Reflection failed: targets={_currentTargetsField != null}, props={_getEditablePropertiesMethod != null}, attach={_attachMethod != null}");
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(
            typeof(Preview3DVisibilityPropertiesEditorPatch), nameof(Postfix)));
        Preview3DPlugin.Log("Visibility Properties Editor Patch Applied.");
    }

    private static void Postfix(PropertiesEditor __instance)
    {
        try
        {
            var targets = _currentTargetsField!.GetValue(__instance) as object[];
            if (targets == null || targets.Length == 0) return;

            // VisualItem などの IVideoItem を対象とする
            var videoItems = targets.OfType<VisualItem>().Cast<IVideoItem>().ToArray();
            if (videoItems.Length == 0) return;

            // 映像エフェクトを持つことができるアイテム（IVideoItem）を対象とする
            // （要素が空でもVideoEffectsプロパティが存在すれば対象）
            var hasVideoEffects = videoItems.Any(item => item.VideoEffects != null);
            if (!hasVideoEffects) return;

            foreach (var g in __instance.Groups)
            {
                foreach (var pair in g.Items)
                {
                    if (pair.EditorCache.Control.Tag as string == InjectedTag)
                        pair.EditorCache.Control.Tag = null;
                }
            }

            var proxy = new Preview3DVisibilityProxy(videoItems);

            var editableProperties = (IEnumerable<PropertiesEditor.EditableProperty>)
                _getEditablePropertiesMethod!.Invoke(null,
                    new object?[] { proxy, proxy, null, 0, null, null })!;

            var propList = editableProperties.ToList();
            if (propList.Count == 0) return;

            var targetGroup = __instance.Groups.FirstOrDefault(g => g.Name == "3Dプレビュー");
            if (targetGroup == null)
            {
                targetGroup = new PropertiesEditor.EditorGroup { Name = "3Dプレビュー" };
                __instance.Groups.Add(targetGroup);
            }

            foreach (var editableProp in propList)
            {
                var attr = editableProp.PropertyEditorAttribute;
                if (attr == null) continue;

                var cache = new PropertiesEditor.EditorCache(attr);
                cache.Control.Tag = InjectedTag;

                _attachMethod!.Invoke(cache,
                    new object[] { (IEnumerable<PropertiesEditor.EditableProperty>)
                        new[] { editableProp } });

                targetGroup.Items.Add(
                    new PropertiesEditor.EditablePropertyAndEditorCachePair(editableProp, cache));
            }
        }
        catch (Exception ex)
        {
            Preview3DPlugin.Log($"PropertiesEditorPatch Error: {ex}");
        }
    }
}
