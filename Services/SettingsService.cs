using System;
using System.IO;
using System.Text.Json;

namespace MineIDE.Services;

public class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public bool HasWindowBounds { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1440;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }
    public string? LastProjectName { get; set; }
}

public static class SettingsService
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MineIDE", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* corrupt/missing settings — fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
