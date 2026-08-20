namespace MineIDE.Models;

/// <summary>A mod (.jar) inside the project's runs/mods directory, with its on/off state.</summary>
public class ModItem : ViewModelBase
{
    private bool _isEnabled = true;

    /// <summary>Display name (e.g. "examplemod-1.0.0.jar", without the .disabled suffix).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Actual file path on disk; ends with ".disabled" when the mod is turned off.</summary>
    public string FullPath { get; set; } = "";

    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(ToggleLabel)); }
    }

    /// <summary>Label for the toggle button — shows the action the click will perform.</summary>
    public string ToggleLabel => IsEnabled ? "Выключить" : "Включить";
}
