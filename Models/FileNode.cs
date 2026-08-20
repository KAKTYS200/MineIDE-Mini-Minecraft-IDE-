using System.Collections.ObjectModel;

namespace MineIDE.Models;

public class FileNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string Icon { get; set; } = "\uE8A5"; // default doc icon
    public long Size { get; set; }
    public string Language { get; set; } = "plaintext";
    public FileNode? Parent { get; set; }
    public ObservableCollection<FileNode> Children { get; set; } = new();

    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }

    public string Display => IsDirectory ? Name : $"{Name}";
}
