using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MineIDE.RecipeEditor.Models;

/// <summary>
/// Minimal INotifyPropertyChanged base (mirrors MineIDE.Models.ViewModelBase) so this
/// editor module stays self-contained and can be copied/extended for other editors
/// (loot tables, tags, advancements, predicates, functions) without touching the core.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Supported Minecraft recipe types.</summary>
public enum RecipeKind
{
    Shaped,
    Shapeless,
    Smelting,
    Blasting,
    Smoking,
    Campfire,
    Stonecutting,
    Smithing
}

/// <summary>A single item / ingredient: id, count and optional SNBT.</summary>
public sealed class RecipeItem : ObservableObject
{
    private string _id = "";
    private int _count = 1;
    private string _nbt = "";
    private bool _isError;

    public string Id
    {
        get => _id;
        set
        {
            if (_id == value) return;
            _id = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShortName));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowCount));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value < 1 ? 1 : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCount));
        }
    }

    /// <summary>Optional raw SNBT string (e.g. "{Damage:0}").</summary>
    public string Nbt
    {
        get => _nbt;
        set { if (_nbt == value) return; _nbt = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>UI-only flag used by validation to highlight required-but-empty slots.</summary>
    public bool IsError
    {
        get => _isError;
        set { if (_isError == value) return; _isError = value; OnPropertyChanged(); }
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(_id);

    /// <summary>True when the slot has an item and a count above 1 (drives the count badge).</summary>
    public bool ShowCount => !IsEmpty && _count > 1;

    /// <summary>Last path segment of the id, e.g. "iron_ingot" for "minecraft:iron_ingot".</summary>
    public string ShortName => string.IsNullOrEmpty(Id) ? "" : (Id.Contains(':') ? Id[(Id.IndexOf(':') + 1)..] : Id);
}

/// <summary>The recipe being edited — holds every field for every recipe kind.</summary>
public sealed class RecipeDocument : ObservableObject
{
    private RecipeKind _kind = RecipeKind.Shaped;
    private string _group = "";
    private double _experience;
    private int _cookingTime = 200;

    public RecipeDocument()
    {
        Grid = Enumerable.Range(0, 9).Select(_ => new RecipeItem()).ToArray();
        Ingredient = new RecipeItem();
        Template = new RecipeItem();
        Base = new RecipeItem();
        Addition = new RecipeItem();
        Result = new RecipeItem();
    }

    public RecipeKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGridRecipe));
            OnPropertyChanged(nameof(IsFurnaceRecipe));
            OnPropertyChanged(nameof(IsStonecutting));
            OnPropertyChanged(nameof(IsSmithingRecipe));
        }
    }

    /// <summary>3x3 crafting grid (used by Shaped and Shapeless).</summary>
    public RecipeItem[] Grid { get; }

    /// <summary>Single input for furnace / stonecutting recipes.</summary>
    public RecipeItem Ingredient { get; }

    /// <summary>Smithing: template / base / addition.</summary>
    public RecipeItem Template { get; }
    public RecipeItem Base { get; }
    public RecipeItem Addition { get; }

    public RecipeItem Result { get; }

    public string Group
    {
        get => _group;
        set { if (_group == value) return; _group = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>Furnace experience, 0 by default.</summary>
    public double Experience
    {
        get => _experience;
        set { if (_experience == value) return; _experience = value; OnPropertyChanged(); }
    }

    /// <summary>Furnace cooking time in ticks; 0 means "use the type's default".</summary>
    public int CookingTime
    {
        get => _cookingTime;
        set { if (_cookingTime == value) return; _cookingTime = value; OnPropertyChanged(); }
    }

    // Convenience flags for the UI (drive which input area is shown).
    public bool IsGridRecipe => Kind is RecipeKind.Shaped or RecipeKind.Shapeless;
    public bool IsFurnaceRecipe => Kind is RecipeKind.Smelting or RecipeKind.Blasting or RecipeKind.Smoking or RecipeKind.Campfire;
    public bool IsStonecutting => Kind == RecipeKind.Stonecutting;
    public bool IsSmithingRecipe => Kind == RecipeKind.Smithing;
}

/// <summary>Plain serializable value of a RecipeItem (for snapshots / autosave).</summary>
public struct ItemState
{
    public string Id { get; set; }
    public int Count { get; set; }
    public string Nbt { get; set; }

    public static ItemState From(RecipeItem i) => new() { Id = i.Id, Count = i.Count, Nbt = i.Nbt };

    public void ApplyTo(RecipeItem i)
    {
        i.Id = Id;
        i.Count = Count;
        i.Nbt = Nbt;
    }
}

/// <summary>Full editable state of a recipe — used for undo/redo and autosave.</summary>
public sealed class RecipeSnapshot
{
    public RecipeKind Kind { get; set; }
    public string Group { get; set; } = "";
    public double Experience { get; set; }
    public int CookingTime { get; set; }
    public ItemState[] Grid { get; set; } = new ItemState[9];
    public ItemState Ingredient { get; set; }
    public ItemState Template { get; set; }
    public ItemState Base { get; set; }
    public ItemState Addition { get; set; }
    public ItemState Result { get; set; }

    public static RecipeSnapshot Capture(RecipeDocument d)
    {
        var s = new RecipeSnapshot
        {
            Kind = d.Kind,
            Group = d.Group,
            Experience = d.Experience,
            CookingTime = d.CookingTime
        };
        for (int i = 0; i < 9; i++) s.Grid[i] = ItemState.From(d.Grid[i]);
        s.Ingredient = ItemState.From(d.Ingredient);
        s.Template = ItemState.From(d.Template);
        s.Base = ItemState.From(d.Base);
        s.Addition = ItemState.From(d.Addition);
        s.Result = ItemState.From(d.Result);
        return s;
    }

    public void ApplyTo(RecipeDocument d)
    {
        d.Kind = Kind;
        d.Group = Group;
        d.Experience = Experience;
        d.CookingTime = CookingTime;
        for (int i = 0; i < 9; i++) Grid[i].ApplyTo(d.Grid[i]);
        Ingredient.ApplyTo(d.Ingredient);
        Template.ApplyTo(d.Template);
        Base.ApplyTo(d.Base);
        Addition.ApplyTo(d.Addition);
        Result.ApplyTo(d.Result);
    }
}
