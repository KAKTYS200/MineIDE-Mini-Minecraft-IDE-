using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using MineIDE.Controls;
using MineIDE.Models;
using MineIDE.Services;
using MineIDE.ViewModels;

namespace MineIDE;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _terminalVisible = true;
    private double _userRightDockWidth; // ширина правого дока, заданная перетаскиванием сплиттера
    private SharpCodeEditor? _hookedEditor;
    private EditorTab? _hookedTab;

    public MainWindow()
    {
        InitializeComponent();
        _vm = (MainViewModel)DataContext;

        RestoreSettings();

        _vm.InitSampleData();
        if (_lastSettings?.LastProjectName is string lastName)
        {
            var match = _vm.Projects.FirstOrDefault(p => p.Name == lastName);
            if (match != null) _vm.SelectedProject = match;
        }
        // Prefer a real Forge project (e.g. one discovered on the Desktop) over the demo examplemod,
        // so Run tests the user's actual mod instead of launching Forge without a JAR.
        if (_vm.SelectedProject != null &&
            _vm.SelectedProject.Path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            var real = _vm.Projects.FirstOrDefault(p => !p.Path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
            if (real != null) _vm.SelectedProject = real;
        }
        _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Success, Source = "IDE", Message = "Mine IDE запущен. Готов к работе." });
        _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Info, Source = "Gradle", Message = "Загружены SDK: MC 1.20.1, Forge 47.2.0." });

        // Tab close buttons (bubbled Button.Click from the EditorTabItem template)
        EditorTabs.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnTabHeaderButtonClick));

        PreviewKeyDown += OnPreviewKeyDown;
        UpdateBuildStatusBar();
        UpdateThemeLabel();
        UpdateLineCount();

        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedProject)) UpdateBuildStatusBar();
            if (e.PropertyName == nameof(MainViewModel.BuildStatus)) UpdateBuildStatusBar();
            if (e.PropertyName == nameof(MainViewModel.IsLauncherRunning)) UpdateLaunchStatus();
            if (e.PropertyName == nameof(MainViewModel.ActiveTab))
            {
                UpdateLineCount();
                UpdateRightDock();
                // The new editor is realized after layout — hook it then.
                Dispatcher.BeginInvoke(new Action(HookActiveEditor), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            if (e.PropertyName == nameof(MainViewModel.CurrentTheme)) UpdateThemeLabel();
            if (e.PropertyName == nameof(MainViewModel.ErrorCount)) RefreshBadges();
            if (e.PropertyName == nameof(MainViewModel.WarningCount)) RefreshBadges();
        };

        Dispatcher.BeginInvoke(new Action(HookActiveEditor), System.Windows.Threading.DispatcherPriority.Loaded);
        Closed += OnClosed;

        // Keep the window on screen when it is dragged (or a monitor disappears).
        LocationChanged += (_, _) => ClampWindowToScreen();

        // Keep the embedded Blockbench (model editor) theme in sync with the app theme.
        ThemeService.Instance.ThemeChanged += (_, _) => ApplyThemeToModelEditor();

        // Own data folder in %APPDATA%\MineIDE (mods / textures / models / projects) —
        // created on first launch by AppDataPaths.
        _vm.ConsoleLogs.Add(new LogEntry
        {
            Level = LogLevel.Info,
            Source = "IDE",
            Message = "Папка данных приложения: " + AppDataPaths.Root
        });
        RegisterMmcpFileAssociation();

        // A .mmcp project file passed on the command line (or opened by double-click)
        // is loaded automatically once the window is ready.
        var mmcpArg = Environment.GetCommandLineArgs().FirstOrDefault(a =>
            a.EndsWith(MmcpService.Extension, StringComparison.OrdinalIgnoreCase));
        if (mmcpArg != null)
        {
            var file = mmcpArg;
            Dispatcher.BeginInvoke(new Action(() => OpenMmcpFile(file)), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ---------- persistence ----------

    private void RestoreSettings()
    {
        var settings = SettingsService.Load();

        if (settings.Theme == AppTheme.Light.ToString())
            ThemeService.Instance.Apply(AppTheme.Light);

        if (settings.HasWindowBounds && settings.Width > 200 && settings.Height > 200)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.Left;
            Top = settings.Top;
            Width = settings.Width;
            Height = settings.Height;
            if (settings.Maximized) WindowState = WindowState.Maximized;
        }
        ClampWindowToScreen();

        _lastSettings = settings;
    }

    // Minimum part of the window that must stay visible (the title bar), so it
    // can never be dragged fully off the screen.
    private const double MinVisibleWindow = 56;

    private void ClampWindowToScreen()
    {
        if (WindowState == WindowState.Maximized) return;

        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        // Horizontal: keep at least MinVisibleWindow of the window on screen.
        double minLeft = vLeft + MinVisibleWindow - Width;
        double maxLeft = vRight - MinVisibleWindow;
        if (minLeft > maxLeft) // окно шире экрана — целиком в пределах экрана
        {
            minLeft = vLeft;
            maxLeft = vRight - Width;
            if (minLeft > maxLeft) { minLeft = (vLeft + vRight - Width) / 2; maxLeft = minLeft; }
        }
        Left = Math.Clamp(Left, minLeft, maxLeft);

        // Vertical: same, with the caption always reachable.
        double minTop = vTop + MinVisibleWindow - Height;
        double maxTop = vBottom - MinVisibleWindow;
        if (minTop > maxTop) // окно выше экрана
        {
            minTop = vTop;
            maxTop = vBottom - Height;
            if (minTop > maxTop) { minTop = (vTop + vBottom - Height) / 2; maxTop = minTop; }
        }
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private AppSettings? _lastSettings;

    private void SaveSettings()
    {
        _lastSettings = new AppSettings
        {
            Theme = ThemeService.Instance.Current.ToString(),
            HasWindowBounds = true,
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            Maximized = WindowState == WindowState.Maximized,
            LastProjectName = _vm.SelectedProject?.Name
        };
        SettingsService.Save(_lastSettings);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RecipeEditorHost.Shutdown();
        AdvancementEditorHost.Shutdown();
        if (ModelEditorWeb.CoreWebView2 != null)
            ModelEditorWeb.Dispose();
        _vm.PrepareForExit();
        SaveSettings();
    }

    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку Forge-проекта (с build.gradle)",
            Multiselect = false
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.AddProject(dlg.FolderName);
            _vm.ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Success,
                Source = "IDE",
                Message = $"Проект открыт: {dlg.FolderName}"
            });
        }
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e)
    {
        ThemeService.Instance.Toggle();
        _vm.OnPropertyChanged(nameof(MainViewModel.CurrentTheme));
        SaveSettings();
    }

    private void OnOpenRecipeEditorClick(object sender, RoutedEventArgs e)
    {
        if (RD_Recipes.IsChecked == true)
            OnRightPanelClick(RD_Properties, new RoutedEventArgs());
        else
            OnRightPanelClick(RD_Recipes, new RoutedEventArgs());
    }

    private void OnOpenQuickAccessClick(object sender, RoutedEventArgs e)
    {
        if (RD_QuickAccess.IsChecked == true)
            OnRightPanelClick(RD_Properties, new RoutedEventArgs());
        else
            OnRightPanelClick(RD_QuickAccess, new RoutedEventArgs());
    }

    private void OnOpenModelEditorClick(object sender, RoutedEventArgs e)
    {
        if (RD_ModelEditor.IsChecked == true)
            OnRightPanelClick(RD_Properties, new RoutedEventArgs());
        else
            OnRightPanelClick(RD_ModelEditor, new RoutedEventArgs());
    }

    // ---------- advancements (visual builder) ----------

    private void OnOpenAdvancementEditorClick(object sender, RoutedEventArgs e)
    {
        if (RD_Advancements.IsChecked == true)
            OnRightPanelClick(RD_Properties, new RoutedEventArgs());
        else
            OnRightPanelClick(RD_Advancements, new RoutedEventArgs());
    }

    private void OnAdvancementListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is string fileName)
        {
            var path = FindAdvancementFile(fileName);
            if (path != null)
            {
                OnRightPanelClick(RD_Advancements, new RoutedEventArgs());
                try { AdvancementEditorHost.LoadFrom(path); } catch { }
            }
        }
    }

    /// <summary>Scans the current project for saved advancement JSON files and lists them.</summary>
    private void RefreshAdvancementsList()
    {
        AdvancementsList.ItemsSource = null;
        var names = new List<string>();
        var project = _vm.SelectedProject;
        if (project != null)
        {
            var dataDir = Path.Combine(project.Path, "src", "main", "resources", "data");
            try
            {
                if (Directory.Exists(dataDir))
                {
                    foreach (var adv in Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories)
                                 .Where(f => f.Contains(Path.DirectorySeparatorChar + "advancements" + Path.DirectorySeparatorChar)
                                          || f.Contains("\\advancements\\"))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(Path.GetFileName(adv));
                    }
                }
            }
            catch { /* best-effort */ }
        }
        AdvancementsList.ItemsSource = names;
    }

    private string? FindAdvancementFile(string fileName)
    {
        var project = _vm.SelectedProject;
        if (project == null) return null;
        var dataDir = Path.Combine(project.Path, "src", "main", "resources", "data");
        try
        {
            if (!Directory.Exists(dataDir)) return null;
            return Directory.GetFiles(dataDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Pushes the current project folder + mod id into the advancement editor for saves.</summary>
    private void SyncAdvancementProject()
    {
        var project = _vm.SelectedProject;
        if (project == null) return;
        AdvancementEditorHost.ProjectPath = project.Path;
        AdvancementEditorHost.NamespaceName = string.IsNullOrWhiteSpace(project.ModId)
            ? "mine_ide"
            : project.ModId;
    }

    // ---------- menu bar (File / View / Import) ----------

    private void OnMenuNewFileClick(object sender, RoutedEventArgs e)
    {
        var tab = EditorTab.New("Untitled.txt", "", "", "plaintext");
        _vm.Tabs.Add(tab);
        _vm.ActiveTab = tab;
        _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Info, Source = "IDE", Message = "Создан новый файл Untitled.txt" });
    }

    private void OnMenuSaveClick(object sender, RoutedEventArgs e) => SaveActive();

    private void OnMenuExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnMenuExplorerClick(object sender, RoutedEventArgs e) => ActivateActivity(AB_Explorer);

    private void OnImportItemClick(object sender, RoutedEventArgs e)
    {
        OnOpenQuickAccessClick(sender, e);
        // The panel becomes visible after layout — focus the search box then.
        Dispatcher.BeginInvoke(new Action(() => QAItemSearch.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnImportModelClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Модель JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            Title = "Импортировать модель"
        };
        if (dlg.ShowDialog(this) == true)
            _vm.OpenFileByPath(dlg.FileName, "json", "\uE8B7");
    }

    // ---------- .mmcp project files ----------

    /// <summary>Registers .mmcp files to open with this app (current user, no admin needed).</summary>
    private static void RegisterMmcpFileAssociation()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.mmcp"))
                key.SetValue("", "MineIDE.MMCP");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\MineIDE.MMCP\DefaultIcon"))
                key.SetValue("", $"\"{exe}\",0");
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\MineIDE.MMCP\shell\open\command"))
                key.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { /* registry unavailable — non-critical */ }
    }

    private void OpenMmcpFile(string file)
    {
        try
        {
            var proj = MmcpService.Read(file);
            if (proj == null)
            {
                _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Error, Source = "IDE", Message = "Не удалось прочитать файл проекта: " + file });
                return;
            }
            if (!string.IsNullOrEmpty(proj.Path) && Directory.Exists(proj.Path))
            {
                _vm.AddProject(proj.Path);
                _vm.ConsoleLogs.Add(new LogEntry
                {
                    Level = LogLevel.Success,
                    Source = "IDE",
                    Message = $"Проект «{proj.Name}» открыт из {Path.GetFileName(file)}"
                });
            }
            else
            {
                _vm.ConsoleLogs.Add(new LogEntry
                {
                    Level = LogLevel.Warning,
                    Source = "IDE",
                    Message = $"Папка проекта из «{Path.GetFileName(file)}» не найдена: {proj.Path}"
                });
                MessageBox.Show(this, "Папка проекта не найдена:\n" + proj.Path + "\n\nФайл проекта: " + file,
                    "Mine IDE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Error, Source = "IDE", Message = "Ошибка открытия .mmcp: " + ex.Message });
        }
    }

    private void OnMenuOpenMmcpClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Открыть проект (.mmcp)",
            Filter = "Проект Mine IDE (*.mmcp)|*.mmcp",
            InitialDirectory = AppDataPaths.Projects
        };
        if (dlg.ShowDialog(this) == true) OpenMmcpFile(dlg.FileName);
    }

    private void OnMenuSaveMmcpClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProject == null)
        {
            MessageBox.Show(this, "Нет выбранного проекта.", "Mine IDE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить проект как .mmcp",
            Filter = "Проект Mine IDE (*.mmcp)|*.mmcp",
            FileName = _vm.SelectedProject.Name + MmcpService.Extension,
            InitialDirectory = AppDataPaths.Projects
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                MmcpService.Write(_vm.SelectedProject, dlg.FileName);
                _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Success, Source = "IDE", Message = "Проект сохранён: " + dlg.FileName });
            }
            catch (Exception ex)
            {
                _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Error, Source = "IDE", Message = "Не удалось сохранить .mmcp: " + ex.Message });
            }
        }
    }

    // ---------- mod folder (idea 2) ----------

    private void OnChooseModFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку с модом",
            InitialDirectory = AppDataPaths.Mods
        };
        if (dlg.ShowDialog(this) == true)
        {
            _vm.OpenModFolder(dlg.FolderName);
            _vm.ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Success,
                Source = "IDE",
                Message = $"Папка мода выбрана: {dlg.FolderName}"
            });
        }
    }

    // ---------- quick access (idea 3: items / images / models) ----------

    private sealed class QuickAccessFile
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
    }

    private void PopulateQuickAccess()
    {
        // Items — from the local Minecraft client jar (resolved once).
        if (QAItems.ItemsSource == null)
        {
            var ids = MineIDE.RecipeEditor.Services.ItemIconService.Instance.GetAllItemIds();
            QAItems.ItemsSource = ids;
            QAItemHint.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Images and models — rescanned from the selected project each time.
        var projPath = _vm.SelectedProject?.Path;
        var images = ScanProjectFiles(projPath, new[] { ".png", ".jpg", ".jpeg", ".gif" });
        QAImages.ItemsSource = images;
        QAImageHint.Visibility = images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var models = ScanProjectFiles(projPath, new[] { ".json" }, modelOnly: true);
        QAModels.ItemsSource = models;
        QAModelHint.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<QuickAccessFile> ScanProjectFiles(string? root, string[] extensions, bool modelOnly = false)
    {
        var result = new List<QuickAccessFile>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!extensions.Contains(ext)) continue;
                if (modelOnly && !file.Contains("\\models\\", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new QuickAccessFile { Name = Path.GetFileName(file), FullPath = file });
                if (result.Count >= 400) break;
            }
        }
        catch { /* best effort */ }
        return result;
    }

    private void OnQAItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (QAItems.ContainerFromElement(src) is not ListBoxItem item || item.DataContext is not string id) return;
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            Clipboard.SetText(id);
            _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Info, Source = "IDE", Message = $"id скопирован: {id}" });
        }
        catch { /* clipboard busy */ }
    }

    private void OnQAItemSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var query = QAItemSearch.Text?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(query)) return;
        foreach (var id in (System.Collections.IEnumerable)QAItems.Items)
        {
            if (id is string s && s.ToLowerInvariant().Contains(query))
            {
                QAItems.SelectedItem = s;
                QAItems.ScrollIntoView(s);
                break;
            }
        }
    }

    private void OnQAImageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is QuickAccessFile f)
            _vm.OpenFileByPath(f.FullPath, "image", "\uE8B9");
    }

    private void OnQAModelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is QuickAccessFile f)
            _vm.OpenFileByPath(f.FullPath, "json", "\uE8B7");
    }

    // ---------- top bar actions ----------

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Settings:\n\nTheme: switch in title bar\nKeyboard:\n Ctrl+Shift+B  Build\n F5            Run\n Shift+F5      Stop\n Ctrl+S        Save file\n Ctrl+`        Toggle terminal\n Ctrl+Shift+E  Explorer\n\nProject state, window size and theme are saved to settings.json.",
            "Mine IDE", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnBuildClick(object sender, RoutedEventArgs e)
    {
        ActivateTerminalTab(TP_Build);
        await _vm.BuildAndLaunchAsync(launchAfter: false);
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        ActivateTerminalTab(TP_Build);
        await _vm.BuildAndLaunchAsync(launchAfter: true);
        ActivateTerminalTab(TP_Logs);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _vm.StopLauncher();
    }

    private void SaveActive()
    {
        if (_vm.ActiveTab == null) return;
        _vm.SaveActiveFile();
        UpdateLineCount();
    }

    // ---------- keyboard shortcuts ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.S)
        {
            SaveActive();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.B)
        {
            OnBuildClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Oem3) // Ctrl+`
        {
            OnToggleTerminal(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.E)
        {
            ActivateActivity(AB_Explorer);
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.F5 && !shift)
        {
            OnRunClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.F5 && shift)
        {
            OnStopClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ---------- activity bar ----------

    private void OnActivityClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Name?.StartsWith("AB_") == true)
            ActivateActivity(btn);
    }

    private void ActivateActivity(ToggleButton btn)
    {
        // Uncheck only the activity-bar buttons — never terminal tabs or dock tabs.
        foreach (var tb in FindVisualChildren<ToggleButton>(this))
        {
            if (tb.Name?.StartsWith("AB_") == true && !ReferenceEquals(tb, btn))
                tb.IsChecked = false;
        }
        btn.IsChecked = true;
        if (btn.Tag is string label)
            ShowPanelForActivity(label);
    }

    private void ShowPanelForActivity(string name)
    {
        PN_Explorer.Visibility = Visibility.Collapsed;
        PN_Mods.Visibility = Visibility.Collapsed;
        PN_Data.Visibility = Visibility.Collapsed;
        PN_Advancements.Visibility = Visibility.Collapsed;
        PN_Performance.Visibility = Visibility.Collapsed;
        PN_Extensions.Visibility = Visibility.Collapsed;

        Grid? target = name switch
        {
            "Explorer (Ctrl+Shift+E)" => PN_Explorer,
            "Mods" => PN_Mods,
            "Data Packs" => PN_Data,
            "Advancements" => PN_Advancements,
            "Performance" => PN_Performance,
            "Extensions" => PN_Extensions,
            _ => PN_Explorer
        };
        if (target != null)
        {
            target.Visibility = Visibility.Visible;
            FadeIn(target);
        }
        if (name == "Advancements")
            RefreshAdvancementsList();
        _vm.ActivePanel = name?.Split(' ')[0] ?? "Explorer";
    }

    // ---------- right dock ----------

    private void OnRightPanelClick(object sender, RoutedEventArgs e)
    {
        RD_Recipes.IsChecked = false;
        RD_Advancements.IsChecked = false;
        RD_Properties.IsChecked = false;
        RD_Model.IsChecked = false;
        RD_Texture.IsChecked = false;
        RD_JSON.IsChecked = false;
        RD_ModelEditor.IsChecked = false;
        RD_QuickAccess.IsChecked = false;
        if (sender is ToggleButton tb) tb.IsChecked = true;

        RD_PN_Recipes.Visibility = Visibility.Collapsed;
        RD_PN_Advancements.Visibility = Visibility.Collapsed;
        RD_PN_Properties.Visibility = Visibility.Collapsed;
        RD_PN_Model.Visibility = Visibility.Collapsed;
        RD_PN_Texture.Visibility = Visibility.Collapsed;
        RD_PN_JSON.Visibility = Visibility.Collapsed;
        RD_PN_ModelEditor.Visibility = Visibility.Collapsed;
        RD_PN_QuickAccess.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(sender, RD_Recipes)) RD_PN_Recipes.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, RD_Advancements))
        {
            RD_PN_Advancements.Visibility = Visibility.Visible;
            SyncAdvancementProject();
        }
        else if (ReferenceEquals(sender, RD_Properties)) RD_PN_Properties.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, RD_Model)) RD_PN_Model.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, RD_Texture)) RD_PN_Texture.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, RD_JSON)) RD_PN_JSON.Visibility = Visibility.Visible;
        else if (ReferenceEquals(sender, RD_ModelEditor))
        {
            RD_PN_ModelEditor.Visibility = Visibility.Visible;
            _ = EnsureModelEditorAsync();
        }
        else if (ReferenceEquals(sender, RD_QuickAccess))
        {
            RD_PN_QuickAccess.Visibility = Visibility.Visible;
            PopulateQuickAccess();
        }

        // The wide editors expand the dock while shown; a width the user set by
        // dragging the splitter is kept (unless the editor needs even more room).
        double required = ReferenceEquals(sender, RD_Recipes) ? 700
            : ReferenceEquals(sender, RD_Advancements) ? 880
            : ReferenceEquals(sender, RD_ModelEditor) ? 1080
            : 320;
        RightDockColumn.Width = new GridLength(Math.Max(required, _userRightDockWidth));
    }

    // Remember the dock width the user picked by dragging its edge.
    private void OnRightDockSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _userRightDockWidth = RightDockColumn.ActualWidth;
    }

    // ---------- Blockbench model editor (WebView2) ----------

    private async Task EnsureModelEditorAsync()
    {
        if (ModelEditorWeb.CoreWebView2 != null) return;
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Blockbench");
            if (!Directory.Exists(folder))
            {
                ShowModelEditorError("Папка Blockbench не найдена: " + folder);
                return;
            }

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineIDE", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await ModelEditorWeb.EnsureCoreWebView2Async(env);

            var core = ModelEditorWeb.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 не инициализирован");
            core.SetVirtualHostNameToFolderMapping(
                "blockbench.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            ModelEditorWeb.NavigationCompleted += (_, _) =>
            {
                ModelEditorLoading.Visibility = Visibility.Collapsed;
                ApplyThemeToModelEditorWhenReady();
            };

            // Auto-open a Java block model so Blockbench lands directly in the editor
            // (skips the start screen — "only the editor").
            // The model must have its own elements: a model with only a parent and no
            // elements triggers Blockbench's "Empty Child Model" message box.
            const string modelJson = """
                {
                  "textures": { "particle": "minecraft:block/stone", "all": "minecraft:block/stone" },
                  "elements": [
                    {
                      "from": [0, 0, 0],
                      "to": [16, 16, 16],
                      "faces": {
                        "down":  { "uv": [0, 0, 16, 16], "texture": "#all" },
                        "up":    { "uv": [0, 0, 16, 16], "texture": "#all" },
                        "north": { "uv": [0, 0, 16, 16], "texture": "#all" },
                        "south": { "uv": [0, 0, 16, 16], "texture": "#all" },
                        "west":  { "uv": [0, 0, 16, 16], "texture": "#all" },
                        "east":  { "uv": [0, 0, 16, 16], "texture": "#all" }
                      }
                    }
                  ]
                }
                """;
            // Blockbench splits URL queries with /=\s*(.+)/, where '.' does not match
            // newlines — a multi-line loaddata value would be truncated. Minify first.
            var compactJson = string.Concat(modelJson.Where(c => !char.IsWhiteSpace(c)));
            var url = "https://blockbench.local/index.html?loadtype=json&loadname=model.json&loaddata="
                + Uri.EscapeDataString(compactJson);
            ModelEditorWeb.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            ShowModelEditorError("Не удалось запустить WebView2 (нужен Microsoft Edge WebView2 Runtime).\n\n" + ex.Message);
        }
    }

    private void ShowModelEditorError(string message)
    {
        ModelEditorError.Text = message;
        ModelEditorError.Visibility = Visibility.Visible;
    }

    // Switch the embedded Blockbench theme to match the app theme.
    private void ApplyThemeToModelEditor()
    {
        if (ModelEditorWeb.CoreWebView2 == null) return;
        var id = ThemeService.Instance.Current == AppTheme.Light ? "default_light" : "default";
        var js = "window.BlockbenchSetTheme && window.BlockbenchSetTheme('" + id + "');";
        try
        {
            _ = ModelEditorWeb.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch { /* WebView2 not ready */ }
    }

    // The theme hook only exists after Blockbench finished booting; retry briefly.
    private async void ApplyThemeToModelEditorWhenReady()
    {
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(300);
            if (ModelEditorWeb.CoreWebView2 == null) return;
            try
            {
                var ok = await ModelEditorWeb.CoreWebView2.ExecuteScriptAsync(
                    "typeof window.BlockbenchSetTheme === 'function'");
                if (ok.Trim('\"').Contains("true"))
                {
                    ApplyThemeToModelEditor();
                    return;
                }
            }
            catch { return; }
        }
    }

    private void UpdateRightDock()
    {
        var tab = _vm.ActiveTab;

        // Texture preview: show the actual opened image
        bool isImage = tab?.Language == "image";
        RD_TextureImage.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        RD_TextureHint.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        RD_TextureCaption.Text = "—";
        RD_TextureSize.Text = "—";
        RD_TextureFormat.Text = "—";
        RD_TextureFile.Text = "—";

        if (isImage && tab != null && File.Exists(tab.FullPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(tab.FullPath);
                bmp.EndInit();
                RD_TextureImage.Source = bmp;
                RD_TextureCaption.Text = Path.GetFileName(tab.FullPath);
                RD_TextureSize.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight} px";
                RD_TextureFormat.Text = "PNG";
                RD_TextureFile.Text = tab.FullPath;
            }
            catch { /* unreadable image */ }
        }

        // JSON viewer: pretty-print the opened .json
        bool isJson = tab?.Language == "json";
        RD_JSONCaption.Text = isJson
            ? $"{tab!.Title} — отформатировано"
            : "Откройте .json файл — он будет отформатирован здесь";
        if (isJson)
        {
            try
            {
                var node = JsonNode.Parse(tab!.Content);
                RD_JSONText.Text = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? tab.Content;
            }
            catch
            {
                RD_JSONText.Text = tab!.Content;
            }
        }
        else
        {
            RD_JSONText.Text = "";
        }
    }

    // ---------- terminal panel ----------

    private void OnTerminalTabClick(object sender, RoutedEventArgs e)
    {
        TP_ConsoleView.Visibility = Visibility.Collapsed;
        TP_BuildView.Visibility = Visibility.Collapsed;
        TP_ProblemsView.Visibility = Visibility.Collapsed;
        TP_LogsView.Visibility = Visibility.Collapsed;
        TP_GradleView.Visibility = Visibility.Collapsed;
        TP_CrashView.Visibility = Visibility.Collapsed;

        TP_Console.IsChecked = false;
        TP_Build.IsChecked = false;
        TP_Problems.IsChecked = false;
        TP_Logs.IsChecked = false;
        TP_Gradle.IsChecked = false;
        TP_Crash.IsChecked = false;

        if (ReferenceEquals(sender, TP_Console)) { TP_ConsoleView.Visibility = Visibility.Visible; TP_Console.IsChecked = true; }
        else if (ReferenceEquals(sender, TP_Build)) { TP_BuildView.Visibility = Visibility.Visible; TP_Build.IsChecked = true; }
        else if (ReferenceEquals(sender, TP_Problems)) { TP_ProblemsView.Visibility = Visibility.Visible; TP_Problems.IsChecked = true; }
        else if (ReferenceEquals(sender, TP_Logs)) { TP_LogsView.Visibility = Visibility.Visible; TP_Logs.IsChecked = true; }
        else if (ReferenceEquals(sender, TP_Gradle)) { TP_GradleView.Visibility = Visibility.Visible; TP_Gradle.IsChecked = true; }
        else if (ReferenceEquals(sender, TP_Crash)) { TP_CrashView.Visibility = Visibility.Visible; TP_Crash.IsChecked = true; }
    }

    private void ActivateTerminalTab(ToggleButton target)
    {
        OnTerminalTabClick(target, new RoutedEventArgs());
    }

    private void OnToggleTerminal(object sender, RoutedEventArgs e)
    {
        _terminalVisible = TerminalRow.ActualHeight <= 1; // скрыт → показать, иначе → скрыть
        var anim = new GridLengthAnimation
        {
            From = new GridLength(Math.Max(TerminalRow.ActualHeight, 0)),
            To = new GridLength(_terminalVisible ? 220 : 0),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        TerminalRow.BeginAnimation(RowDefinition.HeightProperty, anim);
        TerminalToggleIcon.Text = _terminalVisible ? "\uE70D" : "\uE710"; // chevron down / up
    }

    private void OnClearTerminal(object sender, RoutedEventArgs e)
    {
        _vm.ConsoleLogs.Clear();
        _vm.GradleLogs.Clear();
    }

    // ---------- explorer ----------

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem tvi && tvi.DataContext is FileNode node)
        {
            _vm.OpenFile(node);
            // If it's an image, jump straight to the texture preview.
            if (node.Language == "image")
            {
                RD_Texture.IsChecked = true;
                OnRightPanelClick(RD_Texture, new RoutedEventArgs());
            }
        }
    }

    private void OnRefreshExplorerClick(object sender, RoutedEventArgs e)
    {
        _vm.RefreshExplorer();
    }

    // ---------- create project scaffolding (Explorer “+”) ----------

    private enum ScaffoldKind { Mod, Datapack }

    private void OnCreateMenuOpen(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!CreateMenuPopup.IsOpen)
            CreateMenuPopup.IsOpen = true;
    }

    private void OnCreateMenuClose(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Close after a short delay so the pointer can reach the popup.
        System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); if (!CreateMenuPopup.IsMouseOver) CreateMenuPopup.IsOpen = false; };
        timer.Start();
    }

    private void OnCreateMenuPopupEnter(object sender, System.Windows.Input.MouseEventArgs e) { }

    private void OnCreateMenuPopupLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CreateMenuPopup.IsOpen = false;
    }

    private void OnCreateModClick(object sender, RoutedEventArgs e)
    {
        CreateMenuPopup.IsOpen = false;
        CreateScaffold(ScaffoldKind.Mod);
    }

    private void OnCreateDatapackClick(object sender, RoutedEventArgs e)
    {
        CreateMenuPopup.IsOpen = false;
        CreateScaffold(ScaffoldKind.Datapack);
    }

    private void CreateScaffold(ScaffoldKind kind)
    {
        var isMod = kind == ScaffoldKind.Mod;
        var title = isMod ? "Создать мод" : "Создать датапак";
        var prompt = new NamePromptDialog(this, title, isMod ? "Имя мода:" : "Имя датапака:");
        if (prompt.ShowDialog() != true) return;
        var name = prompt.Value;
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        // Create next to the current project; otherwise use the app's projects folder.
        string? baseDir = _vm.SelectedProject?.Path is string p && Directory.Exists(p)
            ? Directory.GetParent(p)?.FullName
            : null;
        if (string.IsNullOrEmpty(baseDir))
            baseDir = AppDataPaths.Projects;

        var target = Path.Combine(baseDir, FileService.Instance.SanitizeFolderName(name));
        try
        {
            if (isMod) FileService.Instance.CreateModProject(target, name);
            else FileService.Instance.CreateDatapackProject(target, name);

            _vm.AddProject(target);
            // Write the .mmcp descriptor next to the project so it can be reopened by double-click.
            try { MmcpService.Write(_vm.SelectedProject!, target + MmcpService.Extension); } catch { }
            _vm.ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Success,
                Source = "IDE",
                Message = (isMod ? "Мод" : "Датапак") + " «" + name + "» создан: " + target +
                           "\nФайл проекта: " + target + MmcpService.Extension
            });
        }
        catch (Exception ex)
        {
            _vm.ConsoleLogs.Add(new LogEntry
            {
                Level = LogLevel.Error,
                Source = "IDE",
                Message = "Не удалось создать " + (isMod ? "мод" : "датапак") + ": " + ex.Message
            });
            MessageBox.Show(this, "Не удалось создать проект:\n" + ex.Message, "Mine IDE",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnToggleModClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ModItem mod)
            _vm.ToggleMod(mod);
    }

    private void OnDeleteModClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ModItem mod) return;
        var res = MessageBox.Show(this,
            $"Удалить мод «{mod.FileName}»?\nФайл будет удалён с диска безвозвратно.",
            "Mine IDE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
            _vm.DeleteMod(mod);
    }

    private void OnOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите директорию проекта",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dlg.ShowDialog(this) == true)
        {
            var gradleFile = Path.Combine(dlg.FolderName, "build.gradle");
            if (File.Exists(gradleFile))
            {
                var name = new DirectoryInfo(dlg.FolderName).Name;
                _vm.Projects.Add(new Project
                {
                    Name = name,
                    Path = dlg.FolderName,
                    MinecraftVersion = "1.20.1",
                    ForgeVersion = "47.2.0",
                    ModId = name.ToLowerInvariant()
                });
                _vm.SelectedProject = _vm.Projects[^1];
                _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Success, Source = "IDE", Message = "Открыт проект: " + name });
            }
            else
            {
                _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Warning, Source = "IDE", Message = "build.gradle не найден — это не Forge-проект." });
            }
        }
    }

    // ---------- editor tabs ----------

    private void OnTabHeaderButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button) return;
        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject) is TabItem item &&
            item.DataContext is EditorTab tab)
        {
            CloseTabWithPrompt(tab);
        }
    }

    private void CloseTabWithPrompt(EditorTab tab)
    {
        if (tab.IsModified)
        {
            var res = MessageBox.Show(this,
                $"Сохранить изменения в «{tab.Title}»?",
                "Mine IDE", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Cancel) return;
            if (res == MessageBoxResult.Yes)
            {
                if (!string.IsNullOrEmpty(tab.FullPath))
                {
                    FileService.Instance.WriteFile(tab.FullPath, tab.Content);
                    tab.MarkSaved();
                }
                else
                {
                    tab.MarkSaved();
                }
            }
        }
        _vm.CloseTab(tab);
    }

    // ---------- status bar ----------

    private void UpdateBuildStatusBar()
    {
        if (StatusBuildLabel == null) return;
        StatusBuildLabel.Text = "Build: " + _vm.BuildStatus.PhaseLabel;
        RefreshBadges();
    }

    private void UpdateLaunchStatus()
    {
        if (StatusLauncherLabel == null) return;
        StatusLauncherLabel.Text = _vm.IsLauncherRunning ? "▶ Minecraft running" : "";
    }

    private void UpdateThemeLabel()
    {
        if (StatusThemeLabel == null) return;
        StatusThemeLabel.Text = ThemeService.Instance.Current == AppTheme.Dark ? "Dark theme" : "Light theme";
    }

    private void UpdateLineCount()
    {
        if (LineCountText == null) return;
        var text = _vm.ActiveTab?.Content;
        if (string.IsNullOrEmpty(text)) { LineCountText.Text = "0"; return; }
        LineCountText.Text = text.Split('\n').Length.ToString();
    }

    private void RefreshBadges()
    {
        if (ErrorsBadge == null) return;
        ErrorsBadge.Text = _vm.ErrorCount.ToString();
        WarningsBadge.Text = _vm.WarningCount.ToString();
    }

    // ---------- editor hooks (caret → status bar) ----------

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorTab.Content))
        {
            UpdateLineCount();
            UpdateRightDock();
        }
    }

    private void HookActiveEditor()
    {
        if (_hookedEditor != null) _hookedEditor.CaretChanged -= OnEditorCaretChanged;
        if (_hookedTab != null) _hookedTab.PropertyChanged -= OnActiveTabPropertyChanged;
        _hookedEditor = null;
        _hookedTab = null;

        var tab = _vm.ActiveTab;
        if (tab == null) return;

        _hookedTab = tab;
        tab.PropertyChanged += OnActiveTabPropertyChanged;

        _hookedEditor = FindFirstVisualChild<SharpCodeEditor>(EditorTabs);
        if (_hookedEditor != null)
            _hookedEditor.CaretChanged += OnEditorCaretChanged;
    }

    private void OnEditorCaretChanged(int line, int col)
    {
        if (StatusLnCol != null)
            StatusLnCol.Text = $"Ln {line + 1}, Col {col}";
    }

    // ---------- misc ----------

    private void OnCrashRefreshClick(object sender, RoutedEventArgs e)
    {
        _vm.ConsoleLogs.Add(new LogEntry { Level = LogLevel.Info, Source = "IDE", Message = "Crash reports refreshed. Всего: " + _vm.CrashReports.Count });
    }

    private void FadeIn(FrameworkElement e)
    {
        try
        {
            e.Opacity = 0;
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            e.BeginAnimation(OpacityProperty, anim);
        }
        catch { }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindFirstVisualChild<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) return t;
            var nested = FindFirstVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }
}

