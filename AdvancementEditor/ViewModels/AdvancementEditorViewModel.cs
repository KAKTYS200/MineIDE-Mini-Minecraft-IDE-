using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MineIDE.AdvancementEditor.Models;
using MineIDE.AdvancementEditor.Services;
using MineIDE.RecipeEditor.Services;

namespace MineIDE.AdvancementEditor.ViewModels;

/// <summary>
/// View-model of the isolated advancement builder. Owns the <see cref="AdvancementDocument"/>,
/// the selected node, JSON generation/validation and the draggable item palette.
/// No WPF dependency.
/// </summary>
public sealed class AdvancementEditorViewModel : ObservableObject
{
    private AdvancementModel? _selectedModel;
    private string _jsonText = "";
    private string _errorText = "";
    private bool _hasErrors;
    private string _statusText = "Готово";
    private string? _fileName;
    private string? _filePath;
    private string _backgroundName = "green";

    public AdvancementEditorViewModel()
    {
        Document = new AdvancementDocument();

        Document.PropertyChanged += (_, _) => Regenerate();
        Document.Nodes.CollectionChanged += OnNodesCollectionChanged;
        foreach (var node in Document.Nodes)
            node.PropertyChanged += OnNodeChanged;

        // Item palette: all vanilla item ids from the local client jar.
        foreach (var id in ItemIconService.Instance.GetAllItemIds())
            Resources.Add(id);

        Regenerate();
    }

