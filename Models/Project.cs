using System;

namespace MineIDE.Models;

public class Project : ViewModelBase
{
    private string _name = "Untitled";
    private string _path = "";
    private string _minecraftVersion = "1.20.1";
    private string _forgeVersion = "47.2.0";
    private string _modId = "examplemod";
    private string _description = "";
    private DateTime _lastOpened = DateTime.Now;
    private ProjectType _type = ProjectType.Mod;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(); }
    }

    public string MinecraftVersion
    {
        get => _minecraftVersion;
        set
        {
            if (_minecraftVersion == value) return;
            _minecraftVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string ForgeVersion
    {
        get => _forgeVersion;
        set
        {
            if (_forgeVersion == value) return;
            _forgeVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string ModId
    {
        get => _modId;
        set { _modId = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public DateTime LastOpened
    {
        get => _lastOpened;
        set { _lastOpened = value; OnPropertyChanged(); }
    }

    public ProjectType Type
    {
        get => _type;
        set { _type = value; OnPropertyChanged(); }
    }

    public string DisplayLabel => $"{Name}  •  MC {MinecraftVersion}  •  Forge {ForgeVersion}";
}

public enum ProjectType
{
    Mod,
    ModPack,
    Plugin,
    ResourcePack,
    DataPack
}
