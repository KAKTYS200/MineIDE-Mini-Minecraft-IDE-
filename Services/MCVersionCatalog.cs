using System.Collections.Generic;

namespace MineIDE.Services;

public class McForgeMapping
{
    public string MinecraftVersion { get; set; } = "";
    public List<string> ForgeVersions { get; set; } = new();
}

public static class MCVersionCatalog
{
    public static List<McForgeMapping> Mappings { get; } = new()
    {
        new McForgeMapping
        {
            MinecraftVersion = "1.20.4",
            ForgeVersions = new() { "49.0.30", "49.0.29", "49.0.27" }
        },
        new McForgeMapping
        {
            MinecraftVersion = "1.20.1",
            ForgeVersions = new() { "47.2.0", "47.1.0", "47.0.35", "47.0.18" }
        },
        new McForgeMapping
        {
            MinecraftVersion = "1.19.4",
            ForgeVersions = new() { "45.0.66", "45.0.63" }
        },
        new McForgeMapping
        {
            MinecraftVersion = "1.19.2",
            ForgeVersions = new() { "43.4.0", "43.3.0", "43.2.0" }
        },
        new McForgeMapping
        {
            MinecraftVersion = "1.18.2",
            ForgeVersions = new() { "40.2.0", "40.1.80", "40.1.0" }
        },
        new McForgeMapping
        {
            MinecraftVersion = "1.16.5",
            ForgeVersions = new() { "36.2.34", "36.2.0" }
        }
    };
}
