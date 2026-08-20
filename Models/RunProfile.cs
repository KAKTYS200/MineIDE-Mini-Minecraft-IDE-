namespace MineIDE.Models;

/// <summary>Launch profile mapped to a real ForgeGradle run task.</summary>
public class RunProfile
{
    public string Name { get; set; } = "client";
    public string Description { get; set; } = "";
    public string GradleTask { get; set; } = "runClient";
    public string Icon { get; set; } = "\uE768";
}