    /// <summary>
    /// Keeps the change subscription in sync with the node list: nodes created
    /// after construction must still trigger JSON regeneration / validation when
    /// their properties change (otherwise errors like "укажите предмет" stay
    /// even after the field has been filled in).
    /// </summary>
    private void OnNodesCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (AdvancementModel n in e.NewItems)
                n.PropertyChanged += OnNodeChanged;
        if (e.OldItems != null)
            foreach (AdvancementModel n in e.OldItems)
                n.PropertyChanged -= OnNodeChanged;
        Regenerate();
        RefreshParents();
    }

    public AdvancementDocument Document { get; }

    public ObservableCollection<string> Resources { get; } = new();

    public AdvancementModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (ReferenceEquals(_selectedModel, value)) return;
            if (_selectedModel != null) _selectedModel.IsSelected = false;
            _selectedModel = value;
            if (_selectedModel != null) _selectedModel.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            RefreshParents();
        }
    }

    public bool HasSelection => SelectedModel != null;

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

    public string? FilePath
    {
        get => _filePath;
        private set { if (_filePath == value) return; _filePath = value; OnPropertyChanged(); }
    }

    public string? FileName
    {
        get => _fileName;
        private set { if (_fileName == value) return; _fileName = value; OnPropertyChanged(); }
    }

    public string BackgroundName
    {
        get => _backgroundName;
        set
        {
            if (_backgroundName == value) return;
            _backgroundName = value;
            OnPropertyChanged();
            Regenerate();
        }
    }

    /// <summary>
    /// Available backgrounds: "green" is a generated grass/earth tile, the rest
    /// are extracted from the client jar (gui/advancements/backgrounds).
    /// </summary>
    public IReadOnlyList<BackgroundOption> BackgroundOptions { get; } = new[]
    {
        new BackgroundOption { Key = "green", Label = Loc.GreenBackground },
        new BackgroundOption { Key = "adventure", Label = "Adventure" },
        new BackgroundOption { Key = "end", Label = "End" },
        new BackgroundOption { Key = "nether", Label = "Nether" },
        new BackgroundOption { Key = "stone", Label = "Stone" }
    };

    public void SetStatus(string text) => StatusText = text;

    // ---------- tree interactions ----------

    /// <summary>Selects a node (its properties become editable).</summary>
    public void SelectNode(AdvancementModel node) => SelectedModel = node;

    /// <summary>Moves a node to a free canvas position.</summary>
    public void MoveNode(AdvancementModel node, double x, double y)
    {
        if (node == null) return;
        node.DisplayX = Math.Max(0, x);
        node.DisplayY = Math.Max(0, y);
    }

    /// <summary>
    /// Creates a new root-ish node at the given position. If the tree already has a
    /// root, the new node gets no parent (user can link it via the properties panel).
    /// </summary>
    public AdvancementModel CreateNodeAt(double x, double y, string? itemId = null)
    {
        var model = new AdvancementModel
        {
            Id = UniqueId(),
            Title = "Новое достижение",
            Description = "",
            IconItem = string.IsNullOrWhiteSpace(itemId) ? "minecraft:stone" : itemId.Trim(),
            DisplayX = Math.Max(0, x),
            DisplayY = Math.Max(0, y)
        };
        Document.Nodes.Add(model);
        SelectedModel = model;
        Regenerate();
        return model;
    }

    /// <summary>Creates a child node linked to the given parent (placed to the right of it).</summary>
    public AdvancementModel CreateLinked(AdvancementModel parent)
    {
        if (parent == null) return CreateNodeAt(0, 0);

        var model = new AdvancementModel
        {
            Id = UniqueId(),
            Title = "Новое достижение",
            Description = "",
            IconItem = "minecraft:stone",
            ParentId = parent.Id,
            DisplayX = parent.DisplayX + 120,
            DisplayY = parent.DisplayY + 40
        };
        Document.Nodes.Add(model);
        SelectedModel = model;
        Regenerate();
        return model;
    }

    /// <summary>Removes a node; children are re-parented onto the removed node's parent.</summary>
    public void DeleteNode(AdvancementModel node)
    {
        if (node == null || !Document.Nodes.Contains(node)) return;
        var parentId = node.ParentId;
        foreach (var m in Document.Models)
        {
            if (m != node && string.Equals(m.ParentId, node.Id, StringComparison.OrdinalIgnoreCase))
                m.ParentId = parentId;
        }
        if (SelectedModel == node) SelectedModel = null;
        Document.Nodes.Remove(node);
        Regenerate();
    }

    public void DeleteSelected()
    {
        if (SelectedModel != null) DeleteNode(SelectedModel);
    }

    /// <summary>Applies a dragged item id to a node's icon.</summary>
    public void ApplyResourceToNode(AdvancementModel node, string itemId)
    {
        if (node == null || string.IsNullOrWhiteSpace(itemId)) return;
        node.IconItem = itemId.Trim();
        SelectedModel = node;
        Regenerate();
    }

    /// <summary>Clears the tree and starts over.</summary>
    public void New()
    {
        Document.Nodes.Clear();
        SelectedModel = null;
        FilePath = null;
        FileName = null;
        StatusText = Loc.StatusNew;
        Regenerate();
        RefreshParents();
    }

    private string UniqueId()
    {
        var baseName = "advancement";
        var existing = new HashSet<string>(Document.ModelIds, StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (existing.Contains(baseName + "_" + n)) n++;
        return baseName + "_" + n;
    }

    // ---------- combo options ----------

    public IReadOnlyList<TriggerOption> TriggerOptions { get; } = Enum.GetValues<AdvancementTrigger>()
        .Select(t => new TriggerOption { Trigger = t, Label = Loc.TriggerName(t) })
        .ToArray();

    public IReadOnlyList<FrameOption> FrameOptions { get; } = Enum.GetValues<AdvancementFrame>()
        .Select(f => new FrameOption { Frame = f, Label = Loc.FrameName(f) })
        .ToArray();

    public ObservableCollection<ParentOption> ParentOptions { get; } = new();

    private void RefreshParents()
    {
        var sel = SelectedModel?.Id;
        ParentOptions.Clear();
        ParentOptions.Add(new ParentOption { Id = "", DisplayName = Loc.NoParent });
        foreach (var m in Document.Models)
        {
            if (m == SelectedModel) continue; // cannot parent to itself
            ParentOptions.Add(new ParentOption { Id = m.Id, DisplayName = m.DisplayName + "  (" + m.Id + ")" });
        }
    }

    // ---------- change tracking ----------

    private void OnNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is AdvancementModel m && m != SelectedModel && e.PropertyName == nameof(AdvancementModel.IsSelected))
            return; // selection flag changes on other nodes are driven by us
        if (e.PropertyName is nameof(AdvancementModel.Id) or nameof(AdvancementModel.ParentId))
            RefreshParents();
        Regenerate();
    }

    // ---------- JSON ----------

    private void Regenerate()
    {
        try
        {
            var errors = AdvancementJsonGenerator.Validate(Document);
            HasErrors = errors.Count > 0;
            ErrorText = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "";
            JsonText = Document.Models.Count > 0 ? AdvancementJsonGenerator.Generate(Document) : "{}";
        }
        catch (Exception ex)
        {
            HasErrors = true;
            ErrorText = "Ошибка генерации JSON: " + ex.Message;
            JsonText = "{}";
        }
    }

    // ---------- save / load ----------

    /// <summary>
    /// Saves every node as its own JSON file under
    /// src/main/resources/data/&lt;namespace&gt;/advancements/&lt;id&gt;.json
    /// (data pack layout, works on Forge 1.20.1). Returns the written files.
    /// </summary>
    public List<string> SaveToProject(string projectRoot, string namespaceName)
    {
        var dir = Path.Combine(projectRoot, "src", "main", "resources", "data", namespaceName, "advancements");
        Directory.CreateDirectory(dir);

        AdvancementJsonGenerator.Namespace = namespaceName;
        AdvancementJsonGenerator.BackgroundName = BackgroundName;

        var files = new List<string>();
        foreach (var model in Document.Models)
        {
            var id = string.IsNullOrWhiteSpace(model.Id) ? "advancement" : model.Id;
            var path = Path.Combine(dir, id + ".json");
            File.WriteAllText(path, AdvancementJsonGenerator.GenerateNode(model));
            files.Add(path);
        }

        FilePath = dir;
        FileName = namespaceName;
        StatusText = Loc.StatusSaved + ": " + files.Count + " файл(ов)";
        return files;
    }

    /// <summary>Loads a single advancement JSON file into the editor.</summary>
    public void LoadFromFile(string path)
    {
        var model = AdvancementJsonParser.Parse(File.ReadAllText(path));
        model.Id = string.IsNullOrWhiteSpace(model.Id)
            ? Path.GetFileNameWithoutExtension(path)
            : AdvancementJsonGenerator.ShortId(model.Id);

        New();
        Document.Nodes.Add(model);
        SelectedModel = model;
        FilePath = path;
        FileName = Path.GetFileName(path);
        StatusText = Loc.StatusLoaded + ": " + Path.GetFileName(path);
        Regenerate();
        RefreshParents();
    }
}

/// <summary>Display entry for the trigger selector.</summary>
public sealed class TriggerOption
{
    public AdvancementTrigger Trigger { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>Display entry for the frame selector.</summary>
public sealed class FrameOption
{
    public AdvancementFrame Frame { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>Display entry for the parent selector (empty id = root).</summary>
public sealed class ParentOption
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public override string ToString() => DisplayName;
}

/// <summary>Display entry for the canvas background selector.</summary>
public sealed class BackgroundOption
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}
