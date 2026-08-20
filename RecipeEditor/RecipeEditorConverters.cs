using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MineIDE.RecipeEditor.Services;

namespace MineIDE.RecipeEditor;

/// <summary>
/// Returns true when two bound values are the same object reference.
/// Used to highlight the currently selected recipe slot (border accent).
/// </summary>
public sealed class ReferenceEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values is { Length: 2 } && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shared cache that loads and freezes a PNG path into a reusable <see cref="ImageSource"/>.</summary>
internal static class TextureImageCache
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

/// <summary>
/// Resolves a Minecraft item id ("minecraft:iron_ingot") to a cached, frozen
/// <see cref="ImageSource"/> using <see cref="ItemIconService"/>. Returns null when
/// the texture is unknown so the view can fall back to showing the id as text.
/// </summary>
public sealed class ItemIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string id && !string.IsNullOrWhiteSpace(id)
            ? TextureImageCache.Get(ItemIconService.Instance.GetIconPath(id))
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Resolves a vanilla GUI container texture name ("crafting_table", "furnace", ...)
/// to a cached <see cref="ImageSource"/> used as the recipe editor background.
/// </summary>
public sealed class GuiIconConverter : IValueConverter
{
    /// <summary>Vanilla container GUI — the 176×84 crafting area only, without the player inventory below.</summary>
    private static readonly Int32Rect GuiRegion = new(0, 0, 176, 84);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string name && !string.IsNullOrWhiteSpace(name)
            ? TextureImageCache.Get(ItemIconService.Instance.GetGuiIconPath(name), GuiRegion)
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
