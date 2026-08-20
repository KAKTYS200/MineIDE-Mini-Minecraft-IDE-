using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MineIDE.Models;

namespace MineIDE.Services;

public class MinecraftLauncherService
{
    private static MinecraftLauncherService? _instance;
    public static MinecraftLauncherService Instance => _instance ??= new MinecraftLauncherService();

    public event EventHandler<LogEntry>? LogEmitted;
    public event EventHandler<int>? Exited;
    public event EventHandler<CrashReport>? CrashReportCreated;

    private Process? _proc;
    private bool _running;
    private FileSystemWatcher? _logWatcher;
    private FileSystemWatcher? _crashWatcher;
    private long _logPosition;
    private string? _watchedLogsDir;
    private readonly HashSet<string> _parsedCrashFiles = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _running && (_proc == null || !_proc.HasExited);

    public string? LastJarPath { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? ExitedAt { get; private set; }

    /// <summary>
    /// Launches Minecraft for the project. Preference order:
    /// 1) locally installed Forge client (e.g. "Forge 1.20.1" in .minecraft) — mods are copied into its mods folder;
    /// 2) gradlew runClient / runServer / runData — the real Forge dev environment;
    /// 3) java -jar — for standalone jars;
    /// 4) simulation — only when nothing else is available.
    /// </summary>
    public async Task<bool> LaunchAsync(string jarPath, string projectPath, string gradleTask = "runClient", string? minecraftVersion = null)
    {
        if (IsRunning) return false;

        LastJarPath = jarPath;
        StartedAt = DateTime.Now;

        // 1) Installed Forge client — test the mod in the real game
        if (gradleTask == "runClient" && !string.IsNullOrEmpty(minecraftVersion))
        {
            var install = MinecraftInstallLocator.FindForge(minecraftVersion);
            if (install != null)
            {
                Emit(LogLevel.Info, "Launcher",
                    $"Найдена установка: Minecraft {install.MinecraftVersion} (Forge {install.ForgeVersion}) — {install.RootDir}");
                if (TryStartInstalledForge(install, projectPath, jarPath, out var forgeProc))
                {
                    _proc = forgeProc;
                    _running = true;
                    WatchLogs(Path.Combine(install.RootDir, "logs"));
                    WatchCrashReports(Path.Combine(install.RootDir, "crash-reports"));
                    WireExit();
                    return true;
                }
                Emit(LogLevel.Warning, "Launcher", "Установленный Forge не запустился — пробую другие способы.");
            }
            else
            {
                Emit(LogLevel.Info, "Launcher", $"Установка Forge {minecraftVersion} не найдена — переключаюсь на другие способы запуска.");
            }
        }

        // 2) Real dev pipeline: gradlew runClient / runServer / runData (ForgeGradle projects)
        if (!string.IsNullOrEmpty(projectPath) && Directory.Exists(projectPath) && IsForgeProject(projectPath))
        {
            var gradle = GradleLocator.Find(projectPath);
            if (gradle != null && TryStart(gradle, $"{gradleTask} --console=plain", projectPath, out var devProc))
            {
                _proc = devProc;
                _running = true;
                Emit(LogLevel.Info, "Launcher", $"Запуск Minecraft через Gradle {gradleTask} (реальная среда разработки).");
                WatchCrashReports(Path.Combine(projectPath, "runs", "crash-reports"));
                WireExit();
                return true;
            }
            if (gradle != null)
                Emit(LogLevel.Warning, "Launcher", "Gradle не запустился — пробую java -jar.");
        }

        // 3) Real jar launch
        if (!string.IsNullOrEmpty(jarPath) && File.Exists(jarPath) && TryStartJava(jarPath, projectPath, out var jarProc))
        {
            _proc = jarProc;
            _running = true;
            Emit(LogLevel.Info, "Launcher", $"Запуск {Path.GetFileName(jarPath)} через java -jar.");
            WatchLogs(Path.Combine(projectPath, "runs", "logs"));
            WatchCrashReports(Path.Combine(projectPath, "runs", "crash-reports"));
            WireExit();
            return true;
        }

        // 4) Simulation — no Java, no Gradle, no installed Forge
        Emit(LogLevel.Warning, "Launcher", "Java, Gradle и установленный Forge не найдены — запуск в режиме симуляции.");
        _running = true;
        await SimulateRunAsync();
        return true;
    }

    public void Stop()
    {
        if (_proc != null && !_proc.HasExited)
        {
            try { _proc.Kill(true); } catch { }
        }
        _running = false;
    }

    // ---------- installed Forge client ----------

    private bool TryStartInstalledForge(MinecraftInstall install, string projectPath, string modJarPath, out Process proc)
    {
        proc = null!;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(install.JsonPath));
            var root = doc.RootElement;

            var mainClass = root.TryGetProperty("mainClass", out var mc) && mc.ValueKind == JsonValueKind.String
                ? mc.GetString() ?? "cpw.mods.bootstraplauncher.BootstrapLauncher"
                : "cpw.mods.bootstraplauncher.BootstrapLauncher";

            var libraryDir = Path.Combine(install.RootDir, "libraries");
            var assetsRoot = Path.Combine(install.RootDir, "assets");

            string assetIndex = "5";
            if (root.TryGetProperty("assetIndex", out var ai) && ai.TryGetProperty("id", out var aid))
                assetIndex = aid.GetString() ?? "5";

            // ---- classpath: libraries + version jar ----
            var cp = new List<string>();
            if (root.TryGetProperty("libraries", out var libs))
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    var name = lib.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Contains(":natives-", StringComparison.Ordinal)) continue; // natives are pre-extracted

