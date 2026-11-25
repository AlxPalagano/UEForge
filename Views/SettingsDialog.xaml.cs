using System.IO;
using System.Windows;
using Microsoft.Win32;
using UELauncher.Helpers;
using UELauncher.Models;
using UELauncher.Services;

namespace UELauncher.Views;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private List<EngineInstall> _engines;

    public SettingsDialog(AppSettings settings, List<EngineInstall> engines)
    {
        InitializeComponent();
        NativeWindowTheme.ApplyDarkTitleBar(this);
        _settings = settings;
        _engines = engines;
        RefreshLists();
    }

    private void RefreshLists()
    {
        ScanRootsList.ItemsSource = null;
        ScanRootsList.ItemsSource = _settings.ScanRoots.ToList();

        _engines = EngineDiscoveryService.DiscoverEngines(_settings.CustomEnginePaths);
        EnginesList.ItemsSource = null;
        EnginesList.ItemsSource = _engines.Select(e =>
            e.IsCustom ? $"[Custom] {e.DisplayName}  —  {e.RootPath}" : $"{e.DisplayName}  —  {e.RootPath}").ToList();
    }

    private void AddScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select a folder containing Unreal Engine projects"
        };
        if (dlg.ShowDialog(this) == true && Directory.Exists(dlg.FolderName))
        {
            if (!_settings.ScanRoots.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.ScanRoots.Add(dlg.FolderName);
                RefreshLists();
            }
        }
    }

    private void RemoveScanFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ScanRootsList.SelectedItem is string path)
        {
            _settings.ScanRoots.RemoveAll(r => r.Equals(path, StringComparison.OrdinalIgnoreCase));
            RefreshLists();
        }
    }

    private void AddCustomEngine_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select an Unreal Engine root folder (containing Engine\\Build\\BatchFiles)"
        };
        if (dlg.ShowDialog(this) == true)
        {
            var bat = Path.Combine(dlg.FolderName, "Engine", "Build", "BatchFiles", "Build.bat");
            if (!File.Exists(bat))
            {
                MessageBox.Show(
                    "That folder doesn't contain Engine\\Build\\BatchFiles\\Build.bat.\nPlease pick the UE root folder (e.g. D:\\Program Files\\Epic Games\\UE_5.8).",
                    "Invalid Engine Folder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            if (!_settings.CustomEnginePaths.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                _settings.CustomEnginePaths.Add(dlg.FolderName);
                RefreshLists();
            }
        }
    }

    private void RemoveCustomEngine_Click(object sender, RoutedEventArgs e)
    {
        int idx = EnginesList.SelectedIndex;
        if (idx >= 0 && idx < _engines.Count)
        {
            var eng = _engines[idx];
            if (!eng.IsCustom)
            {
                System.Windows.MessageBox.Show(
                    "Only user-added [Custom] engine entries can be removed here.",
                    "Auto-Detected Engine",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            _settings.CustomEnginePaths.RemoveAll(p => p.Equals(eng.RootPath, StringComparison.OrdinalIgnoreCase));
            RefreshLists();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        DialogResult = true;
        Close();
    }
}