/// <summary>Минимальный диалог запроса имени — используется для «Создать мод» / «Создать датапак».</summary>
public sealed class NamePromptDialog : Window
{
    private readonly TextBox _input;

    public string? Value => _input.Text?.Trim();

    public NamePromptDialog(Window owner, string title, string label)
    {
        Owner = owner;
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = TryBrush("BgbgBrush");
        Foreground = TryBrush("ForegroundBrush");
        FontFamily = owner.FontFamily;
        FontSize = owner.FontSize;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = TryBrush("ForegroundMutedBrush")
        });

        _input = new TextBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        if (TryFindResource("ModernTextBox") is Style tbStyle) _input.Style = tbStyle;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; }
        };
        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button
        {
            Content = "Создать",
            IsDefault = true,
            Width = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(8, 4, 8, 4)
        };
        if (TryFindResource("AccentButton") is Style okStyle) ok.Style = okStyle;
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button
        {
            Content = "Отмена",
            IsCancel = true,
            Width = 96,
            Padding = new Thickness(8, 4, 8, 4)
        };
        if (TryFindResource("FlatButton") is Style fbStyle) cancel.Style = fbStyle;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _input.Focus();
    }

    private Brush? TryBrush(string key)
    {
        try { return TryFindResource(key) as Brush; }
        catch { return null; }
    }
}

/// <summary>Анимирует GridLength — используется для плавного сворачивания/разворачивания терминала.</summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock.CurrentProgress is not double progress)
            return defaultDestinationValue;
        if (EasingFunction != null)
            progress = EasingFunction.Ease(progress);
        double from = From.Value;
        double to = To.Value;
        return new GridLength(from + (to - from) * progress);
    }
}
