using System;

namespace MineIDE.Models;

public class EditorTab : ViewModelBase
{
    private string _title = "Untitled";
    private string _fullPath = "";
    private string _content = "";
    private string _savedContent = "";
    private string _language = "plaintext";
    private bool _isDirty;
    private bool _isActive;
    private string _icon = "\uE8A5";

    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(Header)); }
    }

    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(); }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (value == _content) return;
            _content = value;
            OnPropertyChanged();
            IsDirty = _content != _savedContent;
        }
    }

    public string Language
    {
        get => _language;
        set { _language = value; OnPropertyChanged(); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set { _isDirty = value; OnPropertyChanged(); OnPropertyChanged(nameof(Header)); }
    }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); OnPropertyChanged(nameof(Header)); }
    }

    public string Header => $"{Title}{(IsDirty ? " •" : "")}";

    /// <summary>True when the in-memory content differs from what's on disk.</summary>
    public bool IsModified => _content != _savedContent;

    /// <summary>Called after saving to disk — the tab becomes clean.</summary>
    public void MarkSaved()
    {
        _savedContent = _content;
        IsDirty = false;
    }

    public static EditorTab New(string title, string path = "", string content = "", string language = "plaintext", string icon = "\uE8A5")
    {
        var tab = new EditorTab
        {
            Title = title,
            FullPath = path,
            Content = content,
            Language = language,
            Icon = icon
        };
        tab.MarkSaved(); // a freshly opened file is clean, not dirty
        return tab;
    }
}
