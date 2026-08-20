using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MineIDE.Models;
using MineIDE.Services;

namespace MineIDE.ViewModels;

public class MainViewModel : ViewModelBase
{
    public ObservableCollection<Project> Projects { get; } = new();
    public ObservableCollection<EditorTab> Tabs { get; } = new();
    public ObservableCollection<LogEntry> BuildLogs { get; } = new();
    public ObservableCollection<LogEntry> ConsoleLogs { get; } = new();
    public ObservableCollection<LogEntry> MinecraftLogs { get; } = new();
    public ObservableCollection<LogEntry> Problems { get; } = new();
    public ObservableCollection<LogEntry> GradleLogs { get; } = new();
    public ObservableCollection<CrashReport> CrashReports { get; } = new();
    public ObservableCollection<FileNode> ExplorerRoots { get; } = new();

    // Real data from the project's runs/ directory
    public ObservableCollection<ModItem> Mods { get; } = new();
    public ObservableCollection<string> ResourcePacks { get; } = new();
    public ObservableCollection<string> DataPacks { get; } = new();
    public ObservableCollection<string> Worlds { get; } = new();
    public ObservableCollection<string> LogFiles { get; } = new();

    // Selected arbitrary mod folder (idea: browse any mod directory, not only runs/mods)
    public ObservableCollection<FileNode> ModFolderFiles { get; } = new();

    private string? _modFolderPath;
    public string? ModFolderPath
    {
        get => _modFolderPath;
        set { _modFolderPath = value; OnPropertyChanged(); }
    }

