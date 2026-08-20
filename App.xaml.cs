using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MineIDE;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Backup: in case StartupUri fails (e.g. blank config), open window manually.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow == null || !MainWindow.IsVisible)
            {
                try
                {
                    var win = new MainWindow();
                    win.Show();
                    MainWindow = win;
                }
                catch (Exception ex)
                {
                    WriteCrashLog("ManualWindowCreation", ex);
                    MessageBox.Show(
                        "Не удалось открыть главное окно:\n" + ex.Message +
                        "\n\nЛог: %LOCALAPPDATA%\\MineIDE\\mine_ide_crash.log",
                        "Mine IDE", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }), DispatcherPriority.ApplicationIdle);

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            "Unexpected UI error:\n" + e.Exception.Message +
            "\n\nDetails logged to mine_ide_crash.log",
            "Mine IDE", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteCrashLog("UnhandledException", ex);
    }

    private void WriteCrashLog(string tag, Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MineIDE", "mine_ide_crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"---\n[{DateTime.Now:O}] {tag}\n{ex}\n\n");
        }
        catch { /* swallow */ }
    }
}
