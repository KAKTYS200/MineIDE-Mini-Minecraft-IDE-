using System;

namespace MineIDE.Models;

public enum BuildPhase
{
    Idle,
    Cleaning,
    Compiling,
    Packaging,
    Copying,
    Done,
    Failed,
    Cancelled
}

public class BuildStatus
{
    public BuildPhase Phase { get; set; } = BuildPhase.Idle;
    public double Progress { get; set; }
    public string Message { get; set; } = "Ready";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? OutputJarPath { get; set; }
    public string? LastError { get; set; }

    public bool IsRunning => Phase != BuildPhase.Idle && Phase != BuildPhase.Done && Phase != BuildPhase.Failed && Phase != BuildPhase.Cancelled;

    public string PhaseLabel => Phase switch
    {
        BuildPhase.Idle => "Готов",
        BuildPhase.Cleaning => "Очистка",
        BuildPhase.Compiling => "Компиляция",
        BuildPhase.Packaging => "Упаковка JAR",
        BuildPhase.Copying => "Копирование в mods",
        BuildPhase.Done => "Готово",
        BuildPhase.Failed => "Ошибка",
        BuildPhase.Cancelled => "Отменено",
        _ => Phase.ToString()
    };
}
