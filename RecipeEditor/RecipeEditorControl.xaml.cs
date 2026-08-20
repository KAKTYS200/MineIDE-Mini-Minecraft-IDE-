using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MineIDE.RecipeEditor.Models;
using MineIDE.RecipeEditor.ViewModels;

namespace MineIDE.RecipeEditor;

public partial class RecipeEditorControl : UserControl
{
    private readonly RecipeEditorViewModel _vm;

    public RecipeEditorControl()
    {
        InitializeComponent();
        _vm = new RecipeEditorViewModel();
        DataContext = _vm;
    }

    /// <summary>Disposes the autosave timer; called when the host window closes.</summary>
    public void Shutdown() => _vm.Shutdown();

    // ---------- drag & drop ----------

    private void OnResourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Drag the item under the cursor directly — no need to select it first.
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject src) return;
        if (list.ContainerFromElement(src) is not ListBoxItem item) return;
        if (item.DataContext is not string id || string.IsNullOrWhiteSpace(id)) return;

        var data = new DataObject(DataFormats.Text, id);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Copy);
    }

    private void OnSlotDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSlotDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RecipeItem item &&
            e.Data.GetDataPresent(DataFormats.Text) &&
            e.Data.GetData(DataFormats.Text) is string id)
        {
            _vm.ApplyResourceTo(item, id);
        }
    }

    private void OnSlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RecipeItem item)
            _vm.SelectItem(item);
    }

    // ---------- resources ----------

    private void OnAddResourceClick(object sender, RoutedEventArgs e)
    {
        _vm.AddResource(ResourceInput.Text);
        ResourceInput.Text = "";
    }

    private void OnRemoveResourceClick(object sender, RoutedEventArgs e)
    {
        if (ResourceList.SelectedItem is string id)
            _vm.RemoveResource(id);
    }

    // ---------- toolbar ----------

    private void OnNewClick(object sender, RoutedEventArgs e) => _vm.New();

    private void OnUndoClick(object sender, RoutedEventArgs e) => _vm.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => _vm.Redo();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON рецепт (*.json)|*.json|Все файлы (*.*)|*.*",
            Title = "Открыть рецепт"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            try
            {
                _vm.LoadFrom(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось открыть рецепт: " + ex.Message,
                    "Редактор рецептов", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.FilePath))
            OnSaveAsClick(sender, e);
        else
            _vm.SaveTo(_vm.FilePath);
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON рецепт (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = _vm.FileName,
            Title = "Сохранить рецепт как"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            _vm.SaveTo(dlg.FileName);
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
