using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using MineIDE.RecipeEditor.Models;
using MineIDE.RecipeEditor.Services;

namespace MineIDE.RecipeEditor.ViewModels;

/// <summary>
/// View-model of the isolated recipe editor. Owns the <see cref="RecipeDocument"/>,
/// undo/redo history, JSON generation/validation, autosave and the draggable item
/// palette. It has no WPF dependency, so the same shape can be reused for other
/// editors (loot tables, tags, advancements, predicates, functions).
/// </summary>
public sealed class RecipeEditorViewModel : ObservableObject
{
    private const int MaxUndoSteps = 200;
    private const int MergeWindowMs = 450;

    private static readonly HashSet<string> TrackedProps = new()
    {
        nameof(RecipeItem.Id),
        nameof(RecipeItem.Count),
        nameof(RecipeItem.Nbt),
        nameof(RecipeDocument.Kind),
        nameof(RecipeDocument.Group),
        nameof(RecipeDocument.Experience),
        nameof(RecipeDocument.CookingTime)
    };

    private readonly List<RecipeSnapshot> _undo = new();
    private readonly List<RecipeSnapshot> _redo = new();
    private RecipeSnapshot _baseline;

    private bool _isApplyingSnapshot;
    private DateTime _lastChangeUtc = DateTime.MinValue;

    private RecipeItem? _selectedItem;
    private string _jsonText = "";
    private string _errorText = "";
    private bool _hasErrors;
    private string _statusText = "Готово";
    private string _fileName = "recipe.json";
    private string? _filePath;

    private readonly System.Timers.Timer _autosaveTimer;

    public RecipeEditorViewModel()
    {
        Document = new RecipeDocument();
        _baseline = RecipeSnapshot.Capture(Document);
        SelectedItem = Document.Result;

        Document.PropertyChanged += OnTreeChanged;
        foreach (var item in AllItems())
            item.PropertyChanged += OnTreeChanged;

        // Load every vanilla item/block id from the local Minecraft client jar.
        foreach (var id in ItemIconService.Instance.GetAllItemIds())
            Resources.Add(id);

        _autosaveTimer = new System.Timers.Timer(600) { AutoReset = false };
        _autosaveTimer.Elapsed += (_, _) => WriteAutosave();

        Regenerate();
        TryRestoreAutosave();
    }

    public RecipeDocument Document { get; }

    public ObservableCollection<string> Resources { get; } = new();

    public IReadOnlyList<RecipeKindOption> KindOptions { get; } = new[]
    {
        new RecipeKindOption { Kind = RecipeKind.Shaped, Label = "Shaped (3×3)" },
        new RecipeKindOption { Kind = RecipeKind.Shapeless, Label = "Shapeless" },
        new RecipeKindOption { Kind = RecipeKind.Smelting, Label = "Печь (smelting)" },
        new RecipeKindOption { Kind = RecipeKind.Blasting, Label = "Плавильня (blasting)" },
        new RecipeKindOption { Kind = RecipeKind.Smoking, Label = "Коптильня (smoking)" },
        new RecipeKindOption { Kind = RecipeKind.Campfire, Label = "Костёр (campfire)" },
        new RecipeKindOption { Kind = RecipeKind.Stonecutting, Label = "Камнерез (stonecutting)" },
        new RecipeKindOption { Kind = RecipeKind.Smithing, Label = "Кузница (smithing)" }
    };

    public RecipeKindOption SelectedKindOption
    {
        get => KindOptions.First(o => o.Kind == Document.Kind);
        set
        {
            if (value != null && value.Kind != Document.Kind)
                Document.Kind = value.Kind;
            OnPropertyChanged();
        }
    }

    /// <summary>Vanilla GUI container texture name matching the current recipe kind (drives the editor background).</summary>
    public string GuiTexture => Document.Kind switch
    {
        RecipeKind.Shaped or RecipeKind.Shapeless => "crafting_table",
        RecipeKind.Smelting => "furnace",
        RecipeKind.Blasting => "blast_furnace",
        RecipeKind.Smoking => "smoker",
        RecipeKind.Campfire => "furnace",      // campfire has no container GUI
        RecipeKind.Stonecutting => "stonecutter",
        RecipeKind.Smithing => "smithing",
        _ => "crafting_table"
    };

