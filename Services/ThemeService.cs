using System;
using System.Windows;

namespace MineIDE.Services;

public enum AppTheme
{
    Dark,
    Light
}

public class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public event EventHandler<AppTheme>? ThemeChanged;

    public void Apply(AppTheme theme)
    {
        Current = theme;
        var app = Application.Current;
        if (app == null) return;

        var merged = app.Resources.MergedDictionaries;

        // Remove previous theme overrides
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.ToString();
            if (src != null && (src.Contains("LightColors.xaml") || src.Contains("Colors.xaml")))
            {
                merged.RemoveAt(i);
            }
        }

        var dict = new ResourceDictionary
        {
            Source = new Uri(theme == AppTheme.Light
                ? "/Themes/LightColors.xaml"
                : "/Themes/Colors.xaml",
                UriKind.Relative)
        };
        merged.Insert(0, dict);

        ThemeChanged?.Invoke(this, theme);
    }

    public void Toggle()
        => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
