using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MineIDE.AdvancementEditor.Models;

/// <summary>
/// Minimal INotifyPropertyChanged base (same shape as RecipeEditor's ObservableObject)
/// so this editor module stays self-contained and UI-agnostic.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Supported Minecraft advancement triggers (subset of 1.20.1 vanilla).</summary>
public enum AdvancementTrigger
{
    InventoryChanged,          // minecraft:inventory_changed
    PlayerKilledEntity,        // minecraft:player_killed_entity
    PlayerLevel,               // minecraft:player_level
    PlayerGeneratesContainerLoot, // minecraft:player_generates_container_loot
    BrewedPotion,              // minecraft:brewed_potion
    ConstructBeacon,           // minecraft:construct_beacon
    ConsumeItem,               // minecraft:consume_item
    UsedTotem,                 // minecraft:used_totem
    Tick,                      // minecraft:tick
    Impossible                 // minecraft:impossible
}

/// <summary>Frame type shown in the toast / tree.</summary>
public enum AdvancementFrame
{
    Task,       // blue-ish
    Goal,       // purple
    Challenge   // red-ish
}

/// <summary>
/// One advancement node. Holds every editable field plus its free position
/// on the tree canvas (display x/y, like vanilla advancement files).
/// </summary>
public sealed class AdvancementModel : ObservableObject
{
    private string _id = "";
    private string _title = "";
    private string _description = "";
    private string _iconItem = "minecraft:stone";
    private AdvancementTrigger _trigger = AdvancementTrigger.InventoryChanged;
    private AdvancementFrame _frame = AdvancementFrame.Task;
    private bool _showToast = true;
    private bool _announceToChat = true;
    private bool _hidden;
    private string _parentId = "";
    private string _triggerItem = "";
    private string _triggerEntity = "";
    private int _triggerLevel = 1;
    private string _triggerLootTable = "";
    private int _rewardExperience;
    private string _rewardItem = "";
    private int _rewardItemCount = 1;
    private double _displayX = 60;
    private double _displayY = 60;
    private bool _isSelected;

