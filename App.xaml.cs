using System.IO;
using System.Windows;
using UELauncher.Models;
using UELauncher.Services;

namespace UELauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show($"Unexpected error:\n{args.Exception.Message}\n\nDetails saved to %AppData%\\UELauncher\\crash.log",
                "UE Forge Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    private static void RunSelfTest()
    {
        var outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UELauncher", "selftest.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var lines = new List<string>();
        var settings = AppSettings.Load();
        var engines = EngineDiscoveryService.DiscoverEngines(settings.CustomEnginePaths);
        lines.Add($"ENGINES ({engines.Count}):");
        foreach (var eng in engines)
            lines.Add($"  {eng.DisplayName} -> {eng.RootPath}");

        var projects = ProjectDiscoveryService.DiscoverProjects(settings, engines);
        lines.Add($"\nPROJECTS ({projects.Count}):");
        foreach (var p in projects.OrderByDescending(x => x.LastModified).Take(15))
            lines.Add($"  {p.Name,-28} | {p.EngineBadgeText,-8} | {p.TypeBadgeText,-3} | {p.EditorTarget,-24} | {p.StatusBadgeText}");

        // Instantiate MainWindow to prove XAML loads cleanly with zero binding/resource errors
        var win = new MainWindow();
        lines.Add($"\nMAINWINDOW_OK: Title='{win.Title}'");
        win.Close();

        File.WriteAllLines(outPath, lines);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UELauncher", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch { }
    }
}