    public RecipeItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public string JsonText
    {
        get => _jsonText;
        private set { if (_jsonText == value) return; _jsonText = value; OnPropertyChanged(); }
    }

    public string ErrorText
    {
        get => _errorText;
        private set { if (_errorText == value) return; _errorText = value; OnPropertyChanged(); }
    }

    public bool HasErrors
    {
        get => _hasErrors;
        private set { if (_hasErrors == value) return; _hasErrors = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public string FileName
    {
        get => _fileName;
        private set { if (_fileName == value) return; _fileName = value; OnPropertyChanged(); }
    }

    public string? FilePath
    {
        get => _filePath;
        private set { if (_filePath == value) return; _filePath = value; OnPropertyChanged(); }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string AutosavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MineIDE", "recipe_editor_autosave.json");

    public void SetStatus(string text) => StatusText = text;

    // ---------- change tracking ----------

    private void OnTreeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingSnapshot) return;
        if (e.PropertyName is not { } name || !TrackedProps.Contains(name)) return;

        var now = DateTime.UtcNow;
        bool merge = (now - _lastChangeUtc).TotalMilliseconds < MergeWindowMs;
        if (!merge)
        {
            _undo.Add(_baseline);
            if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _baseline = RecipeSnapshot.Capture(Document);
        _lastChangeUtc = now;

        Regenerate();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        if (name == nameof(RecipeDocument.Kind)) { OnPropertyChanged(nameof(SelectedKindOption)); OnPropertyChanged(nameof(GuiTexture)); }
        ScheduleAutosave();
    }

    private void Regenerate()
    {
        try
        {
            JsonText = RecipeJsonGenerator.Generate(Document);
            var errors = RecipeJsonGenerator.Validate(Document);
            HasErrors = errors.Count > 0;
            ErrorText = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "";
        }
        catch (Exception ex)
        {
            HasErrors = true;
            ErrorText = "Ошибка генерации JSON: " + ex.Message;
        }
        RefreshErrorHighlights();
    }

    private void RefreshErrorHighlights()
    {
        foreach (var item in AllItems()) item.IsError = false;

        if (string.IsNullOrWhiteSpace(Document.Result.Id))
            Document.Result.IsError = true;

        switch (Document.Kind)
        {
            case RecipeKind.Shaped:
            case RecipeKind.Shapeless:
                if (!Document.Grid.Any(c => !string.IsNullOrWhiteSpace(c.Id)))
                    foreach (var c in Document.Grid) c.IsError = true;
                break;

            case RecipeKind.Smelting:
            case RecipeKind.Blasting:
            case RecipeKind.Smoking:
            case RecipeKind.Campfire:
            case RecipeKind.Stonecutting:
                if (string.IsNullOrWhiteSpace(Document.Ingredient.Id))
                    Document.Ingredient.IsError = true;
                break;

            case RecipeKind.Smithing:
                if (string.IsNullOrWhiteSpace(Document.Template.Id)) Document.Template.IsError = true;
                if (string.IsNullOrWhiteSpace(Document.Base.Id)) Document.Base.IsError = true;
                if (string.IsNullOrWhiteSpace(Document.Addition.Id)) Document.Addition.IsError = true;
                break;
        }
    }

    // ---------- undo / redo ----------

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(RecipeSnapshot.Capture(Document));

        _isApplyingSnapshot = true;
        target.ApplyTo(Document);
        _isApplyingSnapshot = false;
        _baseline = RecipeSnapshot.Capture(Document);
        _lastChangeUtc = DateTime.MinValue;

        Regenerate();
        StatusText = "Undo";
        RaiseHistory();
        OnPropertyChanged(nameof(SelectedKindOption));
        OnPropertyChanged(nameof(GuiTexture));
        ScheduleAutosave();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(RecipeSnapshot.Capture(Document));

        _isApplyingSnapshot = true;
        target.ApplyTo(Document);
        _isApplyingSnapshot = false;
        _baseline = RecipeSnapshot.Capture(Document);
        _lastChangeUtc = DateTime.MinValue;

        Regenerate();
        StatusText = "Redo";
        RaiseHistory();
        OnPropertyChanged(nameof(SelectedKindOption));
        OnPropertyChanged(nameof(GuiTexture));
        ScheduleAutosave();
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    // ---------- actions ----------

    public void SelectItem(RecipeItem item)
    {
        if (item != null) SelectedItem = item;
    }

    /// <summary>Applies a dragged/clicked item id to a slot (drag &amp; drop target).</summary>
    public void ApplyResourceTo(RecipeItem target, string itemId)
    {
        if (target == null || string.IsNullOrWhiteSpace(itemId)) return;
        target.Id = itemId.Trim();
        SelectItem(target);
    }

    public void AddResource(string itemId)
    {
        var id = itemId?.Trim();
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!Resources.Contains(id)) Resources.Add(id);
    }

    public void RemoveResource(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        Resources.Remove(itemId);
    }

    public void New()
    {
        _isApplyingSnapshot = true;
        RecipeSnapshot.Capture(new RecipeDocument()).ApplyTo(Document);
        _isApplyingSnapshot = false;

        _undo.Clear();
        _redo.Clear();
        _baseline = RecipeSnapshot.Capture(Document);
        _lastChangeUtc = DateTime.MinValue;

        FilePath = null;
        FileName = "recipe.json";
        SelectedItem = Document.Result;
        Regenerate();
        StatusText = "Новый рецепт";
        RaiseHistory();
        OnPropertyChanged(nameof(SelectedKindOption));
        OnPropertyChanged(nameof(GuiTexture));
    }

    public void SaveTo(string path)
    {
        File.WriteAllText(path, JsonText);
        FilePath = path;
        FileName = Path.GetFileName(path);
        StatusText = "Сохранено: " + path;
    }

    public void LoadFrom(string path)
    {
        var doc = RecipeJsonParser.Parse(File.ReadAllText(path));

        _isApplyingSnapshot = true;
        RecipeSnapshot.Capture(doc).ApplyTo(Document);
        _isApplyingSnapshot = false;

        _undo.Clear();
        _redo.Clear();
        _baseline = RecipeSnapshot.Capture(Document);
        _lastChangeUtc = DateTime.MinValue;

        FilePath = path;
        FileName = Path.GetFileName(path);
        SelectedItem = Document.Result;
        Regenerate();
        StatusText = "Загружено: " + path;
        RaiseHistory();
        OnPropertyChanged(nameof(SelectedKindOption));
        OnPropertyChanged(nameof(GuiTexture));
        ScheduleAutosave();
    }

    public void TryRestoreAutosave()
    {
        if (!File.Exists(AutosavePath)) return;
        try
        {
            var doc = RecipeJsonParser.Parse(File.ReadAllText(AutosavePath));

            _isApplyingSnapshot = true;
            RecipeSnapshot.Capture(doc).ApplyTo(Document);
            _isApplyingSnapshot = false;

            _undo.Clear();
            _redo.Clear();
            _baseline = RecipeSnapshot.Capture(Document);
            _lastChangeUtc = DateTime.MinValue;
            SelectedItem = Document.Result;
            Regenerate();
            StatusText = "Восстановлено из автосохранения";
            RaiseHistory();
            OnPropertyChanged(nameof(SelectedKindOption));
        OnPropertyChanged(nameof(GuiTexture));
        }
        catch
        {
            // Corrupt or incompatible autosave — start blank.
        }
    }

    // ---------- autosave ----------

    private void ScheduleAutosave()
    {
        try
        {
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
        }
        catch { /* timer disposed */ }
    }

    private void WriteAutosave()
    {
        try
        {
            var json = JsonText;
            Directory.CreateDirectory(Path.GetDirectoryName(AutosavePath)!);
            File.WriteAllText(AutosavePath, json);
        }
        catch { /* best effort */ }
    }

    public void Shutdown()
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Dispose();
    }

    private IEnumerable<RecipeItem> AllItems()
    {
        foreach (var c in Document.Grid) yield return c;
        yield return Document.Ingredient;
        yield return Document.Template;
        yield return Document.Base;
        yield return Document.Addition;
        yield return Document.Result;
    }
}

/// <summary>Display entry for the recipe-kind selector.</summary>
public sealed class RecipeKindOption
{
    public RecipeKind Kind { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}
