using System;

namespace MineIDE.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Debug,
    Minecraft
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Source { get; set; } = "IDE";
    public string Message { get; set; } = "";
    public string? File { get; set; }
    public int? Line { get; set; }
    public int? Column { get; set; }

    public string TimeLabel => Timestamp.ToString("HH:mm:ss");

    public string LevelLabel => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERR",
        LogLevel.Debug => "DBG",
        LogLevel.Minecraft => "MC",
        _ => "INFO"
    };

    public string Icon => Level switch
    {
        LogLevel.Info => "\uE946",
        LogLevel.Success => "\uE73E",
        LogLevel.Warning => "\uE7BA",
        LogLevel.Error => "\uEA39",
        LogLevel.Debug => "\uE94F",
        LogLevel.Minecraft => "\uE8A5",
        _ => "\uE946"
    };
}
