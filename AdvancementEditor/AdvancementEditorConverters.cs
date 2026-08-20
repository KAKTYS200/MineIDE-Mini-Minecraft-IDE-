using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MineIDE.AdvancementEditor.Models;
using MineIDE.RecipeEditor.Services;

namespace MineIDE.AdvancementEditor;

/// <summary>Shared texture cache (same approach as the RecipeEditor module).</summary>
internal static class AdvTextureImageCache
{
    private static readonly Dictionary<string, ImageSource> Cache = new();

    public static ImageSource? Get(string? path, Int32Rect? crop = null)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var key = crop.HasValue
            ? path + "#" + crop.Value.X + "," + crop.Value.Y + "," + crop.Value.Width + "," + crop.Value.Height
            : path;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            if (crop.HasValue) bmp.SourceRect = crop.Value;
            bmp.EndInit();
            bmp.Freeze();
            Cache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Resolves an item id to a cached ImageSource (via the shared ItemIconService).</summary>
public sealed class AdvItemIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string id && !string.IsNullOrWhiteSpace(id)
            ? AdvTextureImageCache.Get(ItemIconService.Instance.GetIconPath(id))
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders the in-game advancement frame sprite from the vanilla 1.20.1
/// widgets.png sheet (NOT window.png — that one only holds the window border).
/// Vanilla layout: 26×26 sprites, X = frame type (task=0, challenge=26, goal=52),
/// Y = 128 for obtained (bright) / 154 for unobtained (dim). Selected nodes use
/// the bright "obtained" variant, like the highlighted node in the game.
/// The frame is rendered 2x (52×52) like the zoomed-in vanilla screen.
/// </summary>
public sealed class AdvFrameConverter : IMultiValueConverter
{
    // Indexed by AdvancementFrame: [0]=task, [1]=goal, [2]=challenge.
    // Vanilla X coordinates: task=0, challenge=26, goal=52.
    private static readonly Int32Rect[] Frames =
    {
        new(0, 128, 26, 26),    // task obtained (selected)
        new(0, 154, 26, 26),    // task unobtained
        new(52, 128, 26, 26),   // goal obtained (selected)
        new(52, 154, 26, 26),   // goal unobtained
        new(26, 128, 26, 26),   // challenge obtained (selected)
        new(26, 154, 26, 26)    // challenge unobtained
    };

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is { Length: 2 } &&
            values[0] is AdvancementFrame frame &&
            values[1] is bool selected)
        {
            var idx = ((int)frame) * 2 + (selected ? 0 : 1);
            var path = ItemIconService.Instance.GetAdvancementGuiPath("widgets");
            return path != null
                ? AdvTextureImageCache.Get(path, Frames[idx])
                : null;
        }
        return null;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