    /// <summary>Short advancement name, e.g. "get_paraglider" (also the file name).</summary>
    public string Id
    {
        get => _id;
        set { if (_id == value) return; _id = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
    }

    public string Description
    {
        get => _description;
        set { if (_description == value) return; _description = value ?? ""; OnPropertyChanged(); }
    }

    public string IconItem
    {
        get => _iconItem;
        set { if (_iconItem == value) return; _iconItem = value ?? ""; OnPropertyChanged(); }
    }

    public AdvancementTrigger Trigger
    {
        get => _trigger;
        set { if (_trigger == value) return; _trigger = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsTriggerItem)); OnPropertyChanged(nameof(NeedsTriggerEntity)); OnPropertyChanged(nameof(NeedsTriggerLevel)); OnPropertyChanged(nameof(NeedsLootTable)); }
    }

    public AdvancementFrame Frame
    {
        get => _frame;
        set { if (_frame == value) return; _frame = value; OnPropertyChanged(); }
    }

    public bool ShowToast
    {
        get => _showToast;
        set { if (_showToast == value) return; _showToast = value; OnPropertyChanged(); }
    }

    public bool AnnounceToChat
    {
        get => _announceToChat;
        set { if (_announceToChat == value) return; _announceToChat = value; OnPropertyChanged(); }
    }

    public bool Hidden
    {
        get => _hidden;
        set { if (_hidden == value) return; _hidden = value; OnPropertyChanged(); }
    }

    /// <summary>Id of the parent advancement (empty for the root node).</summary>
    public string ParentId
    {
        get => _parentId;
        set { if (_parentId == value) return; _parentId = value ?? ""; OnPropertyChanged(); }
    }

    // ---- trigger-specific condition fields ----
    public string TriggerItem { get => _triggerItem; set { if (_triggerItem == value) return; _triggerItem = value ?? ""; OnPropertyChanged(); } }
    public string TriggerEntity { get => _triggerEntity; set { if (_triggerEntity == value) return; _triggerEntity = value ?? ""; OnPropertyChanged(); } }
    public int TriggerLevel { get => _triggerLevel; set { if (_triggerLevel == value) return; _triggerLevel = Math.Max(1, value); OnPropertyChanged(); } }
    public string TriggerLootTable { get => _triggerLootTable; set { if (_triggerLootTable == value) return; _triggerLootTable = value ?? ""; OnPropertyChanged(); } }

    // ---- reward ----
    public int RewardExperience { get => _rewardExperience; set { if (_rewardExperience == value) return; _rewardExperience = Math.Max(0, value); OnPropertyChanged(); } }
    public string RewardItem { get => _rewardItem; set { if (_rewardItem == value) return; _rewardItem = value ?? ""; OnPropertyChanged(); } }
    public int RewardItemCount { get => _rewardItemCount; set { if (_rewardItemCount == value) return; _rewardItemCount = Math.Max(1, value); OnPropertyChanged(); } }

    // ---- free position on the tree canvas (vanilla display x/y) ----
    public double DisplayX
    {
        get => _displayX;
        set { if (Math.Abs(_displayX - value) < 0.01) return; _displayX = value; OnPropertyChanged(); }
    }

    public double DisplayY
    {
        get => _displayY;
        set { if (Math.Abs(_displayY - value) < 0.01) return; _displayY = value; OnPropertyChanged(); }
    }

    /// <summary>UI-only flag: whether this node is currently selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    // Whether the trigger requires a specific condition field.
    public bool NeedsTriggerItem => Trigger == AdvancementTrigger.InventoryChanged || Trigger == AdvancementTrigger.ConsumeItem;
    public bool NeedsTriggerEntity => Trigger == AdvancementTrigger.PlayerKilledEntity;
    public bool NeedsTriggerLevel => Trigger == AdvancementTrigger.PlayerLevel;
    public bool NeedsLootTable => Trigger == AdvancementTrigger.PlayerGeneratesContainerLoot;

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? (string.IsNullOrWhiteSpace(Id) ? "(без названия)" : Id) : Title;

    /// <summary>Deep copy used when saving / duplicating.</summary>
    public AdvancementModel Clone()
    {
        var m = new AdvancementModel
        {
            Id = Id, Title = Title, Description = Description, IconItem = IconItem,
            Trigger = Trigger, Frame = Frame, ShowToast = ShowToast, AnnounceToChat = AnnounceToChat,
            Hidden = Hidden, ParentId = ParentId,
            TriggerItem = TriggerItem, TriggerEntity = TriggerEntity, TriggerLevel = TriggerLevel,
            TriggerLootTable = TriggerLootTable,
            RewardExperience = RewardExperience, RewardItem = RewardItem, RewardItemCount = RewardItemCount,
            DisplayX = DisplayX, DisplayY = DisplayY
        };
        return m;
    }
}

/// <summary>
/// The document being edited: a free set of nodes on the tree canvas.
/// Nodes reference each other via <see cref="AdvancementModel.ParentId"/>.
/// </summary>
public sealed class AdvancementDocument : ObservableObject
{
    public ObservableCollection<AdvancementModel> Nodes { get; } = new();

    /// <summary>All nodes in insertion order.</summary>
    public List<AdvancementModel> Models => Nodes.ToList();

    /// <summary>Ids of all nodes (used for parent selection).</summary>
    public List<string> ModelIds => Nodes.Select(m => m.Id).ToList();

    /// <summary>Finds a node by id, or null.</summary>
    public AdvancementModel? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Nodes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Root = the node without a parent (or the first node).</summary>
    public AdvancementModel? Root => Nodes.FirstOrDefault(m => string.IsNullOrWhiteSpace(m.ParentId)) ?? Nodes.FirstOrDefault();
}
