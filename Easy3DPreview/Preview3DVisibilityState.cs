using System.Runtime.CompilerServices;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

internal static class Preview3DVisibilityState
{
    private static readonly ConditionalWeakTable<IVideoItem, StrongBox<bool>> _visibilityMap = new();

    public static bool IsHiddenIn3DPreview(IVideoItem item)
    {
        if (_visibilityMap.TryGetValue(item, out var box))
            return box.Value;
        return false;
    }

    public static void SetHiddenIn3DPreview(IVideoItem item, bool isHidden)
    {
        if (_visibilityMap.TryGetValue(item, out var box))
        {
            box.Value = isHidden;
        }
        else
        {
            _visibilityMap.Add(item, new StrongBox<bool>(isHidden));
        }
    }
}
