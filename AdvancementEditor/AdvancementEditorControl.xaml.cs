using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using MineIDE.AdvancementEditor.Models;
using MineIDE.AdvancementEditor.ViewModels;
using MineIDE.RecipeEditor.Services;

namespace MineIDE.AdvancementEditor;

public partial class AdvancementEditorControl : UserControl
{
    private readonly AdvancementEditorViewModel _vm;

    // drag state
    private AdvancementModel? _dragModel;
    private Point _dragStartMouse;
    private double _dragStartX;
    private double _dragStartY;
    private bool _isDragging;

    /// <summary>Cell size used to position new linked nodes.</summary>
    private const double NodeStepX = 120;
    private const double NodeStepY = 40;

    public AdvancementEditorControl()
    {
        InitializeComponent();
        _vm = new AdvancementEditorViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AdvancementEditorViewModel.SelectedModel) ||
                e.PropertyName == nameof(AdvancementEditorViewModel.BackgroundName))
            {
                RedrawLines();
                ApplyBackground();
            }
        };
        _vm.Document.Nodes.CollectionChanged += (_, _) => { RedrawLines(); SubscribeNodes(); };
        SubscribeNodes();

        Loaded += (_, _) => { ApplyBackground(); RedrawLines(); };
    }

    /// <summary>Shutdown hook (kept symmetric with the recipe editor).</summary>
    public void Shutdown() { }

    /// <summary>Loads an existing advancement JSON file into the editor.</summary>
    public void LoadFrom(string path) => _vm.LoadFromFile(path);

    /// <summary>Current project folder + mod id used by "Save".</summary>
    public string ProjectPath { get; set; } = "";
    public string NamespaceName { get; set; } = "";

    // ---------- background (in-game advancement background texture) ----------

    private void ApplyBackground()
    {
        try
        {
            var name = _vm.BackgroundName;

            // "green" is a generated grass/earth tile (no such texture in the jar).
            if (name == "green")
            {
                TreeCanvas.Background = new ImageBrush(CreateGrassTile())
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 16, 16),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill
                };
                return;
            }

            var path = ItemIconService.Instance.GetAdvancementGuiPath("backgrounds/" + name);
            if (path == null)
            {
                TreeCanvas.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x27));
                return;
            }

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();

            // Vanilla 1.20.1 tiles the background in 16×16 blocks
            // (AdvancementTab.drawContents: blit(tex, x, y, 0, 0, 16, 16, 16, 16)),
            // and the vanilla background textures are 16×16.
            TreeCanvas.Background = new ImageBrush(img)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            };
        }
        catch
        {
            TreeCanvas.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x27));
        }
    }

    /// <summary>
    /// Generates a 16×16 grass-block-top style tile (green noise with a few
    /// brown dirt specks) — used for the "green" background option and saved
    /// into the mod's assets so the root advancement can use it in-game.
    /// </summary>
    private static WriteableBitmap CreateGrassTile()
    {
        const int size = 16;
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var rnd = new Random(12345);
        var greens = new[]
        {
            Color.FromRgb(0x56, 0x9E, 0x34),
            Color.FromRgb(0x4C, 0x8E, 0x2E),
            Color.FromRgb(0x60, 0xA8, 0x3C),
            Color.FromRgb(0x44, 0x80, 0x29)
        };
        var browns = new[] { Color.FromRgb(0x5C, 0x3F, 0x22), Color.FromRgb(0x6B, 0x4A, 0x2A) };
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            var c = rnd.NextDouble() < 0.08 ? browns[rnd.Next(browns.Length)] : greens[rnd.Next(greens.Length)];
            pixels[i * 4] = c.B;
            pixels[i * 4 + 1] = c.G;
            pixels[i * 4 + 2] = c.R;
            pixels[i * 4 + 3] = 255;
        }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Writes the generated green tile into the mod's own assets so the
    /// advancement's display.background reference actually resolves in-game.
    /// </summary>
    private void SaveGreenTexture(string projectRoot, string ns)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(ns)) return;
        var dir = System.IO.Path.Combine(projectRoot, "src", "main", "resources",
            "assets", ns, "textures", "gui", "advancements", "backgrounds");
        System.IO.Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateGrassTile()));
        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "green.png")))
            encoder.Save(fs);
    }

    // ---------- node change subscription (for line redraw) ----------

    private void SubscribeNodes()
    {
        foreach (var n in _vm.Document.Nodes)
        {
            n.PropertyChanged -= OnNodePropChanged;
            n.PropertyChanged += OnNodePropChanged;
        }
    }

    private void OnNodePropChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdvancementModel.DisplayX) or nameof(AdvancementModel.DisplayY)
            or nameof(AdvancementModel.ParentId) or nameof(AdvancementModel.Id))
            RedrawLines();
    }

    // ---------- connection lines (parent -> child) ----------

    private void RedrawLines()
    {
        LinesCanvas.Children.Clear();
        var nodes = _vm.Document.Models;
        if (nodes.Count < 2) return;

        var byId = nodes.ToDictionary(m => m.Id, m => m, StringComparer.OrdinalIgnoreCase);
        var selected = _vm.SelectedModel;

        foreach (var child in nodes)
        {
            if (string.IsNullOrWhiteSpace(child.ParentId)) continue;
            if (!byId.TryGetValue(child.ParentId, out var parent)) continue;
            if (ReferenceEquals(parent, child)) continue;

            var p1 = Center(parent);
            var p2 = Center(child);

            var line = new Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 2
            };
            // Highlight edges touching the selected node.
            if (selected != null && (ReferenceEquals(child, selected) || ReferenceEquals(parent, selected)))
            {
                line.Stroke = Brushes.Gold;
                line.StrokeThickness = 3;
            }
            LinesCanvas.Children.Add(line);

            // small arrow head at the child end
            var dx = p2.X - p1.X;
            var dy = p2.Y - p1.Y;
            var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var ux = dx / len; var uy = dy / len;
            var tip = new Point(p2.X - ux * 26, p2.Y - uy * 26);
            var side = new Point(-uy, ux);
            var arrow = new Polygon
            {
                Stroke = line.Stroke,
                StrokeThickness = 1.5,
                Fill = line.Stroke,
                Points = new PointCollection
                {
                    tip,
                    new Point(tip.X - ux * 6 + side.X * 3.5, tip.Y - uy * 6 + side.Y * 3.5),
                    new Point(tip.X - ux * 6 - side.X * 3.5, tip.Y - uy * 6 - side.Y * 3.5)
                }
            };
            LinesCanvas.Children.Add(arrow);
        }
    }

    private Point Center(AdvancementModel m)
        => new(m.DisplayX + 26, m.DisplayY + 26);

    // ---------- node dragging ----------

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is FrameworkElement fe && fe.DataContext is AdvancementModel node)
        {
            _vm.SelectNode(node);
            _dragModel = node;
            _dragStartMouse = e.GetPosition(TreeCanvas);
            _dragStartX = node.DisplayX;
            _dragStartY = node.DisplayY;
            _isDragging = false;
            (fe).CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnNodeMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragModel == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(TreeCanvas);
        var dx = pos.X - _dragStartMouse.X;
        var dy = pos.Y - _dragStartMouse.Y;
        if (!_isDragging && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return; // click, not drag
        _isDragging = true;
        _vm.MoveNode(_dragModel, _dragStartX + dx, _dragStartY + dy);
    }

    private void OnNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragModel == null) return;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        _dragModel = null;
        _isDragging = false;
    }

    // ---------- right-click menus ----------

    private void OnCanvasRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        var pos = e.GetPosition(TreeCanvas);
        var menu = new ContextMenu();
        var item = new MenuItem { Header = Loc.CreateAdvancement };
        item.Click += (_, _) => _vm.CreateNodeAt(pos.X, pos.Y);
        menu.Items.Add(item);
        menu.PlacementTarget = TreeCanvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnNodeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        if (sender is FrameworkElement fe && fe.DataContext is AdvancementModel node)
        {
            _vm.SelectNode(node);
            var menu = new ContextMenu();
            var linked = new MenuItem { Header = Loc.CreateLinked };
            linked.Click += (_, _) => _vm.CreateLinked(node);
            menu.Items.Add(linked);
            var del = new MenuItem { Header = Loc.DeleteBtn };
            del.Click += (_, _) => _vm.DeleteNode(node);
            menu.Items.Add(del);
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    // ---------- item palette drag & drop ----------

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject src) return;
        if (list.ContainerFromElement(src) is not ListBoxItem item) return;
        if (item.DataContext is not string id || string.IsNullOrWhiteSpace(id)) return;

        var data = new DataObject(DataFormats.Text, id);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.Text) && e.Data.GetData(DataFormats.Text) is string id)
        {
            var pos = e.GetPosition(TreeCanvas);
            _vm.CreateNodeAt(pos.X, pos.Y, id);
        }
    }

    private void OnNodeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnNodeDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AdvancementModel node &&
            e.Data.GetDataPresent(DataFormats.Text) &&
            e.Data.GetData(DataFormats.Text) is string id)
        {
            _vm.ApplyResourceToNode(node, id);
        }
    }

    // ---------- item palette filter ----------

    private void OnItemFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = ItemFilter.Text?.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            ItemList.ItemsSource = _vm.Resources;
            return;
        }
        ItemList.ItemsSource = new List<string>(
            _vm.Resources.Where(r => r.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------- toolbar ----------

    private void OnNewClick(object sender, RoutedEventArgs e) => _vm.New();

    private void OnDeleteClick(object sender, RoutedEventArgs e) => _vm.DeleteSelected();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON достижение (*.json)|*.json|Все файлы (*.*)|*.*",
            Title = "Открыть достижение"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                _vm.LoadFromFile(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось открыть достижение: " + ex.Message,
                    "Конструктор достижений", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProjectPath) || string.IsNullOrWhiteSpace(NamespaceName))
        {
            MessageBox.Show(Window.GetWindow(this),
                "Сначала выберите проект (Explorer) — сохранение идёт в\n" +
                "src/main/resources/data/<mod_id>/advancements/",
                "Конструктор достижений", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var files = _vm.SaveToProject(ProjectPath, NamespaceName);
            if (_vm.BackgroundName == "green")
                SaveGreenTexture(ProjectPath, NamespaceName);
            _vm.SetStatus(Loc.StatusSaved + ": " + string.Join(", ", files.Select(System.IO.Path.GetFileName)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось сохранить: " + ex.Message,
                "Конструктор достижений", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCopyJsonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.JsonText);
            _vm.SetStatus("JSON скопирован в буфер обмена");
        }
        catch
        {
            _vm.SetStatus("Не удалось скопировать JSON");
        }
    }
}
