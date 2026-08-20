using System;
using System.IO;

namespace MineIDE.Services;

/// <summary>
/// The app's own data folder in %APPDATA%\Roaming\MineIDE, created on first launch.
/// Stores mods, textures, models, and project scaffolding.
/// </summary>
public static class AppDataPaths
{
    /// <summary>%APPDATA%\MineIDE — root data folder of the application.</summary>
    public static string Root { get; }

    /// <summary>%APPDATA%\MineIDE\mods — mod .jar files.</summary>
    public static string Mods { get; }

    /// <summary>%APPDATA%\MineIDE\textures — textures.</summary>
    public static string Textures { get; }

    /// <summary>%APPDATA%\MineIDE\models — models.</summary>
    public static string Models { get; }

    /// <summary>%APPDATA%\MineIDE\projects — project scaffolding (.mmcp + folders).</summary>
    public static string Projects { get; }

    static AppDataPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MineIDE");
        Mods = Path.Combine(Root, "mods");
        Textures = Path.Combine(Root, "textures");
        Models = Path.Combine(Root, "models");
        Projects = Path.Combine(Root, "projects");

        foreach (var dir in new[] { Root, Mods, Textures, Models, Projects })
        {
            try { Directory.CreateDirectory(dir); } catch { /* read-only env */ }
        }
    }
}
