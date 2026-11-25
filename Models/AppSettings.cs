using System.IO;
using System.Text.Json;

namespace UELauncher.Models;

public sealed class AppSettings
{
    public List<string> ScanRoots { get; set; } = new();
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> EngineOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CustomEnginePaths { get; set; } = new();
    public string? LastSelectedProjectPath { get; set; }
    public string SortMode { get; set; } = "Recent";
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UELauncher");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    loaded.Favorites = new HashSet<string>(loaded.Favorites, StringComparer.OrdinalIgnoreCase);
                    loaded.EngineOverrides = new Dictionary<string, string>(loaded.EngineOverrides, StringComparer.OrdinalIgnoreCase);
                    if (loaded.ScanRoots.Count == 0)
                    {
                        loaded.PopulateDefaultScanRoots();
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to fresh defaults if settings file is unreadable.
        }

        var defaults = new AppSettings();
        defaults.PopulateDefaultScanRoots();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore transient IO failures.
        }
    }

    private void PopulateDefaultScanRoots()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Unreal Projects")
        };

        foreach (var drive in new[] { "C", "D", "E", "F" })
        {
            candidates.Add($@"{drive}:\Unreal Projects");
            candidates.Add($@"{drive}:\UE5 Projects");
            candidates.Add($@"{drive}:\UE5pjs");
        }

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && !ScanRoots.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                ScanRoots.Add(dir);
            }
        }
    }
}
