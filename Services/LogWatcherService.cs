using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MineIDE.Models;

namespace MineIDE.Services;

public class CrashReport
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Title { get; set; } = "Minecraft crashed";
    public string ExitCode { get; set; } = "0";
    public List<LogEntry> RelevantLogs { get; set; } = new();
    public string? StackTrace { get; set; }
    public string Suggestions { get; set; } = "";

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Crash Report — {Time:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"**Title:** {Title}");
        sb.AppendLine($"**Exit code:** {ExitCode}");
        sb.AppendLine();
        sb.AppendLine("## Последние записи лога");
        foreach (var e in RelevantLogs.TakeLast(40))
            sb.AppendLine($"- [{e.TimeLabel}] **{e.Source}** [{e.LevelLabel}] {e.Message}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(StackTrace))
        {
            sb.AppendLine("## Stack Trace");
            sb.AppendLine("```");
            sb.AppendLine(StackTrace);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (!string.IsNullOrEmpty(Suggestions))
        {
            sb.AppendLine("## Предложения");
            sb.AppendLine(Suggestions);
        }
        return sb.ToString();
    }
}

public class LogWatcherService
{
    private static LogWatcherService? _instance;
    public static LogWatcherService Instance => _instance ??= new LogWatcherService();

    private readonly Regex _minecraftError = new(@"\b(ERROR|FATAL|Exception|Caused by:|at\s+\S+\.\S+\([\w\.\d\:]+\))", RegexOptions.Compiled);
    private readonly Regex _minecraftStack = new(@"Exception in thread [\""].+[\""]", RegexOptions.Compiled);

    public List<LogEntry> Captured { get; } = new();

    public void Capture(LogEntry e)
    {
        if (e.Level == LogLevel.Error || e.Level == LogLevel.Warning || e.Level == LogLevel.Minecraft)
            Captured.Add(e);
        if (Captured.Count > 2000)
            Captured.RemoveRange(0, 500);
    }

    public CrashReport? GenerateCrashReport(int exitCode)
    {
        if (exitCode == 0 && !Captured.Any(e => e.Level == LogLevel.Error)) return null;

        var errors = Captured.Where(e => e.Level == LogLevel.Error).ToList();
        var warn = Captured.Where(e => e.Level == LogLevel.Warning).ToList();
        var stack = Captured.LastOrDefault(e => _minecraftStack.IsMatch(e.Message));

        if (errors.Count == 0 && warn.Count == 0 && stack == null) return null;

        var sb = new StringBuilder();
        // Extract a small surrounding context
        int idx = Captured.IndexOf(errors.LastOrDefault() ?? stack!);
        if (idx >= 0)
        {
            int start = Math.Max(0, idx - 6);
            int end = Math.Min(Captured.Count - 1, idx + 14);
            for (int i = start; i <= end; i++)
                sb.AppendLine(Captured[i].Message);
        }

        return new CrashReport
        {
            Time = DateTime.Now,
            Title = errors.Count > 0 ? "Minecraft завершился с ошибкой" : "Minecraft завершился аварийно",
            ExitCode = exitCode.ToString(),
            RelevantLogs = Captured.ToList(),
            StackTrace = sb.ToString(),
            Suggestions = ComposeSuggestions(errors, exitCode)
        };
    }

    private string ComposeSuggestions(List<LogEntry> errors, int exitCode)
    {
        var sb = new StringBuilder();
        var firstErrors = errors.Take(5).ToList();
        if (firstErrors.Any(e => e.Message.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Увеличьте -Xmx в настройках запуска (сейчас 2G).");
        if (firstErrors.Any(e => e.Message.Contains("Mixin", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Обнаружен конфликт Mixin. Проверьте совместимость версий мода и Forge.");
        if (firstErrors.Any(e => e.Message.Contains("ClassNotFound", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Не найден класс. Возможно, отсутствует зависимость.");
        if (firstErrors.Any(e => e.Message.Contains("Forge", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("- Проверьте, что версии Forge и Minecraft совпадают.");
        if (sb.Length == 0)
            sb.AppendLine("- Перепроверьте последние изменения в коде и повторите сборку.\n- Изучите раздел Problems внизу окна.");

        return sb.ToString();
    }
}
