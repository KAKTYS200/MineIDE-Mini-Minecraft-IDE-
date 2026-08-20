using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MineIDE.Models;

namespace MineIDE.Services;

public class BuildService
{
    private static BuildService? _instance;
    public static BuildService Instance => _instance ??= new BuildService();

    public BuildStatus Status { get; private set; } = new();
    public event EventHandler<BuildStatus>? StatusChanged;
    public event EventHandler<LogEntry>? LogEmitted;

    private CancellationTokenSource? _cts;
    private Process? _activeProcess;

    // javac/groovy:  C:\path\File.java:12: error: cannot find symbol
    private static readonly Regex JavacProblemRegex = new(
        @"^(?<file>.*\.(java|groovy|kt|kts)):(?<line>\d+): (?<level>error|warning): (?<msg>.*)$",
        RegexOptions.Compiled);

    private enum RealBuildResult { Success, Failed, NotAvailable }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _activeProcess?.Kill(true); } catch { }
    }

    public async Task<BuildStatus> BuildAsync(string projectName, string? projectPath = null)
    {
        if (Status.IsRunning) return Status;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = new BuildStatus { Phase = BuildPhase.Cleaning, Progress = 0, StartedAt = DateTime.Now, Message = "Инициализация..." };
        RaiseStatus();

        Log(LogLevel.Info, "Gradle", "Запуск gradle build...");

        try
        {
            bool isRealProject = !string.IsNullOrEmpty(projectPath) &&
                                 File.Exists(Path.Combine(projectPath, "build.gradle"));

            if (isRealProject)
            {
                var gradle = GradleLocator.Find(projectPath!);
                var result = await TryRealGradleBuildAsync(gradle, projectPath!, projectName, token);
                if (result == RealBuildResult.Success || result == RealBuildResult.Failed)
                    return Status;

                Log(LogLevel.Warning, "Build", "Gradle не найден или не запустился — переход к демо-сборке.");
                Log(LogLevel.Warning, "Build", "Чтобы собирать по-настоящему: установите Gradle или положите gradlew.bat в корень проекта.");
            }
            else
            {
                Log(LogLevel.Warning, "Build", "build.gradle не найден — используется демо-сборка (симуляция).");
            }

            await SimulateBuildAsync(projectName, token);
        }
        catch (OperationCanceledException)
        {
            Status.Phase = BuildPhase.Cancelled;
            Status.Message = "Сборка отменена";
            Status.FinishedAt = DateTime.Now;
            RaiseStatus();
            Log(LogLevel.Warning, "Build", "Сборка отменена пользователем.");
        }
        catch (Exception ex)
        {
            Status.Phase = BuildPhase.Failed;
            Status.Message = ex.Message;
            Status.LastError = ex.Message;
            Status.FinishedAt = DateTime.Now;
            RaiseStatus();
            Log(LogLevel.Error, "Build", "Ошибка сборки: " + ex.Message);
        }
        return Status;
    }

    // ---------- real Gradle build ----------

    private async Task<RealBuildResult> TryRealGradleBuildAsync(string? gradle, string projectPath, string projectName, CancellationToken token)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = projectPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // .NET cannot start a .bat directly (Win32Exception); route it through cmd.exe.
            if (gradle!.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c \"\"{gradle}\" build -x test --console=plain\"";
            }
            else
            {
                psi.FileName = gradle;
                psi.Arguments = "build -x test --console=plain";
            }

            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, "Build", "Не удалось запустить Gradle: " + ex.Message);
            return RealBuildResult.NotAvailable;
        }

        _activeProcess = proc;
        var queue = new BlockingCollection<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) queue.Add(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) queue.Add(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var consumer = Task.Run(() =>
        {
            foreach (var line in queue.GetConsumingEnumerable())
            {
                if (token.IsCancellationRequested) break;
                ConsumeGradleLine(line);
            }
        });

        try
        {
            await proc.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            queue.CompleteAdding();
            await Task.WhenAny(consumer, Task.Delay(500));
            _activeProcess = null;
            Status.Phase = BuildPhase.Cancelled;
            Status.Message = "Сборка отменена";
            Status.FinishedAt = DateTime.Now;
            RaiseStatus();
            Log(LogLevel.Warning, "Build", "Сборка отменена пользователем.");
            return RealBuildResult.Failed;
        }

        proc.WaitForExit(); // drain async output buffers
        queue.CompleteAdding();
        await consumer;
        _activeProcess = null;

        int exit = proc.ExitCode;
        var libsDir = Path.Combine(projectPath, "build", "libs");
        var jars = Directory.Exists(libsDir) ? Directory.GetFiles(libsDir, "*.jar") : Array.Empty<string>();
        var jar = jars.Length > 0 ? jars.OrderByDescending(File.GetLastWriteTime).First() : null;

        if (exit == 0 && jar != null)
        {
            Status.Phase = BuildPhase.Done;
            Status.Progress = 100;
            Status.Message = "Сборка завершена";
            Status.FinishedAt = DateTime.Now;
            Status.OutputJarPath = jar;
            RaiseStatus();
            Log(LogLevel.Success, "Build", $"JAR готов: {jar} ({new FileInfo(jar).Length / 1048576.0:F2} MB)");
            Log(LogLevel.Success, "Build", "BUILD SUCCESSFUL — реальная сборка Gradle.");
            return RealBuildResult.Success;
        }

        Status.Phase = BuildPhase.Failed;
        Status.Message = exit == 0 ? "Сборка прошла, но JAR не найден в build/libs" : $"Gradle завершился с кодом {exit}";
        Status.LastError = Status.Message;
        Status.FinishedAt = DateTime.Now;
        RaiseStatus();
        Log(LogLevel.Error, "Build", Status.Message);
        return RealBuildResult.Failed;
    }

    private void ConsumeGradleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var trimmed = line.Trim();

        if (trimmed.StartsWith("> Task :", StringComparison.Ordinal))
        {
            MapTaskToPhase(trimmed);
            Log(LogLevel.Debug, "Gradle", trimmed);
            return;
        }

        var m = JavacProblemRegex.Match(line);
        if (m.Success)
        {
            var isError = m.Groups["level"].Value == "error";
            LogEmitted?.Invoke(this, new LogEntry
            {
                Level = isError ? LogLevel.Error : LogLevel.Warning,
                Source = "Java",
                Message = m.Groups["msg"].Value,
                File = m.Groups["file"].Value,
                Line = int.TryParse(m.Groups["line"].Value, out var ln) ? ln : null
            });
            return;
        }

        if (trimmed.Contains("BUILD SUCCESSFUL", StringComparison.OrdinalIgnoreCase))
        {
            Log(LogLevel.Success, "Build", trimmed);
            return;
        }
        if (trimmed.Contains("BUILD FAILED", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("FAILURE:", StringComparison.OrdinalIgnoreCase))
        {
            Log(LogLevel.Error, "Build", trimmed);
            return;
        }
        if (line.Contains(": error:", StringComparison.OrdinalIgnoreCase))
        {
            Log(LogLevel.Error, "Gradle", line);
            return;
        }
        if (line.Contains("warning:", StringComparison.OrdinalIgnoreCase))
        {
            Log(LogLevel.Warning, "Gradle", line);
            return;
        }

        Log(LogLevel.Info, "Gradle", line);
    }

    private void MapTaskToPhase(string taskLine)
    {
        string t = taskLine.ToLowerInvariant();
        var phase = Status.Phase;
        double target = Status.Progress;
        string msg = Status.Message;

        if (t.Contains("clean"))
        {
            phase = BuildPhase.Cleaning; target = Math.Max(target, 8); msg = "Очистка";
        }
        else if (t.Contains("compile") || t.Contains("sourcemain") || t.Contains("generate"))
        {
            phase = BuildPhase.Compiling; target = Math.Max(target, 50); msg = "Компиляция";
        }
        else if (t.Contains("jar") || t.Contains("reobf") || t.Contains("sign"))
        {
            phase = BuildPhase.Packaging; target = Math.Max(target, 80); msg = "Упаковка JAR";
        }
        else if (t.Contains("copymods") || t.Contains("prepare"))
        {
            phase = BuildPhase.Copying; target = Math.Max(target, 92); msg = "Копирование";
        }

        if (phase != Status.Phase || target != Status.Progress || msg != Status.Message)
        {
            Status.Phase = phase;
            Status.Progress = Math.Min(100, target);
            Status.Message = msg;
            RaiseStatus();
        }
    }

    // ---------- demo fallback (no Gradle installed) ----------

    private async Task SimulateBuildAsync(string projectName, CancellationToken token)
    {
        var log = Log;

        log(LogLevel.Info, "Gradle", "Запуск gradle build (демо)...");
        await Step(BuildPhase.Cleaning, "Очистка предыдущих артефактов", 5, 600, token);
        log(LogLevel.Debug, "Gradle", "> Task :clean");

        log(LogLevel.Info, "Gradle", "Загрузка зависимостей MinecraftForge...");
        await Step(BuildPhase.Compiling, "Загрузка зависимостей", 25, 1200, token);
        log(LogLevel.Debug, "Gradle", "> Configure project :");
        log(LogLevel.Debug, "Gradle", "> Resolving dependencies...");

        log(LogLevel.Info, "Java", "Компиляция исходников...");
        await Step(BuildPhase.Compiling, "Компиляция Java", 60, 1500, token);
        log(LogLevel.Debug, "Java", "> javac --release 17 src/main/java/...");
        log(LogLevel.Success, "Java", "Compiled 1 source file with 0 errors.");

        log(LogLevel.Info, "Forge", "Обработка мод-метаданных...");
        await Step(BuildPhase.Packaging, "Обработка mods.toml", 75, 700, token);

        log(LogLevel.Info, "Forge", "Парсинг @Mod аннотаций...");
        log(LogLevel.Debug, "Forge", "> Found modId=" + projectName);

        log(LogLevel.Info, "Jar", "Упаковка .jar...");
        await Step(BuildPhase.Packaging, "Упаковка JAR", 90, 900, token);
        log(LogLevel.Debug, "Jar", "> jar cf build/libs/" + projectName + "-1.0.0.jar");

        token.ThrowIfCancellationRequested();

        log(LogLevel.Info, "Forge", "Копирование в директорию mods...");
        await Step(BuildPhase.Copying, "Копирование в runs/mods", 97, 500, token);

        var jarPath = Path.Combine("build", "libs", $"{projectName}-1.0.0.jar");
        Status.Phase = BuildPhase.Done;
        Status.Progress = 100;
        Status.Message = "Сборка завершена";
        Status.FinishedAt = DateTime.Now;
        Status.OutputJarPath = jarPath;
        RaiseStatus();

        log(LogLevel.Success, "Build", $"JAR готов: {jarPath} (1.2 MB) — демо");
        log(LogLevel.Success, "Build", "Пайплайн сборки успешно завершён (демо-режим).");
    }

    private async Task Step(BuildPhase phase, string msg, double target, int ms, CancellationToken token)
    {
        Status.Phase = phase;
        Status.Message = msg;
        const int steps = 20;
        double start = Status.Progress;
        double delta = target - start;
        for (int i = 0; i <= steps; i++)
        {
            token.ThrowIfCancellationRequested();
            Status.Progress = start + delta * (i / (double)steps);
            RaiseStatus();
            await Task.Delay(ms / steps, token);
        }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status);

    private void Log(LogLevel lvl, string src, string msg)
        => LogEmitted?.Invoke(this, new LogEntry { Level = lvl, Source = src, Message = msg });
}
