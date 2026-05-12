using Newtonsoft.Json.Serialization;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

internal sealed class Preview3DVisibilityValueProvider : IValueProvider
{
    public void SetValue(object target, object? value)
    {
        if (target is IVideoItem item && value is bool b)
            Preview3DVisibilityState.SetHiddenIn3DPreview(item, b);
    }

    public object? GetValue(object target)
        => target is IVideoItem item ? Preview3DVisibilityState.IsHiddenIn3DPreview(item) : false;
}
