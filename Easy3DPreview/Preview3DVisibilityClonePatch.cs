using HarmonyLib;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

internal static class Preview3DVisibilityClonePatch
{
    public static void Apply(Harmony harmony)
    {
        var targetMethod = AccessTools.Method(typeof(BaseItem), nameof(BaseItem.GetClone));
        if (targetMethod == null) return;

        harmony.Patch(targetMethod, postfix: new HarmonyMethod(
            typeof(Preview3DVisibilityClonePatch), nameof(Postfix)));
    }

    private static void Postfix(BaseItem __instance, ref IItem __result)
    {
        if (__instance is not VisualItem src) return;
        if (__result is not VisualItem dst) return;

        Preview3DVisibilityState.SetHiddenIn3DPreview(
            (IVideoItem)dst,
            Preview3DVisibilityState.IsHiddenIn3DPreview((IVideoItem)src));
    }
}
