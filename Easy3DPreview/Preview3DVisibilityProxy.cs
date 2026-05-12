using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

public sealed class Preview3DVisibilityProxy : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IVideoItem[] _items;

    public Preview3DVisibilityProxy(IVideoItem[] items)
    {
        _items = items;
    }

    [Display(
        GroupName = "3Dプレビュー",
        Name = "3Dプレ表示無視",
        Description = "ONにすると、このアイテムは3Dプレビューに表示されなくなります。",
        Order = 503)]
    [ToggleSlider]
    public bool HideIn3DPreview
    {
        get
        {
            foreach (var item in _items)
            {
                if (!Preview3DVisibilityState.IsHiddenIn3DPreview(item))
                    return false;
            }
            return _items.Length > 0;
        }
        set
        {
            bool changed = false;
            foreach (var item in _items)
            {
                if (Preview3DVisibilityState.IsHiddenIn3DPreview(item) != value)
                {
                    Preview3DVisibilityState.SetHiddenIn3DPreview(item, value);
                    changed = true;
                }
            }
            if (changed)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideIn3DPreview)));
            }
        }
    }
}
