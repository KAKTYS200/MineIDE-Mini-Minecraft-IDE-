using System;
using System.IO;
using System.Text.Json;
using MineIDE.Models;

namespace MineIDE.Services;

/// <summary>
/// The .mmcp project file format — a small JSON descriptor that lets the app
/// (or Windows, via double-click) open a project with one click.
/// </summary>
public static class MmcpService
{
    public const string Extension = ".mmcp";

    /// <summary>Saves the current project as a .mmcp descriptor.</summary>
    public static void Write(Project project, string filePath)
    {
        var doc = new MmcpProject
        {
            Format = "MineIDE Project",
            Version = 1,
            Name = project.Name,
            Type = project.Type.ToString(),
            Path = project.Path,
            MinecraftVersion = project.MinecraftVersion,
            ForgeVersion = project.ForgeVersion,
            ModId = project.ModId,
            Description = project.Description,
            Created = DateTime.Now.ToString("o")
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }

    /// <summary>Reads a .mmcp descriptor and returns the project it points to.</summary>
    public static Project? Read(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        var doc = JsonSerializer.Deserialize<MmcpProject>(File.ReadAllText(filePath));
        if (doc == null || doc.Format != "MineIDE Project") return null;

        return new Project
        {
            Name = string.IsNullOrEmpty(doc.Name) ? Path.GetFileNameWithoutExtension(filePath) : doc.Name,
            Path = doc.Path ?? "",
            MinecraftVersion = doc.MinecraftVersion ?? "1.20.1",
            ForgeVersion = doc.ForgeVersion ?? "47.2.0",
            ModId = doc.ModId ?? "examplemod",
            Description = doc.Description ?? "Проект из .mmcp файла",
            Type = Enum.TryParse<ProjectType>(doc.Type, out var t) ? t : ProjectType.Mod
        };
    }
}

/// <summary>Serialized shape of a .mmcp file.</summary>
public class MmcpProject
{
    public string Format { get; set; } = "MineIDE Project";
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Path { get; set; }
    public string? MinecraftVersion { get; set; }
    public string? ForgeVersion { get; set; }
    public string? ModId { get; set; }
    public string? Description { get; set; }
    public string? Created { get; set; }
}
