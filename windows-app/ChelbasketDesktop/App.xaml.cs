using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ChelbasketDesktop;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChelbasketTV", "startup.log");

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"\n[{DateTimeOffset.Now:O}] Запуск приложения\n");
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
        }
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportStartupFailure(e.Exception);
        e.Handled = true;
    }

    private static void ReportStartupFailure(Exception exception)
    {
        var details = $"[{DateTimeOffset.Now:O}] {exception}\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, details);
        }
        catch { }

        try
        {
            MessageBox.Show(
                $"Челбаскет ТВ-пакет не смог открыть окно.\n\n{exception.Message}\n\nПодробности сохранены в:\n{LogPath}",
                "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}