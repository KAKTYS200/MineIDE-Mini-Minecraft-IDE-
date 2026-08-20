using System.IO;

namespace MineIDE.Services;

/// <summary>
/// Locates the Gradle executable for a project: the wrapper first, then system Gradle.
/// </summary>
public static class GradleLocator
{
    /// <summary>Returns the gradle command to run, or null if nothing usable was found.</summary>
    public static string? Find(string projectPath)
    {
        if (!string.IsNullOrEmpty(projectPath))
        {
            var wrapperBat = Path.Combine(projectPath, "gradlew.bat");
            if (File.Exists(wrapperBat)) return wrapperBat;

            var wrapperSh = Path.Combine(projectPath, "gradlew");
            if (File.Exists(wrapperSh)) return wrapperSh;
        }

        // System Gradle from PATH — Start() will throw if it's not installed.
        return "gradle";
    }
}