    /// <summary>Opens an arbitrary folder (a downloaded mod, a jar extracted dir, …) and shows its file tree.</summary>
    public void OpenModFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        ModFolderPath = path;
        ModFolderFiles.Clear();
        try
        {
            ModFolderFiles.Add(FileService.Instance.BuildTree(path));
        }
        catch (Exception ex)
        {
            ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Error,
                Source = "IDE",
                Message = "Не удалось открыть папку мода: " + ex.Message
            });
        }
    }

    public ObservableCollection<RunProfile> Profiles { get; } = new()
    {
        new RunProfile { Name = "client", Description = "Запуск клиента (gradlew runClient)", GradleTask = "runClient", Icon = "\uE768" },
        new RunProfile { Name = "server", Description = "Запуск сервера (gradlew runServer)", GradleTask = "runServer", Icon = "\uE7C4" },
        new RunProfile { Name = "data", Description = "Генерация данных (gradlew runData)", GradleTask = "runData", Icon = "\uE93C" }
    };

    private RunProfile? _selectedProfile;
    public RunProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { _selectedProfile = value; OnPropertyChanged(); }
    }

    private string _activePanel = "Explorer";
    public string ActivePanel
    {
        get => _activePanel;
        set { _activePanel = value; OnPropertyChanged(); }
    }

    private Project? _selectedProject;
    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (_selectedProject != null)
                _selectedProject.PropertyChanged -= OnProjectPropertyChanged;
            _selectedProject = value;
            OnPropertyChanged();
            if (value != null)
            {
                value.PropertyChanged += OnProjectPropertyChanged;
                LoadProject(value);
                RefreshForgeVersions(value);
            }
        }
    }

    private void OnProjectPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // When the MC version changes, the list of available Forge versions changes too.
        if (e.PropertyName == nameof(Project.MinecraftVersion) && sender is Project p)
            RefreshForgeVersions(p);
    }

    public ObservableCollection<string> ForgeVersions { get; } = new();

    private void RefreshForgeVersions(Project project)
    {
        ForgeVersions.Clear();
        var mapping = MCVersionCatalog.Mappings.FirstOrDefault(m => m.MinecraftVersion == project.MinecraftVersion);
        if (mapping == null) return;
        foreach (var v in mapping.ForgeVersions) ForgeVersions.Add(v);
        if (!ForgeVersions.Contains(project.ForgeVersion) && ForgeVersions.Count > 0)
            project.ForgeVersion = ForgeVersions[0];
        OnPropertyChanged(nameof(ForgeVersions));
    }

    private EditorTab? _activeTab;
    public EditorTab? ActiveTab
    {
        get => _activeTab;
        set { _activeTab = value; OnPropertyChanged(); ActiveEditorChanged?.Invoke(this, EventArgs.Empty); }
    }

    private BuildStatus _buildStatus = new();
    public BuildStatus BuildStatus
    {
        get => _buildStatus;
        set { _buildStatus = value; OnPropertyChanged(); }
    }

    private bool _isLauncherRunning;
    public bool IsLauncherRunning
    {
        get => _isLauncherRunning;
        set { _isLauncherRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLauncherStopped)); }
    }
    public bool IsLauncherStopped => !IsLauncherRunning;

    public List<string> McVersions { get; } = MCVersionCatalog.Mappings.Select(m => m.MinecraftVersion).ToList();

    public AppTheme CurrentTheme
    {
        get => ThemeService.Instance.Current;
        set { ThemeService.Instance.Apply(value); OnPropertyChanged(); }
    }

    public int ProblemCount => Problems.Count;
    public int ErrorCount => Problems.Count(p => p.Level == LogLevel.Error);
    public int WarningCount => Problems.Count(p => p.Level == LogLevel.Warning);

    // Real performance metrics of the launched Java process
    private double _perfCpu;
    public double PerfCpu
    {
        get => _perfCpu;
        set { _perfCpu = value; OnPropertyChanged(); OnPropertyChanged(nameof(PerfCpuLabel)); }
    }

    private double _perfMemMb;
    public double PerfMemMb
    {
        get => _perfMemMb;
        set
        {
            _perfMemMb = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PerfMemLabel));
            OnPropertyChanged(nameof(PerfMemPct));
        }
    }

    public double PerfMemMaxMb => 2048;
    public double PerfMemPct => Math.Min(100, PerfMemMb / PerfMemMaxMb * 100);
    public string PerfCpuLabel => $"Процессор: {PerfCpu:F0}%";
    public string PerfMemLabel => $"Память: {PerfMemMb:F1}G / {PerfMemMaxMb / 1024:F0}G";

    private bool _realCrashReported;

    public event EventHandler? ActiveEditorChanged;

    private void ReportInstalledForge()
    {
        try
        {
            var install = MinecraftInstallLocator.FindForge("1.20.1");
            ConsoleLogs.Add(new LogEntry
            {
                Level = install != null ? LogLevel.Success : LogLevel.Info,
                Source = "Launcher",
                Message = install != null
                    ? $"Найдена установка Minecraft {install.MinecraftVersion} (Forge {install.ForgeVersion}) — Run запустит моды в ней: {install.RootDir}"
                    : "Установка Forge не найдена — Run будет использовать runClient или демо-режим."
            });
        }
        catch
        {
            // detection is best-effort
        }
    }

    public MainViewModel()
    {
        SelectedProfile = Profiles[0];
        ReportInstalledForge();
        BuildService.Instance.StatusChanged += (s, e) => BuildStatus = e;
        BuildService.Instance.LogEmitted += AddLog;
        MinecraftLauncherService.Instance.LogEmitted += (s, e) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                MinecraftLogs.Add(e);
                LogWatcherService.Instance.Capture(e);
            });
        };
        MinecraftLauncherService.Instance.Exited += async (s, code) =>
        {
            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                IsLauncherRunning = false;
                ConsoleLogs.Add(new LogEntry
                {
                    Level = code == 0 ? LogLevel.Success : LogLevel.Error,
                    Source = "Launcher",
                    Message = code == 0 ? "Minecraft закрылся штатно." : $"Minecraft завершился с кодом {code}."
                });

                // Give the crash-reports file watcher a moment to deliver the real report.
                await Task.Delay(1500);
                if (SelectedProject != null) RefreshRunsData(SelectedProject.Path);
                if (_realCrashReported)
                {
                    _realCrashReported = false;
                    return;
                }

                var report = LogWatcherService.Instance.GenerateCrashReport(code);
                if (report != null)
                {
                    CrashReports.Insert(0, report);
                    ShowReport(report);
                }
            });
        };
        MinecraftLauncherService.Instance.CrashReportCreated += (s, report) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                _realCrashReported = true;
                CrashReports.Insert(0, report);
                ShowReport(report);
            });
        };
        PerformanceService.Instance.Sampled += (s, sample) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                PerfCpu = sample.CpuPercent;
                PerfMemMb = sample.MemoryMb;
            });
        };
    }

    public void InitSampleData()
    {
        var demo = Path.Combine(Path.GetTempPath(), "MineIDE_Demo");
        if (!Directory.Exists(demo))
            FileService.Instance.CreateSampleProject(demo, "examplemod");

        Projects.Clear();
        Projects.Add(new Project
        {
            Name = "examplemod",
            Path = demo,
            MinecraftVersion = "1.20.1",
            ForgeVersion = "47.2.0",
            ModId = "examplemod",
            Description = "Demo Forge mod",
            Type = ProjectType.Mod
        });
        Projects.Add(new Project
        {
            Name = "DragonForge",
            Path = Path.Combine(Path.GetTempPath(), "MineIDE_DragonForge"),
            MinecraftVersion = "1.20.4",
            ForgeVersion = "49.0.30",
            ModId = "dragonforge",
            Description = "Bosses, dragons и кастомные мобы",
            Type = ProjectType.Mod
        });
        Projects.Add(new Project
        {
            Name = "TexturePack_HD",
            Path = Path.Combine(Path.GetTempPath(), "MineIDE_HD"),
            MinecraftVersion = "1.20.1",
            ForgeVersion = "47.2.0",
            ModId = "hd_pack",
            Description = "HD Resource Pack",
            Type = ProjectType.ResourcePack
        });

        // Create skeleton for DragonForge and HD if absent
        if (!Directory.Exists(Projects[1].Path))
            FileService.Instance.CreateSampleProject(Projects[1].Path, "dragonforge");
        if (!Directory.Exists(Projects[2].Path))
            FileService.Instance.CreateSampleProject(Projects[2].Path, "hd_pack");

        // Auto-discover real Forge projects on the Desktop so the user can test their own mods.
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (var dir in Directory.GetDirectories(desktop))
            {
                var gradleFile = Path.Combine(dir, "build.gradle");
                if (!File.Exists(gradleFile)) continue;
                var content = File.ReadAllText(gradleFile);
                if (!content.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase)) continue;
                if (Projects.Any(p => string.Equals(p.Path, dir, StringComparison.OrdinalIgnoreCase))) continue;
                AddProject(dir);
            }
        }
        catch { /* discovery is best-effort */ }

        SelectedProject = Projects[0];
    }

    /// <summary>Adds a local Forge project (folder with build.gradle) to the project list and selects it.</summary>
    public void AddProject(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        var existing = Projects.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { SelectedProject = existing; return; }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string mc = "1.20.1", forge = "47.2.0", modId = name.ToLowerInvariant().Replace(' ', '_');

        var props = Path.Combine(path, "gradle.properties");
        if (File.Exists(props))
        {
            foreach (var line in File.ReadAllLines(props))
            {
                if (line.StartsWith("minecraft_version=", StringComparison.OrdinalIgnoreCase))
                    mc = line.Substring("minecraft_version=".Length).Trim();
                else if (line.StartsWith("forge_version=", StringComparison.OrdinalIgnoreCase))
                    forge = line.Substring("forge_version=".Length).Trim();
                else if (line.StartsWith("mod_id=", StringComparison.OrdinalIgnoreCase))
                    modId = line.Substring("mod_id=".Length).Trim();
            }
        }

        var project = new Project
        {
            Name = name,
            Path = path,
            MinecraftVersion = mc,
            ForgeVersion = forge,
            ModId = modId,
            Description = "Локальный Forge проект",
            Type = ProjectType.Mod
        };
        Projects.Add(project);
        SelectedProject = project;
    }

    public void LoadProject(Project project)
    {
        try
        {
            ExplorerRoots.Clear();
            ExplorerRoots.Add(FileService.Instance.BuildTree(project.Path));
            OpenDefaultFiles(project.Path);
            RefreshRunsData(project.Path);
        }
        catch (Exception ex)
        {
            ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Error,
                Source = "IDE",
                Message = "Не удалось загрузить проект: " + ex.Message
            });
        }
    }

    /// <summary>Reloads the file tree and the runs/ panels (Mods, Worlds, Logs, …).</summary>
    public void RefreshExplorer()
    {
        if (SelectedProject == null) return;
        ExplorerRoots.Clear();
        ExplorerRoots.Add(FileService.Instance.BuildTree(SelectedProject.Path));
        RefreshRunsData(SelectedProject.Path);
        ConsoleLogs.Add(new LogEntry { Level = LogLevel.Info, Source = "IDE", Message = "Explorer обновлён." });
    }

    /// <summary>Scans the project's runs/ directory and fills the real side-panel lists.</summary>
    public void RefreshRunsData(string projectPath)
    {
        var runs = Path.Combine(projectPath, "runs");

        FillMods(Path.Combine(runs, "mods"));
        FillDirs(ResourcePacks, Path.Combine(runs, "resourcepacks"));
        FillDirs(Worlds, Path.Combine(runs, "saves"));
        FillFiles(LogFiles, Path.Combine(runs, "logs"), "*.log*");

        DataPacks.Clear();
        try
        {
            var saves = Path.Combine(runs, "saves");
            if (Directory.Exists(saves))
            {
                foreach (var world in Directory.GetDirectories(saves))
                {
                    var dp = Path.Combine(world, "datapacks");
                    if (!Directory.Exists(dp)) continue;
                    foreach (var name in Directory.GetDirectories(dp)
                                 .Select(Path.GetFileName)
                                 .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!))
                    {
                        DataPacks.Add(name + "  (" + Path.GetFileName(world) + ")");
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>Scans runs/mods and fills the mod list; .jar.disabled files are shown as turned off.</summary>
    private void FillMods(string modsDir)
    {
        Mods.Clear();
        try
        {
            if (!Directory.Exists(modsDir)) return;
            foreach (var file in Directory.GetFiles(modsDir, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    var display = name.Substring(0, name.Length - ".disabled".Length);
                    Mods.Add(new ModItem { FileName = display, FullPath = file, IsEnabled = false });
                }
                else if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    Mods.Add(new ModItem { FileName = name, FullPath = file, IsEnabled = true });
                }
            }
        }
        catch { }
    }

    /// <summary>Toggles a mod on/off by renaming its file between .jar and .jar.disabled.</summary>
    public void ToggleMod(ModItem mod)
    {
        if (mod == null || string.IsNullOrEmpty(mod.FullPath) || !File.Exists(mod.FullPath)) return;

        try
        {
            if (mod.IsEnabled)
            {
                var target = mod.FullPath + ".disabled";
                File.Move(mod.FullPath, target);
                mod.FullPath = target;
                mod.IsEnabled = false;
            }
            else
            {
                var target = mod.FullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? mod.FullPath.Substring(0, mod.FullPath.Length - ".disabled".Length)
                    : mod.FullPath;
                File.Move(mod.FullPath, target);
                mod.FullPath = target;
                mod.IsEnabled = true;
            }

            ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Success,
                Source = "IDE",
                Message = $"Мод «{mod.FileName}» {(mod.IsEnabled ? "включён" : "выключен")}."
            });
        }
        catch (Exception ex)
        {
            ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Error,
                Source = "IDE",
                Message = "Не удалось переключить мод: " + ex.Message
            });
        }
    }

    /// <summary>Deletes a mod's .jar (and its .disabled twin, if any) from runs/mods.</summary>
    public void DeleteMod(ModItem mod)
    {
        if (mod == null || string.IsNullOrEmpty(mod.FullPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(mod.FullPath);
            if (string.IsNullOrEmpty(dir)) return;

            var jar = Path.Combine(dir, mod.FileName);
            var disabled = jar + ".disabled";

            var deleted = false;
            if (File.Exists(jar)) { File.Delete(jar); deleted = true; }
            if (File.Exists(disabled)) { File.Delete(disabled); deleted = true; }

            Mods.Remove(mod);
            if (deleted)
                ConsoleLogs.Add(new LogEntry { Level = LogLevel.Success, Source = "IDE", Message = $"Мод «{mod.FileName}» удалён." });
        }
        catch (Exception ex)
        {
            ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Error,
                Source = "IDE",
                Message = "Не удалось удалить мод: " + ex.Message
            });
        }
    }

    private static void FillFiles(ObservableCollection<string> target, string dir, string pattern)
    {
        target.Clear();
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var name in Directory.GetFiles(dir, pattern)
                         .Select(Path.GetFileName)
                         .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(name);
            }
        }
        catch { }
    }

    private static void FillDirs(ObservableCollection<string> target, string dir)
    {
        target.Clear();
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var name in Directory.GetDirectories(dir)
                         .Select(Path.GetFileName)
                         .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(name);
            }
        }
        catch { }
    }

    private void OpenDefaultFiles(string root)
    {
        Tabs.Clear();

        // Open build.gradle
        var gradle = Path.Combine(root, "build.gradle");
        if (File.Exists(gradle))
        {
            Tabs.Add(EditorTab.New("build.gradle", gradle, FileService.Instance.ReadFile(gradle), "groovy", "\uE8A5"));
        }
        var modsToml = Path.Combine(root, "src", "main", "resources", "META-INF", "mods.toml");
        if (File.Exists(modsToml))
        {
            Tabs.Add(EditorTab.New("mods.toml", modsToml, FileService.Instance.ReadFile(modsToml), "ini", "\uE8A5"));
        }
        // Find first .java file
        var javaDir = Path.Combine(root, "src", "main", "java");
        if (Directory.Exists(javaDir))
        {
            var firstJava = Directory.GetFiles(javaDir, "*.java", SearchOption.AllDirectories).FirstOrDefault();
            if (firstJava != null)
            {
                Tabs.Add(EditorTab.New(Path.GetFileName(firstJava), firstJava, FileService.Instance.ReadFile(firstJava), "java", "\uE9E9"));
            }
        }

        ActiveTab = Tabs.FirstOrDefault();
    }

    public void OpenFile(FileNode node)
    {
        if (node.IsDirectory) return;
        OpenFileByPath(node.FullPath, node.Language, node.Icon);
    }

    public void OpenFileByPath(string path, string language, string icon)
    {
        if (!File.Exists(path)) return;
        var existing = Tabs.FirstOrDefault(t => t.FullPath == path);
        if (existing != null) { ActiveTab = existing; return; }
        var tab = EditorTab.New(
            Path.GetFileName(path),
            path,
            FileService.Instance.ReadFile(path),
            language,
            icon
        );
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    public void CloseTab(EditorTab tab)
    {
        Tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = Tabs.LastOrDefault();
    }

    public async Task BuildAndLaunchAsync(bool launchAfter = true)
    {
        var project = SelectedProject;
        if (project == null) return;

        // Build — real Gradle build when available, demo otherwise.
        // A failed build does NOT block launch: we still open the installed Forge
        // with the last available JAR so Minecraft always starts when Run is pressed.
        await BuildService.Instance.BuildAsync(project.Name, project.Path);
        if (BuildStatus.Phase == BuildPhase.Failed)
        {
            BuildLogs.Add(new LogEntry
            {
                Level = LogLevel.Warning,
                Source = "Build",
                Message = "Сборка не удалась — запускаю установленный Forge с последним доступным JAR (если есть). Ошибки смотрите во вкладке Problems."
            });
        }

        // Auto copy the newest .jar from build/libs into runs/mods
        string? sourceJar = null;
        var libsDir = Path.Combine(project.Path, "build", "libs");
        if (Directory.Exists(libsDir))
        {
            var jars = Directory.GetFiles(libsDir, "*.jar");
            if (jars.Length > 0)
            {
                sourceJar = jars.OrderByDescending(File.GetLastWriteTime).First();
                try
                {
                    var modsDir = Path.Combine(project.Path, "runs", "mods");
                    Directory.CreateDirectory(modsDir);
                    var targetJar = Path.Combine(modsDir, Path.GetFileName(sourceJar));
                    // If the mod was turned off in the Mods panel, keep it off — otherwise
                    // the fresh copy would sit next to the old .jar.disabled and duplicate it
                    // (and load anyway on the next launch).
                    if (File.Exists(targetJar + ".disabled"))
                    {
                        BuildLogs.Add(new LogEntry { Level = LogLevel.Info, Source = "Build", Message = $"Мод {Path.GetFileName(sourceJar)} выключен — новый JAR не скопирован." });
                    }
                    else
                    {
                        File.Copy(sourceJar, targetJar, true);
                        BuildLogs.Add(new LogEntry { Level = LogLevel.Success, Source = "Build", Message = $"JAR скопирован в {targetJar}" });
                    }
                }
                catch (Exception ex)
                {
                    BuildLogs.Add(new LogEntry { Level = LogLevel.Error, Source = "Build", Message = "Copy failed: " + ex.Message });
                    return;
                }
            }
            else
            {
                BuildLogs.Add(new LogEntry { Level = LogLevel.Warning, Source = "Build", Message = "JAR не найден в build/libs — запуск без копирования." });
            }
        }

        if (!launchAfter) return;

        // Launch — installed Forge client / gradlew runClient/runServer/runData / java -jar / simulation
        MinecraftLogs.Clear();
        _realCrashReported = false;
        var task = SelectedProfile?.GradleTask ?? "runClient";
        await MinecraftLauncherService.Instance.LaunchAsync(sourceJar ?? "", project.Path, task, project.MinecraftVersion);
        IsLauncherRunning = MinecraftLauncherService.Instance.IsRunning;
        RefreshRunsData(project.Path);
    }

    public void SaveActiveFile()
    {
        var tab = ActiveTab;
        if (tab == null || string.IsNullOrEmpty(tab.FullPath)) return;
        FileService.Instance.WriteFile(tab.FullPath, tab.Content);
        tab.MarkSaved();
        ConsoleLogs.Add(new LogEntry
        {
            Level = LogLevel.Success,
            Source = "IDE",
            Message = $"Сохранено: {tab.Title}"
        });
    }

    public void StopLauncher()
    {
        BuildService.Instance.Cancel();
        MinecraftLauncherService.Instance.Stop();
        IsLauncherRunning = MinecraftLauncherService.Instance.IsRunning;
    }

    private void AddLog(object? sender, LogEntry e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            BuildLogs.Add(e);
            if (e.Source == "Gradle" || e.Source == "Java")
                GradleLogs.Add(e);
            if (e.Level == LogLevel.Error || e.Level == LogLevel.Warning)
            {
                Problems.Add(e);
                OnPropertyChanged(nameof(ProblemCount));
                OnPropertyChanged(nameof(ErrorCount));
                OnPropertyChanged(nameof(WarningCount));
            }
        });
    }

    public void ShowReport(CrashReport report)
    {
        // Add a synthetic error to Problems so the user sees the count update,
        // but avoid duplicates when the same crash is reported multiple times.
        var msg = report.Title + $" (exit code {report.ExitCode})";
        var last = Problems.LastOrDefault();
        if (last == null || last.Source != "Crash" || last.Message != msg)
        {
            Problems.Add(new LogEntry
            {
                Level = LogLevel.Error,
                Source = "Crash",
                Message = msg
            });
            OnPropertyChanged(nameof(ProblemCount));
            OnPropertyChanged(nameof(ErrorCount));
        }
    }

    public void PrepareForExit()
    {
        if (MinecraftLauncherService.Instance.IsRunning)
        {
            MinecraftLauncherService.Instance.Stop();
        }
    }
}
