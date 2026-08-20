using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineIDE.Services;

/// <summary>Detected local Minecraft installation with Forge (official launcher, Prism, MultiMC, CurseForge).</summary>
public class MinecraftInstall
{
    public string RootDir { get; init; } = "";      // e.g. %APPDATA%\.minecraft
    public string VersionDir { get; init; } = "";
    public string JsonPath { get; init; } = "";
    public string VersionId { get; init; } = "";    // e.g. "Forge 1.20.1" or "1.20.1-forge-47.2.0"
    public string MinecraftVersion { get; init; } = "";
    public string ForgeVersion { get; init; } = "";
    public string NativesDir { get; init; } = "";   // pre-extracted natives, if present
}

/// <summary>
/// Finds an installed Minecraft + Forge client on this machine so the IDE can launch
/// mods in it for testing (instead of embedding the game into the exe).
/// </summary>
public static class MinecraftInstallLocator
{
    public static MinecraftInstall? FindForge(string mcVersion)
    {
        var roots = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Official launcher / TLauncher
        roots.Add(Path.Combine(appData, ".minecraft"));

        // PrismLauncher / MultiMC instances (each has its own .minecraft)
        foreach (var launcher in new[] { "PrismLauncher", "MultiMC" })
        {
            var instances = Path.Combine(appData, launcher, "instances");
            if (Directory.Exists(instances))
                roots.AddRange(Directory.GetDirectories(instances).Select(d => Path.Combine(d, ".minecraft")));
        }

        // CurseForge app
        var cf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurseForge", "Instances");
        if (Directory.Exists(cf))
            roots.AddRange(Directory.GetDirectories(cf).Select(d => Path.Combine(d, ".minecraft")));

        foreach (var root in roots)
        {
            var versionsDir = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsDir)) continue;

            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                if (!id.Contains("forge", StringComparison.OrdinalIgnoreCase)) continue;

                var jsonPath = Path.Combine(dir, id + ".json");
                if (!File.Exists(jsonPath)) continue;

                var (mc, forge) = ParseVersions(jsonPath, id);
                if (mc != mcVersion) continue;

                var natives = Path.Combine(dir, "natives");
                return new MinecraftInstall
                {
                    RootDir = root,
                    VersionDir = dir,
                    JsonPath = jsonPath,
                    VersionId = id,
                    MinecraftVersion = mc,
                    ForgeVersion = forge,
                    NativesDir = Directory.Exists(natives) ? natives : ""
                };
            }
        }
        return null;
    }

    /// <summary>Reads MC/Forge versions from the version JSON (--fml.mcVersion / --fml.forgeVersion), falling back to the folder name.</summary>
    private static (string mc, string forge) ParseVersions(string jsonPath, string id)
    {
        var mc = "";
        var forge = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("arguments", out var args) && args.TryGetProperty("game", out var game))
            {
                var vals = new List<string>();
                foreach (var item in game.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        vals.Add(item.GetString() ?? "");
                    else if (item.TryGetProperty("values", out var v))
                        foreach (var x in v.EnumerateArray())
                            vals.Add(x.GetString() ?? "");
                }
                for (int i = 0; i < vals.Count - 1; i++)
                {
                    if (vals[i] == "--fml.mcVersion") mc = vals[i + 1];
                    if (vals[i] == "--fml.forgeVersion") forge = vals[i + 1];
                }
            }
        }
        catch { /* fall back to the folder name */ }

        if (string.IsNullOrEmpty(mc))
        {
            var m = Regex.Match(id, @"(\d+\.\d+(?:\.\d+)?)");
            if (m.Success) mc = m.Groups[1].Value;
        }
        if (string.IsNullOrEmpty(forge))
        {
            var m = Regex.Match(id, @"-forge-(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (m.Success) forge = m.Groups[1].Value;
        }
        return (mc, forge);
    }
}