                    string? path = null;
                    if (lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art) &&
                        art.TryGetProperty("path", out var p))
                        path = p.GetString();

                    path ??= LibraryPathFromName(name, libraryDir);
                    if (path != null && File.Exists(path)) cp.Add(path);
                }
            }

            var versionJar = Path.Combine(install.VersionDir, install.VersionId + ".jar");
            if (!File.Exists(versionJar))
            {
                var jars = Directory.GetFiles(install.VersionDir, "*.jar");
                if (jars.Length > 0) versionJar = jars[0];
            }
            if (File.Exists(versionJar)) cp.Add(versionJar);

            // ---- placeholders ----
            var ph = new Dictionary<string, string>
            {
                ["${auth_player_name}"] = "Player",
                ["${auth_uuid}"] = Guid.NewGuid().ToString(),
                ["${auth_access_token}"] = "0",
                ["${auth_xuid}"] = "0",
                ["${clientid}"] = "0",
                ["${user_type}"] = "msa",
                ["${version_name}"] = install.VersionId,
                ["${version_type}"] = "release",
                ["${game_directory}"] = install.RootDir,
                ["${assets_root}"] = assetsRoot,
                ["${assets_index_name}"] = assetIndex,
                ["${launcher_name}"] = "MineIDE",
                ["${launcher_version}"] = "1.0.0",
                ["${classpath}"] = string.Join(Path.PathSeparator, cp),
                ["${classpath_separator}"] = Path.PathSeparator.ToString(),
                ["${library_directory}"] = libraryDir,
                ["${natives_directory}"] = install.NativesDir,
                ["${resolution_width}"] = "1280",
                ["${resolution_height}"] = "720"
            };

            var jvmArgs = CollectArgs(root, "jvm", "windows", ph);
            var gameArgs = CollectArgs(root, "game", "windows", ph);

            // ---- copy the built mod into the install's mods folder ----
            if (!string.IsNullOrEmpty(modJarPath) && File.Exists(modJarPath))
            {
                var modsDir = Path.Combine(install.RootDir, "mods");
                Directory.CreateDirectory(modsDir);
                var target = Path.Combine(modsDir, Path.GetFileName(modJarPath));

                // Respect the on/off state from the Mods panel: if the mod was turned off
                // (runs/mods/<name>.jar.disabled), do not copy it — and remove a stale copy.
                var runsMods = Path.Combine(projectPath, "runs", "mods");
                var disabledMarker = Path.Combine(runsMods, Path.GetFileName(modJarPath) + ".disabled");
                if (File.Exists(disabledMarker))
                {
                    if (File.Exists(target))
                    {
                        try { File.Delete(target); } catch { }
                        Emit(LogLevel.Info, "Launcher", $"Мод выключен — старый файл удалён из {target}");
                    }
                    Emit(LogLevel.Info, "Launcher", $"Мод {Path.GetFileName(modJarPath)} выключен в панели Mods — не копируем в установку.");
                }
                else
                {
                    File.Copy(modJarPath, target, true);
                    Emit(LogLevel.Success, "Launcher", $"Мод скопирован в {target}");
                }
            }
            else
            {
                Emit(LogLevel.Warning, "Launcher", "JAR мода не найден — Forge запустится без вашего мода. Соберите проект реальным Gradle.");
            }

            // ---- java + process ----
            var java = FindJava(install.RootDir);
            var psi = new ProcessStartInfo
            {
                FileName = java,
                WorkingDirectory = install.RootDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-Xmx2G");
            foreach (var a in jvmArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(mainClass);
            foreach (var a in gameArgs) psi.ArgumentList.Add(a);

            Emit(LogLevel.Debug, "Launcher", $"Java: {java}");
            Emit(LogLevel.Debug, "Launcher", $"Запуск: {install.VersionId} (Forge {install.ForgeVersion}, MC {install.MinecraftVersion})");

            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Emit(LogLevel.Minecraft, "MC", e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit(LogLevel.Warning, "MC", e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Warning, "Launcher", "Не удалось запустить установленный Forge: " + ex.Message);
            return false;
        }
    }

    private static List<string> CollectArgs(JsonElement root, string section, string osName, Dictionary<string, string> placeholders)
    {
        var result = new List<string>();
        if (!root.TryGetProperty("arguments", out var args) || !args.TryGetProperty(section, out var list))
            return result;

        foreach (var item in list.EnumerateArray())
        {
            string[] values;
            if (item.ValueKind == JsonValueKind.String)
            {
                values = new[] { item.GetString() ?? "" };
            }
            else
            {
                if (!RuleAllows(item, osName)) continue;
                if (!item.TryGetProperty("values", out var v)) continue;
                values = v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            }
            foreach (var raw in values)
            {
                var arg = raw;
                foreach (var (key, val) in placeholders)
                    arg = arg.Replace(key, val);
                result.Add(arg);
            }
        }
        return result;
    }

    private static bool RuleAllows(JsonElement item, string osName)
    {
        // No rules, or an EMPTY rules list → the argument always applies.
        // Forge ships always-args (${classpath}, module-path, natives, …) as
        // "rules": [] — dropping them breaks the launch with ClassNotFoundException.
        if (!item.TryGetProperty("rules", out var rules) || rules.GetArrayLength() == 0) return true;

        // Mojang spec: start disallowed; each APPLICABLE rule sets allow/deny.
        // This excludes e.g. macOS-only args (-XstartOnFirstThread) on Windows.
        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            bool applies = true;
            if (rule.TryGetProperty("os", out var os) && os.TryGetProperty("name", out var on))
            {
                if (on.GetString() != osName) applies = false;
            }
            if (rule.TryGetProperty("features", out _)) applies = false; // we enable no optional features
            if (!applies) continue;
            allowed = rule.TryGetProperty("action", out var act) && act.GetString() == "allow";
        }
        return allowed;
    }

    private static string? LibraryPathFromName(string name, string libraryDir)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return Path.Combine(libraryDir, group, artifact, version, $"{artifact}-{version}{classifier}.jar");
    }

    private static string FindJava(string minecraftRoot)
    {
        var candidates = new List<string>();

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));

        // Bundled launcher runtimes (java-runtime-gamma = Java 17)
        var runtimeDir = Path.Combine(minecraftRoot, "runtime");
        if (Directory.Exists(runtimeDir))
        {
            candidates.AddRange(Directory.GetDirectories(runtimeDir)
                .Select(d => Path.Combine(d, "bin", "java.exe"))
                .Where(File.Exists));
            candidates.AddRange(Directory.GetFiles(runtimeDir, "java.exe", SearchOption.AllDirectories)
                .Where(p => !p.Contains("jre-legacy", StringComparison.OrdinalIgnoreCase)));
        }

        candidates.Add("java"); // PATH fallback (resolved at process start)
        return candidates.FirstOrDefault(c => c == "java" || File.Exists(c)) ?? "java";
    }

    // ---------- process helpers ----------

    private static bool IsForgeProject(string projectPath)
    {
        try
        {
            var gradleFile = Path.Combine(projectPath, "build.gradle");
            if (!File.Exists(gradleFile)) return false;
            var content = File.ReadAllText(gradleFile);
            return content.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("forgegradle", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool TryStart(string exe, string args, string workingDir, out Process proc)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // .NET cannot start a .bat directly (Win32Exception); route it through cmd.exe.
            if (exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/c \"\"{exe}\" {args}\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = args;
            }

            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) Emit(LogLevel.Minecraft, "MC", e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit(LogLevel.Warning, "MC", e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Debug, "Launcher", "Запуск не удался: " + ex.Message);
            proc = null!;
            return false;
        }
    }

    private bool TryStartJava(string jarPath, string workingDir, out Process proc)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
        var java = string.IsNullOrEmpty(javaHome) ? "java" : Path.Combine(javaHome, "bin", "java.exe");

        Emit(LogLevel.Debug, "Launcher", $"Java: {java}");
        return TryStart(java, $"-Xmx2G -jar \"{jarPath}\" nogui", workingDir, out proc);
    }

    private void WireExit()
    {
        _proc!.Exited += (_, _) =>
        {
            _running = false;
            ExitedAt = DateTime.Now;
            FinalizeLogs();
            PerformanceService.Instance.Detach();
            Exited?.Invoke(this, _proc?.ExitCode ?? -1);
        };
        PerformanceService.Instance.Attach(_proc);
    }

    // ---------- real log file tailing ----------

    private void WatchLogs(string logsDir)
    {
        _watchedLogsDir = logsDir;
        try
        {
            Directory.CreateDirectory(logsDir);
            _logPosition = 0;

            _logWatcher = new FileSystemWatcher(logsDir, "latest.log")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            _logWatcher.Changed += OnLogFileEvent;
            _logWatcher.Created += OnLogFileEvent;
            _logWatcher.EnableRaisingEvents = true;

            TailLogFile(Path.Combine(logsDir, "latest.log"));
        }
        catch { /* watcher is best-effort */ }
    }

    private void OnLogFileEvent(object sender, FileSystemEventArgs e) => TailLogFile(e.FullPath);

    private void TailLogFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _logPosition) _logPosition = 0; // file was rotated/recreated

            fs.Seek(_logPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                _logPosition = fs.Position;
                Emit(LogLevel.Minecraft, "MC", line);
            }
        }
        catch { /* file may be locked while MC writes — next event will retry */ }
    }

    private void FinalizeLogs()
    {
        if (_logWatcher != null)
        {
            _logWatcher.EnableRaisingEvents = false;
            _logWatcher.Dispose();
            _logWatcher = null;
        }
        if (_watchedLogsDir != null)
            TailLogFile(Path.Combine(_watchedLogsDir, "latest.log"));
    }

    // ---------- real crash reports ----------

    private void WatchCrashReports(string crashDir)
    {
        try
        {
            Directory.CreateDirectory(crashDir);

            foreach (var f in Directory.GetFiles(crashDir, "crash-reports-*.txt"))
                ProcessCrashFile(f);

            _crashWatcher = new FileSystemWatcher(crashDir, "crash-reports-*.txt")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            _crashWatcher.Created += OnCrashFileEvent;
            _crashWatcher.Changed += OnCrashFileEvent;
            _crashWatcher.EnableRaisingEvents = true;
        }
        catch { /* best-effort */ }
    }

    private void OnCrashFileEvent(object sender, FileSystemEventArgs e) => ProcessCrashFile(e.FullPath);

    private void ProcessCrashFile(string path)
    {
        if (_parsedCrashFiles.Contains(path)) return;
        _parsedCrashFiles.Add(path);

        var report = ParseCrashFile(path);
        if (report != null)
            CrashReportCreated?.Invoke(this, report);
    }

    private static CrashReport? ParseCrashFile(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            string description = "";
            var stack = new List<string>();
            bool inStack = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("Description:", StringComparison.Ordinal))
                {
                    description = line.Substring("Description:".Length).Trim();
                }
                else if (line.StartsWith("java.", StringComparison.Ordinal) || line.StartsWith("\tat ", StringComparison.Ordinal))
                {
                    inStack = true;
                    if (stack.Count < 16) stack.Add(line);
                }
                else if (inStack && string.IsNullOrWhiteSpace(line))
                {
                    break;
                }
            }

            var stackText = string.Join(Environment.NewLine, stack);
            return new CrashReport
            {
                Time = File.GetLastWriteTime(path),
                Title = string.IsNullOrEmpty(description) ? "Minecraft crashed" : description,
                ExitCode = "file",
                StackTrace = stackText,
                Suggestions = SuggestFromText(stackText)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string SuggestFromText(string text)
    {
        var sb = new StringBuilder();
        if (text.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- Увеличьте -Xmx в настройках запуска (сейчас 2G).");
        if (text.Contains("Mixin", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- Обнаружен конфликт Mixin. Проверьте совместимость версий мода и Forge.");
        if (text.Contains("ClassNotFound", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- Не найден класс. Возможно, отсутствует зависимость.");
        if (text.Contains("Forge", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("- Проверьте, что версии Forge и Minecraft совпадают.");
        if (sb.Length == 0)
            sb.AppendLine("- Перепроверьте последние изменения в коде и повторите сборку.");
        return sb.ToString();
    }

    // ---------- simulation (nothing available) ----------

    private async Task SimulateRunAsync()
    {
        _running = true;
        var ct = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            var lines = new[]
            {
                "[INFO] Loading Minecraft 1.20.1 with Forge 47.2.0 (демо)",
                "[INFO] Mod list: examplemod 1.0.0",
                "[INFO] Initializing textures...",
                "[INFO] Preparing spawn area: 0%",
                "[INFO] Preparing spawn area: 50%",
                "[INFO] Preparing spawn area: 100%",
                "[INFO] Time elapsed: 1234 ms",
                "[INFO] Loading net.minecraftforge.client.gui.TitleScreen",
                "[CHAT] <Steve> Hello, world!",
            };
            int i = 0;
            while (!ct.IsCancellationRequested && i < lines.Length)
            {
                Emit(LogLevel.Minecraft, "MC", lines[i]);
                i++;
                await Task.Delay(700);
            }
            _running = false;
            ExitedAt = DateTime.Now;
            Exited?.Invoke(this, 0);
        });

        var watcher = Task.Run(async () =>
        {
            while (_running && !ct.IsCancellationRequested) await Task.Delay(200);
            ct.Cancel();
        });
        await task;
    }

    private void Emit(LogLevel lvl, string source, string msg)
        => LogEmitted?.Invoke(this, new LogEntry { Level = lvl, Source = source, Message = msg });
}